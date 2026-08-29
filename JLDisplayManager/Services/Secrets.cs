using System;
using System.Runtime.InteropServices;
using System.Text;

namespace JLDisplayManager.Services;

/// <summary>
/// Per-user encryption for the one class of thing in this app that should not
/// sit on disk in the clear: API keys.
///
/// This is DPAPI, called straight through crypt32 rather than through
/// System.Security.Cryptography.ProtectedData. The package would work, but it
/// would put a NuGet assembly into a release drop that is otherwise three
/// binaries and nothing else — a poor trade for two P/Invokes.
///
/// The scope is the current user, so a copied settings file is useless on
/// another machine or under another account. That is the whole guarantee: it
/// stops a key travelling, not a determined attacker already running as you.
/// </summary>
public static class Secrets
{
    /// <summary>Never let DPAPI put UI on screen — this runs on background threads.</summary>
    private const int CryptprotectUiForbidden = 0x1;

    /// <summary>
    /// Encrypts a string for this user. Returns base64, or empty for empty
    /// input, so "no key set" and "a key that encrypts to nothing" cannot be
    /// confused.
    /// </summary>
    public static string Protect(string? plaintext)
    {
        if (string.IsNullOrEmpty(plaintext)) return "";

        try
        {
            byte[] bytes = Encoding.UTF8.GetBytes(plaintext);
            return Convert.ToBase64String(Transform(bytes, protect: true));
        }
        catch (Exception ex)
        {
            Models.Storage.Log($"could not protect a secret: {ex.Message}");
            return "";
        }
    }

    /// <summary>
    /// Decrypts what <see cref="Protect"/> wrote. Returns empty on anything
    /// unexpected — a blob from another user, a truncated file, a hand-edited
    /// value — because the only sane response is to behave as if no key is set.
    /// </summary>
    public static string Unprotect(string? protectedBase64)
    {
        if (string.IsNullOrEmpty(protectedBase64)) return "";

        try
        {
            byte[] bytes = Convert.FromBase64String(protectedBase64);
            return Encoding.UTF8.GetString(Transform(bytes, protect: false));
        }
        catch (Exception ex)
        {
            Models.Storage.Log($"could not unprotect a secret: {ex.Message}");
            return "";
        }
    }

    /// <summary>Masks a key for display: never show one back in full.</summary>
    public static string Mask(string? key) =>
        string.IsNullOrEmpty(key) ? "" : new string('\u2022', Math.Min(key.Length, 24));

    private static byte[] Transform(byte[] input, bool protect)
    {
        var inBlob = new DataBlob();
        var outBlob = new DataBlob();

        try
        {
            // Marshal.AllocHGlobal rather than a pinned array: the blob must
            // stay put across the call, and this keeps both sides symmetric.
            inBlob.cbData = input.Length;
            inBlob.pbData = Marshal.AllocHGlobal(input.Length);
            Marshal.Copy(input, 0, inBlob.pbData, input.Length);

            bool ok = protect
                ? CryptProtectData(ref inBlob, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                                   CryptprotectUiForbidden, out outBlob)
                : CryptUnprotectData(ref inBlob, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                                     CryptprotectUiForbidden, out outBlob);

            if (!ok) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());

            var result = new byte[outBlob.cbData];
            Marshal.Copy(outBlob.pbData, result, 0, outBlob.cbData);
            return result;
        }
        finally
        {
            if (inBlob.pbData != IntPtr.Zero) Marshal.FreeHGlobal(inBlob.pbData);

            // The output blob is crypt32's, allocated with LocalAlloc; freeing
            // it any other way corrupts the heap.
            if (outBlob.pbData != IntPtr.Zero) LocalFree(outBlob.pbData);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DataBlob pDataIn, string? szDataDescr, IntPtr pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, out DataBlob pDataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DataBlob pDataIn, IntPtr ppszDataDescr, IntPtr pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, out DataBlob pDataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);
}
