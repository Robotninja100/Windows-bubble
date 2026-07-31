using System.Runtime.InteropServices;
using System.Text;

namespace CursorBubble.Native;

/// <summary>
/// Encrypts small secrets (the Anthropic API key) with the Windows Data
/// Protection API (DPAPI), scoped to the current user. The ciphertext is stored
/// as base64 in config.json and can only be decrypted by the same Windows user
/// on the same machine.
/// </summary>
public static class DataProtection
{
    private const int CRYPTPROTECT_UI_FORBIDDEN = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct DATA_BLOB
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DATA_BLOB pDataIn, string? szDataDescr,
        IntPtr pOptionalEntropy, IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, ref DATA_BLOB pDataOut);

    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DATA_BLOB pDataIn, IntPtr ppszDataDescr,
        IntPtr pOptionalEntropy, IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, ref DATA_BLOB pDataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);

    /// <summary>Encrypt <paramref name="plain"/> and return base64 ciphertext (empty if empty).</summary>
    public static string Protect(string? plain)
    {
        if (string.IsNullOrEmpty(plain))
            return "";

        byte[] input = Encoding.UTF8.GetBytes(plain);
        return Convert.ToBase64String(Transform(input, protect: true) ?? input);
    }

    /// <summary>Decrypt a base64 blob produced by <see cref="Protect"/>; returns the input unchanged if it isn't DPAPI ciphertext.</summary>
    public static string Unprotect(string? stored)
    {
        if (string.IsNullOrEmpty(stored))
            return "";

        try
        {
            byte[] blob = Convert.FromBase64String(stored);
            byte[]? plain = Transform(blob, protect: false);
            return plain is null ? stored : Encoding.UTF8.GetString(plain);
        }
        catch (FormatException)
        {
            // Not base64 — treat as a plain-text value (e.g. a legacy config).
            return stored;
        }
    }

    private static byte[]? Transform(byte[] data, bool protect)
    {
        var input = new DATA_BLOB();
        var output = new DATA_BLOB();
        IntPtr inputPtr = Marshal.AllocHGlobal(data.Length);
        try
        {
            Marshal.Copy(data, 0, inputPtr, data.Length);
            input.cbData = data.Length;
            input.pbData = inputPtr;

            bool ok = protect
                ? CryptProtectData(ref input, "CursorBubble", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, ref output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, ref output);

            if (!ok || output.pbData == IntPtr.Zero)
                return null;

            var result = new byte[output.cbData];
            Marshal.Copy(output.pbData, result, 0, output.cbData);
            return result;
        }
        catch
        {
            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(inputPtr);
            if (output.pbData != IntPtr.Zero)
                LocalFree(output.pbData);
        }
    }
}
