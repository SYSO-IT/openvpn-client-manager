using System.Text.Json.Serialization;

namespace OpenVpnClientManager.Models;

public sealed class AppConfig
{
    public int Version { get; set; } = 1;
    public AppSettings Settings { get; set; } = new();
    public List<Customer> Customers { get; set; } = [];
}

public sealed class AppSettings
{
    public string Theme { get; set; } = "Dark";

    /// <summary>When false (default) openvpn is started with --script-security 1 so config scripts never run.</summary>
    public bool AllowScripts { get; set; }

    public bool MinimizeToTray { get; set; } = true;
    public bool CloseToTray { get; set; } = true;
    public bool StartMinimized { get; set; }
    public bool ShowNotifications { get; set; } = true;
}

public sealed class Customer
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string? Notes { get; set; }
    public List<Site> Sites { get; set; } = [];
}

public sealed class Site
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string? Notes { get; set; }

    /// <summary>Summary of the config, extracted at import time (informational only).</summary>
    public string? Remote { get; set; }
    public string? Protocol { get; set; }
    public bool RequiresUserPass { get; set; }
    public bool RequiresPrivateKeyPassword { get; set; }

    public DateTime? ImportedUtc { get; set; }
    public DateTime? LastConnectedUtc { get; set; }

    [JsonIgnore]
    public string ConfigFileName => Id.ToString("D") + ".ovpn";
}

/// <summary>Credentials persisted (DPAPI-encrypted) per site.</summary>
public sealed class StoredCredentials
{
    public string? Username { get; set; }
    public string? Password { get; set; }
    public string? PrivateKeyPassword { get; set; }
}
