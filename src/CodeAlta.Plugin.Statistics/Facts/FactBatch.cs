using System.Globalization;
using System.Text;

namespace CodeAlta.Plugin.Statistics.Facts;

/// <summary>
/// The fixed steps of the distributions: each step is about 19% wider than the one before, from a millisecond to several days
/// (about 110 steps). The bounds are computed with integers so that every machine gives the same steps for the same value, and a
/// step that was stored means the same range for good.
/// </summary>
internal static class HistogramSteps
{
    private static readonly long[] LowerBounds = BuildBounds();

    /// <summary>Gets the number of steps. The last one holds everything above its lower bound.</summary>
    public static int Count => LowerBounds.Length;

    /// <summary>Gets the step a value falls in.</summary>
    /// <param name="value">The value; zero and negative values fall in the first step.</param>
    /// <returns>The step, from 0 to <see cref="Count"/> minus one.</returns>
    public static int StepOf(long value)
    {
        if (value <= LowerBounds[0])
        {
            return 0;
        }

        var low = 0;
        var high = LowerBounds.Length - 1;
        while (low < high)
        {
            var middle = (low + high + 1) >>> 1;
            if (LowerBounds[middle] <= value)
            {
                low = middle;
            }
            else
            {
                high = middle - 1;
            }
        }

        return low;
    }

    /// <summary>Gets the smallest value of a step.</summary>
    /// <param name="step">The step.</param>
    /// <returns>The lower bound, included.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="step"/> is not a step.</exception>
    public static long LowerBound(int step)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(step);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(step, LowerBounds.Length);
        return LowerBounds[step];
    }

    /// <summary>Gets the first value above a step; <see cref="long.MaxValue"/> for the last step.</summary>
    /// <param name="step">The step.</param>
    /// <returns>The upper bound, excluded.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="step"/> is not a step.</exception>
    public static long UpperBound(int step)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(step);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(step, LowerBounds.Length);
        return step + 1 < LowerBounds.Length ? LowerBounds[step + 1] : long.MaxValue;
    }

    private static long[] BuildBounds()
    {
        const long TopBound = 500_000_000;
        var bounds = new List<long> { 1 };
        while (bounds[^1] < TopBound)
        {
            var last = bounds[^1];
            // About 19% wider each step, and always at least one more: the first steps are 1, 2, 3, ...
            bounds.Add(Math.Max(last + 1, ((last * 119) + 99) / 100));
        }

        return [.. bounds];
    }
}

/// <summary>
/// The additions one catch-up of a session makes to the facts: one dictionary per family of facts, each value a sum (or, for a
/// few measures named in their documentation, a largest value). Merging two batches is a plain addition, so reading a session
/// in two halves gives the same facts as reading it at once.
/// </summary>
internal sealed class FactBatch
{
    /// <summary>Initializes a batch for a session.</summary>
    /// <param name="sessionId">The session the facts belong to.</param>
    public FactBatch(string sessionId)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionId);
        SessionId = sessionId;
    }

    /// <summary>Gets the session the facts belong to.</summary>
    public string SessionId { get; }

    /// <summary>Gets the active time and the runs.</summary>
    public Dictionary<ActivityKey, ActivityMeasures> Activity { get; } = [];

    /// <summary>Gets the requests to models.</summary>
    public Dictionary<UsageKey, UsageMeasures> Usage { get; } = [];

    /// <summary>Gets the costs, by unit.</summary>
    public Dictionary<CostKey, CostMeasures> Cost { get; } = [];

    /// <summary>Gets the tool calls.</summary>
    public Dictionary<ToolKey, ToolMeasures> Tools { get; } = [];

    /// <summary>Gets the contents: prompts, answers, reasoning, instructions.</summary>
    public Dictionary<ContentKey, ContentMeasures> Content { get; } = [];

    /// <summary>Gets the counted names.</summary>
    public Dictionary<DetailKey, long> Details { get; } = [];

    /// <summary>Gets the distributions: the number of values in each step.</summary>
    public Dictionary<HistogramKey, long> Histograms { get; } = [];

    /// <summary>Gets the largest values.</summary>
    public Dictionary<ExtremeKey, ExtremeValue> Extremes { get; } = [];

    /// <summary>Gets the runs that changed, by run: the last version of each.</summary>
    public Dictionary<string, RunRow> Runs { get; } = new(StringComparer.Ordinal);

    /// <summary>Gets or sets the session as it is after the catch-up; null when nothing about it changed.</summary>
    public SessionRow? Session { get; set; }

    /// <summary>Gets a value indicating whether the batch adds nothing.</summary>
    public bool IsEmpty
        => Activity.Count == 0 && Usage.Count == 0 && Cost.Count == 0 && Tools.Count == 0 && Content.Count == 0 && Details.Count == 0
            && Histograms.Count == 0 && Extremes.Count == 0 && Runs.Count == 0 && Session is null;

    /// <summary>Gets the measures for a key, added when there are none.</summary>
    /// <param name="key">The key.</param>
    /// <returns>The measures to add to.</returns>
    public ActivityMeasures ActivityFor(ActivityKey key) => GetOrAdd(Activity, key);

    /// <summary>Gets the measures for a key, added when there are none.</summary>
    /// <param name="key">The key.</param>
    /// <returns>The measures to add to.</returns>
    public UsageMeasures UsageFor(UsageKey key) => GetOrAdd(Usage, key);

    /// <summary>Gets the measures for a key, added when there are none.</summary>
    /// <param name="key">The key.</param>
    /// <returns>The measures to add to.</returns>
    public CostMeasures CostFor(CostKey key) => GetOrAdd(Cost, key);

    /// <summary>Gets the measures for a key, added when there are none.</summary>
    /// <param name="key">The key.</param>
    /// <returns>The measures to add to.</returns>
    public ToolMeasures ToolFor(ToolKey key) => GetOrAdd(Tools, key);

    /// <summary>Gets the measures for a key, added when there are none.</summary>
    /// <param name="key">The key.</param>
    /// <returns>The measures to add to.</returns>
    public ContentMeasures ContentFor(ContentKey key) => GetOrAdd(Content, key);

    /// <summary>Counts a name.</summary>
    /// <param name="key">The key.</param>
    /// <param name="count">The count to add; negative to correct an earlier count.</param>
    public void CountDetail(DetailKey key, long count = 1)
    {
        Details.TryGetValue(key, out var current);
        Details[key] = current + count;
    }

    /// <summary>Puts a value in the distribution of a measure.</summary>
    /// <param name="quarter">The quarter hour.</param>
    /// <param name="measure">The measure.</param>
    /// <param name="subject">What the measure is of; empty for none.</param>
    /// <param name="value">The value.</param>
    public void Observe(QuarterHour quarter, HistogramMeasure measure, string subject, long value)
    {
        var key = new HistogramKey(quarter, measure, subject, HistogramSteps.StepOf(value));
        Histograms.TryGetValue(key, out var current);
        Histograms[key] = current + 1;
    }

    /// <summary>Offers a value to the largest values.</summary>
    /// <param name="quarter">The quarter hour.</param>
    /// <param name="measure">The measure.</param>
    /// <param name="subject">What the measure is of; empty for none.</param>
    /// <param name="value">The value.</param>
    /// <param name="runId">The run the value comes from, when there is one.</param>
    /// <param name="at">The time of the record.</param>
    public void Offer(QuarterHour quarter, ExtremeMeasure measure, string subject, long value, string? runId, DateTimeOffset at)
    {
        var key = new ExtremeKey(quarter, measure, subject);
        var candidate = new ExtremeValue(value, SessionId, runId, at);
        Extremes[key] = Extremes.TryGetValue(key, out var current) ? current.Larger(candidate) : candidate;
    }

    /// <summary>Adds the facts of another batch of the same session; its runs and its session replace the ones of this batch.</summary>
    /// <param name="other">The batch that follows this one.</param>
    /// <exception cref="ArgumentNullException"><paramref name="other"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="other"/> belongs to another session.</exception>
    public void Merge(FactBatch other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (!string.Equals(SessionId, other.SessionId, StringComparison.Ordinal))
        {
            throw new ArgumentException("The batches belong to different sessions.", nameof(other));
        }

        foreach (var (key, value) in other.Activity)
        {
            GetOrAdd(Activity, key).Add(value);
        }

        foreach (var (key, value) in other.Usage)
        {
            GetOrAdd(Usage, key).Add(value);
        }

        foreach (var (key, value) in other.Cost)
        {
            GetOrAdd(Cost, key).Add(value);
        }

        foreach (var (key, value) in other.Tools)
        {
            GetOrAdd(Tools, key).Add(value);
        }

        foreach (var (key, value) in other.Content)
        {
            GetOrAdd(Content, key).Add(value);
        }

        foreach (var (key, value) in other.Details)
        {
            CountDetail(key, value);
        }

        foreach (var (key, value) in other.Histograms)
        {
            Histograms.TryGetValue(key, out var current);
            Histograms[key] = current + value;
        }

        foreach (var (key, value) in other.Extremes)
        {
            Extremes[key] = Extremes.TryGetValue(key, out var current) ? current.Larger(value) : value;
        }

        foreach (var (runId, row) in other.Runs)
        {
            Runs[runId] = row;
        }

        if (other.Session is not null)
        {
            Session = other.Session;
        }
    }

    /// <summary>
    /// Writes the batch as sorted text lines, without the rows whose measures are all zero: two batches that add the same
    /// facts give the same text. Used by the tests and the harness.
    /// </summary>
    /// <returns>The text.</returns>
    public string ToCanonicalText()
    {
        var lines = new List<string>();
        foreach (var (key, value) in Activity.Where(static pair => !pair.Value.IsZero))
        {
            lines.Add($"activity {key.Quarter} {key.Provider}/{key.Model}/{key.Effort} {value}");
        }

        foreach (var (key, value) in Usage.Where(static pair => !pair.Value.IsZero))
        {
            lines.Add($"usage {key.Quarter} {key.Provider}/{key.Model}/{key.Effort}/{key.AgentPrompt}/{key.Purpose} {value}");
        }

        foreach (var (key, value) in Cost.Where(static pair => !pair.Value.IsZero))
        {
            lines.Add($"cost {key.Quarter} {key.Provider}/{key.Model}/{key.Unit} {value}");
        }

        foreach (var (key, value) in Tools.Where(static pair => !pair.Value.IsZero))
        {
            lines.Add($"tool {key.Quarter} {key.Provider}/{key.Kind}/{key.Tool} {value}");
        }

        foreach (var (key, value) in Content.Where(static pair => !pair.Value.IsZero))
        {
            lines.Add($"content {key.Quarter} {key.Kind}/{key.Sender}/{key.PromptKind} {value}");
        }

        foreach (var (key, value) in Details.Where(static pair => pair.Value != 0))
        {
            lines.Add($"detail {key.Quarter} {key.List}/{key.Name} {value}");
        }

        foreach (var (key, value) in Histograms.Where(static pair => pair.Value != 0))
        {
            lines.Add($"histogram {key.Quarter} {key.Measure}/{key.Subject}/{key.Step} {value}");
        }

        foreach (var (key, value) in Extremes)
        {
            lines.Add($"extreme {key.Quarter} {key.Measure}/{key.Subject} {value.Value} {value.RunId} {value.At:O}");
        }

        lines.Sort(StringComparer.Ordinal);
        var builder = new StringBuilder();
        foreach (var line in lines)
        {
            builder.AppendLine(line);
        }

        return builder.ToString();
    }

    /// <summary>Writes the runs of the batch as sorted text lines.</summary>
    /// <returns>The text.</returns>
    public string ToCanonicalRunsText()
    {
        var builder = new StringBuilder();
        foreach (var row in Runs.Values.OrderBy(static row => row.Start).ThenBy(static row => row.RunId, StringComparer.Ordinal))
        {
            builder.Append(CultureInfo.InvariantCulture, $"run {row.RunId} {row.Start:O} {row.End:O} {row.Outcome} {row.Sender}/{row.PromptKind} chars={row.PromptChars} words={row.PromptWords} req={row.Requests} tools={row.ToolCalls}/{row.ToolFailures} tokens={row.InputTokens}/{row.OutputTokens} compactions={row.Compactions} answer={row.AnswerChars}/{row.AnswerWords} cost={row.CostUsd:R}/{row.CostCredits:R} {row.Provider}/{row.Model}/{row.Effort}/{row.PermissionMode}")
                .AppendLine();
        }

        return builder.ToString();
    }

    private static TValue GetOrAdd<TKey, TValue>(Dictionary<TKey, TValue> dictionary, TKey key)
        where TKey : notnull
        where TValue : class, new()
    {
        if (!dictionary.TryGetValue(key, out var value))
        {
            value = new TValue();
            dictionary[key] = value;
        }

        return value;
    }
}
