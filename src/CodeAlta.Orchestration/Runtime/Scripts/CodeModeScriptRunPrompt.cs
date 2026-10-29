using System.Text.Json;
using System.Text.Json.Nodes;
using CodeAlta.Catalog;

namespace CodeAlta.Orchestration.Runtime.Scripts;

/// <summary>
/// The prompt that runs a script of the library in a new session: one call of <c>exec</c> that runs it with its arguments,
/// then a short answer with its result. An automation that runs a script, <b>Run…</b> in the Workflows tab and
/// <c>alta script run</c> all send it, after the same checks: the script is in the library of the project and in the space
/// of the session, parses, and accepts its arguments.
/// </summary>
public static partial class CodeModeScriptRunPrompt
{
    /// <summary>Checks a script and its arguments, and writes the prompt that runs it.</summary>
    /// <param name="library">The library of the host.</param>
    /// <param name="name">The name of the script.</param>
    /// <param name="argumentsJson">Its arguments, a JSON object; null for none.</param>
    /// <param name="trigger">
    /// What started the run, such as the issue that started an automation: its <c>event</c> argument when the script declares
    /// one and the arguments do not give it; null or blank for nothing.
    /// </param>
    /// <param name="projectPath">The folder of the project the session runs in, whose scripts come first; null for none.</param>
    /// <param name="problem">Why the script cannot run so.</param>
    /// <returns>The prompt, or null when <paramref name="problem"/> is set.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="library"/> or <paramref name="name"/> is null.</exception>
    public static string? Build(CodeModeScriptLibrary library, string name, string? argumentsJson, string? trigger, string? projectPath, out string? problem)
        => Build(library, name, argumentsJson, trigger, projectPath, null, out problem);

    /// <summary>Checks a script of a space and its arguments, and writes the prompt that runs it.</summary>
    /// <param name="library">The library of the host.</param>
    /// <param name="name">The name of the script.</param>
    /// <param name="argumentsJson">Its arguments, a JSON object; null for none.</param>
    /// <param name="trigger">What started the run: its <c>event</c> argument when the script declares one; null or blank for nothing.</param>
    /// <param name="projectPath">The folder of the project the session runs in, whose scripts come first; null for none.</param>
    /// <param name="spaces">The space of the session that runs it: yours and the samples only when the space has them; null for every script.</param>
    /// <param name="problem">Why the script cannot run so.</param>
    /// <returns>The prompt, or null when <paramref name="problem"/> is set.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="library"/> or <paramref name="name"/> is null.</exception>
    public static string? Build(CodeModeScriptLibrary library, string name, string? argumentsJson, string? trigger, string? projectPath, SpaceFilter? spaces,
        out string? problem)
    {
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(name);
        problem = null;
        if (library.Find(name, projectPath, spaces) is not { } script)
        {
            problem = spaces is not null && library.Find(name, projectPath) is not null
                ? ResourceNotInSpaceException.MessageOf(SpaceResourceKind.Script, name, spaces.SpaceId)
                : $"There is no script '{name}' in the library{(projectPath is null ? string.Empty : " of its project")}.";
            return null;
        }

        if (script.Problem is { } error)
        {
            problem = $"The script '{name}' does not parse: {FirstLine(error)}";
            return null;
        }

        JsonObject values;
        try
        {
            values = string.IsNullOrWhiteSpace(argumentsJson) ? [] : JsonNode.Parse(argumentsJson) as JsonObject ?? throw new JsonException();
        }
        catch (JsonException)
        {
            problem = $"The arguments of the script '{name}' are not a JSON object.";
            return null;
        }

        var started = trigger?.Trim() ?? string.Empty;
        if (started.StartsWith("---", StringComparison.Ordinal)) started = started[3..].Trim();
        if (started.Length > 0 && !values.ContainsKey("event") && Declares(script.ParametersSchemaJson, "event")) values["event"] = started;
        var json = values.ToJsonString();
        if (script.CheckArguments(json) is { } invalid)
        {
            problem = $"The script '{name}' refuses its arguments: {invalid}";
            return null;
        }

        return $"""
            Run the script `{name}` of the library with `exec`, as it is written below, and wait for it to end. Then answer with its result in a few lines: do nothing else.

            ```js
            // @exec: {"{"}"title": "{name}", "yield_ms": 600000{"}"}
            return await workflow("{name}", {json});
            ```
            """;

        static bool Declares(string? schema, string parameter)
        {
            if (schema is null) return false;
            using var document = JsonDocument.Parse(schema);
            return document.RootElement.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object
                && properties.TryGetProperty(parameter, out _);
        }
    }

    /// <summary>
    /// The prompt that asks the session of a run to turn it into a script of the library: the fixed values become
    /// <c>meta.params</c>, the script gets its <c>meta</c>, is checked, then saved under the name the user gave.
    /// </summary>
    /// <param name="runId">The run.</param>
    /// <param name="name">The name of the script, one <see cref="CodeModeScriptLibrary.IsName"/> accepts.</param>
    /// <returns>The prompt.</returns>
    /// <exception cref="ArgumentException"><paramref name="runId"/> is blank, or <paramref name="name"/> is not a script name.</exception>
    public static string SaveAsScript(string runId, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        if (!CodeModeScriptLibrary.IsName(name)) throw new ArgumentException($"'{name}' is not a script name.", nameof(name));
        return $"""
            Turn the script of the run `{runId}` into the script `{name}` of the library, so that it can run again with other values:

            1. Read its source: `alta workflow show {runId}` names it, or start from `alta script save {name} --from-run {runId}` as written below.
            2. Replace the values it was written for (a pull request, a path, a model, a count, a text) by parameters declared in `meta.params` and read from `args`, with a default where one is sensible.
            3. Give it `export const meta = {"{"} name: "{name}", description: "…", params: {"{"} … {"}"}, steps: [ … ] {"}"}`: a one-line description, and the steps it runs (`id`, `title`, `after`, `each`).
            4. Check it with `alta script check <path>` (or `--stdin`), and fix what it reports.
            5. Publish it with `alta script save {name} --from <path>` (or `--stdin`), or with `alta script save {name} --from-run {runId}` when it needs no change.

            Do not run it again. Answer with its parameters in a few lines.
            """;
    }

    /// <summary>
    /// Gets whether code is the call the prompt that runs a script asks for (<see cref="Build(CodeModeScriptLibrary, string, string?, string?, string?, SpaceFilter?, out string?)"/>):
    /// one <c>return await workflow("name", args)</c>, after an optional line of options.
    /// </summary>
    /// <param name="code">The code <c>exec</c> was given.</param>
    /// <returns>True for that call.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="code"/> is null.</exception>
    public static bool IsRunOfScript(string code)
    {
        ArgumentNullException.ThrowIfNull(code);
        return RunOfScript().IsMatch(code);
    }

    [System.Text.RegularExpressions.GeneratedRegex("""\A\s*(?:\x60{3}(?:js|javascript)?\s*\n)?(?://\s*@exec:[^\n]*\n)?\s*return\s+await\s+workflow\(\s*"[a-z][a-z0-9-]*"\s*(?:,[\s\S]*)?\)\s*;?\s*(?:\x60{3}\s*)?\z""")]
    private static partial System.Text.RegularExpressions.Regex RunOfScript();

    private static string FirstLine(string message)
    {
        var line = message.ReplaceLineEndings("\n").Split('\n')[0].Trim();
        return line.Length <= 300 ? line : line[..300];
    }
}
