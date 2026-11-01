using CodeAlta.Plugins;
using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Desktop.Settings;

/// <summary>
/// The settings, as a plugin of the window: it tells the sessions that they find, read and show the settings of
/// CodeAlta with <c>alta settings</c>, and change them only when the user asks. The registry and the command are the
/// window's (<see cref="DesktopSettings"/>); the plugin holds no state of its own.
/// </summary>
[Plugin(PluginId, DisplayName = "Settings", Description = Summary)]
internal sealed class DesktopSettingsPlugin : PluginBase
{
    /// <summary>The id of the plugin.</summary>
    internal const string PluginId = "settings";

    private const string Summary = "The settings of CodeAlta Desktop for agents: alta settings finds, reads and shows them, and changes those agents may change when the user asks.";

    /// <summary>What the instructions of a session say about the settings.</summary>
    internal const string Guidance = "Settings: `alta settings list <words>` finds a setting of CodeAlta and its id, `alta settings get <id>` reads it and `alta settings open <id>` shows it to the user in Settings; " +
        "change one with `alta settings set` only when the user asks.";

    /// <summary>The definition a window lists among its built-in plugins.</summary>
    internal static BuiltInPluginDefinition Definition()
        => new()
        {
            Id = PluginId, DisplayName = "Settings", Description = Summary,
            PluginType = typeof(DesktopSettingsPlugin), Factory = static () => new DesktopSettingsPlugin(),
        };

    /// <inheritdoc />
    public override IEnumerable<PluginSystemPromptContribution> GetSystemPromptContributions()
    {
        // One line, the same from turn to turn: provider prompt caches survive.
        yield return Prompt.Dynamic(
            PluginPromptChannel.Developer,
            static (_, _) => new ValueTask<string?>(Guidance),
            title: "Settings",
            kind: PluginPromptPartKind.ToolGuidance,
            order: 56);
    }
}
