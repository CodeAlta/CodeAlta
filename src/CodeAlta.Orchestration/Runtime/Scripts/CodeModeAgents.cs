using System.Text.Json;
using CodeAlta.Agent;

namespace CodeAlta.Orchestration.Runtime.Scripts;

/// <summary>What a script asks of a session it starts with <c>agent()</c>.</summary>
/// <param name="Prompt">The prompt of the session.</param>
public sealed record CodeModeAgentRequest(string Prompt)
{
    /// <summary>Gets the run of the script.</summary>
    public required string RunId { get; init; }

    /// <summary>Gets the step of the script the session belongs to, or null.</summary>
    public string? StepId { get; init; }

    /// <summary>Gets the title of the session, or null for one taken from its prompt.</summary>
    public string? Title { get; init; }

    /// <summary>Gets the model (<c>provider:model@effort</c>), or null for the tier or the model of the script's session.</summary>
    public string? ModelRef { get; init; }

    /// <summary>Gets the tier (<c>complex</c>, <c>standard</c>, <c>quick</c>), or null.</summary>
    public string? Tier { get; init; }

    /// <summary>Gets the reasoning effort, or null for the model's.</summary>
    public AgentReasoningEffort? Effort { get; init; }

    /// <summary>Gets the agent prompt, or null for the default one.</summary>
    public string? AgentPromptId { get; init; }

    /// <summary>Gets the sandbox profile, or null for the one the script's session gives the sessions it creates.</summary>
    public string? Sandbox { get; init; }

    /// <summary>
    /// Gets whether the session can start sessions of its own; true, the default. False creates it without sub-sessions
    /// (<see cref="CodeAlta.Agent.AgentSessionOptions.SubSessions"/>); a script never gives a session more than its own has.
    /// </summary>
    public bool SubSessions { get; init; } = true;

    /// <summary>
    /// Gets whether the session works in a new git worktree of the project of the script's session, on a branch of its own
    /// (<c>alta/&lt;name&gt;</c>); false for the folder that session works in.
    /// </summary>
    public bool Worktree { get; init; }

    /// <summary>Gets the branch or commit a new worktree starts from, or null for the commit the project is on.</summary>
    public string? WorktreeBase { get; init; }

    /// <summary>Gets the time the session is given, or null for no limit but the run's.</summary>
    public TimeSpan? Timeout { get; init; }

    /// <summary>
    /// Gets the JSON Schema of the answer, or null for a text: the session is asked for one JSON value that matches it, and
    /// is asked once more when its answer does not.
    /// </summary>
    public JsonElement? Schema { get; init; }

    /// <summary>Gets the tokens the sessions of the run may spend in all, or null for no budget.</summary>
    public long? BudgetTokens { get; init; }
}

/// <summary>How a session a script started ended.</summary>
/// <param name="SessionId">The session, or null when it could not be created.</param>
/// <param name="Succeeded">Whether it answered.</param>
/// <param name="Status">How it ended: <c>completed</c>, <c>failed</c>, <c>aborted</c>, <c>timeout</c>, <c>refused</c>...</param>
/// <param name="Answer">Its final answer, or null.</param>
/// <param name="Failure">What went wrong, or null.</param>
/// <param name="Usage">The tokens and cost of the session and of the sessions it started.</param>
/// <param name="Elapsed">How long it ran.</param>
public sealed record CodeModeAgentResult(string? SessionId, bool Succeeded, string Status, string? Answer, string? Failure, SessionUsageTotals Usage, TimeSpan Elapsed)
{
    /// <summary>Gets the JSON text of the answer that matched the schema of the request, or null.</summary>
    public string? Value { get; init; }

    /// <summary>Gets what the script is told in its output about how the session was started, such as a tier that is not set up; null for nothing.</summary>
    public string? Warning { get; init; }

    /// <summary>Gets the folder of the worktree the session works in, when the request asked for one; null otherwise.</summary>
    public string? WorktreeFolder { get; init; }

    /// <summary>Gets the branch of the worktree the session works in, when the request asked for one; null otherwise.</summary>
    public string? WorktreeBranch { get; init; }
}

/// <summary>
/// Starts the sessions of scripts (<c>agent()</c>): real child sessions of the session that runs the script, whose answer
/// goes to the script, never to that session as a prompt.
/// </summary>
public interface ICodeModeAgents
{
    /// <summary>Creates a child of a session, sends it a prompt, and waits until it and the sessions it started have settled.</summary>
    /// <param name="parentSessionId">The session that runs the script.</param>
    /// <param name="request">What to ask.</param>
    /// <param name="cancellationToken">Stops the session and the sessions it started.</param>
    /// <returns>How it ended; a refusal (a limit, a model that cannot run) is a result, not an exception.</returns>
    /// <exception cref="OperationCanceledException">The wait was cancelled.</exception>
    Task<CodeModeAgentResult> RunAsync(string parentSessionId, CodeModeAgentRequest request, CancellationToken cancellationToken);

    /// <summary>Creates a child of a session that the script keeps open for several prompts (<c>session()</c>); the prompt of the request is not sent.</summary>
    /// <param name="parentSessionId">The session that runs the script.</param>
    /// <param name="request">What the session is created with.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The session (status <c>opened</c>), or the refusal.</returns>
    /// <exception cref="OperationCanceledException">The creation was cancelled.</exception>
    Task<CodeModeAgentResult> OpenAsync(string parentSessionId, CodeModeAgentRequest request, CancellationToken cancellationToken);

    /// <summary>Sends a prompt to a session the script keeps open, and waits for its answer; prompts to one session go one at a time.</summary>
    /// <param name="runId">The run of the script.</param>
    /// <param name="sessionId">The session.</param>
    /// <param name="prompt">The prompt.</param>
    /// <param name="schema">The JSON Schema of the answer, or null.</param>
    /// <param name="timeout">The time the session is given, or null.</param>
    /// <param name="cancellationToken">Stops the session.</param>
    /// <returns>How it ended.</returns>
    /// <exception cref="OperationCanceledException">The wait was cancelled.</exception>
    Task<CodeModeAgentResult> AskAsync(string runId, string sessionId, string prompt, JsonElement? schema, TimeSpan? timeout, CancellationToken cancellationToken);

    /// <summary>Closes a session the script kept open; its run closes those it did not.</summary>
    /// <param name="runId">The run of the script.</param>
    /// <param name="sessionId">The session.</param>
    /// <returns>A task that completes when it is closed.</returns>
    Task CloseAsync(string runId, string sessionId);
}

/// <summary>The sessions of scripts of a host, asked for when a script first starts one: the host makes them then.</summary>
/// <param name="agents">Gives the sessions of scripts of the host.</param>
public sealed class DeferredCodeModeAgents(Func<ICodeModeAgents> agents) : ICodeModeAgents
{
    private readonly Func<ICodeModeAgents> _agents = agents ?? throw new ArgumentNullException(nameof(agents));

    /// <inheritdoc />
    public Task<CodeModeAgentResult> RunAsync(string parentSessionId, CodeModeAgentRequest request, CancellationToken cancellationToken)
        => _agents().RunAsync(parentSessionId, request, cancellationToken);

    /// <inheritdoc />
    public Task<CodeModeAgentResult> OpenAsync(string parentSessionId, CodeModeAgentRequest request, CancellationToken cancellationToken)
        => _agents().OpenAsync(parentSessionId, request, cancellationToken);

    /// <inheritdoc />
    public Task<CodeModeAgentResult> AskAsync(string runId, string sessionId, string prompt, JsonElement? schema, TimeSpan? timeout, CancellationToken cancellationToken)
        => _agents().AskAsync(runId, sessionId, prompt, schema, timeout, cancellationToken);

    /// <inheritdoc />
    public Task CloseAsync(string runId, string sessionId) => _agents().CloseAsync(runId, sessionId);
}
