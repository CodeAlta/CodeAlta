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

    /// <summary>Gets the time the session is given, or null for no limit but the run's.</summary>
    public TimeSpan? Timeout { get; init; }
}

/// <summary>How a session a script started ended.</summary>
/// <param name="SessionId">The session, or null when it could not be created.</param>
/// <param name="Succeeded">Whether it answered.</param>
/// <param name="Status">How it ended: <c>completed</c>, <c>failed</c>, <c>aborted</c>, <c>timeout</c>, <c>refused</c>...</param>
/// <param name="Answer">Its final answer, or null.</param>
/// <param name="Failure">What went wrong, or null.</param>
/// <param name="Usage">The tokens and cost of the session and of the sessions it started.</param>
/// <param name="Elapsed">How long it ran.</param>
public sealed record CodeModeAgentResult(string? SessionId, bool Succeeded, string Status, string? Answer, string? Failure, SessionUsageTotals Usage, TimeSpan Elapsed);

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
}

/// <summary>The sessions of scripts of a host, asked for when a script first starts one: the host makes them then.</summary>
/// <param name="agents">Gives the sessions of scripts of the host.</param>
public sealed class DeferredCodeModeAgents(Func<ICodeModeAgents> agents) : ICodeModeAgents
{
    private readonly Func<ICodeModeAgents> _agents = agents ?? throw new ArgumentNullException(nameof(agents));

    /// <inheritdoc />
    public Task<CodeModeAgentResult> RunAsync(string parentSessionId, CodeModeAgentRequest request, CancellationToken cancellationToken)
        => _agents().RunAsync(parentSessionId, request, cancellationToken);
}
