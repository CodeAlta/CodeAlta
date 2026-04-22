using CodeAlta.Catalog;
using XenoAtom.Terminal;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Commands;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Geometry;
using XenoAtom.Terminal.UI.Input;
using XenoAtom.Terminal.UI.Styling;

namespace CodeAlta.Views;

internal sealed class AcpAgentSettingsDialog
{
    private readonly AcpBackendDefinition _initialDefinition;
    private readonly bool _canEditAgentId;
    private readonly Func<string?, string?> _validateAgentId;
    private readonly Func<AcpBackendDefinition, Task> _onSaveAsync;
    private readonly Func<Visual?> _getFocusTarget;
    private readonly Dialog _dialog;
    private readonly TextBox? _agentIdBox;
    private readonly TextBox _displayNameBox;
    private readonly CheckBox _enabledBox;
    private readonly TextBox _commandBox;
    private readonly TextArea _argsArea;
    private readonly TextBox _workingDirectoryBox;
    private readonly TextArea _envArea;
    private readonly CheckBox _unstableBox;
    private readonly CheckBox _filesystemBox;
    private readonly CheckBox _terminalBox;
    private readonly CheckBox _elicitationBox;
    private readonly TextBlock _validationText;

    public AcpAgentSettingsDialog(
        string title,
        AcpBackendDefinition definition,
        bool canEditAgentId,
        Func<string?, string?> validateAgentId,
        Func<AcpBackendDefinition, Task> onSaveAsync,
        Func<Rectangle?> getBounds,
        Func<Visual?> getFocusTarget)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(validateAgentId);
        ArgumentNullException.ThrowIfNull(onSaveAsync);
        ArgumentNullException.ThrowIfNull(getBounds);
        ArgumentNullException.ThrowIfNull(getFocusTarget);

        _initialDefinition = CloneDefinition(definition);
        _canEditAgentId = canEditAgentId;
        _validateAgentId = validateAgentId;
        _onSaveAsync = onSaveAsync;
        _getFocusTarget = getFocusTarget;

        var closeButton = new Button(new TextBlock($"{NerdFont.MdClose} Close"))
        {
            HorizontalAlignment = Align.End,
            VerticalAlignment = Align.Start,
            Tone = ControlTone.Default,
        };
        closeButton.Click(Close);

        _agentIdBox = canEditAgentId ? new TextBox(definition.AgentId) : null;
        _displayNameBox = new TextBox(definition.DisplayName ?? string.Empty);
        _enabledBox = new CheckBox("Enabled") { IsChecked = definition.Enabled ?? AcpBackendDefinition.DefaultEnabled };
        _commandBox = new TextBox(definition.Command ?? string.Empty);
        _argsArea = new TextArea(string.Join(Environment.NewLine, definition.Arguments ?? []))
            .MinHeight(3)
            .MaxHeight(5);
        _workingDirectoryBox = new TextBox(definition.WorkingDirectory ?? string.Empty);
        _envArea = new TextArea(FormatEnvironment(definition.EnvironmentVariables))
            .MinHeight(4)
            .MaxHeight(6);
        _unstableBox = new CheckBox("Allow unstable ACP") { IsChecked = definition.UseUnstable ?? AcpBackendDefinition.DefaultUseUnstable };
        _filesystemBox = new CheckBox("Enable filesystem bridge") { IsChecked = definition.EnableFilesystem ?? AcpBackendDefinition.DefaultEnableFilesystem };
        _terminalBox = new CheckBox("Enable terminal bridge") { IsChecked = definition.EnableTerminal ?? AcpBackendDefinition.DefaultEnableTerminal };
        _elicitationBox = new CheckBox("Enable elicitation") { IsChecked = definition.EnableElicitation ?? AcpBackendDefinition.DefaultEnableElicitation };
        _validationText = new TextBlock(string.Empty)
        {
            Wrap = true,
        }.Style(TextBlockStyle.Default with { Foreground = Colors.OrangeRed });

        var form = new Grid
            {
                HorizontalAlignment = Align.Stretch,
                VerticalAlignment = Align.Stretch,
            }
            .Rows(
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = GridLength.Auto })
            .Columns(
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = GridLength.Star(1) });

        form.Cell(new TextBlock("Agent Id"), 0, 0);
        form.Cell(canEditAgentId ? _agentIdBox!.Stretch() : new TextBlock(() => definition.AgentId), 0, 1);
        form.Cell(new TextBlock("Display Name"), 1, 0);
        form.Cell(_displayNameBox.Stretch(), 1, 1);
        form.Cell(new TextBlock("Enabled"), 2, 0);
        form.Cell(_enabledBox, 2, 1);
        form.Cell(new TextBlock("Command"), 3, 0);
        form.Cell(_commandBox.Stretch(), 3, 1);
        form.Cell(new TextBlock("Arguments"), 4, 0);
        form.Cell(_argsArea.Scrollable(), 4, 1);
        form.Cell(new TextBlock("Working Directory"), 5, 0);
        form.Cell(_workingDirectoryBox.Stretch(), 5, 1);
        form.Cell(new TextBlock("Environment"), 6, 0);
        form.Cell(_envArea.Scrollable(), 6, 1);
        form.Cell(new TextBlock("ACP Flags"), 7, 0);
        form.Cell(new VStack(_unstableBox, _filesystemBox, _terminalBox, _elicitationBox) { Spacing = 1 }, 7, 1);
        form.Cell(new TextBlock("Registry Id"), 8, 0);
        form.Cell(new TextBlock(definition.RegistryId ?? "None"), 8, 1);
        form.Cell(_validationText, 9, 0, columnSpan: 2);

        var saveButton = new Button("Save")
            .Tone(ControlTone.Primary)
            .Click(() => _ = SaveAsync());
        var cancelButton = new Button("Cancel")
            .Tone(ControlTone.Default)
            .Click(Close);

        var content = new VStack(
            new TextBlock("Arguments use one line per argument. Environment overrides use one KEY=VALUE entry per line.")
            {
                Wrap = true,
            },
            form,
            new HStack(cancelButton, saveButton)
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
            .BottomRightText(new Markup("[dim]Esc Close[/]"))
            .IsModal(true)
            .Padding(1)
            .Content(content);
        ResponsiveDialogSize.Apply(_dialog, getBounds(), minWidth: 86, minHeight: 24, widthFactor: 0.82, heightFactor: 0.82);
        _dialog.AddCommand(new Command
        {
            Id = "CodeAlta.Acp.Settings.Close",
            LabelMarkup = "Close",
            DescriptionMarkup = "Close the ACP settings dialog.",
            Gesture = new KeyGesture(TerminalKey.Escape),
            Importance = CommandImportance.Primary,
            Execute = _ => Close(),
        });
    }

    public void Show()
        => _dialog.Show();

    private async Task SaveAsync()
    {
        _validationText.Text = string.Empty;
        var agentId = _canEditAgentId ? _agentIdBox?.Text : _initialDefinition.AgentId;
        var validationError = _validateAgentId(agentId);
        if (!string.IsNullOrWhiteSpace(validationError))
        {
            _validationText.Text = validationError;
            return;
        }

        if (!TryParseEnvironment(_envArea.Text, out var environmentVariables, out var environmentError))
        {
            _validationText.Text = environmentError;
            return;
        }

        var normalizedAgentId = agentId!.Trim().ToLowerInvariant();
        if (_enabledBox.IsChecked && string.IsNullOrWhiteSpace(_commandBox.Text))
        {
            _validationText.Text = "Command is required when the ACP backend is enabled.";
            return;
        }

        var definition = new AcpBackendDefinition
        {
            AgentId = normalizedAgentId,
            DisplayName = string.IsNullOrWhiteSpace(_displayNameBox.Text) ? null : _displayNameBox.Text.Trim(),
            Enabled = _enabledBox.IsChecked,
            RegistryId = _initialDefinition.RegistryId,
            Command = string.IsNullOrWhiteSpace(_commandBox.Text) ? null : _commandBox.Text.Trim(),
            Arguments = SplitLines(_argsArea.Text),
            WorkingDirectory = string.IsNullOrWhiteSpace(_workingDirectoryBox.Text) ? null : _workingDirectoryBox.Text.Trim(),
            EnvironmentVariables = environmentVariables,
            UseUnstable = _unstableBox.IsChecked,
            EnableFilesystem = _filesystemBox.IsChecked,
            EnableTerminal = _terminalBox.IsChecked,
            EnableElicitation = _elicitationBox.IsChecked,
        };

        await _onSaveAsync(definition);
        Close();
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

    private static List<string>? SplitLines(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var items = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static line => !string.IsNullOrWhiteSpace(line))
            .ToList();
        return items.Count == 0 ? null : items;
    }

    private static bool TryParseEnvironment(
        string? text,
        out Dictionary<string, string>? environmentVariables,
        out string error)
    {
        environmentVariables = null;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separatorIndex = line.IndexOf('=');
            if (separatorIndex <= 0)
            {
                error = $"Environment entries must use KEY=VALUE. Invalid entry: '{line}'.";
                return false;
            }

            var key = line[..separatorIndex].Trim();
            if (key.Length == 0)
            {
                error = $"Environment keys must not be empty. Invalid entry: '{line}'.";
                return false;
            }

            result[key] = line[(separatorIndex + 1)..];
        }

        environmentVariables = result.Count == 0 ? null : result;
        return true;
    }

    private static string FormatEnvironment(Dictionary<string, string>? environmentVariables)
    {
        if (environmentVariables is null || environmentVariables.Count == 0)
        {
            return string.Empty;
        }

        return string.Join(
            Environment.NewLine,
            environmentVariables
                .OrderBy(static entry => entry.Key, StringComparer.OrdinalIgnoreCase)
                .Select(static entry => $"{entry.Key}={entry.Value}"));
    }

    private static AcpBackendDefinition CloneDefinition(AcpBackendDefinition definition)
    {
        return new AcpBackendDefinition
        {
            AgentId = definition.AgentId,
            DisplayName = definition.DisplayName,
            Enabled = definition.Enabled,
            RegistryId = definition.RegistryId,
            Command = definition.Command,
            Arguments = definition.Arguments is null ? null : [.. definition.Arguments],
            WorkingDirectory = definition.WorkingDirectory,
            EnvironmentVariables = definition.EnvironmentVariables is null
                ? null
                : new Dictionary<string, string>(definition.EnvironmentVariables, StringComparer.OrdinalIgnoreCase),
            UseUnstable = definition.UseUnstable,
            EnableTerminal = definition.EnableTerminal,
            EnableFilesystem = definition.EnableFilesystem,
            EnableElicitation = definition.EnableElicitation,
        };
    }
}
