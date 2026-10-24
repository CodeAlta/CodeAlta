using System.Collections.Frozen;
using System.Security.Cryptography;
using CodeAlta.Plugins.Abstractions;
using NeoAstra;

namespace CodeAlta.Desktop;

/// <summary>
/// The files of the window's page that plugins serve to a browser of their own, such as the page of the phones: those the
/// manifest of the assets lists, read from the folder of the application and checked against the manifest, as the window
/// reads them. A path the manifest does not list is no file, whatever the folder holds.
/// </summary>
internal sealed class DesktopPluginAppFiles : IPluginAppFileService
{
    private readonly string _root;
    private readonly FrozenDictionary<string, NeoAssetEntry> _assets;

    /// <summary>Creates the files of a page.</summary>
    /// <param name="root">The folder of the page's files.</param>
    /// <param name="manifest">The manifest the build wrote for them.</param>
    /// <exception cref="ArgumentException"><paramref name="root"/> is empty.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="manifest"/> is null.</exception>
    internal DesktopPluginAppFiles(string root, NeoAssetManifest manifest)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(manifest);
        _root = Path.GetFullPath(root);
        _assets = manifest.Assets.ToFrozenDictionary(static asset => asset.Path, StringComparer.Ordinal);
    }

    /// <inheritdoc />
    public bool HasFiles => true;

    /// <inheritdoc />
    public async ValueTask<PluginAppFile?> ReadAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!_assets.TryGetValue(path, out var asset)) return null;
        byte[] content;
        try
        {
            content = await File.ReadAllBytesAsync(Path.Combine(_root, asset.Path.Replace('/', Path.DirectorySeparatorChar)), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        // A file that is not the one the build made is not given, as the window does not show it.
        if (content.LongLength != asset.Length || !Convert.ToHexString(SHA256.HashData(content)).Equals(asset.Sha256, StringComparison.OrdinalIgnoreCase)) return null;
        return new PluginAppFile(asset.Path, content, asset.ContentType, asset.CacheControl.Contains("immutable", StringComparison.Ordinal));
    }
}
