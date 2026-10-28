namespace CodeAlta.Catalog;

/// <summary>
/// Describes the CodeAlta actor that created or submitted an alta-visible runtime artifact.
/// </summary>
public sealed record AltaActorProvenance
{
    /// <summary>The <see cref="Kind"/> of an automation: a session it starts names it in <see cref="AutomationId"/>.</summary>
    public const string AutomationKind = "automation";

    /// <summary>
    /// The <see cref="Kind"/> of a script of Code Mode: a session its <c>agent()</c> creates names it, and the answer of that
    /// session goes to the script, never to the session that runs the script.
    /// </summary>
    public const string WorkflowKind = "workflow";

    /// <summary>Gets the actor kind, such as <c>user</c>, <c>agent</c>, <c>host</c>, <c>plugin</c>, or <c>automation</c>.</summary>
    public required string Kind { get; init; }

    /// <summary>Gets the identifier of the automation, when the actor is one.</summary>
    public string? AutomationId { get; init; }

    /// <summary>Gets the source CodeAlta session identifier, when the actor is associated with a session.</summary>
    public string? SourceSessionId { get; init; }

    /// <summary>Gets the source project identifier, when known.</summary>
    public string? SourceProjectId { get; init; }

    /// <summary>Gets the source agent identifier, when known.</summary>
    public string? SourceAgentId { get; init; }

    /// <summary>Gets the plugin runtime key when the actor is a plugin.</summary>
    public string? PluginRuntimeKey { get; init; }

    /// <summary>
    /// Gets the provenance the prompts this actor submits are recorded with in their session (an identifier of
    /// <c>CodeAlta.Agent.AgentPromptProvenance</c>, such as <c>user-review</c>), when the host names one; null to have it
    /// follow from <see cref="Kind"/>.
    /// </summary>
    public string? PromptProvenance { get; init; }

    /// <summary>
    /// Gets a value indicating whether the actor acted for the user, who asked for it in a client of a plugin (the phone of
    /// the Mobile plugin): the prompts it submits are the user's, with no provenance of its <see cref="Kind"/>.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool OnBehalfOfUser { get; init; }

    /// <summary>Gets the alta command correlation identifier associated with the action.</summary>
    public string? CorrelationId { get; init; }

    /// <summary>Gets the UTC time when the provenance record was created.</summary>
    public DateTimeOffset CreatedAt { get; init; }
}
