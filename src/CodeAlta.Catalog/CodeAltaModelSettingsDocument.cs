using System.Text.Json.Serialization;

namespace CodeAlta.Catalog;

/// <summary>Represents the <c>[models]</c> table: the model tiers and the provider fallback.</summary>
public sealed class CodeAltaModelSettingsDocument
{
    /// <summary>Gets or sets the ordered targets of each tier.</summary>
    [JsonPropertyName("tiers")]
    public CodeAltaModelTiersDocument? Tiers { get; set; }

    /// <summary>Gets or sets when a session goes on with the next target of its tier after a failure.</summary>
    [JsonPropertyName("fallback")]
    public CodeAltaModelFallbackDocument? Fallback { get; set; }

    /// <summary>Gets or sets how CodeAlta chooses the model of each turn of a session on Adaptive; not set up when absent.</summary>
    [JsonPropertyName("adaptive")]
    public CodeAltaModelAdaptiveDocument? Adaptive { get; set; }
}

/// <summary>
/// Represents the <c>[models.adaptive]</c> table: the levels of Adaptive (<c>low</c>, <c>medium</c>, <c>high</c>,
/// <c>max</c>), each a model and the critics that review its changes, and the level a session takes when none is chosen.
/// </summary>
public sealed class CodeAltaModelAdaptiveDocument
{
    /// <summary>Gets or sets the level a session put on Adaptive takes when none is chosen; <c>medium</c> when absent.</summary>
    [JsonPropertyName("level")]
    public string? Level { get; set; }

    /// <summary>Gets or sets the <c>low</c> level.</summary>
    [JsonPropertyName("low")]
    public CodeAltaModelAdaptiveLevelDocument? Low { get; set; }

    /// <summary>Gets or sets the <c>medium</c> level.</summary>
    [JsonPropertyName("medium")]
    public CodeAltaModelAdaptiveLevelDocument? Medium { get; set; }

    /// <summary>Gets or sets the <c>high</c> level.</summary>
    [JsonPropertyName("high")]
    public CodeAltaModelAdaptiveLevelDocument? High { get; set; }

    /// <summary>Gets or sets the <c>max</c> level.</summary>
    [JsonPropertyName("max")]
    public CodeAltaModelAdaptiveLevelDocument? Max { get; set; }

    /// <summary>Gets or sets the tier of the turns of the first Adaptive; read so that an older file loads, and ignored.</summary>
    [JsonPropertyName("code")]
    public string? Code { get; set; }

    /// <summary>Gets or sets the escalation tier of the first Adaptive; read so that an older file loads, and ignored.</summary>
    [JsonPropertyName("escalate")]
    public string? Escalate { get; set; }

    /// <summary>Gets or sets the recheck switch of the first Adaptive; read so that an older file loads, and ignored.</summary>
    [JsonPropertyName("recheck")]
    public bool? Recheck { get; set; }
}

/// <summary>Represents a level of <c>[models.adaptive]</c>, such as <c>[models.adaptive.medium]</c>.</summary>
public sealed class CodeAltaModelAdaptiveLevelDocument
{
    /// <summary>Gets or sets the model the turns run on, written <c>provider:model@reasoning</c>.</summary>
    [JsonPropertyName("model")]
    public string? Model { get; set; }

    /// <summary>
    /// Gets or sets the models that review, read-only, each change of code of a turn, written as <see cref="Model"/>; none
    /// asks the model of the turns to recheck its change itself.
    /// </summary>
    [JsonPropertyName("critics")]
    public List<string>? Critics { get; set; }
}

/// <summary>
/// Represents the <c>[models.tiers]</c> table. Each tier is an ordered list of targets written
/// <c>provider:model@reasoning1,reasoning2</c>, the provider being the key of a configured provider.
/// </summary>
public sealed class CodeAltaModelTiersDocument
{
    /// <summary>Gets or sets the targets for complex work.</summary>
    [JsonPropertyName("complex")]
    public List<string>? Complex { get; set; }

    /// <summary>Gets or sets the targets for standard work.</summary>
    [JsonPropertyName("standard")]
    public List<string>? Standard { get; set; }

    /// <summary>Gets or sets the targets for quick work.</summary>
    [JsonPropertyName("quick")]
    public List<string>? Quick { get; set; }
}

/// <summary>Represents the <c>[models.fallback]</c> table.</summary>
public sealed class CodeAltaModelFallbackDocument
{
    /// <summary>Gets or sets whether a session goes on with the next target of its tier; false when absent.</summary>
    [JsonPropertyName("enabled")]
    public bool? Enabled { get; set; }

    /// <summary>
    /// Gets or sets the failures that lead to the next target: <c>quota</c> and <c>unavailable</c>
    /// (both when absent).
    /// </summary>
    [JsonPropertyName("on")]
    public List<string>? On { get; set; }

    /// <summary>
    /// Gets or sets whether a session that stopped on a limit, with no other model of its tier to take over, resumes by
    /// itself when the earliest limit of its tier resets; false when absent.
    /// </summary>
    [JsonPropertyName("resume")]
    public bool? Resume { get; set; }

    /// <summary>
    /// Gets or sets how many times in a row a session resumes by itself before it waits for the user;
    /// <see cref="ModelTiers.ModelFallbackSettings.DefaultMaxResumes"/> when absent.
    /// </summary>
    [JsonPropertyName("max_resumes")]
    public int? MaxResumes { get; set; }
}
