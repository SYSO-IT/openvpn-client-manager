using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace OpenVpnClientManager.Services;

public sealed class ScannedConfig
{
    public required string Path { get; init; }
    public required string FileName { get; init; }
    public required string SuggestedCustomer { get; init; }
    public required string SuggestedSite { get; init; }
    public ParsedOvpn? Parsed { get; init; }
    public string? Error { get; init; }
}

/// <summary>
/// Finds the .ovpn files OpenVPN GUI uses (%USERPROFILE%\OpenVPN\config and the global config
/// directory) and proposes a customer / site name from the file name. Read-only: the original
/// files are never modified or removed.
/// </summary>
public static partial class GuiConfigScanner
{
    private const int MaxFiles = 2000;

    public static IReadOnlyList<string> DefaultDirectories()
    {
        var dirs = new List<string>
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "OpenVPN", "config"),
        };
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\OpenVPN");
            if (key?.GetValue("config_dir") is string global && !string.IsNullOrWhiteSpace(global))
            {
                dirs.Add(global.TrimEnd('\\'));
            }
        }
        catch
        {
            // ignore
        }
        return dirs.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static List<ScannedConfig> Scan(string directory)
    {
        var results = new List<ScannedConfig>();
        var options = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 3, IgnoreInaccessible = true };
        foreach (var path in Directory.EnumerateFiles(directory, "*.ovpn", options).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).Take(MaxFiles))
        {
            var (customer, site) = SuggestNames(Path.GetFileNameWithoutExtension(path));
            ParsedOvpn? parsed = null;
            string? error = null;
            try
            {
                parsed = OvpnParser.ParseFile(path);
                if (parsed.Remotes.Count == 0)
                {
                    error = "geen 'remote' gevonden";
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }
            results.Add(new ScannedConfig
            {
                Path = path,
                FileName = Path.GetFileName(path),
                SuggestedCustomer = customer,
                SuggestedSite = site,
                Parsed = parsed,
                Error = error,
            });
        }
        return Disambiguate(results);
    }

    /// <summary>Two exports for the same router (different VPN users) get the user name appended.</summary>
    private static List<ScannedConfig> Disambiguate(List<ScannedConfig> items)
    {
        var duplicates = items
            .GroupBy(i => (i.SuggestedCustomer + "|" + i.SuggestedSite).ToUpperInvariant())
            .Where(g => g.Count() > 1)
            .SelectMany(g => g)
            .ToHashSet();

        return items.Select(i =>
        {
            if (!duplicates.Contains(i))
            {
                return i;
            }
            var user = ExtractUser(Path.GetFileNameWithoutExtension(i.Path));
            return new ScannedConfig
            {
                Path = i.Path,
                FileName = i.FileName,
                SuggestedCustomer = i.SuggestedCustomer,
                SuggestedSite = user is null ? i.SuggestedSite : $"{i.SuggestedSite} ({user})",
                Parsed = i.Parsed,
                Error = i.Error,
            };
        }).ToList();
    }

    private static string? ExtractUser(string fileNameWithoutExtension)
    {
        var name = CopySuffixRegex().Replace(fileNameWithoutExtension.Trim(), string.Empty).Trim();
        if (name.EndsWith("-config", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^7];
        }
        var tokens = name.Split('-', StringSplitOptions.RemoveEmptyEntries);
        int deviceIndex = Array.FindIndex(tokens, t => DeviceTokenRegex().IsMatch(t));
        // DEVICE-PROTO-PORT-USER...
        return deviceIndex >= 0 && tokens.Length > deviceIndex + 3 ? string.Join("-", tokens[(deviceIndex + 3)..]) : null;
    }

    [GeneratedRegex(@"(\s*\(\d+\)|\s*\[\d+\])+$")]
    private static partial Regex CopySuffixRegex();

    [GeneratedRegex(@"^(RT|FW|GW|SW|RTKVM|VPN)[A-Z]*\d+$", RegexOptions.IgnoreCase)]
    private static partial Regex DeviceTokenRegex();

    /// <summary>
    /// pfSense-style export names look like CUSTOMER[-LOCATION]-DEVICE-PROTO-PORT-USER-config.
    /// Everything before the device token is customer (first part) and location (rest).
    /// </summary>
    public static (string customer, string site) SuggestNames(string fileNameWithoutExtension)
    {
        string name = fileNameWithoutExtension.Trim();
        string previous;
        do
        {
            previous = name;
            name = CopySuffixRegex().Replace(name, string.Empty).Trim();
            if (name.EndsWith("-config", StringComparison.OrdinalIgnoreCase))
            {
                name = name[..^7];
            }
        }
        while (name != previous && name.Length > 0);

        if (name.Length == 0)
        {
            return ("Overig", fileNameWithoutExtension);
        }

        var tokens = name.Split('-', StringSplitOptions.RemoveEmptyEntries);
        int deviceIndex = Array.FindIndex(tokens, t => DeviceTokenRegex().IsMatch(t));

        if (deviceIndex > 0)
        {
            string customer = tokens[0];
            string site = deviceIndex > 1 ? string.Join("-", tokens[1..deviceIndex]) : tokens[deviceIndex].ToUpperInvariant();
            return (customer, site);
        }
        if (deviceIndex == 0)
        {
            return ("Overig", name);
        }
        return (tokens[0], tokens.Length == 1 ? tokens[0] : name);
    }
}
