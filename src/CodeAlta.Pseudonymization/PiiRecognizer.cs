using System.Text.RegularExpressions;

namespace CodeAlta.Pseudonymization;

/// <summary>A piece of personal data found in a text.</summary>
/// <param name="Entity">Its kind (<see cref="PiiEntities"/>).</param>
/// <param name="Start">Where it starts in the text.</param>
/// <param name="Length">Its length.</param>
/// <param name="Score">How sure the recognizer is, from 0 to 1.</param>
/// <param name="Recognizer">The recognizer that found it.</param>
public sealed record PiiSpan(string Entity, int Start, int Length, double Score, string Recognizer)
{
    /// <summary>Gets where it ends in the text.</summary>
    public int End => Start + Length;
}

/// <summary>Finds personal data of some kinds in a text.</summary>
public abstract class PiiRecognizer
{
    /// <summary>Gets the name of the recognizer, as its results name it.</summary>
    public abstract string Name { get; }

    /// <summary>Finds the personal data of a text.</summary>
    /// <param name="text">The text.</param>
    /// <param name="cancellationToken">Stops the analysis.</param>
    /// <returns>What it found, overlapping or not.</returns>
    public abstract ValueTask<IReadOnlyList<PiiSpan>> AnalyzeAsync(string text, CancellationToken cancellationToken);

    /// <summary>Finds the personal data of several texts, in one call for a recognizer that works in batches.</summary>
    /// <param name="texts">The texts.</param>
    /// <param name="cancellationToken">Stops the analysis.</param>
    /// <returns>What it found in each text, in order.</returns>
    public virtual async ValueTask<IReadOnlyList<IReadOnlyList<PiiSpan>>> AnalyzeAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(texts);
        var results = new IReadOnlyList<PiiSpan>[texts.Count];
        for (var index = 0; index < texts.Count; index++)
        {
            results[index] = await AnalyzeAsync(texts[index], cancellationToken).ConfigureAwait(false);
        }

        return results;
    }
}

/// <summary>
/// Finds personal data by a pattern, as the pattern recognizers of Presidio do: a match has a score, which words of
/// context around it raise; a validation (a key) can turn it down, or make it suspect when words of context name it.
/// </summary>
public sealed class PatternRecognizer : PiiRecognizer
{
    private readonly string _entity;
    private readonly Regex _pattern;
    private readonly double _score;

    /// <summary>Creates the recognizer.</summary>
    /// <param name="entity">The kind of what it finds.</param>
    /// <param name="pattern">The pattern of what it finds.</param>
    /// <param name="score">The score of a match without context.</param>
    /// <exception cref="ArgumentException"><paramref name="entity"/> is blank, or <paramref name="score"/> is not between 0 and 1.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="pattern"/> is <see langword="null"/>.</exception>
    public PatternRecognizer(string entity, Regex pattern, double score)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entity);
        ArgumentNullException.ThrowIfNull(pattern);
        if (score is < 0 or > 1) throw new ArgumentException("A score is between 0 and 1.", nameof(score));
        _entity = entity;
        _pattern = pattern;
        _score = score;
    }

    /// <inheritdoc />
    public override string Name => "pattern:" + _entity;

    /// <summary>Gets the group of the pattern that holds the value; 0 for the whole match.</summary>
    public int Group { get; init; }

    /// <summary>Gets what checks a match (its key); a match it turns down is dropped, or is suspect in context.</summary>
    public Func<string, bool>? Validate { get; init; }

    /// <summary>Gets the words that raise the score of a match, on its line or the line before.</summary>
    public Regex? Context { get; init; }

    /// <summary>Gets how much the words of context raise the score.</summary>
    public double ContextBoost { get; init; } = 0.35;

    /// <summary>Gets whether a match counts only beside the words of context.</summary>
    public bool ContextRequired { get; init; }

    /// <summary>Gets the kind of a match whose validation fails beside the words of context; null drops it.</summary>
    public string? SuspectEntity { get; init; }

    /// <summary>Gets the score of a suspect match.</summary>
    public double SuspectScore { get; init; } = 0.7;

    /// <inheritdoc />
    public override ValueTask<IReadOnlyList<PiiSpan>> AnalyzeAsync(string text, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(text);
        List<PiiSpan>? spans = null;
        foreach (Match match in _pattern.Matches(text))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var group = match.Groups[Group];
            if (!group.Success || group.Length == 0) continue;
            var inContext = Context is not null && HasContext(text, group.Index, group.Length, Context);
            if (ContextRequired && !inContext) continue;
            string entity;
            double score;
            if (Validate is not null && !Validate(group.Value))
            {
                if (SuspectEntity is null || !inContext) continue;
                (entity, score) = (SuspectEntity, SuspectScore);
            }
            else
            {
                (entity, score) = (_entity, Math.Min(1, _score + (inContext && !ContextRequired ? ContextBoost : 0)));
            }

            (spans ??= []).Add(new PiiSpan(entity, group.Index, group.Length, score, Name));
        }

        return ValueTask.FromResult<IReadOnlyList<PiiSpan>>(spans ?? (IReadOnlyList<PiiSpan>)[]);
    }

    // The words of context are on the line of the match, before or after it, or on the line before when that one is not
    // blank: the label of a field of a form is beside its value or above it. A blank line ends a section.
    internal static bool HasContext(string text, int start, int length, Regex context)
    {
        var lineStart = start == 0 ? 0 : text.LastIndexOf('\n', start - 1) + 1;
        var lineEnd = text.IndexOf('\n', start + length);
        if (lineEnd < 0) lineEnd = text.Length;
        var windowStart = lineStart;
        if (lineStart > 1)
        {
            var previousStart = text.LastIndexOf('\n', lineStart - 2) + 1;
            if (!text.AsSpan(previousStart, lineStart - 1 - previousStart).IsWhiteSpace()) windowStart = previousStart;
        }

        return context.IsMatch(text.AsSpan(windowStart, start - windowStart)) || context.IsMatch(text.AsSpan(start + length, lineEnd - start - length));
    }
}
