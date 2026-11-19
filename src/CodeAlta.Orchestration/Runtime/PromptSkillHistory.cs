using System.Text.Json;
using System.Text.RegularExpressions;
using CodeAlta.Agent;

namespace CodeAlta.Orchestration.Runtime;

/// <summary>
/// Reads which skills a persisted user message gave the model: the notes of kind
/// <see cref="AgentInputItem.HostContext.SkillKind"/> the host put before its text, one per <c>/name</c> it expanded.
/// </summary>
public static partial class PromptSkillHistory
{
    /// <summary>Most skills read from one message.</summary>
    public const int MaximumSkills = 32;

    /// <summary>Lists the names of the skills given with a user message, in their order.</summary>
    /// <param name="details">The details of a completed user content event: its recorded input.</param>
    /// <returns>The names, possibly empty; a note whose content names no skill is left out.</returns>
    public static IReadOnlyList<string> ReadSkills(JsonElement? details)
    {
        if (details is not { ValueKind: JsonValueKind.Object } root || !root.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array) return [];
        var names = new List<string>();
        foreach (var item in items.EnumerateArray())
        {
            if (names.Count == MaximumSkills) break;
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("$type", out var type) || !type.ValueEquals("hostContext")
                || !item.TryGetProperty("kind", out var kind) || !kind.ValueEquals(AgentInputItem.HostContext.SkillKind)
                || !item.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.String) continue;
            var match = SkillName().Match(value.GetString()!);
            if (match.Success) names.Add(match.Groups["name"].Value);
        }

        return names;
    }

    // The name the content of a skill begins with (SkillCatalog writes it so).
    [GeneratedRegex(@"<skill_content name=""(?<name>[^""\s<>]{1,128})""", RegexOptions.CultureInvariant)]
    private static partial Regex SkillName();
}
