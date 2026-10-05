using CodeAlta.Catalog;
using CodeAlta.Plugins.Abstractions;
using CodeAlta.Tui.Presentation.Styling;
using XenoAtom.Terminal;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Commands;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Geometry;
using XenoAtom.Terminal.UI.Input;
using XenoAtom.Terminal.UI.Styling;
using XenoAtom.Terminal.UI.Templating;
using Command = XenoAtom.Terminal.UI.Commands.Command;

namespace CodeAlta.Tui.Views;

/// <summary>One item of a plugin selection dialog.</summary>
/// <param name="Label">The text of the item.</param>
/// <param name="Description">A short text shown after the label, or null.</param>
internal sealed record PluginRequestDialogChoice(string Label, string? Description);

/// <summary>What a plugin dialog shows, with its buttons already decided.</summary>
internal sealed record PluginRequestDialogModel
{
    /// <summary>Gets the kind of dialog.</summary>
    public required PluginDialogKind Kind { get; init; }

    /// <summary>Gets the title.</summary>
    public required string Title { get; init; }

    /// <summary>Gets the message shown above the field, the list or the content.</summary>
    public string? Message { get; init; }

    /// <summary>Gets the initial text of an input or a text editor.</summary>
    public string? Text { get; init; }

    /// <summary>Gets the items of a selection.</summary>
    public IReadOnlyList<PluginRequestDialogChoice> Choices { get; init; } = [];

    /// <summary>Gets the item selected when a selection opens.</summary>
    public int SelectedIndex { get; init; }

    /// <summary>Gets the native content of a custom dialog.</summary>
    public Visual? Content { get; init; }

    /// <summary>Gets the buttons, in order; at least one.</summary>
    public required IReadOnlyList<PluginDialogButton> Buttons { get; init; }
}

/// <summary>
/// The dialog the terminal application shows for a request of a plugin: a message, a confirmation, a line
/// of text, a text to edit, a list to choose from, or the plugin's own content. It answers once, with the
/// button that closed it and what the user entered; Escape answers as cancelled.
/// </summary>
internal sealed class PluginRequestDialog
{
    private readonly Dialog _dialog;
    private readonly PluginRequestDialogModel _model;
    private readonly Action<PluginDialogResponse> _complete;
    private readonly Func<Visual?> _getFocusTarget;
    private readonly TextBox? _input;
    private readonly TextArea? _editor;
    private readonly OptionList<PluginRequestDialogChoice>? _list;
    private readonly Visual? _initialFocus;
    private bool _completed;

    /// <summary>Creates the dialog; nothing is shown until <see cref="Show"/>.</summary>
    /// <param name="model">What to show.</param>
    /// <param name="getBounds">The area the dialog is sized for, or null for the whole application.</param>
    /// <param name="getFocusTarget">Where the focus returns when the dialog closes.</param>
    /// <param name="complete">Receives the answer, once.</param>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="ArgumentException">The model has no button.</exception>
    public PluginRequestDialog(PluginRequestDialogModel model, Func<Rectangle?> getBounds, Func<Visual?> getFocusTarget, Action<PluginDialogResponse> complete)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(getBounds);
        ArgumentNullException.ThrowIfNull(getFocusTarget);
        ArgumentNullException.ThrowIfNull(complete);
        if (model.Buttons.Count == 0) throw new ArgumentException("A plugin dialog needs at least one button.", nameof(model));
        _model = model;
        _complete = complete;
        _getFocusTarget = getFocusTarget;

        var parts = new List<Visual>();
        if (!string.IsNullOrWhiteSpace(model.Message)) parts.Add(new TextBlock(model.Message).Wrap(true));
        Visual? fill = null;
        var (minWidth, minHeight, widthFactor, heightFactor) = (48, 9, 0.30, 0.30);
        switch (model.Kind)
        {
            case PluginDialogKind.Input:
                _input = new TextBox().HorizontalAlignment(Align.Stretch);
                _input.Text = model.Text ?? string.Empty;
                _input.KeyDown((_, e) => { if (e.Key == TerminalKey.Enter) { Accept(); e.Handled = true; } });
                parts.Add(_input);
                _initialFocus = _input;
                (minWidth, widthFactor) = (56, 0.40);
                break;
            case PluginDialogKind.TextEditor:
                _editor = new TextArea(model.Text ?? string.Empty).HorizontalAlignment(Align.Stretch).VerticalAlignment(Align.Stretch);
                fill = new ScrollViewer(_editor, focusable: false).HorizontalAlignment(Align.Stretch).VerticalAlignment(Align.Stretch);
                _initialFocus = _editor;
                (minWidth, minHeight, widthFactor, heightFactor) = (72, 18, 0.60, 0.60);
                break;
            case PluginDialogKind.Selection:
                _list = new OptionList<PluginRequestDialogChoice>()
                    .ActivateOnClick(true)
                    .HorizontalAlignment(Align.Stretch)
                    .VerticalAlignment(Align.Stretch)
                    .ItemActivated((_, _) => Accept());
                _list.ItemTemplate = new DataTemplate<PluginRequestDialogChoice>(
                    static (DataTemplateValue<PluginRequestDialogChoice> value, in DataTemplateContext _) => BuildChoiceRow(value.GetValue()), null);
                foreach (var choice in model.Choices) _list.Items.Add(choice);
                _list.SelectedIndex = model.Choices.Count == 0 ? -1 : Math.Clamp(model.SelectedIndex, 0, model.Choices.Count - 1);
                _list.KeyDown((_, e) => { if (e.Key == TerminalKey.Enter) { Accept(); e.Handled = true; } });
                fill = new ScrollViewer(_list, focusable: false)
                    .HorizontalScrollEnabled(false).VerticalScrollEnabled(true)
                    .HorizontalAlignment(Align.Stretch).VerticalAlignment(Align.Stretch).MinHeight(5);
                _initialFocus = _list;
                (minWidth, minHeight, widthFactor, heightFactor) = (56, 16, 0.40, 0.50);
                break;
            case PluginDialogKind.Custom when model.Content is not null:
                fill = model.Content;
                _initialFocus = model.Content;
                (minWidth, minHeight, widthFactor, heightFactor) = (72, 18, 0.60, 0.60);
                break;
        }

        var buttons = new List<Visual>();
        foreach (var button in model.Buttons)
        {
            var control = new Button(button.Label) { Tone = button.IsDefault && !button.IsCancel ? ControlTone.Primary : ControlTone.Default };
            control.Click(() => Press(button));
            buttons.Add(control);
            if (_initialFocus is null && (button.IsDefault || buttons.Count == model.Buttons.Count)) _initialFocus = control;
        }

        var closeButton = new Button(new TextBlock($"{TerminalIcons.MdClose} {SR.T("Close")}"))
        {
            HorizontalAlignment = Align.End,
            VerticalAlignment = Align.Start,
            Tone = ControlTone.Default,
        };
        closeButton.Click(Cancel);

        var header = new VStack([.. parts]) { HorizontalAlignment = Align.Stretch, Spacing = 1 };
        var layout = new DockLayout()
            .Bottom(new HStack([.. buttons]) { HorizontalAlignment = Align.End, Spacing = 2 })
            .HorizontalAlignment(Align.Stretch)
            .VerticalAlignment(Align.Stretch);
        if (fill is null)
        {
            layout.Content(new ScrollViewer(header, focusable: false).Stretch());
        }
        else
        {
            if (parts.Count > 0) layout.Top(header);
            layout.Content(fill);
        }

        _dialog = new Dialog()
            .Title(model.Title)
            .TopRightText(closeButton)
            .IsModal(true)
            .Padding(1)
            .Content(layout);
        ResponsiveDialogSize.Apply(_dialog, getBounds(), minWidth, minHeight, widthFactor, heightFactor);
        _dialog.AddCommand(new Command
        {
            Id = "CodeAlta.PluginRequestDialog.Close",
            LabelMarkup = SR.T("Close"),
            DescriptionMarkup = SR.T("Close the dialog."),
            Gesture = new KeyGesture(TerminalKey.Escape),
            Importance = CommandImportance.Primary,
            Execute = _ => Cancel(),
        });
    }

    /// <summary>Gets a value indicating whether the dialog is shown.</summary>
    public bool IsOpen => _dialog.App is not null;

    /// <summary>Gets the dialog control.</summary>
    internal Dialog Dialog => _dialog;

    /// <summary>Shows the dialog and puts the focus where the answer is given.</summary>
    public void Show()
    {
        _dialog.Show();
        if (_initialFocus is not { } focus || _dialog.App is not { } app) return;
        app.Focus(focus);
        // The text of an input starts selected, so typing replaces it.
        _input?.Commands.FirstOrDefault(static command => string.Equals(command.Id, "TextEditor.SelectAll", StringComparison.Ordinal))?.Execute(_input);
        // A command that opens the dialog from the palette gets the focus back when the palette closes: take it again after that.
        _dialog.Dispatcher.Post(() => { if (!_completed && ReferenceEquals(_dialog.App, app)) app.Focus(focus); });
    }

    /// <summary>Closes the dialog without an answer, for a request that was withdrawn.</summary>
    public void Close()
    {
        _completed = true;
        CloseDialog();
    }

    /// <summary>Answers with the default button, as Enter does in a field or on a list item.</summary>
    internal void Accept()
        => Press(_model.Buttons.FirstOrDefault(static button => button.IsDefault && !button.IsCancel)
            ?? _model.Buttons.FirstOrDefault(static button => !button.IsCancel) ?? _model.Buttons[0]);

    /// <summary>Answers as cancelled, as Escape does.</summary>
    internal void Cancel()
        => Complete(new PluginDialogResponse { Cancelled = true });

    private void Press(PluginDialogButton button)
        => Complete(new PluginDialogResponse
        {
            ButtonName = button.Name,
            Cancelled = button.IsCancel,
            Text = button.IsCancel ? null : _input?.Text ?? _editor?.Text,
            SelectedIndex = button.IsCancel || _list is null || _list.SelectedIndex < 0 ? null : _list.SelectedIndex,
        });

    private void Complete(PluginDialogResponse response)
    {
        if (_completed) return;
        _completed = true;
        CloseDialog();
        _complete(response);
    }

    private void CloseDialog()
    {
        var app = _dialog.App;
        _dialog.Close();
        if (_getFocusTarget() is { } focusTarget) app?.Focus(focusTarget);
    }

    private static Visual BuildChoiceRow(PluginRequestDialogChoice choice)
    {
        var label = new TextBlock(choice.Label) { Wrap = false, IsSelectable = false };
        if (string.IsNullOrWhiteSpace(choice.Description)) return new OptionListItem(label, null);
        TextBlock? description = null;
        description = new TextBlock(choice.Description) { Wrap = false, IsSelectable = false }
            .Style(() => TextBlockStyle.Default with { Foreground = UiPalette.GetPromptPlaceholderColor(description!.GetTheme()) });
        return new OptionListItem(label, description);
    }
}
