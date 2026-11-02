using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Desktop.Rpc;

/// <summary>
/// The images of issues and pull requests the Issues tab showed: an attachment does not change, so it is kept half an
/// hour, and one that could not be read a minute, so that a broken image is not asked again on every render.
/// </summary>
/// <remarks>Owned by one <see cref="IssuesService"/>; the oldest images leave first past <see cref="MaximumBytes"/>.</remarks>
internal sealed class IssueImageCache
{
    /// <summary>The most bytes of images kept.</summary>
    public const long MaximumBytes = 64L * 1024 * 1024;

    private static readonly TimeSpan Kept = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan Missing = TimeSpan.FromMinutes(1);
    private readonly Lock _gate = new();
    private readonly Dictionary<string, (DateTimeOffset Until, TrackedImage? Image)> _entries = new(StringComparer.Ordinal);
    private readonly TimeProvider _time;
    private long _bytes;

    /// <summary>Creates an empty cache.</summary>
    /// <param name="time">The clock.</param>
    public IssueImageCache(TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);
        _time = time;
    }

    /// <summary>Gets what is kept for a key: an image, or null for one that could not be read; false when nothing is.</summary>
    public bool TryGet(string key, out TrackedImage? image)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var entry))
            {
                if (entry.Until > _time.GetUtcNow())
                {
                    image = entry.Image;
                    return true;
                }

                Remove(key);
            }
        }

        image = null;
        return false;
    }

    /// <summary>Keeps an image, or that it could not be read (null); the oldest images leave when the cache is full.</summary>
    public void Set(string key, TrackedImage? image)
    {
        if (image is not null && image.Bytes.Length > MaximumBytes) return;
        lock (_gate)
        {
            Remove(key);
            _entries[key] = (_time.GetUtcNow() + (image is null ? Missing : Kept), image);
            _bytes += image?.Bytes.Length ?? 0;
            while (_bytes > MaximumBytes) Remove(_entries.MinBy(static pair => pair.Value.Until).Key);
        }
    }

    private void Remove(string key)
    {
        if (_entries.Remove(key, out var removed)) _bytes -= removed.Image?.Bytes.Length ?? 0;
    }
}
