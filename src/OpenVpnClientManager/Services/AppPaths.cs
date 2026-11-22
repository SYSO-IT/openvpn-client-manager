using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;

namespace OpenVpnClientManager.Services;

/// <summary>
/// All persistent data lives under %LOCALAPPDATA%\OpenVpnClientManager. The directory is
/// ACL-hardened on first use so that only the current user and SYSTEM can access it.
/// </summary>
public static class AppPaths
{
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create),
        "OpenVpnClientManager");

    public static string Configs => Path.Combine(Root, "configs");
    public static string Credentials => Path.Combine(Root, "credentials");
    public static string Logs => Path.Combine(Root, "logs");
    public static string ConfigFile => Path.Combine(Root, "config.json");
    public static string AppLogFile => Path.Combine(Logs, "app.log");

    public static string ConfigPathFor(Guid siteId) => Path.Combine(Configs, siteId.ToString("D") + ".ovpn");
    public static string CredentialPathFor(Guid siteId) => Path.Combine(Credentials, siteId.ToString("D") + ".bin");
    public static string LogPathFor(Guid siteId) => Path.Combine(Logs, siteId.ToString("D") + ".log");

    public static void EnsureCreated()
    {
        bool fresh = !Directory.Exists(Root);
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Configs);
        Directory.CreateDirectory(Credentials);
        Directory.CreateDirectory(Logs);

        try
        {
            HardenAcl(new DirectoryInfo(Root), fresh);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Could not harden data directory ACL: " + ex.Message);
        }
    }

    /// <summary>
    /// Disable inheritance and grant access only to the current user and LocalSystem.
    /// Applied when the directory is created, or when inheritance is still enabled.
    /// </summary>
    private static void HardenAcl(DirectoryInfo dir, bool fresh)
    {
        var security = dir.GetAccessControl();
        if (!fresh && security.AreAccessRulesProtected)
        {
            return;
        }

        var user = WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException("No user SID");
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;

        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, false, typeof(SecurityIdentifier)))
        {
            security.RemoveAccessRule(rule);
        }
        security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(system, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        dir.SetAccessControl(security);
    }
}

/// <summary>Tiny append-only application log (no secrets are ever written here).</summary>
public static class AppLog
{
    private static readonly object Lock = new();

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message) => Write("ERROR", message);

    private static void Write(string level, string message)
    {
        try
        {
            lock (Lock)
            {
                Directory.CreateDirectory(AppPaths.Logs);
                var fi = new FileInfo(AppPaths.AppLogFile);
                if (fi.Exists && fi.Length > 2 * 1024 * 1024)
                {
                    fi.Delete();
                }
                File.AppendAllText(AppPaths.AppLogFile, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never break the application.
        }
    }
}
