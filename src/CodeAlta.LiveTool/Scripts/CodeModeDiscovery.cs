using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodeAlta.CodeMode;

namespace CodeAlta.LiveTool.Scripts;

/// <summary>
/// What a script learns of its own functions: <c>codemode.help()</c> lists the namespaces, <c>codemode.search()</c>
/// finds functions by what they do, <c>codemode.describe()</c> gives their TypeScript declarations. The answers come
/// in the script, after the cached part of the prompt, so the model learns the API without a longer prompt.
/// </summary>
public sealed partial class CodeModeDiscovery : ICodeModeCallHandler
{
    /// <summary>The namespace of discovery in scripts.</summary>
    public const string Namespace = "codemode";

    private readonly Func<CodeModeApi> _api;

    /// <summary>Creates the discovery of an API.</summary>
    /// <param name="api">The API of the run, the discovery functions included.</param>
    /// <exception cref="ArgumentNullException"><paramref name="api"/> is null.</exception>
    public CodeModeDiscovery(Func<CodeModeApi> api)
    {
        ArgumentNullException.ThrowIfNull(api);
        _api = api;
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
            Description = "Finds functions by what they do. Read the declaration of a result with describe before calling it.",
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
            $"{Namespace}.search" => Search(api, String(arguments, "query") ?? string.Empty, arguments.TryGetProperty("limit", out var limit) && limit.TryGetInt32(out var count) ? count : 10),
            $"{Namespace}.describe" => Describe(api, String(arguments, "path") ?? string.Empty),
            _ => CodeModeCallResult.Failure(new CodeModeCallError("TypeError", $"`{call.Path}` is not a function of {Namespace}.")),
        });
    }

    private static string Help(CodeModeApi api, string? path)
    {
        var prefix = path?.Trim().TrimEnd('.') ?? string.Empty;
        var functions = api.Under(prefix).ToArray();
        if (functions.Length == 0)
        {
            return $"No function under `{prefix}`. Namespaces: {string.Join(", ", Namespaces(api, string.Empty).Select(static entry => entry.Name))}.";
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

    private static CodeModeCallResult Search(CodeModeApi api, string query, int limit)
    {
        var words = query.ToLowerInvariant().Split([' ', '.', '_', '-', ',', '/'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 0)
        {
            return CodeModeCallResult.Failure(new CodeModeCallError("TypeError", "codemode.search({ query }): say what you want to do, in a few words."));
        }

        var hits = api.Functions
            .Where(static function => !function.Path.StartsWith(Namespace + ".", StringComparison.Ordinal))
            .Select(function => (Function: function, Score: Score(function, words)))
            .Where(static hit => hit.Score > 0)
            .OrderByDescending(static hit => hit.Score)
            .ThenBy(static hit => hit.Function.Path.Length)
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
            foreach (var (function, _) in hits)
            {
                writer.WriteStartObject();
                writer.WriteString("path", function.Path);
                writer.WriteString("description", FirstLine(function.Description));
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        return CodeModeCallResult.Success(Encoding.UTF8.GetString(stream.ToArray()));
    }

    // A word of the query in the path counts more than one in the description.
    private static int Score(CodeModeFunction function, string[] words)
    {
        var path = function.Path.ToLowerInvariant();
        var description = function.Description?.ToLowerInvariant() ?? string.Empty;
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
