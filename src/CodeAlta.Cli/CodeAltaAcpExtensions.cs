namespace CodeAlta.Cli;

/// <summary>
/// What CodeAlta adds to the Agent Client Protocol, by its extensibility rules: custom notifications whose methods start
/// with <c>_codealta/</c> for what ACP has no update for, and the <see cref="MetaKey"/> object in the <c>_meta</c> of the
/// standard types for small extras. Clients ignore the notifications they do not know. <c>altacli run --format jsonl</c>
/// writes them, and <c>altacli acp</c> sends them and advertises <see cref="AcpMethods"/> in <c>agentCapabilities._meta</c>.
/// </summary>
/// <remarks>Every notification names the session it is about in <c>sessionId</c>.</remarks>
internal static class CodeAltaAcpExtensions
{
    /// <summary>The key of the CodeAlta object in a <c>_meta</c>.</summary>
    public const string MetaKey = "codealta";

    /// <summary>The version of these extensions: the methods and their parameters.</summary>
    public const int SchemaVersion = 1;

    /// <summary>
    /// The header of a run, before its updates: <c>schemaVersion</c>, <c>sessionId</c>, <c>created</c>, <c>cwd</c>,
    /// <c>provider</c>, <c>model</c>, <c>reasoning</c>, <c>agentPrompt</c>, <c>tier</c>, <c>storage</c>.
    /// </summary>
    public const string SessionMethod = "_codealta/session";

    /// <summary>
    /// The result of a run, after its updates: <c>sessionId</c>, <c>runId</c>, <c>stopReason</c>, <c>status</c>,
    /// <c>exitCode</c>, <c>finalMessage</c>, <c>abortReason</c>, <c>failure</c>, <c>usage</c>, <c>cost</c>, <c>durationMs</c>.
    /// </summary>
    public const string ResultMethod = "_codealta/result";

    /// <summary>A session the run started: <c>sessionId</c>, <c>parentSessionId</c>, <c>title</c>.</summary>
    public const string SessionStartedMethod = "_codealta/session_started";

    /// <summary>A compaction of the conversation of a session: <c>sessionId</c>, <c>phase</c> (<c>started</c>, <c>completed</c>), <c>message</c>.</summary>
    public const string CompactionMethod = "_codealta/compaction";

    /// <summary>A retry of the provider after a transient failure: <c>sessionId</c>, <c>message</c>.</summary>
    public const string RetryMethod = "_codealta/retry";

    /// <summary>The run goes on with the next model of its tier: <c>sessionId</c>, <c>from</c>, <c>to</c>, <c>reason</c>, <c>message</c>.</summary>
    public const string FallbackMethod = "_codealta/fallback";

    /// <summary>An error of a session: <c>sessionId</c>, <c>kind</c> (the failure kind, or <c>other</c>), <c>message</c>.</summary>
    public const string ErrorMethod = "_codealta/error";

    /// <summary>Gets every method of the extensions, for a capability object.</summary>
    public static IReadOnlyList<string> Methods { get; } =
        [SessionMethod, ResultMethod, SessionStartedMethod, CompactionMethod, RetryMethod, FallbackMethod, ErrorMethod];

    /// <summary>
    /// Gets the methods <c>altacli acp</c> sends, which it advertises in <c>agentCapabilities._meta.codealta.notifications</c>:
    /// those of the events of a run. The header and the result are the lines of JSONL only: an ACP client has the responses
    /// of <c>session/new</c> and <c>session/prompt</c> for them.
    /// </summary>
    public static IReadOnlyList<string> AcpMethods { get; } = [SessionStartedMethod, CompactionMethod, RetryMethod, FallbackMethod, ErrorMethod];
}
