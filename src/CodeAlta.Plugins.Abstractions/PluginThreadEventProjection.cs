using CodeAlta.Agent;
using XenoAtom.Terminal.UI;

namespace CodeAlta.Plugins.Abstractions;

/// <summary>
/// Projects canonical agent events into plugin-owned transient thread events.
/// </summary>
/// <param name="context">Projection context.</param>
/// <param name="cancellationToken">Cancellation token.</param>
/// <returns>Derived transient events to upsert or remove.</returns>
public delegate ValueTask<IReadOnlyList<PluginDerivedThreadEvent>> PluginThreadEventProjectionHandler(
    PluginThreadEventProjectionContext context,
    CancellationToken cancellationToken);

/// <summary>
/// Creates a host-rendered visual for a plugin-derived thread event or detail section.
/// </summary>
/// <param name="context">The visual rendering context.</param>
/// <returns>The visual to render.</returns>
public delegate Visual PluginThreadEventVisualFactory(PluginThreadEventVisualContext context);

/// <summary>
/// Describes a plugin contribution that can project replayed and live canonical thread events into transient events.
/// </summary>
public sealed record PluginThreadEventProjectionContribution
{
    /// <summary>Gets the contribution name.</summary>
    public required string Name { get; init; }

    /// <summary>Gets the projection handler.</summary>
    public required PluginThreadEventProjectionHandler ProjectAsync { get; init; }
}

/// <summary>
/// Provides canonical thread events to a plugin-owned transient event projection.
/// </summary>
public sealed record PluginThreadEventProjectionContext
{
    /// <summary>Gets the owning contribution handle.</summary>
    public required PluginContributionHandle Handle { get; init; }

    /// <summary>Gets the durable thread identifier.</summary>
    public required string ThreadId { get; init; }

    /// <summary>Gets the project identifier, when known.</summary>
    public string? ProjectId { get; init; }

    /// <summary>Gets the project path, when known.</summary>
    public string? ProjectPath { get; init; }

    /// <summary>Gets the backend identifier, when known.</summary>
    public string? BackendId { get; init; }

    /// <summary>Gets the active model identifier, when known.</summary>
    public string? Model { get; init; }

    /// <summary>Gets the primary session identifier represented by the event batch, when known.</summary>
    public string? SessionId { get; init; }

    /// <summary>Gets the primary run identifier represented by the event batch, when known.</summary>
    public string? RunId { get; init; }

    /// <summary>Gets the canonical events being projected.</summary>
    public required IReadOnlyList<AgentEvent> Events { get; init; }

    /// <summary>Gets a value indicating whether the events came from history replay instead of the live event stream.</summary>
    public bool IsReplay { get; init; }

    /// <summary>Gets a value indicating whether the batch is complete enough to emit final turn projections.</summary>
    public bool IsCompleteBatch { get; init; }
}

/// <summary>
/// Provides host context to a plugin visual factory.
/// </summary>
public sealed record PluginThreadEventVisualContext
{
    /// <summary>Gets the plugin-stable derived event identifier.</summary>
    public required string EventId { get; init; }

    /// <summary>Gets the optional renderer target/schema name.</summary>
    public string? RenderTarget { get; init; }

    /// <summary>Gets the current fallback Markdown for the visual being rendered.</summary>
    public string? Markdown { get; init; }

    /// <summary>Gets the optional structured payload.</summary>
    public object? Payload { get; init; }

    /// <summary>Gets the detail section header when rendering a detail section.</summary>
    public string? DetailHeader { get; init; }
}

/// <summary>
/// Describes a plugin-owned transient thread event projection result.
/// </summary>
public sealed record PluginDerivedThreadEvent
{
    /// <summary>Gets the plugin-stable derived event identifier.</summary>
    public required string EventId { get; init; }

    /// <summary>Gets markdown text for default frontend rendering, when available.</summary>
    public string? Markdown { get; init; }

    /// <summary>Gets the timestamp to show for the transient event, when available.</summary>
    public DateTimeOffset? Timestamp { get; init; }

    /// <summary>Gets an optional renderer target/schema name.</summary>
    public string? RenderTarget { get; init; }

    /// <summary>Gets an optional structured payload.</summary>
    public object? Payload { get; init; }

    /// <summary>Gets optional Markdown detail sections that the frontend may render collapsed by default.</summary>
    public IReadOnlyList<PluginDerivedThreadEventDetailSection> DetailSections { get; init; } = [];

    /// <summary>
    /// Gets an optional visual factory for advanced frontend rendering that replaces the default Markdown card content. <see cref="Markdown"/> remains the clipboard and fallback representation.
    /// </summary>
    public PluginThreadEventVisualFactory? VisualFactory { get; init; }

    /// <summary>
    /// Gets optional dynamic Markdown content for projections that complete asynchronously after the event is first rendered.
    /// </summary>
    public PluginDynamicDerivedThreadEventContent? DynamicContent { get; init; }

    /// <summary>Gets a value indicating whether an existing transient event should be removed.</summary>
    public bool Remove { get; init; }
}

/// <summary>
/// Provides mutable Markdown content for a plugin-derived transient thread event.
/// </summary>
/// <remarks>
/// Implementations may update <see cref="Markdown"/> and <see cref="DetailSections"/> from background work and call
/// <see cref="NotifyChanged"/> so hosts can refresh the existing transient card without replaying canonical history.
/// </remarks>
public abstract class PluginDynamicDerivedThreadEventContent
{
    /// <summary>Raised when the dynamic Markdown content has changed.</summary>
    public event EventHandler? Changed;

    /// <summary>Gets the current Markdown text.</summary>
    public abstract string Markdown { get; }

    /// <summary>Gets the current detail sections.</summary>
    public virtual IReadOnlyList<PluginDerivedThreadEventDetailSection> DetailSections => [];

    /// <summary>
    /// Gets an optional visual factory for advanced frontend rendering that replaces the default Markdown card content. <see cref="Markdown"/> remains the clipboard and fallback representation.
    /// </summary>
    public virtual PluginThreadEventVisualFactory? VisualFactory => null;

    /// <summary>Raises the <see cref="Changed"/> event.</summary>
    protected void NotifyChanged() => Changed?.Invoke(this, EventArgs.Empty);
}

/// <summary>
/// Describes a plugin-derived Markdown detail section for a transient thread event.
/// </summary>
public sealed record PluginDerivedThreadEventDetailSection
{
    /// <summary>Gets the section header.</summary>
    public required string Header { get; init; }

    /// <summary>Gets the section Markdown.</summary>
    public required string Markdown { get; init; }

    /// <summary>
    /// Gets an optional visual factory for advanced frontend rendering. <see cref="Markdown"/> remains the clipboard and fallback representation.
    /// </summary>
    public PluginThreadEventVisualFactory? VisualFactory { get; init; }

    /// <summary>
    /// Gets an optional visual factory for rendering the collapsible detail header. <see cref="Header"/> remains the fallback header text.
    /// </summary>
    public PluginThreadEventVisualFactory? HeaderVisualFactory { get; init; }
}
