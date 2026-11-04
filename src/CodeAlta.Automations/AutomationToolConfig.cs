using System.Globalization;
using System.Text;
using Tomlyn.Model;

namespace CodeAlta.Automations;

/// <summary>
/// Reads and writes the <c>allowed_tools</c> of an autonomy: a tool by its name, or a table that pins it and bounds it,
/// such as <c>{ name = "k8s_restart_pod", hash = "sha256:…", version = "1.4.2", bounds = { namespace = ["shop"], replicas = { min = 1, max = 5 } },
/// preconditions = [{ tool = "k8s_get_deployment", arguments = ["namespace", "name"], expect = "available" }] }</c>.
/// </summary>
internal static class AutomationToolConfig
{
    /// <summary>Reads the allowed tools: their names, in order, and what those written as tables say.</summary>
    internal static bool TryRead(TomlTable table, out IReadOnlyList<string> names, out IReadOnlyList<AutomationAllowedTool> tools, out string? problem)
    {
        (names, tools, problem) = ([], [], null);
        if (!table.TryGetValue("allowed_tools", out var value)) return true;
        IEnumerable<object> items = value switch
        {
            string single => [single],
            TomlArray array => array.Cast<object>(),
            TomlTableArray tables => tables.Cast<object>(),
            _ => [],
        };
        if (value is not (string or TomlArray or TomlTableArray))
        {
            problem = "allowed_tools is a list of tools: names, or tables with a name.";
            return false;
        }

        var read = new List<string>();
        var described = new List<AutomationAllowedTool>();
        foreach (var item in items)
        {
            switch (item)
            {
                case string name:
                    read.Add(name.Trim());
                    break;
                case TomlTable tool when TryReadTool(tool, out var allowed, out problem):
                    read.Add(allowed!.Name);
                    if (allowed.HasDetails) described.Add(allowed);
                    break;
                default:
                    problem ??= "allowed_tools is a list of tools: names, or tables with a name.";
                    return false;
            }
        }

        (names, tools) = (read, described);
        return true;
    }

    private static bool TryReadTool(TomlTable table, out AutomationAllowedTool? tool, out string? problem)
    {
        tool = null;
        foreach (var key in table.Keys)
        {
            if (key is not ("name" or "hash" or "version" or "bounds" or "preconditions"))
            {
                problem = $"An allowed tool has no {key}: name, hash, version, bounds or preconditions.";
                return false;
            }
        }

        if (!AutomationConfig.TryText(table, "name", out var name, out problem) || !AutomationConfig.TryText(table, "hash", out var hash, out problem)
            || !AutomationConfig.TryText(table, "version", out var version, out problem)) return false;
        if (string.IsNullOrWhiteSpace(name))
        {
            problem = "An allowed tool written as a table has a name.";
            return false;
        }

        var bounds = new List<AutomationToolBound>();
        if (table.TryGetValue("bounds", out var boundsValue))
        {
            if (boundsValue is not TomlTable boundsTable)
            {
                problem = $"The bounds of {name} are a table of arguments, such as {{ namespace = [\"shop\"], replicas = {{ min = 1, max = 5 }} }}.";
                return false;
            }

            foreach (var (argument, bound) in boundsTable)
            {
                if (!TryReadBound(name, argument, bound, out var read, out problem)) return false;
                bounds.Add(read!);
            }
        }

        var preconditions = new List<AutomationToolPrecondition>();
        if (table.TryGetValue("preconditions", out var preconditionsValue))
        {
            IEnumerable<object>? items = preconditionsValue switch { TomlArray array => array.Cast<object>(), TomlTableArray tables => tables.Cast<object>(), TomlTable one => [one], _ => null };
            foreach (var item in items ?? [null!])
            {
                if (item is not TomlTable precondition || !TryReadPrecondition(name, precondition, out var read, out problem))
                {
                    problem ??= $"The preconditions of {name} are tables with a tool, the arguments it passes on and what its answer must contain.";
                    return false;
                }

                preconditions.Add(read!);
            }
        }

        tool = new AutomationAllowedTool(name.Trim())
        {
            Hash = string.IsNullOrWhiteSpace(hash) ? null : hash.Trim().ToLowerInvariant(),
            Version = string.IsNullOrWhiteSpace(version) ? null : version.Trim(),
            Bounds = bounds,
            Preconditions = preconditions,
        };
        return true;
    }

    private static bool TryReadBound(string tool, string argument, object value, out AutomationToolBound? bound, out string? problem)
    {
        (bound, problem) = (null, null);
        switch (value)
        {
            case TomlArray values:
                bound = new AutomationToolBound(argument) { Values = [.. values.Select(static item => Text(item))] };
                return true;
            case TomlTable range:
                double? min = null, max = null;
                foreach (var (key, number) in range)
                {
                    if (key is not ("min" or "max") || number is not (long or double))
                    {
                        problem = $"The bound of {argument} of {tool} is a list of values, or {{ min = …, max = … }}.";
                        return false;
                    }

                    var read = Convert.ToDouble(number, CultureInfo.InvariantCulture);
                    if (key == "min") min = read;
                    else max = read;
                }

                bound = new AutomationToolBound(argument) { Min = min, Max = max };
                return true;
            default:
                problem = $"The bound of {argument} of {tool} is a list of values, or {{ min = …, max = … }}.";
                return false;
        }
    }

    private static bool TryReadPrecondition(string tool, TomlTable table, out AutomationToolPrecondition? precondition, out string? problem)
    {
        precondition = null;
        foreach (var key in table.Keys)
        {
            if (key is not ("tool" or "arguments" or "expect"))
            {
                problem = $"A precondition of {tool} has no {key}: tool, arguments or expect.";
                return false;
            }
        }

        if (!AutomationConfig.TryText(table, "tool", out var name, out problem) || !AutomationConfig.TryTexts(table, "arguments", out var arguments, out problem)
            || !AutomationConfig.TryText(table, "expect", out var expect, out problem)) return false;
        if (string.IsNullOrWhiteSpace(name))
        {
            problem = $"A precondition of {tool} names the tool it calls.";
            return false;
        }

        precondition = new AutomationToolPrecondition(name.Trim()) { Arguments = [.. arguments.Select(static argument => argument.Trim())], Expect = expect ?? string.Empty };
        return true;
    }

    // A value of a bound as text: what an argument of the call is compared with.
    private static string Text(object? value) => value switch
    {
        string text => text,
        bool flag => flag ? "true" : "false",
        long number => number.ToString(CultureInfo.InvariantCulture),
        double number => number.ToString("R", CultureInfo.InvariantCulture),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
    };

    /// <summary>Writes the allowed tools: a name for one allowed by its name, an inline table for one that says more.</summary>
    internal static string Format(AutomationAutonomy autonomy)
    {
        var builder = new StringBuilder("[");
        var first = true;
        foreach (var name in autonomy.AllowedTools)
        {
            if (!first) builder.Append(", ");
            first = false;
            if (autonomy.Tool(name) is not { } tool)
            {
                builder.Append(AutomationConfig.Quote(name));
                continue;
            }

            builder.Append("{ name = ").Append(AutomationConfig.Quote(name));
            if (tool.Hash is { } hash) builder.Append(", hash = ").Append(AutomationConfig.Quote(hash));
            if (tool.Version is { } version) builder.Append(", version = ").Append(AutomationConfig.Quote(version));
            if (tool.Bounds.Count > 0)
            {
                builder.Append(", bounds = { ").Append(string.Join(", ", tool.Bounds.Select(FormatBound))).Append(" }");
            }

            if (tool.Preconditions.Count > 0)
            {
                builder.Append(", preconditions = [").Append(string.Join(", ", tool.Preconditions.Select(static precondition =>
                    $"{{ tool = {AutomationConfig.Quote(precondition.Tool)}, arguments = [{string.Join(", ", precondition.Arguments.Select(AutomationConfig.Quote))}], expect = {AutomationConfig.Quote(precondition.Expect)} }}")))
                    .Append(']');
            }

            builder.Append(" }");
        }

        return builder.Append(']').ToString();
    }

    private static string FormatBound(AutomationToolBound bound)
    {
        var key = IsBareKey(bound.Argument) ? bound.Argument : AutomationConfig.Quote(bound.Argument);
        if (bound.Values is { } values) return $"{key} = [{string.Join(", ", values.Select(AutomationConfig.Quote))}]";
        var parts = new List<string>();
        if (bound.Min is { } min) parts.Add("min = " + Number(min));
        if (bound.Max is { } max) parts.Add("max = " + Number(max));
        return $"{key} = {{ {string.Join(", ", parts)} }}";
    }

    private static string Number(double value)
        => value == Math.Floor(value) && Math.Abs(value) < 1e15 ? ((long)value).ToString(CultureInfo.InvariantCulture) : value.ToString("R", CultureInfo.InvariantCulture);

    private static bool IsBareKey(string key) => key.Length > 0 && key.All(static ch => char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-');
}
