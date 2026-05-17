using CodeAlta.Catalog;
using CodeAlta.Presentation.Styling;
using CodeAlta.ViewModels;
using XenoAtom.Terminal;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Commands;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Geometry;
using XenoAtom.Terminal.UI.Input;
using XenoAtom.Terminal.UI.Styling;

namespace CodeAlta.Views;

internal sealed class NavigatorSettingsDialog
{
    private readonly NavigatorSettingsDialogViewModel _viewModel;
    private readonly INavigatorSettingsDialogService _dialogService;
    private readonly Dialog _dialog;
    private bool _isSaving;

    public NavigatorSettingsDialog(
        NavigatorSettings settings,
        INavigatorSettingsDialogService dialogService)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(dialogService);

        _viewModel = new NavigatorSettingsDialogViewModel
        {
            SortMode = settings.SortMode,
            RecentThreadsPerProject = settings.RecentThreadsPerProject,
            ThemeSchemeName = settings.ThemeSchemeName ?? string.Empty,
        };
        _dialogService = dialogService;

        var closeButton = new Button(new TextBlock($"{NerdFont.MdClose} Close"))
        {
            HorizontalAlignment = Align.End,
            VerticalAlignment = Align.Start,
            Tone = ControlTone.Default,
        };
        closeButton.Click(Close);

        var sortModeSelect = new EnumSelect<NavigatorProjectSortMode>()
            .Value(_viewModel.Bind.SortMode)
            .MinWidth(18);

        var themeOptions = CreateThemeOptions();
        var themeSelect = new Select<ThemeOption>()
            .MinWidth(24);
        foreach (var option in themeOptions)
        {
            themeSelect.Items.Add(option);
        }

        themeSelect.SelectedIndex = FindThemeOptionIndex(themeOptions, _viewModel.ThemeSchemeName);
        themeSelect.SelectionChanged(
            (_, e) =>
            {
                if ((uint)e.NewIndex >= (uint)themeOptions.Count)
                {
                    return;
                }

                _viewModel.ThemeSchemeName = themeOptions[e.NewIndex].SchemeName ?? string.Empty;
                _dialogService.PreviewNavigatorTheme(themeOptions[e.NewIndex].SchemeName);
            });

        var recentThreadsBox = new NumberBox<int>()
            .Value(_viewModel.Bind.RecentThreadsPerProject)
            .ValueValidator(static value => value is >= 1 and <= 50 ? null : "Use a value from 1 to 50.")
            .MinWidth(6)
            .MaxWidth(8);
        recentThreadsBox.ShowValidationMessage = false;
        recentThreadsBox.InvalidNumberMessage = "Enter a whole number.";
        var recentThreadsField = recentThreadsBox.Validate(
            _viewModel.Bind.RecentThreadsPerProject,
            static value => value is >= 1 and <= 50
                ? null
                : new ValidationMessage(ValidationSeverity.Error, "Use a value from 1 to 50."));

        var form = new Grid
            {
                HorizontalAlignment = Align.Stretch,
                RowGap = 1,
            }
            .Rows(
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = GridLength.Auto })
            .Columns(
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = GridLength.Star(1) });
        form.Cell(new TextBlock("Sort mode") { VerticalAlignment = Align.Center }, 0, 0);
        form.Cell(sortModeSelect, 0, 1);
        form.Cell(new TextBlock("Theme") { VerticalAlignment = Align.Center }, 1, 0);
        form.Cell(themeSelect, 1, 1);
        form.Cell(new TextBlock("Recent threads") { VerticalAlignment = Align.Center }, 2, 0);
        form.Cell(recentThreadsField, 2, 1);

        var cancelButton = new Button("Cancel")
        {
            Tone = ControlTone.Default,
        };
        cancelButton.Click(Close);

        var saveButton = new Button("Save")
        {
            Tone = ControlTone.Primary,
        };
        saveButton.Click(() => _ = SaveAsync());

        var description = new TextBlock("Configure workspace appearance and navigator behavior.")
        {
            Wrap = true,
        };
        var buttonRow = new HStack(cancelButton, saveButton)
        {
            HorizontalAlignment = Align.End,
            Spacing = 2,
        };

        var content = new DockLayout()
            .Top(description)
            .Content(form)
            .Bottom(buttonRow)
            .HorizontalAlignment(Align.Stretch)
            .VerticalAlignment(Align.Stretch);

        _dialog = new Dialog()
            .Title("Workspace Settings")
            .TopRightText(closeButton)
            .BottomRightText(new Markup("[dim]Esc Close[/]"))
            .IsModal(true)
            .Padding(1)
            .Content(content);
        ResponsiveDialogSize.Apply(_dialog, _dialogService.GetDialogBounds(), minWidth: 58, minHeight: 14, widthFactor: 0.36, heightFactor: 0.34);
        _dialog.AddCommand(new Command
        {
            Id = "CodeAlta.NavigatorSettings.Close",
            LabelMarkup = "Close",
            DescriptionMarkup = "Close the navigator settings dialog.",
            Gesture = new KeyGesture(TerminalKey.Escape),
            Importance = CommandImportance.Primary,
            Execute = _ => Close(),
        });
    }

    public void Show()
        => _dialog.Show();

    private async Task SaveAsync()
    {
        var settings = new NavigatorSettings
        {
            SortMode = _viewModel.SortMode,
            RecentThreadsPerProject = _viewModel.RecentThreadsPerProject,
            ThemeSchemeName = NormalizeThemeSchemeName(_viewModel.ThemeSchemeName),
        };

        try
        {
            settings.Validate();
        }
        catch (ArgumentOutOfRangeException)
        {
            return;
        }

        _isSaving = true;
        Close();
        try
        {
            await _dialogService.SaveNavigatorSettingsAsync(settings);
        }
        finally
        {
            _dialogService.ClearNavigatorThemePreview();
        }
    }

    private void Close()
    {
        if (!_isSaving)
        {
            _dialogService.ClearNavigatorThemePreview();
        }

        var app = _dialog.App;
        _dialog.Close();
        if (_dialogService.GetDialogFocusTarget() is { } focusTarget)
        {
            app?.Focus(focusTarget);
        }
    }

    private static List<ThemeOption> CreateThemeOptions()
    {
        var options = new List<ThemeOption>
        {
            new("Default", null),
        };

        foreach (var scheme in CodeAltaThemeResolver.GetSelectableSchemes())
        {
            options.Add(new ThemeOption(scheme.Name, scheme.Name));
        }

        return options;
    }

    private static int FindThemeOptionIndex(IReadOnlyList<ThemeOption> options, string? themeSchemeName)
    {
        var normalizedName = NormalizeThemeSchemeName(themeSchemeName);
        if (normalizedName is null)
        {
            return 0;
        }

        for (var i = 0; i < options.Count; i++)
        {
            if (string.Equals(options[i].SchemeName, normalizedName, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return 0;
    }

    private static string? NormalizeThemeSchemeName(string? themeSchemeName)
        => string.IsNullOrWhiteSpace(themeSchemeName) ? null : themeSchemeName.Trim();

    private sealed record ThemeOption(string Label, string? SchemeName)
    {
        public override string ToString() => Label;
    }
}
