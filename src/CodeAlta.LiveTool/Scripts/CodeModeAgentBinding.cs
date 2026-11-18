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

    // What a script says of the session it starts: the one list the schemas of the functions, the declarations of
    // agent() and session() and the check of a call are made from, so that an option cannot be taken without being
    // declared, nor given without being taken. An alias is the name `alta session create` has for the same thing.
    private static readonly Option[] Options =
    [
        new("title", """{"type":"string","description":"The title of the session."}"""),
        new("model", """{"type":"string","description":"provider:model or provider:model@reasoning, as alta.model.list({}) lists them; with `provider`, the id of a model of that provider. The model and the reasoning effort of this session when neither model nor tier is given."}""", Alias: "modelRef"),
        new("provider", """{"type":"string","description":"The provider of `model`, when `model` is only the id of a model."}"""),
        new("tier", """{"type":"string","enum":["complex","standard","quick"],"description":"A tier of models of the settings, instead of a model."}"""),
        new("reasoning", """{"type":"string","enum":["minimal","low","medium","high","xhigh","max","none"],"description":"The reasoning effort, over the one of the model or of the tier."}""", Alias: "effort"),
        new("agent", """{"type":"string","description":"The agent prompt, such as default or plan (alta.prompt.list({}) lists them)."}""", Alias: "promptId"),
        new("sandbox", """{"type":"string","description":"A sandbox profile (alta.sandbox.list({}) lists them); a script turns a sandbox on, never off."}"""),
        new("subSessions", """{"type":"boolean","description":"false creates the session without sub-sessions: it cannot start sessions of its own, for a part it must do itself."}"""),
        new("worktree", """{"type":"boolean","description":"Work in a new git worktree of the project, on a branch of its own, so that sessions that change files in parallel do not collide; the worktree and its branch stay for the work to be merged."}"""),
        new("base", """{"type":"string","description":"The branch or commit the worktree starts from; the commit the project is on by default."}"""),
        new("machine", """{"type":"string","description":"Another machine to start the session on (alta.session.machines({}) lists them), with at most what this session has, in the project of the same repository there."}""", AgentOnly: true),
        new("timeout", """{"type":"integer","minimum":1,"description":"Milliseconds the session is given for a prompt."}""", AgentOnly: true),
        new("step", """{"type":"string","description":"The step the session belongs to, when not the current one."}"""),
        new("schema", """{"type":"object","description":"A JSON Schema of the answer: the call then resolves with the JSON value the session answered, checked against it."}""", AgentOnly: true),
    ];

    private static readonly Option Prompt = new("prompt", """{"type":"string","description":"What the session is asked: all it needs to know, it sees nothing else."}""");

    private static readonly Option[] AgentOptions = [Prompt, .. Options];

    private static readonly Option[] SessionOptions = [.. Options.Where(static option => !option.AgentOnly)];

    private static readonly Option[] AskOptions =
    [
        new("sessionId", """{"type":"string"}"""),
        Prompt,
        .. Options.Where(static option => option.Name is "schema" or "timeout"),
    ];

    /// <summary>Gets the declaration of <c>codemode.agent</c>.</summary>
    public static CodeModeFunction Function { get; } = new(Path)
    {
        Description = "Starts a session of its own for a part of the work, sends it the prompt, and resolves with its final answer once it and the sessions it started ended; scripts call it as agent(prompt, options). It runs in parallel with the other calls, 8 sessions at once (the others wait for one); a session that fails throws an AgentError with its sessionId.",
        InputSchemaJson = Schema(AgentOptions, "prompt"),
        ResultType = "{ sessionId: string; answer: string; value?: unknown; status: string; inputTokens: number; outputTokens: number; durationMs: number; worktree?: { folder: string; branch: string } }",
        Parallel = true,
    };

    /// <summary>Gets the declarations of the functions, for the API of a run.</summary>
    public static IReadOnlyList<CodeModeFunction> Functions { get; } =
    [
        Function,
        new(OpenPath)
        {
            Description = "Creates a session of its own that the script keeps for several prompts; scripts call it as session(options), which gives { id, worktree, run(prompt, { schema, timeout }), close() }. A session not closed is closed when the script ends.",
            InputSchemaJson = Schema(SessionOptions),
            ResultType = "{ sessionId: string; worktree?: { folder: string; branch: string } }",
        },
        new(AskPath)
        {
            Description = "Sends a prompt to a session the script keeps, and resolves with its answer (session(...).run(prompt, { schema, timeout })).",
            InputSchemaJson = Schema(AskOptions, "sessionId", "prompt"),
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

    /// <summary>Gets the TypeScript declaration of <c>agent()</c> as scripts call it, with every option it takes.</summary>
    public static string AgentDeclaration { get; } =
        $"declare function agent(prompt: string, options?: {CodeModeDeclarations.RenderType(Schema(Options))}): Promise<any>;";

    /// <summary>Gets the TypeScript declaration of <c>session()</c> as scripts call it, with every option it takes.</summary>
    public static string SessionDeclaration { get; } =
        $"declare function session(options?: {CodeModeDeclarations.RenderType(Schema(SessionOptions))}): Promise<{{ id: string; worktree: {{ folder: string; branch: string }} | null; run(prompt: string, options?: {CodeModeDeclarations.RenderType(Schema([.. Options.Where(static option => option.Name is "schema" or "timeout")]))}): Promise<any>; close(): Promise<void> }}>;";

    /// <inheritdoc />
    public async ValueTask<CodeModeCallResult> InvokeAsync(CodeModeCall call, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(call);
        using var document = JsonDocument.Parse(call.ArgumentsJson);
        switch (call.Path)
        {
            case ClosePath:
                await _agents.CloseAsync(_runId(), String(document.RootElement, "sessionId") ?? string.Empty).ConfigureAwait(false);
                return CodeModeCallResult.Success("null");
            case AskPath:
                if (!TryRead(document.RootElement, AskOptions, "session.run(prompt, options)", out var asking, out var askError))
                {
                    return CodeModeCallResult.Failure(askError!);
                }

                if (String(asking, "sessionId") is not { Length: > 0 } sessionId || String(asking, "prompt") is not { Length: > 0 } asked)
                {
                    return CodeModeCallResult.Failure(new CodeModeCallError("TypeError", "session.run(prompt, options): the prompt is a non-empty string."));
                }

                return Answer(await _agents.AskAsync(_runId(), sessionId, asked, Schema(asking), Timeout(asking), cancellationToken).ConfigureAwait(false));
        }

        var opening = call.Path == OpenPath;
        var name = opening ? "session(options)" : "agent(prompt, options)";
        if (!TryRead(document.RootElement, opening ? SessionOptions : AgentOptions, name, out var arguments, out var error))
        {
            return CodeModeCallResult.Failure(error!);
        }

        var prompt = String(arguments, "prompt");
        if (!opening && prompt is not { Length: > 0 })
        {
            return CodeModeCallResult.Failure(new CodeModeCallError("TypeError", "agent(prompt, options): the prompt is a non-empty string."));
        }

        AgentReasoningEffort? reasoning = null;
        if (String(arguments, "reasoning") is { } reasoningName && !AltaModelRef.TryParseReasoning(reasoningName, out reasoning))
        {
            return CodeModeCallResult.Failure(new CodeModeCallError("TypeError", $"{name}: '{reasoningName}' is not a reasoning effort: minimal, low, medium, high, xhigh, max or none."));
        }

        // `provider` with the id of a model is the model `provider:model`, as `alta session create --provider --model`.
        var model = String(arguments, "model");
        if (String(arguments, "provider") is { } provider)
        {
            if (model is null)
            {
                return CodeModeCallResult.Failure(new CodeModeCallError("TypeError", $"{name}: `provider` goes with `model`, the id of one of its models (alta.model.list({{ provider: \"{provider}\" }}) lists them)."));
            }

            if (!model.Contains(':', StringComparison.Ordinal))
            {
                model = $"{provider}:{model}";
            }
            else if (!model.StartsWith(provider + ":", StringComparison.OrdinalIgnoreCase))
            {
                return CodeModeCallResult.Failure(new CodeModeCallError("TypeError", $"{name}: `model` '{model}' names another provider than `provider` '{provider}': give one of them."));
            }
        }

        var request = new CodeModeAgentRequest(prompt ?? string.Empty)
        {
            RunId = _runId(),
            StepId = String(arguments, "step") ?? call.StepId,
            Title = String(arguments, "title"),
            ModelRef = model,
            Tier = String(arguments, "tier"),
            Effort = reasoning,
            AgentPromptId = String(arguments, "agent"),
            Sandbox = String(arguments, "sandbox"),
            // An agent turns sub-sessions off for a child, never on: a session that has none runs no agent().
            SubSessions = !(arguments.TryGetValue("subSessions", out var subSessions) && subSessions.ValueKind == JsonValueKind.False),
            Worktree = arguments.TryGetValue("worktree", out var worktree) && worktree.ValueKind == JsonValueKind.True,
            WorktreeBase = String(arguments, "base"),
            Machine = String(arguments, "machine"),
            Timeout = Timeout(arguments),
            Schema = Schema(arguments),
            BudgetTokens = _budgetTokens,
        };
        return Answer(opening
            ? await _agents.OpenAsync(_sessionId, request, cancellationToken).ConfigureAwait(false)
            : await _agents.RunAsync(_sessionId, request, cancellationToken).ConfigureAwait(false));
    }

    // An option of a call: its name, the JSON Schema of its value, the other name it is taken under, and whether only
    // agent() and run() take it (a session a script keeps has no prompt to bound nor to check when it is created).
    private sealed record Option(string Name, string Schema, string? Alias = null, bool AgentOnly = false)
    {
        public string Type { get; } = JsonDocument.Parse(Schema).RootElement.GetProperty("type").GetString()!;
    }

    private static string Schema(IReadOnlyList<Option> options, params string[] required)
    {
        var builder = new StringBuilder("""{"type":"object","properties":{""");
        for (var index = 0; index < options.Count; index++)
        {
            builder.Append(index == 0 ? "\"" : ",\"").Append(options[index].Name).Append("\":").Append(options[index].Schema);
        }

        builder.Append('}');
        if (required.Length > 0)
        {
            builder.Append(",\"required\":[").AppendJoin(',', required.Select(static name => $"\"{name}\"")).Append(']');
        }

        return builder.Append(",\"additionalProperties\":false}").ToString();
    }

    // The options of a call by their name, each of the type it is declared with. A name the function does not take, or a
    // value of another type, is an error that names what it takes: an option dropped in silence starts a session that is
    // not the one asked for, and the script cannot tell.
    private static bool TryRead(JsonElement arguments, IReadOnlyList<Option> options, string call, out Dictionary<string, JsonElement> values, out CodeModeCallError? error)
    {
        values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        error = null;
        if (arguments.ValueKind != JsonValueKind.Object)
        {
            error = new CodeModeCallError("TypeError", $"{call}: options is an object.");
            return false;
        }

        foreach (var property in arguments.EnumerateObject())
        {
            var option = options.FirstOrDefault(candidate => candidate.Name == property.Name || candidate.Alias == property.Name);
            if (option is null)
            {
                var names = options.Where(static candidate => candidate != Prompt && candidate.Name != "sessionId").Select(static candidate => candidate.Name).ToArray();
                var suggestion = CodeModeNames.Closest(property.Name, names);
                error = new CodeModeCallError("TypeError",
                    $"{call}: unknown option `{property.Name}`.{(suggestion is null ? string.Empty : $" Did you mean `{suggestion}`?")} It takes: {string.Join(", ", names)}.");
                return false;
            }

            if (property.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                continue;
            }

            var matches = option.Type switch
            {
                "string" => property.Value.ValueKind == JsonValueKind.String,
                "boolean" => property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False,
                "integer" => property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt32(out var number) && number > 0,
                _ => property.Value.ValueKind == JsonValueKind.Object,
            };
            if (!matches)
            {
                error = new CodeModeCallError("TypeError", $"{call}: `{property.Name}` is {(option.Type == "integer" ? "a positive integer" : option.Type == "object" ? "an object" : "a " + option.Type)}.");
                return false;
            }

            if (!values.TryAdd(option.Name, property.Value.Clone()))
            {
                error = new CodeModeCallError("TypeError", $"{call}: `{option.Name}` and `{option.Alias}` are the same option: give one.");
                return false;
            }
        }

        return true;
    }

    private static TimeSpan? Timeout(Dictionary<string, JsonElement> arguments)
        => arguments.TryGetValue("timeout", out var timeout) ? TimeSpan.FromMilliseconds(timeout.GetInt32()) : null;

    private static JsonElement? Schema(Dictionary<string, JsonElement> arguments)
        => arguments.TryGetValue("schema", out var schema) ? schema : null;

    private static string? String(Dictionary<string, JsonElement> arguments, string name)
        => arguments.TryGetValue(name, out var value) ? value.GetString() : null;

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
