using System.Buffers;
using System.Collections.Frozen;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeAlta.CodeMode;

namespace CodeAlta.LiveTool.Scripts;

/// <summary>A command line built from the call of a script.</summary>
/// <param name="CommandPath">The path of the command, such as <c>session create</c>.</param>
/// <param name="Arguments">The arguments, without the executable name.</param>
/// <param name="Stdin">What <c>--stdin</c> reads, or null.</param>
public sealed record AltaScriptCommandLine(string CommandPath, IReadOnlyList<string> Arguments, string? Stdin)
{
    /// <summary>Gets whether the script gave the arguments itself (<c>alta.run</c>): it receives all the command wrote, whatever its exit code.</summary>
    public bool Raw { get; init; }
}

/// <summary>
/// The <c>alta</c> commands as functions of the scripts of Code Mode. Each function takes one object: positional
/// arguments and options by their name in camelCase, and <c>stdin</c> for what <c>--stdin</c> reads. The model never
/// quotes a command line: <c>alta.session.create({ project: "CodeAlta", tier: "quick" })</c> becomes
/// <c>session create --project=CodeAlta --tier=quick</c>. The script receives the records the command writes: the
/// record when it writes one, else the list; a failure throws an <c>AltaError</c> with the code of the command.
/// <c>alta.run({ args, stdin })</c> runs any command line of the tree as the <c>alta</c> tool does, for what the
/// functions do not say: the help of a command, or a syntax of its own.
/// </summary>
public sealed class AltaScriptBinding
{
    /// <summary>The namespace of the functions in scripts.</summary>
    public const string Namespace = "alta";

    /// <summary>The path of the function that runs a command line as it is given.</summary>
    public const string RunPath = Namespace + ".run";

    // What a script does not get as functions: older names of other commands. `alta.run` still runs them.
    private static readonly FrozenSet<string> ExcludedPaths = new[]
    {
        "note", "skills", "skills_activate", "issues",
    }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly CodeModeFunction RunFunction = new(RunPath)
    {
        Description = "Runs a command line of `alta` as the `alta` tool does, and resolves with all it wrote, also when it fails: for the help of a command (`{ args: [\"session\", \"create\", \"--help\"] }`) and for a syntax the functions do not have. Prefer the function of a command, which checks its arguments and gives its records.",
        InputSchemaJson = """{"type":"object","properties":{"args":{"type":"array","items":{"type":"string"},"description":"The arguments, without `alta`: one string each, never quoted."},"stdin":{"type":"string","description":"What a command reads with --stdin."}},"required":["args"],"additionalProperties":false}""",
        ResultType = "{ exitCode: number; output: string; records: AltaRecord[] }",
        Parallel = true,
        Annotations = ["mutating"],
    };

    private readonly FrozenDictionary<string, Entry> _byFunction;

    /// <summary>Creates the binding of the commands a caller gets, without <c>alta.run</c>.</summary>
    /// <param name="commands">The commands, from <see cref="AltaCommandRegistry.Describe"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="commands"/> is null.</exception>
    public AltaScriptBinding(IReadOnlyList<AltaCommandDescriptor> commands)
        : this(commands, runsCommandLines: false)
    {
    }

    /// <summary>Creates the binding of the commands a caller gets.</summary>
    /// <param name="commands">The commands, from <see cref="AltaCommandRegistry.Describe"/>.</param>
    /// <param name="runsCommandLines">
    /// Whether scripts get <c>alta.run</c>, which runs any command of the tree of the caller. False for a caller whose
    /// scripts get fewer commands than its tree has, which <c>alta.run</c> would give back.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="commands"/> is null.</exception>
    public AltaScriptBinding(IReadOnlyList<AltaCommandDescriptor> commands, bool runsCommandLines)
    {
        ArgumentNullException.ThrowIfNull(commands);
        var entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
        var functions = new List<CodeModeFunction>();
        foreach (var command in commands)
        {
            if (IsExcluded(command.Path))
            {
                continue;
            }

            var path = Namespace + "." + string.Join('.', command.Path.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(CodeModeNames.ToCamelCase));
            if (entries.ContainsKey(path))
            {
                continue;
            }

            AltaScriptAnnotations.Commands.TryGetValue(command.Path, out var annotation);
            var entry = new Entry(command, annotation);
            entries[path] = entry;
            functions.Add(new CodeModeFunction(path)
            {
                Description = command.Description,
                InputSchemaJson = entry.InputSchemaJson,
                // One record, several, none, or the text of a command that writes no record.
                ResultType = annotation?.Result ?? "AltaRecord | AltaRecord[] | string | null",
                Parallel = true,
                Annotations = PolicyTags(command.Policy),
            });
        }

        // A command named `run` at the root keeps its function: its name is the tree's.
        _runsCommandLines = runsCommandLines && commands.Count > 0 && !entries.ContainsKey(RunPath);
        if (_runsCommandLines)
        {
            functions.Add(RunFunction);
        }

        _byFunction = entries.ToFrozenDictionary(StringComparer.Ordinal);
        Functions = functions;
    }

    private readonly bool _runsCommandLines;

    /// <summary>Gets the functions, one per command.</summary>
    public IReadOnlyList<CodeModeFunction> Functions { get; }

    /// <summary>Gets the TypeScript declarations of the records the functions answer.</summary>
    public static string Types => AltaScriptAnnotations.Types;

    /// <summary>Gets what the tree does not say about the commands scripts use most, by command path.</summary>
    public static IReadOnlyDictionary<string, AltaCommandAnnotation> Annotations => AltaScriptAnnotations.Commands;

    /// <summary>Gets whether a path of a script function is a command of this binding.</summary>
    /// <param name="functionPath">The path, such as <c>alta.session.create</c>.</param>
    /// <returns>True when it is one.</returns>
    public bool Contains(string functionPath) => _byFunction.ContainsKey(functionPath) || (_runsCommandLines && functionPath == RunPath);

    /// <summary>
    /// Gets whether a function is a command that only reads (a policy that does not mutate): it may run again with the same
    /// effect, so a resumed script reruns it without asking. A command without a policy is not.
    /// </summary>
    /// <param name="functionPath">The path, such as <c>alta.session.list</c>.</param>
    /// <returns>True when it only reads.</returns>
    public bool IsReadOnly(string functionPath)
        => _byFunction.TryGetValue(functionPath, out var entry) && entry.Command.Policy is { IsMutating: false };

    /// <summary>Builds the command line of a call of a script.</summary>
    /// <param name="functionPath">The path of the function.</param>
    /// <param name="argumentsJson">The argument object, as JSON.</param>
    /// <param name="commandLine">The command line.</param>
    /// <param name="error">Why the call cannot run, as the script receives it.</param>
    /// <returns>True when the command line was built.</returns>
    public bool TryCreateCommandLine(string functionPath, string argumentsJson, out AltaScriptCommandLine? commandLine, out CodeModeCallError? error)
    {
        commandLine = null;
        var runs = _runsCommandLines && functionPath == RunPath;
        if (!_byFunction.TryGetValue(functionPath, out var entry) && !runs)
        {
            error = new CodeModeCallError("TypeError", $"`{functionPath}` is not a command of this session.");
            return false;
        }

        JsonElement arguments;
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
            arguments = document.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            error = new CodeModeCallError("TypeError", $"{functionPath}: the arguments are not JSON: {ex.Message}");
            return false;
        }

        if (arguments.ValueKind != JsonValueKind.Object)
        {
            error = new CodeModeCallError("TypeError", $"{functionPath}(args): args must be an object.");
            return false;
        }

        return runs ? TryBuildRun(arguments, out commandLine, out error) : entry!.TryBuild(functionPath, arguments, out commandLine, out error);
    }

    private static bool TryBuildRun(JsonElement arguments, out AltaScriptCommandLine? commandLine, out CodeModeCallError? error)
    {
        commandLine = null;
        string? stdin = null;
        List<string>? args = null;
        foreach (var property in arguments.EnumerateObject())
        {
            switch (property.Name)
            {
                case "args" when property.Value.ValueKind == JsonValueKind.Array && property.Value.EnumerateArray().All(static item => item.ValueKind == JsonValueKind.String):
                    args = [.. property.Value.EnumerateArray().Select(static item => item.GetString()!)];
                    break;
                case "stdin" when property.Value.ValueKind is JsonValueKind.String or JsonValueKind.Null:
                    stdin = property.Value.GetString();
                    break;
                default:
                    error = new CodeModeCallError("TypeError", $"{RunPath}({{ args, stdin }}): `args` is an array of strings, such as [\"session\", \"list\", \"--limit=5\"], and `stdin` a string.");
                    return false;
            }
        }

        if (args is not { Count: > 0 })
        {
            error = new CodeModeCallError("TypeError", $"{RunPath}({{ args, stdin }}): `args` names a command, such as [\"session\", \"list\"].");
            return false;
        }

        commandLine = new AltaScriptCommandLine(string.Join(' ', args.TakeWhile(static arg => !arg.StartsWith('-'))), args, stdin) { Raw = true };
        error = null;
        return true;
    }

    /// <summary>Reads what a command line of <c>alta.run</c> wrote: its exit code, its output as text, and the records in it.</summary>
    /// <param name="result">The result of the command.</param>
    /// <returns>The value of the call; a command that failed is a value too, with its exit code.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="result"/> is null.</exception>
    public static CodeModeCallResult ReadRun(AltaCommandResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var records = new JsonArray();
        foreach (var record in Records(result))
        {
            records.Add(record);
        }

        var output = result.Stdout.TrimEnd();
        if (result.Stderr.Trim() is { Length: > 0 } errors)
        {
            output = output.Length == 0 ? errors : output + "\n" + errors;
        }

        return CodeModeCallResult.Success(new JsonObject { ["exitCode"] = result.ExitCode, ["output"] = output, ["records"] = records }.ToJsonString());
    }

    // Whether a line is a record of `alta`, its header included: an object that says its type.
    private static bool IsRecord(string line)
    {
        try
        {
            return JsonNode.Parse(line) is JsonObject record && record["type"] is JsonValue type && type.TryGetValue<string>(out _);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    // The records of an output, without its header nor their envelope fields.
    private static IEnumerable<JsonObject> Records(AltaCommandResult result)
    {
        foreach (var line in Lines(result.Stdout).Concat(Lines(result.Stderr)))
        {
            JsonNode? node;
            try
            {
                node = JsonNode.Parse(line);
            }
            catch (JsonException)
            {
                continue;
            }

            if (node is JsonObject record && record["type"] is JsonValue type && type.TryGetValue<string>(out var name) && name != "alta.result")
            {
                record.Remove("version");
                record.Remove("correlationId");
                yield return record;
            }
        }
    }

    /// <summary>
    /// Reads what a command wrote: the value the script receives, without the envelope fields of the records
    /// (<c>version</c>, <c>correlationId</c>), or the error it throws.
    /// </summary>
    /// <param name="result">The result of the command, as <see cref="AltaCommandDispatcher.InvokeRawAsync"/> returns it.</param>
    /// <param name="commandPath">The path of the command.</param>
    /// <returns>The outcome of the call.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="result"/> is null.</exception>
    public static CodeModeCallResult ReadResult(AltaCommandResult result, string commandPath)
    {
        ArgumentNullException.ThrowIfNull(result);
        var records = new List<JsonNode>();
        JsonObject? error = null;
        foreach (var record in Records(result))
        {
            if (record["type"]!.GetValue<string>() == "alta.error")
            {
                error ??= record;
                continue;
            }

            records.Add(record);
        }

        if (result.ExitCode != AltaExitCodes.Success)
        {
            var message = error?["message"]?.GetValue<string>() ?? result.Error ?? $"`alta {commandPath}` failed with exit code {result.ExitCode}.";
            var data = new JsonObject { ["exitCode"] = result.ExitCode, ["command"] = commandPath };
            if (error?["usageHint"] is { } hint)
            {
                data["usageHint"] = hint.DeepClone();
            }

            if (error?["suggestions"] is { } suggestions)
            {
                data["suggestions"] = suggestions.DeepClone();
            }

            if (records.Count > 0)
            {
                data["records"] = new JsonArray([.. records]);
            }

            return CodeModeCallResult.Failure(new CodeModeCallError("AltaError", message)
            {
                Code = error?["code"]?.GetValue<string>() ?? "alta.failed",
                DataJson = data.ToJsonString(),
            });
        }

        // A command that wrote something else than records (the command of a plugin, a help) gives its text: what it
        // wrote is never lost as an empty result.
        if (records.Count == 0 && error is null && result.Stdout.Trim() is { Length: > 0 } text && !Lines(text).Any(IsRecord))
        {
            return CodeModeCallResult.Text(text);
        }

        return records.Count switch
        {
            0 => CodeModeCallResult.Success(null),
            1 => CodeModeCallResult.Success(records[0].ToJsonString()),
            _ => CodeModeCallResult.Success(new JsonArray([.. records]).ToJsonString()),
        };
    }

    private static IEnumerable<string> Lines(string text)
    {
        using var reader = new StringReader(text);
        while (reader.ReadLine() is { } line)
        {
            if (line.Length > 0 && line[0] == '{')
            {
                yield return line;
            }
        }
    }

    private static bool IsExcluded(string path)
    {
        foreach (var excluded in ExcludedPaths)
        {
            if (path == excluded || path.StartsWith(excluded + " ", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static IReadOnlyList<string> PolicyTags(AltaCommandPolicy? policy)
    {
        if (policy is null)
        {
            return [];
        }

        var tags = new List<string>(2);
        if (policy.IsMutating)
        {
            tags.Add("mutating");
        }

        if (policy.IsDisruptive)
        {
            tags.Add("disruptive");
        }

        return tags;
    }

    private enum ParameterKind
    {
        Argument,
        Option,
        // An option whose value can be left out: true gives it alone, a value gives it with the value.
        OptionalValue,
        Flag,
        Stdin,
    }

    private sealed record Parameter(string JsName, string CliName, ParameterKind Kind, AltaValue Type, bool Required, bool IsList, string? Description);

    private sealed class Entry
    {
        private readonly AltaCommandDescriptor _command;
        private readonly AltaCommandAnnotation? _annotation;
        private readonly List<Parameter> _parameters = [];
        private readonly Dictionary<string, Parameter> _byName = new(StringComparer.Ordinal);

        public AltaCommandDescriptor Command => _command;

        public Entry(AltaCommandDescriptor command, AltaCommandAnnotation? annotation)
        {
            _command = command;
            _annotation = annotation;
            var required = annotation?.Required.ToHashSet(StringComparer.Ordinal) ?? [];
            foreach (var argument in command.Arguments)
            {
                Add(new Parameter(CodeModeNames.ToCamelCase(argument.Name), argument.Name, ParameterKind.Argument, TypeOf(argument.Name, argument.IsList ? AltaValue.StringList : AltaValue.String),
                    !argument.Optional, argument.IsList, argument.Description), []);
            }

            foreach (var option in command.Options)
            {
                var kind = option.Name == "stdin" ? ParameterKind.Stdin : !option.TakesValue ? ParameterKind.Flag : option.ValueOptional ? ParameterKind.OptionalValue : ParameterKind.Option;
                var description = option.Description;
                if (annotation?.Defaults.TryGetValue(option.Name, out var fallback) == true)
                {
                    description = $"{description} Defaults to {fallback}.".Trim();
                }

                if (kind == ParameterKind.Stdin && annotation?.StdinDescription is { } stdinDescription)
                {
                    description = stdinDescription;
                }

                Add(new Parameter(kind == ParameterKind.Stdin ? "stdin" : CodeModeNames.ToCamelCase(option.Name), option.Name, kind, TypeOf(option.Name, AltaValue.String),
                    required.Contains(option.Name), false, description), option.Aliases);
            }

            InputSchemaJson = BuildSchema();
        }

        public string InputSchemaJson { get; }

        public bool TryBuild(string functionPath, JsonElement arguments, out AltaScriptCommandLine? commandLine, out CodeModeCallError? error)
        {
            commandLine = null;
            var argv = new List<string>(_command.Path.Split(' ', StringSplitOptions.RemoveEmptyEntries));
            var positional = new SortedDictionary<int, List<string>>();
            var options = new List<string>();
            string? stdin = null;
            var given = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in arguments.EnumerateObject())
            {
                if (!_byName.TryGetValue(property.Name, out var parameter))
                {
                    var suggestion = Suggest(property.Name);
                    error = new CodeModeCallError("TypeError",
                        $"{functionPath}: unknown property `{property.Name}`.{(suggestion is null ? string.Empty : $" Did you mean `{suggestion}`?")} It takes: {string.Join(", ", _parameters.Select(static p => p.JsName))}.");
                    return false;
                }

                if (property.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                {
                    continue;
                }

                given.Add(parameter.CliName);
                switch (parameter.Kind)
                {
                    case ParameterKind.Stdin:
                        stdin = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : property.Value.GetRawText();
                        options.Add("--stdin");
                        break;
                    case ParameterKind.Flag:
                        if (property.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                        {
                            error = new CodeModeCallError("TypeError", $"{functionPath}: `{parameter.JsName}` is a flag: true or false.");
                            return false;
                        }

                        if (property.Value.ValueKind == JsonValueKind.True)
                        {
                            options.Add("--" + parameter.CliName);
                        }

                        break;
                    case ParameterKind.OptionalValue:
                        options.Add(property.Value.ValueKind == JsonValueKind.True ? "--" + parameter.CliName : $"--{parameter.CliName}={Text(property.Value)}");
                        break;
                    case ParameterKind.Argument:
                        if (property.Value.ValueKind == JsonValueKind.Array && !parameter.IsList)
                        {
                            error = new CodeModeCallError("TypeError", $"{functionPath}: `{parameter.JsName}` takes one value, not an array.");
                            return false;
                        }

                        var index = _parameters.IndexOf(parameter);
                        positional[index] = property.Value.ValueKind == JsonValueKind.Array
                            ? [.. property.Value.EnumerateArray().Select(Text)]
                            : [Text(property.Value)];
                        break;
                    default:
                        if (property.Value.ValueKind == JsonValueKind.Array && parameter.Type.Kind != AltaValueKind.Json)
                        {
                            var values = property.Value.EnumerateArray().Select(Text).ToArray();
                            if (parameter.Type.Kind == AltaValueKind.CommaList)
                            {
                                options.Add($"--{parameter.CliName}={string.Join(',', values)}");
                            }
                            else
                            {
                                options.AddRange(values.Select(value => $"--{parameter.CliName}={value}"));
                            }
                        }
                        else
                        {
                            options.Add($"--{parameter.CliName}={(parameter.Type.Kind == AltaValueKind.Json ? property.Value.GetRawText() : Text(property.Value))}");
                        }

                        break;
                }
            }

            var missing = _parameters.Where(parameter => parameter.Required && !given.Contains(parameter.CliName)).Select(static parameter => parameter.JsName).ToArray();
            if (missing.Length > 0)
            {
                error = new CodeModeCallError("TypeError", $"{functionPath}: missing {string.Join(", ", missing.Select(static name => $"`{name}`"))}.");
                return false;
            }

            // The options first, then `--`: a positional value that starts with `-` (a title, a query, a file name) is
            // a value, never an option of the command.
            argv.AddRange(options);
            if (positional.Count > 0)
            {
                argv.Add("--");
                foreach (var values in positional.Values)
                {
                    argv.AddRange(values);
                }
            }

            commandLine = new AltaScriptCommandLine(_command.Path, argv, stdin);
            error = null;
            return true;
        }

        private void Add(Parameter parameter, IReadOnlyList<string> aliases)
        {
            if (!_byName.TryAdd(parameter.JsName, parameter))
            {
                return;
            }

            _parameters.Add(parameter);
            _byName.TryAdd(parameter.CliName, parameter);
            foreach (var alias in aliases)
            {
                _byName.TryAdd(alias, parameter);
                _byName.TryAdd(CodeModeNames.ToCamelCase(alias), parameter);
            }
        }

        private AltaValue TypeOf(string cliName, AltaValue fallback)
            => _annotation?.Values.TryGetValue(cliName, out var value) == true ? value : fallback;

        private string? Suggest(string name) => CodeModeNames.Closest(name, _parameters.Select(static parameter => parameter.JsName));

        private string BuildSchema()
        {
            var buffer = new ArrayBufferWriter<byte>();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                writer.WriteString("type", "object");
                writer.WritePropertyName("properties");
                writer.WriteStartObject();
                foreach (var parameter in _parameters)
                {
                    writer.WritePropertyName(parameter.JsName);
                    writer.WriteStartObject();
                    switch (parameter.Kind)
                    {
                        case ParameterKind.Flag:
                            writer.WriteString("type", "boolean");
                            break;
                        case ParameterKind.OptionalValue:
                            writer.WritePropertyName("type");
                            writer.WriteStartArray();
                            writer.WriteStringValue("boolean");
                            writer.WriteStringValue("string");
                            writer.WriteEndArray();
                            break;
                        case ParameterKind.Stdin:
                            if (_annotation?.Stdin != AltaStdinKind.Json)
                            {
                                writer.WriteString("type", "string");
                            }

                            break;
                        default:
                            WriteType(writer, parameter.Type);
                            break;
                    }

                    if (!string.IsNullOrWhiteSpace(parameter.Description))
                    {
                        writer.WriteString("description", FirstSentence(parameter.Description));
                    }

                    writer.WriteEndObject();
                }

                writer.WriteEndObject();
                var required = _parameters.Where(static parameter => parameter.Required).Select(static parameter => parameter.JsName).ToArray();
                if (required.Length > 0)
                {
                    writer.WritePropertyName("required");
                    writer.WriteStartArray();
                    foreach (var name in required)
                    {
                        writer.WriteStringValue(name);
                    }

                    writer.WriteEndArray();
                }

                writer.WriteEndObject();
            }

            return Encoding.UTF8.GetString(buffer.WrittenSpan);
        }

        private static void WriteType(Utf8JsonWriter writer, AltaValue type)
        {
            switch (type.Kind)
            {
                case AltaValueKind.Integer:
                    writer.WriteString("type", "integer");
                    break;
                case AltaValueKind.Number:
                    writer.WriteString("type", "number");
                    break;
                case AltaValueKind.Enum:
                    writer.WritePropertyName("enum");
                    writer.WriteStartArray();
                    foreach (var value in type.Values)
                    {
                        writer.WriteStringValue(value);
                    }

                    writer.WriteEndArray();
                    break;
                case AltaValueKind.Json:
                    break;
                case AltaValueKind.StringList or AltaValueKind.CommaList:
                    writer.WriteString("type", "array");
                    writer.WritePropertyName("items");
                    writer.WriteStartObject();
                    if (type.Values.Count > 0)
                    {
                        writer.WritePropertyName("enum");
                        writer.WriteStartArray();
                        foreach (var value in type.Values)
                        {
                            writer.WriteStringValue(value);
                        }

                        writer.WriteEndArray();
                    }
                    else
                    {
                        writer.WriteString("type", "string");
                    }

                    writer.WriteEndObject();
                    break;
                default:
                    writer.WriteString("type", "string");
                    break;
            }
        }

        private static string FirstSentence(string text)
        {
            var trimmed = text.Replace('\n', ' ').Trim();
            var end = trimmed.IndexOf(". ", StringComparison.Ordinal);
            return end > 0 && end < 160 ? trimmed[..(end + 1)] : trimmed.Length > 200 ? trimmed[..200] + "…" : trimmed;
        }

        private static string Text(JsonElement value) => value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Number => value.GetRawText(),
            _ => value.GetRawText(),
        };
    }
}
