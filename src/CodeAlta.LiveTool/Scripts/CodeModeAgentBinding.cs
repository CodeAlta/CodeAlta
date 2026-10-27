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

    /// <summary>The path of the function that opens a session a script keeps (<c>session()</c>).</summary>
    public const string OpenPath = "codemode.session.open";

    /// <summary>The path of the function that sends a prompt to a session a script keeps.</summary>
    public const string AskPath = "codemode.session.ask";

    /// <summary>The path of the function that closes a session a script keeps.</summary>
    public const string ClosePath = "codemode.session.close";

    /// <summary>Gets the paths this binding runs.</summary>
    public static IReadOnlyList<string> Paths { get; } = [Path, OpenPath, AskPath, ClosePath];

    private readonly ICodeModeAgents _agents;
    private readonly string _sessionId;
    private readonly Func<string> _runId;
    private readonly long? _budgetTokens;

    /// <summary>Creates the binding of one run.</summary>
    /// <param name="agents">The sessions of scripts of the host.</param>
    /// <param name="sessionId">The session that runs the script.</param>
    /// <param name="runId">Gives the id of the run.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public CodeModeAgentBinding(ICodeModeAgents agents, string sessionId, Func<string> runId)
        : this(agents, sessionId, runId, budgetTokens: null)
    {
    }

    /// <summary>Creates the binding of one run whose sessions have a budget of tokens.</summary>
    /// <param name="agents">The sessions of scripts of the host.</param>
    /// <param name="sessionId">The session that runs the script.</param>
    /// <param name="runId">Gives the id of the run.</param>
    /// <param name="budgetTokens">The tokens the sessions of the run may spend in all, or null.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public CodeModeAgentBinding(ICodeModeAgents agents, string sessionId, Func<string> runId, long? budgetTokens)
    {
        _budgetTokens = budgetTokens;
        ArgumentNullException.ThrowIfNull(agents);
        ArgumentNullException.ThrowIfNull(sessionId);
        ArgumentNullException.ThrowIfNull(runId);
        _agents = agents;
        _sessionId = sessionId;
        _runId = runId;
    }

    /// <summary>Gets the declaration of <c>codemode.agent</c>.</summary>
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
              "worktree":{"type":"boolean","description":"Work in a new git worktree of the project, on a branch of its own, so that sessions that change files in parallel do not collide; the worktree and its branch stay for the work to be merged."},
              "base":{"type":"string","description":"The branch or commit the worktree starts from; the commit the project is on by default."},
              "timeout":{"type":"integer","minimum":1,"description":"Milliseconds the session is given."},
              "step":{"type":"string","description":"The step the session belongs to, when not the current one."},
              "schema":{"type":"object","description":"A JSON Schema of the answer: agent() then resolves with the JSON value the session answered, checked against it."}
            },"required":["prompt"],"additionalProperties":false}
            """,
        ResultType = "{ sessionId: string; answer: string; value?: unknown; status: string; inputTokens: number; outputTokens: number; durationMs: number; worktree?: { folder: string; branch: string } }",
        Parallel = true,
    };

    /// <summary>Gets the declarations of the functions, for the API of a run.</summary>
    public static IReadOnlyList<CodeModeFunction> Functions { get; } =
    [
        Function,
        new(OpenPath)
        {
            Description = "Creates a session of its own that the script keeps for several prompts; scripts call it as session(options), which gives { id, worktree, run(prompt, { schema }), close() }. The options are those of agent(), without the prompt. A session not closed is closed when the script ends.",
            InputSchemaJson = """{"type":"object","properties":{"title":{"type":"string"},"model":{"type":"string"},"tier":{"type":"string","enum":["complex","standard","quick"]},"effort":{"type":"string"},"agent":{"type":"string"},"sandbox":{"type":"string"},"worktree":{"type":"boolean"},"base":{"type":"string"},"step":{"type":"string"}},"additionalProperties":false}""",
            ResultType = "{ sessionId: string; worktree?: { folder: string; branch: string } }",
        },
        new(AskPath)
        {
            Description = "Sends a prompt to a session the script keeps, and resolves with its answer (session(...).run(prompt, { schema })).",
            InputSchemaJson = """{"type":"object","properties":{"sessionId":{"type":"string"},"prompt":{"type":"string"},"schema":{"type":"object"},"timeout":{"type":"integer","minimum":1}},"required":["sessionId","prompt"],"additionalProperties":false}""",
            ResultType = "{ sessionId: string; answer: string; value?: unknown; status: string; inputTokens: number; outputTokens: number; durationMs: number }",
            Parallel = true,
        },
        new(ClosePath)
        {
            Description = "Closes a session the script keeps (session(...).close()).",
            InputSchemaJson = """{"type":"object","properties":{"sessionId":{"type":"string"}},"required":["sessionId"],"additionalProperties":false}""",
            ResultType = "void",
        },
    ];

    /// <inheritdoc />
    public async ValueTask<CodeModeCallResult> InvokeAsync(CodeModeCall call, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(call);
        using var document = JsonDocument.Parse(call.ArgumentsJson);
        var arguments = document.RootElement;
        switch (call.Path)
        {
            case ClosePath:
                await _agents.CloseAsync(_runId(), String(arguments, "sessionId") ?? string.Empty).ConfigureAwait(false);
                return CodeModeCallResult.Success("null");
            case AskPath:
                if (String(arguments, "sessionId") is not { Length: > 0 } sessionId || String(arguments, "prompt") is not { Length: > 0 } asked)
                {
                    return CodeModeCallResult.Failure(new CodeModeCallError("TypeError", "session.run(prompt, options): the prompt is a non-empty string."));
                }

                return Answer(await _agents.AskAsync(_runId(), sessionId, asked, Schema(arguments), Timeout(arguments), cancellationToken).ConfigureAwait(false));
        }

        var opening = call.Path == OpenPath;
        var prompt = String(arguments, "prompt");
        if (!opening && prompt is not { Length: > 0 })
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

        var request = new CodeModeAgentRequest(prompt ?? string.Empty)
        {
            RunId = _runId(),
            StepId = String(arguments, "step") ?? call.StepId,
            Title = String(arguments, "title"),
            ModelRef = String(arguments, "model"),
            Tier = String(arguments, "tier"),
            Effort = effort,
            AgentPromptId = String(arguments, "agent"),
            Sandbox = String(arguments, "sandbox"),
            Worktree = arguments.TryGetProperty("worktree", out var worktree) && worktree.ValueKind == JsonValueKind.True,
            WorktreeBase = String(arguments, "base"),
            Timeout = Timeout(arguments),
            Schema = Schema(arguments),
            BudgetTokens = _budgetTokens,
        };
        return Answer(opening
            ? await _agents.OpenAsync(_sessionId, request, cancellationToken).ConfigureAwait(false)
            : await _agents.RunAsync(_sessionId, request, cancellationToken).ConfigureAwait(false));
    }

    private static TimeSpan? Timeout(JsonElement arguments)
        => arguments.TryGetProperty("timeout", out var timeout) && timeout.TryGetInt32(out var milliseconds) && milliseconds > 0 ? TimeSpan.FromMilliseconds(milliseconds) : null;

    private static JsonElement? Schema(JsonElement arguments)
        => arguments.TryGetProperty("schema", out var schema) && schema.ValueKind == JsonValueKind.Object ? schema.Clone() : null;

    // A session that did not answer is an AgentError the script can catch, with the session and the status.
    private static CodeModeCallResult Answer(CodeModeAgentResult result)
    {
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
            if (result.Value is { } value)
            {
                writer.WritePropertyName("value");
                writer.WriteRawValue(value);
            }

            writer.WriteString("status", result.Status);
            writer.WriteNumber("inputTokens", result.Usage.InputTokens);
            writer.WriteNumber("outputTokens", result.Usage.OutputTokens);
            writer.WriteNumber("durationMs", (long)result.Elapsed.TotalMilliseconds);
            if (result.Warning is { } warning) writer.WriteString("warning", warning);
            if (result.WorktreeFolder is { } folder)
            {
                writer.WriteStartObject("worktree");
                writer.WriteString("folder", folder);
                if (result.WorktreeBranch is { } branch) writer.WriteString("branch", branch);
                writer.WriteEndObject();
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string? String(JsonElement arguments, string name)
        => arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
