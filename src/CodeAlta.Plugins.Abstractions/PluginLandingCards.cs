namespace CodeAlta.Plugins.Abstractions;

/// <summary>The limits on the cards that one plugin pins on the landing page.</summary>
public static class PluginLandingCardLimits
{
    /// <summary>The largest number of cards one plugin pins.</summary>
    public const int Cards = 2;

    /// <summary>The largest number of actions of one card.</summary>
    public const int Actions = 3;

    /// <summary>The longest identifier of a card, in characters.</summary>
    public const int MaximumIdLength = 64;

    /// <summary>The longest title of a card, in characters.</summary>
    public const int MaximumTitleLength = 80;

    /// <summary>The longest label of an action and the longest status text of a card, in characters.</summary>
    public const int MaximumLabelLength = 60;

    /// <summary>The most characters of the HTML fragment of a card; a longer one is cut.</summary>
    public const int MaximumHtmlLength = 32 * 1024;
}

/// <summary>What a card of the landing page is asked about: the space the page is shown in, and the project the plugin belongs to.</summary>
/// <param name="SpaceId">The space the landing page is shown in, or <see langword="null"/> when the window has no spaces.</param>
/// <param name="ProjectId">
/// The project of a plugin that belongs to one (a plugin of the folder of a project): the commands and the canvases of its card are about
/// that project. <see langword="null"/> for a plugin of the application or of the user.
/// </param>
public sealed record PluginLandingCardContext(string? SpaceId, string? ProjectId);

/// <summary>Writes what a card of the landing page shows now.</summary>
/// <param name="context">The space and the project the card is asked about.</param>
/// <param name="cancellationToken">A token cancelled after a few seconds, the time the page waits for the card, or when the plugin stops.</param>
/// <returns>What the card shows, or <see langword="null"/> to leave the card out for now.</returns>
public delegate ValueTask<PluginLandingCard?> PluginLandingCardHandler(PluginLandingCardContext context, CancellationToken cancellationToken);

/// <summary>
/// A card that a plugin pins on the landing page of the desktop application: a title, an icon, a short piece of content and a few actions.
/// </summary>
/// <remarks>
/// <para>
/// Return it from <see cref="PluginBase.GetLandingCards"/>. The page asks <see cref="GetCard"/> for the content each time it is shown, when the
/// plugin calls <see cref="IPluginUiService.InvalidateLandingCards"/>, and when a command of the plugin ends. A plugin pins at most
/// <see cref="PluginLandingCardLimits.Cards"/> cards; the extras and the cards that are not valid are left out with a diagnostic.
/// </para>
/// <para>
/// A card that throws, or that does not answer in a few seconds, is shown as a card that could not be read: the other cards and the page
/// are not affected. A card is asked once at a time for a space: while a call runs, the readings that come wait for it. The terminal
/// application has no landing page and never asks.
/// </para>
/// </remarks>
public sealed record PluginLandingCardContribution
{
    /// <summary>Gets the identifier of the card in its plugin: 1 to <see cref="PluginLandingCardLimits.MaximumIdLength"/> letters, digits, <c>-</c>, <c>_</c> or <c>.</c>.</summary>
    public required string Id { get; init; }

    /// <summary>Gets the title of the card.</summary>
    public required string Title { get; init; }

    /// <summary>
    /// Gets the icon of the card, as the icon of a button of a plugin is given (<see cref="PluginButtonContribution.Icon"/>): the name of an icon
    /// of the Lucide icon library (<c>list-checks</c>), the name of a brand logo, or the path of an SVG file of the plugin package. Without one,
    /// or when it is not found, the card has a neutral icon.
    /// </summary>
    public string? Icon { get; init; }

    /// <summary>Gets the order among the cards of the plugins; the lower comes first.</summary>
    public int Order { get; init; }

    /// <summary>Gets the handler that writes what the card shows now. It may read a file or a database: it runs off the window, with a timeout.</summary>
    public required PluginLandingCardHandler GetCard { get; init; }

    /// <summary>Gets a message that says why a card is not valid, or <see langword="null"/> when it is.</summary>
    /// <returns>The reason, in a sentence.</returns>
    public string? Validate()
    {
        if (!PluginButtonContribution.IsValidId(Id)) return $"has an identifier that is not 1 to {PluginLandingCardLimits.MaximumIdLength} letters, digits, '-', '_' or '.'";
        if (string.IsNullOrWhiteSpace(Title) || Title.Length > PluginLandingCardLimits.MaximumTitleLength) return $"has no title, or one longer than {PluginLandingCardLimits.MaximumTitleLength} characters";
        if (GetCard is null) return "has no handler";
        return null;
    }
}

/// <summary>
/// What a card of the landing page shows at one moment: an HTML fragment, a short status and a few actions.
/// </summary>
/// <remarks>
/// The fragment is sanitized and drawn by the application as it draws the other HTML of a plugin, with the same vocabulary
/// (<see cref="PluginHtml"/>): <c>data-alta-command</c> runs a command of the plugin, <see cref="PluginHtml.Markdown"/> writes Markdown and
/// <see cref="PluginHtml.Chart"/> a chart. A card has no script and no action handler: what it does is a command or a canvas of its plugin.
/// </remarks>
public sealed record PluginLandingCard
{
    /// <summary>Gets the HTML fragment of the card, as <see cref="PluginHtml"/> describes. It is cut at <see cref="PluginLandingCardLimits.MaximumHtmlLength"/> characters.</summary>
    public string Html { get; init; } = string.Empty;

    /// <summary>Gets a short text shown beside the title (for example <c>3 open</c>), or <see langword="null"/> for none.</summary>
    public string? Status { get; init; }

    /// <summary>Gets the tone of the status text.</summary>
    public PluginStatusTone Tone { get; init; } = PluginStatusTone.Info;

    /// <summary>
    /// Gets the actions shown at the bottom of the card, at most <see cref="PluginLandingCardLimits.Actions"/>. The extras, and the actions that
    /// name a command or a canvas the plugin does not have, are left out.
    /// </summary>
    public IReadOnlyList<PluginLandingCardAction> Actions { get; init; } = [];

    /// <summary>Creates what a card shows from an HTML fragment and its actions.</summary>
    /// <param name="html">The HTML fragment.</param>
    /// <param name="actions">The actions of the card.</param>
    /// <returns>The card.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="html"/> or <paramref name="actions"/> is <see langword="null"/>.</exception>
    public static PluginLandingCard Of(string html, params PluginLandingCardAction[] actions)
    {
        ArgumentNullException.ThrowIfNull(html);
        ArgumentNullException.ThrowIfNull(actions);
        return new PluginLandingCard { Html = html, Actions = actions };
    }
}

/// <summary>
/// An action of a card of the landing page: a button that runs a command of the plugin or opens a canvas of the plugin, as a button of a
/// plugin does (<see cref="PluginButtonContribution"/>).
/// </summary>
public sealed record PluginLandingCardAction
{
    /// <summary>Gets the label of the button: 1 to <see cref="PluginLandingCardLimits.MaximumLabelLength"/> characters.</summary>
    public required string Label { get; init; }

    /// <summary>Gets the icon of the button, as <see cref="PluginLandingCardContribution.Icon"/> is given, or <see langword="null"/> for none.</summary>
    public string? Icon { get; init; }

    /// <summary>
    /// Gets the name of a command of the same plugin that the action runs, or <see langword="null"/> when it opens a canvas. The command runs
    /// for the space and the project of the card.
    /// </summary>
    public string? Command { get; init; }

    /// <summary>Gets the identifier of a canvas of the same plugin that the action opens, or <see langword="null"/> when it runs a command.</summary>
    public string? Canvas { get; init; }

    /// <summary>Gets the key of the instance of <see cref="Canvas"/> to open (<see cref="PluginCanvasOpenOptions.Key"/>), or <see langword="null"/>.</summary>
    public string? Key { get; init; }

    /// <summary>Gets a value indicating whether the button is the main action of the card.</summary>
    public bool Primary { get; init; }

    /// <summary>Creates an action that runs a command of the plugin.</summary>
    /// <param name="label">The label of the button.</param>
    /// <param name="command">The name of the command, as <see cref="PluginCommandContribution.Name"/> gives it.</param>
    /// <param name="icon">The icon of the button, or <see langword="null"/>.</param>
    /// <returns>The action.</returns>
    /// <exception cref="ArgumentException"><paramref name="label"/> or <paramref name="command"/> is null, empty or whitespace.</exception>
    public static PluginLandingCardAction RunCommand(string label, string command, string? icon = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        return new PluginLandingCardAction { Label = label, Command = command, Icon = icon };
    }

    /// <summary>Creates an action that opens a canvas of the plugin.</summary>
    /// <param name="label">The label of the button.</param>
    /// <param name="canvas">The identifier of the canvas, as <see cref="PluginCanvasContribution.Id"/> gives it.</param>
    /// <param name="key">The key of the instance to open, or <see langword="null"/>.</param>
    /// <param name="icon">The icon of the button, or <see langword="null"/>.</param>
    /// <returns>The action.</returns>
    /// <exception cref="ArgumentException"><paramref name="label"/> or <paramref name="canvas"/> is null, empty or whitespace.</exception>
    public static PluginLandingCardAction OpenCanvas(string label, string canvas, string? key = null, string? icon = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        ArgumentException.ThrowIfNullOrWhiteSpace(canvas);
        return new PluginLandingCardAction { Label = label, Canvas = canvas, Key = key, Icon = icon };
    }

    /// <summary>Gets a message that says why an action is not valid, or <see langword="null"/> when it is.</summary>
    /// <returns>The reason, in a sentence.</returns>
    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(Label) || Label.Length > PluginLandingCardLimits.MaximumLabelLength) return $"has no label, or one longer than {PluginLandingCardLimits.MaximumLabelLength} characters";
        if (!string.IsNullOrWhiteSpace(Command) == !string.IsNullOrWhiteSpace(Canvas)) return "must name exactly one of a command and a canvas";
        if (Key is not null && string.IsNullOrWhiteSpace(Canvas)) return "has a key without a canvas";
        return null;
    }
}
