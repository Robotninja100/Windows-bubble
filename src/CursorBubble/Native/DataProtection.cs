using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace CursorBubble.Native;

/// <summary>
/// Encrypts small secrets (the Anthropic API key) with the Windows Data
/// Protection API (DPAPI), scoped to the current user. The ciphertext is stored
/// as base64 in config.json and can only be decrypted by the same Windows user
/// on the same machine.
///
/// Both directions fail loudly. An earlier version fell back to base64 of the
/// plaintext when DPAPI refused, which stored the key unprotected in a file the
/// README and the settings screen both describe as encrypted — and then handed
/// that base64 text back as if it were the key, so the API rejected it with a
/// misleading "invalid key". A secret store that cannot store a secret has to
/// say so.
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
    /// <exception cref="CryptographicException">
    /// DPAPI refused to encrypt. Deliberately not swallowed: the caller is
    /// storing a secret and must not be told that succeeded when it did not.
    /// </exception>
    public static string Protect(string? plain)
    {
        if (string.IsNullOrEmpty(plain))
            return "";

        byte[] input = Encoding.UTF8.GetBytes(plain);
        byte[]? cipher = Transform(input, protect: true, out int error);
        if (cipher is null)
        {
            throw new CryptographicException(
                $"Windows kon de sleutel niet versleutelen (DPAPI-fout {error}). " +
                "De sleutel is niet opgeslagen — hij onversleuteld wegschrijven zou " +
                "de belofte van dit veld breken.");
        }

        return Convert.ToBase64String(cipher);
    }

    /// <summary>
    /// Decrypt a value produced by <see cref="Protect"/>.
    ///
    /// Returns false — with <paramref name="plain"/> empty — when the stored
    /// value is DPAPI ciphertext that this user on this machine cannot decrypt,
    /// which is what happens to a config copied from another pc or another
    /// Windows account. Callers should treat that as "no key configured"
    /// rather than pass the undecryptable text on as if it were the key.
    /// </summary>
    public static bool TryUnprotect(string? stored, out string plain)
    {
        plain = "";
        if (string.IsNullOrEmpty(stored))
            return true;

        byte[] blob;
        try
        {
            blob = Convert.FromBase64String(stored);
        }
        catch (FormatException)
        {
            // Not base64, so it was never DPAPI ciphertext — a plain-text value
            // from a config written before the key was encrypted. Hand it back
            // unchanged so upgrading does not lose the user's key.
            plain = stored;
            return true;
        }

        byte[]? decrypted = Transform(blob, protect: false, out _);
        if (decrypted is null)
            return false;

        plain = Encoding.UTF8.GetString(decrypted);
        return true;
    }

    /// <param name="error">
    /// The Win32 error from the failing call, captured before the cleanup in
    /// <c>finally</c> can overwrite it. Zero on success.
    /// </param>
    private static byte[]? Transform(byte[] data, bool protect, out int error)
    {
        error = 0;

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
            {
                error = Marshal.GetLastWin32Error();
                return null;
            }

            var result = new byte[output.cbData];
            Marshal.Copy(output.pbData, result, 0, output.cbData);
            return result;
        }
        catch (Exception ex)
        {
            error = ex.HResult;
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
