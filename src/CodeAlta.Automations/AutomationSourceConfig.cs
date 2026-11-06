using System.Globalization;
using System.Text;
using CodeAlta.Plugins.Abstractions;
using Tomlyn.Model;

namespace CodeAlta.Automations;

/// <summary>
/// The triggers of a trigger source, as one inline table of a configuration file: its <c>type</c>, the events it starts
/// on (<c>on</c>), and every other key as a setting the source reads, such as
/// <c>{ type = "project", on = ["added"], space = "work" }</c>. What a setting means is the source's: this file checks its
/// form only, and the source checks the rest when the automation is loaded, so that the trigger of a plugin that is not
/// active is kept as written.
/// </summary>
internal static class AutomationSourceConfig
{
    /// <summary>Reads the trigger of a source from its table.</summary>
    /// <param name="type">The type of the source, already checked with <see cref="AutomationSourceTypes.IsType"/>.</param>
    /// <param name="table">The table of the trigger.</param>
    /// <param name="trigger">The trigger read.</param>
    /// <param name="problem">What is wrong with the table.</param>
    /// <returns>True when the trigger was read.</returns>
    internal static bool TryRead(string type, TomlTable table, out AutomationTrigger? trigger, out string? problem)
    {
        trigger = null;
        if (!AutomationConfig.TryTexts(table, "on", out var on, out problem)) return false;
        var values = new List<KeyValuePair<string, PluginTriggerValue>>();
        foreach (var (key, value) in table)
        {
            if (key is "type" or "on") continue;
            PluginTriggerValue? read = value switch
            {
                string text => PluginTriggerValue.OfText(text),
                bool flag => PluginTriggerValue.OfFlag(flag),
                long number => PluginTriggerValue.OfNumber(number),
                TomlArray array when array.All(static item => item is string) => PluginTriggerValue.OfTexts(array.Cast<string>()),
                _ => null,
            };
            if (read is null)
            {
                problem = $"{AutomationText.Line(key, 64)} is a text, true or false, a whole number or a list of texts.";
                return false;
            }

            values.Add(new(key, read));
        }

        var events = new List<string>();
        foreach (var name in on.Select(static text => text.Trim().ToLowerInvariant()))
        {
            if (!events.Contains(name, StringComparer.Ordinal)) events.Add(name);
        }

        trigger = new AutomationTrigger(AutomationTriggerKind.Source) { Source = type, On = events, Settings = new PluginTriggerSettings(values) };
        return true;
    }

    /// <summary>Checks the form of the trigger of a source: its type, its events and its settings.</summary>
    /// <returns>What is wrong with it, or null.</returns>
    internal static string? Validate(AutomationTrigger trigger)
    {
        ArgumentNullException.ThrowIfNull(trigger);
        if (!AutomationSourceTypes.IsType(trigger.Source))
            return $"'{AutomationText.Line(trigger.Source, 80)}' is not a trigger source: {string.Join(", ", AutomationSourceTypes.BuiltIn)}, or <prefix>/<name> for a trigger of a plugin.";
        if (trigger.On.Count > AutomationSourceTypes.MaximumEvents) return $"A trigger starts on at most {AutomationSourceTypes.MaximumEvents} events.";
        if (trigger.On.FirstOrDefault(static name => !AutomationSourceTypes.IsName(name)) is { } wrong)
            return $"'{AutomationText.Line(wrong, 64)}' is not the name of an event: lowercase letters, digits and _.";
        if (trigger.On.Distinct(StringComparer.Ordinal).Count() != trigger.On.Count) return "A trigger names an event twice.";
        if (trigger.Settings.Count > AutomationSourceTypes.MaximumSettings) return $"A trigger has at most {AutomationSourceTypes.MaximumSettings} settings.";
        foreach (var (key, value) in trigger.Settings.Values)
        {
            if (!AutomationSourceTypes.IsName(key) || key is "type" or "on")
                return $"'{AutomationText.Line(key, 64)}' is not the name of a setting of a trigger: lowercase letters, digits and _.";
            if (value.Kind == PluginTriggerValueKind.Text && TextProblem(value.Text!) is { } text) return $"{key} {text}";
            if (value.Kind == PluginTriggerValueKind.Texts)
            {
                if (value.Texts.Count > AutomationSourceTypes.MaximumValues) return $"{key} has at most {AutomationSourceTypes.MaximumValues} texts.";
                if (value.Texts.Select(TextProblem).OfType<string>().FirstOrDefault() is { } texts) return $"{key} {texts}";
            }
        }

        return null;
    }

    /// <summary>Writes the trigger of a source as an inline table.</summary>
    internal static string Format(AutomationTrigger trigger)
    {
        ArgumentNullException.ThrowIfNull(trigger);
        var builder = new StringBuilder("{ type = ").Append(AutomationConfig.Quote(trigger.Source ?? string.Empty));
        if (trigger.On.Count > 0) builder.Append(", on = ").Append(List(trigger.On));
        foreach (var (key, value) in trigger.Settings.Values)
        {
            builder.Append(", ").Append(key).Append(" = ").Append(value.Kind switch
            {
                PluginTriggerValueKind.Text => AutomationConfig.Quote(value.Text!),
                PluginTriggerValueKind.Flag => value.Flag ? "true" : "false",
                PluginTriggerValueKind.Number => value.Number.ToString(CultureInfo.InvariantCulture),
                _ => List(value.Texts),
            });
        }

        return builder.Append(" }").ToString();

        static string List(IEnumerable<string> values) => "[" + string.Join(", ", values.Select(AutomationConfig.Quote)) + "]";
    }

    private static string? TextProblem(string text)
        => text.Length > AutomationSourceTypes.MaximumValueLength || text.Any(char.IsControl) || !AutomationConfig.IsWellFormed(text)
            ? $"is one line of at most {AutomationSourceTypes.MaximumValueLength} characters." : null;
}
