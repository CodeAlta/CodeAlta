namespace CodeAlta.Plugins.Abstractions;

/// <summary>A file of the application's page, as a plugin serves it to a browser.</summary>
/// <param name="Path">The path of the file, relative to the root of the page, with <c>/</c> between folders.</param>
/// <param name="Content">The bytes of the file.</param>
/// <param name="ContentType">The media type of the file, such as <c>text/javascript; charset=utf-8</c>.</param>
/// <param name="Immutable">
/// Whether the file never changes under its path, such as a script whose name holds a hash of its content: a browser may keep
/// it as long as it wants. The others are read again each time.
/// </param>
public sealed record PluginAppFile(string Path, ReadOnlyMemory<byte> Content, string ContentType, bool Immutable);

/// <summary>
/// Gives a plugin the files of the application's page, the ones its build made: a built-in plugin whose page is written with
/// the frontend of the application serves them to a browser of its own, such as the page a phone opens. Only those files are
/// given, never another file of the computer.
/// </summary>
public interface IPluginAppFileService
{
    /// <summary>Gets whether the host has a page whose files it gives.</summary>
    bool HasFiles { get; }

    /// <summary>Reads a file of the page.</summary>
    /// <param name="path">The path of the file, relative to the root of the page, such as <c>mobile.html</c> or <c>assets/index-1a2b3c.js</c>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The file; <see langword="null"/> when the page has no file at that path, or when the file is not the one the build made.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
    ValueTask<PluginAppFile?> ReadAsync(string path, CancellationToken cancellationToken = default);
}

/// <summary>The file service of a host without a page: it has no file.</summary>
public sealed class NoopPluginAppFileService : IPluginAppFileService
{
    private NoopPluginAppFileService()
    {
    }

    /// <summary>Gets the shared instance.</summary>
    public static NoopPluginAppFileService Instance { get; } = new();

    /// <inheritdoc />
    public bool HasFiles => false;

    /// <inheritdoc />
    public ValueTask<PluginAppFile?> ReadAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        return ValueTask.FromResult<PluginAppFile?>(null);
    }
}
