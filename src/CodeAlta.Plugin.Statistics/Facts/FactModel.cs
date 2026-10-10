using System.Globalization;

namespace CodeAlta.Plugin.Statistics.Facts;

/// <summary>
/// A quarter of an hour of UTC time. Every time zone in use differs from UTC by a whole number of quarter hours, so a local
/// day is an exact sum of quarters wherever the user is.
/// </summary>
/// <param name="Index">The number of quarter hours since 1970-01-01T00:00:00Z.</param>
internal readonly record struct QuarterHour(int Index) : IComparable<QuarterHour>
{
    /// <summary>The length of a quarter hour in milliseconds.</summary>
    public const long Milliseconds = 15 * 60 * 1000;

    /// <summary>Gets the quarter hour that holds a time.</summary>
    /// <param name="time">The time.</param>
    /// <returns>The quarter hour.</returns>
    public static QuarterHour Of(DateTimeOffset time)
        => new((int)Math.Floor(time.ToUnixTimeMilliseconds() / (double)Milliseconds));

    /// <summary>Gets the start of the quarter hour.</summary>
    public DateTimeOffset Start => DateTimeOffset.FromUnixTimeMilliseconds(Index * Milliseconds);

    /// <summary>Gets the start of the next quarter hour.</summary>
    public DateTimeOffset End => DateTimeOffset.FromUnixTimeMilliseconds((Index + 1L) * Milliseconds);

    /// <inheritdoc />
    public int CompareTo(QuarterHour other) => Index.CompareTo(other.Index);

    /// <inheritdoc />
    public override string ToString() => Start.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm'Z'", CultureInfo.InvariantCulture);
}

/// <summary>The fixed list of kinds a tool belongs to.</summary>
internal enum ToolKind : byte
{
    /// <summary>A tool that is none of the others; it keeps its own name.</summary>
    Other,

    /// <summary>A tool that reads, writes, edits, lists or removes files.</summary>
    Files,

    /// <summary>A tool that searches files or tools.</summary>
    Search,

    /// <summary>A shell command.</summary>
    Shell,

    /// <summary>A tool of the web: a fetch or a search.</summary>
    Web,

    /// <summary>The <c>alta</c> live tool.</summary>
    Alta,

    /// <summary>A tool of an MCP server.</summary>
    Mcp,

    /// <summary>The activation of a skill.</summary>
    Skill,
}

/// <summary>Who wrote a prompt.</summary>
internal enum PromptSender : byte
{
    /// <summary>Not a prompt: the content is an answer, a reasoning or the instructions.</summary>
    None,

    /// <summary>A person, from the window or the terminal.</summary>
    You,

    /// <summary>Another agent, through a session of its own.</summary>
    Agent,

    /// <summary>A reminder that fired.</summary>
    Reminder,

    /// <summary>An automation.</summary>
    Automation,

    /// <summary>Another sender: an MCP client, a plugin, a job, the host.</summary>
    Other,
}

/// <summary>How a prompt reached the session.</summary>
internal enum PromptKind : byte
{
    /// <summary>Not a prompt.</summary>
    None,

    /// <summary>A prompt that starts a turn.</summary>
    NewTurn,

    /// <summary>A prompt sent into a run that was going.</summary>
    Steer,

    /// <summary>A prompt that waited in the queue of the session.</summary>
    Queued,

    /// <summary>The answer to a question the agent asked.</summary>
    Answer,
}

/// <summary>The kind of a content the facts count.</summary>
internal enum ContentKind : byte
{
    /// <summary>A prompt.</summary>
    Prompt,

    /// <summary>A text the assistant wrote.</summary>
    Answer,

    /// <summary>A reasoning the model showed.</summary>
    Reasoning,

    /// <summary>A summary of a reasoning.</summary>
    ReasoningSummary,

    /// <summary>The system and developer instructions of the session.</summary>
    Instructions,
}

/// <summary>Why a request was made.</summary>
internal enum UsagePurpose : byte
{
    /// <summary>A request of a turn.</summary>
    Turn,

    /// <summary>A request that compacts the context.</summary>
    Compaction,
}

/// <summary>The lists of names the facts count.</summary>
internal enum DetailList : byte
{
    /// <summary>The program of a shell command: <c>git</c>, <c>dotnet</c>.</summary>
    ShellProgram,

    /// <summary>The first two words of an <c>alta</c> command: <c>session create</c>.</summary>
    AltaCommand,

    /// <summary>The extension of a file a tool changed, without the dot; empty for none.</summary>
    ChangedFileExtension,

    /// <summary>The permission mode a run started in.</summary>
    PermissionMode,

    /// <summary>The trigger of a compaction.</summary>
    CompactionTrigger,

    /// <summary>The sender of the first prompt of a run: <c>you</c>, <c>agent</c>...</summary>
    RunOrigin,

    /// <summary>A skill that was activated.</summary>
    Skill,

    /// <summary>How a session was created: the kind of actor, or <c>root</c> and <c>child</c> by whether it has a parent.</summary>
    SessionOrigin,
}

/// <summary>The measures that have a distribution in fixed steps.</summary>
internal enum HistogramMeasure : byte
{
    /// <summary>The duration of a run in milliseconds.</summary>
    RunDurationMs,

    /// <summary>The duration of a tool call in milliseconds; the subject is the tool.</summary>
    ToolDurationMs,

    /// <summary>The input tokens of a request; the subject is the model.</summary>
    RequestInputTokens,

    /// <summary>The output tokens of a request; the subject is the model.</summary>
    RequestOutputTokens,

    /// <summary>The characters of a prompt of yours.</summary>
    PromptChars,

    /// <summary>The words of a prompt of yours.</summary>
    PromptWords,

    /// <summary>The cost of a run in millionths of its unit; the subject is the unit.</summary>
    RunCostMicro,

    /// <summary>The tool calls of a run.</summary>
    RunToolCalls,
}

/// <summary>The measures of which the facts keep the largest value and where it came from.</summary>
internal enum ExtremeMeasure : byte
{
    /// <summary>The longest run, in milliseconds.</summary>
    LongestRunMs,

    /// <summary>The run with the most tool calls.</summary>
    MostToolCallsInRun,

    /// <summary>The longest tool call, in milliseconds; the subject is the tool.</summary>
    LongestToolMs,

    /// <summary>The fullest context window, in millionths of the limit; the subject is the model.</summary>
    HighestContextFillPpm,

    /// <summary>The request with the most input tokens; the subject is the model.</summary>
    LargestRequestInputTokens,
}

/// <summary>How a run ended.</summary>
internal enum RunOutcome : byte
{
    /// <summary>The run has not ended.</summary>
    Running,

    /// <summary>The run went idle.</summary>
    Completed,

    /// <summary>The run ended with an error.</summary>
    Failed,

    /// <summary>The run has no end: the application stopped during it, or the next run started.</summary>
    Interrupted,
}

/// <summary>The key of the active time and the runs.</summary>
/// <param name="Quarter">The quarter hour.</param>
/// <param name="Provider">The provider, its old names folded.</param>
/// <param name="Model">The model in force.</param>
/// <param name="Effort">The reasoning effort in force; empty when none.</param>
internal readonly record struct ActivityKey(QuarterHour Quarter, string Provider, string Model, string Effort);

/// <summary>The measures of <see cref="ActivityKey"/>: every field is a sum.</summary>
internal sealed class ActivityMeasures
{
    /// <summary>Gets or sets the milliseconds of run time that fall in the quarter.</summary>
    public long ActiveMs { get; set; }

    /// <summary>Gets or sets the runs that started in the quarter.</summary>
    public long RunsStarted { get; set; }

    /// <summary>Gets or sets the runs that completed in the quarter.</summary>
    public long RunsCompleted { get; set; }

    /// <summary>Gets or sets the runs that failed in the quarter.</summary>
    public long RunsFailed { get; set; }

    /// <summary>Gets or sets the runs found without an end, counted in the quarter of the last record they had.</summary>
    public long RunsInterrupted { get; set; }

    /// <summary>Gets or sets the error records of the quarter.</summary>
    public long Errors { get; set; }

    /// <summary>Gets or sets the compactions that completed in the quarter.</summary>
    public long Compactions { get; set; }

    /// <summary>Gets or sets the tokens the compactions of the quarter started from.</summary>
    public long CompactionTokensBefore { get; set; }

    /// <summary>Gets or sets the tokens the compactions of the quarter ended with.</summary>
    public long CompactionTokensAfter { get; set; }

    /// <summary>Adds another set of measures.</summary>
    /// <param name="other">The measures to add.</param>
    public void Add(ActivityMeasures other)
    {
        ActiveMs += other.ActiveMs;
        RunsStarted += other.RunsStarted;
        RunsCompleted += other.RunsCompleted;
        RunsFailed += other.RunsFailed;
        RunsInterrupted += other.RunsInterrupted;
        Errors += other.Errors;
        Compactions += other.Compactions;
        CompactionTokensBefore += other.CompactionTokensBefore;
        CompactionTokensAfter += other.CompactionTokensAfter;
    }

    /// <summary>Gets a value indicating whether every measure is zero.</summary>
    public bool IsZero => ActiveMs == 0 && RunsStarted == 0 && RunsCompleted == 0 && RunsFailed == 0 && RunsInterrupted == 0
        && Errors == 0 && Compactions == 0 && CompactionTokensBefore == 0 && CompactionTokensAfter == 0;

    /// <inheritdoc />
    public override string ToString()
        => $"active={ActiveMs} started={RunsStarted} completed={RunsCompleted} failed={RunsFailed} interrupted={RunsInterrupted} errors={Errors} compactions={Compactions}/{CompactionTokensBefore}/{CompactionTokensAfter}";
}

/// <summary>The key of the requests to models.</summary>
/// <param name="Quarter">The quarter hour.</param>
/// <param name="Provider">The provider, its old names folded.</param>
/// <param name="Model">The model the provider says it used.</param>
/// <param name="Effort">The reasoning effort; empty when none.</param>
/// <param name="AgentPrompt">The agent prompt in force; empty when none.</param>
/// <param name="Purpose">Whether the request was part of a turn or of a compaction.</param>
internal readonly record struct UsageKey(QuarterHour Quarter, string Provider, string Model, string Effort, string AgentPrompt, UsagePurpose Purpose);

/// <summary>The measures of <see cref="UsageKey"/>.</summary>
internal sealed class UsageMeasures
{
    /// <summary>Gets or sets the requests: the <c>UsageUpdated</c> records.</summary>
    public long Requests { get; set; }

    /// <summary>Gets or sets all the input tokens, the cached ones included.</summary>
    public long InputTokens { get; set; }

    /// <summary>Gets or sets the input that was neither read from nor written to the cache.</summary>
    public long FreshInputTokens { get; set; }

    /// <summary>Gets or sets the input read from the cache.</summary>
    public long CacheReadTokens { get; set; }

    /// <summary>Gets or sets the input written to the cache.</summary>
    public long CacheWriteTokens { get; set; }

    /// <summary>Gets or sets the output tokens, the reasoning ones included.</summary>
    public long OutputTokens { get; set; }

    /// <summary>Gets or sets the reasoning tokens, a part of the output.</summary>
    public long ReasoningTokens { get; set; }

    /// <summary>Gets or sets the duration the provider reported, in milliseconds.</summary>
    public long ProviderDurationMs { get; set; }

    /// <summary>Gets or sets the requests that reported a context window.</summary>
    public long ContextSamples { get; set; }

    /// <summary>Gets or sets the sum of the tokens of the context window of those requests.</summary>
    public long ContextTokensSum { get; set; }

    /// <summary>Gets or sets the sum of the limits of the context window of those requests.</summary>
    public long ContextLimitSum { get; set; }

    /// <summary>Gets or sets the sum of the fill of the context window, in millionths of the limit, of those requests.</summary>
    public long ContextFillPpmSum { get; set; }

    /// <summary>Gets or sets the largest fill of the context window, in millionths of the limit. Merged with the larger.</summary>
    public long ContextFillPpmMax { get; set; }

    /// <summary>Adds another set of measures; the largest fill is the larger of the two.</summary>
    /// <param name="other">The measures to add.</param>
    public void Add(UsageMeasures other)
    {
        Requests += other.Requests;
        InputTokens += other.InputTokens;
        FreshInputTokens += other.FreshInputTokens;
        CacheReadTokens += other.CacheReadTokens;
        CacheWriteTokens += other.CacheWriteTokens;
        OutputTokens += other.OutputTokens;
        ReasoningTokens += other.ReasoningTokens;
        ProviderDurationMs += other.ProviderDurationMs;
        ContextSamples += other.ContextSamples;
        ContextTokensSum += other.ContextTokensSum;
        ContextLimitSum += other.ContextLimitSum;
        ContextFillPpmSum += other.ContextFillPpmSum;
        ContextFillPpmMax = Math.Max(ContextFillPpmMax, other.ContextFillPpmMax);
    }

    /// <summary>Gets a value indicating whether every measure is zero.</summary>
    public bool IsZero => Requests == 0 && InputTokens == 0 && FreshInputTokens == 0 && CacheReadTokens == 0 && CacheWriteTokens == 0
        && OutputTokens == 0 && ReasoningTokens == 0 && ProviderDurationMs == 0 && ContextSamples == 0 && ContextTokensSum == 0
        && ContextLimitSum == 0 && ContextFillPpmSum == 0 && ContextFillPpmMax == 0;

    /// <inheritdoc />
    public override string ToString()
        => $"req={Requests} in={InputTokens}/{FreshInputTokens}/{CacheReadTokens}/{CacheWriteTokens} out={OutputTokens} reason={ReasoningTokens} dur={ProviderDurationMs} ctx={ContextSamples}/{ContextTokensSum}/{ContextLimitSum}/{ContextFillPpmSum}/{ContextFillPpmMax}";
}

/// <summary>The key of a cost: costs of different units never add up.</summary>
/// <param name="Quarter">The quarter hour.</param>
/// <param name="Provider">The provider, its old names folded.</param>
/// <param name="Model">The model.</param>
/// <param name="Unit">The unit the provider gives the cost in: <c>usd</c> when it names none, <c>AI credits</c>.</param>
internal readonly record struct CostKey(QuarterHour Quarter, string Provider, string Model, string Unit);

/// <summary>The measures of <see cref="CostKey"/>.</summary>
internal sealed class CostMeasures
{
    /// <summary>Gets or sets the cost, in the unit of the key.</summary>
    public double Total { get; set; }

    /// <summary>Gets or sets the number of costs added.</summary>
    public long Records { get; set; }

    /// <summary>Adds another set of measures.</summary>
    /// <param name="other">The measures to add.</param>
    public void Add(CostMeasures other)
    {
        Total += other.Total;
        Records += other.Records;
    }

    /// <summary>Gets a value indicating whether every measure is zero.</summary>
    public bool IsZero => Total == 0 && Records == 0;

    /// <inheritdoc />
    public override string ToString() => $"cost={Total.ToString("R", CultureInfo.InvariantCulture)} records={Records}";
}

/// <summary>The key of the tool calls.</summary>
/// <param name="Quarter">The quarter hour.</param>
/// <param name="Provider">The provider, its old names folded.</param>
/// <param name="Kind">The kind of the tool.</param>
/// <param name="Tool">The bucket of the tool: <c>shell</c>, or <c>&lt;ActivityKind&gt;:&lt;name&gt;</c> as the cards of the timeline say it.</param>
internal readonly record struct ToolKey(QuarterHour Quarter, string Provider, ToolKind Kind, string Tool);

/// <summary>The measures of <see cref="ToolKey"/>.</summary>
internal sealed class ToolMeasures
{
    /// <summary>Gets or sets the calls started in the quarter.</summary>
    public long Calls { get; set; }

    /// <summary>Gets or sets the calls that failed in the quarter.</summary>
    public long Failures { get; set; }

    /// <summary>Gets or sets the calls that were canceled in the quarter.</summary>
    public long Canceled { get; set; }

    /// <summary>Gets or sets the ended calls whose duration is known.</summary>
    public long DurationCount { get; set; }

    /// <summary>Gets or sets the sum of those durations in milliseconds.</summary>
    public long DurationMsTotal { get; set; }

    /// <summary>Gets or sets the longest duration in milliseconds. Merged with the larger.</summary>
    public long DurationMsMax { get; set; }

    /// <summary>Gets or sets the bytes of the arguments of the calls.</summary>
    public long BytesIn { get; set; }

    /// <summary>Gets or sets the bytes of the results of the calls.</summary>
    public long BytesOut { get; set; }

    /// <summary>Gets or sets the files the calls read.</summary>
    public long FilesRead { get; set; }

    /// <summary>Gets or sets the files the calls changed.</summary>
    public long FilesChanged { get; set; }

    /// <summary>Gets or sets the lines the diffs of the calls added.</summary>
    public long LinesAdded { get; set; }

    /// <summary>Gets or sets the lines the diffs of the calls removed.</summary>
    public long LinesRemoved { get; set; }

    /// <summary>Adds another set of measures; the longest duration is the larger of the two.</summary>
    /// <param name="other">The measures to add.</param>
    public void Add(ToolMeasures other)
    {
        Calls += other.Calls;
        Failures += other.Failures;
        Canceled += other.Canceled;
        DurationCount += other.DurationCount;
        DurationMsTotal += other.DurationMsTotal;
        DurationMsMax = Math.Max(DurationMsMax, other.DurationMsMax);
        BytesIn += other.BytesIn;
        BytesOut += other.BytesOut;
        FilesRead += other.FilesRead;
        FilesChanged += other.FilesChanged;
        LinesAdded += other.LinesAdded;
        LinesRemoved += other.LinesRemoved;
    }

    /// <summary>Gets a value indicating whether every measure is zero.</summary>
    public bool IsZero => Calls == 0 && Failures == 0 && Canceled == 0 && DurationCount == 0 && DurationMsTotal == 0 && DurationMsMax == 0
        && BytesIn == 0 && BytesOut == 0 && FilesRead == 0 && FilesChanged == 0 && LinesAdded == 0 && LinesRemoved == 0;

    /// <inheritdoc />
    public override string ToString()
        => $"calls={Calls} fail={Failures} cancel={Canceled} dur={DurationCount}/{DurationMsTotal}/{DurationMsMax} bytes={BytesIn}/{BytesOut} files={FilesRead}/{FilesChanged} lines=+{LinesAdded}/-{LinesRemoved}";
}

/// <summary>The key of the contents the session wrote or received.</summary>
/// <param name="Quarter">The quarter hour.</param>
/// <param name="Kind">The kind of content.</param>
/// <param name="Sender">Who wrote the prompt; none for the other kinds.</param>
/// <param name="PromptKind">How the prompt reached the session; none for the other kinds.</param>
internal readonly record struct ContentKey(QuarterHour Quarter, ContentKind Kind, PromptSender Sender, PromptKind PromptKind);

/// <summary>The measures of <see cref="ContentKey"/>.</summary>
internal sealed class ContentMeasures
{
    /// <summary>Gets or sets the number of contents.</summary>
    public long Count { get; set; }

    /// <summary>Gets or sets their characters.</summary>
    public long Chars { get; set; }

    /// <summary>Gets or sets their words.</summary>
    public long Words { get; set; }

    /// <summary>Gets or sets the approximate tokens the instructions hold.</summary>
    public long ApproxTokens { get; set; }

    /// <summary>Gets or sets the files attached to the prompts.</summary>
    public long Files { get; set; }

    /// <summary>Gets or sets the folders attached to the prompts.</summary>
    public long Directories { get; set; }

    /// <summary>Gets or sets the images attached to the prompts.</summary>
    public long Images { get; set; }

    /// <summary>Gets or sets the skills attached to the prompts.</summary>
    public long Skills { get; set; }

    /// <summary>Adds another set of measures.</summary>
    /// <param name="other">The measures to add.</param>
    public void Add(ContentMeasures other)
    {
        Count += other.Count;
        Chars += other.Chars;
        Words += other.Words;
        ApproxTokens += other.ApproxTokens;
        Files += other.Files;
        Directories += other.Directories;
        Images += other.Images;
        Skills += other.Skills;
    }

    /// <summary>Subtracts another set of measures, to correct what an earlier read counted under another key.</summary>
    /// <param name="other">The measures to take away.</param>
    public void Subtract(ContentMeasures other)
    {
        Count -= other.Count;
        Chars -= other.Chars;
        Words -= other.Words;
        ApproxTokens -= other.ApproxTokens;
        Files -= other.Files;
        Directories -= other.Directories;
        Images -= other.Images;
        Skills -= other.Skills;
    }

    /// <summary>Gets a value indicating whether every measure is zero.</summary>
    public bool IsZero => Count == 0 && Chars == 0 && Words == 0 && ApproxTokens == 0 && Files == 0 && Directories == 0 && Images == 0 && Skills == 0;

    /// <inheritdoc />
    public override string ToString() => $"count={Count} chars={Chars} words={Words} tokens={ApproxTokens} attach={Files}/{Directories}/{Images}/{Skills}";
}

/// <summary>The key of a counted name.</summary>
/// <param name="Quarter">The quarter hour.</param>
/// <param name="List">The list the name belongs to.</param>
/// <param name="Name">The name.</param>
internal readonly record struct DetailKey(QuarterHour Quarter, DetailList List, string Name);

/// <summary>The key of a step of a distribution.</summary>
/// <param name="Quarter">The quarter hour.</param>
/// <param name="Measure">The measure.</param>
/// <param name="Subject">What the measure is of: a tool, a model, a unit; empty for none.</param>
/// <param name="Step">The step; see <see cref="HistogramSteps"/>.</param>
internal readonly record struct HistogramKey(QuarterHour Quarter, HistogramMeasure Measure, string Subject, int Step);

/// <summary>The key of an extreme value.</summary>
/// <param name="Quarter">The quarter hour.</param>
/// <param name="Measure">The measure.</param>
/// <param name="Subject">What the measure is of; empty for none.</param>
internal readonly record struct ExtremeKey(QuarterHour Quarter, ExtremeMeasure Measure, string Subject);

/// <summary>The largest value of a measure and the session and run it comes from.</summary>
/// <param name="Value">The value.</param>
/// <param name="SessionId">The session.</param>
/// <param name="RunId">The run, when the value belongs to one.</param>
/// <param name="At">The time of the record the value comes from.</param>
internal readonly record struct ExtremeValue(long Value, string SessionId, string? RunId, DateTimeOffset At)
{
    /// <summary>Gets the larger of two values; at a tie, the earlier one.</summary>
    /// <param name="other">The other value.</param>
    /// <returns>The value to keep.</returns>
    public ExtremeValue Larger(ExtremeValue other)
        => other.Value > Value || (other.Value == Value && other.At < At) ? other : this;
}

/// <summary>A run as the table of runs holds it: one row, replaced each time the run changes.</summary>
internal sealed class RunRow
{
    /// <summary>Gets or sets the session.</summary>
    public string SessionId { get; set; } = string.Empty;

    /// <summary>Gets or sets the run.</summary>
    public string RunId { get; set; } = string.Empty;

    /// <summary>Gets or sets the time of the first record of the run, as the file gives them.</summary>
    public DateTimeOffset Start { get; set; }

    /// <summary>Gets or sets the end of the run: its terminal record, or its last record while it has none.</summary>
    public DateTimeOffset End { get; set; }

    /// <summary>
    /// Gets or sets the milliseconds between <see cref="Start"/> and <see cref="End"/> that are not time of the run: the gap before
    /// a record whose time is far from the others.
    /// </summary>
    public long SkippedMs { get; set; }

    /// <summary>Gets or sets how the run ended.</summary>
    public RunOutcome Outcome { get; set; }

    /// <summary>Gets or sets who sent the prompt that started the run.</summary>
    public PromptSender Sender { get; set; }

    /// <summary>Gets or sets how the prompt that started the run reached the session.</summary>
    public PromptKind PromptKind { get; set; }

    /// <summary>Gets or sets the characters of that prompt.</summary>
    public long PromptChars { get; set; }

    /// <summary>Gets or sets the words of that prompt.</summary>
    public long PromptWords { get; set; }

    /// <summary>Gets or sets the requests of the run.</summary>
    public long Requests { get; set; }

    /// <summary>Gets or sets the tool calls of the run.</summary>
    public long ToolCalls { get; set; }

    /// <summary>Gets or sets the tool calls of the run that failed.</summary>
    public long ToolFailures { get; set; }

    /// <summary>Gets or sets the input tokens of the run.</summary>
    public long InputTokens { get; set; }

    /// <summary>Gets or sets the output tokens of the run.</summary>
    public long OutputTokens { get; set; }

    /// <summary>Gets or sets the compactions of the run.</summary>
    public long Compactions { get; set; }

    /// <summary>Gets or sets the characters the assistant wrote during the run.</summary>
    public long AnswerChars { get; set; }

    /// <summary>Gets or sets the words the assistant wrote during the run.</summary>
    public long AnswerWords { get; set; }

    /// <summary>Gets or sets the cost of the run in dollars.</summary>
    public double CostUsd { get; set; }

    /// <summary>Gets or sets the cost of the run in AI credits.</summary>
    public double CostCredits { get; set; }

    /// <summary>Gets or sets the provider at the start of the run.</summary>
    public string Provider { get; set; } = string.Empty;

    /// <summary>Gets or sets the model at the start of the run.</summary>
    public string Model { get; set; } = string.Empty;

    /// <summary>Gets or sets the reasoning effort at the start of the run.</summary>
    public string Effort { get; set; } = string.Empty;

    /// <summary>Gets or sets the permission mode in force when the run started.</summary>
    public string PermissionMode { get; set; } = string.Empty;

    /// <summary>Gets the duration of the run.</summary>
    public TimeSpan Duration => End - Start - TimeSpan.FromMilliseconds(SkippedMs) is var duration && duration > TimeSpan.Zero ? duration : TimeSpan.Zero;
}

/// <summary>A session as the table of sessions holds it: one row, replaced each time the session changes.</summary>
internal sealed class SessionRow
{
    /// <summary>Gets or sets the session.</summary>
    public string SessionId { get; set; } = string.Empty;

    /// <summary>Gets or sets the project the session was created for.</summary>
    public string? ProjectRef { get; set; }

    /// <summary>Gets or sets the kind of session.</summary>
    public string? SessionKind { get; set; }

    /// <summary>Gets or sets the parent of a sub-agent session.</summary>
    public string? ParentSessionId { get; set; }

    /// <summary>Gets or sets the kind of actor that created the session.</summary>
    public string? CreatedByKind { get; set; }

    /// <summary>Gets or sets the session of the agent that created the session.</summary>
    public string? CreatedBySessionId { get; set; }

    /// <summary>Gets or sets the automation that created the session.</summary>
    public string? AutomationId { get; set; }

    /// <summary>Gets or sets the title the header gave, in the form it is kept in (<see cref="SessionTitles.Clean"/>).</summary>
    public string? Title { get; set; }

    /// <summary>Gets or sets the provider the session was created with, its old name folded.</summary>
    public string? Provider { get; set; }

    /// <summary>Gets or sets the permission mode in force at the last state record.</summary>
    public string? PermissionMode { get; set; }

    /// <summary>Gets or sets the time of the earliest record.</summary>
    public DateTimeOffset? FirstRecord { get; set; }

    /// <summary>Gets or sets the time of the latest record.</summary>
    public DateTimeOffset? LastRecord { get; set; }

    /// <summary>Gets or sets a value indicating whether a header was read.</summary>
    public bool HasHeader { get; set; }
}
