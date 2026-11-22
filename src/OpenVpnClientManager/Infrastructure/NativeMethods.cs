using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace OpenVpnClientManager.Infrastructure;

/// <summary>
/// Minimal P/Invoke surface. Kept deliberately small: DPAPI for credential protection and
/// DWM for the dark title bar. No other native code is used by the application.
/// </summary>
internal static class NativeMethods
{
    // ---------------------------------------------------------------- DPAPI

    [StructLayout(LayoutKind.Sequential)]
    private struct DATA_BLOB
    {
        public int cbData;
        public IntPtr pbData;
    }

    private const int CRYPTPROTECT_UI_FORBIDDEN = 0x1;

    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DATA_BLOB pDataIn, IntPtr szDataDescr, ref DATA_BLOB pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, out DATA_BLOB pDataOut);

    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DATA_BLOB pDataIn, IntPtr ppszDataDescr, ref DATA_BLOB pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, out DATA_BLOB pDataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);

    /// <summary>Encrypts data for the current Windows user (DPAPI, CurrentUser scope).</summary>
    public static byte[] DpapiProtect(byte[] plaintext, byte[] entropy)
        => DpapiCall(plaintext, entropy, protect: true);

    /// <summary>Decrypts data previously protected with <see cref="DpapiProtect"/>.</summary>
    public static byte[] DpapiUnprotect(byte[] ciphertext, byte[] entropy)
        => DpapiCall(ciphertext, entropy, protect: false);

    private static byte[] DpapiCall(byte[] input, byte[] entropy, bool protect)
    {
        var hIn = GCHandle.Alloc(input, GCHandleType.Pinned);
        var hEnt = GCHandle.Alloc(entropy, GCHandleType.Pinned);
        try
        {
            var inBlob = new DATA_BLOB { cbData = input.Length, pbData = hIn.AddrOfPinnedObject() };
            var entBlob = new DATA_BLOB { cbData = entropy.Length, pbData = hEnt.AddrOfPinnedObject() };

            bool ok = protect
                ? CryptProtectData(ref inBlob, IntPtr.Zero, ref entBlob, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, out var outBlob)
                : CryptUnprotectData(ref inBlob, IntPtr.Zero, ref entBlob, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, out outBlob);

            if (!ok)
            {
                throw new CryptographicException(Marshal.GetLastWin32Error());
            }

            try
            {
                var result = new byte[outBlob.cbData];
                Marshal.Copy(outBlob.pbData, result, 0, outBlob.cbData);
                return result;
            }
            finally
            {
                // Wipe and free the unmanaged output buffer.
                for (int i = 0; i < outBlob.cbData; i++)
                {
                    Marshal.WriteByte(outBlob.pbData, i, 0);
                }
                LocalFree(outBlob.pbData);
            }
        }
        finally
        {
            hIn.Free();
            hEnt.Free();
        }
    }

    // ---------------------------------------------------------------- DWM (dark title bar)

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    public static void SetDarkTitleBar(IntPtr hwnd, bool dark)
    {
        int value = dark ? 1 : 0;
        try
        {
            _ = DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref value, sizeof(int));
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // Older Windows builds: ignore.
        }
    }
}
