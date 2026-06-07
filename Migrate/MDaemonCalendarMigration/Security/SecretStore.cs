using System.Runtime.InteropServices;
using System.Text;

namespace MDaemonCalendarMigration.Security;

/// <summary>
/// DPAPI encrypt / decrypt via P/Invoke to crypt32.dll (Windows built-in).
/// Zero NuGet dependencies. Requires no installed packages.
/// Encrypted value is bound to the current Windows user account (CurrentUser scope).
/// Falls back to plain-text storage if DPAPI call fails.
/// </summary>
public static class SecretStore
{
    // ── Win32 structs ─────────────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DATA_BLOB
    {
        public int    cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(
        ref DATA_BLOB pDataIn,
        string?       szDataDescr,
        ref DATA_BLOB pOptionalEntropy,
        IntPtr        pvReserved,
        IntPtr        pPromptStruct,
        int           dwFlags,
        out DATA_BLOB pDataOut);

    [DllImport("crypt32.dll", SetLastError = true)]
    private static extern bool CryptUnprotectData(
        ref DATA_BLOB pDataIn,
        IntPtr        ppszDataDescr,
        ref DATA_BLOB pOptionalEntropy,
        IntPtr        pvReserved,
        IntPtr        pPromptStruct,
        int           dwFlags,
        out DATA_BLOB pDataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);

    // ── constants ─────────────────────────────────────────────────────────────

    private const int CRYPTPROTECT_UI_FORBIDDEN = 0x01;  // no prompts
    private static readonly byte[] Entropy =
        Encoding.UTF8.GetBytes("MDaemonCalMig_GraphSecret_v1");

    // ── public API ────────────────────────────────────────────────────────────

    public static string Protect(string plainText)
    {
        if (string.IsNullOrEmpty(plainText)) return "";
        try
        {
            var input   = ToBlob(Encoding.UTF8.GetBytes(plainText));
            var entropy = ToBlob(Entropy);
            try
            {
                if (CryptProtectData(ref input, "MDCalMig", ref entropy,
                        IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, out var output))
                {
                    try { return BlobToBase64(output); }
                    finally { LocalFree(output.pbData); }
                }
            }
            finally { FreeBlob(ref input); FreeBlob(ref entropy); }
        }
        catch { }
        return plainText;  // DPAPI unavailable — store as-is
    }

    public static string Unprotect(string storedValue)
    {
        if (string.IsNullOrEmpty(storedValue)) return "";
        try
        {
            var bytes = Convert.FromBase64String(storedValue);
            var input   = ToBlob(bytes);
            var entropy = ToBlob(Entropy);
            try
            {
                if (CryptUnprotectData(ref input, IntPtr.Zero, ref entropy,
                        IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, out var output))
                {
                    try { return Encoding.UTF8.GetString(BlobToBytes(output)); }
                    finally { LocalFree(output.pbData); }
                }
            }
            finally { FreeBlob(ref input); FreeBlob(ref entropy); }
        }
        catch { }
        return storedValue;  // Not DPAPI-encoded — return raw value as-is
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static DATA_BLOB ToBlob(byte[] data)
    {
        var ptr = Marshal.AllocHGlobal(data.Length);
        Marshal.Copy(data, 0, ptr, data.Length);
        return new DATA_BLOB { cbData = data.Length, pbData = ptr };
    }

    private static void FreeBlob(ref DATA_BLOB blob)
    {
        if (blob.pbData != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(blob.pbData);
            blob.pbData = IntPtr.Zero;
        }
    }

    private static string BlobToBase64(DATA_BLOB blob)
    {
        var b = new byte[blob.cbData];
        Marshal.Copy(blob.pbData, b, 0, blob.cbData);
        return Convert.ToBase64String(b);
    }

    private static byte[] BlobToBytes(DATA_BLOB blob)
    {
        var b = new byte[blob.cbData];
        Marshal.Copy(blob.pbData, b, 0, blob.cbData);
        return b;
    }
}
