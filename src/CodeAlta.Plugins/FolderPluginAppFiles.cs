using System.Collections.Frozen;
using System.Security.Cryptography;
using System.Text.Json;
using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Plugins;

/// <summary>
/// The files of a page that a host without a window gives its plugins (<see cref="IPluginServices.AppFiles"/>): those a
/// list of the build names (<c>files.json</c>: path, bytes, SHA-256), read from the folder that holds the list and checked
/// against it. A path the list does not name is no file, whatever the folder holds, and a file that is not the one the
/// build made is not given.
/// </summary>
public sealed class FolderPluginAppFiles : IPluginAppFileService
{
    /// <summary>The name of the list of the files, in their folder.</summary>
    public const string ListFileName = "files.json";

    private readonly string _root;
    private readonly FrozenDictionary<string, Entry> _files;

    private FolderPluginAppFiles(string root, FrozenDictionary<string, Entry> files)
    {
        _root = root;
        _files = files;
    }

    /// <summary>Reads the list of a folder.</summary>
    /// <param name="root">The folder that holds the files and their list.</param>
    /// <returns>The files of the folder, or null when it has no list that reads.</returns>
    /// <exception cref="ArgumentException"><paramref name="root"/> is empty.</exception>
    public static FolderPluginAppFiles? TryOpen(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        root = Path.GetFullPath(root);
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, ListFileName)));
            if (!document.RootElement.TryGetProperty("files", out var list) || list.ValueKind != JsonValueKind.Array) return null;
            var files = new Dictionary<string, Entry>(StringComparer.Ordinal);
            foreach (var item in list.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object
                    || !item.TryGetProperty("path", out var path) || path.GetString() is not { Length: > 0 } name || !IsRelative(name)
                    || !item.TryGetProperty("bytes", out var bytes) || !bytes.TryGetInt64(out var length)
                    || !item.TryGetProperty("sha256", out var hash) || hash.GetString() is not { Length: 64 } sha256)
                    return null;
                files[name] = new Entry(length, sha256);
            }

            return files.Count == 0 ? null : new FolderPluginAppFiles(root, files.ToFrozenDictionary(StringComparer.Ordinal));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public bool HasFiles => true;

    /// <inheritdoc />
    public async ValueTask<PluginAppFile?> ReadAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!_files.TryGetValue(path, out var entry)) return null;
        byte[] content;
        try
        {
            content = await File.ReadAllBytesAsync(Path.Combine(_root, path.Replace('/', Path.DirectorySeparatorChar)), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        if (content.LongLength != entry.Length || !Convert.ToHexString(SHA256.HashData(content)).Equals(entry.Sha256, StringComparison.OrdinalIgnoreCase)) return null;
        return new PluginAppFile(path, content, ContentType(path), IsHashed(path));
    }

    // A path of the list stays in its folder: segments with names, no drive, no parent.
    private static bool IsRelative(string path)
        => !path.Contains('\\') && !path.Contains(':') && path.Split('/') is var parts && parts.All(static part => part.Length > 0 && part != "." && part != "..");

    // A chunk the build names by the hash of its content never changes: assets/mobile-CK1DWdvA.js.
    private static bool IsHashed(string path)
    {
        if (!path.StartsWith("assets/", StringComparison.Ordinal)) return false;
        var name = Path.GetFileNameWithoutExtension(path);
        var dash = name.LastIndexOf('-');
        return dash > 0 && name.Length - dash - 1 >= 8;
    }

    private static string ContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".html" => "text/html; charset=utf-8",
        ".js" or ".mjs" => "text/javascript; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".json" => "application/json; charset=utf-8",
        ".webmanifest" => "application/manifest+json; charset=utf-8",
        ".svg" => "image/svg+xml",
        ".png" => "image/png",
        ".webp" => "image/webp",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".ico" => "image/x-icon",
        ".woff2" => "font/woff2",
        ".woff" => "font/woff",
        ".ttf" => "font/ttf",
        ".wasm" => "application/wasm",
        ".txt" => "text/plain; charset=utf-8",
        _ => "application/octet-stream",
    };

    private readonly record struct Entry(long Length, string Sha256);
}
