using System.Globalization;
using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.CodeMode;
using CodeAlta.Orchestration.Runtime.Scripts;

namespace CodeAlta.LiveTool.Scripts;

/// <summary>The options of the Code Mode tools of a host.</summary>
public sealed record CodeModeToolOptions
{
    /// <summary>Gets the runs of the host.</summary>
    public required CodeModeRunRegistry Runs { get; init; }

    /// <summary>Gets the prepared scripts of the host.</summary>
    public required CodeModeScriptCache Cache { get; init; }

    /// <summary>Gets the limits of a run.</summary>
    public CodeModeLimits Limits { get; init; } = CodeModeLimits.Default;

    /// <summary>Gets how long <c>exec</c> waits for a run before it goes on in the background, when the script does not say.</summary>
    public TimeSpan DefaultYield { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Gets the folder where the runs of a session keep their source, by session id, or null to keep none.</summary>
    public Func<string, string?>? SourceFolder { get; init; }

    /// <summary>Gets whether <c>exec</c> takes over the other tools (the <c>only</c> mode): the model calls them through scripts.</summary>
    public bool TakesOver { get; init; }

    /// <summary>Gets the sessions scripts start with <c>agent()</c>, or null for a host whose scripts start none.</summary>
    public ICodeModeAgents? Agents { get; init; }
}

/// <summary>
/// Creates the tools of Code Mode: <c>exec</c>, which runs a script with every tool of the session as a function, and
/// <c>wait</c>, which waits for a script that went on in the background. Their names, descriptions and schemas never
/// change, so the prompt cache holds; what a script can call is found inside it (<c>codemode.help()</c>).
/// </summary>
public static class CodeModeToolFactory
{
    /// <summary>The name of the tool that runs a script.</summary>
    public const string ExecToolName = "exec";

    /// <summary>The name of the tool that waits for a script.</summary>
    public const string WaitToolName = "wait";

    private const int MaxWaitMilliseconds = 600_000;

    private const string ExecDescription = """
        Run JavaScript in CodeAlta to compose tool calls: loops, filters and parallel calls run without model turns, and only what the script returns comes back to you. Use it when a task needs several calls whose results you would otherwise read one by one; for a single call, call the tool directly.

        The script is the body of an async function: use `await` and `return`. It is plain JavaScript in strict mode: no TypeScript syntax, no `import`. It has no file, network or timer access of its own: every effect is a call of a function below, with this session's permissions.

        Functions (each takes one object and returns a promise of a value, not of text):
        - `tools.<name>(args)`: the tools of this session, such as `tools.read_file({ path })` (resolves to `{ content, startLine, endLine, more }`), `tools.grep({ pattern, path })`, `tools.shell_command({ command })` (resolves to `{ exitCode, stdout, stderr }`, also when the exit code is not 0).
        - `alta.<group>.<command>(args)`: the commands of `alta` as functions, not a shell, such as `alta.session.list({ limit: 20 })`. Positional arguments and options go by name in camelCase, and `stdin` holds what `--stdin` reads. Resolves to the record the command writes, or to the list of its records; a failure throws an `AltaError` with a `code`.
        - `mcp.<server>.<tool>(args)`: the tools of the MCP servers active in this session. A server that `alta.mcp.activate({ id: ["<server>"] })` activates is in `mcp` from the next `exec`, not in the script that activates it.
        - `codemode.help({ path })`, `codemode.search({ query })`, `codemode.describe({ path })`: find functions and read their TypeScript declarations, results included. Never guess a function or a field of a result: describe it first.
        - `agent(prompt, { tier, model, title })` starts a session of its own for a part of the work and resolves with its final answer; several run at once under `Promise.all`.
        - `step(id, fn)` groups the calls of one part of the work; `log(...)`, `console.log(...)` and `text(...)` write output; `args` holds the arguments of the run.

        Calls run at the same time under `Promise.all`; tools that change files run one at a time. Await every call whose result matters: calls still pending when the script returns are cancelled. A failed call throws an error you can catch, and the calls made before a failure are not undone.

        Options go on an optional first line: `// @exec: {"title": "…", "yield_ms": 30000, "max_output_tokens": 4000, "timeout_ms": 600000}`. `exec` waits `yield_ms` (30 s by default) for the script. A script still running then goes on in the background: call `wait` with its run id, or end your turn, and its result comes back to this session as a prompt.

        The result says where the source of the run was kept: to fix a long script, edit that file with your file tools and run it again with `return await workflow({ scriptPath: "<path>" })` instead of sending it whole.
        """;

    private const string ExecSchema = """
        {
          "type": "object",
          "properties": {
            "code": { "type": "string", "description": "The JavaScript, optionally starting with a // @exec: {...} line of options." }
          },
          "required": ["code"],
          "additionalProperties": false
        }
        """;

    private const string WaitDescription = "Wait for a script that `exec` left running in the background: returns its result, or what it wrote so far when `yield_ms` passes (at most 600000). `terminate: true` stops the script.";

    private const string WaitSchema = """
        {
          "type": "object",
          "properties": {
            "run": { "type": "string", "description": "The id of the run, such as run-7f3c2a." },
            "yield_ms": { "type": "integer", "minimum": 1, "description": "How long to wait, in milliseconds; 30000 by default, at most 600000." },
            "terminate": { "type": "boolean", "description": "Stop the script instead of waiting for it." }
          },
          "required": ["run"],
          "additionalProperties": false
        }
        """;

    /// <summary>Creates the <c>exec</c> and <c>wait</c> tools of a session.</summary>
    /// <param name="dispatcher">The dispatcher of <c>alta</c>.</param>
    /// <param name="caller">Who the session is, as for its <c>alta</c> tool.</param>
    /// <param name="options">The options of the host.</param>
    /// <returns>The two tools.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static IReadOnlyList<AgentToolDefinition> Create(AltaCommandDispatcher dispatcher, AltaSessionToolOptions caller, CodeModeToolOptions options)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(options);
        return
        [
            new AgentToolDefinition(
                new AgentToolSpec(ExecToolName, ExecDescription, Parse(ExecSchema)) { Exposure = AgentToolExposure.Model, ScriptsTakeOver = options.TakesOver },
                (invocation, cancellationToken) => ExecAsync(dispatcher, caller, options, invocation, cancellationToken)),
            new AgentToolDefinition(
                new AgentToolSpec(WaitToolName, WaitDescription, Parse(WaitSchema)) { Exposure = AgentToolExposure.Model },
                (invocation, cancellationToken) => WaitAsync(options, invocation, cancellationToken)),
        ];
    }

    private static async Task<AgentToolResult> ExecAsync(AltaCommandDispatcher dispatcher, AltaSessionToolOptions caller, CodeModeToolOptions options, AgentToolInvocation invocation, CancellationToken cancellationToken)
    {
        if (invocation.Arguments.ValueKind != JsonValueKind.Object || !invocation.Arguments.TryGetProperty("code", out var codeElement) || codeElement.ValueKind != JsonValueKind.String)
        {
            return Failure("exec needs `code`: the JavaScript to run.");
        }

        var source = CodeModeSource.Prepare(codeElement.GetString() ?? string.Empty);
        if (source.Error is not null)
        {
            return Finished(new CodeModeRunResult(CodeModeRunStatus.Failed, TimeSpan.Zero) { Error = source.Error, Source = source }, source, null);
        }

        var sessionId = invocation.SessionId;
        var cwd = caller.WorkingDirectoryProvider?.Invoke() ?? caller.WorkingDirectory;
        var identity = new AltaCallerIdentity
        {
            Kind = string.IsNullOrWhiteSpace(caller.CallerKind) ? "agent" : caller.CallerKind,
            // A run names its own session: the session the user looks at may be another one.
            SourceSessionId = invocation.RunTools is not null ? sessionId : caller.SourceSessionIdProvider?.Invoke() ?? caller.SourceSessionId,
            SourceAgentId = caller.SourceAgentId,
            SourceProjectId = caller.SourceProjectIdProvider?.Invoke() ?? caller.SourceProjectId,
            PluginRuntimeKey = caller.PluginRuntimeKey,
            RunTools = invocation.RunTools,
        };

        var alta = new AltaScriptBinding(dispatcher.Describe(identity, cwd));
        var tools = invocation.Tools is { } invoker ? new ToolScriptBinding(invoker, ["alta", ExecToolName, WaitToolName]) : null;
        CodeModeApi? api = null;
        api = new CodeModeApi([.. alta.Functions, .. tools?.Functions ?? [], .. CodeModeDiscovery.Functions,
            .. options.Agents is null ? [] : (CodeModeFunction[])[CodeModeAgentBinding.Function]]);
        var discovery = new CodeModeDiscovery(() => api);
        var limits = source.Options.TimeoutMilliseconds is { } timeout ? options.Limits with { Timeout = TimeSpan.FromMilliseconds(timeout) } : options.Limits;
        var maxOutputTokens = source.Options.MaxOutputTokens ?? CodeModeResultFormatter.DefaultMaxOutputTokens;
        var host = new CodeModeHost(api, options.Cache);

        var run = options.Runs.TryStart(sessionId, source.Options.Title, maxOutputTokens, invocation.ToolCallId, (observer, runCancellation) =>
            {
                // The observer of a run is its handle: the sessions it starts know its id.
                var agents = options.Agents is { } sessions ? new CodeModeAgentBinding(sessions, sessionId, () => ((CodeModeRunHandle)observer).Id) : null;
                var handler = new CodeModeCallRouter(
                [
                    .. agents is null ? [] : (ReadOnlySpan<(string, ICodeModeCallHandler)>)[(CodeModeAgentBinding.Path, agents)],
                    (AltaScriptBinding.Namespace, new AltaScriptCallHandler(dispatcher, alta, identity, cwd)),
                    (ToolScriptBinding.ToolsNamespace, (ICodeModeCallHandler?)tools ?? new CodeModeCallRouter([])),
                    (ToolScriptBinding.McpNamespace, (ICodeModeCallHandler?)tools ?? new CodeModeCallRouter([])),
                    (CodeModeDiscovery.Namespace, discovery),
                ]);
                return host.RunAsync(source, handler, new CodeModeRunOptions { Limits = limits, ResolveScript = ResolveWith(invocation.Tools) }, observer, runCancellation);
            }, out var refusal);
        if (run is null)
        {
            return Failure(refusal!);
        }

        var sourcePath = KeepSource(options, sessionId, run.Id, source);

        // While the turn waits for the run, stopping the turn stops it; once it goes on in the background, it no longer does.
        var yieldTime = source.Options.YieldMilliseconds is { } yieldMs ? TimeSpan.FromMilliseconds(yieldMs) : options.DefaultYield;
        using (cancellationToken.Register(static state => ((CodeModeRunHandle)state!).Stop(), run))
        {
            try
            {
                await run.Completion.WaitAsync(yieldTime, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                run.Stop();
                throw;
            }

            if (!run.Completion.IsCompleted)
            {
                run.Background();
            }
        }

        if (run.Completion.IsCompleted && run.TryCollect())
        {
            return Finished(await run.Completion.ConfigureAwait(false), source, sourcePath);
        }

        return new AgentToolResult(true, [new AgentToolResultItem.Text(
            CodeModeResultFormatter.FormatRunning(run.Id, run.Elapsed, run.Calls, run.RecentOutput()) + SourceLine(sourcePath))]);
    }

    private static async Task<AgentToolResult> WaitAsync(CodeModeToolOptions options, AgentToolInvocation invocation, CancellationToken cancellationToken)
    {
        var arguments = invocation.Arguments;
        if (arguments.ValueKind != JsonValueKind.Object || !arguments.TryGetProperty("run", out var runElement) || runElement.ValueKind != JsonValueKind.String)
        {
            return Failure("wait needs `run`: the id `exec` gave, such as run-7f3c2a.");
        }

        var run = options.Runs.Find(invocation.SessionId, runElement.GetString()!);
        if (run is null)
        {
            var known = options.Runs.List(invocation.SessionId).Select(static info => info.Id).ToArray();
            return Failure($"No run `{runElement.GetString()}` of this session.{(known.Length == 0 ? string.Empty : " Its runs: " + string.Join(", ", known) + ".")}");
        }

        if (arguments.TryGetProperty("terminate", out var terminate) && terminate.ValueKind == JsonValueKind.True)
        {
            run.Stop();
        }

        var yieldTime = arguments.TryGetProperty("yield_ms", out var yieldElement) && yieldElement.TryGetInt32(out var yieldMs) && yieldMs > 0
            ? TimeSpan.FromMilliseconds(Math.Min(yieldMs, MaxWaitMilliseconds))
            : options.DefaultYield;
        var (ended, result) = await options.Runs.WaitAsync(run, yieldTime, cancellationToken).ConfigureAwait(false);
        if (ended)
        {
            return result is null
                ? new AgentToolResult(true, [new AgentToolResultItem.Text($"Script {run.Id} ended; its result was already given to this session.")])
                : new AgentToolResult(result.Status == CodeModeRunStatus.Completed, [new AgentToolResultItem.Text(CodeModeResultFormatter.Format(result, run.MaxOutputTokens))]);
        }

        return new AgentToolResult(true, [new AgentToolResultItem.Text(CodeModeResultFormatter.FormatRunning(run.Id, run.Elapsed, run.Calls, run.RecentOutput()))]);
    }

    // workflow({ scriptPath }) reads the file with the session's own read_file, so its sandbox and its tools apply.
    private static Func<CodeModeScriptReference, CancellationToken, ValueTask<CodeModeSource>> ResolveWith(IAgentRunToolInvoker? tools)
        => async (reference, cancellationToken) =>
        {
            if (reference.ScriptPath is not { Length: > 0 } path)
            {
                throw new InvalidOperationException($"No script library yet: name a script file with workflow({{ scriptPath }}) instead of `{reference.Name}`.");
            }

            if (tools is null)
            {
                throw new InvalidOperationException("This session cannot read files: workflow({ scriptPath }) is not available.");
            }

            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                writer.WriteString("path", path);
                writer.WriteNumber("limit", 100_000);
                writer.WriteEndObject();
            }

            using var arguments = JsonDocument.Parse(stream.ToArray());
            var read = await tools.InvokeAsync(new AgentNestedToolCall("read_file", arguments.RootElement.Clone()), cancellationToken).ConfigureAwait(false);
            if (!read.Success || read.Structured is not { } value)
            {
                throw new InvalidOperationException(read.Error ?? $"`{path}` could not be read.");
            }

            if (value.TryGetProperty("more", out var more) && more.ValueKind == JsonValueKind.True)
            {
                throw new InvalidOperationException($"`{path}` is longer than a script read in one call.");
            }

            return CodeModeSource.Prepare(value.GetProperty("content").GetString() ?? string.Empty, Path.GetFileName(path));
        };

    private static AgentToolResult Finished(CodeModeRunResult result, CodeModeSource source, string? sourcePath)
    {
        var text = CodeModeResultFormatter.Format(result, source.Options.MaxOutputTokens ?? CodeModeResultFormatter.DefaultMaxOutputTokens) + SourceLine(sourcePath);
        return new AgentToolResult(result.Status == CodeModeRunStatus.Completed, [new AgentToolResultItem.Text(text)], result.Error is { } error ? $"{error.Name}: {error.Message}" : null);
    }

    private static string SourceLine(string? sourcePath) => sourcePath is null ? string.Empty : $"Source: {sourcePath}\n";

    // The source of each run is kept as a file, so that the model fixes a long script with its file tools.
    private static string? KeepSource(CodeModeToolOptions options, string sessionId, string runId, CodeModeSource source)
    {
        if (options.SourceFolder?.Invoke(sessionId) is not { } folder)
        {
            return null;
        }

        try
        {
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, runId + ".js");
            File.WriteAllText(path, source.Original);
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static AgentToolResult Failure(string message) => new(false, [new AgentToolResultItem.Text(message)], message);

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    /// <summary>Formats the line a timeline shows for a run, such as <c>exec · Review PR 231</c>.</summary>
    /// <param name="title">The title of the run.</param>
    /// <returns>The line.</returns>
    public static string DisplayName(string? title) => string.IsNullOrWhiteSpace(title) ? ExecToolName : string.Create(CultureInfo.InvariantCulture, $"{ExecToolName} · {title}");
}
