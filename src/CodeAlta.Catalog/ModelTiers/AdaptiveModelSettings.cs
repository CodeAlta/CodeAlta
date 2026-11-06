using CodeAlta.Agent;

namespace CodeAlta.Catalog.ModelTiers;

/// <summary>A level of Adaptive: how much a session spends to get each change right.</summary>
public enum AdaptiveLevel
{
    /// <summary>The cheapest level: a fast model, its changes rechecked or reviewed by an inexpensive critic.</summary>
    Low,

    /// <summary>The usual level: a strong, inexpensive model whose changes a model of another family reviews.</summary>
    Medium,

    /// <summary>Harder work: the model of the turns thinks longer, and its changes are reviewed.</summary>
    High,

    /// <summary>Work that must be right: the strongest combination, with several critics.</summary>
    Max,
}

/// <summary>Names and parses the levels of Adaptive.</summary>
public static class AdaptiveLevels
{
    /// <summary>Gets the levels, from the cheapest to the strongest.</summary>
    public static IReadOnlyList<AdaptiveLevel> All { get; } = [AdaptiveLevel.Low, AdaptiveLevel.Medium, AdaptiveLevel.High, AdaptiveLevel.Max];

    /// <summary>Gets the name of a level as the configuration and the commands write it.</summary>
    /// <param name="level">The level.</param>
    /// <returns><c>low</c>, <c>medium</c>, <c>high</c> or <c>max</c>.</returns>
    public static string NameOf(AdaptiveLevel level) => level.ToString().ToLowerInvariant();

    /// <summary>Parses the name of a level, whatever its case.</summary>
    /// <param name="value">The name.</param>
    /// <returns>The level, or <see langword="null"/> when the name is none of them.</returns>
    public static AdaptiveLevel? Parse(string? value)
        => value?.Trim().ToLowerInvariant() switch
        {
            "low" => AdaptiveLevel.Low,
            "medium" => AdaptiveLevel.Medium,
            "high" => AdaptiveLevel.High,
            "max" => AdaptiveLevel.Max,
            _ => null,
        };
}

/// <summary>
/// A level of Adaptive: the model its turns run on, and the models that review, read-only, each change of code a turn
/// makes. Without critics, the model of the turns rechecks its change itself.
/// </summary>
/// <param name="Level">The level.</param>
/// <param name="Model">The model of the turns, with at most one reasoning effort.</param>
/// <param name="Critics">The critics, in order, each with at most one reasoning effort; empty for a recheck.</param>
public sealed record AdaptiveLevelSettings(AdaptiveLevel Level, ModelTierTarget Model, IReadOnlyList<ModelTierTarget> Critics)
{
    /// <summary>The most critics of a level: each one more reviews the change in parallel.</summary>
    public const int MaximumCritics = 3;

    /// <summary>Gets the table of the level as the configuration writes it.</summary>
    /// <returns>The table.</returns>
    public CodeAltaModelAdaptiveLevelDocument ToDocument()
        => new() { Model = Model.Format(), Critics = Critics.Count == 0 ? null : [.. Critics.Select(static critic => critic.Format())] };

    /// <inheritdoc />
    public bool Equals(AdaptiveLevelSettings? other)
        => other is not null && Level == other.Level && Model.Equals(other.Model) && Critics.SequenceEqual(other.Critics);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Level, Model, Critics.Count);
}

/// <summary>
/// How CodeAlta runs the turns of a session on Adaptive, from the <c>[models.adaptive]</c> table: the levels, each a
/// model and its critics, which can be of any configured provider, and the level a session takes when none is chosen.
/// </summary>
/// <remarks>
/// A turn of the user runs on the model of the level of the session. When it changed code, the critics of the level
/// review the change in sessions of their own, read-only, and the session revises it once with what they found; a level
/// without critics asks its model to recheck the change instead. Adaptive is set up when the table has a level: a session
/// cannot be put on it before that, so that it never runs on a model the user did not choose. The keys of the first
/// Adaptive (<c>code</c>, <c>escalate</c>, <c>recheck</c>) are read and ignored.
/// </remarks>
/// <param name="DefaultLevel">The level a session takes when none is chosen.</param>
/// <param name="Levels">The levels that are set, from the cheapest.</param>
public sealed record AdaptiveModelSettings(AdaptiveLevel DefaultLevel, IReadOnlyList<AdaptiveLevelSettings> Levels)
{
    /// <summary>Gets Adaptive set up without any level yet: the table exists, and no session can be put on it.</summary>
    public static AdaptiveModelSettings Empty { get; } = new(AdaptiveLevel.Medium, []);

    /// <summary>Gets whether a session can be put on Adaptive: a level is set.</summary>
    public bool IsReady => Levels.Count > 0;

    /// <summary>Gets a level that is set.</summary>
    /// <param name="level">The level.</param>
    /// <returns>The level, or <see langword="null"/> when it is not set.</returns>
    public AdaptiveLevelSettings? Get(AdaptiveLevel level) => Levels.FirstOrDefault(set => set.Level == level);

    /// <summary>
    /// Gets the level a session runs on: the one it chose when it is set, else the default level, else the nearest level
    /// that is set (the cheaper one first on a tie).
    /// </summary>
    /// <param name="chosen">The level the session chose, or <see langword="null"/>.</param>
    /// <returns>The level, or <see langword="null"/> when none is set.</returns>
    public AdaptiveLevelSettings? Resolve(AdaptiveLevel? chosen)
    {
        if (chosen is { } asked && Get(asked) is { } exact) return exact;
        var wanted = chosen ?? DefaultLevel;
        if (Get(wanted) is { } preferred) return preferred;
        return Levels.OrderBy(set => Math.Abs((int)set.Level - (int)wanted)).ThenBy(static set => set.Level).FirstOrDefault();
    }

    /// <summary>Gets the table as the configuration writes it.</summary>
    /// <returns>The table, every level that is set written.</returns>
    public CodeAltaModelAdaptiveDocument ToDocument()
        => new()
        {
            Level = DefaultLevel == Empty.DefaultLevel ? null : AdaptiveLevels.NameOf(DefaultLevel),
            Low = Get(AdaptiveLevel.Low)?.ToDocument(),
            Medium = Get(AdaptiveLevel.Medium)?.ToDocument(),
            High = Get(AdaptiveLevel.High)?.ToDocument(),
            Max = Get(AdaptiveLevel.Max)?.ToDocument(),
        };

    /// <summary>Reads the <c>[models.adaptive]</c> table; the keys of the first Adaptive are ignored.</summary>
    /// <param name="document">The table, or <see langword="null"/> when the file has none.</param>
    /// <returns>The settings, or <see langword="null"/> when Adaptive is not set up.</returns>
    public static AdaptiveModelSettings? Read(CodeAltaModelAdaptiveDocument? document)
    {
        if (document is null) return null;
        var levels = new List<AdaptiveLevelSettings>();
        foreach (var level in AdaptiveLevels.All)
        {
            if (ReadLevel(level, LevelOf(document, level)) is { } read) levels.Add(read);
        }

        return new AdaptiveModelSettings(AdaptiveLevels.Parse(document.Level) ?? Empty.DefaultLevel, levels);
    }

    /// <inheritdoc />
    public bool Equals(AdaptiveModelSettings? other)
        => other is not null && DefaultLevel == other.DefaultLevel && Levels.SequenceEqual(other.Levels);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(DefaultLevel, Levels.Count);

    /// <summary>Gets the table of a level in the document.</summary>
    /// <param name="document">The table of Adaptive.</param>
    /// <param name="level">The level.</param>
    /// <returns>The table of the level, or <see langword="null"/>.</returns>
    public static CodeAltaModelAdaptiveLevelDocument? LevelOf(CodeAltaModelAdaptiveDocument document, AdaptiveLevel level)
    {
        ArgumentNullException.ThrowIfNull(document);
        return level switch
        {
            AdaptiveLevel.Low => document.Low,
            AdaptiveLevel.Medium => document.Medium,
            AdaptiveLevel.High => document.High,
            _ => document.Max,
        };
    }

    // A level whose model is not written is not set; what is not valid was refused by Validate.
    private static AdaptiveLevelSettings? ReadLevel(AdaptiveLevel level, CodeAltaModelAdaptiveLevelDocument? document)
    {
        if (document?.Model is null || !ModelTierTarget.TryParse(document.Model, out var model, out _)) return null;
        var critics = (document.Critics ?? [])
            .Select(static written => ModelTierTarget.TryParse(written, out var critic, out _) ? critic : null)
            .OfType<ModelTierTarget>()
            .Take(AdaptiveLevelSettings.MaximumCritics)
            .ToArray();
        return new AdaptiveLevelSettings(level, model, critics);
    }

    // What is wrong with the table, each message naming the setting. The keys of the first Adaptive are never wrong.
    internal static void Validate(List<string> errors, CodeAltaModelAdaptiveDocument? document)
    {
        if (document is null) return;
        if (document.Level is { } level && AdaptiveLevels.Parse(level) is null)
            errors.Add($"models.adaptive.level: '{level}' is not a level. Use low, medium, high or max.");
        foreach (var each in AdaptiveLevels.All)
        {
            var table = LevelOf(document, each);
            if (table is null) continue;
            var name = $"models.adaptive.{AdaptiveLevels.NameOf(each)}";
            if (table.Model is null)
            {
                if (table.Critics is { Count: > 0 }) errors.Add($"{name}.model: a level with critics needs the model of its turns.");
            }
            else
            {
                ValidateTarget(errors, $"{name}.model", table.Model);
            }

            var critics = table.Critics ?? [];
            if (critics.Count > AdaptiveLevelSettings.MaximumCritics)
                errors.Add($"{name}.critics: {critics.Count} critics; a level has at most {AdaptiveLevelSettings.MaximumCritics}.");
            for (var index = 0; index < critics.Count; index++) ValidateTarget(errors, $"{name}.critics[{index + 1}]", critics[index]);
        }
    }

    private static void ValidateTarget(List<string> errors, string setting, string? written)
    {
        if (!ModelTierTarget.TryParse(written, out var target, out var error)) errors.Add($"{setting}: {error}");
        else if (target.Reasoning.Count > 1) errors.Add($"{setting}: '{written}' names {target.Reasoning.Count} reasoning efforts; name one at most.");
    }
}

/// <summary>
/// The combinations of models Adaptive proposes for the providers that are configured, as the evaluation of November 2026
/// measured them (GPT-6.1 Sol for the turns, a Claude model of another family as the critic), and what it proposes when
/// only one family is there.
/// </summary>
public static class AdaptiveProposals
{
    /// <summary>A model of a configured provider that a proposal can use.</summary>
    /// <param name="ProviderKey">The key of the provider.</param>
    /// <param name="ModelId">The model as the provider names it.</param>
    /// <param name="Subscription">Whether the provider runs on a subscription (Codex, Claude Code), which a proposal prefers.</param>
    /// <param name="DisplayName">
    /// The name the provider gives the model, or <see langword="null"/>: an alias (<c>sonnet</c>) is proposed only when it
    /// names the version that was measured, as Claude Code's <c>haiku</c> may be another one.
    /// </param>
    public sealed record Candidate(string ProviderKey, string ModelId, bool Subscription, string? DisplayName = null);

    /// <summary>Proposes the levels of Adaptive from the models the configured providers offer.</summary>
    /// <param name="candidates">The models of the providers that are ready, in the order the providers are listed.</param>
    /// <returns>The levels, from the cheapest; empty when no model a proposal knows is there.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="candidates"/> is null.</exception>
    public static IReadOnlyList<AdaptiveLevelSettings> Propose(IEnumerable<Candidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var list = candidates.ToArray();
        var sol = Find(list, "6.1", "gpt-6.1-sol");
        var sonnet = Find(list, "5.5", "claude-sonnet-5-5", "sonnet");
        var opus = Find(list, "5.5", "claude-opus-5-5", "opus");
        var haiku = Find(list, "5.5", "claude-haiku-5-5", "haiku");
        if (sol is not null && (sonnet ?? opus) is { } critic)
        {
            // Both families: measured in November 2026 on SWE-bench Pro and Terminal-Bench.
            var strong = opus ?? critic;
            return
            [
                Level(AdaptiveLevel.Low, sol, AgentReasoningEffort.Low, haiku is null ? [] : [(haiku, AgentReasoningEffort.High)]),
                Level(AdaptiveLevel.Medium, sol, AgentReasoningEffort.Low, [(critic, AgentReasoningEffort.High)]),
                Level(AdaptiveLevel.High, sol, AgentReasoningEffort.Medium, [(critic, AgentReasoningEffort.High)]),
                Level(AdaptiveLevel.Max, sol, AgentReasoningEffort.High,
                    ReferenceEquals(strong, critic) ? [(critic, AgentReasoningEffort.High)] : [(critic, AgentReasoningEffort.High), (strong, AgentReasoningEffort.High)]),
            ];
        }

        if (sol is not null)
        {
            // One family: the critic is the same model, in a session of its own, thinking longer.
            return
            [
                Level(AdaptiveLevel.Low, sol, AgentReasoningEffort.Low, []),
                Level(AdaptiveLevel.Medium, sol, AgentReasoningEffort.Medium, []),
                Level(AdaptiveLevel.High, sol, AgentReasoningEffort.Medium, [(sol, AgentReasoningEffort.High)]),
                Level(AdaptiveLevel.Max, sol, AgentReasoningEffort.High, [(sol, AgentReasoningEffort.XHigh)]),
            ];
        }

        if ((sonnet ?? opus ?? haiku) is { } claude)
        {
            var turns = sonnet ?? opus ?? claude;
            var review = opus ?? turns;
            return
            [
                Level(AdaptiveLevel.Low, haiku ?? turns, AgentReasoningEffort.High, []),
                Level(AdaptiveLevel.Medium, turns, AgentReasoningEffort.High, ReferenceEquals(review, turns) ? [] : [(review, AgentReasoningEffort.High)]),
                Level(AdaptiveLevel.High, review, AgentReasoningEffort.High, ReferenceEquals(review, turns) ? [] : [(turns, AgentReasoningEffort.High)]),
                Level(AdaptiveLevel.Max, review, AgentReasoningEffort.XHigh, [(turns, AgentReasoningEffort.High)]),
            ];
        }

        return [];
    }

    // The first candidate of a model, a subscription first, an exact name before an alias; an alias (the names after
    // the first) only when the provider gives no name or one with the version measured.
    private static Candidate? Find(Candidate[] candidates, string version, params string[] names)
    {
        foreach (var subscription in new[] { true, false })
        {
            for (var index = 0; index < names.Length; index++)
            {
                var name = names[index];
                var alias = index > 0;
                if (candidates.FirstOrDefault(candidate => candidate.Subscription == subscription
                        && string.Equals(candidate.ModelId, name, StringComparison.OrdinalIgnoreCase)
                        && (!alias || candidate.DisplayName is null || candidate.DisplayName.Contains(version, StringComparison.Ordinal))) is { } found)
                    return found;
            }
        }

        return null;
    }

    private static AdaptiveLevelSettings Level(AdaptiveLevel level, Candidate model, AgentReasoningEffort effort, (Candidate Model, AgentReasoningEffort Effort)[] critics)
        => new(level, Target(model, effort), [.. critics.Select(static critic => Target(critic.Model, critic.Effort))]);

    private static ModelTierTarget Target(Candidate candidate, AgentReasoningEffort effort)
        => new(candidate.ProviderKey.ToLowerInvariant(), candidate.ModelId, [effort]);
}
