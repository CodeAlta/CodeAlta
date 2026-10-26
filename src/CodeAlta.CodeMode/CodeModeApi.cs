using System.Collections.Frozen;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodeAlta.CodeMode;

/// <summary>
/// The functions a script of one session can call, grouped by their paths (<c>tools.read_file</c>,
/// <c>alta.session.create</c>). It generates the prelude that builds those namespaces in the engine, and the
/// TypeScript declarations the model reads. Immutable: a session builds a new one when its tools change.
/// </summary>
public sealed class CodeModeApi
{
    /// <summary>The global names of the helpers, which no namespace of an API may take.</summary>
    public static IReadOnlySet<string> ReservedNames { get; } = new[]
    {
        "args", "console", "log", "text", "step", "meta", "workflow", "agent", "session", "parallel",
        "pipeline", "phase", "budget", "store", "load", "exit", "globalThis", "Math", "JSON", "Promise",
    }.ToFrozenSet(StringComparer.Ordinal);

    private readonly FrozenDictionary<string, CodeModeFunction> _byPath;

    /// <summary>Creates an API.</summary>
    /// <param name="functions">The functions. Each path has at least two segments, and its first one is not a reserved name.</param>
    /// <exception cref="ArgumentNullException"><paramref name="functions"/> is null.</exception>
    /// <exception cref="ArgumentException">A path is empty, has one segment, takes a reserved name, or appears twice.</exception>
    public CodeModeApi(IEnumerable<CodeModeFunction> functions)
    {
        ArgumentNullException.ThrowIfNull(functions);
        var list = functions.ToList();
        var byPath = new Dictionary<string, CodeModeFunction>(StringComparer.Ordinal);
        foreach (var function in list)
        {
            var segments = function.Path.Split('.');
            if (segments.Length < 2 || segments.Any(string.IsNullOrEmpty))
            {
                throw new ArgumentException($"The path `{function.Path}` must be a namespace and a name, such as `tools.read_file`.", nameof(functions));
            }

            if (ReservedNames.Contains(segments[0]))
            {
                throw new ArgumentException($"The namespace `{segments[0]}` of `{function.Path}` is a name of the helpers.", nameof(functions));
            }

            if (!byPath.TryAdd(function.Path, function))
            {
                throw new ArgumentException($"The path `{function.Path}` appears twice.", nameof(functions));
            }
        }

        Functions = list;
        _byPath = byPath.ToFrozenDictionary(StringComparer.Ordinal);
        Prelude = BuildPrelude(list);
        Hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Prelude)));
        Declarations = CodeModeDeclarations.Render(list);
    }

    /// <summary>Gets an API without functions: scripts then only compute and write output.</summary>
    public static CodeModeApi Empty { get; } = new([]);

    /// <summary>Gets the functions, in their order.</summary>
    public IReadOnlyList<CodeModeFunction> Functions { get; }

    /// <summary>Gets the SHA-256 of the prelude: two APIs with the same hash build the same namespaces.</summary>
    public string Hash { get; }

    /// <summary>Gets the TypeScript declarations of the functions.</summary>
    public string Declarations { get; }

    /// <summary>Gets the JavaScript that builds the namespaces and the helpers in an engine.</summary>
    internal string Prelude { get; }

    /// <summary>Finds a function by its path.</summary>
    /// <param name="path">The path.</param>
    /// <returns>The function, or null.</returns>
    public CodeModeFunction? Find(string path) => _byPath.GetValueOrDefault(path);

    /// <summary>Lists the functions whose path starts with a prefix, such as <c>alta.session</c>.</summary>
    /// <param name="prefix">The prefix, without a final dot; empty for every function.</param>
    /// <returns>The functions.</returns>
    public IEnumerable<CodeModeFunction> Under(string prefix)
        => string.IsNullOrEmpty(prefix)
            ? Functions
            : Functions.Where(function => function.Path == prefix || function.Path.StartsWith(prefix + ".", StringComparison.Ordinal));

    // The prelude takes the host functions the engine installs, hides them in a closure, and defines read-only
    // globals: the namespaces, `console`, `log`, `text` and `step`. Each run has its own engine, so a script can only
    // ever break its own globals; the bridge itself is out of its reach.
    private static string BuildPrelude(IReadOnlyList<CodeModeFunction> functions)
    {
        var builder = new StringBuilder();
        builder.Append("""
            (() => {
            "use strict";
            const host = globalThis.__host, writeOutput = globalThis.__output, stepRun = globalThis.__stepRun,
              stepBegin = globalThis.__stepBegin, stepEnd = globalThis.__stepEnd, random = globalThis.__random,
              argsJson = globalThis.__args;
            for (const name of ["__host", "__output", "__stepRun", "__stepBegin", "__stepEnd", "__random", "__args"]) delete globalThis[name];
            const stringify = JSON.stringify, parse = JSON.parse, freeze = Object.freeze, define = Object.defineProperty;
            class CodeModeError extends Error {
              constructor(e) {
                super(e.message);
                define(this, "name", { value: e.name, writable: true, configurable: true });
                if (e.code !== undefined) this.code = e.code;
                if (e.data !== undefined) this.data = e.data;
              }
            }
            const unwrap = (json) => {
              const reply = parse(json);
              if (reply.ok) return reply.value;
              throw new CodeModeError(reply.error);
            };
            // A plain function that calls the host at once: an async function in between would hide the script's
            // frames from the engine's stack, which gives the host the line of each call.
            const fn = (path) => function (args) {
              if (args !== undefined && (args === null || typeof args !== "object" || Array.isArray(args))) {
                return Promise.reject(new TypeError(`${path}(args): args must be an object, such as { path: "src/app.ts" }.`));
              }
              return host(path, args === undefined ? "{}" : stringify(args)).then(unwrap);
            };
            const message = (e) => (e !== null && typeof e === "object" && e.message !== undefined) ? String(e.message) : String(e);
            const format = (values) => values.map((v) => {
              if (typeof v === "string") return v;
              try { const s = stringify(v); return s === undefined ? String(v) : s; } catch { return String(v); }
            }).join(" ");
            const global = (name, value) => define(globalThis, name, { value, writable: false, enumerable: false, configurable: false });
            global("console", freeze({
              log: (...v) => writeOutput(1, format(v)), info: (...v) => writeOutput(1, format(v)), debug: (...v) => writeOutput(1, format(v)),
              warn: (...v) => writeOutput(2, format(v)), error: (...v) => writeOutput(3, format(v)),
            }));
            global("log", (...v) => writeOutput(1, format(v)));
            global("text", (...v) => writeOutput(0, format(v)));
            global("step", (id, body) => {
              if (typeof id !== "string" || id.length === 0) throw new TypeError("step(id, fn): id must be a non-empty string.");
              if (typeof body !== "function") throw new TypeError("step(id, fn): fn must be a function.");
              const handle = stepBegin(id);
              let result;
              try { result = stepRun(id, body); } catch (e) { stepEnd(handle, false, message(e)); throw e; }
              return Promise.resolve(result).then((v) => { stepEnd(handle, true, null); return v; }, (e) => { stepEnd(handle, false, message(e)); throw e; });
            });
            global("workflow", function (reference, workflowArgs) {
              const target = typeof reference === "string" ? { name: reference } : reference;
              if (target === null || typeof target !== "object" || (typeof target.name !== "string" && typeof target.scriptPath !== "string")) {
                return Promise.reject(new TypeError("workflow(name | { scriptPath }, args): name a script of the library, or the path of a script file."));
              }
              return host("codemode.workflow", stringify({ name: target.name, scriptPath: target.scriptPath, args: workflowArgs === undefined ? {} : workflowArgs })).then(unwrap);
            });
            global("args", freeze(argsJson === undefined ? {} : parse(argsJson)));
            define(Math, "random", { value: random, writable: false, enumerable: false, configurable: false });

            """);

        var root = new PathNode();
        foreach (var function in functions)
        {
            var node = root;
            foreach (var segment in function.Path.Split('.'))
            {
                node = node.Child(segment);
            }

            node.Function = function.Path;
        }

        foreach (var (name, node) in root.Children)
        {
            builder.Append("global(").Append(Quote(name)).Append(", ");
            AppendNode(builder, node);
            builder.Append(");\n");
        }

        builder.Append("})();\n");
        return builder.ToString();
    }

    // A path that is both a function and a namespace (`alta.search` and `alta.search.show`) is a function that carries
    // its members.
    private static void AppendNode(StringBuilder builder, PathNode node)
    {
        if (node.Children.Count == 0)
        {
            builder.Append("fn(").Append(Quote(node.Function!)).Append(')');
            return;
        }

        builder.Append("freeze(Object.assign(").Append(node.Function is null ? "{}" : "fn(" + Quote(node.Function) + ")").Append(", {");
        var first = true;
        foreach (var (name, child) in node.Children)
        {
            if (!first)
            {
                builder.Append(", ");
            }

            first = false;
            builder.Append(Quote(name)).Append(": ");
            AppendNode(builder, child);
        }

        builder.Append("}))");
    }

    private sealed class PathNode
    {
        public string? Function { get; set; }

        public List<(string Name, PathNode Node)> Children { get; } = [];

        public PathNode Child(string name)
        {
            foreach (var (existing, node) in Children)
            {
                if (existing == name)
                {
                    return node;
                }
            }

            var added = new PathNode();
            Children.Add((name, added));
            return added;
        }
    }

    private static string Quote(string value) => JsonSerializer.Serialize(value, CodeModeJsonContext.Default.String);
}
