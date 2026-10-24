using System.Text;
using System.Text.Json;
using CodeAlta.Acp;
using CodeAlta.Agent;
using CodeAlta.Agent.Harness;

namespace CodeAlta.Plugin.Acp;

/// <summary>Maps the tool calls of an agent to the tool calls a CodeAlta session records.</summary>
internal static class AcpToolCalls
{
    private static readonly string[] ServerSeparators = ["__", "/", ":", ".", "-", "_"];

    /// <summary>
    /// Maps a <c>tool_call</c> to the name, the arguments and the kind of the tool call the session records: a tool of
    /// CodeAlta keeps its name and arguments; a tool of the agent is named by its kind, with names the window shows as
    /// a command, a file read or an edit, and arguments that hold its title and its input.
    /// </summary>
    public static (string Name, JsonElement Arguments, bool CodeAlta) Map(
        JsonElement update,
        IReadOnlyDictionary<string, AgentToolDefinition> codeAltaTools,
        string workingDirectory)
    {
        var title = AcpJson.GetString(update, "title");
        var kind = AcpJson.GetString(update, "kind");
        update.TryGetProperty("rawInput", out var rawInput);
        if (FindCodeAltaTool(title, rawInput, codeAltaTools) is { } tool)
        {
            return (tool.Name, tool.Arguments, true);
        }

        var path = FirstPath(update, rawInput);
        var name = kind switch
        {
            "execute" => "shell",
            "read" => "read",
            "edit" or "delete" or "move" when path is not null => "Edit",
            "edit" or "delete" or "move" => "edit",
            "search" => "search",
            "fetch" => "fetch",
            "think" => "think",
            "switch_mode" => "switch_mode",
            _ => HarnessToolCallBridge.SanitizeName(title is { Length: > 0 and <= 40 } ? title : "tool"),
        };
        var arguments = AcpJson.Build(writer =>
        {
            writer.WriteStartObject();
            if (title is not null)
            {
                writer.WriteString("title", title);
            }

            if (kind == "execute" && FindCommand(rawInput) is { } command)
            {
                writer.WriteString("command", command);
            }

            if (path is not null)
            {
                // The name the file tools of CodeAlta read the path of a change from: the session follows the file.
                writer.WriteString("file_path", Path.IsPathFullyQualified(path) ? path : Path.GetFullPath(Path.Combine(workingDirectory, path)));
            }

            if (rawInput.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in rawInput.EnumerateObject())
                {
                    if (property.Name is "title" or "command" or "file_path")
                    {
                        continue;
                    }

                    property.WriteTo(writer);
                }
            }
            else if (rawInput.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null))
            {
                writer.WritePropertyName("input");
                rawInput.WriteTo(writer);
            }

            writer.WriteEndObject();
        });
        return (name, arguments, false);
    }

    /// <summary>Creates the result of a call from its final update: its content, else its raw output.</summary>
    public static AgentToolResult CreateResult(JsonElement update, bool success)
    {
        var text = update.TryGetProperty("content", out var content) ? AcpPrompts.RenderToolContent(content) : string.Empty;
        if (string.IsNullOrWhiteSpace(text) && update.TryGetProperty("rawOutput", out var rawOutput) && rawOutput.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
        {
            text = rawOutput.ValueKind == JsonValueKind.String ? rawOutput.GetString() ?? string.Empty : rawOutput.GetRawText();
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            text = success ? "(no output)" : "The tool call failed.";
        }

        return new AgentToolResult(success, [new AgentToolResultItem.Text(text)], success ? null : text);
    }

    /// <summary>Renders a <c>plan</c> update as a checklist.</summary>
    public static string RenderPlan(JsonElement update)
    {
        var builder = new StringBuilder();
        if (AcpJson.TryGetArray(update, "entries", out var entries))
        {
            foreach (var entry in entries.EnumerateArray())
            {
                var status = AcpJson.GetString(entry, "status");
                builder.Append(status switch { "completed" => "- [x] ", "in_progress" => "- [~] ", _ => "- [ ] " })
                    .AppendLine(AcpJson.GetString(entry, "content") ?? string.Empty);
            }
        }

        return builder.Length == 0 ? "Plan updated." : builder.ToString().TrimEnd();
    }

    /// <summary>Returns the command of the input of a call that runs one, or <see langword="null"/>.</summary>
    public static string? FindCommand(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var name in (string[])["command", "cmd", "commandLine", "script"])
        {
            if (input.TryGetProperty(name, out var value))
            {
                if (value.ValueKind == JsonValueKind.String)
                {
                    return value.GetString();
                }

                if (value.ValueKind == JsonValueKind.Array)
                {
                    return string.Join(' ', value.EnumerateArray().Where(static item => item.ValueKind == JsonValueKind.String).Select(static item => item.GetString()));
                }
            }
        }

        return null;
    }

    /// <summary>Returns the first file a call names: its locations, else a path of its input, or <see langword="null"/>.</summary>
    public static string? FirstPath(JsonElement toolCall, JsonElement input)
    {
        if (AcpJson.TryGetArray(toolCall, "locations", out var locations))
        {
            foreach (var location in locations.EnumerateArray())
            {
                if (AcpJson.GetString(location, "path") is { Length: > 0 } path)
                {
                    return path;
                }
            }
        }

        foreach (var name in (string[])["path", "file_path", "filePath", "file"])
        {
            if (AcpJson.GetString(input, name) is { Length: > 0 } path)
            {
                return path;
            }
        }

        return null;
    }

    // A call of a tool of the MCP server of CodeAlta: its title names the tool, alone or after the name of the server
    // ("alta", "codealta/alta", "mcp__codealta__alta"), and some agents wrap the arguments with the names.
    private static (string Name, JsonElement Arguments)? FindCodeAltaTool(
        string? title,
        JsonElement rawInput,
        IReadOnlyDictionary<string, AgentToolDefinition> tools)
    {
        if (tools.Count == 0)
        {
            return null;
        }

        if (rawInput.ValueKind == JsonValueKind.Object &&
            (AcpJson.GetString(rawInput, "tool") ?? AcpJson.GetString(rawInput, "toolName")) is { } wrapped &&
            MatchName(wrapped, tools) is { } wrappedName &&
            (AcpJson.GetString(rawInput, "server") ?? AcpJson.GetString(rawInput, "serverName") ?? AcpPrompts.McpServerName) == AcpPrompts.McpServerName)
        {
            return (wrappedName, AcpJson.TryGetObject(rawInput, "arguments", out var arguments) ? arguments.Clone() : AcpJson.EmptyObject);
        }

        if (title is null || MatchName(title.Trim(), tools) is not { } name)
        {
            return null;
        }

        return (name, rawInput.ValueKind == JsonValueKind.Object ? rawInput.Clone() : AcpJson.EmptyObject);
    }

    private static string? MatchName(string text, IReadOnlyDictionary<string, AgentToolDefinition> tools)
    {
        if (tools.ContainsKey(text))
        {
            return text;
        }

        foreach (var prefix in (string[])["mcp__", ""])
        {
            foreach (var separator in ServerSeparators)
            {
                var start = prefix + AcpPrompts.McpServerName + separator;
                if (text.StartsWith(start, StringComparison.OrdinalIgnoreCase) && tools.ContainsKey(text[start.Length..]))
                {
                    return text[start.Length..];
                }
            }
        }

        // "alta (codealta)", "alta (MCP: codealta)".
        var open = text.IndexOf(" (", StringComparison.Ordinal);
        if (open > 0 && text.EndsWith(')') && text.Contains(AcpPrompts.McpServerName, StringComparison.OrdinalIgnoreCase) && tools.ContainsKey(text[..open]))
        {
            return text[..open];
        }

        return null;
    }
}

/// <summary>Reads the configuration options of a session of an agent: its models and its reasoning efforts.</summary>
internal static class AcpConfigOptions
{
    /// <summary>Returns the option of a category (<c>model</c>, <c>thought_level</c>, <c>mode</c>), else the one with that id.</summary>
    public static JsonElement? Find(JsonElement options, string category)
    {
        if (options.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        JsonElement? byId = null;
        foreach (var option in options.EnumerateArray())
        {
            if (string.Equals(AcpJson.GetString(option, "category"), category, StringComparison.Ordinal))
            {
                return option;
            }

            if (string.Equals(AcpJson.GetString(option, "id"), category, StringComparison.Ordinal))
            {
                byId ??= option;
            }
        }

        return byId;
    }

    /// <summary>Lists the values of a select option, flattening its groups; a value listed twice is kept once.</summary>
    public static IReadOnlyList<AcpOptionValue> Values(JsonElement option)
    {
        var values = new List<AcpOptionValue>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (option.TryGetProperty("options", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            Collect(items);
        }

        return values;

        void Collect(JsonElement array)
        {
            foreach (var item in array.EnumerateArray())
            {
                if (item.TryGetProperty("options", out var group) && group.ValueKind == JsonValueKind.Array)
                {
                    Collect(group);
                    continue;
                }

                if (AcpJson.GetString(item, "value") is { Length: > 0 } value && seen.Add(value))
                {
                    values.Add(new AcpOptionValue(value, AcpJson.GetString(item, "name"), AcpJson.GetString(item, "description"),
                        AcpJson.TryGetObject(item, "_meta", out var meta) ? meta.Clone() : default));
                }
            }
        }
    }

    /// <summary>
    /// Lists the models the <c>model</c> option offers. The options are the whole state of the session for its current
    /// model: that one has the reasoning efforts of the <c>thought_level</c> option, none when there is no such option;
    /// what the others have is not known from them.
    /// </summary>
    public static IReadOnlyList<AcpListedModel> ReadModels(JsonElement options)
    {
        if (Find(options, "model") is not { } model)
        {
            return [];
        }

        var current = AcpJson.GetString(model, "currentValue");
        return [.. Values(model).Select(value => string.Equals(value.Value, current, StringComparison.Ordinal)
            ? AcpListedModel.From(value) with { Efforts = ReadEfforts(options), DefaultEffort = CurrentEffort(options) }
            : AcpListedModel.From(value))];
    }

    /// <summary>Lists the reasoning efforts the <c>thought_level</c> option offers now, as CodeAlta names them.</summary>
    public static IReadOnlyList<AgentReasoningEffort> ReadEfforts(JsonElement options)
        => Find(options, "thought_level") is { } option
            ? [.. Values(option).Select(static value => ToEffort(value.Value)).OfType<AgentReasoningEffort>().Distinct()]
            : [];

    /// <summary>Returns the reasoning effort the <c>thought_level</c> option is set to, or <see langword="null"/>.</summary>
    public static AgentReasoningEffort? CurrentEffort(JsonElement options)
        => Find(options, "thought_level") is { } option ? ToEffort(AcpJson.GetString(option, "currentValue")) : null;

    /// <summary>Returns the value of the <c>thought_level</c> option that stands for an effort, or <see langword="null"/>.</summary>
    public static string? EffortValue(JsonElement options, AgentReasoningEffort effort)
        => Find(options, "thought_level") is { } option
            ? Values(option).Select(static value => value.Value).FirstOrDefault(value => ToEffort(value) == effort)
            : null;

    /// <summary>Creates the request that sets a select option of a session to a value.</summary>
    public static SetSessionConfigOptionRequest CreateSetRequest(string sessionId, string configId, string value)
        => new()
        {
            Value = AcpJson.Build(writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("sessionId", sessionId);
                writer.WriteString("configId", configId);
                writer.WriteString("value", value);
                writer.WriteEndObject();
            }),
        };

    public static AgentReasoningEffort? ToEffort(string? value)
        => value?.Trim().ToLowerInvariant() switch
        {
            "none" or "off" => AgentReasoningEffort.None,
            "minimal" => AgentReasoningEffort.Minimal,
            "low" => AgentReasoningEffort.Low,
            "medium" => AgentReasoningEffort.Medium,
            "high" => AgentReasoningEffort.High,
            "xhigh" or "x-high" or "extra-high" => AgentReasoningEffort.XHigh,
            "max" or "maximum" => AgentReasoningEffort.Max,
            _ => null,
        };
}

/// <summary>Says how a prompt ended when it did not end as it should.</summary>
internal static class AcpStopReasons
{
    public static string? Describe(JsonElement stopReason, string agentName)
        => (stopReason.ValueKind == JsonValueKind.String ? stopReason.GetString() : null) switch
        {
            "max_tokens" => $"_{agentName} stopped: its output limit was reached._",
            "max_turn_requests" => $"_{agentName} stopped: the limit of its model requests for one turn was reached._",
            "refusal" => $"_{agentName} refused to go on with this request._",
            _ => null,
        };
}
