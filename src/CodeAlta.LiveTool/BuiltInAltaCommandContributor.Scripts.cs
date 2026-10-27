using System.Text.Json;
using CodeAlta.CodeMode;
using CodeAlta.Orchestration.Runtime;
using CodeAlta.Orchestration.Runtime.Scripts;
using XenoAtom.CommandLine;

namespace CodeAlta.LiveTool;

// The `alta script` commands: the library of the scripts of Code Mode, which `workflow("name", args)` runs.
internal sealed partial class BuiltInAltaCommandContributor
{
    private static Command CreateScriptCommand(AltaCommandContext context)
    {
        var group = Group("script", "The library of the scripts of Code Mode: list them, show one, check one, edit one through a copy, save, delete, run one in a new session. A script runs from `exec` with `workflow(\"name\", args)`.");
        group.Add(CreateScriptListCommand(context));
        group.Add(CreateScriptShowCommand(context));
        group.Add(CreateScriptCheckCommand(context));
        group.Add(CreateScriptEditCommand(context));
        group.Add(CreateScriptSaveCommand(context));
        group.Add(CreateScriptDeleteCommand(context));
        group.Add(CreateScriptRunCommand(context));
        AddHelpText(
            group,
            "Scripts live in `~/.alta/scripts/<name>.js` (yours) and `.alta/scripts/<name>.js` (the project's, committed with it); a project script wins over yours of the same name, and yours over a sample of CodeAlta. A script starts with `export const meta = { name, description, params, steps }`.",
            "Your scripts and the samples are in every space, unless the user limits them to some (`[scripts.spaces] <name> = [...]` in the configuration); the scripts of a project are in the spaces of its sessions.",
            "Edit a script through a copy: `alta script edit <name>` gives a file to change with your file tools and the hash it started from; check it with `alta script check <path>`, try it with `workflow({ scriptPath })`, then publish it with `alta script save <name> --from <path> --hash <hash>`.",
            "Examples: `alta script list`; `alta script show review-pr --declarations`; `alta script save triage --scope project --stdin`; `alta script run review-pr --arg pr=128`.");
        return group;
    }

    private static Command CreateScriptRunCommand(AltaCommandContext context)
    {
        var options = new ScriptRunOptions();
        var command = AltaSubSessionsGate.Leaf(context, "run", "Run a script of the library in a new session of the project, as Run… in the Workflows tab does: the session runs it with `exec` and answers with its result. Returns once the session got the prompt, without the result: in `exec`, `workflow(\"name\", args)` runs the script there and gives its result.");
        command.Add("<name>", "Script name.", value => options.Name = value);
        command.Add("arg=", "An argument of the script as `<key>=<value>`; repeat for several. A value is JSON (`7`, `true`, `[\"a\",\"b\"]`) unless the parameter is a string.", value => { if (value is not null) options.Arguments.Add(value); });
        command.Add("project=", "Project id, slug or path. Defaults to the project of the calling session, then to the cwd.", value => options.Project = value);
        command.Add(async (_, _) => await HandleScriptRunAsync(context, options).ConfigureAwait(false));
        AddHelpText(
            command,
            "From a session, the new session is one of its sub-sessions, in its space: a script of yours or a sample runs only when that space has it. `alta session report <session-id>` follows it.",
            "Examples: `alta script run review-pr --arg pr=128`; `alta script run research --arg question=\"How are TOML keys parsed?\" --arg angles=3`.");
        return command;
    }

    private static Command CreateScriptListCommand(AltaCommandContext context)
    {
        string? scope = null;
        var command = Leaf("list", "List the scripts the project sees: the project's, yours and the samples, one per name.");
        command.Add("scope=", "Only `project`, `global` or `builtin`.", value => scope = value);
        command.Add(async (_, _) => await HandleScriptListAsync(context, scope).ConfigureAwait(false));
        return command;
    }

    private static Command CreateScriptShowCommand(AltaCommandContext context)
    {
        string? name = null;
        var declarations = false;
        var command = Leaf("show", "Show a script: its meta, where it is, its hash, and its source.");
        command.Add("<name>", "Script name.", value => name = value);
        command.Add("declarations", "Give the declaration a caller uses instead of the source: `workflow(\"name\", args: {...})`.", value => declarations = value is not null);
        command.Add(async (_, _) => await HandleScriptShowAsync(context, name, declarations).ConfigureAwait(false));
        return command;
    }

    private static Command CreateScriptCheckCommand(AltaCommandContext context)
    {
        string? target = null;
        var stdin = false;
        var command = Leaf("check", "Check a script without running it: that it parses, and that its meta is valid.");
        command.Add("<name-or-path>?", "A script name, or the path of a script file. Without it, --stdin gives the script.", value => target = value);
        command.Add("stdin", "Check the script given on stdin.", value => stdin = value is not null);
        command.Add(async (_, _) => await HandleScriptCheckAsync(context, target, stdin).ConfigureAwait(false));
        return command;
    }

    private static Command CreateScriptEditCommand(AltaCommandContext context)
    {
        string? name = null;
        var command = Leaf("edit", "Copy a script into the scratchpad of the session to change it with your file tools; gives the path and the hash it started from.");
        command.Add("<name>", "Script name.", value => name = value);
        command.Add(async (_, _) => await HandleScriptEditAsync(context, name).ConfigureAwait(false));
        return command;
    }

    private static Command CreateScriptSaveCommand(AltaCommandContext context)
    {
        var options = new ScriptSaveOptions();
        var command = Leaf("save", "Publish a script in the library: from a file (`--from`), from stdin, or from the source of a run (`--from-run`).");
        command.Add("<name>", "Script name: lowercase letters, digits and hyphens.", value => options.Name = value);
        command.Add("from=", "The file of the script, such as the copy `alta script edit` gave.", value => options.From = value);
        command.Add("from-run=", "A run of `exec` whose source becomes the script.", value => options.FromRun = value);
        command.Add("stdin", "Read the script from stdin.", value => options.Stdin = value is not null);
        command.Add("scope=", "`project` (the default in a project) or `global`.", value => options.Scope = value);
        command.Add("hash=", "The hash `alta script edit` gave: the save is refused when the library copy changed since.", value => options.Hash = value);
        command.Add(async (_, _) => await HandleScriptSaveAsync(context, options).ConfigureAwait(false));
        return command;
    }

    private static Command CreateScriptDeleteCommand(AltaCommandContext context)
    {
        string? name = null;
        string? scope = null;
        var command = Leaf("delete", "Delete a script of the project or of yours.");
        command.Add("<name>", "Script name.", value => name = value);
        command.Add("scope=", "`project` (the default in a project) or `global`.", value => scope = value);
        command.Add((_, _) => ValueTask.FromResult(HandleScriptDelete(context, name, scope)));
        return command;
    }

    private static async ValueTask<int> HandleScriptListAsync(AltaCommandContext context, string? scope)
    {
        if (context.Services.Get<CodeModeScriptLibrary>() is not { } library) return ScriptsUnavailable(context);
        CodeModeScriptScope? only = null;
        if (NormalizeOptionalText(scope) is { } named)
        {
            if (ParseScope(named) is not { } parsed) return UsageError(context, "usage.invalidScope", "--scope is `project`, `global` or `builtin`.", "alta script list");
            only = parsed;
        }

        var spaces = await CallerSpaceFilterAsync(context).ConfigureAwait(false);
        var scripts = library.List(ProjectRoot(context), spaces).Where(script => only is null || script.Scope == only).ToArray();
        foreach (var script in scripts)
        {
            AltaJsonlWriter.WriteRecord(context.Stdout, ScriptRecord(context, "alta.script", script));
        }

        WriteSummary(context, "alta.script.summary", scripts.Length, truncated: false);
        return AltaExitCodes.Success;
    }

    private static async ValueTask<int> HandleScriptShowAsync(AltaCommandContext context, string? name, bool declarations)
    {
        if (context.Services.Get<CodeModeScriptLibrary>() is not { } library) return ScriptsUnavailable(context);
        if (NormalizeOptionalText(name) is not { } scriptName) return UsageError(context, "usage.missingName", "A script name is required.", "alta script show");
        if (await FindInCallerSpaceAsync(context, library, scriptName).ConfigureAwait(false) is not { } script) return ScriptNotFound(context, scriptName);
        var record = ScriptRecord(context, "alta.script.show", script);
        if (declarations) record["declaration"] = Declaration(script);
        else record["source"] = script.Source.Original;
        AltaJsonlWriter.WriteRecord(context.Stdout, record);
        return AltaExitCodes.Success;
    }

    private static async ValueTask<int> HandleScriptCheckAsync(AltaCommandContext context, string? target, bool stdin)
    {
        const string path = "alta script check";
        string content;
        string label;
        if (stdin)
        {
            content = await context.Stdin.ReadToEndAsync(context.CancellationToken).ConfigureAwait(false);
            label = "stdin";
        }
        else if (NormalizeOptionalText(target) is not { } given)
        {
            return UsageError(context, "usage.missingScript", "Name a script, the path of a script file, or use --stdin.", path);
        }
        else if (CodeModeScriptLibrary.IsName(given) && context.Services.Get<CodeModeScriptLibrary>() is { } library
                 && await FindInCallerSpaceAsync(context, library, given).ConfigureAwait(false) is { } script)
        {
            content = script.Source.Original;
            label = given;
        }
        else
        {
            var file = ResolvePath(context, given);
            if (!File.Exists(file)) return NotFound(context, "script.notFound", $"No script '{given}': neither a script of the library nor a file.");
            content = await File.ReadAllTextAsync(file, context.CancellationToken).ConfigureAwait(false);
            label = file;
        }

        var source = CodeModeSource.Prepare(content, Path.GetFileName(label));
        var record = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["type"] = "alta.script.check",
            ["version"] = 1,
            ["correlationId"] = context.CorrelationId,
            ["script"] = label,
            ["valid"] = source.Error is null,
        };
        if (source.Error is { } error)
        {
            record["error"] = $"{error.Name}: {error.Message}";
            if (error.Location is { } location) record["line"] = location.Line;
        }
        else if (source.Descriptor is null)
        {
            record["warning"] = "The script has no `export const meta = { ... }`: a library script describes itself (name, description, params, steps).";
        }

        AltaJsonlWriter.WriteRecord(context.Stdout, record);
        return source.Error is null ? AltaExitCodes.Success : AltaExitCodes.Failure;
    }

    private static async ValueTask<int> HandleScriptEditAsync(AltaCommandContext context, string? name)
    {
        if (context.Services.Get<CodeModeScriptLibrary>() is not { } library) return ScriptsUnavailable(context);
        if (NormalizeOptionalText(name) is not { } scriptName) return UsageError(context, "usage.missingName", "A script name is required.", "alta script edit");
        var script = await FindInCallerSpaceAsync(context, library, scriptName).ConfigureAwait(false);
        // The copy is in the scratchpad of the session, which its file tools reach; a new script starts empty.
        var scratchpad = context.Services.Get<SessionRuntimeService>()?.ScratchpadRoot
            ?? (context.Services.Get<CodeAlta.Catalog.CatalogOptions>() is { } catalog ? Path.Combine(catalog.GlobalRoot, "scratchpad") : Path.GetTempPath());
        var folder = Path.Combine(scratchpad, "scripts", NormalizeOptionalText(context.Caller.SourceSessionId) ?? "user");
        Directory.CreateDirectory(folder);
        var copy = Path.Combine(folder, scriptName + ".js");
        File.WriteAllText(copy, script?.Source.Original ?? $"export const meta = {{ name: \"{scriptName}\", description: \"\", params: {{}} }};\n\nreturn null;\n");
        var record = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["type"] = "alta.script.edit",
            ["version"] = 1,
            ["correlationId"] = context.CorrelationId,
            ["name"] = scriptName,
            ["path"] = copy,
            ["hash"] = script?.Hash,
            ["scope"] = script is null ? null : ScopeName(script.Scope),
            ["nextStep"] = $"Change {copy} with your file tools, check it with `alta script check {copy}`, try it with `workflow({{ scriptPath: \"{copy.Replace("\\", "/", StringComparison.Ordinal)}\" }})`, then publish it with `alta script save {scriptName} --from {copy}{(script is null ? string.Empty : " --hash " + script.Hash)}`.",
        };
        AltaJsonlWriter.WriteRecord(context.Stdout, record);
        return AltaExitCodes.Success;
    }

    private static async ValueTask<int> HandleScriptSaveAsync(AltaCommandContext context, ScriptSaveOptions options)
    {
        const string path = "alta script save";
        if (context.Services.Get<CodeModeScriptLibrary>() is not { } library) return ScriptsUnavailable(context);
        if (await AltaSandboxGate.OfCallerAsync(context).ConfigureAwait(false) is { } sandbox)
            return PermissionDenied(context, "sandbox.scriptSave", AltaSandboxGate.Refusal(sandbox, "save a script in the library"));
        if (NormalizeOptionalText(options.Name) is not { } name) return UsageError(context, "usage.missingName", "A script name is required.", path);
        var sources = (options.From is not null ? 1 : 0) + (options.FromRun is not null ? 1 : 0) + (options.Stdin ? 1 : 0);
        if (sources != 1) return UsageError(context, "usage.source", "Give the script with exactly one of --from <path>, --from-run <run-id> or --stdin.", path);

        string content;
        if (options.Stdin)
        {
            content = await context.Stdin.ReadToEndAsync(context.CancellationToken).ConfigureAwait(false);
        }
        else if (options.From is { } from)
        {
            var file = ResolvePath(context, from);
            if (!File.Exists(file)) return NotFound(context, "script.notFound", $"File '{file}' does not exist.");
            content = await File.ReadAllTextAsync(file, context.CancellationToken).ConfigureAwait(false);
        }
        else
        {
            // The source of a run is kept in the scratchpad of its session, under its id.
            var runSources = context.Services.Get<SessionRuntimeService>()?.ScratchpadRoot is { } scratchpad ? Path.Combine(scratchpad, "codemode") : null;
            var kept = runSources is null || !Directory.Exists(runSources) ? null : Directory.EnumerateFiles(runSources, options.FromRun + ".js", SearchOption.AllDirectories).FirstOrDefault();
            if (kept is null) return NotFound(context, "run.notFound", $"No source kept for run '{options.FromRun}'.");
            content = await File.ReadAllTextAsync(kept, context.CancellationToken).ConfigureAwait(false);
        }

        var projectRoot = ProjectRoot(context);
        CodeModeScriptScope scope;
        if (NormalizeOptionalText(options.Scope) is { } named)
        {
            if (ParseScope(named) is not { } parsed || parsed == CodeModeScriptScope.BuiltIn) return UsageError(context, "usage.invalidScope", "--scope is `project` or `global`.", path);
            scope = parsed;
        }
        else
        {
            scope = projectRoot is null ? CodeModeScriptScope.Global : CodeModeScriptScope.Project;
        }

        var (saved, refusal) = library.Save(name, scope, content, projectRoot, NormalizeOptionalText(options.Hash));
        if (saved is null)
        {
            AltaJsonlWriter.WriteError(context.Stderr, context.CorrelationId, "script.saveRefused", AltaExitCodes.Failure, refusal!);
            return AltaExitCodes.Failure;
        }

        AltaJsonlWriter.WriteRecord(context.Stdout, ScriptRecord(context, "alta.script.saved", saved));
        return AltaExitCodes.Success;
    }

    private static int HandleScriptDelete(AltaCommandContext context, string? name, string? scope)
    {
        if (context.Services.Get<CodeModeScriptLibrary>() is not { } library) return ScriptsUnavailable(context);
        if (NormalizeOptionalText(name) is not { } scriptName) return UsageError(context, "usage.missingName", "A script name is required.", "alta script delete");
        var projectRoot = ProjectRoot(context);
        var where = NormalizeOptionalText(scope) is { } named ? ParseScope(named) : projectRoot is null ? CodeModeScriptScope.Global : CodeModeScriptScope.Project;
        if (where is null or CodeModeScriptScope.BuiltIn) return UsageError(context, "usage.invalidScope", "--scope is `project` or `global`.", "alta script delete");
        if (!library.Delete(scriptName, where.Value, projectRoot)) return ScriptNotFound(context, scriptName);
        AltaJsonlWriter.WriteRecord(context.Stdout, new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["type"] = "alta.script.deleted",
            ["version"] = 1,
            ["correlationId"] = context.CorrelationId,
            ["name"] = scriptName,
            ["scope"] = ScopeName(where.Value),
        });
        return AltaExitCodes.Success;
    }

    private static async ValueTask<int> HandleScriptRunAsync(AltaCommandContext context, ScriptRunOptions options)
    {
        const string path = "alta script run";
        if (context.Services.Get<CodeModeScriptLibrary>() is not { } library || context.Services.Get<CodeModeRunRegistry>() is not { } runs) return ScriptsUnavailable(context);
        if (NormalizeOptionalText(options.Name) is not { } name) return UsageError(context, "usage.missingName", "A script name is required.", path);
        // Without a calling session, only a host with a window keeps the new session running: `altacli alta` ends with the command.
        if (NormalizeOptionalText(context.Caller.SourceSessionId) is null && context.Services.Get<IAltaSpaceView>() is null)
        {
            return Unsupported(context, "script.noSessionHost",
                "A script runs in a new session, and this CodeAlta ends with the command: the session would not run. Run it from a session " +
                "(`alta script run`, or `workflow(\"name\", args)` in `exec`), from the Workflows tab of CodeAlta Desktop, or with `altacli run` and a prompt that runs it.");
        }

        var (project, exitCode) = await ResolveWorkItemProjectAsync(context, options.Project).ConfigureAwait(false);
        if (project is null) return exitCode;
        if (ScriptArguments(library.Find(name, project.ProjectPath), options.Arguments, out var argumentsJson) is { } invalid)
            return UsageError(context, "usage.invalidArg", invalid, path);

        // The script must be in the space of the session that runs it: the space of the calling session, else the one the window shows.
        var (spaceId, spaceExit) = await ResolveNewSessionSpaceAsync(context, null, path).ConfigureAwait(false);
        if (spaceExit != AltaExitCodes.Success) return spaceExit;
        var spaces = context.Services.Get<SessionRuntimeService>() is { } runtime
            ? await runtime.GetSpaceFilterAsync(spaceId, project, context.CancellationToken).ConfigureAwait(false)
            : null;
        if (CodeModeScriptRunPrompt.Build(library, name, argumentsJson, null, project.ProjectPath, spaces, out var problem) is not { } prompt)
        {
            AltaJsonlWriter.WriteError(context.Stderr, context.CorrelationId, "script.runRefused", AltaExitCodes.Failure, problem!);
            return AltaExitCodes.Failure;
        }

        var dispatcher = context.Services.Get<AltaCommandDispatcher>() ?? new AltaCommandDispatcher(new AltaCommandRegistry(), context.Services);
        var title = library.Find(name, project.ProjectPath, spaces)?.Title is { Length: > 0 } named ? named : name;
        var created = await dispatcher.InvokeAsync(["session", "create", "--project", project.Id, "--title", title], caller: context.Caller, cwd: context.Cwd,
            cancellationToken: context.CancellationToken).ConfigureAwait(false);
        var sessionId = created.ExitCode == AltaExitCodes.Success ? StringProperty(created.Stdout, "alta.session.created", "sessionId") : null;
        if (sessionId is null) return Forward(context, created);

        // The session runs the script through exec, whatever the setting of Code Mode says.
        runs.RequireCodeMode(sessionId);
        var sent = await dispatcher.InvokeAsync(["session", "send", sessionId, "--stdin"], prompt, context.Caller, context.Cwd,
            cancellationToken: context.CancellationToken).ConfigureAwait(false);
        if (sent.ExitCode != AltaExitCodes.Success) return Forward(context, sent);

        AltaJsonlWriter.WriteRecord(context.Stdout, new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["type"] = "alta.script.run",
            ["version"] = 1,
            ["correlationId"] = context.CorrelationId,
            ["name"] = name,
            ["sessionId"] = sessionId,
            ["projectId"] = project.Id,
            ["arguments"] = argumentsJson,
            ["nextStep"] = $"The session `{sessionId}` runs the script and answers with its result: `alta session report {sessionId}` follows it.",
        });
        return AltaExitCodes.Success;

        // The errors of a command it ran, as its own: the flat transcript of a dispatch holds them with the records.
        static int Forward(AltaCommandContext context, AltaCommandResult result)
        {
            if (result.Stderr.Length > 0)
            {
                context.Stderr.Write(result.Stderr);
            }
            else
            {
                foreach (var line in result.Stdout.Split('\n'))
                {
                    if (line.Contains("\"alta.error\"", StringComparison.Ordinal)) context.Stderr.WriteLine(line.TrimEnd('\r'));
                }
            }

            return result.ExitCode == AltaExitCodes.Success ? AltaExitCodes.Failure : result.ExitCode;
        }
    }

    // The arguments `--arg key=value` give, as a JSON object: a value is JSON unless the parameter is a string, or is not JSON.
    internal static string? ScriptArguments(CodeModeLibraryScript? script, IReadOnlyList<string> pairs, out string json)
    {
        var values = new System.Text.Json.Nodes.JsonObject();
        JsonElement properties = default;
        using var schema = script?.ParametersSchemaJson is { } text ? JsonDocument.Parse(text) : null;
        var hasProperties = schema is not null && schema.RootElement.TryGetProperty("properties", out properties) && properties.ValueKind == JsonValueKind.Object;
        json = "{}";
        foreach (var pair in pairs)
        {
            var separator = pair.IndexOf('=', StringComparison.Ordinal);
            if (separator <= 0) return $"--arg takes `<key>=<value>`, not `{pair}`.";
            var (key, value) = (pair[..separator].Trim(), pair[(separator + 1)..]);
            var isString = hasProperties && properties.TryGetProperty(key, out var parameter) && parameter.ValueKind == JsonValueKind.Object
                && parameter.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String && type.GetString() == "string";
            System.Text.Json.Nodes.JsonNode? node;
            try
            {
                node = isString ? System.Text.Json.Nodes.JsonValue.Create(value) : System.Text.Json.Nodes.JsonNode.Parse(value);
            }
            catch (JsonException)
            {
                node = System.Text.Json.Nodes.JsonValue.Create(value);
            }

            values[key] = node;
        }

        json = values.ToJsonString();
        return null;
    }

    // A string property of the first record of a type in a JSONL transcript.
    private static string? StringProperty(string jsonl, string type, string property)
    {
        foreach (var line in jsonl.Split('\n'))
        {
            if (!line.Contains($"\"{type}\"", StringComparison.Ordinal)) continue;
            using var document = JsonDocument.Parse(line);
            if (document.RootElement.TryGetProperty("type", out var found) && found.GetString() == type
                && document.RootElement.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String)
                return value.GetString();
        }

        return null;
    }

    // The declaration a caller of a library script reads: `workflow("name", args: {...}): Promise<unknown>`.
    internal static string Declaration(CodeModeLibraryScript script)
    {
        var meta = script.Source.Descriptor;
        var args = "{}";
        if (meta?.ParametersSchemaJson is { } schema)
        {
            using var document = JsonDocument.Parse(schema);
            var required = document.RootElement.TryGetProperty("required", out var names) ? names.EnumerateArray().Select(static name => name.GetString()).ToHashSet(StringComparer.Ordinal) : [];
            var fields = document.RootElement.TryGetProperty("properties", out var properties)
                ? properties.EnumerateObject().Select(property => $"{property.Name}{(required.Contains(property.Name) ? string.Empty : "?")}: {TypeOf(property.Value)}")
                : [];
            args = "{ " + string.Join("; ", fields) + " }";
        }

        var comment = meta?.Description is { Length: > 0 } description ? $"/** {description} */\n" : string.Empty;
        return $"{comment}declare function workflow(name: \"{script.Name}\", args: {args}): Promise<unknown>;";

        static string TypeOf(JsonElement schema) => schema.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String
            ? type.GetString() switch { "integer" => "number", "array" => "unknown[]", "object" => "Record<string, unknown>", var other => other! }
            : "unknown";
    }

    private static Dictionary<string, object?> ScriptRecord(AltaCommandContext context, string type, CodeModeLibraryScript script)
    {
        var meta = script.Source.Descriptor;
        var record = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["type"] = type,
            ["version"] = 1,
            ["correlationId"] = context.CorrelationId,
            ["name"] = script.Name,
            ["scope"] = ScopeName(script.Scope),
            ["path"] = script.Path,
            ["hash"] = script.Hash,
        };
        if (meta?.Description is { } description) record["description"] = description;
        if (meta?.WhenToUse is { } whenToUse) record["whenToUse"] = whenToUse;
        if (meta?.Steps is { Count: > 0 } steps) record["steps"] = steps.Select(static step => step.Id).ToArray();
        if (script.Source.Error is { } error) record["error"] = $"{error.Name}: {error.Message}";
        return record;
    }

    private static string? ProjectRoot(AltaCommandContext context) => CodeModeScriptLibrary.ProjectRootOf(NormalizeOptionalText(context.Cwd));

    // The script a caller sees under a name: one of yours or a sample only when the space of the calling session has it.
    private static async Task<CodeModeLibraryScript?> FindInCallerSpaceAsync(AltaCommandContext context, CodeModeScriptLibrary library, string name)
        => library.Find(name, ProjectRoot(context), await CallerSpaceFilterAsync(context).ConfigureAwait(false));

    private static CodeModeScriptScope? ParseScope(string scope) => scope.ToLowerInvariant() switch
    {
        "project" => CodeModeScriptScope.Project,
        "global" => CodeModeScriptScope.Global,
        "builtin" => CodeModeScriptScope.BuiltIn,
        _ => null,
    };

    private static string ScopeName(CodeModeScriptScope scope) => scope switch
    {
        CodeModeScriptScope.Project => "project",
        CodeModeScriptScope.Global => "global",
        _ => "builtin",
    };

    private static int ScriptNotFound(AltaCommandContext context, string name)
        => NotFound(context, "script.notFound", $"No script '{name}' in the library: `alta script list` lists them.");

    private static int ScriptsUnavailable(AltaCommandContext context)
    {
        AltaJsonlWriter.WriteError(context.Stderr, context.CorrelationId, "service.unavailable", AltaExitCodes.ServiceUnavailable, "This host has no library of scripts.");
        return AltaExitCodes.ServiceUnavailable;
    }

    private sealed class ScriptRunOptions
    {
        public string? Name { get; set; }
        public List<string> Arguments { get; } = [];
        public string? Project { get; set; }
    }

    private sealed class ScriptSaveOptions
    {
        public string? Name { get; set; }
        public string? From { get; set; }
        public string? FromRun { get; set; }
        public bool Stdin { get; set; }
        public string? Scope { get; set; }
        public string? Hash { get; set; }
    }
}
