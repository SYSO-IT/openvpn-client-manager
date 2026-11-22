using System.IO;
using System.Text;
using OpenVpnClientManager.Models;

namespace OpenVpnClientManager.Services;

public sealed class ParsedOvpn
{
    public string NormalizedContent { get; init; } = string.Empty;
    public List<string> Warnings { get; } = [];

    /// <summary>Directives that can execute code and were found in the config.</summary>
    public List<string> BlockedDirectives { get; } = [];

    /// <summary>Directives that were removed because the manager supplies them itself.</summary>
    public List<string> StrippedDirectives { get; } = [];

    /// <summary>Credentials found in a referenced/inline auth-user-pass file (never written to the config).</summary>
    public StoredCredentials? ExtractedCredentials { get; set; }

    public List<string> Remotes { get; } = [];
    public string? Protocol { get; set; }
    public string? Device { get; set; }
    public bool RequiresUserPass { get; set; }
    public bool RequiresPrivateKeyPassword { get; set; }
    public bool HasCertificate { get; set; }

    public string RemoteSummary => Remotes.Count switch
    {
        0 => "(geen remote)",
        1 => Remotes[0],
        _ => $"{Remotes[0]} (+{Remotes.Count - 1})",
    };
}

public sealed class OvpnParseOptions
{
    /// <summary>Keep directives that can execute scripts/plugins instead of removing them.</summary>
    public bool KeepBlockedDirectives { get; set; }
}

/// <summary>
/// Parses an OpenVPN client configuration, inlines referenced certificate/key files so the
/// result is fully self-contained, strips directives the manager controls itself and flags
/// directives that could execute code on the client.
/// </summary>
public static class OvpnParser
{
    public const int MaxConfigBytes = 1024 * 1024;
    public const int MaxReferencedFileBytes = 1024 * 1024;

    private static readonly HashSet<string> InlineableFileDirectives = new(StringComparer.OrdinalIgnoreCase)
    {
        "ca", "cert", "key", "tls-auth", "tls-crypt", "tls-crypt-v2", "dh", "extra-certs", "pkcs12", "secret", "crl-verify",
    };

    private static readonly HashSet<string> BinaryFileDirectives = new(StringComparer.OrdinalIgnoreCase) { "pkcs12" };

    /// <summary>Directives that can run arbitrary code on the client machine.</summary>
    private static readonly HashSet<string> DangerousDirectives = new(StringComparer.OrdinalIgnoreCase)
    {
        "up", "down", "route-up", "route-pre-down", "ipchange", "tls-verify", "auth-user-pass-verify",
        "client-connect", "client-disconnect", "learn-address", "plugin", "dns-updown", "up-restart",
    };

    /// <summary>Directives the manager provides on the command line or that make no sense inside a managed config.</summary>
    private static readonly HashSet<string> StrippedDirectives = new(StringComparer.OrdinalIgnoreCase)
    {
        "management", "management-client", "management-hold", "management-query-passwords", "management-query-proxy",
        "management-forget-disconnect", "management-signal", "management-log-cache", "management-up-down",
        "management-client-auth", "management-external-key", "management-external-cert",
        "log", "log-append", "daemon", "service", "writepid", "askpass", "auth-retry", "config",
    };

    public static ParsedOvpn ParseFile(string path, OvpnParseOptions? options = null)
    {
        var fi = new FileInfo(path);
        if (fi.Length > MaxConfigBytes)
        {
            throw new InvalidDataException("Het configuratiebestand is groter dan 1 MB en wordt geweigerd.");
        }
        var text = ReadText(path);
        return Parse(text, fi.DirectoryName, options);
    }

    public static ParsedOvpn Parse(string content, string? baseDirectory, OvpnParseOptions? options = null)
    {
        options ??= new OvpnParseOptions();
        var result = new ParsedOvpn();
        var output = new StringBuilder();
        var lines = content.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

        Emit(output, result, lines, baseDirectory, options, depth: 0);

        var normalized = output.ToString();
        return new ParsedOvpn
        {
            NormalizedContent = normalized,
            ExtractedCredentials = result.ExtractedCredentials,
            Protocol = result.Protocol,
            Device = result.Device,
            RequiresUserPass = result.RequiresUserPass,
            RequiresPrivateKeyPassword = result.RequiresPrivateKeyPassword,
            HasCertificate = result.HasCertificate,
        }.CopyListsFrom(result);
    }

    private static ParsedOvpn CopyListsFrom(this ParsedOvpn target, ParsedOvpn source)
    {
        target.Warnings.AddRange(source.Warnings);
        target.BlockedDirectives.AddRange(source.BlockedDirectives);
        target.StrippedDirectives.AddRange(source.StrippedDirectives);
        target.Remotes.AddRange(source.Remotes);
        return target;
    }

    private static void Emit(StringBuilder output, ParsedOvpn result, string[] lines, string? baseDirectory, OvpnParseOptions options, int depth)
    {
        for (int i = 0; i < lines.Length; i++)
        {
            var raw = lines[i];
            var line = raw.Trim();

            if (line.Length == 0 || line[0] == '#' || line[0] == ';')
            {
                output.Append(raw.TrimEnd()).Append('\n');
                continue;
            }

            // Inline block  <tag> ... </tag>
            if (line.StartsWith('<') && line.EndsWith('>') && !line.StartsWith("</", StringComparison.Ordinal))
            {
                var tag = line[1..^1].Trim();
                var block = new StringBuilder();
                int j = i + 1;
                bool closed = false;
                for (; j < lines.Length; j++)
                {
                    if (lines[j].Trim().Equals("</" + tag + ">", StringComparison.OrdinalIgnoreCase))
                    {
                        closed = true;
                        break;
                    }
                    block.Append(lines[j].TrimEnd()).Append('\n');
                }
                if (!closed)
                {
                    result.Warnings.Add($"Inline blok <{tag}> wordt niet afgesloten.");
                }
                i = j;
                HandleInlineBlock(output, result, tag, block.ToString());
                continue;
            }

            var tokens = Tokenize(line);
            if (tokens.Count == 0)
            {
                continue;
            }
            var directive = tokens[0].TrimStart('-');
            var args = tokens.Skip(1).ToList();

            if (StrippedDirectives.Contains(directive))
            {
                if (directive.Equals("config", StringComparison.OrdinalIgnoreCase) && args.Count == 1)
                {
                    IncludeConfig(output, result, args[0], baseDirectory, options, depth);
                    continue;
                }
                result.StrippedDirectives.Add(line);
                output.Append("# [manager] verwijderd: ").Append(directive).Append('\n');
                continue;
            }

            if (directive.Equals("script-security", StringComparison.OrdinalIgnoreCase))
            {
                int level = args.Count > 0 && int.TryParse(args[0], out var l) ? l : 0;
                if (level >= 2)
                {
                    result.BlockedDirectives.Add(line);
                    if (!options.KeepBlockedDirectives)
                    {
                        output.Append("# [manager] geblokkeerd: ").Append(line).Append('\n');
                        continue;
                    }
                }
                output.Append(line).Append('\n');
                continue;
            }

            if (DangerousDirectives.Contains(directive))
            {
                bool harmless = directive.Equals("dns-updown", StringComparison.OrdinalIgnoreCase)
                                && args.Count > 0 && args[0].Equals("disable", StringComparison.OrdinalIgnoreCase);
                if (!harmless)
                {
                    result.BlockedDirectives.Add(line);
                    if (!options.KeepBlockedDirectives)
                    {
                        output.Append("# [manager] geblokkeerd: ").Append(line).Append('\n');
                        continue;
                    }
                }
                output.Append(line).Append('\n');
                continue;
            }

            switch (directive.ToLowerInvariant())
            {
                case "remote":
                    if (args.Count >= 1)
                    {
                        var r = args[0];
                        if (args.Count >= 2)
                        {
                            r += ":" + args[1];
                        }
                        result.Remotes.Add(r);
                        if (args.Count >= 3)
                        {
                            result.Protocol ??= args[2];
                        }
                    }
                    output.Append(line).Append('\n');
                    break;

                case "proto":
                    if (args.Count >= 1)
                    {
                        result.Protocol = args[0];
                    }
                    output.Append(line).Append('\n');
                    break;

                case "dev":
                    result.Device = args.FirstOrDefault();
                    output.Append(line).Append('\n');
                    break;

                case "auth-user-pass":
                    result.RequiresUserPass = true;
                    if (args.Count >= 1)
                    {
                        // Never carry a plaintext credentials file along. Extract it (user may store it encrypted).
                        TryExtractCredentialsFile(result, args[0], baseDirectory);
                        result.Warnings.Add("auth-user-pass verwees naar een wachtwoordbestand; die verwijzing is verwijderd. Inloggegevens worden via de manager gevraagd (optioneel versleuteld opgeslagen).");
                    }
                    output.Append("auth-user-pass\n");
                    break;

                case "http-proxy-user-pass":
                    result.Warnings.Add("http-proxy-user-pass met bestand wordt niet ondersteund en is verwijderd.");
                    break;

                default:
                    if (InlineableFileDirectives.Contains(directive) && args.Count >= 1)
                    {
                        TryInlineFile(output, result, directive, args, baseDirectory, line);
                    }
                    else
                    {
                        output.Append(line).Append('\n');
                    }
                    break;
            }
        }
    }

    private static void HandleInlineBlock(StringBuilder output, ParsedOvpn result, string tag, string block)
    {
        switch (tag.ToLowerInvariant())
        {
            case "auth-user-pass":
                result.RequiresUserPass = true;
                ExtractCredentials(result, block);
                result.Warnings.Add("Inline <auth-user-pass> blok met wachtwoord gevonden; verwijderd uit de config.");
                output.Append("auth-user-pass\n");
                return;
            case "key":
                if (block.Contains("ENCRYPTED", StringComparison.OrdinalIgnoreCase))
                {
                    result.RequiresPrivateKeyPassword = true;
                }
                result.HasCertificate = true;
                break;
            case "cert":
            case "pkcs12":
                result.HasCertificate = true;
                break;
        }
        output.Append('<').Append(tag).Append(">\n").Append(block);
        if (!block.EndsWith('\n'))
        {
            output.Append('\n');
        }
        output.Append("</").Append(tag).Append(">\n");
    }

    private static void TryInlineFile(StringBuilder output, ParsedOvpn result, string directive, List<string> args, string? baseDirectory, string originalLine)
    {
        var fileArg = args[0];
        var extra = args.Skip(1).ToList();

        // crl-verify <dir> dir  -> directory mode cannot be inlined
        if (directive.Equals("crl-verify", StringComparison.OrdinalIgnoreCase) && extra.Any(a => a.Equals("dir", StringComparison.OrdinalIgnoreCase)))
        {
            result.Warnings.Add("crl-verify in directory-modus kan niet worden ingesloten; regel behouden.");
            output.Append(originalLine).Append('\n');
            return;
        }

        string? resolved = ResolveFile(fileArg, baseDirectory);
        if (resolved is null)
        {
            result.Warnings.Add($"{directive} verwijst naar '{fileArg}' maar dat bestand is niet gevonden; de config werkt mogelijk niet.");
            output.Append(originalLine).Append('\n');
            return;
        }

        var fi = new FileInfo(resolved);
        if (fi.Length > MaxReferencedFileBytes)
        {
            result.Warnings.Add($"{directive}: bestand '{fileArg}' is groter dan 1 MB en is niet ingesloten.");
            output.Append(originalLine).Append('\n');
            return;
        }

        // key-direction for tls-auth / secret
        if ((directive.Equals("tls-auth", StringComparison.OrdinalIgnoreCase) || directive.Equals("secret", StringComparison.OrdinalIgnoreCase)) && extra.Count >= 1)
        {
            output.Append("key-direction ").Append(extra[0]).Append('\n');
        }

        string body;
        if (BinaryFileDirectives.Contains(directive))
        {
            body = Convert.ToBase64String(File.ReadAllBytes(resolved), Base64FormattingOptions.InsertLineBreaks).Replace("\r\n", "\n") + "\n";
        }
        else
        {
            body = ReadText(resolved).Replace("\r\n", "\n").Replace('\r', '\n');
            if (!body.EndsWith('\n'))
            {
                body += "\n";
            }
            if (directive.Equals("key", StringComparison.OrdinalIgnoreCase) && body.Contains("ENCRYPTED", StringComparison.OrdinalIgnoreCase))
            {
                result.RequiresPrivateKeyPassword = true;
            }
        }
        if (directive.ToLowerInvariant() is "key" or "cert" or "pkcs12")
        {
            result.HasCertificate = true;
        }

        output.Append('<').Append(directive).Append(">\n").Append(body).Append("</").Append(directive).Append(">\n");
    }

    private static void IncludeConfig(StringBuilder output, ParsedOvpn result, string fileArg, string? baseDirectory, OvpnParseOptions options, int depth)
    {
        if (depth >= 3)
        {
            result.Warnings.Add("Te diep geneste 'config' verwijzing overgeslagen.");
            return;
        }
        var resolved = ResolveFile(fileArg, baseDirectory);
        if (resolved is null || new FileInfo(resolved).Length > MaxConfigBytes)
        {
            result.Warnings.Add($"config verwijst naar '{fileArg}' dat niet gevonden (of te groot) is; overgeslagen.");
            return;
        }
        output.Append("# [manager] ingesloten uit ").Append(Path.GetFileName(resolved)).Append('\n');
        var lines = ReadText(resolved).Replace("\r\n", "\n").Split('\n');
        Emit(output, result, lines, Path.GetDirectoryName(resolved), options, depth + 1);
    }

    private static void TryExtractCredentialsFile(ParsedOvpn result, string fileArg, string? baseDirectory)
    {
        var resolved = ResolveFile(fileArg, baseDirectory);
        if (resolved is null || new FileInfo(resolved).Length > 64 * 1024)
        {
            return;
        }
        ExtractCredentials(result, ReadText(resolved));
    }

    private static void ExtractCredentials(ParsedOvpn result, string text)
    {
        var parts = text.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).ToList();
        if (parts.Count >= 1)
        {
            result.ExtractedCredentials = new StoredCredentials
            {
                Username = parts[0],
                Password = parts.Count >= 2 ? parts[1] : null,
            };
        }
    }

    private static string? ResolveFile(string fileArg, string? baseDirectory)
    {
        try
        {
            if (fileArg.Contains('\0'))
            {
                return null;
            }
            string candidate = Path.IsPathRooted(fileArg)
                ? fileArg
                : Path.Combine(baseDirectory ?? Directory.GetCurrentDirectory(), fileArg);
            candidate = Path.GetFullPath(candidate);
            return File.Exists(candidate) ? candidate : null;
        }
        catch
        {
            return null;
        }
    }

    private static string ReadText(string path)
    {
        var bytes = File.ReadAllBytes(path);
        // UTF-8 (with or without BOM) is the norm; fall back to Latin-1 for legacy files.
        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes).TrimStart('﻿');
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(bytes);
        }
    }

    /// <summary>OpenVPN-style tokenizer: whitespace separated, single/double quotes, backslash escapes.</summary>
    public static List<string> Tokenize(string line)
    {
        var tokens = new List<string>();
        var cur = new StringBuilder();
        bool inToken = false;
        char quote = '\0';

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (quote != '\0')
            {
                if (c == quote)
                {
                    quote = '\0';
                }
                else if (c == '\\' && i + 1 < line.Length && quote == '"')
                {
                    cur.Append(line[++i]);
                }
                else
                {
                    cur.Append(c);
                }
                continue;
            }

            if ((c == '#' || c == ';') && !inToken)
            {
                break; // trailing comment
            }
            if (char.IsWhiteSpace(c))
            {
                if (inToken)
                {
                    tokens.Add(cur.ToString());
                    cur.Clear();
                    inToken = false;
                }
                continue;
            }
            inToken = true;
            if (c == '"' || c == '\'')
            {
                quote = c;
            }
            else if (c == '\\' && i + 1 < line.Length)
            {
                cur.Append(line[++i]);
            }
            else
            {
                cur.Append(c);
            }
        }
        if (inToken)
        {
            tokens.Add(cur.ToString());
        }
        return tokens;
    }
}
