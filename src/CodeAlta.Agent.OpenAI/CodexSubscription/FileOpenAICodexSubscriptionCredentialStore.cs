using System.Text;
using System.Text.Json;
using System.Runtime.InteropServices;

namespace CodeAlta.Agent.OpenAI.CodexSubscription;

internal sealed class FileOpenAICodexSubscriptionCredentialStore : IOpenAICodexSubscriptionCredentialStore
{
    private readonly string _rootPath;

    public FileOpenAICodexSubscriptionCredentialStore(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        _rootPath = rootPath;
    }

    public async ValueTask<OpenAICodexSubscriptionCredential?> LoadAsync(
        string providerKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerKey);
        var path = GetCredentialPath(providerKey);
        if (!File.Exists(path))
        {
            return null;
        }

        var protectedBytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        var jsonBytes = Unprotect(protectedBytes);
        return JsonSerializer.Deserialize(
            jsonBytes,
            OpenAICodexSubscriptionJsonSerializerContext.Default.OpenAICodexSubscriptionCredential);
    }

    public async ValueTask SaveAsync(
        string providerKey,
        OpenAICodexSubscriptionCredential credential,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerKey);
        ArgumentNullException.ThrowIfNull(credential);

        var path = GetCredentialPath(providerKey);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(
            credential,
            OpenAICodexSubscriptionJsonSerializerContext.Default.OpenAICodexSubscriptionCredential);
        var protectedBytes = Protect(jsonBytes);
        var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        await File.WriteAllBytesAsync(temporaryPath, protectedBytes, cancellationToken).ConfigureAwait(false);
        TryRestrictFilePermissions(temporaryPath);
        File.Move(temporaryPath, path, overwrite: true);
        TryRestrictFilePermissions(path);
    }

    public ValueTask DeleteAsync(
        string providerKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerKey);
        cancellationToken.ThrowIfCancellationRequested();
        var path = GetCredentialPath(providerKey);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return ValueTask.CompletedTask;
    }

    private string GetCredentialPath(string providerKey)
    {
        var safeProviderKey = string.Concat(providerKey.Trim().Select(static ch =>
            char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.' ? ch : '_'));
        return Path.Combine(_rootPath, "auth", "openai-codex-subscription", safeProviderKey + ".credential");
    }

    private static byte[] Protect(byte[] bytes)
    {
        if (OperatingSystem.IsWindows())
        {
            return Encoding.UTF8.GetBytes("dpapi:" + Convert.ToBase64String(WindowsDataProtection.Protect(bytes)));
        }

        return Encoding.UTF8.GetBytes("plain64:" + Convert.ToBase64String(bytes));
    }

    private static byte[] Unprotect(byte[] bytes)
    {
        var text = Encoding.UTF8.GetString(bytes);
        if (text.StartsWith("dpapi:", StringComparison.Ordinal))
        {
            if (!OperatingSystem.IsWindows())
            {
                throw new InvalidOperationException("Codex subscription credentials are DPAPI-protected and can only be read by the same Windows user profile.");
            }

            return WindowsDataProtection.Unprotect(Convert.FromBase64String(text["dpapi:".Length..]));
        }

        if (text.StartsWith("plain64:", StringComparison.Ordinal))
        {
            return Convert.FromBase64String(text["plain64:".Length..]);
        }

        return Convert.FromBase64String(text);
    }

    private static void TryRestrictFilePermissions(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
        catch (PlatformNotSupportedException)
        {
        }
    }

    private static class WindowsDataProtection
    {
        private const int CryptProtectUiForbidden = 0x1;

        public static byte[] Protect(byte[] data)
            => Transform(data, protect: true);

        public static byte[] Unprotect(byte[] data)
            => Transform(data, protect: false);

        private static byte[] Transform(byte[] data, bool protect)
        {
            ArgumentNullException.ThrowIfNull(data);

            var input = default(DataBlob);
            var output = default(DataBlob);
            try
            {
                input.cbData = data.Length;
                input.pbData = Marshal.AllocHGlobal(data.Length);
                Marshal.Copy(data, 0, input.pbData, data.Length);

                var success = protect
                    ? CryptProtectData(ref input, "CodeAlta Codex subscription credential", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, ref output)
                    : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, ref output);
                if (!success)
                {
                    throw new InvalidOperationException("Windows DPAPI failed for Codex subscription credential storage.");
                }

                var result = new byte[output.cbData];
                Marshal.Copy(output.pbData, result, 0, output.cbData);
                return result;
            }
            finally
            {
                if (input.pbData != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(input.pbData);
                }

                if (output.pbData != IntPtr.Zero)
                {
                    _ = LocalFree(output.pbData);
                }
            }
        }

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CryptProtectData(
            ref DataBlob pDataIn,
            string? szDataDescr,
            IntPtr pOptionalEntropy,
            IntPtr pvReserved,
            IntPtr pPromptStruct,
            int dwFlags,
            ref DataBlob pDataOut);

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CryptUnprotectData(
            ref DataBlob pDataIn,
            IntPtr ppszDataDescr,
            IntPtr pOptionalEntropy,
            IntPtr pvReserved,
            IntPtr pPromptStruct,
            int dwFlags,
            ref DataBlob pDataOut);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr hMem);

        [StructLayout(LayoutKind.Sequential)]
        private struct DataBlob
        {
            public int cbData;
            public IntPtr pbData;
        }
    }
}
