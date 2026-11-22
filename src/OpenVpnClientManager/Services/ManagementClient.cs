using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace OpenVpnClientManager.Services;

/// <summary>
/// Client for the OpenVPN management interface (loopback TCP, password protected).
/// Real-time notifications start with '&gt;' and are forwarded through <see cref="Notification"/>.
/// Command replies are collected until a terminating "SUCCESS:", "ERROR:" or "END" line.
/// </summary>
public sealed class ManagementClient : IDisposable
{
    private readonly TcpClient _tcp;
    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;
    private readonly SemaphoreSlim _commandLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private TaskCompletionSource<string>? _pending;
    private readonly StringBuilder _pendingBuffer = new();
    private bool _disposed;

    /// <summary>(type, payload) e.g. ("STATE", "1700000000,CONNECTED,SUCCESS,10.8.0.2,...").</summary>
    public event Action<string, string>? Notification;

    public event Action? Closed;

    private ManagementClient(TcpClient tcp)
    {
        _tcp = tcp;
        var stream = tcp.GetStream();
        _reader = new StreamReader(stream, new UTF8Encoding(false), false, 4096, leaveOpen: true);
        _writer = new StreamWriter(stream, new UTF8Encoding(false), 1024, leaveOpen: true) { NewLine = "\n", AutoFlush = true };
    }

    public static async Task<ManagementClient> ConnectAsync(int port, string password, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        TcpClient? tcp = null;
        while (tcp is null)
        {
            ct.ThrowIfCancellationRequested();
            var candidate = new TcpClient(AddressFamily.InterNetwork) { NoDelay = true };
            try
            {
                await candidate.ConnectAsync(IPAddress.Loopback, port, ct).ConfigureAwait(false);
                tcp = candidate;
            }
            catch (SocketException) when (DateTime.UtcNow < deadline)
            {
                candidate.Dispose();
                await Task.Delay(200, ct).ConfigureAwait(false);
            }
            catch
            {
                candidate.Dispose();
                throw;
            }
        }

        var client = new ManagementClient(tcp);
        try
        {
            // openvpn prints "ENTER PASSWORD:" without newline; just answer it.
            await client._writer.WriteLineAsync(password.AsMemory(), ct).ConfigureAwait(false);

            using var authTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            authTimeout.CancelAfter(TimeSpan.FromSeconds(10));
            while (true)
            {
                var line = await client._reader.ReadLineAsync(authTimeout.Token).ConfigureAwait(false)
                           ?? throw new IOException("Management-interface sloot de verbinding tijdens authenticatie.");
                if (line.Contains("SUCCESS: password is correct", StringComparison.Ordinal))
                {
                    break;
                }
                if (line.Contains("ERROR:", StringComparison.Ordinal))
                {
                    throw new IOException("Management-interface weigerde het wachtwoord: " + line);
                }
                // Anything else (e.g. >INFO) is forwarded later once the loop runs.
            }

            _ = client.ReadLoopAsync();
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    /// <summary>Sends a command and returns the full reply (SUCCESS/ERROR line included).</summary>
    public async Task<string> SendAsync(string command, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _commandLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_pendingBuffer)
            {
                _pendingBuffer.Clear();
                _pending = tcs;
            }
            await _writer.WriteLineAsync(command.AsMemory(), ct).ConfigureAwait(false);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            await using var reg = timeout.Token.Register(() => tcs.TrySetCanceled());
            return await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            lock (_pendingBuffer)
            {
                _pending = null;
            }
            _commandLock.Release();
        }
    }

    /// <summary>Fire-and-forget variant used for replies that may arrive after the process is already exiting.</summary>
    public async Task TrySendAsync(string command)
    {
        try
        {
            await SendAsync(command).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or SocketException)
        {
            // connection gone
        }
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var line = await _reader.ReadLineAsync(_cts.Token).ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                if (line.StartsWith('>'))
                {
                    int colon = line.IndexOf(':', StringComparison.Ordinal);
                    string type = colon > 0 ? line[1..colon] : line[1..];
                    string payload = colon > 0 ? line[(colon + 1)..] : string.Empty;
                    Notification?.Invoke(type, payload);
                    continue;
                }

                TaskCompletionSource<string>? complete = null;
                string? reply = null;
                lock (_pendingBuffer)
                {
                    _pendingBuffer.Append(line).Append('\n');
                    if (line.StartsWith("SUCCESS:", StringComparison.Ordinal) || line.StartsWith("ERROR:", StringComparison.Ordinal) || line == "END")
                    {
                        complete = _pending;
                        reply = _pendingBuffer.ToString();
                        _pendingBuffer.Clear();
                    }
                }
                complete?.TrySetResult(reply!);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException or SocketException)
        {
            // connection closed
        }
        catch (Exception ex)
        {
            AppLog.Warn("Management read loop: " + ex.Message);
        }
        finally
        {
            lock (_pendingBuffer)
            {
                _pending?.TrySetCanceled();
            }
            Closed?.Invoke();
        }
    }

    /// <summary>Escapes a value for use inside double quotes in a management command.</summary>
    public static string Quote(string value)
        => "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _cts.Cancel();
        try
        {
            _tcp.Dispose();
        }
        catch
        {
            // ignore
        }
        _reader.Dispose();
        _writer.Dispose();
        _commandLock.Dispose();
        _cts.Dispose();
    }
}
