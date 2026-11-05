using System.Text.RegularExpressions;

namespace CodeAlta.Agent;

/// <summary>How a rule of <see cref="ModelVisibility"/> matches the id of a model.</summary>
public enum ModelRuleMatch
{
    /// <summary>The whole id, whatever the case: <c>gpt-5.4</c> matches <c>GPT-5.4</c>.</summary>
    AnyCase,

    /// <summary>The whole id, as it is written.</summary>
    Exact,

    /// <summary>A .NET regular expression that matches somewhere in the id (anchor it with <c>^</c> and <c>$</c>).</summary>
    Regex,
}

/// <summary>A rule of <see cref="ModelVisibility"/>: what it matches and how.</summary>
public sealed class ModelRule
{
    private const string ExactPrefix = "exact:";
    private const string RegexPrefix = "regex:";
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(100);
    private readonly Regex? _regex;

    private ModelRule(ModelRuleMatch match, string pattern, Regex? regex)
    {
        Match = match;
        Pattern = pattern;
        _regex = regex;
    }

    /// <summary>Gets how it matches.</summary>
    public ModelRuleMatch Match { get; }

    /// <summary>Gets the id, or the regular expression.</summary>
    public string Pattern { get; }

    /// <summary>
    /// Reads a rule as the configuration writes it: <c>regex:&lt;pattern&gt;</c>, <c>exact:&lt;id&gt;</c>, or the id alone, which
    /// matches whatever its case.
    /// </summary>
    /// <param name="text">The rule.</param>
    /// <returns>The rule.</returns>
    /// <exception cref="ArgumentException">The rule is empty, or its regular expression is not valid.</exception>
    public static ModelRule Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var trimmed = text.Trim();
        var (match, pattern) = trimmed.StartsWith(RegexPrefix, StringComparison.OrdinalIgnoreCase) ? (ModelRuleMatch.Regex, trimmed[RegexPrefix.Length..].Trim())
            : trimmed.StartsWith(ExactPrefix, StringComparison.OrdinalIgnoreCase) ? (ModelRuleMatch.Exact, trimmed[ExactPrefix.Length..].Trim())
            : (ModelRuleMatch.AnyCase, trimmed);
        if (pattern.Length == 0) throw new ArgumentException("A model rule names a model or a regular expression.", nameof(text));
        if (match != ModelRuleMatch.Regex) return new ModelRule(match, pattern, null);
        try
        {
            return new ModelRule(match, pattern, new Regex(pattern, RegexOptions.CultureInvariant, MatchTimeout));
        }
        catch (ArgumentException exception)
        {
            throw new ArgumentException($"'{pattern}' is not a valid regular expression: {exception.Message}", nameof(text), exception);
        }
    }

    /// <summary>Writes the rule as the configuration keeps it.</summary>
    /// <returns>The text <see cref="Parse"/> reads back.</returns>
    public string Format() => Match switch
    {
        ModelRuleMatch.Regex => RegexPrefix + Pattern,
        ModelRuleMatch.Exact => ExactPrefix + Pattern,
        _ => Pattern,
    };

    /// <summary>Whether the rule matches the id of a model; a regular expression that takes too long matches nothing.</summary>
    /// <param name="modelId">The id.</param>
    /// <returns>Whether it matches.</returns>
    public bool Matches(string modelId)
    {
        ArgumentNullException.ThrowIfNull(modelId);
        switch (Match)
        {
            case ModelRuleMatch.Exact:
                return string.Equals(modelId, Pattern, StringComparison.Ordinal);
            case ModelRuleMatch.AnyCase:
                return string.Equals(modelId, Pattern, StringComparison.OrdinalIgnoreCase);
            default:
                try
                {
                    return _regex!.IsMatch(modelId);
                }
                catch (RegexMatchTimeoutException)
                {
                    return false;
                }
        }
    }
}

/// <summary>
/// Which models of a provider are offered (<c>models_hide</c> and <c>models_show</c> of its configuration): a model is hidden when a rule
/// of <see cref="Hide"/> matches it and no rule of <see cref="Show"/> does. Hiding every model with <c>regex:.*</c> and showing a few
/// offers those alone. A hidden model is never picked from a list; a session that names it still runs it.
/// </summary>
/// <param name="Hide">The rules that hide.</param>
/// <param name="Show">The rules that show a model again, whatever hides it.</param>
public sealed record ModelVisibility(IReadOnlyList<ModelRule> Hide, IReadOnlyList<ModelRule> Show)
{
    /// <summary>Gets the visibility that shows every model.</summary>
    public static ModelVisibility All { get; } = new([], []);

    /// <summary>Gets whether it hides nothing.</summary>
    public bool ShowsAll => Hide.Count == 0;

    /// <summary>Reads the rules of a provider as its configuration writes them.</summary>
    /// <param name="hide">The rules that hide; null for none.</param>
    /// <param name="show">The rules that show again; null for none.</param>
    /// <returns>The visibility.</returns>
    /// <exception cref="ArgumentException">A rule is empty, or its regular expression is not valid.</exception>
    public static ModelVisibility Parse(IEnumerable<string>? hide, IEnumerable<string>? show)
    {
        var hidden = (hide ?? []).Where(static rule => !string.IsNullOrWhiteSpace(rule)).Select(ModelRule.Parse).ToArray();
        var shown = (show ?? []).Where(static rule => !string.IsNullOrWhiteSpace(rule)).Select(ModelRule.Parse).ToArray();
        return hidden.Length == 0 && shown.Length == 0 ? All : new ModelVisibility(hidden, shown);
    }

    /// <summary>Reads the rules as <see cref="Parse"/> does, leaving out the rules that do not read.</summary>
    /// <param name="hide">The rules that hide; null for none.</param>
    /// <param name="show">The rules that show again; null for none.</param>
    /// <param name="refused">The rules left out, as they were written.</param>
    /// <returns>The visibility of the rules that read.</returns>
    public static ModelVisibility ParseValid(IEnumerable<string>? hide, IEnumerable<string>? show, out IReadOnlyList<string> refused)
    {
        var left = new List<string>();
        ModelRule[] Read(IEnumerable<string>? rules) => [.. (rules ?? []).Where(static rule => !string.IsNullOrWhiteSpace(rule)).SelectMany(rule =>
        {
            try { return (IEnumerable<ModelRule>)[ModelRule.Parse(rule)]; }
            catch (ArgumentException) { left.Add(rule); return []; }
        })];
        var hidden = Read(hide);
        var shown = Read(show);
        refused = left;
        return hidden.Length == 0 && shown.Length == 0 ? All : new ModelVisibility(hidden, shown);
    }

    /// <summary>Whether a model of this id is offered.</summary>
    /// <param name="modelId">The id.</param>
    /// <returns>Whether it is shown.</returns>
    public bool Shows(string modelId)
    {
        ArgumentNullException.ThrowIfNull(modelId);
        return !Hide.Any(rule => rule.Matches(modelId)) || Show.Any(rule => rule.Matches(modelId));
    }

    /// <summary>Splits models into those offered and those hidden, each in the order they came.</summary>
    /// <param name="models">The models.</param>
    /// <returns>The shown models (<paramref name="models"/> itself when nothing is hidden) and the hidden ones.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="models"/> is null.</exception>
    public (IReadOnlyList<AgentModelInfo> Shown, IReadOnlyList<AgentModelInfo> Hidden) Apply(IReadOnlyList<AgentModelInfo> models)
    {
        ArgumentNullException.ThrowIfNull(models);
        if (ShowsAll) return (models, []);
        var shown = new List<AgentModelInfo>(models.Count);
        var hidden = new List<AgentModelInfo>();
        foreach (var model in models) (Shows(model.Id) ? shown : hidden).Add(model);
        return hidden.Count == 0 ? (models, []) : (shown, hidden);
    }
}
