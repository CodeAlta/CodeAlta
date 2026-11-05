using System.Security.Cryptography;
using CodeAlta.Agent.Runtime;
using CodeAlta.Desktop.Rpc;
using NeoAstra;
using NeoAstra.Desktop.DragDrop;
using XenoAtom.Logging;

namespace CodeAlta.Desktop;

/// <summary>
/// The files and folders the user drops on a window from the file manager, where the host gets the drop and the page
/// does not. On Linux and Windows NeoAstra's native drop target takes every drop that comes from outside the application
/// to its broker, so the page never sees the HTML <c>drop</c> event of its composer; on macOS the page gets it as well, and
/// attaches it itself. The page of the window is told where the drop was, the folders by their path (as Add folder…
/// gives them), and the files by an id it reads their content with once, as from a file input: never their path.
/// </summary>
internal sealed class DesktopFileDrops
{
    /// <summary>The largest file a prompt takes, as the page counts it.</summary>
    internal const int MaximumFileBytes = 20 * 1024 * 1024;

    /// <summary>The number of dropped files that wait for the page at most: the oldest goes first.</summary>
    internal const int MaximumPendingFiles = 256;

    /// <summary>How long a dropped file waits for the page to read it.</summary>
    internal static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(2);

    private readonly Lock _gate = new();
    // The files that wait for the page, each with the number of its drop: the smallest is the oldest.
    private readonly Dictionary<string, (string Path, string Name, DateTimeOffset Expires, long Sequence)> _files = new(StringComparer.Ordinal);
    private long _sequence;
    private readonly TimeProvider _time;

    /// <summary>Creates the dropped files of the application.</summary>
    /// <param name="time">The clock of their lifetime; the system clock by default.</param>
    internal DesktopFileDrops(TimeProvider? time = null) => _time = time ?? TimeProvider.System;

    /// <summary>Whether the page of a view gets the files dropped on it by itself: on macOS only.</summary>
    internal static bool PageGetsDrops => OperatingSystem.IsMacOS();

    /// <summary>
    /// Takes the drops of NeoAstra's broker until the returned registration is disposed, and gives each one that
    /// carries files or folders to the page of the view it was dropped on.
    /// </summary>
    /// <param name="broker">NeoAstra's broker of the drops.</param>
    /// <param name="publish">Gives a drop to the page of a view, by the label of the view.</param>
    /// <param name="pageGetsDrops">The page gets the drops by itself (<see cref="PageGetsDrops"/>): nothing is given twice.</param>
    /// <param name="scaleOf">The pixels of the display for one unit of the point of a drop on a view, by its label: 1 where the point is in logical units.</param>
    /// <param name="zoom">The zoom of the pages.</param>
    /// <returns>The registration.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    internal IDisposable Follow(NeoDragDropBroker broker, Action<string, DesktopShellDrop> publish, bool pageGetsDrops, Func<string, double> scaleOf, Func<double> zoom)
    {
        ArgumentNullException.ThrowIfNull(broker);
        ArgumentNullException.ThrowIfNull(publish);
        ArgumentNullException.ThrowIfNull(scaleOf);
        ArgumentNullException.ThrowIfNull(zoom);
        EventHandler<NeoOwnedDropEvent> handler = (_, owned) =>
        {
            if (pageGetsDrops) return;
            try
            {
                // The tokens are resolved at once: the broker releases them when no page takes the drop.
                List<string> paths = [];
                foreach (var item in owned.Drop.Items)
                    if (item is { Kind: NeoDragDataKind.File, FileToken: { } token } && broker.TryResolveFile(token, owned.Owner, out var path) && path is not null) paths.Add(path);
                if (paths.Count == 0) return;
                var label = owned.Drop.ViewLabel;
                var (x, y) = PagePoint(owned.Drop.Position, scaleOf(label), zoom());
                if (Take((int)Math.Round(x), (int)Math.Round(y), paths) is { } drop) publish(label, drop);
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or ObjectDisposedException)
            {
                Warn($"A drop of files could not be taken: {exception.Message}");
            }
        };
        broker.Inbound += handler;
        return new Registration(() => broker.Inbound -= handler);
    }

    /// <summary>
    /// The point of a drop in CSS pixels of the page: the point NeoAstra gives, divided by the pixels of the display of
    /// one unit (on Windows, where the point is in pixels of the display) and by the zoom of the page.
    /// </summary>
    internal static (double X, double Y) PagePoint(NeoPoint position, double scale, double zoom)
    {
        var factor = (double.IsFinite(scale) && scale > 0 ? scale : 1) * (double.IsFinite(zoom) && zoom > 0 ? zoom : 1);
        return (position.X / factor, position.Y / factor);
    }

    /// <summary>
    /// Takes the paths of one drop: the existing folders by their path, the existing files under ids for the page.
    /// </summary>
    /// <returns>What the page is told, or null when the drop holds nothing to attach.</returns>
    internal DesktopShellDrop? Take(int x, int y, IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        List<string> folders = [];
        List<(string Path, string Name, long Size)> files = [];
        foreach (var path in paths)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) continue;
                if (Directory.Exists(path)) folders.Add(Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)));
                else if (new FileInfo(path) is { Exists: true } file) files.Add((file.FullName, file.Name, file.Length));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                // A path that cannot be read is not offered.
            }
        }
        if (folders.Count == 0 && files.Count == 0) return null;
        var now = _time.GetUtcNow();
        var dropped = new DesktopShellDroppedFile[files.Count];
        lock (_gate)
        {
            foreach (var expired in _files.Where(entry => entry.Value.Expires <= now).Select(static entry => entry.Key).ToArray()) _files.Remove(expired);
            for (var index = 0; index < files.Count; index++)
            {
                var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
                if (_files.Count >= MaximumPendingFiles) _files.Remove(_files.MinBy(static entry => entry.Value.Sequence).Key);
                _files[id] = (files[index].Path, files[index].Name, now + Lifetime, ++_sequence);
                dropped[index] = new(id, files[index].Name, (int)Math.Min(files[index].Size, int.MaxValue));
            }
        }
        return new(x, y, [.. folders], dropped);
    }

    /// <summary>
    /// Reads a dropped file for the page, once: <c>ok</c> with its content and its media type, <c>not_found</c> for an
    /// id that is not, or no longer, one of a drop, <c>too_large</c> for a file larger than an attachment, and
    /// <c>failed</c> for a file that cannot be read.
    /// </summary>
    internal async Task<DesktopShellDroppedFileResponse> ReadAsync(string? id, CancellationToken cancellationToken)
    {
        (string Path, string Name, DateTimeOffset Expires, long Sequence) file;
        lock (_gate)
        {
            if (id is null || !_files.Remove(id, out file)) return new("not_found", null, null, null);
            if (file.Expires <= _time.GetUtcNow()) return new("not_found", null, null, null);
        }
        try
        {
            await using var stream = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, useAsync: true);
            if (stream.Length > MaximumFileBytes) return new("too_large", file.Name, null, null);
            // The file may grow while it is read: one byte more than an attachment is enough to tell.
            var buffer = new byte[Math.Min(stream.Length, MaximumFileBytes) + 1];
            var read = 0;
            while (read < buffer.Length && await stream.ReadAsync(buffer.AsMemory(read), cancellationToken).ConfigureAwait(false) is var count and > 0) read += count;
            if (read > MaximumFileBytes) return new("too_large", file.Name, null, null);
            var content = buffer.AsSpan(0, read);
            return new("ok", file.Name, AgentAttachedFiles.FindMediaType(content, file.Name), Convert.ToBase64String(content));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Warn($"A dropped file could not be read: {exception.Message}");
            return new("failed", file.Name, null, null);
        }
    }

    private static void Warn(string message)
    {
        if (LogManager.IsInitialized) LogManager.GetLogger("CodeAlta.Desktop").Warn(message);
    }

    private sealed class Registration(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;

        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }
}
