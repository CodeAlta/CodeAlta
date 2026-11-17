namespace CodeAlta.Pseudonymization;

/// <summary>
/// Runs recognizers over a text and keeps one result for each part of it, as the analyzer of Presidio does: what scores
/// under the threshold is dropped, then overlaps are resolved by <see cref="PiiEntities.Priority"/>.
/// </summary>
public sealed class PiiAnalyzer
{
    // The characters a result that loses part of itself to another is trimmed of at its edges.
    private const string Edges = " \t\r\n,;:.()!?«»\"'";

    private readonly IReadOnlyList<PiiRecognizer> _recognizers;
    private readonly double _threshold;

    /// <summary>
    /// The score under which a result is dropped: 0.4, the score Presidio gives a weak pattern beside its words of
    /// context, so that its recognizers find what they are made to find; a weak pattern alone stays under it.
    /// </summary>
    public const double DefaultThreshold = 0.4;

    /// <summary>Creates the analyzer.</summary>
    /// <param name="recognizers">The recognizers it runs, in order.</param>
    /// <param name="threshold">The score under which a result is dropped.</param>
    /// <exception cref="ArgumentNullException"><paramref name="recognizers"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="threshold"/> is not between 0 and 1.</exception>
    public PiiAnalyzer(IEnumerable<PiiRecognizer> recognizers, double threshold = DefaultThreshold)
    {
        ArgumentNullException.ThrowIfNull(recognizers);
        ArgumentOutOfRangeException.ThrowIfLessThan(threshold, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(threshold, 1);
        _recognizers = [.. recognizers];
        _threshold = threshold;
    }

    /// <summary>Finds the personal data of texts.</summary>
    /// <param name="texts">The texts.</param>
    /// <param name="cancellationToken">Stops the analysis.</param>
    /// <returns>For each text, its results, ordered by position and without overlap.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="texts"/> is <see langword="null"/>.</exception>
    public async ValueTask<IReadOnlyList<IReadOnlyList<PiiSpan>>> AnalyzeAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(texts);
        var found = new List<PiiSpan>[texts.Count];
        for (var index = 0; index < texts.Count; index++) found[index] = [];
        if (texts.Count == 0) return [];
        foreach (var recognizer in _recognizers)
        {
            var results = await recognizer.AnalyzeAsync(texts, cancellationToken).ConfigureAwait(false);
            for (var index = 0; index < texts.Count; index++)
            {
                foreach (var span in results[index])
                {
                    if (span.Score >= _threshold && span.Start >= 0 && span.Length > 0 && span.End <= texts[index].Length) found[index].Add(span);
                }
            }
        }

        var resolved = new IReadOnlyList<PiiSpan>[texts.Count];
        for (var index = 0; index < texts.Count; index++) resolved[index] = Resolve(texts[index], found[index]);
        return resolved;
    }

    /// <summary>Finds the personal data of one text.</summary>
    /// <param name="text">The text.</param>
    /// <param name="cancellationToken">Stops the analysis.</param>
    /// <returns>Its results, ordered by position and without overlap.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is <see langword="null"/>.</exception>
    public async ValueTask<IReadOnlyList<PiiSpan>> AnalyzeAsync(string text, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(text);
        return (await AnalyzeAsync([text], cancellationToken).ConfigureAwait(false))[0];
    }

    /// <summary>
    /// Keeps, for each part of a text, the result of the surest kind; at the same rank the longer, then the better scored.
    /// A result that overlaps one already kept is trimmed to what sticks out of it, never dropped whole: a span of a model
    /// that takes "Marie Martin (marie@x.fr)" still hides the name once the email is kept.
    /// </summary>
    /// <param name="text">The text the results are in.</param>
    /// <param name="candidates">The results, overlapping or not.</param>
    /// <returns>The results kept, ordered by position.</returns>
    public static IReadOnlyList<PiiSpan> Resolve(string text, IEnumerable<PiiSpan> candidates)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(candidates);
        var ordered = candidates
            .OrderBy(static span => PiiEntities.Rank(span.Entity))
            .ThenByDescending(static span => span.Length)
            .ThenByDescending(static span => span.Score);
        var kept = new List<PiiSpan>();
        foreach (var candidate in ordered)
        {
            if (kept.All(span => candidate.End <= span.Start || candidate.Start >= span.End))
            {
                kept.Add(candidate);
                continue;
            }

            kept.AddRange(Trim(text, candidate, kept));
        }

        kept.Sort(static (left, right) => left.Start.CompareTo(right.Start));
        return MergeAdjacent(kept);
    }

    private static List<PiiSpan> Trim(string text, PiiSpan candidate, List<PiiSpan> kept)
    {
        var free = new List<(int Start, int End)> { (candidate.Start, candidate.End) };
        foreach (var span in kept)
        {
            var next = new List<(int Start, int End)>();
            foreach (var (start, end) in free)
            {
                if (span.End <= start || span.Start >= end)
                {
                    next.Add((start, end));
                    continue;
                }

                if (start < span.Start) next.Add((start, span.Start));
                if (span.End < end) next.Add((span.End, end));
            }

            free = next;
        }

        var residues = new List<PiiSpan>();
        foreach (var (start, end) in free)
        {
            var first = start;
            var last = end;
            while (first < last && Edges.Contains(text[first], StringComparison.Ordinal)) first++;
            while (last > first && Edges.Contains(text[last - 1], StringComparison.Ordinal)) last--;
            if (last > first) residues.Add(candidate with { Start = first, Length = last - first });
        }

        return residues;
    }

    // A model can cut one mention in two ("avenue de l'" and "Europe"): two results of a named kind that touch, with
    // nothing between them, are one. A space can separate two people ("Sophie MARTIN Paul DURAND"): they stay two.
    private static List<PiiSpan> MergeAdjacent(List<PiiSpan> spans)
    {
        var merged = new List<PiiSpan>(spans.Count);
        foreach (var span in spans)
        {
            if (merged.Count > 0 && merged[^1] is { } previous && previous.End == span.Start
                && previous.Entity == span.Entity && PiiEntities.IsNamed(span.Entity))
            {
                merged[^1] = previous with { Length = span.End - previous.Start, Score = Math.Min(previous.Score, span.Score) };
                continue;
            }

            merged.Add(span);
        }

        return merged;
    }
}
