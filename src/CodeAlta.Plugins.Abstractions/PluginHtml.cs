using System.Net;
using System.Text.RegularExpressions;

namespace CodeAlta.Plugins.Abstractions;

/// <summary>
/// Helpers for the HTML fragments that plugins give to the desktop application.
/// </summary>
/// <remarks>
/// <para>
/// A fragment is inserted in the page of the application after it is sanitized. Scripts, styles, event
/// handler attributes and <c>javascript:</c> links are removed, so a fragment cannot call the application's
/// code directly. A plugin that wants script gives it next to the fragment (<see cref="PluginScript"/>, <see cref="Script"/>),
/// never inside it. Elements act through attributes that the application handles:
/// </para>
/// <list type="bullet">
/// <item><description><c>data-alta-command="name"</c> runs a command of the same plugin when the element is activated.</description></item>
/// <item><description><c>data-alta-action="name"</c>, in a dialog, calls <see cref="PluginDialogRequest.OnAction"/> with the values of the dialog's fields.</description></item>
/// </list>
/// <para>
/// Buttons, fields, tables and links take the look of the application. The classes named by the
/// constants of this type add an intent or a layout.
/// </para>
/// <para>
/// A fragment runs no script of its own. What the application itself draws is reached through Markdown:
/// <see cref="Markdown"/>, <see cref="Code"/> and <see cref="Diagram"/> write a block that the application
/// renders as it renders the messages of a session, with highlighted code and Mermaid diagrams, and
/// <see cref="Chart"/> writes a chart.
/// </para>
/// </remarks>
public static partial class PluginHtml
{
    /// <summary>The attribute that runs a command of the same plugin: <c>data-alta-command</c>.</summary>
    public const string CommandAttribute = "data-alta-command";

    /// <summary>The attribute that raises a dialog action: <c>data-alta-action</c>.</summary>
    public const string ActionAttribute = "data-alta-action";

    /// <summary>Class of a primary button.</summary>
    public const string PrimaryClass = "alta-primary";

    /// <summary>Class of content that reports success.</summary>
    public const string SuccessClass = "alta-success";

    /// <summary>Class of content that reports a warning.</summary>
    public const string WarningClass = "alta-warning";

    /// <summary>Class of content that reports an error or a destructive action.</summary>
    public const string DangerClass = "alta-danger";

    /// <summary>Class of secondary, dimmed text.</summary>
    public const string MutedClass = "alta-muted";

    /// <summary>Class of a small rounded label.</summary>
    public const string TagClass = "alta-tag";

    /// <summary>Class of a boxed note; combine it with an intent class.</summary>
    public const string CalloutClass = "alta-callout";

    /// <summary>Class of a container that lays its children out in a row.</summary>
    public const string RowClass = "alta-row";

    /// <summary>Class of a container that lays its children out in a column.</summary>
    public const string ColumnClass = "alta-column";

    /// <summary>Marks an element of a row that takes the space the others leave: a field beside a button.</summary>
    public const string GrowClass = "alta-grow";

    /// <summary>Marks a label that is shown above the field it contains, which takes the width.</summary>
    public const string FieldClass = "alta-field";

    /// <summary>Marks a block shown as a card: a bordered panel.</summary>
    public const string CardClass = "alta-card";

    /// <summary>
    /// Marks a block whose text is Markdown. The application renders it as it renders the messages of a
    /// session: headings, lists, tables, fenced code with the colors of its language, and <c>mermaid</c>
    /// fences as diagrams. The indentation that every line of the block shares is not part of the Markdown.
    /// </summary>
    public const string MarkdownClass = "alta-markdown";

    /// <summary>Marks a block that holds a chart: the application draws the chart whose option is the JSON in its <c>data-option</c> attribute.</summary>
    public const string ChartClass = "alta-chart";

    /// <summary>Encodes text so that it can be placed in a fragment as content or as an attribute value.</summary>
    /// <param name="text">The text to encode; <see langword="null"/> is encoded as an empty string.</param>
    /// <returns>The encoded text.</returns>
    public static string Encode(string? text) => WebUtility.HtmlEncode(text ?? string.Empty);

    /// <summary>Creates a button that runs a command of the same plugin.</summary>
    /// <param name="command">The command name, as in <see cref="PluginCommandContribution.Name"/>.</param>
    /// <param name="label">The button label.</param>
    /// <param name="primary">Whether the button is the primary action.</param>
    /// <returns>The button markup.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="command"/> or <paramref name="label"/> is null, empty or whitespace.</exception>
    public static string CommandButton(string command, string label, bool primary = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        return Button(CommandAttribute, command, label, primary);
    }

    /// <summary>Creates a dialog button that raises an action without closing the dialog.</summary>
    /// <param name="action">The action name given to <see cref="PluginDialogRequest.OnAction"/>.</param>
    /// <param name="label">The button label.</param>
    /// <param name="primary">Whether the button is the primary action.</param>
    /// <returns>The button markup.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="action"/> or <paramref name="label"/> is null, empty or whitespace.</exception>
    public static string ActionButton(string action, string label, bool primary = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(action);
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        return Button(ActionAttribute, action, label, primary);
    }

    /// <summary>Creates a block that the application renders as Markdown.</summary>
    /// <param name="markdown">The Markdown text; <see langword="null"/> is an empty block.</param>
    /// <returns>The block markup, with the text encoded.</returns>
    public static string Markdown(string? markdown) => $"<div class=\"{MarkdownClass}\">{Encode(markdown)}</div>";

    /// <summary>Creates a block of source code, shown with the colors of its language.</summary>
    /// <param name="code">The code; <see langword="null"/> is an empty block.</param>
    /// <param name="language">
    /// The language as Markdown names it after a fence (<c>csharp</c>, <c>json</c>, <c>diff</c>), or
    /// <see langword="null"/> for plain text. An unknown language is shown as plain text.
    /// </param>
    /// <returns>The block markup.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="language"/> is not 1 to 32 letters, digits, <c>_</c> or <c>-</c>.</exception>
    public static string Code(string? code, string? language = null)
    {
        if (language is not null && !LanguageName().IsMatch(language))
            throw new ArgumentException("A language is 1 to 32 letters, digits, '_' or '-'.", nameof(language));
        return Markdown(Fence(code ?? string.Empty, language));
    }

    /// <summary>Creates a diagram from its Mermaid text (a flowchart, a sequence diagram, a pie chart).</summary>
    /// <param name="mermaid">The text of the diagram, as after a <c>mermaid</c> fence.</param>
    /// <returns>The block markup. Text that is not a diagram is shown as it is.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="mermaid"/> is null, empty or whitespace.</exception>
    public static string Diagram(string mermaid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mermaid);
        return Markdown(Fence(mermaid, "mermaid"));
    }

    /// <summary>Creates a chart, drawn by the application with the colors of the window and of its color scheme.</summary>
    /// <param name="optionJson">
    /// The option of the chart as JSON: an ECharts option made of data only (series, axes, a legend). A function, a link, a toolbox or a
    /// formatter that is not a template of <c>{b}</c> and <c>{c}</c> is refused, and the block then shows a short message.
    /// </param>
    /// <param name="label">The text that says what the chart shows, for people who cannot see it, or <see langword="null"/>.</param>
    /// <returns>The block markup, with the option and the label encoded.</returns>
    /// <exception cref="ArgumentException"><paramref name="optionJson"/> is null, empty or whitespace.</exception>
    public static string Chart(string optionJson, string? label = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(optionJson);
        return $"<div class=\"{ChartClass}\" data-option=\"{Encode(optionJson)}\"{(string.IsNullOrWhiteSpace(label) ? string.Empty : $" data-label=\"{Encode(label)}\"")}></div>";
    }

    /// <summary>Creates the script of a fragment from the text of a JavaScript module; give it to the contribution that shows the fragment.</summary>
    /// <param name="code">The module, as <see cref="PluginScript"/> describes.</param>
    /// <returns>The script.</returns>
    /// <exception cref="ArgumentException"><paramref name="code"/> is blank or too long.</exception>
    public static PluginScript Script(string code) => PluginScript.Inline(code);

    // A fence longer than any run of backticks in the text, so that the text cannot close it.
    private static string Fence(string text, string? language)
    {
        var longest = 0;
        var run = 0;
        foreach (var character in text)
        {
            run = character == '`' ? run + 1 : 0;
            longest = Math.Max(longest, run);
        }

        var fence = new string('`', Math.Max(3, longest + 1));
        var body = text.ReplaceLineEndings("\n").TrimEnd('\n');
        return $"{fence}{language}\n{body}\n{fence}";
    }

    [GeneratedRegex(@"^[A-Za-z0-9_-]{1,32}\z")]
    private static partial Regex LanguageName();

    private static string Button(string attribute, string name, string label, bool primary)
        => $"<button type=\"button\"{(primary ? $" class=\"{PrimaryClass}\"" : string.Empty)} {attribute}=\"{Encode(name)}\">{Encode(label)}</button>";
}
