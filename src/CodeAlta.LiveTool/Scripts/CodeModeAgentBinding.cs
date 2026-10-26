using System.Text;
using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.CodeMode;
using CodeAlta.Orchestration.Runtime.Scripts;

namespace CodeAlta.LiveTool.Scripts;

/// <summary>
/// <c>agent(prompt, options)</c> in scripts: a real child session of the session that runs the script, which answers the
/// script. The prelude gives scripts the function; it calls <c>codemode.agent</c>, which this binding runs.
/// </summary>
public sealed class CodeModeAgentBinding : ICodeModeCallHandler
{
    /// <summary>The path of the function in scripts.</summary>
    public const string Path = "codemode.agent";

    private readonly ICodeModeAgents _agents;
    private readonly string _sessionId;
    private readonly Func<string> _runId;

    /// <summary>Creates the binding of one run.</summary>
    /// <param name="agents">The sessions of scripts of the host.</param>
    /// <param name="sessionId">The session that runs the script.</param>
    /// <param name="runId">Gives the id of the run.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public CodeModeAgentBinding(ICodeModeAgents agents, string sessionId, Func<string> runId)
    {
        ArgumentNullException.ThrowIfNull(agents);
        ArgumentNullException.ThrowIfNull(sessionId);
        ArgumentNullException.ThrowIfNull(runId);
        _agents = agents;
        _sessionId = sessionId;
        _runId = runId;
    }

    /// <summary>Gets the declaration of the function, for the API of a run.</summary>
    public static CodeModeFunction Function { get; } = new(Path)
    {
        Description = "Starts a session of its own for a part of the work, sends it the prompt, and resolves with its final answer once it and the sessions it started ended; scripts call it as agent(prompt, options). It runs in parallel with the other calls (8 sessions at once, 50 per script); a session that fails throws an AgentError with its sessionId.",
        InputSchemaJson = """
            {"type":"object","properties":{
              "prompt":{"type":"string","description":"What the session is asked: all it needs to know, it sees nothing else."},
              "title":{"type":"string","description":"The title of the session."},
              "model":{"type":"string","description":"provider:model@effort; the model of this session when neither model nor tier is given."},
              "tier":{"type":"string","enum":["complex","standard","quick"]},
              "effort":{"type":"string","enum":["minimal","low","medium","high","xhigh","max"]},
              "agent":{"type":"string","description":"The agent prompt, such as default or plan."},
              "sandbox":{"type":"string","description":"A sandbox profile."},
              "timeout":{"type":"integer","minimum":1,"description":"Milliseconds the session is given."},
              "step":{"type":"string","description":"The step the session belongs to, when not the current one."}
            },"required":["prompt"],"additionalProperties":false}
            """,
        ResultType = "{ sessionId: string; answer: string; status: string; inputTokens: number; outputTokens: number; durationMs: number }",
        Parallel = true,
    };

    /// <inheritdoc />
    public async ValueTask<CodeModeCallResult> InvokeAsync(CodeModeCall call, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(call);
        using var document = JsonDocument.Parse(call.ArgumentsJson);
        var arguments = document.RootElement;
        if (String(arguments, "prompt") is not { Length: > 0 } prompt)
        {
            return CodeModeCallResult.Failure(new CodeModeCallError("TypeError", "agent(prompt, options): the prompt is a non-empty string."));
        }

        AgentReasoningEffort? effort = null;
        if (String(arguments, "effort") is { } effortName)
        {
            if (!Enum.TryParse<AgentReasoningEffort>(effortName, ignoreCase: true, out var parsed))
            {
                return CodeModeCallResult.Failure(new CodeModeCallError("TypeError", $"agent(): '{effortName}' is not a reasoning effort."));
            }

            effort = parsed;
        }

        var result = await _agents.RunAsync(_sessionId, new CodeModeAgentRequest(prompt)
        {
            RunId = _runId(),
            StepId = String(arguments, "step") ?? call.StepId,
            Title = String(arguments, "title"),
            ModelRef = String(arguments, "model"),
            Tier = String(arguments, "tier"),
            Effort = effort,
            AgentPromptId = String(arguments, "agent"),
            Sandbox = String(arguments, "sandbox"),
            Timeout = arguments.TryGetProperty("timeout", out var timeout) && timeout.TryGetInt32(out var milliseconds) && milliseconds > 0
                ? TimeSpan.FromMilliseconds(milliseconds) : null,
        }, cancellationToken).ConfigureAwait(false);

        if (!result.Succeeded)
        {
            return CodeModeCallResult.Failure(new CodeModeCallError("AgentError", result.Failure ?? $"The session ended: {result.Status}.")
            {
                Code = result.Status,
                DataJson = Json(result),
            });
        }

        return CodeModeCallResult.Success(Json(result));
    }

    private static string Json(CodeModeAgentResult result)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            if (result.SessionId is { } sessionId) writer.WriteString("sessionId", sessionId); else writer.WriteNull("sessionId");
            writer.WriteString("answer", result.Answer ?? string.Empty);
            writer.WriteString("status", result.Status);
            writer.WriteNumber("inputTokens", result.Usage.InputTokens);
            writer.WriteNumber("outputTokens", result.Usage.OutputTokens);
            writer.WriteNumber("durationMs", (long)result.Elapsed.TotalMilliseconds);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string? String(JsonElement arguments, string name)
        => arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
