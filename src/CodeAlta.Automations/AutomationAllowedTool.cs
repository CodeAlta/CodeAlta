using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodeAlta.Agent;

namespace CodeAlta.Automations;

/// <summary>
/// What an allowed tool must be, and stay within, to run without a person (<c>doc/fleet/orchestration.md</c>, "Autonomy and
/// approvals"): its <b>pinned implementation</b> (the hash of its definition, and the version of its server), the
/// <b>bounds</b> of its arguments, and its <b>preconditions</b>: read-only tools it calls first, whose answer must say what
/// they expect. A call that does not hold its pin or bounds waits for a person; one whose precondition fails is not made.
/// </summary>
/// <param name="Name">The tool, as the run calls it.</param>
internal sealed record AutomationAllowedTool(string Name)
{
    /// <summary>What a hash of a definition starts with.</summary>
    internal const string HashPrefix = "sha256:";

    /// <summary>The most bounds of a tool.</summary>
    internal const int MaximumBounds = 16;

    /// <summary>The most preconditions of a tool.</summary>
    internal const int MaximumPreconditions = 4;

    /// <summary>The most values a bound lists.</summary>
    internal const int MaximumValues = 64;

    /// <summary>Gets the version of the server of the tool it is pinned to, as that server says it; null for any.</summary>
    public string? Version { get; init; }

    /// <summary>Gets the hash of the definition of the tool it is pinned to (<see cref="HashOf"/>); null for any.</summary>
    public string? Hash { get; init; }

    /// <summary>Gets the bounds of its arguments.</summary>
    public IReadOnlyList<AutomationToolBound> Bounds { get; init; } = [];

    /// <summary>Gets the tools it calls first, which must say what they expect.</summary>
    public IReadOnlyList<AutomationToolPrecondition> Preconditions { get; init; } = [];

    /// <summary>Whether it says more than its name.</summary>
    internal bool HasDetails => Version is not null || Hash is not null || Bounds.Count > 0 || Preconditions.Count > 0;

    /// <summary>A text that changes with every part, for the fingerprint of the automation.</summary>
    internal string Key => string.Join('\u001f', Name, Version ?? string.Empty, Hash ?? string.Empty, string.Join('\u001e', Bounds.Select(static bound => bound.Key)),
        string.Join('\u001e', Preconditions.Select(static precondition => precondition.Key)));

    /// <summary>
    /// The hash of the definition of a tool as the model is given it: its name, description and the schema of its
    /// arguments; what a pin names, so that a tool whose definition changed is not called unattended.
    /// </summary>
    /// <param name="spec">The tool.</param>
    /// <returns><c>sha256:</c> and the lowercase hexadecimal of the SHA-256.</returns>
    internal static string HashOf(AgentToolSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        using var buffer = new MemoryStream();
        buffer.Write(Encoding.UTF8.GetBytes(spec.Name + "\n" + spec.Description + "\n"));
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false }))
        {
            if (spec.InputSchema.ValueKind is JsonValueKind.Undefined) writer.WriteNullValue();
            else spec.InputSchema.WriteTo(writer);
        }

        return HashPrefix + Convert.ToHexStringLower(SHA256.HashData(buffer.ToArray()));
    }

    /// <summary>Why a call does not hold the pin of the tool; null when it does.</summary>
    /// <param name="spec">The tool the call runs; null for one the host does not know.</param>
    internal string? CheckPin(AgentToolSpec? spec)
    {
        if (Hash is null && Version is null) return null;
        if (spec is null) return $"the definition of {Name} is not known here, so it cannot be checked against its pin";
        if (Hash is { } pinned && !string.Equals(HashOf(spec), pinned, StringComparison.OrdinalIgnoreCase))
            return $"the definition of {Name} changed since it was pinned ({AutomationText.Line(HashOf(spec), 80)})";
        if (Version is { } version && !string.Equals(spec.Version, version, StringComparison.Ordinal))
            return $"{Name} is at version {spec.Version ?? "unknown"}, not {version} it was pinned to";
        return null;
    }

    /// <summary>Why the arguments of a call are out of the bounds of the tool; null when they are within.</summary>
    /// <param name="arguments">The arguments of the call.</param>
    internal string? CheckBounds(JsonElement arguments)
    {
        foreach (var bound in Bounds)
        {
            if (bound.Check(arguments) is { } problem) return problem;
        }

        return null;
    }

    /// <summary>What is wrong with it, or null.</summary>
    /// <param name="allowed">The tools the automation allows.</param>
    internal string? Validate(IReadOnlyList<string> allowed)
    {
        if (Version is { Length: 0 or > 64 }) return $"The version {Name} is pinned to is 1 to 64 characters.";
        if (Hash is { } hash && (!hash.StartsWith(HashPrefix, StringComparison.Ordinal) || hash.Length != HashPrefix.Length + 64 || !hash[HashPrefix.Length..].All(Uri.IsHexDigit)))
            return $"The hash {Name} is pinned to is sha256: and 64 hexadecimal digits, as a try says it.";
        if (Bounds.Count > MaximumBounds) return $"A tool has at most {MaximumBounds} bounds.";
        if (Preconditions.Count > MaximumPreconditions) return $"A tool has at most {MaximumPreconditions} preconditions.";
        foreach (var bound in Bounds)
        {
            if (bound.Validate() is { } problem) return $"{Name}: {problem}";
        }

        if (Bounds.Select(static bound => bound.Argument).Distinct(StringComparer.Ordinal).Count() != Bounds.Count) return $"{Name} bounds an argument twice.";
        foreach (var precondition in Preconditions)
        {
            if (precondition.Tool == Name || !allowed.Contains(precondition.Tool, StringComparer.Ordinal))
                return $"A precondition of {Name} calls {precondition.Tool}: a tool the automation also allows, other than {Name}, to read the target before acting.";
            if (precondition.Validate() is { } problem) return $"{Name}: {problem}";
        }

        return null;
    }

    /// <summary>Whether a name is one an argument of a tool can have.</summary>
    internal static bool IsArgument(string name) => name.Length is > 0 and <= 64 && name.All(static ch => char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-' or '.');
}

/// <summary>A bound on an argument of an allowed tool: the values it may take, or the range of a number.</summary>
/// <param name="Argument">The argument, a top-level property of the arguments of the call.</param>
internal sealed record AutomationToolBound(string Argument)
{
    /// <summary>Gets the values it may take, as text; null for any.</summary>
    public IReadOnlyList<string>? Values { get; init; }

    /// <summary>Gets the least number it may be; null for no least.</summary>
    public double? Min { get; init; }

    /// <summary>Gets the greatest number it may be; null for no greatest.</summary>
    public double? Max { get; init; }

    /// <summary>A text that changes with every part.</summary>
    internal string Key => string.Join('\u001d', Argument, Values is null ? "*" : string.Join('\u001c', Values),
        Min?.ToString("R", CultureInfo.InvariantCulture) ?? string.Empty, Max?.ToString("R", CultureInfo.InvariantCulture) ?? string.Empty);

    /// <summary>Why the arguments of a call are out of it; null when they are within.</summary>
    internal string? Check(JsonElement arguments)
    {
        // A bound holds a value: an argument it names that the call leaves out is out of it, as one the tool would default.
        if (arguments.ValueKind is not JsonValueKind.Object || !arguments.TryGetProperty(Argument, out var value))
            return $"{Argument} is not given, and its bound says what it may be";
        if (Values is { } values)
        {
            var text = value.ValueKind is JsonValueKind.String ? value.GetString()! : value.GetRawText();
            if (!values.Contains(text, StringComparer.Ordinal)) return $"{Argument} is {AutomationText.Line(text, 60)}, not one of {string.Join(", ", values)}";
        }

        if (Min is not null || Max is not null)
        {
            if (value.ValueKind is not JsonValueKind.Number || !value.TryGetDouble(out var number)) return $"{Argument} is not a number";
            if (number < Min) return $"{Argument} is {number.ToString(CultureInfo.InvariantCulture)}, less than {Min.Value.ToString(CultureInfo.InvariantCulture)}";
            if (number > Max) return $"{Argument} is {number.ToString(CultureInfo.InvariantCulture)}, more than {Max.Value.ToString(CultureInfo.InvariantCulture)}";
        }

        return null;
    }

    /// <summary>What is wrong with it, or null.</summary>
    internal string? Validate()
    {
        if (!AutomationAllowedTool.IsArgument(Argument)) return $"'{AutomationText.Line(Argument, 40)}' is not the name of an argument.";
        if (Values is null && Min is null && Max is null) return $"the bound of {Argument} lists values, or a min and a max.";
        if (Values is { } values && (values.Count is 0 or > AutomationAllowedTool.MaximumValues || values.Any(static value => value.Length > 256)))
            return $"the bound of {Argument} lists 1 to {AutomationAllowedTool.MaximumValues} values.";
        if (Min is { } min && Max is { } max && min > max) return $"the bound of {Argument} has a min above its max.";
        if (Min is { } low && !double.IsFinite(low) || Max is { } high && !double.IsFinite(high)) return $"the bound of {Argument} is a finite number.";
        return null;
    }
}

/// <summary>
/// A precondition of an allowed tool: a tool the automation also allows, called first with arguments of the call, whose
/// answer must contain what it expects. It is called again after an approval that waited, before the call is made.
/// </summary>
/// <param name="Tool">The tool it calls, one that reads the target.</param>
internal sealed record AutomationToolPrecondition(string Tool)
{
    /// <summary>Gets the arguments of the call it passes on, by name.</summary>
    public IReadOnlyList<string> Arguments { get; init; } = [];

    /// <summary>Gets what its answer must contain, ignoring case.</summary>
    public string Expect { get; init; } = string.Empty;

    /// <summary>A text that changes with every part.</summary>
    internal string Key => string.Join('\u001d', Tool, string.Join('\u001c', Arguments), Expect);

    /// <summary>The arguments it calls its tool with: those of the call it names.</summary>
    internal JsonElement ArgumentsOf(JsonElement call)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            if (call.ValueKind is JsonValueKind.Object)
            {
                foreach (var name in Arguments)
                {
                    if (!call.TryGetProperty(name, out var value)) continue;
                    writer.WritePropertyName(name);
                    value.WriteTo(writer);
                }
            }

            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(buffer.ToArray());
        return document.RootElement.Clone();
    }

    /// <summary>What is wrong with it, or null.</summary>
    internal string? Validate()
    {
        if (Arguments.Count > AutomationAllowedTool.MaximumBounds || Arguments.Any(static name => !AutomationAllowedTool.IsArgument(name)))
            return $"a precondition passes on arguments by their names, at most {AutomationAllowedTool.MaximumBounds}.";
        if (Expect.Trim().Length is 0 or > 256) return $"a precondition calling {Tool} says what its answer must contain, 1 to 256 characters.";
        return null;
    }
}
