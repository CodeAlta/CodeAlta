using CodeAlta.Plugins.Abstractions;
using XenoAtom.Terminal.UI;

namespace CodeAlta.Plugins.Tui;

/// <summary>Creates an optional terminal visual for a derived session event or detail section.</summary>
/// <param name="context">The context supplied when the frontend invokes the deferred factory.</param>
/// <returns>The visual to render.</returns>
/// <remarks>The frontend borrows this callback. Exceptions propagate through its existing rendering policy; they do not request portable fallback.</remarks>
public delegate Visual PluginSessionEventVisualFactory(PluginSessionEventVisualContext context);

/// <summary>Provides deferred terminal rendering context; this is not an RPC or serialization contract.</summary>
public sealed record PluginSessionEventVisualContext
{
    /// <summary>Gets the plugin-stable derived event identifier.</summary>
    public required string EventId { get; init; }

    /// <summary>Gets the optional renderer target/schema name.</summary>
    public string? RenderTarget { get; init; }

    /// <summary>Gets event Markdown for a card, or section Markdown for detail/header rendering.</summary>
    public string? Markdown { get; init; }

    /// <summary>Gets the original in-process opaque event payload; no wire schema or copying is implied.</summary>
    public object? Payload { get; init; }

    /// <summary>Gets the detail section header, or null for card rendering.</summary>
    public string? DetailHeader { get; init; }
}

/// <summary>Optional terminal presentation of a portable derived event.</summary>
/// <remarks>Inherited Markdown remains the clipboard and fallback representation. Other heads can consume the neutral base without invoking this factory.</remarks>
public sealed record PluginTerminalDerivedSessionEvent : PluginDerivedSessionEvent
{
    /// <summary>Gets the borrowed deferred card factory, used when the dynamic content supplies no native factory on initial application.</summary>
    public PluginSessionEventVisualFactory? VisualFactory { get; init; }
}

/// <summary>Optional terminal presentation of a portable derived-event detail section.</summary>
public sealed record PluginTerminalDerivedSessionEventDetailSection : PluginDerivedSessionEventDetailSection
{
    /// <summary>Gets the deferred detail-content factory. Inherited Markdown remains the clipboard and fallback representation.</summary>
    public PluginSessionEventVisualFactory? VisualFactory { get; init; }

    /// <summary>Gets the deferred collapsible-header factory. Inherited Header remains the fallback text.</summary>
    public PluginSessionEventVisualFactory? HeaderVisualFactory { get; init; }
}

/// <summary>Optional native rendering for mutable portable derived-event content.</summary>
/// <remarks>
/// Notification threading and subscription ownership remain those of the neutral base and its consuming frontend.
/// Initial TUI application selects this factory before the event's static factory. Dynamic refresh uses this factory
/// alone: returning null clears the prior native factory, without restoring the event's static fallback.
/// This contract adds no synchronization, disposal ownership or background-work lifetime guarantee.
/// </remarks>
public abstract class PluginTerminalDynamicDerivedSessionEventContent : PluginDynamicDerivedSessionEventContent
{
    /// <summary>Gets the borrowed deferred native factory, or null. Markdown remains the clipboard and fallback representation.</summary>
    public virtual PluginSessionEventVisualFactory? VisualFactory => null;
}
