using System.Runtime.InteropServices;

namespace ClickZen.App.Services;

/// <summary>Minimal DPAPI (CryptProtectData / CryptUnprotectData, current-user scope) without an extra package.</summary>
internal static class Dpapi
{
    private const int CryptProtectUiForbidden = 0x1;

    public static byte[] Protect(byte[] data, byte[] entropy) => Transform(data, entropy, protect: true);

    public static byte[] Unprotect(byte[] data, byte[] entropy) => Transform(data, entropy, protect: false);

    private static byte[] Transform(byte[] data, byte[] entropy, bool protect)
    {
        var hData = GCHandle.Alloc(data, GCHandleType.Pinned);
        var hEntropy = GCHandle.Alloc(entropy, GCHandleType.Pinned);
        try
        {
            var input = new DataBlob { Size = data.Length, Data = hData.AddrOfPinnedObject() };
            var extra = new DataBlob { Size = entropy.Length, Data = hEntropy.AddrOfPinnedObject() };
            var output = default(DataBlob);
            var ok = protect
                ? CryptProtectData(ref input, null, ref extra, 0, 0, CryptProtectUiForbidden, ref output)
                : CryptUnprotectData(ref input, 0, ref extra, 0, 0, CryptProtectUiForbidden, ref output);
            if (!ok)
            {
                throw new System.Security.Cryptography.CryptographicException(Marshal.GetLastWin32Error());
            }

            try
            {
                var result = new byte[output.Size];
                Marshal.Copy(output.Data, result, 0, output.Size);
                return result;
            }
            finally
            {
                LocalFree(output.Data);
            }
        }
        finally
        {
            hData.Free();
            hEntropy.Free();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public nint Data;
    }

    [DllImport("crypt32.dll", EntryPoint = "CryptProtectData", SetLastError = true, CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DataBlob dataIn, string? description, ref DataBlob entropy, nint reserved, nint prompt, int flags, ref DataBlob dataOut);

    [DllImport("crypt32.dll", EntryPoint = "CryptUnprotectData", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DataBlob dataIn, nint description, ref DataBlob entropy, nint reserved, nint prompt, int flags, ref DataBlob dataOut);

    [DllImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern nint LocalFree(nint mem);
}
