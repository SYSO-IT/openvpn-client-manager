using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using Microsoft.Win32;

namespace OpenVpnClientManager.Services;

/// <summary>Detects the local OpenVPN installation and whether the current user may use the Interactive Service.</summary>
public sealed class OpenVpnEnvironment
{
    public const string ServicePipeName = @"openvpn\service";

    public string? ExePath { get; private set; }
    public string? Version { get; private set; }
    public string AdminGroup { get; private set; } = "OpenVPN Administrators";
    public bool IsInstalled => ExePath is not null && File.Exists(ExePath);
    public bool ServiceAvailable { get; private set; }
    public bool UserIsAuthorized { get; private set; }
    public bool RunningElevated { get; private set; }
    public string UserName { get; private set; } = string.Empty;

    public IReadOnlyList<string> Problems { get; private set; } = [];

    public static OpenVpnEnvironment Detect()
    {
        var env = new OpenVpnEnvironment();
        var problems = new List<string>();

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\OpenVPN");
            env.ExePath = key?.GetValue("exe_path") as string;
            if (key?.GetValue("ovpn_admin_group") is string grp && !string.IsNullOrWhiteSpace(grp))
            {
                env.AdminGroup = grp;
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("Registry read failed: " + ex.Message);
        }

        env.ExePath ??= @"C:\Program Files\OpenVPN\bin\openvpn.exe";
        if (!env.IsInstalled)
        {
            problems.Add("OpenVPN is niet gevonden. Installeer OpenVPN (community edition) 2.5 of nieuwer.");
        }
        else
        {
            try
            {
                var fvi = FileVersionInfo.GetVersionInfo(env.ExePath!);
                env.Version = fvi.ProductVersion ?? fvi.FileVersion;
            }
            catch
            {
                // ignore
            }
        }

        env.ServiceAvailable = PipeExists();
        if (!env.ServiceAvailable)
        {
            problems.Add("De OpenVPN Interactive Service draait niet (service 'OpenVPNServiceInteractive'). Zonder deze service kan een standaard gebruiker geen VPN starten.");
        }

        using (var identity = WindowsIdentity.GetCurrent())
        {
            env.UserName = identity.Name;
            var principal = new WindowsPrincipal(identity);
            env.RunningElevated = principal.IsInRole(WindowsBuiltInRole.Administrator);
            env.UserIsAuthorized = env.RunningElevated || IsMemberOfLocalGroup(identity, env.AdminGroup);
        }

        if (!env.UserIsAuthorized)
        {
            problems.Add($"Gebruiker {env.UserName} is geen lid van de lokale groep '{env.AdminGroup}'. De Interactive Service weigert dan configs buiten C:\\Program Files\\OpenVPN\\config.");
        }
        if (env.RunningElevated)
        {
            problems.Add("De applicatie draait met beheerdersrechten. Start hem als standaard gebruiker; verhoogde rechten zijn niet nodig en onveilig.");
        }

        env.Problems = problems;
        return env;
    }

    private static bool PipeExists()
    {
        try
        {
            return File.Exists(@"\\.\pipe\" + ServicePipeName);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsMemberOfLocalGroup(WindowsIdentity identity, string groupName)
    {
        try
        {
            var sid = (SecurityIdentifier)new NTAccount(groupName).Translate(typeof(SecurityIdentifier));
            return identity.Groups?.Contains(sid) == true;
        }
        catch (IdentityNotMappedException)
        {
            return false;
        }
        catch (Exception ex)
        {
            AppLog.Warn("Group lookup failed: " + ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Asks Windows (UAC) to add the current user to the OpenVPN admin group. This is the only
    /// elevated action in the whole application and runs in a separate net.exe process.
    /// Membership takes effect after the user signs out and in again.
    /// </summary>
    public bool RequestGroupMembership()
    {
        var psi = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "net.exe"),
            ArgumentList = { "localgroup", AdminGroup, UserName, "/add" },
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        try
        {
            using var p = Process.Start(psi);
            p?.WaitForExit(30_000);
            return p?.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // User cancelled the UAC prompt.
            return false;
        }
    }
}
