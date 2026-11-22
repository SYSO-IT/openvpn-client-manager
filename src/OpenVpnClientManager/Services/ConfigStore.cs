using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenVpnClientManager.Models;

namespace OpenVpnClientManager.Services;

/// <summary>Loads and saves config.json atomically and manages the per-site .ovpn files.</summary>
public sealed class ConfigStore
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly object _lock = new();

    public AppConfig Config { get; private set; } = new();

    public void Load()
    {
        AppPaths.EnsureCreated();
        if (!File.Exists(AppPaths.ConfigFile))
        {
            Config = new AppConfig();
            return;
        }

        try
        {
            var json = File.ReadAllText(AppPaths.ConfigFile, Encoding.UTF8);
            Config = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions) ?? new AppConfig();
        }
        catch (Exception ex)
        {
            AppLog.Error("config.json could not be read, starting with an empty configuration: " + ex.Message);
            var backup = AppPaths.ConfigFile + ".corrupt-" + DateTime.Now.ToString("yyyyMMddHHmmss");
            File.Copy(AppPaths.ConfigFile, backup, overwrite: true);
            Config = new AppConfig();
        }

        // Defensive normalisation.
        Config.Customers ??= [];
        foreach (var c in Config.Customers)
        {
            c.Sites ??= [];
        }
        Config.Settings ??= new AppSettings();
    }

    public void Save()
    {
        lock (_lock)
        {
            var json = JsonSerializer.Serialize(Config, JsonOptions);
            var tmp = AppPaths.ConfigFile + ".tmp";
            File.WriteAllText(tmp, json, new UTF8Encoding(false));
            File.Move(tmp, AppPaths.ConfigFile, overwrite: true);
        }
    }

    // ------------------------------------------------------------ customers / sites

    public Customer AddCustomer(string name, string? notes)
    {
        var c = new Customer { Name = name.Trim(), Notes = NullIfEmpty(notes) };
        Config.Customers.Add(c);
        Save();
        return c;
    }

    public void RemoveCustomer(Customer customer)
    {
        foreach (var site in customer.Sites.ToList())
        {
            DeleteSiteFiles(site);
        }
        Config.Customers.Remove(customer);
        Save();
    }

    public Site AddSite(Customer customer, string name, string? notes, ParsedOvpn parsed)
    {
        var site = new Site
        {
            Name = name.Trim(),
            Notes = NullIfEmpty(notes),
            Remote = parsed.RemoteSummary,
            Protocol = parsed.Protocol,
            RequiresUserPass = parsed.RequiresUserPass,
            RequiresPrivateKeyPassword = parsed.RequiresPrivateKeyPassword,
            ImportedUtc = DateTime.UtcNow,
        };
        WriteConfigFile(site.Id, parsed.NormalizedContent);
        customer.Sites.Add(site);
        Save();
        return site;
    }

    public void ReplaceSiteConfig(Site site, ParsedOvpn parsed)
    {
        WriteConfigFile(site.Id, parsed.NormalizedContent);
        site.Remote = parsed.RemoteSummary;
        site.Protocol = parsed.Protocol;
        site.RequiresUserPass = parsed.RequiresUserPass;
        site.RequiresPrivateKeyPassword = parsed.RequiresPrivateKeyPassword;
        site.ImportedUtc = DateTime.UtcNow;
        Save();
    }

    public void RemoveSite(Customer customer, Site site)
    {
        DeleteSiteFiles(site);
        customer.Sites.Remove(site);
        Save();
    }

    public void MoveSite(Customer from, Customer to, Site site)
    {
        if (from == to)
        {
            return;
        }
        from.Sites.Remove(site);
        to.Sites.Add(site);
        Save();
    }

    public static string ReadConfigFile(Site site)
        => File.Exists(AppPaths.ConfigPathFor(site.Id)) ? File.ReadAllText(AppPaths.ConfigPathFor(site.Id), Encoding.UTF8) : string.Empty;

    public static void WriteConfigFile(Guid siteId, string content)
    {
        var path = AppPaths.ConfigPathFor(siteId);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, content, new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }

    private static void DeleteSiteFiles(Site site)
    {
        SafeDelete(AppPaths.ConfigPathFor(site.Id));
        SafeDelete(AppPaths.CredentialPathFor(site.Id));
        SafeDelete(AppPaths.LogPathFor(site.Id));
    }

    private static void SafeDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Could not delete {path}: {ex.Message}");
        }
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
