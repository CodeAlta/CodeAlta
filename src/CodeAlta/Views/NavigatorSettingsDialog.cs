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
    private const string EnglishLanguageCode = "en";
    private const string ChineseLanguageCode = "zh-CN";

    private readonly NavigatorSettingsDialogViewModel _viewModel;
    private readonly INavigatorSettingsDialogService _dialogService;
    private readonly string? _initialLanguageName;
    private readonly Dialog _dialog;
    private bool _isSaving;

    public NavigatorSettingsDialog(
        NavigatorSettings settings,
        INavigatorSettingsDialogService dialogService)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(dialogService);

        _initialLanguageName = NormalizeLanguageName(settings.LanguageName);
        _viewModel = new NavigatorSettingsDialogViewModel
        {
            SortMode = settings.SortMode,
            RecentSessionsPerProject = settings.RecentSessionsPerProject,
            ThemeSchemeName = settings.ThemeSchemeName ?? string.Empty,
            LanguageName = _initialLanguageName ?? string.Empty,
            AutoApprove = settings.AutoApprove,
        };
        _dialogService = dialogService;

        var closeButton = new Button(new TextBlock($"{TerminalIcons.MdClose} {SR.T("Close")}"))
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

        var recentSessionsBox = new NumberBox<int>()
            .Value(_viewModel.Bind.RecentSessionsPerProject)
            .ValueValidator(static value => value is >= 1 and <= 50 ? null : SR.T("Use a value from 1 to 50."))
            .MinWidth(6)
            .MaxWidth(8);
        recentSessionsBox.ShowValidationMessage = false;
        recentSessionsBox.InvalidNumberMessage = SR.T("Enter a whole number.");
        var recentSessionsField = recentSessionsBox.Validate(
            _viewModel.Bind.RecentSessionsPerProject,
            static value => value is >= 1 and <= 50
                ? null
                : new ValidationMessage(ValidationSeverity.Error, SR.T("Use a value from 1 to 50.")));

        // Language options
        var languageOptions = CreateLanguageOptions();
        var languageSelect = new Select<LanguageOption>()
            .MinWidth(24);
        foreach (var option in languageOptions)
        {
            languageSelect.Items.Add(option);
        }

        languageSelect.SelectedIndex = FindLanguageOptionIndex(languageOptions, _viewModel.LanguageName);
        languageSelect.SelectionChanged(
            (_, e) =>
            {
                if ((uint)e.NewIndex >= (uint)languageOptions.Count)
                {
                    return;
                }

                var code = NormalizeLanguageName(languageOptions[e.NewIndex].LanguageCode);
                _viewModel.LanguageName = code ?? string.Empty;
                ApplyLanguage(code);
            });

        var form = new Grid
            {
                HorizontalAlignment = Align.Stretch,
                RowGap = 1,
            }
            .Rows(
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = GridLength.Auto })
            .Columns(
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = GridLength.Star(1) });
        form.Cell(new TextBlock(SR.T("Sort mode")) { VerticalAlignment = Align.Center }, 0, 0);
        form.Cell(sortModeSelect, 0, 1);
        form.Cell(new TextBlock(SR.T("Theme")) { VerticalAlignment = Align.Center }, 1, 0);
        form.Cell(themeSelect, 1, 1);
        form.Cell(new TextBlock(SR.T("Recent sessions")) { VerticalAlignment = Align.Center }, 2, 0);
        form.Cell(recentSessionsField, 2, 1);
        form.Cell(new TextBlock(SR.T("Language")) { VerticalAlignment = Align.Center }, 3, 0);
        form.Cell(languageSelect, 3, 1);

        var autoApproveCheckbox = new CheckBox(SR.T("Auto approve commands"))
            .IsChecked(_viewModel.Bind.AutoApprove);

        form.Cell(autoApproveCheckbox, 4, 0);

        var cancelButton = new Button(SR.T("Cancel"))
        {
            Tone = ControlTone.Default,
        };
        cancelButton.Click(Close);

        var saveButton = new Button(SR.T("Save"))
        {
            Tone = ControlTone.Primary,
        };
        saveButton.Click(() => _ = SaveAsync());

        var description = new TextBlock(SR.T("Configure workspace appearance and navigator behavior."))
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
            .Title(SR.T("Workspace Settings"))
            .TopRightText(closeButton)
            .BottomRightText(new Markup($"[dim]{SR.T("Esc")} {SR.T("Close")}[/]"))
            .IsModal(true)
            .Padding(1)
            .Content(content);
        ResponsiveDialogSize.Apply(_dialog, _dialogService.GetDialogBounds(), minWidth: 58, minHeight: 14, widthFactor: 0.36, heightFactor: 0.34);
        _dialog.AddCommand(new Command
        {
            Id = "CodeAlta.NavigatorSettings.Close",
            LabelMarkup = SR.T("Close"),
            DescriptionMarkup = SR.T("Close the navigator settings dialog."),
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
            RecentSessionsPerProject = _viewModel.RecentSessionsPerProject,
            ThemeSchemeName = NormalizeThemeSchemeName(_viewModel.ThemeSchemeName),
            LanguageName = NormalizeLanguageName(_viewModel.LanguageName),
            AutoApprove = _viewModel.AutoApprove,
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
            ApplyLanguage(_initialLanguageName);
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
            new(SR.T("Default"), null),
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

    private static string? NormalizeLanguageName(string? languageName)
    {
        if (string.IsNullOrWhiteSpace(languageName))
        {
            return null;
        }

        var trimmed = languageName.Trim();
        if (string.Equals(trimmed, "auto", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (trimmed.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
        {
            return ChineseLanguageCode;
        }

        if (trimmed.StartsWith("en", StringComparison.OrdinalIgnoreCase))
        {
            return EnglishLanguageCode;
        }

        return trimmed;
    }

    private static void ApplyLanguage(string? languageName)
    {
        if (NormalizeLanguageName(languageName) is { } normalized)
        {
            SR.Language = normalized;
            return;
        }

        SR.AutoDetect();
    }

    private static List<LanguageOption> CreateLanguageOptions()
    {
        return
        [
            new LanguageOption(SR.T("Auto"), null),
            new LanguageOption("English", EnglishLanguageCode),
            new LanguageOption("中文 (简体)", ChineseLanguageCode),
        ];
    }

    private static int FindLanguageOptionIndex(IReadOnlyList<LanguageOption> options, string? languageName)
    {
        var normalizedName = NormalizeLanguageName(languageName);
        for (var i = 0; i < options.Count; i++)
        {
            if (string.Equals(options[i].LanguageCode, normalizedName, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return 0;
    }

    private sealed record ThemeOption(string Label, string? SchemeName)
    {
        public override string ToString() => Label;
    }

    private sealed record LanguageOption(string Label, string? LanguageCode)
    {
        public override string ToString() => Label;
    }
}
