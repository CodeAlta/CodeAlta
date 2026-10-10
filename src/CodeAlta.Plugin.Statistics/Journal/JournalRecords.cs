using CodeAlta.Agent;

namespace CodeAlta.Plugin.Statistics.Journal;

/// <summary>The kind of a journal record, as far as statistics tell them apart.</summary>
internal enum JournalRecordKind : byte
{
    /// <summary>The shape of the line is not known.</summary>
    Unknown,

    /// <summary>A record statistics do not read: only its time and its run are kept.</summary>
    Touch,

    /// <summary>The header of the session (<c>codealta.sessionHeader</c> or the legacy <c>codealta.threadHeader</c>).</summary>
    Header,

    /// <summary>The state of the session (<c>codealta.sessionState</c> or the legacy <c>codealta.threadState</c>).</summary>
    State,

    /// <summary>A phase of a tool call.</summary>
    Tool,

    /// <summary>A prompt: a user content.</summary>
    UserContent,

    /// <summary>An assistant, reasoning or reasoning summary content.</summary>
    Content,

    /// <summary>A <c>sessionUpdate</c> of kind <c>UsageUpdated</c>: one request to a model.</summary>
    Usage,

    /// <summary>A <c>sessionUpdate</c> of kind <c>ModelChanged</c>.</summary>
    ModelChanged,

    /// <summary>The end of a run: <c>Idle</c> or <c>error</c>.</summary>
    RunEnd,

    /// <summary>A <c>sessionUpdate</c> of kind <c>CompactionCompleted</c>.</summary>
    Compaction,

    /// <summary>A <c>system_prompt</c> record.</summary>
    SystemPrompt,

}

/// <summary>Base of the records the reader hands to its sink. Only the cheap, shared fields live here.</summary>
internal abstract class JournalRecord
{
    /// <summary>Gets the kind of the record.</summary>
    public abstract JournalRecordKind Kind { get; }

    /// <summary>Gets or sets the UTC time of the record, or <see langword="null"/> when the line had none.</summary>
    public DateTimeOffset? Timestamp { get; set; }

    /// <summary>Gets or sets the run the record belongs to, when it carries one.</summary>
    public string? RunId { get; set; }

    /// <summary>Gets or sets the provider key the record was written with (<c>backendId</c>), as written.</summary>
    public string? Provider { get; set; }

    /// <summary>Gets or sets the offset of the first byte of the line in the file.</summary>
    public long Offset { get; set; }

    /// <summary>Gets or sets the length of the line in bytes, without its line end.</summary>
    public long Length { get; set; }

    /// <summary>Gets or sets a value indicating whether the line was larger than the bound the reader parses: only its start and its end were read.</summary>
    public bool Oversize { get; set; }
}

/// <summary>A record whose body statistics do not read: its time and its run are kept.</summary>
internal sealed class TouchRecord : JournalRecord
{
    /// <inheritdoc />
    public override JournalRecordKind Kind => JournalRecordKind.Touch;
}

/// <summary>The identity of a session, from its header.</summary>
internal sealed class HeaderRecord : JournalRecord
{
    /// <inheritdoc />
    public override JournalRecordKind Kind => JournalRecordKind.Header;

    /// <summary>Gets or sets a value indicating whether the header is the legacy <c>codealta.threadHeader</c>.</summary>
    public bool IsLegacy { get; set; }

    /// <summary>Gets or sets the kind of session (<c>ProjectSession</c>, <c>ProjectThread</c>, ...).</summary>
    public string? SessionKind { get; set; }

    /// <summary>Gets or sets the identifier of the project the session was created for.</summary>
    public string? ProjectRef { get; set; }

    /// <summary>Gets or sets the parent session of a sub-agent session.</summary>
    public string? ParentSessionId { get; set; }

    /// <summary>Gets or sets the actor that created the session.</summary>
    public JournalActor? CreatedBy { get; set; }

    /// <summary>Gets or sets the working directory of the session.</summary>
    public string? WorkingDirectory { get; set; }

    /// <summary>Gets or sets the title of the session at its creation.</summary>
    public string? Title { get; set; }

    /// <summary>Gets or sets the provider key the session was created with.</summary>
    public string? ProviderKey { get; set; }

    /// <summary>Gets or sets the creation time recorded by the header.</summary>
    public DateTimeOffset? CreatedAt { get; set; }
}

/// <summary>An actor (<c>AltaActorProvenance</c>): who created a session or submitted a prompt.</summary>
internal sealed class JournalActor
{
    /// <summary>Gets or sets the actor kind: <c>user</c>, <c>agent</c>, <c>host</c>, <c>plugin</c>, <c>automation</c>, <c>reminder</c>, <c>mcp</c>, <c>job</c>...</summary>
    public string? Kind { get; set; }

    /// <summary>Gets or sets the session of the agent that acted, when there is one.</summary>
    public string? SourceSessionId { get; set; }

    /// <summary>Gets or sets the identifier of the automation, for an automation.</summary>
    public string? AutomationId { get; set; }
}

/// <summary>The state of a session: the choices in force and the prompts that were not seen before.</summary>
internal sealed class StateRecord : JournalRecord
{
    /// <inheritdoc />
    public override JournalRecordKind Kind => JournalRecordKind.State;

    /// <summary>Gets or sets the provider key selected for the session.</summary>
    public string? ProviderKey { get; set; }

    /// <summary>Gets or sets the model selected for the session.</summary>
    public string? ModelId { get; set; }

    /// <summary>Gets or sets the reasoning effort selected for the session.</summary>
    public string? ReasoningEffort { get; set; }

    /// <summary>Gets or sets the permission mode chosen for the session.</summary>
    public string? PermissionMode { get; set; }

    /// <summary>Gets or sets the agent prompt selected for the session.</summary>
    public string? AgentPromptId { get; set; }

    /// <summary>Gets or sets the parent session.</summary>
    public string? ParentSessionId { get; set; }

    /// <summary>Gets or sets the actor that created the session.</summary>
    public JournalActor? CreatedBy { get; set; }

    /// <summary>Gets the prompt provenance entries the sink had not seen before; the others are passed over.</summary>
    public List<PromptProvenanceEntry> Provenance { get; } = [];

    /// <summary>Gets or sets the number of provenance entries of the record that were passed over because the sink had seen them.</summary>
    public int ProvenancePassedOver { get; set; }
}

/// <summary>A prompt provenance entry of a state record.</summary>
internal sealed class PromptProvenanceEntry
{
    /// <summary>Gets or sets the 64-bit hash of the prompt identifier.</summary>
    public ulong IdHash { get; set; }

    /// <summary>Gets or sets the dispatch kind: <c>send</c>, <c>steer</c>, <c>parent-notify</c>, <c>message</c>, <c>request</c>, <c>job</c>...</summary>
    public string? DispatchKind { get; set; }

    /// <summary>Gets or sets the run the prompt started or joined, when it was known.</summary>
    public string? RunId { get; set; }

    /// <summary>Gets or sets a value indicating whether the prompt was queued.</summary>
    public bool Queued { get; set; }

    /// <summary>Gets or sets who submitted the prompt.</summary>
    public JournalActor? SubmittedBy { get; set; }

    /// <summary>Gets or sets the creation time of the entry.</summary>
    public DateTimeOffset? CreatedAt { get; set; }
}

/// <summary>The phase of a tool call a record reports.</summary>
internal enum ToolPhase : byte
{
    /// <summary>The call started.</summary>
    Started,

    /// <summary>The call completed.</summary>
    Completed,

    /// <summary>The call failed.</summary>
    Failed,

    /// <summary>The call was canceled.</summary>
    Canceled,
}

/// <summary>A phase of a tool call (<c>activity</c> records that are not turn or compaction lifecycle).</summary>
internal sealed class ToolRecord : JournalRecord
{
    /// <inheritdoc />
    public override JournalRecordKind Kind => JournalRecordKind.Tool;

    /// <summary>Gets or sets the phase.</summary>
    public ToolPhase Phase { get; set; }

    /// <summary>Gets or sets the activity kind of the call.</summary>
    public AgentActivityKind ActivityKind { get; set; }

    /// <summary>Gets or sets the identifier of the call.</summary>
    public string? ActivityId { get; set; }

    /// <summary>Gets or sets the name of the tool.</summary>
    public string? Name { get; set; }

    /// <summary>Gets or sets the size in bytes of the arguments, as written in the journal.</summary>
    public long ArgumentBytes { get; set; }

    /// <summary>Gets or sets the size in bytes of the result, as written in the journal.</summary>
    public long ResultBytes { get; set; }

    /// <summary>Gets or sets the success flag of the result, when it has one.</summary>
    public bool? Success { get; set; }

    /// <summary>Gets or sets the number of files the call read.</summary>
    public int FilesRead { get; set; }

    /// <summary>Gets or sets the extensions of the files the call modified (one entry per file, empty for no extension).</summary>
    public List<string>? ModifiedExtensions { get; set; }

    /// <summary>Gets or sets the lines the diff of the call added.</summary>
    public long LinesAdded { get; set; }

    /// <summary>Gets or sets the lines the diff of the call removed.</summary>
    public long LinesRemoved { get; set; }

    /// <summary>Gets or sets the program a shell call ran (the first word of its command).</summary>
    public string? ShellProgram { get; set; }

    /// <summary>Gets or sets the first words of the arguments of an <c>alta</c> call.</summary>
    public string? AltaCommand { get; set; }

    /// <summary>Gets or sets the skill an activation call named.</summary>
    public string? SkillName { get; set; }

    /// <summary>Gets or sets the MCP server of an MCP call, from its details.</summary>
    public string? McpServer { get; set; }

    /// <summary>Gets or sets the MCP tool of an MCP call, from its details.</summary>
    public string? McpTool { get; set; }
}

/// <summary>A prompt of a user: a <c>contentCompleted</c> record of kind <c>User</c>.</summary>
internal sealed class UserContentRecord : JournalRecord
{
    /// <inheritdoc />
    public override JournalRecordKind Kind => JournalRecordKind.UserContent;

    /// <summary>Gets or sets the number of characters of the text.</summary>
    public long Chars { get; set; }

    /// <summary>Gets or sets the number of words of the text.</summary>
    public long Words { get; set; }

    /// <summary>Gets or sets the files attached to the prompt.</summary>
    public int Files { get; set; }

    /// <summary>Gets or sets the folders attached to the prompt.</summary>
    public int Directories { get; set; }

    /// <summary>Gets or sets the images attached to the prompt.</summary>
    public int Images { get; set; }

    /// <summary>Gets or sets the skills attached to the prompt.</summary>
    public int Skills { get; set; }

    /// <summary>Gets or sets a value indicating whether the prompt answers a question (<c>ask_id</c>).</summary>
    public bool IsAnswer { get; set; }

    /// <summary>Gets or sets the session of the agent that sent the prompt (<c>source_session_id</c>).</summary>
    public string? SourceSessionId { get; set; }
}

/// <summary>The channel of a <see cref="ContentRecord"/>.</summary>
internal enum ContentChannel : byte
{
    /// <summary>The text the assistant wrote.</summary>
    Assistant,

    /// <summary>The reasoning of the model.</summary>
    Reasoning,

    /// <summary>A summary of the reasoning.</summary>
    ReasoningSummary,
}

/// <summary>An assistant or reasoning content.</summary>
internal sealed class ContentRecord : JournalRecord
{
    /// <inheritdoc />
    public override JournalRecordKind Kind => JournalRecordKind.Content;

    /// <summary>Gets or sets the channel.</summary>
    public ContentChannel Channel { get; set; }

    /// <summary>Gets or sets the number of characters of the text.</summary>
    public long Chars { get; set; }

    /// <summary>Gets or sets the number of words of the text.</summary>
    public long Words { get; set; }
}

/// <summary>One request to a model: a <c>UsageUpdated</c> record.</summary>
internal sealed class UsageRecord : JournalRecord
{
    /// <inheritdoc />
    public override JournalRecordKind Kind => JournalRecordKind.Usage;

    /// <summary>Gets or sets the usage of the last operation, when the record has one.</summary>
    public AgentOperationUsageSnapshot? Operation { get; set; }

    /// <summary>Gets or sets the tokens of the active context window.</summary>
    public long? WindowTokens { get; set; }

    /// <summary>Gets or sets the limit of the active context window.</summary>
    public long? WindowLimit { get; set; }
}

/// <summary>A change of provider, model, effort or agent prompt.</summary>
internal sealed class ModelChangedRecord : JournalRecord
{
    /// <inheritdoc />
    public override JournalRecordKind Kind => JournalRecordKind.ModelChanged;

    /// <summary>Gets or sets the provider key in force from now on.</summary>
    public string? ProviderKey { get; set; }

    /// <summary>Gets or sets the model in force from now on.</summary>
    public string? ModelId { get; set; }

    /// <summary>Gets or sets the reasoning effort in force from now on.</summary>
    public string? ReasoningEffort { get; set; }

    /// <summary>Gets or sets the agent prompt in force from now on.</summary>
    public string? AgentPromptId { get; set; }
}

/// <summary>How a run ended.</summary>
internal enum RunEndKind : byte
{
    /// <summary>The run went idle: it completed.</summary>
    Idle,

    /// <summary>The run ended with an error.</summary>
    Error,
}

/// <summary>The end of a run.</summary>
internal sealed class RunEndRecord : JournalRecord
{
    /// <inheritdoc />
    public override JournalRecordKind Kind => JournalRecordKind.RunEnd;

    /// <summary>Gets or sets how the run ended.</summary>
    public RunEndKind End { get; set; }
}

/// <summary>A compaction of the context that completed.</summary>
internal sealed class CompactionRecord : JournalRecord
{
    /// <inheritdoc />
    public override JournalRecordKind Kind => JournalRecordKind.Compaction;

    /// <summary>Gets or sets the trigger: <c>threshold</c>, <c>manual</c>, <c>overflow</c>...</summary>
    public string? Trigger { get; set; }

    /// <summary>Gets or sets the tokens of the context before.</summary>
    public long? TokensBefore { get; set; }

    /// <summary>Gets or sets the tokens of the context after.</summary>
    public long? TokensAfter { get; set; }
}

/// <summary>The instructions of a session at one moment.</summary>
internal sealed class SystemPromptRecord : JournalRecord
{
    /// <inheritdoc />
    public override JournalRecordKind Kind => JournalRecordKind.SystemPrompt;

    /// <summary>Gets or sets why the record was written (<c>session_start</c>, <c>changed</c>...).</summary>
    public string? Reason { get; set; }

    /// <summary>Gets or sets the agent prompt the instructions were composed from.</summary>
    public string? AgentPromptId { get; set; }

    /// <summary>Gets or sets the characters of the system message.</summary>
    public long SystemChars { get; set; }

    /// <summary>Gets or sets the characters of the developer instructions.</summary>
    public long DeveloperChars { get; set; }

    /// <summary>Gets or sets the approximate tokens of the whole.</summary>
    public long ApproxTokens { get; set; }
}

