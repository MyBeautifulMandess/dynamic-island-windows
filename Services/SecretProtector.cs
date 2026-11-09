using System.Runtime.InteropServices;
using System.Text;

namespace DynamicIsland.Services;

internal static class SecretProtector
{
    public static string Protect(string value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        return Crypt(value, true);
    }

    public static string Unprotect(string value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        try { return Crypt(value, false); }
        catch { return ""; }
    }

    private static string Crypt(string value, bool encrypt)
    {
        var input = encrypt ? Encoding.UTF8.GetBytes(value) : Convert.FromBase64String(value);
        var inBlob = new DataBlob { Length = input.Length, Data = Marshal.AllocHGlobal(input.Length) };
        Marshal.Copy(input, 0, inBlob.Data, input.Length);
        try
        {
            var success = encrypt
                ? CryptProtectData(ref inBlob, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0x01, out var outBlob)
                : CryptUnprotectData(ref inBlob, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0x01, out outBlob);
            if (!success)
                throw new InvalidOperationException("DPAPI operation failed");
            try
            {
                var output = new byte[outBlob.Length];
                Marshal.Copy(outBlob.Data, output, 0, output.Length);
                return encrypt ? Convert.ToBase64String(output) : Encoding.UTF8.GetString(output);
            }
            finally { if (outBlob.Data != IntPtr.Zero) LocalFree(outBlob.Data); }
        }
        finally { Marshal.FreeHGlobal(inBlob.Data); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Length;
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref DataBlob data, string? description, IntPtr optionalEntropy,
        IntPtr reserved, IntPtr promptStruct, int flags, out DataBlob protectedData);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CryptUnprotectData")]
    private static extern bool CryptUnprotectData(ref DataBlob data, IntPtr description, IntPtr optionalEntropy,
        IntPtr reserved, IntPtr promptStruct, int flags, out DataBlob protectedData);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
