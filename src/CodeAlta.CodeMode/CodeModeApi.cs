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
        "args", "console", "log", "text", "step", "meta", "workflow", "agent", "ask", "sleep", "session", "parallel",
        "pipeline", "phase", "budget", "store", "load", "image", "present", "exit", "globalThis", "Math", "JSON", "Promise",
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
              argsJson = globalThis.__args, attachImage = globalThis.__image, presentPart = globalThis.__present;
            for (const name of ["__host", "__output", "__stepRun", "__stepBegin", "__stepEnd", "__random", "__args", "__image", "__present"]) delete globalThis[name];
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
            const fn = (path, textArgument) => function (args) {
              // A tool whose input is one text also takes that text alone: tools.apply_patch(patch).
              if (typeof args === "string" && textArgument !== undefined) args = { [textArgument]: args };
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
            // image(path, { title }): a picture for the result, which the host reads when the run ends.
            global("image", (path, options) => {
              if (typeof path !== "string" || path.length === 0) throw new TypeError('image(path, { title }): path is the path of a picture file, such as "out/chart.png".');
              if (options !== undefined && (options === null || typeof options !== "object" || Array.isArray(options))) {
                throw new TypeError('image(path, { title }): options is an object, such as { title: "Chart" }.');
              }
              const title = options === undefined || options.title === undefined || options.title === null ? undefined : String(options.title);
              attachImage(path, title);
            });
            // present(markdown): a part of what the run shows the user, on its card at once; the result tells the model it was shown.
            global("present", (markdown) => {
              if (typeof markdown !== "string") throw new TypeError('present(markdown): markdown is a text, such as "## Findings\n- …".');
              presentPart(markdown);
            });
            // The value of a step as the host records it for a resume: its JSON, "" for undefined, null when it is not JSON.
            const recordOf = (v) => {
              if (v === undefined) return "";
              try { const s = stringify(v); return s === undefined ? null : s; } catch { return null; }
            };
            global("step", (id, body) => {
              if (typeof id !== "string" || id.length === 0) throw new TypeError("step(id, fn): id must be a non-empty string.");
              if (typeof body !== "function") throw new TypeError("step(id, fn): fn must be a function.");
              const handle = stepBegin(id);
              // A step a resume replays: the host gives the value a previous run recorded, and its function does not run.
              if (typeof handle === "string") return Promise.resolve(handle === "" ? undefined : parse(handle));
              let result;
              try { result = stepRun(handle, body); } catch (e) { stepEnd(handle, false, message(e), null); throw e; }
              return Promise.resolve(result).then((v) => { stepEnd(handle, true, null, recordOf(v)); return v; }, (e) => { stepEnd(handle, false, message(e), null); throw e; });
            });
            global("workflow", function (reference, workflowArgs) {
              const target = typeof reference === "string" ? { name: reference } : reference;
              if (target === null || typeof target !== "object" || (typeof target.name !== "string" && typeof target.scriptPath !== "string")) {
                return Promise.reject(new TypeError("workflow(name | { scriptPath }, args): name a script of the library, or the path of a script file."));
              }
              // The arguments go second; models also put them in the first object, which is taken when nothing comes second.
              if (typeof reference === "object" && target.args !== undefined && workflowArgs !== undefined) {
                return Promise.reject(new TypeError("workflow({ scriptPath }, args): give the arguments once, as the second argument."));
              }
              const given = workflowArgs !== undefined ? workflowArgs : typeof reference === "object" ? target.args : undefined;
              return host("codemode.workflow", stringify({ name: target.name, scriptPath: target.scriptPath, args: given === undefined ? {} : given })).then(unwrap);
            });
            // A warning the host gives with a reply, such as a tier that is not set up, goes to the output of the run.
            const warned = (json) => {
              const reply = parse(json);
              const warning = reply.ok ? reply.value && reply.value.warning : reply.error && reply.error.data && reply.error.data.warning;
              if (typeof warning === "string" && warning.length > 0) writeOutput(2, warning);
              return json;
            };
            // A session of its own for a part of the work: resolves with its final answer (codemode.agent, given by the host).
            global("agent", function (prompt, options) {
              if (typeof prompt !== "string" || prompt.length === 0) return Promise.reject(new TypeError("agent(prompt, options): the prompt is a non-empty string."));
              if (options !== undefined && (options === null || typeof options !== "object" || Array.isArray(options))) {
                return Promise.reject(new TypeError('agent(prompt, options): options is an object, such as { tier: "quick" }.'));
              }
              return host("codemode.agent", stringify(Object.assign({}, options, { prompt }))).then(warned).then(unwrap).then((reply) => reply.value !== undefined ? reply.value : reply.answer);
            });
            // sleep(duration): waits, in milliseconds or "30s", "5m", "2h", "1d"; a run CodeAlta ended during wakes on time.
            global("sleep", function (duration) {
              return host("codemode.sleep", stringify({ duration: duration === undefined ? null : duration })).then(unwrap).then(() => undefined);
            });
            // ask(question, { choices, default }): a question to the user, where the session asks; resolves with the answer.
            global("ask", function (question, options) {
              if (typeof question !== "string" || question.length === 0) return Promise.reject(new TypeError("ask(question, options): the question is a non-empty string."));
              if (options !== undefined && (options === null || typeof options !== "object" || Array.isArray(options))) {
                return Promise.reject(new TypeError('ask(question, options): options is an object, such as { choices: ["yes", "no"] }.'));
              }
              return host("codemode.ask", stringify(Object.assign({}, options, { question }))).then(unwrap).then((reply) => reply.answer);
            });
            // store(key, value) and load(key): JSON values kept for the later scripts of the session; undefined removes one.
            global("store", function (key, value) {
              if (typeof key !== "string" || key.length === 0) return Promise.reject(new TypeError("store(key, value): the key is a non-empty string."));
              let args;
              try {
                if (value !== undefined && stringify(value) === undefined) throw new TypeError("not JSON");
                args = stringify(value === undefined ? { key } : { key, value });
              } catch (e) {
                return Promise.reject(new TypeError(`store("${key}", value): the value is not JSON: ${message(e)}`));
              }
              return host("codemode.store", args).then(unwrap).then(() => undefined);
            });
            global("load", function (key) {
              if (typeof key !== "string" || key.length === 0) return Promise.reject(new TypeError("load(key): the key is a non-empty string."));
              return host("codemode.load", stringify({ key })).then(unwrap).then((reply) => reply.found ? reply.value : undefined);
            });
            // session(options): a session of its own the script keeps for several prompts; closed when the script ends.
            global("session", function (options) {
              if (options !== undefined && (options === null || typeof options !== "object" || Array.isArray(options))) {
                return Promise.reject(new TypeError('session(options): options is an object, such as { tier: "quick", title: "Reviewer" }.'));
              }
              return host("codemode.session.open", stringify(options === undefined ? {} : options)).then(warned).then(unwrap).then((opened) => freeze({
                id: opened.sessionId,
                worktree: opened.worktree === undefined ? null : freeze(opened.worktree),
                run: (prompt, runOptions) => {
                  if (typeof prompt !== "string" || prompt.length === 0) return Promise.reject(new TypeError("session.run(prompt, options): the prompt is a non-empty string."));
                  return host("codemode.session.ask", stringify(Object.assign({}, runOptions, { sessionId: opened.sessionId, prompt }))).then(unwrap)
                    .then((reply) => reply.value !== undefined ? reply.value : reply.answer);
                },
                close: () => host("codemode.session.close", stringify({ sessionId: opened.sessionId })).then(unwrap),
              }));
            });
            // parallel(thunks, { limit }): a barrier over functions that each return a promise. A failure is null in the
            // array, and a warning in the output; at most `limit` (8 by default) run at once. Null, not the text of the
            // error: the answer of a sub-agent is a text too, and an error must not read as one. The model reads the
            // error in the output; a script that needs it uses Promise.allSettled.
            global("parallel", async function (thunks, options) {
              if (!Array.isArray(thunks) || thunks.some((t) => typeof t !== "function")) {
                throw new TypeError("parallel(thunks, { limit }): thunks is an array of functions, such as [() => agent(\"a\"), () => agent(\"b\")].");
              }
              const limit = Math.max(1, Math.min(64, (options && options.limit) | 0 || 8));
              const results = new Array(thunks.length).fill(null);
              let next = 0;
              const worker = async () => {
                while (next < thunks.length) {
                  const index = next++;
                  try { results[index] = await thunks[index](); }
                  catch (e) { writeOutput(2, `parallel: item ${index} failed: ${message(e)}`); }
                }
              };
              await Promise.all(Array.from({ length: Math.min(limit, thunks.length) }, worker));
              return results;
            });
            // pipeline(items, ...stages): each item goes through the stages on its own, without waiting for the other
            // items at each stage. An item whose stage fails is null, and a warning in the output.
            global("pipeline", async function (items, ...stages) {
              if (!Array.isArray(items)) throw new TypeError("pipeline(items, ...stages): items is an array.");
              if (stages.length === 0 || stages.some((s) => typeof s !== "function")) throw new TypeError("pipeline(items, ...stages): each stage is a function (item, index) => value or promise.");
              return Promise.all(items.map(async (item, index) => {
                let value = item;
                for (let stage = 0; stage < stages.length; stage++) {
                  try { value = await stages[stage](value, index); }
                  catch (e) { writeOutput(2, `pipeline: item ${index} failed at stage ${stage + 1}: ${message(e)}`); return null; }
                }
                return value;
              }));
            });
            // Models write Node.js first (`require("fs")`): the call fails with what a script uses instead.
            global("require", function (name) {
              throw new TypeError(`require(${stringify(String(name))}) is not available: scripts are not Node.js. Read and write files with tools.read_file and tools.write_file, list them with tools.list_dir, run commands with tools.shell_command.`);
            });
            // phase(title): a marker in the output, between the parts of the work.
            global("phase", (title) => writeOutput(1, `## ${String(title)}`));
            global("args", freeze(argsJson === undefined ? {} : parse(argsJson)));
            // A name a namespace does not have is a function that fails when called and says what the namespace has, so a
            // guess fails with the fix instead of "is not a function".
            const missing = (path, known) => new Proxy(function () {}, {
              apply: () => Promise.reject(new TypeError(`${path} is not a function of this script. ${known}`)),
              get: (t, p) => typeof p === "string" && p !== "then" ? missing(`${path}.${p}`, known) : undefined,
            });
            const guard = (path, target) => new Proxy(target, {
              get: (t, p, r) => typeof p !== "string" || p in t || p === "then" || p === "toJSON" ? Reflect.get(t, p, r)
                : missing(`${path}.${p}`, `${path} has: ${Object.keys(t).join(", ") || "nothing"}. codemode.search({ query }) finds a function; an MCP server activated during this script is in mcp from the next exec.`),
            });
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

            node.Function = function;
        }

        foreach (var (name, node) in root.Children)
        {
            builder.Append("global(").Append(Quote(name)).Append(", ");
            AppendNode(builder, node, name);
            builder.Append(");\n");
        }

        // The namespaces of tools exist even without a function, so that a guess says what to do instead of failing on a name.
        foreach (var name in (ReadOnlySpan<string>)["tools", "mcp"])
        {
            if (!root.Children.Exists(child => child.Name == name))
            {
                builder.Append("global(").Append(Quote(name)).Append(", guard(").Append(Quote(name)).Append(", freeze({})));\n");
            }
        }

        builder.Append("})();\n");
        return builder.ToString();
    }

    // A path that is both a function and a namespace (`alta.search` and `alta.search.show`) is a function that carries
    // its members. A namespace answers a name it does not have with a function that says what it has.
    private static void AppendNode(StringBuilder builder, PathNode node, string path)
    {
        if (node.Children.Count == 0)
        {
            builder.Append(FunctionValue(node.Function!));
            return;
        }

        builder.Append("guard(").Append(Quote(path)).Append(", freeze(Object.assign(").Append(node.Function is null ? "{}" : FunctionValue(node.Function)).Append(", {");
        var first = true;
        foreach (var (name, child) in node.Children)
        {
            if (!first)
            {
                builder.Append(", ");
            }

            first = false;
            builder.Append(Quote(name)).Append(": ");
            AppendNode(builder, child, path + "." + name);
        }

        builder.Append("})))");
    }

    private sealed class PathNode
    {
        public CodeModeFunction? Function { get; set; }

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

    private static string FunctionValue(CodeModeFunction function)
        => function.TextArgument is { } textArgument
            ? "fn(" + Quote(function.Path) + ", " + Quote(textArgument) + ")"
            : "fn(" + Quote(function.Path) + ")";

    private static string Quote(string value) => JsonSerializer.Serialize(value, CodeModeJsonContext.Default.String);
}
