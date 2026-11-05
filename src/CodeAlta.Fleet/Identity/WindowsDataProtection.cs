using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;

namespace CodeAlta.Fleet.Identity;

/// <summary>DPAPI for the current Windows user: what protects the device key at rest on Windows.</summary>
[SupportedOSPlatform("windows")]
internal static unsafe partial class WindowsDataProtection
{
    private const int CryptProtectUiForbidden = 0x1;

    // Ties the protected bytes to this use: another DPAPI caller of the same user cannot unprotect them by accident.
    private static ReadOnlySpan<byte> Entropy => "CodeAlta fleet device key v1"u8;

    /// <summary>Encrypts bytes for the current Windows user.</summary>
    /// <exception cref="CryptographicException">DPAPI failed.</exception>
    public static byte[] Protect(ReadOnlySpan<byte> data) => Transform(data, protect: true);

    /// <summary>Decrypts bytes protected for the current Windows user.</summary>
    /// <exception cref="CryptographicException">DPAPI failed: another user, another computer, or damaged bytes.</exception>
    public static byte[] Unprotect(ReadOnlySpan<byte> data) => Transform(data, protect: false);

    private static byte[] Transform(ReadOnlySpan<byte> data, bool protect)
    {
        var output = default(DataBlob);
        try
        {
            fixed (byte* dataPointer = data)
            fixed (byte* entropyPointer = Entropy)
            {
                var input = new DataBlob { Size = data.Length, Data = dataPointer };
                var entropy = new DataBlob { Size = Entropy.Length, Data = entropyPointer };
                var success = protect
                    ? CryptProtectData(&input, null, &entropy, 0, 0, CryptProtectUiForbidden, &output)
                    : CryptUnprotectData(&input, 0, &entropy, 0, 0, CryptProtectUiForbidden, &output);
                if (!success)
                {
                    throw new CryptographicException(Marshal.GetLastPInvokeError());
                }
            }

            var result = new ReadOnlySpan<byte>(output.Data, output.Size).ToArray();
            new Span<byte>(output.Data, output.Size).Clear();
            return result;
        }
        finally
        {
            if (output.Data is not null)
            {
                _ = LocalFree(output.Data);
            }
        }
    }

    [LibraryImport("crypt32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptProtectData(DataBlob* dataIn, string? description, DataBlob* optionalEntropy, nint reserved, nint promptStruct, int flags, DataBlob* dataOut);

    [LibraryImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptUnprotectData(DataBlob* dataIn, nint description, DataBlob* optionalEntropy, nint reserved, nint promptStruct, int flags, DataBlob* dataOut);

    [LibraryImport("kernel32.dll")]
    private static partial nint LocalFree(void* memory);

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public byte* Data;
    }
}
