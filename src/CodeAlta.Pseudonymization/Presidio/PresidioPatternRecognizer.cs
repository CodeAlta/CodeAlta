using System.Text.RegularExpressions;

namespace CodeAlta.Pseudonymization.Presidio;

/// <summary>A pattern of a recognizer ported from Presidio: its name, its regular expression and its score.</summary>
/// <param name="Name">The name Presidio gives it, such as <c>SSN5 (medium)</c>.</param>
/// <param name="Regex">The regular expression, as Presidio writes it.</param>
/// <param name="Score">The score of a match before its validation and its context.</param>
public sealed record PresidioPattern(string Name, string Regex, double Score);

/// <summary>
/// A pattern recognizer that works as the <c>PatternRecognizer</c> of Presidio (MIT, Microsoft) does, so that its
/// predefined recognizers are ported as they are: each pattern has its score; a validation that passes raises it to 1
/// and one that fails drops the match; an invalidation drops it; then words of context among the words before the match
/// raise the score by 0.35, to at least 0.4.
/// </summary>
/// <remarks>
/// Presidio compiles its patterns case-insensitive, multiline and with the dot matching new lines; they are compiled so
/// here. Its context is the lemmas of the five words before a match, found by spaCy; here it is the five words before it
/// that are no stop words, compared lowercase as substrings, as Presidio's default does, and a context word of several
/// words is looked for in those words joined.
/// </remarks>
public class PresidioPatternRecognizer : PiiRecognizer
{
    /// <summary>The flags Presidio compiles its patterns with.</summary>
    public const RegexOptions PresidioFlags = RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Singleline | RegexOptions.CultureInvariant;

    /// <summary>How much words of context raise a score.</summary>
    public const double ContextFactor = 0.35;

    /// <summary>The score a match with words of context has at least.</summary>
    public const double MinimumScoreWithContext = 0.4;

    private readonly (PresidioPattern Pattern, Regex Regex)[] _patterns;

    /// <summary>Creates the recognizer.</summary>
    /// <param name="entity">The kind of what it finds.</param>
    /// <param name="patterns">Its patterns.</param>
    /// <param name="context">Its words of context; empty for none.</param>
    /// <param name="flags">The flags of its patterns; Presidio's by default.</param>
    /// <exception cref="ArgumentException"><paramref name="entity"/> is blank, or there is no pattern.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="patterns"/> or <paramref name="context"/> is <see langword="null"/>.</exception>
    public PresidioPatternRecognizer(string entity, IReadOnlyList<PresidioPattern> patterns, IReadOnlyList<string> context, RegexOptions flags = PresidioFlags)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entity);
        ArgumentNullException.ThrowIfNull(patterns);
        ArgumentNullException.ThrowIfNull(context);
        if (patterns.Count == 0) throw new ArgumentException("A recognizer has at least one pattern.", nameof(patterns));
        Entity = entity;
        Context = [.. context.Select(static word => word.ToLowerInvariant())];
        _patterns = [.. patterns.Select(pattern => (pattern, new Regex(pattern.Regex, flags, TimeSpan.FromSeconds(5))))];
    }

    /// <summary>Gets the kind of what it finds.</summary>
    public string Entity { get; }

    /// <summary>Gets its words of context, lowercase.</summary>
    public IReadOnlyList<string> Context { get; }

    /// <inheritdoc />
    public override string Name => "presidio:" + Entity;

    /// <summary>
    /// Gets what checks a match, as <c>validate_result</c> does: true raises its score to 1, false drops it, null keeps it.
    /// </summary>
    public Func<string, bool?>? Validate { get; init; }

    /// <summary>Gets what drops a match, as <c>invalidate_result</c> does: true drops it.</summary>
    public Func<string, bool>? Invalidate { get; init; }

    /// <inheritdoc />
    public override ValueTask<IReadOnlyList<PiiSpan>> AnalyzeAsync(string text, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(text);
        var raw = AnalyzeRaw(text, cancellationToken);
        if (raw.Count == 0 || Context.Count == 0) return ValueTask.FromResult(raw);
        var enhanced = new List<PiiSpan>(raw.Count);
        foreach (var span in raw)
            enhanced.Add(span.Score < 1 && PresidioContext.Supports(text, span.Start, Context) ? span with { Score = Math.Min(1, Math.Max(span.Score + ContextFactor, MinimumScoreWithContext)) } : span);
        return ValueTask.FromResult<IReadOnlyList<PiiSpan>>(enhanced);
    }

    /// <summary>Finds the matches and scores them without their context, as the recognizers of Presidio do alone.</summary>
    /// <param name="text">The text.</param>
    /// <param name="cancellationToken">Stops the analysis.</param>
    /// <returns>The matches, the best one of each span.</returns>
    /// <exception cref="OperationCanceledException">The analysis was stopped.</exception>
    public virtual IReadOnlyList<PiiSpan> AnalyzeRaw(string text, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(text);
        List<PiiSpan>? spans = null;
        foreach (var (pattern, regex) in _patterns)
        {
            foreach (Match match in regex.Matches(text))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (match.Length == 0) continue;
                var score = Score(match.Value, pattern.Score);
                if (score > 0) (spans ??= []).Add(new PiiSpan(Entity, match.Index, match.Length, score, Name));
            }
        }

        return spans is null ? [] : RemoveDuplicates(spans);
    }

    /// <summary>Scores a match as Presidio does: its validation, then its invalidation.</summary>
    /// <param name="value">The text of the match.</param>
    /// <param name="score">The score of its pattern.</param>
    /// <returns>Its score; 0 drops it.</returns>
    protected double Score(string value, double score)
    {
        if (Validate?.Invoke(value) is { } valid) score = valid ? 1 : 0;
        return Invalidate?.Invoke(value) == true ? 0 : score;
    }

    /// <summary>
    /// Drops the results a result of the same kind contains with a score at least as high, as <c>remove_duplicates</c>
    /// does for the results of one recognizer: the same span found twice is kept once, with its best score.
    /// </summary>
    /// <param name="spans">The results.</param>
    /// <returns>The results kept, by position.</returns>
    protected static IReadOnlyList<PiiSpan> RemoveDuplicates(IReadOnlyList<PiiSpan> spans)
    {
        var ordered = spans.Where(static span => span.Score > 0).OrderBy(static span => span.Entity, StringComparer.Ordinal)
            .ThenBy(static span => span.Start).ThenByDescending(static span => span.Length).ThenByDescending(static span => span.Score).ToList();
        var kept = new List<PiiSpan>(ordered.Count);
        foreach (var span in ordered)
        {
            if (kept.Any(other => other.Entity == span.Entity && other.Start <= span.Start && span.End <= other.End && span.Score <= other.Score)) continue;
            kept.Add(span);
        }

        return [.. kept.OrderBy(static span => span.Start)];
    }
}
