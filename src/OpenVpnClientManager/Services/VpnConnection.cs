using System.Collections.ObjectModel;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using OpenVpnClientManager.Infrastructure;
using OpenVpnClientManager.Models;

namespace OpenVpnClientManager.Services;

public enum VpnState
{
    Disconnected,
    Connecting,
    Authenticating,
    Connected,
    Reconnecting,
    Disconnecting,
    Error,
}

public sealed class CredentialRequest
{
    public required Guid SiteId { get; init; }
    public required string SiteName { get; init; }
    public required string CustomerName { get; init; }

    /// <summary>"Auth", "Private Key", "HTTP Proxy", ...</summary>
    public required string Kind { get; init; }
    public bool NeedsUsername { get; init; }
    public string? PrefilledUsername { get; init; }
    public bool PreviousAttemptFailed { get; init; }
    public bool HasStoredCredentials { get; init; }

    /// <summary>Static challenge text (2FA) when the server requests one.</summary>
    public string? ChallengeText { get; init; }
    public bool ChallengeEcho { get; init; }
}

public sealed class CredentialResponse
{
    public string? Username { get; init; }
    public string? Password { get; init; }
    public string? ChallengeResponse { get; init; }
    public bool Remember { get; init; }
}

/// <summary>
/// One VPN connection: starts openvpn.exe through the Interactive Service, drives it through the
/// management interface and exposes observable state for the UI.
/// </summary>
public sealed partial class VpnConnection : ObservableObject
{
    private const int MaxLogLines = 2000;

    private readonly AppSettings _settings;
    private readonly Func<CredentialRequest, Task<CredentialResponse?>> _credentialProvider;
    private readonly SynchronizationContext _ui;
    private readonly Channel<(string type, string payload)> _notifications = Channel.CreateUnbounded<(string, string)>();

    private InteractiveServiceSession? _session;
    private ManagementClient? _management;
    private TaskCompletionSource<bool>? _exited;
    private bool _authFailed;
    private bool _storedCredentialsUsed;
    private string? _fatalMessage;
    private VpnState _state;
    private string _statusText = "Niet verbonden";
    private string? _localIp;
    private string? _remoteIp;
    private long _bytesIn;
    private long _bytesOut;
    private DateTime? _connectedSinceUtc;
    private string? _lastError;

    public Site Site { get; }
    public Customer Customer { get; }

    public ObservableCollection<string> LogLines { get; } = [];

    public VpnState State { get => _state; private set => Set(ref _state, value); }
    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }
    public string? LocalIp { get => _localIp; private set => Set(ref _localIp, value); }
    public string? RemoteIp { get => _remoteIp; private set => Set(ref _remoteIp, value); }
    public long BytesIn { get => _bytesIn; private set => Set(ref _bytesIn, value); }
    public long BytesOut { get => _bytesOut; private set => Set(ref _bytesOut, value); }
    public DateTime? ConnectedSinceUtc { get => _connectedSinceUtc; private set => Set(ref _connectedSinceUtc, value); }
    public string? LastError { get => _lastError; private set => Set(ref _lastError, value); }

    public bool IsActive => State is VpnState.Connecting or VpnState.Authenticating or VpnState.Connected or VpnState.Reconnecting or VpnState.Disconnecting;
    public bool CanConnect => State is VpnState.Disconnected or VpnState.Error;

    public event Action<VpnConnection>? Connected;
    public event Action<VpnConnection, string?>? Dropped;

    public VpnConnection(Customer customer, Site site, AppSettings settings, Func<CredentialRequest, Task<CredentialResponse?>> credentialProvider)
    {
        Customer = customer;
        Site = site;
        _settings = settings;
        _credentialProvider = credentialProvider;
        _ui = SynchronizationContext.Current ?? new SynchronizationContext();
        _ = ProcessNotificationsAsync();
    }

    // ------------------------------------------------------------------ connect / disconnect

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        if (!CanConnect)
        {
            return;
        }

        _authFailed = false;
        _storedCredentialsUsed = false;
        _fatalMessage = null;
        _exited = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        SetState(VpnState.Connecting, "Starten...");
        LastError = null;
        LocalIp = null;
        RemoteIp = null;
        BytesIn = 0;
        BytesOut = 0;
        ConnectedSinceUtc = null;
        Ui(() => LogLines.Clear());

        var configPath = AppPaths.ConfigPathFor(Site.Id);
        if (!File.Exists(configPath))
        {
            SetState(VpnState.Error, "Configuratiebestand ontbreekt");
            LastError = "Het .ovpn bestand van deze site ontbreekt. Importeer de config opnieuw.";
            return;
        }

        int port = GetFreeLoopbackPort();
        string managementPassword = CreateManagementPassword();
        string options = BuildOptions(configPath, port);
        Log($"[manager] openvpn starten via Interactive Service (management op 127.0.0.1:{port})");

        try
        {
            var session = await InteractiveServiceSession.StartAsync(AppPaths.Configs, options, managementPassword + "\n", ct).ConfigureAwait(false);
            session.MessageReceived += msg => Log("[service] " + msg);
            session.Closed += OnProcessExited;
            _session = session;
            Log($"[manager] openvpn.exe gestart (PID {session.ProcessId})");

            var mgmt = await ManagementClient.ConnectAsync(port, managementPassword, TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
            mgmt.Notification += (type, payload) => _notifications.Writer.TryWrite((type, payload));
            mgmt.Closed += OnManagementClosed;
            _management = mgmt;

            await mgmt.SendAsync("state on", ct).ConfigureAwait(false);
            await mgmt.SendAsync("log on", ct).ConfigureAwait(false);
            await mgmt.SendAsync("bytecount 5", ct).ConfigureAwait(false);
            await mgmt.TrySendAsync("hold release").ConfigureAwait(false);
        }
        catch (InteractiveServiceException ex)
        {
            Cleanup();
            string friendly = ex.Message;
            if (friendly.Contains("requires admin approval", StringComparison.OrdinalIgnoreCase))
            {
                friendly = "De Interactive Service weigert deze config omdat de gebruiker geen lid is van de groep 'OpenVPN Administrators'.";
            }
            Log("[service] " + ex.Message);
            SetState(VpnState.Error, "Service weigerde start");
            LastError = friendly;
        }
        catch (Exception ex)
        {
            Cleanup();
            Log("[manager] fout: " + ex.Message);
            SetState(VpnState.Error, "Fout bij starten");
            LastError = ex.Message;
        }
    }

    public async Task DisconnectAsync()
    {
        if (State is VpnState.Disconnected or VpnState.Error && _session is null)
        {
            return;
        }

        SetState(VpnState.Disconnecting, "Verbreken...");
        var exited = _exited;
        if (_management is not null)
        {
            await _management.TrySendAsync("signal SIGTERM").ConfigureAwait(false);
            if (exited is not null)
            {
                await Task.WhenAny(exited.Task, Task.Delay(TimeSpan.FromSeconds(6))).ConfigureAwait(false);
            }
        }
        Cleanup();
        ConnectedSinceUtc = null;
        SetState(VpnState.Disconnected, "Niet verbonden");
    }

    private void OnProcessExited()
    {
        _exited?.TrySetResult(true);
        bool intentional = State == VpnState.Disconnecting;
        Cleanup();
        ConnectedSinceUtc = null;
        if (intentional)
        {
            SetState(VpnState.Disconnected, "Niet verbonden");
            return;
        }

        if (_fatalMessage is not null || _authFailed)
        {
            LastError = _fatalMessage ?? "Authenticatie mislukt.";
            SetState(VpnState.Error, "Verbinding mislukt");
        }
        else
        {
            SetState(VpnState.Disconnected, "Verbinding beëindigd");
        }
        Log("[manager] openvpn.exe is gestopt");
        Dropped?.Invoke(this, LastError);
    }

    private void OnManagementClosed()
    {
        // The process normally closes the management socket just before exiting; the pipe
        // closure (OnProcessExited) is authoritative. Nothing to do here.
    }

    private void Cleanup()
    {
        var m = Interlocked.Exchange(ref _management, null);
        m?.Dispose();
        var s = Interlocked.Exchange(ref _session, null);
        s?.Dispose();
    }

    // ------------------------------------------------------------------ notifications

    private async Task ProcessNotificationsAsync()
    {
        await foreach (var (type, payload) in _notifications.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                await HandleNotificationAsync(type, payload).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                AppLog.Warn($"Notification {type} failed: {ex.Message}");
            }
        }
    }

    private async Task HandleNotificationAsync(string type, string payload)
    {
        switch (type)
        {
            case "HOLD":
                if (_management is { } m)
                {
                    await m.TrySendAsync("hold release").ConfigureAwait(false);
                }
                break;

            case "STATE":
                HandleState(payload);
                break;

            case "LOG":
                HandleLog(payload);
                break;

            case "BYTECOUNT":
            {
                var parts = payload.Split(',');
                if (parts.Length >= 2 && long.TryParse(parts[0], out var inB) && long.TryParse(parts[1], out var outB))
                {
                    BytesIn = inB;
                    BytesOut = outB;
                }
                break;
            }

            case "PASSWORD":
                await HandlePasswordAsync(payload).ConfigureAwait(false);
                break;

            case "FATAL":
                _fatalMessage = payload;
                LastError = payload;
                Log("[fatal] " + payload);
                break;

            case "INFO":
            case "INFOMSG":
            case "ECHO":
                Log("[" + type.ToLowerInvariant() + "] " + payload);
                break;

            default:
                Log(">" + type + ":" + payload);
                break;
        }
    }

    private void HandleState(string payload)
    {
        var p = payload.Split(',');
        if (p.Length < 2)
        {
            return;
        }
        string state = p[1];
        string desc = p.Length > 2 ? p[2] : string.Empty;

        switch (state)
        {
            case "CONNECTED":
                LocalIp = p.Length > 3 && p[3].Length > 0 ? p[3] : null;
                RemoteIp = p.Length > 4 && p[4].Length > 0 ? p[4] : null;
                ConnectedSinceUtc = DateTime.UtcNow;
                Site.LastConnectedUtc = DateTime.UtcNow;
                SetState(VpnState.Connected, desc == "SUCCESS" ? "Verbonden" : "Verbonden (" + desc + ")");
                Connected?.Invoke(this);
                break;
            case "RECONNECTING":
                ConnectedSinceUtc = null;
                SetState(VpnState.Reconnecting, "Opnieuw verbinden" + (desc.Length > 0 ? " (" + desc + ")" : string.Empty));
                break;
            case "EXITING":
                if (State != VpnState.Disconnecting)
                {
                    SetState(VpnState.Disconnecting, "Afsluiten" + (desc.Length > 0 ? " (" + desc + ")" : string.Empty));
                }
                break;
            case "AUTH":
                SetState(VpnState.Authenticating, "Authenticeren...");
                break;
            case "WAIT":
                SetState(VpnState.Connecting, "Wachten op server...");
                break;
            case "RESOLVE":
                SetState(VpnState.Connecting, "Adres opzoeken...");
                break;
            case "TCP_CONNECT":
                SetState(VpnState.Connecting, "TCP-verbinding opzetten...");
                break;
            case "GET_CONFIG":
                SetState(VpnState.Connecting, "Configuratie ophalen...");
                break;
            case "ASSIGN_IP":
                SetState(VpnState.Connecting, "IP-adres toewijzen...");
                break;
            case "ADD_ROUTES":
                SetState(VpnState.Connecting, "Routes toevoegen...");
                break;
            default:
                SetState(VpnState.Connecting, state);
                break;
        }
    }

    private void HandleLog(string payload)
    {
        // time,flags,message
        var p = payload.Split(',', 3);
        if (p.Length < 3)
        {
            Log(payload);
            return;
        }
        string flags = p[1];
        string msg = p[2];
        string prefix = flags.Contains('F') ? "[fatal] " : flags.Contains('N') ? "[error] " : flags.Contains('W') ? "[warn] " : string.Empty;
        if (flags.Contains('F'))
        {
            _fatalMessage = msg;
            LastError = msg;
        }
        Log(prefix + msg);
    }

    [GeneratedRegex(@"^Need '([^']+)' (username/password|password)(?: SC:(\d+),(.*))?$")]
    private static partial Regex NeedPasswordRegex();

    private async Task HandlePasswordAsync(string payload)
    {
        var need = NeedPasswordRegex().Match(payload);
        if (need.Success)
        {
            string kind = need.Groups[1].Value;
            bool needsUser = need.Groups[2].Value == "username/password";
            bool hasChallenge = need.Groups[3].Success;
            string? challengeText = hasChallenge ? need.Groups[4].Value : null;
            bool challengeEcho = hasChallenge && int.TryParse(need.Groups[3].Value, out var flags) && (flags & 1) == 1;
            await AnswerPasswordRequestAsync(kind, needsUser, challengeText, challengeEcho).ConfigureAwait(false);
            return;
        }

        if (payload.StartsWith("Verification Failed", StringComparison.Ordinal))
        {
            _authFailed = true;
            LastError = "Inloggegevens geweigerd door de server.";
            Log("[auth] " + payload + (_storedCredentialsUsed ? " (opgeslagen inloggegevens)" : string.Empty));
            SetState(VpnState.Authenticating, "Inloggegevens geweigerd");
            return;
        }

        // Auth-Token and others: informational only.
        if (!payload.StartsWith("Auth-Token", StringComparison.Ordinal))
        {
            Log("[auth] " + payload);
        }
    }

    private async Task AnswerPasswordRequestAsync(string kind, bool needsUser, string? challengeText, bool challengeEcho)
    {
        var mgmt = _management;
        if (mgmt is null)
        {
            return;
        }

        SetState(VpnState.Authenticating, "Inloggegevens nodig...");
        var stored = CredentialStore.Load(Site.Id);

        string? username = null;
        string? password = null;
        string? challengeResponse = null;
        bool useStored = false;

        if (kind == "Auth" && !_authFailed && stored is { Password.Length: > 0 } && challengeText is null)
        {
            useStored = true;
            username = stored.Username;
            password = stored.Password;
        }
        else if (kind == "Private Key" && !_authFailed && stored is { PrivateKeyPassword.Length: > 0 })
        {
            useStored = true;
            password = stored.PrivateKeyPassword;
        }

        if (!useStored)
        {
            var request = new CredentialRequest
            {
                SiteId = Site.Id,
                SiteName = Site.Name,
                CustomerName = Customer.Name,
                Kind = kind,
                NeedsUsername = needsUser,
                PrefilledUsername = stored?.Username,
                PreviousAttemptFailed = _authFailed,
                HasStoredCredentials = stored is not null,
                ChallengeText = challengeText,
                ChallengeEcho = challengeEcho,
            };

            var response = await _credentialProvider(request).ConfigureAwait(false);
            if (response is null)
            {
                Log("[auth] geannuleerd door gebruiker");
                await DisconnectAsync().ConfigureAwait(false);
                return;
            }

            username = response.Username;
            password = response.Password;
            challengeResponse = response.ChallengeResponse;

            if (response.Remember)
            {
                var toStore = stored ?? new StoredCredentials();
                if (kind == "Auth")
                {
                    toStore.Username = username;
                    toStore.Password = password;
                }
                else if (kind == "Private Key")
                {
                    toStore.PrivateKeyPassword = password;
                }
                try
                {
                    CredentialStore.Save(Site.Id, toStore);
                }
                catch (Exception ex)
                {
                    Log("[manager] opslaan van inloggegevens mislukt: " + ex.Message);
                }
            }
            _authFailed = false;
        }

        _storedCredentialsUsed = useStored;
        SetState(VpnState.Authenticating, "Authenticeren...");

        if (needsUser)
        {
            await mgmt.TrySendAsync($"username {ManagementClient.Quote(kind)} {ManagementClient.Quote(username ?? string.Empty)}").ConfigureAwait(false);
        }

        string finalPassword = password ?? string.Empty;
        if (challengeText is not null)
        {
            // Static challenge/response protocol (management-interface.txt).
            finalPassword = "SCRV1:" + Base64(password ?? string.Empty) + ":" + Base64(challengeResponse ?? string.Empty);
        }
        await mgmt.TrySendAsync($"password {ManagementClient.Quote(kind)} {ManagementClient.Quote(finalPassword)}").ConfigureAwait(false);
    }

    private static string Base64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s));

    // ------------------------------------------------------------------ helpers

    private string BuildOptions(string configPath, int port)
    {
        static string Q(string path)
        {
            if (path.Contains('"'))
            {
                throw new InvalidOperationException("Pad bevat een aanhalingsteken.");
            }
            return "\"" + path + "\"";
        }

        var sb = new StringBuilder();
        sb.Append("--config ").Append(Q(configPath));
        sb.Append(" --log ").Append(Q(AppPaths.LogPathFor(Site.Id)));
        sb.Append(" --setenv IV_GUI_VER \"OpenVpnClientManager 1.0\"");
        sb.Append(" --auth-retry interact --auth-nocache");
        sb.Append(" --management 127.0.0.1 ").Append(port).Append(" stdin");
        sb.Append(" --management-query-passwords --management-hold");
        if (!_settings.AllowScripts)
        {
            // Overrides any script-security directive inside the config (command line wins).
            sb.Append(" --script-security 1");
        }
        return sb.ToString();
    }

    private static int GetFreeLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static string CreateManagementPassword()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexString(bytes);
    }

    private void SetState(VpnState state, string status)
    {
        Ui(() =>
        {
            State = state;
            StatusText = status;
            OnPropertyChanged(nameof(IsActive));
            OnPropertyChanged(nameof(CanConnect));
            RelayCommand.Requery();
        });
    }

    private void Log(string line)
    {
        var stamped = DateTime.Now.ToString("HH:mm:ss") + "  " + line;
        Ui(() =>
        {
            LogLines.Add(stamped);
            while (LogLines.Count > MaxLogLines)
            {
                LogLines.RemoveAt(0);
            }
        });
    }

    private void Ui(Action action)
    {
        if (SynchronizationContext.Current == _ui)
        {
            action();
        }
        else
        {
            _ui.Post(_ => action(), null);
        }
    }
}
