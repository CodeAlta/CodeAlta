using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodeAlta.Plugin.Statistics.Facts;

/// <summary>
/// What a catch-up of a session must carry to the next one for the facts to come out as if the journal had been read at once:
/// the model in force, the runs that are going, the tool calls that have not ended, the last prompts that a later record may
/// still describe. It is small and it serializes to JSON, so the store can keep it beside the offset.
/// </summary>
internal sealed class SessionFactsState
{
    /// <summary>
    /// The version of the facts: a state of another version is not used, and the session is read again. Version 2 keeps no variable
    /// of a shell command as its program and no working directory, and counts every cost that has no duration.
    /// </summary>
    public const int CurrentVersion = 2;

    /// <summary>The number of runs whose end is remembered, so that a late record of a run that ended does not open it again.</summary>
    public const int MaxClosedRuns = 32;

    /// <summary>The number of tool calls without an end that are remembered.</summary>
    public const int MaxOpenTools = 512;

    /// <summary>The number of recent prompts a provenance entry may still describe.</summary>
    public const int MaxRecentPrompts = 8;

    /// <summary>The number of provenance entries that wait for their prompt.</summary>
    public const int MaxUnmatchedProvenance = 64;

    /// <summary>The number of prompt identifiers that are remembered as seen.</summary>
    public const int MaxSeenPrompts = 2048;

    /// <summary>The number of runs closed for a session that went quiet that are remembered, so that a run that goes on is opened again.</summary>
    public const int MaxSettledRuns = 4;

    /// <summary>Gets or sets the version of the facts this state belongs to.</summary>
    public int Version { get; set; } = CurrentVersion;

    /// <summary>Gets or sets a value indicating whether a header was read.</summary>
    public bool HasHeader { get; set; }

    /// <summary>Gets or sets the project the session was created for.</summary>
    public string? ProjectRef { get; set; }

    /// <summary>Gets or sets the kind of session.</summary>
    public string? SessionKind { get; set; }

    /// <summary>Gets or sets the parent session.</summary>
    public string? ParentSessionId { get; set; }

    /// <summary>Gets or sets the kind of actor that created the session.</summary>
    public string? CreatedByKind { get; set; }

    /// <summary>Gets or sets the session of the agent that created the session.</summary>
    public string? CreatedBySessionId { get; set; }

    /// <summary>Gets or sets the automation that created the session.</summary>
    public string? AutomationId { get; set; }

    /// <summary>Gets or sets the title.</summary>
    public string? Title { get; set; }

    /// <summary>Gets or sets the provider the session was created with.</summary>
    public string? InitialProvider { get; set; }

    /// <summary>Gets or sets a value indicating whether the origin of the session was counted.</summary>
    public bool OriginCounted { get; set; }

    /// <summary>Gets or sets the time of the earliest record.</summary>
    public DateTimeOffset? FirstRecord { get; set; }

    /// <summary>Gets or sets the time of the latest record.</summary>
    public DateTimeOffset? LastRecord { get; set; }

    /// <summary>Gets or sets the provider in force.</summary>
    public string Provider { get; set; } = string.Empty;

    /// <summary>Gets or sets the model in force.</summary>
    public string Model { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the models the providers reported for the models that were chosen: a session that chose <c>opus</c> and was
    /// told the request ran on <c>claude-opus-5-5</c> counts under the second name from then on.
    /// </summary>
    public Dictionary<string, string> ModelAliases { get; set; } = [];

    /// <summary>Gets or sets the reasoning effort in force.</summary>
    public string Effort { get; set; } = string.Empty;

    /// <summary>Gets or sets the agent prompt in force.</summary>
    public string AgentPrompt { get; set; } = string.Empty;

    /// <summary>Gets or sets the permission mode in force.</summary>
    public string PermissionMode { get; set; } = string.Empty;

    /// <summary>Gets or sets the runs that have not ended.</summary>
    public List<OpenRunState> OpenRuns { get; set; } = [];

    /// <summary>Gets or sets the identifiers of the runs that ended last.</summary>
    public List<string> ClosedRuns { get; set; } = [];

    /// <summary>
    /// Gets or sets the runs that were closed as interrupted because their session had been quiet for long, as they were then: a
    /// later record of one of them opens it again, and what its closing counted is taken back. Emptied when another run starts.
    /// </summary>
    public List<SettledRunState> SettledRuns { get; set; } = [];

    /// <summary>Gets or sets the tool calls that started and have not ended.</summary>
    public List<OpenToolState> OpenTools { get; set; } = [];

    /// <summary>Gets or sets the last prompts.</summary>
    public List<RecentPromptState> RecentPrompts { get; set; } = [];

    /// <summary>Gets or sets the provenance entries that no prompt matched yet.</summary>
    public List<PendingProvenanceState> UnmatchedProvenance { get; set; } = [];

    /// <summary>Gets or sets the hashes of the prompt identifiers already taken from the state records, oldest first.</summary>
    public List<ulong> SeenPrompts { get; set; } = [];

    /// <summary>Makes an independent copy of the state, so that a catch-up that is not saved leaves the saved one as it was.</summary>
    /// <returns>The copy.</returns>
    public SessionFactsState Clone() => FromUtf8Json(ToUtf8Json());

    /// <summary>Serializes the state to UTF-8 JSON.</summary>
    /// <returns>The JSON.</returns>
    public byte[] ToUtf8Json() => JsonSerializer.SerializeToUtf8Bytes(this, StatisticsJsonContext.Default.SessionFactsState);

    /// <summary>Serializes the state to JSON text.</summary>
    /// <returns>The JSON.</returns>
    public string ToJson() => JsonSerializer.Serialize(this, StatisticsJsonContext.Default.SessionFactsState);

    /// <summary>Reads a state from JSON.</summary>
    /// <param name="json">The JSON.</param>
    /// <returns>The state; a fresh one when the JSON is not a state.</returns>
    public static SessionFactsState FromJson(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        try
        {
            return JsonSerializer.Deserialize(json, StatisticsJsonContext.Default.SessionFactsState) ?? new SessionFactsState { Version = 0 };
        }
        catch (JsonException)
        {
            return new SessionFactsState { Version = 0 };
        }
    }

    /// <summary>Reads a state from UTF-8 JSON.</summary>
    /// <param name="json">The JSON.</param>
    /// <returns>The state; a state of version 0, which is never used, when the JSON is not a state.</returns>
    public static SessionFactsState FromUtf8Json(ReadOnlySpan<byte> json)
    {
        try
        {
            return JsonSerializer.Deserialize(json, StatisticsJsonContext.Default.SessionFactsState) ?? new SessionFactsState { Version = 0 };
        }
        catch (JsonException)
        {
            return new SessionFactsState { Version = 0 };
        }
    }
}

/// <summary>A run that has not ended.</summary>
internal sealed class OpenRunState
{
    /// <summary>Gets or sets the identifier of the run.</summary>
    public string RunId { get; set; } = string.Empty;

    /// <summary>Gets or sets the time of the first record of the run, in the order of the file.</summary>
    public DateTimeOffset Start { get; set; }

    /// <summary>Gets or sets the time the active time was counted up to: the latest time of a record of the run.</summary>
    public DateTimeOffset AccountedTo { get; set; }

    /// <summary>Gets or sets the ticks of active time that have not made a whole millisecond yet.</summary>
    public long RemainderTicks { get; set; }

    /// <summary>Gets or sets the provider at the start of the run.</summary>
    public string Provider { get; set; } = string.Empty;

    /// <summary>Gets or sets the model at the start of the run.</summary>
    public string Model { get; set; } = string.Empty;

    /// <summary>Gets or sets the reasoning effort at the start of the run.</summary>
    public string Effort { get; set; } = string.Empty;

    /// <summary>Gets or sets the permission mode at the start of the run.</summary>
    public string PermissionMode { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether the first prompt of the run was seen.</summary>
    public bool HasPrompt { get; set; }

    /// <summary>Gets or sets who sent the first prompt.</summary>
    public PromptSender Sender { get; set; }

    /// <summary>Gets or sets how the first prompt reached the session.</summary>
    public PromptKind PromptKind { get; set; }

    /// <summary>Gets or sets the characters of the first prompt.</summary>
    public long PromptChars { get; set; }

    /// <summary>Gets or sets the words of the first prompt.</summary>
    public long PromptWords { get; set; }

    /// <summary>Gets or sets the requests of the run.</summary>
    public long Requests { get; set; }

    /// <summary>Gets or sets the tool calls of the run.</summary>
    public long ToolCalls { get; set; }

    /// <summary>Gets or sets the failed tool calls of the run.</summary>
    public long ToolFailures { get; set; }

    /// <summary>Gets or sets the input tokens of the run.</summary>
    public long InputTokens { get; set; }

    /// <summary>Gets or sets the output tokens of the run.</summary>
    public long OutputTokens { get; set; }

    /// <summary>Gets or sets the compactions of the run.</summary>
    public long Compactions { get; set; }

    /// <summary>Gets or sets the characters the assistant wrote.</summary>
    public long AnswerChars { get; set; }

    /// <summary>Gets or sets the words the assistant wrote.</summary>
    public long AnswerWords { get; set; }

    /// <summary>Gets or sets the cost of the run in dollars.</summary>
    public double CostUsd { get; set; }

    /// <summary>Gets or sets the cost of the run in AI credits.</summary>
    public double CostCredits { get; set; }

    /// <summary>Gets or sets the cost of the last costed record, to tell a cost the provider repeats from a new one.</summary>
    public double? LastCost { get; set; }

    /// <summary>Gets or sets the duration of the last costed record.</summary>
    public double? LastCostDuration { get; set; }
}

/// <summary>A run that was closed as interrupted because its session had been quiet for long, with what its closing was counted under.</summary>
internal sealed class SettledRunState
{
    /// <summary>Gets or sets the run as it was when it was closed.</summary>
    public OpenRunState Run { get; set; } = new();

    /// <summary>Gets or sets the time the run was closed at: its last record.</summary>
    public DateTimeOffset End { get; set; }

    /// <summary>Gets or sets the provider the interruption was counted for.</summary>
    public string Provider { get; set; } = string.Empty;

    /// <summary>Gets or sets the model the interruption was counted for.</summary>
    public string Model { get; set; } = string.Empty;

    /// <summary>Gets or sets the reasoning effort the interruption was counted for.</summary>
    public string Effort { get; set; } = string.Empty;
}

/// <summary>A tool call that started and has not ended.</summary>
internal sealed class OpenToolState
{
    /// <summary>Gets or sets the identifier of the call.</summary>
    public string ActivityId { get; set; } = string.Empty;

    /// <summary>Gets or sets the time the call started.</summary>
    public DateTimeOffset Start { get; set; }
}

/// <summary>A prompt a later record of the state may still describe.</summary>
internal sealed class RecentPromptState
{
    /// <summary>Gets or sets the time of the prompt.</summary>
    public DateTimeOffset At { get; set; }

    /// <summary>Gets or sets the run of the prompt.</summary>
    public string? RunId { get; set; }

    /// <summary>Gets or sets who the prompt was counted for.</summary>
    public PromptSender Sender { get; set; }

    /// <summary>Gets or sets how the prompt was counted.</summary>
    public PromptKind Kind { get; set; }

    /// <summary>Gets or sets a value indicating whether the prompt answers a question.</summary>
    public bool IsAnswer { get; set; }

    /// <summary>Gets or sets a value indicating whether the prompt came with the session of the agent that sent it.</summary>
    public bool HasSourceSession { get; set; }

    /// <summary>Gets or sets a value indicating whether the prompt was sent into a run that was going.</summary>
    public bool DetectedSteer { get; set; }

    /// <summary>Gets or sets a value indicating whether a provenance entry already described the prompt.</summary>
    public bool Bound { get; set; }

    /// <summary>Gets or sets a value indicating whether it is the first prompt of its run.</summary>
    public bool StartsRun { get; set; }

    /// <summary>Gets or sets the characters of the prompt.</summary>
    public long Chars { get; set; }

    /// <summary>Gets or sets the words of the prompt.</summary>
    public long Words { get; set; }

    /// <summary>Gets or sets the files attached.</summary>
    public int Files { get; set; }

    /// <summary>Gets or sets the folders attached.</summary>
    public int Directories { get; set; }

    /// <summary>Gets or sets the images attached.</summary>
    public int Images { get; set; }

    /// <summary>Gets or sets the skills attached.</summary>
    public int Skills { get; set; }
}

/// <summary>A prompt provenance entry that no prompt matched yet.</summary>
internal sealed class PendingProvenanceState
{
    /// <summary>Gets or sets the hash of the identifier of the prompt.</summary>
    public ulong IdHash { get; set; }

    /// <summary>Gets or sets the dispatch kind.</summary>
    public string? DispatchKind { get; set; }

    /// <summary>Gets or sets the run the prompt started or joined.</summary>
    public string? RunId { get; set; }

    /// <summary>Gets or sets a value indicating whether the prompt was queued.</summary>
    public bool Queued { get; set; }

    /// <summary>Gets or sets the kind of actor that submitted the prompt.</summary>
    public string? ActorKind { get; set; }

    /// <summary>Gets or sets the session of the agent that submitted the prompt.</summary>
    public string? ActorSessionId { get; set; }

    /// <summary>Gets or sets the creation time of the entry.</summary>
    public DateTimeOffset? CreatedAt { get; set; }
}

[JsonSourceGenerationOptions(
    UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(SessionFactsState))]
internal sealed partial class StatisticsJsonContext : JsonSerializerContext;
