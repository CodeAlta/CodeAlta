using CodeAlta.App;
using CodeAlta.Presentation.Prompting;
using CodeAlta.Presentation.Sidebar;
using CodeAlta.Presentation.Styling;
using XenoAtom.Terminal;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Commands;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Geometry;
using XenoAtom.Terminal.UI.Input;
using XenoAtom.Terminal.UI.Styling;
using XenoAtom.Terminal.UI.Templating;
using XenoAtom.Terminal.UI.Text;

namespace CodeAlta.Views;

internal sealed class DirectoryPathDialog
{
    private const int ProjectPathMaxLength = 72;
    private readonly IDirectoryPathDialogService _dialogService;
    private readonly State<ValidationMessage?> _validationMessage = new(null);
    private readonly DirectoryPathCompletionProvider _suggestionProvider;
    private readonly TextBox _editor;
    private readonly OptionList<OpenProjectSuggestion> _suggestions;
    private readonly CheckBox _includeHiddenCheckBox;
    private readonly Dialog _dialog;
    private readonly List<OpenProjectSuggestion> _items = [];
    private bool _suppressTextChanged;

    public DirectoryPathDialog(
        string title,
        string description,
        string submitText,
        IDirectoryPathDialogService dialogService,
        string? initialPath = null,
        string? placeholder = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        ArgumentException.ThrowIfNullOrWhiteSpace(submitText);
        ArgumentNullException.ThrowIfNull(dialogService);

        _dialogService = dialogService;
        _includeHiddenCheckBox = new CheckBox("Include hidden", false);
        _includeHiddenCheckBox.KeyDown((_, e) => RefreshSuggestionsAfterToggle(e));
        _includeHiddenCheckBox.PointerPressed((_, e) => RefreshSuggestionsAfterPointerToggle(e));
        _suggestionProvider = new DirectoryPathCompletionProvider(
            includeHidden: () => _includeHiddenCheckBox.IsChecked,
            projects: () => _dialogService.GetProjects() ?? []);

        var placeholderText = placeholder ?? "Project name from the sidebar or C:\\code\\SomeFolder";
        TextBox? editor = null;
        editor = new TextBox()
            .Placeholder(placeholderText)
            .HorizontalAlignment(Align.Stretch)
            .Style(() => TextBoxStyle.Default with
            {
                Placeholder = UiPalette.GetPromptPlaceholderColor(editor!.GetTheme()),
            });
        _editor = editor;
        _editor.TextDocument.Changed += OnEditorTextChanged;
        _editor.KeyDown((_, e) => HandleEditorKeyDown(e));
        if (!string.IsNullOrEmpty(initialPath))
        {
            _editor.Text = initialPath;
        }

        _suggestions = new OptionList<OpenProjectSuggestion>()
            .ActivateOnClick(true)
            .HorizontalAlignment(Align.Stretch)
            .VerticalAlignment(Align.Stretch)
            .ItemActivated((_, _) => ApplySelectedSuggestion());
        _suggestions.ItemTemplate = new DataTemplate<OpenProjectSuggestion>(
            static (DataTemplateValue<OpenProjectSuggestion> value, in DataTemplateContext _)
                => BuildSuggestionRow(value.GetValue()),
            null);
        _suggestions.KeyDown((_, e) => HandleSuggestionKeyDown(e));

        var resultsHost = new ScrollViewer(_suggestions, focusable: false)
            .HorizontalScrollEnabled(false)
            .VerticalScrollEnabled(true)
            .HorizontalAlignment(Align.Stretch)
            .VerticalAlignment(Align.Stretch)
            .MinHeight(5);

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
        submitButton.Click(() => _ = SubmitAsync(selectedSuggestionPreferred: true));

        var validatedEditor = _editor
            .Validation(_validationMessage)
            .HorizontalAlignment(Align.Stretch);

        var descriptionBlock = new TextBlock(description)
        {
            Wrap = true,
        };
        var buttonRow = new HStack(cancelButton, submitButton)
        {
            HorizontalAlignment = Align.End,
            Spacing = 2,
        };

        var content = new Grid()
            .Rows(
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = GridLength.Star(1) },
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = GridLength.Auto })
            .Columns(new ColumnDefinition { Width = GridLength.Star(1) })
            .Cell(descriptionBlock, 0, 0)
            .Cell(validatedEditor, 1, 0)
            .Cell(resultsHost, 2, 0)
            .Cell(_includeHiddenCheckBox, 3, 0)
            .Cell(buttonRow, 4, 0);
        content.HorizontalAlignment = Align.Stretch;
        content.VerticalAlignment = Align.Stretch;
        content.RowGap = 1;

        _dialog = new Dialog()
            .Title(title)
            .TopRightText(closeButton)
            .BottomRightText(new Markup("[dim]Arrows select · Enter open · Tab complete · Ctrl+I hidden[/]"))
            .IsModal(true)
            .Padding(1)
            .Content(content)
            .Style(DialogStyle.Rounded);
        ResponsiveDialogSize.Apply(_dialog, _dialogService.GetDialogBounds(), minWidth: 72, minHeight: 18, widthFactor: 0.56, heightFactor: 0.50);
        _dialog.AddCommand(new Command
        {
            Id = "CodeAlta.DirectoryPathDialog.Close",
            LabelMarkup = "Close",
            DescriptionMarkup = "Close the directory input dialog.",
            Gesture = new KeyGesture(TerminalKey.Escape),
            Importance = CommandImportance.Primary,
            Execute = _ => Close(),
        });
        _dialog.AddCommand(new Command
        {
            Id = "CodeAlta.DirectoryPathDialog.ToggleIncludeHidden",
            LabelMarkup = "Toggle Include Hidden",
            DescriptionMarkup = "Toggle archived projects in the open-project picker.",
            Gesture = new KeyGesture(TerminalChar.CtrlI, TerminalModifiers.Ctrl),
            Importance = CommandImportance.Secondary,
            Execute = _ => ToggleIncludeHidden(),
        });

        _validationMessage.Value = ValidateInput(_editor.Text, requireExactProjectMatch: false);
        RefreshSuggestions();
    }

    public void Show()
    {
        _dialog.Show();
        _dialog.App?.Focus(_editor);
    }

    private void OnEditorTextChanged(object? sender, TextDocumentChangedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (_suppressTextChanged)
        {
            return;
        }

        _validationMessage.Value = ValidateInput(_editor.Text, requireExactProjectMatch: false);
        RefreshSuggestions();
    }

    private void HandleEditorKeyDown(KeyEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        var handled = e.Key switch
        {
            TerminalKey.Up => TryMoveSelection(-1),
            TerminalKey.Down => TryMoveSelection(1),
            TerminalKey.PageUp => TryMoveSelection(-8),
            TerminalKey.PageDown => TryMoveSelection(8),
            TerminalKey.Home => TryMoveSelectionToBoundary(first: true),
            TerminalKey.End => TryMoveSelectionToBoundary(first: false),
            TerminalKey.Tab => ApplySelectedSuggestion(),
            TerminalKey.Enter => RaiseSubmit(selectedSuggestionPreferred: true),
            _ => false,
        };

        if (handled)
        {
            e.Handled = true;
        }
    }

    private void HandleSuggestionKeyDown(KeyEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        var handled = e.Key switch
        {
            TerminalKey.Tab => ApplySelectedSuggestion(),
            TerminalKey.Enter => RaiseSubmit(selectedSuggestionPreferred: true),
            TerminalKey.Escape => RaiseClose(),
            _ => false,
        };

        if (handled)
        {
            e.Handled = true;
        }
    }

    private void RefreshSuggestionsAfterToggle(KeyEventArgs e)
    {
        if (e.Key is not (TerminalKey.Space or TerminalKey.Enter))
        {
            return;
        }

        _dialog.Dispatcher.Post(() =>
        {
            _validationMessage.Value = ValidateInput(_editor.Text, requireExactProjectMatch: false);
            RefreshSuggestions();
        });
    }

    private void RefreshSuggestionsAfterPointerToggle(PointerEventArgs e)
    {
        if (e.Button != TerminalMouseButton.Left)
        {
            return;
        }

        _dialog.Dispatcher.Post(() =>
        {
            _validationMessage.Value = ValidateInput(_editor.Text, requireExactProjectMatch: false);
            RefreshSuggestions();
        });
    }

    private void RefreshSuggestions()
    {
        _items.Clear();
        _items.AddRange(_suggestionProvider.GetSuggestions(_editor.Text));

        _suggestions.Items.Clear();
        foreach (var item in _items)
        {
            _suggestions.Items.Add(item);
        }

        _suggestions.SelectedIndex = _items.Count == 0 ? -1 : 0;
    }

    private bool TryMoveSelection(int delta)
    {
        if (_items.Count == 0)
        {
            return false;
        }

        _suggestions.SelectedIndex = Math.Clamp(Math.Max(_suggestions.SelectedIndex, 0) + delta, 0, _items.Count - 1);
        return true;
    }

    private bool TryMoveSelectionToBoundary(bool first)
    {
        if (_items.Count == 0)
        {
            return false;
        }

        _suggestions.SelectedIndex = first ? 0 : _items.Count - 1;
        return true;
    }

    private bool ApplySelectedSuggestion()
    {
        if (_suggestions.SelectedIndex < 0 || _suggestions.SelectedIndex >= _items.Count)
        {
            return false;
        }

        SetEditorText(_items[_suggestions.SelectedIndex].ReplaceText);
        _dialog.App?.Focus(_editor);
        return true;
    }

    private bool RaiseSubmit(bool selectedSuggestionPreferred = false)
    {
        _ = SubmitAsync(selectedSuggestionPreferred);
        return true;
    }

    private bool RaiseClose()
    {
        Close();
        return true;
    }

    private void SetEditorText(string value)
    {
        _suppressTextChanged = true;
        try
        {
            _editor.Text = string.IsNullOrEmpty(value) ? null : value;
            _editor.CaretIndex = _editor.TextDocument.CurrentSnapshot.Length;
        }
        finally
        {
            _suppressTextChanged = false;
        }

        _validationMessage.Value = ValidateInput(_editor.Text, requireExactProjectMatch: false);
        RefreshSuggestions();
    }

    private async Task SubmitAsync(bool selectedSuggestionPreferred = false)
    {
        var input = ResolveSubmissionText(selectedSuggestionPreferred);
        var validationMessage = ValidateInput(input, requireExactProjectMatch: true);
        if (validationMessage is not null)
        {
            _validationMessage.Value = validationMessage;
            return;
        }

        try
        {
            await _dialogService.OpenFolderAsync(input.Trim(), _includeHiddenCheckBox.IsChecked);
            Close(_dialogService.GetSubmitFocusTarget);
        }
        catch (Exception ex)
        {
            _validationMessage.Value = new ValidationMessage(ValidationSeverity.Error, ex.Message);
        }
    }

    private string ResolveSubmissionText(bool selectedSuggestionPreferred)
    {
        if (selectedSuggestionPreferred &&
            _suggestions.SelectedIndex >= 0 &&
            _suggestions.SelectedIndex < _items.Count)
        {
            return _items[_suggestions.SelectedIndex].ReplaceText;
        }

        return _editor.Text ?? string.Empty;
    }

    private void Close()
        => Close(_dialogService.GetDialogFocusTarget);

    private void Close(Func<Visual?> getFocusTarget)
    {
        var app = _dialog.App;
        _dialog.Close();
        if (getFocusTarget() is { } focusTarget)
        {
            app?.Focus(focusTarget);
        }
    }

    private void ToggleIncludeHidden()
    {
        _includeHiddenCheckBox.IsChecked = !_includeHiddenCheckBox.IsChecked;
        _validationMessage.Value = ValidateInput(_editor.Text, requireExactProjectMatch: false);
        RefreshSuggestions();
    }

    private ValidationMessage? ValidateInput(string? text, bool requireExactProjectMatch)
    {
        if (text is null || text.Length == 0)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return new ValidationMessage(ValidationSeverity.Error, "A project name or rooted path is required.");
        }

        var trimmed = text.Trim();
        if (LooksLikePathAttempt(trimmed))
        {
            if (!OpenProjectRequestResolver.LooksLikePath(trimmed))
            {
                return new ValidationMessage(ValidationSeverity.Error, "Folder paths must be rooted, for example C:\\code\\CodeAlta or ~/repo.");
            }

            string normalizedPath;
            try
            {
                normalizedPath = OpenProjectRequestResolver.NormalizePath(trimmed);
            }
            catch (Exception ex)
            {
                return new ValidationMessage(ValidationSeverity.Error, ex.Message);
            }

            if (Directory.Exists(normalizedPath) ||
                _suggestionProvider.GetSuggestions(trimmed).Any(static suggestion => suggestion.Kind == OpenProjectSuggestionKind.Directory))
            {
                return null;
            }

            return new ValidationMessage(ValidationSeverity.Error, $"Folder '{normalizedPath}' was not found.");
        }

        var projects = _dialogService.GetProjects();
        if (projects is null)
        {
            return null;
        }

        var availableProjects = projects
            .Where(project => _includeHiddenCheckBox.IsChecked || !project.Archived)
            .ToArray();
        var exactMatches = availableProjects
            .Where(project => string.Equals(project.DisplayName, trimmed, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (exactMatches.Length > 0)
        {
            try
            {
                _ = OpenProjectRequestResolver.ResolveProjectReference(availableProjects, trimmed);
                return null;
            }
            catch (Exception ex)
            {
                return new ValidationMessage(ValidationSeverity.Error, ex.Message);
            }
        }

        var hasProjectPrefixMatch = availableProjects.Any(project =>
            project.DisplayName.StartsWith(trimmed, StringComparison.OrdinalIgnoreCase));
        if (hasProjectPrefixMatch)
        {
            return requireExactProjectMatch
                ? new ValidationMessage(ValidationSeverity.Error, "Choose a suggestion or enter the full project name.")
                : null;
        }

        return new ValidationMessage(
            ValidationSeverity.Error,
            $"Project '{trimmed}' was not found. Enter a rooted path or use an existing project name from the sidebar.");
    }

    private static bool LooksLikePathAttempt(string text)
    {
        if (text.StartsWith("~", StringComparison.Ordinal) ||
            text.StartsWith(".", StringComparison.Ordinal) ||
            text.Contains(Path.DirectorySeparatorChar) ||
            text.Contains(Path.AltDirectorySeparatorChar))
        {
            return true;
        }

        return OperatingSystem.IsWindows() &&
               text.Length >= 2 &&
               char.IsLetter(text[0]) &&
               text[1] == ':';
    }

    private static Visual BuildSuggestionRow(OpenProjectSuggestion suggestion)
    {
        var icon = suggestion.Kind switch
        {
            OpenProjectSuggestionKind.Project => CreateThemedSuggestionIcon(NerdFont.MdFolderOutline.ToString(), SidebarAccent.Projects),
            _ => CreateThemedSuggestionIcon(ProjectFileAppearanceRegistry.Default.GetDirectoryAppearance().Icon, SidebarAccent.Fallback),
        };

        var label = new HStack(
        [
            icon,
            new TextBlock(suggestion.PrimaryText)
            {
                Wrap = false,
                IsSelectable = false,
            },
        ])
        {
            Spacing = 1,
        };

        Visual? shortcut = null;
        if (!string.IsNullOrWhiteSpace(suggestion.SecondaryText))
        {
            TextBlock? shortcutText = null;
            shortcutText = new TextBlock(ShortenProjectPath(suggestion.SecondaryText))
            {
                Wrap = false,
                IsSelectable = false,
            }.Style(() => TextBlockStyle.Default with
            {
                Foreground = UiPalette.GetPromptPlaceholderColor(shortcutText!.GetTheme()),
            });
            shortcut = shortcutText;
        }

        return new OptionListItem(label, shortcut);
    }

    private static TextBlock CreateThemedSuggestionIcon(string text, SidebarAccent accent)
    {
        TextBlock? icon = null;
        icon = new TextBlock(text)
                .Style(() => TextBlockStyle.Default with
                {
                    Foreground = UiPalette.GetSidebarAccentColor(accent),
                });
        return icon;
    }

    private static string ShortenProjectPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (path.Length <= ProjectPathMaxLength)
        {
            return path;
        }

        return "..." + path[^Math.Max(0, ProjectPathMaxLength - 3)..];
    }
}
