using CodeAlta.Presentation.Styling;
using XenoAtom.Terminal;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Commands;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Geometry;
using XenoAtom.Terminal.UI.Input;
using XenoAtom.Terminal.UI.Styling;

namespace CodeAlta.Views;

internal sealed class DirectoryPathDialog
{
    private readonly Func<string, Task> _onSubmitAsync;
    private readonly Func<Visual?> _getFocusTarget;
    private readonly PromptEditor _editor;
    private readonly Dialog _dialog;
    private readonly State<string?> _validationText = new(null);

    public DirectoryPathDialog(
        string title,
        string description,
        string submitText,
        Func<string, Task> onSubmitAsync,
        Func<Rectangle?> getBounds,
        Func<Visual?> getFocusTarget,
        string? initialPath = null,
        string? placeholder = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        ArgumentException.ThrowIfNullOrWhiteSpace(submitText);
        ArgumentNullException.ThrowIfNull(onSubmitAsync);
        ArgumentNullException.ThrowIfNull(getBounds);
        ArgumentNullException.ThrowIfNull(getFocusTarget);

        _onSubmitAsync = onSubmitAsync;
        _getFocusTarget = getFocusTarget;

        var completionProvider = new DirectoryPathCompletionProvider();
        _editor = new PromptEditor()
            .PromptMarkup("[primary]Path[/] ")
            .Placeholder(placeholder ?? "Enter a folder path...")
            .EnterMode(PromptEditorEnterMode.EnterAccepts)
            .EscapeBehavior(PromptEditorEscapeBehavior.CancelCompletionOnly)
            .EnableWordHints(false)
            .CompletionPresentation(PromptEditorCompletionPresentation.PopupList)
            .CompletionHandler(completionProvider.Complete)
            .MinHeight(1)
            .MaxHeight(1)
            .Style(PromptEditorStyle.Default with
            {
                Padding = new Thickness(0, 0, 1, 0),
                PlaceholderForeground = UiPalette.PromptPlaceholderColor,
            });
        _editor.Text = initialPath ?? string.Empty;
        _editor.Accepted((_, _) =>
        {
            _ = SubmitAsync();
        });

        var closeButton = new Button(new TextBlock($"{NerdFont.MdClose} Close"))
        {
            HorizontalAlignment = Align.End,
            VerticalAlignment = Align.Start,
            Tone = ControlTone.Default,
        };
        closeButton.Click(Close);

        var cancelButton = new Button("Cancel")
        {
            Tone = ControlTone.Default,
        };
        cancelButton.Click(Close);

        var submitButton = new Button(submitText)
        {
            Tone = ControlTone.Primary,
        };
        submitButton.Click(() => _ = SubmitAsync());

        var validation = new ComputedVisual(
            () =>
            {
                if (string.IsNullOrWhiteSpace(_validationText.Value))
                {
                    return new Markup("[dim]Tab completes directories · Enter opens the folder · Esc closes[/]");
                }

                return new TextBlock(() => _validationText.Value ?? string.Empty)
                    .Style(TextBlockStyle.Default with { Foreground = Colors.OrangeRed })
                    .Wrap(true);
            });

        var content = new VStack(
            new TextBlock(description)
            {
                Wrap = true,
            },
            _editor,
            validation,
            new HStack(cancelButton, submitButton)
            {
                HorizontalAlignment = Align.End,
                Spacing = 2,
            })
        {
            HorizontalAlignment = Align.Stretch,
            VerticalAlignment = Align.Stretch,
            Spacing = 1,
        };

        _dialog = new Dialog()
            .Title(title)
            .TopRightText(closeButton)
            .BottomRightText(new Markup("[dim]Tab autocomplete[/]"))
            .IsModal(true)
            .Padding(1)
            .Content(content);
        ResponsiveDialogSize.Apply(_dialog, getBounds(), minWidth: 72, minHeight: 11, widthFactor: 0.5, heightFactor: 0.14);
        _dialog.AddCommand(new Command
        {
            Id = "CodeAlta.DirectoryPathDialog.Close",
            LabelMarkup = "Close",
            DescriptionMarkup = "Close the directory input dialog.",
            Gesture = new KeyGesture(TerminalKey.Escape),
            Importance = CommandImportance.Primary,
            Execute = _ => Close(),
        });
    }

    public void Show()
    {
        _dialog.Show();
        _dialog.App?.Focus(_editor);
    }

    private async Task SubmitAsync()
    {
        if (!TryNormalizeExistingDirectory(_editor.Text, out var normalizedPath, out var validationText))
        {
            _validationText.Value = validationText;
            return;
        }

        Close();
        await _onSubmitAsync(normalizedPath);
    }

    private void Close()
    {
        var app = _dialog.App;
        _dialog.Close();
        if (_getFocusTarget() is { } focusTarget)
        {
            app?.Focus(focusTarget);
        }
    }

    private static bool TryNormalizeExistingDirectory(string? value, out string normalizedPath, out string? validationText)
    {
        normalizedPath = string.Empty;
        validationText = null;

        if (string.IsNullOrWhiteSpace(value))
        {
            validationText = "Folder path is required.";
            return false;
        }

        try
        {
            normalizedPath = Path.GetFullPath(value.Trim());
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            validationText = $"Invalid folder path: {ex.Message}";
            return false;
        }

        if (!Directory.Exists(normalizedPath))
        {
            validationText = "The folder does not exist.";
            return false;
        }

        return true;
    }
}
