using System.Text.Json;

namespace CodeAlta.Agent;

/// <summary>Who may call a tool.</summary>
public enum AgentToolExposure
{
    /// <summary>The model and the scripts of Code Mode.</summary>
    Direct,

    /// <summary>The scripts of Code Mode only: the model is never offered the tool, so it lengthens no prompt.</summary>
    Scripts,

    /// <summary>The model only, such as the tool that runs scripts itself, or one that asks the user a question.</summary>
    Model,
}

/// <summary>
/// The tools of a run, which a script of Code Mode calls from the tool call that runs it. A nested call runs the run's
/// own handler of the tool: its permissions, its sandbox routing and the tools the session does without stay as for a
/// call of the model. It is recorded as an activity of the calling tool call, never as a message of the conversation.
/// </summary>
public interface IAgentRunToolInvoker
{
    /// <summary>Lists the tools a script may call: every tool of the run, deferred ones included, except those of the model only.</summary>
    /// <returns>The tools.</returns>
    IReadOnlyList<AgentToolSpec> ListTools();

    /// <summary>Runs one nested call. A failure of the tool is a result that is not a success, never an exception.</summary>
    /// <param name="call">The call.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The result of the tool.</returns>
    /// <exception cref="OperationCanceledException">The call was cancelled.</exception>
    Task<AgentToolResult> InvokeAsync(AgentNestedToolCall call, CancellationToken cancellationToken);
}

/// <summary>A call that a script makes to a tool of its run.</summary>
/// <param name="ToolName">The name of the tool.</param>
/// <param name="Arguments">The arguments, a JSON object.</param>
public sealed record AgentNestedToolCall(string ToolName, JsonElement Arguments)
{
    /// <summary>Gets the step of the script the call belongs to, or null.</summary>
    public string? StepId { get; init; }

    /// <summary>Gets the name shown for the call, such as <c>alta session create</c>; null shows the tool name.</summary>
    public string? DisplayName { get; init; }
}
