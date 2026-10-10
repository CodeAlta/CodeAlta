using CodeAlta.Agent;

namespace CodeAlta.Plugins.Abstractions;

/// <summary>
/// Projects canonical agent events into plugin-owned transient session events.
/// </summary>
/// <param name="context">Projection context.</param>
/// <param name="cancellationToken">Cancellation token.</param>
/// <returns>Derived transient events to upsert or remove.</returns>
public delegate ValueTask<IReadOnlyList<PluginDerivedSessionEvent>> PluginSessionEventProjectionHandler(
    PluginSessionEventProjectionContext context,
    CancellationToken cancellationToken);

/// <summary>
/// Describes a plugin contribution that can project replayed and live canonical session events into transient events.
/// </summary>
public sealed record PluginSessionEventProjectionContribution
{
    /// <summary>Gets the contribution name.</summary>
    public required string Name { get; init; }

    /// <summary>Gets the projection handler.</summary>
    public required PluginSessionEventProjectionHandler ProjectAsync { get; init; }
}

/// <summary>
/// Provides canonical session events to a plugin-owned transient event projection.
/// </summary>
public sealed record PluginSessionEventProjectionContext
{
    /// <summary>Gets the owning contribution handle.</summary>
    public required PluginContributionHandle Handle { get; init; }

    /// <summary>Gets the durable session identifier.</summary>
    public required string SessionId { get; init; }

    /// <summary>Gets the project identifier, when known.</summary>
    public string? ProjectId { get; init; }

    /// <summary>Gets the project path, when known.</summary>
    public string? ProjectPath { get; init; }

    /// <summary>Gets the model provider identifier, when known.</summary>
    public string? ProviderId { get; init; }

    /// <summary>Gets the active model identifier, when known.</summary>
    public string? Model { get; init; }

    /// <summary>Gets the provider/runtime session identifier represented by the event batch, when known.</summary>
    public string? RuntimeSessionId { get; init; }

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
/// Describes a plugin-owned transient session event projection result.
/// </summary>
public record PluginDerivedSessionEvent
{
    /// <summary>Gets the plugin-stable derived event identifier.</summary>
    public required string EventId { get; init; }

    /// <summary>Gets markdown text for default frontend rendering, when available.</summary>
    public string? Markdown { get; init; }

    /// <summary>
    /// Gets an HTML fragment that the desktop application shows instead of <see cref="Markdown"/>, when available.
    /// </summary>
    /// <remarks>Keep <see cref="Markdown"/>: it is what the terminal application shows and what Copy uses.</remarks>
    public string? Html { get; init; }

    /// <summary>
    /// Gets the script of <see cref="Html"/> in the desktop application, or <see langword="null"/> for a fragment alone (see <see cref="PluginScript"/>).
    /// The window runs it when the card becomes visible, since a session can have hundreds of cards.
    /// </summary>
    public PluginScript? Script { get; init; }

    /// <summary>Gets the timestamp to show for the transient event, when available.</summary>
    public DateTimeOffset? Timestamp { get; init; }

    /// <summary>Gets an optional renderer target/schema name.</summary>
    public string? RenderTarget { get; init; }

    /// <summary>Gets an optional structured payload.</summary>
    public object? Payload { get; init; }

    /// <summary>Gets optional Markdown detail sections that the frontend may render collapsed by default.</summary>
    public IReadOnlyList<PluginDerivedSessionEventDetailSection> DetailSections { get; init; } = [];

    /// <summary>
    /// Gets optional dynamic Markdown content for projections that complete asynchronously after the event is first rendered.
    /// </summary>
    public PluginDynamicDerivedSessionEventContent? DynamicContent { get; init; }

    /// <summary>Gets a value indicating whether an existing transient event should be removed.</summary>
    public bool Remove { get; init; }
}

/// <summary>
/// Provides mutable Markdown content for a plugin-derived transient session event.
/// </summary>
/// <remarks>
/// Implementations may update <see cref="Markdown"/> and <see cref="DetailSections"/> from background work and call
/// <see cref="NotifyChanged"/> so hosts can refresh the existing transient card without replaying canonical history.
/// </remarks>
public abstract class PluginDynamicDerivedSessionEventContent
{
    /// <summary>Raised when the dynamic Markdown content has changed.</summary>
    public event EventHandler? Changed;

    /// <summary>Gets the current Markdown text.</summary>
    public abstract string Markdown { get; }

    /// <summary>Gets the current HTML fragment for the desktop application, or <see langword="null"/> to show <see cref="Markdown"/>.</summary>
    public virtual string? Html => null;

    /// <summary>Gets the current detail sections.</summary>
    public virtual IReadOnlyList<PluginDerivedSessionEventDetailSection> DetailSections => [];

    /// <summary>Raises the <see cref="Changed"/> event.</summary>
    protected void NotifyChanged() => Changed?.Invoke(this, EventArgs.Empty);
}

/// <summary>
/// Describes a plugin-derived Markdown detail section for a transient session event.
/// </summary>
public record PluginDerivedSessionEventDetailSection
{
    /// <summary>Gets the section header.</summary>
    public required string Header { get; init; }

    /// <summary>Gets the section Markdown.</summary>
    public required string Markdown { get; init; }

    /// <summary>Gets an HTML fragment that the desktop application shows instead of <see cref="Markdown"/>, when available.</summary>
    public string? Html { get; init; }
}
