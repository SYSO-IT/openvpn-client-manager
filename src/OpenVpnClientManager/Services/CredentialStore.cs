using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenVpnClientManager.Infrastructure;
using OpenVpnClientManager.Models;

namespace OpenVpnClientManager.Services;

/// <summary>
/// Persists per-site credentials using Windows DPAPI (CurrentUser scope) with per-site entropy.
/// The blobs can only be decrypted by the same Windows user on the same machine/domain profile.
/// </summary>
public static class CredentialStore
{
    private static readonly byte[] AppEntropy = Encoding.UTF8.GetBytes("OpenVpnClientManager.v1");

    private static byte[] EntropyFor(Guid siteId)
    {
        var id = siteId.ToByteArray();
        var e = new byte[AppEntropy.Length + id.Length];
        AppEntropy.CopyTo(e, 0);
        id.CopyTo(e, AppEntropy.Length);
        return e;
    }

    public static bool Exists(Guid siteId) => File.Exists(AppPaths.CredentialPathFor(siteId));

    public static void Save(Guid siteId, StoredCredentials credentials)
    {
        var plain = JsonSerializer.SerializeToUtf8Bytes(credentials, ConfigStore.JsonOptions);
        try
        {
            var blob = NativeMethods.DpapiProtect(plain, EntropyFor(siteId));
            Directory.CreateDirectory(AppPaths.Credentials);
            var path = AppPaths.CredentialPathFor(siteId);
            File.WriteAllBytes(path + ".tmp", blob);
            File.Move(path + ".tmp", path, overwrite: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    public static StoredCredentials? Load(Guid siteId)
    {
        var path = AppPaths.CredentialPathFor(siteId);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var blob = File.ReadAllBytes(path);
            var plain = NativeMethods.DpapiUnprotect(blob, EntropyFor(siteId));
            try
            {
                return JsonSerializer.Deserialize<StoredCredentials>(plain, ConfigStore.JsonOptions);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plain);
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Stored credentials for {siteId} could not be decrypted: {ex.Message}");
            return null;
        }
    }

    public static void Delete(Guid siteId)
    {
        var path = AppPaths.CredentialPathFor(siteId);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
