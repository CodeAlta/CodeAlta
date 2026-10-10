using System.Diagnostics.CodeAnalysis;

namespace CodeAlta.Plugins.Abstractions;

/// <summary>Names a place of the desktop window where a plugin can put a button.</summary>
public enum PluginButtonPlace
{
    /// <summary>The top right of the title bar, before the space switch, the zoom and the theme. The button is given the shown space and the selected project and session.</summary>
    TitleBar,

    /// <summary>The navigation rail at the left, after the buttons of the application and before Settings. The button is given the shown space and the selected project and session.</summary>
    Rail,

    /// <summary>The menu of a project row of the Explorer, as a line. The button is given that project, whatever is selected.</summary>
    ProjectMenu,

    /// <summary>The menu of a session row, as a line. The button is given that session and its project, whatever is selected.</summary>
    SessionMenu,
}

/// <summary>The limits on the buttons of one plugin.</summary>
public static class PluginButtonLimits
{
    /// <summary>The largest number of buttons one plugin has in the title bar.</summary>
    public const int TitleBar = 2;

    /// <summary>The largest number of buttons one plugin has in the rail.</summary>
    public const int Rail = 1;

    /// <summary>The largest number of buttons one plugin has in the menu of a project or of a session.</summary>
    public const int Menu = 6;

    /// <summary>The longest identifier of a button, in characters.</summary>
    public const int MaximumIdLength = 64;

    /// <summary>The longest label of a button, in characters.</summary>
    public const int MaximumLabelLength = 100;

    /// <summary>Gets the largest number of buttons one plugin has at a place.</summary>
    /// <param name="place">The place.</param>
    /// <returns>The limit.</returns>
    public static int For(PluginButtonPlace place) => place switch
    {
        PluginButtonPlace.TitleBar => TitleBar,
        PluginButtonPlace.Rail => Rail,
        _ => Menu,
    };
}

/// <summary>What a button shows beside its icon: nothing, a number, a dot, or a small ring while something is read.</summary>
public enum PluginButtonBadgeKind
{
    /// <summary>No badge.</summary>
    None,

    /// <summary>A number, <see cref="PluginButtonBadge.Count"/>.</summary>
    Count,

    /// <summary>A dot: something deserves a look.</summary>
    Dot,

    /// <summary>An indeterminate small ring: the plugin is busy.</summary>
    Busy,
}

/// <summary>The badge of a button: a number, a dot or a busy ring. An integer converts to a number badge, and 0 or less to none.</summary>
public readonly record struct PluginButtonBadge
{
    private PluginButtonBadge(PluginButtonBadgeKind kind, int count)
    {
        Kind = kind;
        Count = count;
    }

    /// <summary>Gets the badge without anything to show.</summary>
    public static PluginButtonBadge None => default;

    /// <summary>Gets a dot badge.</summary>
    public static PluginButtonBadge Dot => new(PluginButtonBadgeKind.Dot, 0);

    /// <summary>Gets an indeterminate ring, for a plugin that is busy.</summary>
    public static PluginButtonBadge Busy => new(PluginButtonBadgeKind.Busy, 0);

    /// <summary>Gets what the badge is.</summary>
    public PluginButtonBadgeKind Kind { get; }

    /// <summary>Gets the number of a <see cref="PluginButtonBadgeKind.Count"/> badge.</summary>
    public int Count { get; }

    /// <summary>Creates a number badge.</summary>
    /// <param name="count">The number; zero or less gives no badge.</param>
    /// <returns>The badge.</returns>
    public static PluginButtonBadge Of(int count) => count > 0 ? new(PluginButtonBadgeKind.Count, count) : None;

    /// <summary>Converts a number to a number badge, or to no badge when it is zero or less.</summary>
    /// <param name="count">The number.</param>
    public static implicit operator PluginButtonBadge(int count) => Of(count);
}

/// <summary>What a plugin says about one of its buttons at one moment.</summary>
public sealed record PluginButtonState
{
    /// <summary>Gets the state of a button that has nothing particular to say.</summary>
    public static PluginButtonState Default { get; } = new();

    /// <summary>Gets the badge.</summary>
    public PluginButtonBadge Badge { get; init; }

    /// <summary>Gets the tone of the icon and of the badge.</summary>
    public PluginStatusTone Tone { get; init; } = PluginStatusTone.Info;

    /// <summary>Gets a value indicating whether the button is left out for now.</summary>
    public bool Hidden { get; init; }

    /// <summary>Gets a value indicating whether the button is shown but cannot be used for now.</summary>
    public bool Disabled { get; init; }

    /// <summary>Gets a tooltip that replaces the label, or <see langword="null"/> to show the label.</summary>
    public string? Tooltip { get; init; }
}

/// <summary>What a button is asked about: where it is, and the space, project and session it is for.</summary>
/// <param name="Place">The place of the button.</param>
/// <param name="SpaceId">The shown space, or <see langword="null"/> when the window has no spaces.</param>
/// <param name="ProjectId">The project: the selected one for the title bar and the rail, the one of the row for the menus. <see langword="null"/> for none.</param>
/// <param name="SessionId">The session: the selected one for the title bar and the rail, the one of the row for a session menu. <see langword="null"/> for none.</param>
public sealed record PluginButtonContext(PluginButtonPlace Place, string? SpaceId, string? ProjectId, string? SessionId);

/// <summary>
/// A button that a plugin puts in the desktop window: an icon and a label at a named place, which runs a command of the
/// plugin or opens a canvas of the plugin.
/// </summary>
/// <remarks>
/// <para>
/// Return it from <see cref="PluginBase.GetUiContributions"/>, so its registration, scope and order follow those of status
/// items and region content. <see cref="PluginUi.Button"/> creates one. A plugin has at most
/// <see cref="PluginButtonLimits.For"/> buttons at a place; the extras, and the buttons that name neither or both of a
/// command and a canvas, are left out with a diagnostic.
/// </para>
/// <para>The terminal application draws no button: the command a button names stays in its palette.</para>
/// </remarks>
public sealed record PluginButtonContribution : PluginUiContribution
{
    /// <summary>Creates a button. Set <see cref="Place"/>, <see cref="Id"/>, <see cref="Icon"/>, <see cref="Label"/> and one of <see cref="Command"/> and <see cref="Canvas"/>.</summary>
    [SetsRequiredMembers]
    public PluginButtonContribution()
    {
        // A button has no region around the prompt: the field only satisfies the base type, and the button ignores it.
        Region = PluginUiRegion.CommandBar;
    }

    /// <summary>Gets the place of the button.</summary>
    public PluginButtonPlace Place { get; init; }

    /// <summary>Gets the identifier of the button in its plugin: 1 to <see cref="PluginButtonLimits.MaximumIdLength"/> letters, digits, <c>-</c>, <c>_</c> or <c>.</c>. The user's choice to hide a button is kept under it.</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>
    /// Gets the icon: the name of an icon of the Lucide icon library (<c>chart-column</c>), the name of a brand logo, or
    /// the path of an SVG file of the plugin package, relative to its folder (<c>icons/statistics.svg</c>).
    /// An icon that is not found is replaced by a neutral one.
    /// </summary>
    public string Icon { get; init; } = string.Empty;

    /// <summary>Gets the label: the tooltip, the accessible name and the text of a menu line.</summary>
    public string Label { get; init; } = string.Empty;

    /// <summary>
    /// Gets the name of a command of the same plugin that the button runs, as <see cref="PluginStatusItem.Command"/> names
    /// one, or <see langword="null"/> when the button opens a canvas. The command runs for the context of the button.
    /// </summary>
    public string? Command { get; init; }

    /// <summary>
    /// Gets the identifier of a canvas of the same plugin that the button opens, with the context of the button, or
    /// <see langword="null"/> when the button runs a command. No handler runs.
    /// </summary>
    public string? Canvas { get; init; }

    /// <summary>
    /// Gets the callback that gives the state of the button, or <see langword="null"/> for a button that is always shown as it is.
    /// It is called synchronously each time the host reads the buttons (when a command of the plugin ends, and when the
    /// plugin calls <see cref="IPluginUiService.InvalidateButtons"/>), so it must be cheap: read a field, never a file or
    /// the network. A callback that throws leaves the button in its default state.
    /// </summary>
    public Func<PluginButtonContext, PluginButtonState?>? GetState { get; init; }

    /// <summary>Gets a message that says why a button is not valid, or <see langword="null"/> when it is.</summary>
    /// <returns>The reason, in a sentence.</returns>
    public string? Validate()
    {
        if (!Enum.IsDefined(Place)) return "has an unknown place";
        if (!IsValidId(Id)) return $"has an identifier that is not 1 to {PluginButtonLimits.MaximumIdLength} letters, digits, '-', '_' or '.'";
        if (string.IsNullOrWhiteSpace(Label) || Label.Length > PluginButtonLimits.MaximumLabelLength) return $"has no label, or one longer than {PluginButtonLimits.MaximumLabelLength} characters";
        if (string.IsNullOrWhiteSpace(Icon)) return "has no icon";
        var command = !string.IsNullOrWhiteSpace(Command);
        var canvas = !string.IsNullOrWhiteSpace(Canvas);
        if (command == canvas) return "must name exactly one of a command and a canvas";
        return null;
    }

    /// <summary>Determines whether a text can identify a button.</summary>
    /// <param name="id">The text.</param>
    /// <returns><see langword="true"/> when it is 1 to <see cref="PluginButtonLimits.MaximumIdLength"/> letters, digits, <c>-</c>, <c>_</c> or <c>.</c>.</returns>
    public static bool IsValidId(string? id)
        => id is { Length: > 0 and <= PluginButtonLimits.MaximumIdLength } && id.All(static value => char.IsAsciiLetterOrDigit(value) || value is '-' or '_' or '.');
}
