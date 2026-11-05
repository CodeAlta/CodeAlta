using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace CodeAlta.Catalog.Secrets;

/// <summary>
/// Protects values with the Data Protection API of Windows for the current user: only this user, on this machine, gives
/// them back.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiSecretProtector : ISecretProtector
{
    // Ties the protected values to CodeAlta: a program of the same user calling DPAPI without it cannot read them back.
    private static readonly byte[] Entropy = "CodeAlta.Secrets.v1"u8.ToArray();

    private const int CryptProtectUiForbidden = 0x1;

    /// <inheritdoc />
    public string Kind => SecretProtectionKinds.Dpapi;

    /// <inheritdoc />
    public byte[] Protect(ReadOnlySpan<byte> value) => Call(value, protect: true);

    /// <inheritdoc />
    public byte[] Unprotect(ReadOnlySpan<byte> protectedValue) => Call(protectedValue, protect: false);

    private static byte[] Call(ReadOnlySpan<byte> input, bool protect)
    {
        var buffer = input.ToArray();
        var inputHandle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        var entropyHandle = GCHandle.Alloc(Entropy, GCHandleType.Pinned);
        try
        {
            var data = new DataBlob { Size = input.Length, Data = inputHandle.AddrOfPinnedObject() };
            var entropy = new DataBlob { Size = Entropy.Length, Data = entropyHandle.AddrOfPinnedObject() };
            var output = default(DataBlob);
            var done = protect
                ? CryptProtectData(ref data, 0, ref entropy, 0, 0, CryptProtectUiForbidden, ref output)
                : CryptUnprotectData(ref data, 0, ref entropy, 0, 0, CryptProtectUiForbidden, ref output);
            if (!done)
            {
                var error = new Win32Exception(Marshal.GetLastWin32Error());
                throw new SecretStoreUnavailableException(protect
                    ? $"Windows could not protect the value: {error.Message}"
                    : $"Windows could not read a protected value back: {error.Message} It was protected by another user or on another machine.", error);
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
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(buffer);
            inputHandle.Free();
            entropyHandle.Free();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public nint Data;
    }

    [DllImport("crypt32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DataBlob dataIn, nint description, ref DataBlob entropy, nint reserved, nint prompt, int flags, ref DataBlob dataOut);

    [DllImport("crypt32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DataBlob dataIn, nint description, ref DataBlob entropy, nint reserved, nint prompt, int flags, ref DataBlob dataOut);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern nint LocalFree(nint memory);
}
