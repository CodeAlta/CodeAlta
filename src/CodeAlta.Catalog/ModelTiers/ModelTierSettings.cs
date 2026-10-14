using CodeAlta.Agent;

namespace CodeAlta.Catalog.ModelTiers;

/// <summary>The kind of work a tier of models is for.</summary>
public enum ModelTier
{
    /// <summary>Complex work: the strongest models.</summary>
    Complex,

    /// <summary>Standard work: the models new sessions start with.</summary>
    Standard,

    /// <summary>Quick work, such as a plain search: the fastest models.</summary>
    Quick,
}

/// <summary>Names and parses the tiers of models.</summary>
public static class ModelTiers
{
    /// <summary>Gets the tiers, from the strongest to the quickest.</summary>
    public static IReadOnlyList<ModelTier> All { get; } = [ModelTier.Complex, ModelTier.Standard, ModelTier.Quick];

    /// <summary>Gets the name of a tier as the configuration and the commands write it.</summary>
    /// <param name="tier">The tier.</param>
    /// <returns><c>complex</c>, <c>standard</c> or <c>quick</c>.</returns>
    public static string NameOf(ModelTier tier)
        => tier switch
        {
            ModelTier.Complex => "complex",
            ModelTier.Standard => "standard",
            ModelTier.Quick => "quick",
            _ => tier.ToString().ToLowerInvariant(),
        };

    /// <summary>Parses the name of a tier, whatever its case.</summary>
    /// <param name="value">The name.</param>
    /// <returns>The tier, or <see langword="null"/> when the name is none of them.</returns>
    public static ModelTier? Parse(string? value)
        => value?.Trim().ToLowerInvariant() switch
        {
            "complex" => ModelTier.Complex,
            "standard" => ModelTier.Standard,
            "quick" => ModelTier.Quick,
            _ => null,
        };
}

/// <summary>
/// One target of a tier: a model of a configured provider, with the reasoning efforts to use in order of preference.
/// Written <c>provider:model@reasoning1,reasoning2</c>; the reasoning part is optional.
/// </summary>
/// <param name="ProviderKey">The key of the configured provider, which names the account.</param>
/// <param name="ModelId">The model.</param>
/// <param name="Reasoning">The reasoning efforts in order of preference; empty for the provider's own choice.</param>
public sealed record ModelTierTarget(string ProviderKey, string ModelId, IReadOnlyList<AgentReasoningEffort> Reasoning)
{
    /// <summary>The longest text of a target.</summary>
    public const int MaximumLength = 256;

    /// <summary>Gets the preferred reasoning effort, or <see langword="null"/> for the provider's own choice.</summary>
    public AgentReasoningEffort? PreferredReasoning => Reasoning.Count == 0 ? null : Reasoning[0];

    /// <summary>Gets whether the target is the model of a provider, whatever its reasoning.</summary>
    /// <param name="providerKey">The provider key.</param>
    /// <param name="modelId">The model.</param>
    public bool IsModel(string? providerKey, string? modelId)
        => string.Equals(ProviderKey, providerKey?.Trim(), StringComparison.OrdinalIgnoreCase)
           && string.Equals(ModelId, modelId?.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>Formats the target as the configuration writes it.</summary>
    /// <returns><c>provider:model</c>, followed by <c>@</c> and the reasoning efforts when there are any.</returns>
    public string Format()
        => Reasoning.Count == 0
            ? $"{ProviderKey}:{ModelId}"
            : $"{ProviderKey}:{ModelId}@{string.Join(',', Reasoning.Select(static effort => CodeAltaConfigStore.FormatReasoningEffort(effort)))}";

    /// <summary>Parses a target written <c>provider:model@reasoning1,reasoning2</c>.</summary>
    /// <param name="value">The written target.</param>
    /// <param name="target">The target when it is valid.</param>
    /// <param name="error">What is wrong with it, otherwise <see langword="null"/>.</param>
    /// <returns>Whether the target is valid.</returns>
    public static bool TryParse(string? value, out ModelTierTarget target, out string? error)
    {
        target = null!;
        var text = value?.Trim() ?? string.Empty;
        var colon = text.IndexOf(':', StringComparison.Ordinal);
        if (colon <= 0 || colon == text.Length - 1 || text.Length > MaximumLength || text.Any(char.IsWhiteSpace))
        {
            error = $"'{text}' must be written '<provider>:<model>' or '<provider>:<model>@<reasoning>,<reasoning>'.";
            return false;
        }

        var providerKey = text[..colon].Trim();
        if (!providerKey.All(static ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.'))
        {
            error = $"'{text}': '{providerKey}' is not the key of a provider.";
            return false;
        }
        var modelAndReasoning = text[(colon + 1)..];
        var at = modelAndReasoning.LastIndexOf('@');
        var modelId = (at < 0 ? modelAndReasoning : modelAndReasoning[..at]).Trim();
        if (providerKey.Length == 0 || modelId.Length == 0)
        {
            error = $"'{text}' must name a provider and a model.";
            return false;
        }

        var reasoning = new List<AgentReasoningEffort>();
        if (at >= 0)
        {
            foreach (var part in modelAndReasoning[(at + 1)..].Split(','))
            {
                if (CodeAltaConfigStore.ParseReasoningEffort(part) is not { } effort)
                {
                    error = $"'{text}': '{part.Trim()}' is not a reasoning effort. Use none, minimal, low, medium, high, xhigh or max.";
                    return false;
                }

                if (reasoning.Contains(effort))
                {
                    error = $"'{text}': the reasoning effort '{part.Trim()}' is listed twice.";
                    return false;
                }

                reasoning.Add(effort);
            }
        }

        target = new ModelTierTarget(providerKey.ToLowerInvariant(), modelId, reasoning);
        error = null;
        return true;
    }

    /// <inheritdoc />
    public bool Equals(ModelTierTarget? other)
        => other is not null
           && string.Equals(ProviderKey, other.ProviderKey, StringComparison.Ordinal)
           && string.Equals(ModelId, other.ModelId, StringComparison.Ordinal)
           && Reasoning.SequenceEqual(other.Reasoning);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(ProviderKey, ModelId, Reasoning.Count);

    /// <summary>Writes the target as it is read (see <see cref="Format"/>).</summary>
    /// <returns>The text of the target.</returns>
    public override string ToString() => Format();
}

/// <summary>The ordered targets of each tier, from the <c>[models.tiers]</c> table.</summary>
public sealed class ModelTierSettings
{
    /// <summary>Initializes the tiers.</summary>
    /// <param name="complex">The targets for complex work.</param>
    /// <param name="standard">The targets for standard work.</param>
    /// <param name="quick">The targets for quick work.</param>
    /// <exception cref="ArgumentNullException">A list is null.</exception>
    public ModelTierSettings(
        IReadOnlyList<ModelTierTarget> complex,
        IReadOnlyList<ModelTierTarget> standard,
        IReadOnlyList<ModelTierTarget> quick)
    {
        ArgumentNullException.ThrowIfNull(complex);
        ArgumentNullException.ThrowIfNull(standard);
        ArgumentNullException.ThrowIfNull(quick);
        Complex = complex;
        Standard = standard;
        Quick = quick;
    }

    /// <summary>Gets tiers with no target: the user set none.</summary>
    public static ModelTierSettings Empty { get; } = new([], [], []);

    /// <summary>Gets the targets for complex work.</summary>
    public IReadOnlyList<ModelTierTarget> Complex { get; }

    /// <summary>Gets the targets for standard work.</summary>
    public IReadOnlyList<ModelTierTarget> Standard { get; }

    /// <summary>Gets the targets for quick work.</summary>
    public IReadOnlyList<ModelTierTarget> Quick { get; }

    /// <summary>Gets whether no tier has a target.</summary>
    public bool IsEmpty => Complex.Count == 0 && Standard.Count == 0 && Quick.Count == 0;

    /// <summary>Gets the targets of a tier, in order of preference.</summary>
    /// <param name="tier">The tier.</param>
    public IReadOnlyList<ModelTierTarget> Get(ModelTier tier)
        => tier switch
        {
            ModelTier.Complex => Complex,
            ModelTier.Standard => Standard,
            ModelTier.Quick => Quick,
            _ => [],
        };

    /// <summary>
    /// Finds the tier a model belongs to, so that a session on a model chosen by hand falls back within it: the
    /// tier where it comes first, standard before complex before quick when it comes as early in several.
    /// </summary>
    /// <param name="providerKey">The provider key.</param>
    /// <param name="modelId">The model.</param>
    /// <returns>The tier, or <see langword="null"/> when no tier lists the model.</returns>
    public ModelTier? FindTierOf(string? providerKey, string? modelId)
    {
        ModelTier? found = null;
        var best = int.MaxValue;
        foreach (var tier in (ReadOnlySpan<ModelTier>)[ModelTier.Standard, ModelTier.Complex, ModelTier.Quick])
        {
            var targets = Get(tier);
            for (var index = 0; index < targets.Count && index < best; index++)
            {
                if (targets[index].IsModel(providerKey, modelId))
                {
                    found = tier;
                    best = index;
                    break;
                }
            }
        }

        return found;
    }

    /// <summary>Gets the targets of the tiers as the configuration writes them, or null when there are none.</summary>
    /// <returns>The table, or <see langword="null"/> when no tier has a target.</returns>
    public CodeAltaModelTiersDocument? ToDocument()
        => IsEmpty
            ? null
            : new CodeAltaModelTiersDocument
            {
                Complex = Complex.Count == 0 ? null : [.. Complex.Select(static target => target.Format())],
                Standard = Standard.Count == 0 ? null : [.. Standard.Select(static target => target.Format())],
                Quick = Quick.Count == 0 ? null : [.. Quick.Select(static target => target.Format())],
            };
}

/// <summary>When a session goes on with the next target of its tier after a failure, from <c>[models.fallback]</c>.</summary>
/// <param name="Enabled">Whether the fallback is on; it is off unless the user turns it on.</param>
/// <param name="OnQuota">Whether an exhausted quota or allowance leads to the next target.</param>
/// <param name="OnUnavailable">Whether a provider that stays unavailable after its retries leads to the next target.</param>
public sealed record ModelFallbackSettings(bool Enabled, bool OnQuota, bool OnUnavailable)
{
    /// <summary>The name of the quota trigger.</summary>
    public const string QuotaTrigger = "quota";

    /// <summary>The name of the unavailable trigger.</summary>
    public const string UnavailableTrigger = "unavailable";

    /// <summary>Gets the choices when nothing is configured: off, on both failures once turned on.</summary>
    public static ModelFallbackSettings Default { get; } = new(false, true, true);

    /// <summary>Gets the table as the configuration writes it, or null for the defaults.</summary>
    /// <returns>The table, or <see langword="null"/> when every choice is the default.</returns>
    public CodeAltaModelFallbackDocument? ToDocument()
        => this == Default
            ? null
            : new CodeAltaModelFallbackDocument
            {
                Enabled = Enabled ? true : null,
                On = OnQuota && OnUnavailable ? null : [.. Triggers()],
            };

    private IEnumerable<string> Triggers()
    {
        if (OnQuota) yield return QuotaTrigger;
        if (OnUnavailable) yield return UnavailableTrigger;
    }
}

/// <summary>The model tiers and the provider fallback, from the global <c>[models]</c> table.</summary>
/// <param name="Tiers">The ordered targets of each tier.</param>
/// <param name="Fallback">When a session goes on with the next target of its tier.</param>
public sealed record ModelSettings(ModelTierSettings Tiers, ModelFallbackSettings Fallback)
{
    /// <summary>Gets the choices when nothing is configured: no tier, fallback off.</summary>
    public static ModelSettings Default { get; } = new(ModelTierSettings.Empty, ModelFallbackSettings.Default);

    /// <summary>Reads the <c>[models]</c> table.</summary>
    /// <param name="document">The table, or <see langword="null"/> when the file has none.</param>
    /// <returns>The settings.</returns>
    /// <exception cref="InvalidDataException">A target or a fallback trigger is not valid; the message says which.</exception>
    public static ModelSettings Read(CodeAltaModelSettingsDocument? document)
    {
        if (document is null) return Default;
        var errors = Validate(document);
        if (errors.Count > 0) throw new InvalidDataException(string.Join(Environment.NewLine, errors));
        return new ModelSettings(
            new ModelTierSettings(
                ParseTier(document?.Tiers?.Complex),
                ParseTier(document?.Tiers?.Standard),
                ParseTier(document?.Tiers?.Quick)),
            ReadFallback(document?.Fallback));
    }

    /// <summary>Lists what is wrong with a <c>[models]</c> table, each message naming the setting.</summary>
    /// <param name="document">The table, or <see langword="null"/>.</param>
    /// <returns>The messages; empty when the table is valid.</returns>
    public static IReadOnlyList<string> Validate(CodeAltaModelSettingsDocument? document)
    {
        var errors = new List<string>();
        if (document is null) return errors;
        ValidateTier(errors, ModelTier.Complex, document.Tiers?.Complex);
        ValidateTier(errors, ModelTier.Standard, document.Tiers?.Standard);
        ValidateTier(errors, ModelTier.Quick, document.Tiers?.Quick);
        foreach (var trigger in document.Fallback?.On ?? [])
        {
            if (trigger?.Trim().ToLowerInvariant() is not (ModelFallbackSettings.QuotaTrigger or ModelFallbackSettings.UnavailableTrigger))
            {
                errors.Add($"models.fallback.on: '{trigger}' is not a failure to fall back on. Use quota or unavailable.");
            }
        }

        return errors;
    }

    /// <summary>Gets the table as the configuration writes it, or null when every choice is the default.</summary>
    /// <returns>The table, or <see langword="null"/>.</returns>
    public CodeAltaModelSettingsDocument? ToDocument()
    {
        var tiers = Tiers.ToDocument();
        var fallback = Fallback.ToDocument();
        return tiers is null && fallback is null ? null : new CodeAltaModelSettingsDocument { Tiers = tiers, Fallback = fallback };
    }

    private static void ValidateTier(List<string> errors, ModelTier tier, List<string>? targets)
    {
        if (targets is null) return;
        var seen = new List<ModelTierTarget>();
        for (var index = 0; index < targets.Count; index++)
        {
            var setting = $"models.tiers.{ModelTiers.NameOf(tier)}[{index + 1}]";
            if (!ModelTierTarget.TryParse(targets[index], out var target, out var error))
            {
                errors.Add($"{setting}: {error}");
            }
            else if (seen.Any(other => other.IsModel(target.ProviderKey, target.ModelId)))
            {
                errors.Add($"{setting}: '{target.ProviderKey}:{target.ModelId}' is listed twice; give one entry its reasoning efforts in order.");
            }
            else
            {
                seen.Add(target);
            }
        }
    }

    private static IReadOnlyList<ModelTierTarget> ParseTier(List<string>? targets)
        => targets is null
            ? []
            : [.. targets.Select(static value => ModelTierTarget.TryParse(value, out var target, out _) ? target : null).OfType<ModelTierTarget>()];

    private static ModelFallbackSettings ReadFallback(CodeAltaModelFallbackDocument? document)
    {
        if (document is null) return ModelFallbackSettings.Default;
        var on = document.On?.Select(static trigger => trigger.Trim().ToLowerInvariant()).ToArray();
        return new ModelFallbackSettings(
            document.Enabled ?? false,
            on is null || on.Contains(ModelFallbackSettings.QuotaTrigger),
            on is null || on.Contains(ModelFallbackSettings.UnavailableTrigger));
    }
}
