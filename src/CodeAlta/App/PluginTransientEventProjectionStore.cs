using CodeAlta.Catalog;
using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.App;

internal sealed class PluginTransientEventProjectionStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, PluginTransientEventProjection> _events = new(StringComparer.Ordinal);

    public IReadOnlyList<PluginTransientEventProjection> Snapshot
    {
        get
        {
            lock (_gate)
            {
                return _events.Values.OrderBy(static projection => projection.EventId, StringComparer.Ordinal).ToArray();
            }
        }
    }

    public bool Apply(PluginDerivedSessionEvent derivedEvent)
    {
        ArgumentNullException.ThrowIfNull(derivedEvent);
        ArgumentException.ThrowIfNullOrWhiteSpace(derivedEvent.EventId);

        lock (_gate)
        {
            if (derivedEvent.Remove)
            {
                return _events.Remove(derivedEvent.EventId);
            }

            var projection = new PluginTransientEventProjection(
                derivedEvent.EventId,
                ResolveMarkdown(derivedEvent),
                derivedEvent.Timestamp,
                derivedEvent.RenderTarget,
                derivedEvent.Payload,
                ResolveDetailSections(derivedEvent),
                ResolveVisualFactory(derivedEvent),
                derivedEvent.DynamicContent);
            var changed = !_events.TryGetValue(derivedEvent.EventId, out var existing) || !Equals(existing, projection);
            _events[derivedEvent.EventId] = projection;
            return changed;
        }
    }

    public PluginTransientEventProjection? Get(string eventId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventId);

        lock (_gate)
        {
            return _events.GetValueOrDefault(eventId);
        }
    }

    public bool RefreshDynamic(string eventId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventId);

        lock (_gate)
        {
            if (!_events.TryGetValue(eventId, out var existing) || existing.DynamicContent is null)
            {
                return false;
            }

            var updated = existing with
            {
                Markdown = ResolveMarkdown(existing),
                DetailSections = existing.DynamicContent.DetailSections,
                VisualFactory = existing.DynamicContent.VisualFactory,
            };
            var changed = !Equals(existing, updated);
            _events[eventId] = updated;
            return changed;
        }
    }

    public bool ApplyRange(IEnumerable<PluginDerivedSessionEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        var changed = false;
        foreach (var derivedEvent in events)
        {
            changed |= Apply(derivedEvent);
        }

        return changed;
    }

    public void Clear()
    {
        lock (_gate)
        {
            _events.Clear();
        }
    }

    private static string BuildDefaultMarkdown(PluginDerivedSessionEvent derivedEvent)
        => string.IsNullOrWhiteSpace(derivedEvent.RenderTarget)
            ? SR.T("Plugin event `{0}`", derivedEvent.EventId)
            : SR.T("Plugin event `{0}` ({1})", derivedEvent.EventId, derivedEvent.RenderTarget);

    private static string ResolveMarkdown(PluginDerivedSessionEvent derivedEvent)
        => derivedEvent.DynamicContent is { } dynamicContent
            ? dynamicContent.Markdown
            : string.IsNullOrWhiteSpace(derivedEvent.Markdown)
                ? BuildDefaultMarkdown(derivedEvent)
                : derivedEvent.Markdown;

    private static string ResolveMarkdown(PluginTransientEventProjection projection)
        => projection.DynamicContent?.Markdown ?? projection.Markdown;

    private static IReadOnlyList<PluginDerivedSessionEventDetailSection> ResolveDetailSections(PluginDerivedSessionEvent derivedEvent)
        => derivedEvent.DynamicContent?.DetailSections ?? derivedEvent.DetailSections;

    private static PluginSessionEventVisualFactory? ResolveVisualFactory(PluginDerivedSessionEvent derivedEvent)
        => derivedEvent.DynamicContent?.VisualFactory ?? derivedEvent.VisualFactory;
}

internal sealed record PluginTransientEventProjection(
    string EventId,
    string Markdown,
    DateTimeOffset? Timestamp,
    string? RenderTarget,
    object? Payload,
    IReadOnlyList<PluginDerivedSessionEventDetailSection> DetailSections,
    PluginSessionEventVisualFactory? VisualFactory,
    PluginDynamicDerivedSessionEventContent? DynamicContent);

internal sealed class PluginDynamicProjectionSubscription : IDisposable
{
    private readonly EventHandler _handler;

    public PluginDynamicProjectionSubscription(PluginDynamicDerivedSessionEventContent content, EventHandler handler)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(handler);
        Content = content;
        _handler = handler;
        Content.Changed += _handler;
    }

    public PluginDynamicDerivedSessionEventContent Content { get; }

    public void Dispose() => Content.Changed -= _handler;
}
