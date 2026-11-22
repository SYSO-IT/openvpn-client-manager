using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenVpnClientManager.Models;

namespace OpenVpnClientManager.Services;

public sealed class BundleManifest
{
    public string Format { get; set; } = "ovpn-client-manager-bundle";
    public int Version { get; set; } = 1;
    public DateTime ExportedUtc { get; set; }
    public string? Application { get; set; }
    public List<BundleCustomer> Customers { get; set; } = [];
}

public sealed class BundleCustomer
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Notes { get; set; }
    public List<BundleSite> Sites { get; set; } = [];
}

public sealed class BundleSite
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Notes { get; set; }
    public string? Remote { get; set; }
    public string? Protocol { get; set; }
    public bool RequiresUserPass { get; set; }
    public bool RequiresPrivateKeyPassword { get; set; }
    public bool HasCredentials { get; set; }
}

public sealed class ExportOptions
{
    public required string OutputPath { get; init; }
    public string? Passphrase { get; init; }
    public bool IncludeCredentials { get; init; }
    public required IReadOnlyList<Customer> Customers { get; init; }
}

public sealed class BundleContents
{
    public required BundleManifest Manifest { get; init; }
    public required Dictionary<Guid, string> Configs { get; init; }
    public required Dictionary<Guid, StoredCredentials> Credentials { get; init; }
    public bool WasEncrypted { get; init; }
    public int SiteCount => Manifest.Customers.Sum(c => c.Sites.Count);
}

public sealed class ImportOptions
{
    public bool OverwriteExisting { get; init; }
    public bool ImportCredentials { get; init; }
}

public sealed class ImportSummary
{
    public int CustomersAdded { get; set; }
    public int SitesAdded { get; set; }
    public int SitesUpdated { get; set; }
    public int SitesSkipped { get; set; }
    public int CredentialsImported { get; set; }
    public List<string> Warnings { get; } = [];
}

public sealed class BundleFormatException(string message) : Exception(message);

/// <summary>
/// Bundle = zip with manifest.json + configs/&lt;id&gt;.ovpn (+ credentials/&lt;id&gt;.json), optionally
/// wrapped in an AES-256-GCM container keyed from a passphrase (PBKDF2-HMAC-SHA512).
/// Credentials are only ever exported inside an encrypted container.
/// </summary>
public static class ImportExportService
{
    private static readonly byte[] Magic = "OVCMBNDL"u8.ToArray();
    private const byte ContainerVersion = 1;
    private const int KdfIterations = 600_000;
    private const int SaltSize = 16;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int KeySize = 32;

    private const long MaxBundleBytes = 64L * 1024 * 1024;
    private const long MaxManifestBytes = 4L * 1024 * 1024;
    private const long MaxCredentialBytes = 64L * 1024;
    private const int MaxEntries = 5000;

    public const string EncryptedExtension = ".ovpnbundle";
    public const string PlainExtension = ".zip";

    // ------------------------------------------------------------------ export

    public static void Export(ExportOptions options)
    {
        if (options.IncludeCredentials && string.IsNullOrEmpty(options.Passphrase))
        {
            throw new InvalidOperationException("Inloggegevens kunnen alleen in een versleutelde bundel worden geëxporteerd.");
        }

        var manifest = new BundleManifest
        {
            ExportedUtc = DateTime.UtcNow,
            Application = "OpenVpnClientManager 1.0",
        };

        using var zipStream = new MemoryStream();
        using (var zip = new ZipArchive(zipStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var customer in options.Customers)
            {
                var bc = new BundleCustomer { Id = customer.Id, Name = customer.Name, Notes = customer.Notes };
                foreach (var site in customer.Sites)
                {
                    var configPath = AppPaths.ConfigPathFor(site.Id);
                    if (!File.Exists(configPath))
                    {
                        continue;
                    }

                    var bs = new BundleSite
                    {
                        Id = site.Id,
                        Name = site.Name,
                        Notes = site.Notes,
                        Remote = site.Remote,
                        Protocol = site.Protocol,
                        RequiresUserPass = site.RequiresUserPass,
                        RequiresPrivateKeyPassword = site.RequiresPrivateKeyPassword,
                    };

                    WriteEntry(zip, $"configs/{site.Id:D}.ovpn", File.ReadAllBytes(configPath));

                    if (options.IncludeCredentials && CredentialStore.Load(site.Id) is { } creds)
                    {
                        WriteEntry(zip, $"credentials/{site.Id:D}.json", JsonSerializer.SerializeToUtf8Bytes(creds, ConfigStore.JsonOptions));
                        bs.HasCredentials = true;
                    }
                    bc.Sites.Add(bs);
                }
                manifest.Customers.Add(bc);
            }
            WriteEntry(zip, "manifest.json", JsonSerializer.SerializeToUtf8Bytes(manifest, ConfigStore.JsonOptions));
        }

        var zipBytes = zipStream.ToArray();
        try
        {
            byte[] output = string.IsNullOrEmpty(options.Passphrase) ? zipBytes : Encrypt(zipBytes, options.Passphrase);
            var tmp = options.OutputPath + ".tmp";
            File.WriteAllBytes(tmp, output);
            File.Move(tmp, options.OutputPath, overwrite: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(zipBytes);
        }
    }

    private static void WriteEntry(ZipArchive zip, string name, byte[] data)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var s = entry.Open();
        s.Write(data);
    }

    // ------------------------------------------------------------------ import

    public static bool IsEncryptedBundle(string path)
    {
        using var fs = File.OpenRead(path);
        var head = new byte[Magic.Length];
        return fs.Read(head, 0, head.Length) == head.Length && head.AsSpan().SequenceEqual(Magic);
    }

    public static BundleContents Read(string path, string? passphrase)
    {
        var fi = new FileInfo(path);
        if (fi.Length > MaxBundleBytes)
        {
            throw new BundleFormatException("De bundel is groter dan 64 MB en wordt geweigerd.");
        }

        var raw = File.ReadAllBytes(path);
        bool encrypted = raw.Length >= Magic.Length && raw.AsSpan(0, Magic.Length).SequenceEqual(Magic);
        byte[] zipBytes = raw;
        if (encrypted)
        {
            if (string.IsNullOrEmpty(passphrase))
            {
                throw new BundleFormatException("Deze bundel is versleuteld; een wachtwoordzin is vereist.");
            }
            zipBytes = Decrypt(raw, passphrase);
        }

        try
        {
            return ReadZip(zipBytes, encrypted);
        }
        finally
        {
            if (encrypted)
            {
                CryptographicOperations.ZeroMemory(zipBytes);
            }
        }
    }

    private static BundleContents ReadZip(byte[] zipBytes, bool encrypted)
    {
        using var ms = new MemoryStream(zipBytes, writable: false);
        ZipArchive zip;
        try
        {
            zip = new ZipArchive(ms, ZipArchiveMode.Read);
        }
        catch (InvalidDataException)
        {
            throw new BundleFormatException("Het bestand is geen geldige bundel (zip).");
        }

        using (zip)
        {
            if (zip.Entries.Count > MaxEntries)
            {
                throw new BundleFormatException("De bundel bevat te veel bestanden.");
            }

            var manifestEntry = zip.GetEntry("manifest.json") ?? throw new BundleFormatException("manifest.json ontbreekt in de bundel.");
            if (manifestEntry.Length > MaxManifestBytes)
            {
                throw new BundleFormatException("manifest.json is te groot.");
            }

            BundleManifest manifest;
            using (var s = manifestEntry.Open())
            {
                manifest = JsonSerializer.Deserialize<BundleManifest>(ReadAll(s, MaxManifestBytes), ConfigStore.JsonOptions)
                           ?? throw new BundleFormatException("manifest.json is leeg.");
            }
            if (manifest.Format != "ovpn-client-manager-bundle" || manifest.Version != 1)
            {
                throw new BundleFormatException("Onbekend bundelformaat of versie.");
            }
            manifest.Customers ??= [];
            foreach (var c in manifest.Customers)
            {
                c.Sites ??= [];
            }

            var configs = new Dictionary<Guid, string>();
            var credentials = new Dictionary<Guid, StoredCredentials>();
            long total = 0;

            foreach (var site in manifest.Customers.SelectMany(c => c.Sites))
            {
                // Entry names are derived from the manifest ids, never taken from the zip directory.
                var cfg = zip.GetEntry($"configs/{site.Id:D}.ovpn");
                if (cfg is null)
                {
                    continue;
                }
                if (cfg.Length > OvpnParser.MaxConfigBytes)
                {
                    throw new BundleFormatException($"Config van '{site.Name}' is groter dan 1 MB.");
                }
                total += cfg.Length;
                using (var s = cfg.Open())
                {
                    configs[site.Id] = Encoding.UTF8.GetString(ReadAll(s, OvpnParser.MaxConfigBytes)).TrimStart('﻿');
                }

                if (site.HasCredentials && encrypted)
                {
                    var cred = zip.GetEntry($"credentials/{site.Id:D}.json");
                    if (cred is not null && cred.Length <= MaxCredentialBytes)
                    {
                        using var s = cred.Open();
                        var sc = JsonSerializer.Deserialize<StoredCredentials>(ReadAll(s, MaxCredentialBytes), ConfigStore.JsonOptions);
                        if (sc is not null)
                        {
                            credentials[site.Id] = sc;
                        }
                    }
                }

                if (total > MaxBundleBytes)
                {
                    throw new BundleFormatException("De bundel is te groot.");
                }
            }

            return new BundleContents { Manifest = manifest, Configs = configs, Credentials = credentials, WasEncrypted = encrypted };
        }
    }

    private static byte[] ReadAll(Stream s, long limit)
    {
        using var ms = new MemoryStream();
        var buf = new byte[16 * 1024];
        int n;
        while ((n = s.Read(buf, 0, buf.Length)) > 0)
        {
            ms.Write(buf, 0, n);
            if (ms.Length > limit)
            {
                throw new BundleFormatException("Een bestand in de bundel overschrijdt de maximale grootte.");
            }
        }
        return ms.ToArray();
    }

    public static ImportSummary Apply(ConfigStore store, BundleContents bundle, ImportOptions options)
    {
        var summary = new ImportSummary();
        var config = store.Config;

        foreach (var bc in bundle.Manifest.Customers)
        {
            var customer = config.Customers.FirstOrDefault(c => c.Id == bc.Id)
                           ?? config.Customers.FirstOrDefault(c => c.Name.Equals(bc.Name, StringComparison.OrdinalIgnoreCase));
            if (customer is null)
            {
                customer = new Customer { Id = bc.Id, Name = SafeName(bc.Name, "Klant"), Notes = bc.Notes };
                config.Customers.Add(customer);
                summary.CustomersAdded++;
            }

            foreach (var bs in bc.Sites)
            {
                if (!bundle.Configs.TryGetValue(bs.Id, out var content))
                {
                    summary.Warnings.Add($"Site '{bs.Name}' heeft geen config in de bundel en is overgeslagen.");
                    summary.SitesSkipped++;
                    continue;
                }

                var existing = config.Customers.SelectMany(c => c.Sites).FirstOrDefault(s => s.Id == bs.Id);
                if (existing is not null && !options.OverwriteExisting)
                {
                    summary.SitesSkipped++;
                    continue;
                }

                ParsedOvpn parsed;
                try
                {
                    parsed = OvpnParser.Parse(content, baseDirectory: null);
                }
                catch (Exception ex)
                {
                    summary.Warnings.Add($"Config van '{bs.Name}' kon niet worden verwerkt: {ex.Message}");
                    summary.SitesSkipped++;
                    continue;
                }
                foreach (var b in parsed.BlockedDirectives)
                {
                    summary.Warnings.Add($"'{bs.Name}': script-directive verwijderd: {b}");
                }

                if (existing is null)
                {
                    var site = new Site { Id = bs.Id, Name = SafeName(bs.Name, "Site"), Notes = bs.Notes };
                    ConfigStore.WriteConfigFile(site.Id, parsed.NormalizedContent);
                    FillSite(site, parsed);
                    customer.Sites.Add(site);
                    summary.SitesAdded++;
                }
                else
                {
                    ConfigStore.WriteConfigFile(existing.Id, parsed.NormalizedContent);
                    existing.Name = SafeName(bs.Name, existing.Name);
                    existing.Notes = bs.Notes;
                    FillSite(existing, parsed);
                    summary.SitesUpdated++;
                }

                if (options.ImportCredentials && bundle.Credentials.TryGetValue(bs.Id, out var creds))
                {
                    CredentialStore.Save(bs.Id, creds);
                    summary.CredentialsImported++;
                }
            }
        }

        store.Save();
        return summary;
    }

    private static void FillSite(Site site, ParsedOvpn parsed)
    {
        site.Remote = parsed.RemoteSummary;
        site.Protocol = parsed.Protocol;
        site.RequiresUserPass = parsed.RequiresUserPass;
        site.RequiresPrivateKeyPassword = parsed.RequiresPrivateKeyPassword;
        site.ImportedUtc = DateTime.UtcNow;
    }

    private static string SafeName(string? name, string fallback)
    {
        var n = (name ?? string.Empty).Trim();
        if (n.Length == 0)
        {
            return fallback;
        }
        return n.Length > 200 ? n[..200] : n;
    }

    // ------------------------------------------------------------------ crypto

    private static byte[] Encrypt(byte[] plaintext, string passphrase)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var header = BuildHeader(salt, nonce, KdfIterations);
        var key = DeriveKey(passphrase, salt, KdfIterations);
        try
        {
            var cipher = new byte[plaintext.Length];
            var tag = new byte[TagSize];
            using (var aes = new AesGcm(key, TagSize))
            {
                aes.Encrypt(nonce, plaintext, cipher, tag, header);
            }
            var output = new byte[header.Length + TagSize + cipher.Length];
            header.CopyTo(output, 0);
            tag.CopyTo(output, header.Length);
            cipher.CopyTo(output, header.Length + TagSize);
            return output;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private static byte[] Decrypt(byte[] container, string passphrase)
    {
        int headerLen = Magic.Length + 1 + 4 + SaltSize + NonceSize;
        if (container.Length < headerLen + TagSize)
        {
            throw new BundleFormatException("De versleutelde bundel is beschadigd.");
        }
        int pos = Magic.Length;
        byte version = container[pos++];
        if (version != ContainerVersion)
        {
            throw new BundleFormatException("Onbekende versie van de versleutelde bundel.");
        }
        int iterations = BitConverter.ToInt32(container, pos);
        pos += 4;
        if (iterations < 100_000 || iterations > 10_000_000)
        {
            throw new BundleFormatException("Ongeldige KDF-parameters in de bundel.");
        }
        var salt = container.AsSpan(pos, SaltSize).ToArray();
        pos += SaltSize;
        var nonce = container.AsSpan(pos, NonceSize).ToArray();
        pos += NonceSize;
        var header = container.AsSpan(0, headerLen).ToArray();
        var tag = container.AsSpan(pos, TagSize).ToArray();
        pos += TagSize;
        var cipher = container.AsSpan(pos).ToArray();

        var key = DeriveKey(passphrase, salt, iterations);
        try
        {
            var plain = new byte[cipher.Length];
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(nonce, cipher, tag, plain, header);
            return plain;
        }
        catch (CryptographicException)
        {
            throw new BundleFormatException("Onjuiste wachtwoordzin of beschadigde bundel.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private static byte[] BuildHeader(byte[] salt, byte[] nonce, int iterations)
    {
        var header = new byte[Magic.Length + 1 + 4 + SaltSize + NonceSize];
        int pos = 0;
        Magic.CopyTo(header, pos);
        pos += Magic.Length;
        header[pos++] = ContainerVersion;
        BitConverter.GetBytes(iterations).CopyTo(header, pos);
        pos += 4;
        salt.CopyTo(header, pos);
        pos += SaltSize;
        nonce.CopyTo(header, pos);
        return header;
    }

    private static byte[] DeriveKey(string passphrase, byte[] salt, int iterations)
        => Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(passphrase.Normalize(NormalizationForm.FormKC)), salt, iterations, HashAlgorithmName.SHA512, KeySize);
}
