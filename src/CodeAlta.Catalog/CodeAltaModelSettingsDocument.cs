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

/// <summary>Represents the <c>[models.adaptive]</c> table.</summary>
public sealed class CodeAltaModelAdaptiveDocument
{
    /// <summary>Gets or sets the tier the turns of a session on Adaptive run on; <c>quick</c> when absent.</summary>
    [JsonPropertyName("code")]
    public string? Code { get; set; }

    /// <summary>Gets or sets the tier a turn that ends in visible trouble continues on; <c>complex</c> when absent.</summary>
    [JsonPropertyName("escalate")]
    public string? Escalate { get; set; }

    /// <summary>Gets or sets whether a turn that changed code is asked once to check its change against the request; true when absent.</summary>
    [JsonPropertyName("recheck")]
    public bool? Recheck { get; set; }
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
