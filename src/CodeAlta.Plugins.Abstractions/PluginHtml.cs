using System.Net;

namespace CodeAlta.Plugins.Abstractions;

/// <summary>
/// Helpers for the HTML fragments that plugins give to the desktop application.
/// </summary>
/// <remarks>
/// <para>
/// A fragment is inserted in the page of the application after it is sanitized. Scripts, styles, event
/// handler attributes and <c>javascript:</c> links are removed, so a fragment cannot call the application's
/// code directly. Elements act through attributes that the application handles:
/// </para>
/// <list type="bullet">
/// <item><description><c>data-alta-command="name"</c> runs a command of the same plugin when the element is activated.</description></item>
/// <item><description><c>data-alta-action="name"</c>, in a dialog, calls <see cref="PluginDialogRequest.OnAction"/> with the values of the dialog's fields.</description></item>
/// </list>
/// <para>
/// Buttons, fields, tables and links take the look of the application. The classes named by the
/// constants of this type add an intent or a layout.
/// </para>
/// </remarks>
public static class PluginHtml
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

    private static string Button(string attribute, string name, string label, bool primary)
        => $"<button type=\"button\"{(primary ? $" class=\"{PrimaryClass}\"" : string.Empty)} {attribute}=\"{Encode(name)}\">{Encode(label)}</button>";
}
