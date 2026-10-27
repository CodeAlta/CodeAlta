using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodeAlta.CodeMode;
using CodeAlta.Orchestration.Runtime.Scripts;

namespace CodeAlta.LiveTool.Scripts;

/// <summary>
/// What a script learns of its own functions: <c>codemode.help()</c> lists the namespaces, <c>codemode.search()</c>
/// finds functions by what they do, <c>codemode.describe()</c> gives their TypeScript declarations. The answers come
/// in the script, after the cached part of the prompt, so the model learns the API without a longer prompt. The scripts
/// of the library are found too, as <c>workflow:&lt;name&gt;</c>, which <c>workflow("name", args)</c> runs.
/// </summary>
public sealed partial class CodeModeDiscovery : ICodeModeCallHandler
{
    /// <summary>The namespace of discovery in scripts.</summary>
    public const string Namespace = "codemode";

    /// <summary>The prefix of the paths of the scripts of the library in the results of search.</summary>
    public const string WorkflowPrefix = "workflow:";

    private readonly Func<CodeModeApi> _api;
    private readonly Func<IReadOnlyList<CodeModeLibraryScript>>? _scripts;

    /// <summary>Creates the discovery of an API.</summary>
    /// <param name="api">The API of the run, the discovery functions included.</param>
    /// <exception cref="ArgumentNullException"><paramref name="api"/> is null.</exception>
    public CodeModeDiscovery(Func<CodeModeApi> api)
        : this(api, null)
    {
    }

    /// <summary>Creates the discovery of an API and of the scripts of a library.</summary>
    /// <param name="api">The API of the run, the discovery functions included.</param>
    /// <param name="scripts">Lists the scripts of the library the run sees (those of its project and of its space), or null for none.</param>
    /// <exception cref="ArgumentNullException"><paramref name="api"/> is null.</exception>
    public CodeModeDiscovery(Func<CodeModeApi> api, Func<IReadOnlyList<CodeModeLibraryScript>>? scripts)
    {
        ArgumentNullException.ThrowIfNull(api);
        _api = api;
        _scripts = scripts;
    }

    /// <summary>Gets the discovery functions, for the API of a run.</summary>
    public static IReadOnlyList<CodeModeFunction> Functions { get; } =
    [
        new($"{Namespace}.help")
        {
            Description = "Lists the namespaces of this session, or the functions of one (`{ path: \"alta.session\" }`), with one line each.",
            InputSchemaJson = """{"type":"object","properties":{"path":{"type":"string","description":"A namespace, such as alta.session or mcp.github."}}}""",
            ResultType = "string",
        },
        new($"{Namespace}.search")
        {
            Description = "Finds functions, and scripts of the library (`workflow:<name>`, run with workflow(\"name\", args)), by what they do. Read the declaration of a result with describe before calling it.",
            InputSchemaJson = """{"type":"object","properties":{"query":{"type":"string","description":"What you want to do, in a few words."},"limit":{"type":"integer","minimum":1,"description":"The most results, 10 by default."}},"required":["query"]}""",
            // A string says why nothing matched.
            ResultType = "{ path: string; description: string }[] | string",
        },
        new($"{Namespace}.describe")
        {
            Description = "Gives the TypeScript declaration of a function, or of every function of a namespace.",
            InputSchemaJson = """{"type":"object","properties":{"path":{"type":"string","description":"A function or a namespace, such as alta.session.create."}},"required":["path"]}""",
            ResultType = "string",
        },
    ];

    // The functions a script calls by their own name. They are what scripts are written with, so search gives those that
    // match first, and help and describe know them: models that searched for sessions found only `alta.session.*`, whose
    // send does not wait for the answer. One that needs a function of the host is there only when the run has it.
    private static IReadOnlyList<GlobalFunction> Globals { get; } =
    [
        new("agent", "Starts a sub-agent: creates a child session for a part of the work, sends it the prompt, waits for it and resolves with its final answer. Several at once with Promise.all or parallel; worktree: true gives each its own git worktree and branch; subSessions: false creates it without sessions of its own.",
            "declare function agent(prompt: string, options?: { tier?: \"complex\" | \"standard\" | \"quick\"; model?: string; title?: string; schema?: object; worktree?: boolean; base?: string; subSessions?: boolean }): Promise<any>;",
            CodeModeAgentBinding.Path),
        new("session", "Creates a sub-agent session the script keeps for several prompts: run(prompt) sends one and resolves with its answer, close() ends it; with worktree: true it works in its own git worktree, named by worktree.",
            "declare function session(options?: { tier?: \"complex\" | \"standard\" | \"quick\"; model?: string; title?: string; worktree?: boolean; base?: string; subSessions?: boolean }): Promise<{ id: string; worktree: { folder: string; branch: string } | null; run(prompt: string, options?: { schema?: object }): Promise<any>; close(): Promise<void> }>;",
            CodeModeAgentBinding.Path),
        new("workflow", "Runs a script of the library by its name (search finds them as workflow:<name>), or a script file, with its arguments, and resolves with what it returns.",
            "declare function workflow(name: string, args?: Record<string, unknown>): Promise<any>;\ndeclare function workflow(file: { scriptPath: string }, args?: Record<string, unknown>): Promise<any>;",
            null),
        new("parallel", "Runs functions at once, at most limit (8) at a time; a failure is null in the results and a warning in the output.",
            "declare function parallel<T>(thunks: (() => Promise<T>)[], options?: { limit?: number }): Promise<(T | null)[]>;",
            null),
        new("pipeline", "Takes each item through stages on its own, without waiting for the other items at each stage; an item that fails is null.",
            "declare function pipeline(items: unknown[], ...stages: ((value: any, index: number) => unknown)[]): Promise<unknown[]>;",
            null),
        new("step", "Groups the calls of one part of the work under an id; a resumed run replays the recorded result of a step that completed.",
            "declare function step<T>(id: string, fn: () => T | Promise<T>): Promise<T>;",
            null),
        new("ask", "Asks the user a question and resolves with the answer; throws an AskError when nobody can answer.",
            "declare function ask(question: string, options?: { choices?: string[]; default?: string }): Promise<string>;",
            CodeModeAskBinding.Path),
        new("image", "Attaches a picture file (PNG, JPEG, GIF or WebP, 8 MB at most) to the result of the script: the user sees it, and the model too. A relative path starts from the working folder; 8 pictures per run at most.",
            "declare function image(path: string, options?: { title?: string }): void;",
            null),
        new("sleep", "Waits: milliseconds, or \"30s\", \"5m\", \"2h\", \"1d\" (7 days at most).",
            "declare function sleep(duration: number | string): Promise<void>;",
            null),
        new("store", "Keeps a JSON value for the later scripts of this session; load(key) reads it back.",
            "declare function store(key: string, value: unknown): Promise<void>;\ndeclare function load(key: string): Promise<any>;",
            CodeModeValueBinding.StorePath),
    ];

    /// <inheritdoc />
    public ValueTask<CodeModeCallResult> InvokeAsync(CodeModeCall call, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(call);
        using var document = JsonDocument.Parse(call.ArgumentsJson);
        var arguments = document.RootElement;
        var api = _api();
        return ValueTask.FromResult(call.Path switch
        {
            $"{Namespace}.help" => CodeModeCallResult.Text(Help(api, String(arguments, "path"))),
            $"{Namespace}.search" => Search(api, Scripts(), String(arguments, "query") ?? string.Empty, arguments.TryGetProperty("limit", out var limit) && limit.TryGetInt32(out var count) ? count : 10),
            $"{Namespace}.describe" => String(arguments, "path")?.Trim() is { } scriptPath && scriptPath.StartsWith(WorkflowPrefix, StringComparison.Ordinal)
                ? DescribeScript(Scripts(), scriptPath[WorkflowPrefix.Length..])
                : FindGlobal(api, String(arguments, "path")) is { } global ? CodeModeCallResult.Text(DescribeGlobal(global))
                : Describe(api, String(arguments, "path") ?? string.Empty),
            _ => CodeModeCallResult.Failure(new CodeModeCallError("TypeError", $"`{call.Path}` is not a function of {Namespace}.")),
        });
    }

    private static string Help(CodeModeApi api, string? path)
    {
        var prefix = path?.Trim().TrimEnd('.') ?? string.Empty;
        var functions = api.Under(prefix).ToArray();
        if (FindGlobal(api, prefix) is { } global)
        {
            return DescribeGlobal(global);
        }

        if (functions.Length == 0)
        {
            return $"No function under `{prefix}`. Namespaces: {string.Join(", ", Namespaces(api, string.Empty).Select(static entry => entry.Name))}. {GlobalsLine(api)}";
        }

        var builder = new StringBuilder();
        var depth = prefix.Length == 0 ? 0 : prefix.Count(static c => c == '.') + 1;
        var groups = Namespaces(api, prefix).ToArray();
        if (groups.Length > 12 || prefix.Length == 0)
        {
            foreach (var (name, count) in groups)
            {
                builder.Append(name).Append(" (").Append(count.ToString(CultureInfo.InvariantCulture)).Append(count == 1 ? " function)\n" : " functions)\n");
            }

            builder.Append("Call codemode.help({ path }) for the functions of a namespace, codemode.describe({ path }) for their declarations.");
            if (prefix.Length == 0)
            {
                builder.Append('\n').Append(GlobalsLine(api));
            }

            return builder.ToString();
        }

        foreach (var function in functions.Take(200))
        {
            builder.Append(function.Path).Append(" — ").Append(FirstLine(function.Description)).Append('\n');
        }

        if (functions.Length > 200)
        {
            builder.Append("… ").Append((functions.Length - 200).ToString(CultureInfo.InvariantCulture)).Append(" more: narrow the path.\n");
        }

        _ = depth;
        return builder.ToString().TrimEnd();
    }

    // The namespaces one level below a prefix, with the number of functions under each.
    private static IEnumerable<(string Name, int Count)> Namespaces(CodeModeApi api, string prefix)
    {
        var depth = prefix.Length == 0 ? 1 : prefix.Count(static c => c == '.') + 2;
        return api.Under(prefix)
            .Select(function => string.Join('.', function.Path.Split('.').Take(depth)))
            .GroupBy(static name => name, StringComparer.Ordinal)
            .Select(static group => (group.Key, group.Count()));
    }

    // The scripts of the library, or none when it cannot be read.
    private IReadOnlyList<CodeModeLibraryScript> Scripts()
    {
        try
        {
            return _scripts?.Invoke() ?? [];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static CodeModeCallResult DescribeScript(IReadOnlyList<CodeModeLibraryScript> scripts, string name)
        => scripts.FirstOrDefault(script => script.Name == name) is { } script
            ? CodeModeCallResult.Text(BuiltInAltaCommandContributor.Declaration(script))
            : CodeModeCallResult.Failure(new CodeModeCallError("TypeError", $"No script `{name}` in the library of this session: alta.script.list({{}}) lists them."));

    private static CodeModeCallResult Search(CodeModeApi api, IReadOnlyList<CodeModeLibraryScript> scripts, string query, int limit)
    {
        var words = query.ToLowerInvariant().Split([' ', '.', '_', '-', ',', '/'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 0)
        {
            return CodeModeCallResult.Failure(new CodeModeCallError("TypeError", "codemode.search({ query }): say what you want to do, in a few words."));
        }

        // The functions called by name that match two words or more come first, the best first; the others are ranked with
        // the namespaces and the library.
        var globals = AvailableGlobals(api)
            .Select(global => (Path: global.Name, Description: global.Description, Score: Score(global.Name, global.Description, words)))
            .Where(static hit => hit.Score > 0)
            .ToArray();
        var hits = globals.Where(static hit => hit.Score >= 2).OrderByDescending(static hit => hit.Score).Concat(api.Functions
            .Where(static function => !function.Path.StartsWith(Namespace + ".", StringComparison.Ordinal))
            .Select(function => (Path: function.Path, Description: FirstLine(function.Description), Score: Score(function.Path, function.Description, words)))
            .Concat(scripts.Where(static script => script.Problem is null).Select(script => (Path: WorkflowPrefix + script.Name,
                Description: $"Script of the library, run with workflow(\"{script.Name}\", args): {FirstLine(script.Description)}",
                // A query for library scripts or workflows finds them all.
                Score: Score(script.Name, "script library workflow\n" + script.Description + "\n" + script.Source.Descriptor?.WhenToUse, words)))
            .Concat(globals.Where(static hit => hit.Score < 2)))
            .Where(static hit => hit.Score > 0)
            .OrderByDescending(static hit => hit.Score)
            .ThenBy(static hit => hit.Path.Length))
            .Take(Math.Clamp(limit, 1, 50))
            .ToArray();
        if (hits.Length == 0)
        {
            // Nothing found is not "nothing exists": the tools of an MCP server that is not active are not functions yet.
            return CodeModeCallResult.Text($"No function matches `{query}`. Only the tools of active MCP servers are functions: alta.mcp.list({{}}) lists the servers, and alta.mcp.activate({{ id: [\"<server>\"] }}) makes the tools of one functions of the next exec. codemode.help() lists the namespaces.");
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartArray();
            foreach (var (path, description, _) in hits)
            {
                writer.WriteStartObject();
                writer.WriteString("path", path);
                writer.WriteString("description", description);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        return CodeModeCallResult.Success(Encoding.UTF8.GetString(stream.ToArray()));
    }

    private static IEnumerable<GlobalFunction> AvailableGlobals(CodeModeApi api)
        => Globals.Where(global => global.Requires is null || api.Under(global.Requires).Any());

    // A function called by name, `load` with `store`.
    private static GlobalFunction? FindGlobal(CodeModeApi api, string? name)
    {
        var trimmed = name?.Trim().TrimEnd('(', ')', ' ');
        return string.IsNullOrEmpty(trimmed) ? null
            : AvailableGlobals(api).FirstOrDefault(global => global.Name == trimmed || global.Declaration.Contains($"function {trimmed}(", StringComparison.Ordinal));
    }

    private static string DescribeGlobal(GlobalFunction global) => $"/** {global.Description} */\n{global.Declaration}";

    private static string GlobalsLine(CodeModeApi api)
    {
        var globals = AvailableGlobals(api).ToArray();
        return $"Functions called by name: {string.Join(", ", globals.Select(static global => global.Name == "store" ? "store, load" : global.Name))} (codemode.describe({{ path: \"{globals[0].Name}\" }}) gives one).";
    }

    // A function a script calls by its name, outside the namespaces.
    private sealed record GlobalFunction(string Name, string Description, string Declaration, string? Requires);

    // A word of the query in the path (or the name of a script) counts more than one in the description.
    private static int Score(string functionPath, string? text, string[] words)
    {
        var path = functionPath.ToLowerInvariant();
        var description = text?.ToLowerInvariant() ?? string.Empty;
        var score = 0;
        foreach (var word in words)
        {
            if (path.Contains(word, StringComparison.Ordinal))
            {
                score += 3;
            }
            else if (description.Contains(word, StringComparison.Ordinal))
            {
                score += 1;
            }
        }

        return score;
    }

    private static CodeModeCallResult Describe(CodeModeApi api, string path)
    {
        var trimmed = path.Trim().TrimEnd('.');
        var functions = api.Under(trimmed).ToArray();
        if (functions.Length == 0)
        {
            var near = api.Functions.Select(static function => function.Path)
                .OrderBy(candidate => Distance(candidate, trimmed))
                .FirstOrDefault();
            return CodeModeCallResult.Failure(new CodeModeCallError("TypeError",
                $"`{trimmed}` is not a function or a namespace of this session.{(near is null ? string.Empty : $" Did you mean `{near}`?")}"));
        }

        var declarations = functions.Length == 1 && functions[0].Path == trimmed
            ? CodeModeDeclarations.RenderFunction(functions[0])
            : CodeModeDeclarations.Render(functions.Take(100));
        if (trimmed.StartsWith(AltaScriptBinding.Namespace + ".", StringComparison.Ordinal) || trimmed == AltaScriptBinding.Namespace)
        {
            declarations += ReferencedTypes(declarations, AltaScriptBinding.Types);
        }

        return CodeModeCallResult.Text(declarations);
    }

    /// <summary>
    /// Keeps of shared type declarations those a declaration uses, and those they use in turn: the declaration of one
    /// command does not bury its line under every type of every command.
    /// </summary>
    /// <param name="declarations">The declarations that use the types.</param>
    /// <param name="types">The shared types, one declaration (with its comment) per block, blocks separated by a blank line.</param>
    /// <returns>The types used, each on lines of its own, or an empty string.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static string ReferencedTypes(string declarations, string types)
    {
        ArgumentNullException.ThrowIfNull(declarations);
        ArgumentNullException.ThrowIfNull(types);
        var blocks = new List<(string Name, string Text)>();
        foreach (var block in types.Replace("\r\n", "\n", StringComparison.Ordinal).Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var match = DeclaredName().Match(block);
            if (match.Success)
            {
                blocks.Add((match.Groups[1].Value, block));
            }
        }

        var included = new HashSet<string>(StringComparer.Ordinal);
        var text = new StringBuilder();
        var pending = new Queue<string>([declarations]);
        while (pending.TryDequeue(out var source))
        {
            foreach (var (name, block) in blocks)
            {
                if (!included.Contains(name) && Regex.IsMatch(source, $@"\b{name}\b"))
                {
                    included.Add(name);
                    pending.Enqueue(block);
                }
            }
        }

        foreach (var (name, block) in blocks)
        {
            if (included.Contains(name))
            {
                text.Append('\n').Append(block).Append('\n');
            }
        }

        return text.ToString();
    }

    [GeneratedRegex(@"(?:interface|type)\s+(\w+)")]
    private static partial Regex DeclaredName();

    private static string? String(JsonElement arguments, string name)
        => arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string FirstLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var line = text.Trim().Split('\n')[0].Trim();
        return line.Length > 160 ? line[..160] + "…" : line;
    }

    private static int Distance(string a, string b)
    {
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++)
        {
            previous[j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }
}

/// <summary>Sends each call of a script to the handler of its namespace.</summary>
public sealed class CodeModeCallRouter : ICodeModeCallHandler
{
    private readonly IReadOnlyList<(string Namespace, ICodeModeCallHandler Handler)> _handlers;

    /// <summary>Creates a router.</summary>
    /// <param name="handlers">The handlers, by the path they run or by the first segment of the paths they run.</param>
    /// <exception cref="ArgumentNullException"><paramref name="handlers"/> is null.</exception>
    public CodeModeCallRouter(IReadOnlyList<(string Namespace, ICodeModeCallHandler Handler)> handlers)
    {
        ArgumentNullException.ThrowIfNull(handlers);
        _handlers = handlers;
    }

    /// <inheritdoc />
    public ValueTask<CodeModeCallResult> InvokeAsync(CodeModeCall call, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(call);
        // A handler of the exact path first (codemode.agent), then the handler of its namespace.
        foreach (var (name, handler) in _handlers)
        {
            if (name == call.Path)
            {
                return handler.InvokeAsync(call, cancellationToken);
            }
        }

        var root = call.Path.Split('.', 2)[0];
        foreach (var (name, handler) in _handlers)
        {
            if (name == root)
            {
                return handler.InvokeAsync(call, cancellationToken);
            }
        }

        return ValueTask.FromResult(CodeModeCallResult.Failure(new CodeModeCallError("TypeError", $"`{call.Path}` is not a function of this session.")));
    }
}
