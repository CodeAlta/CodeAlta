using CodeAlta.Catalog;
using CodeAlta.Agent;
using XenoAtom.Ansi;
using XenoAtom.Terminal;
using XenoAtom.Terminal.UI;
using CodeAlta.Presentation.Styling;
using XenoAtom.Terminal.UI.Commands;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Geometry;
using XenoAtom.Terminal.UI.Input;
using XenoAtom.Terminal.UI.Styling;
using XenoAtom.Terminal.UI.Templating;

namespace CodeAlta.Views;

internal sealed class ModelProviderModelSelectionDialog
{
    private readonly Dialog _dialog;
    private readonly ListBox<ModelSelectionOption> _modelList;
    private readonly IReadOnlyList<ModelSelectionOption> _models;
    private readonly Action<string> _selectModel;
    private readonly Func<Visual?> _getFocusTarget;

    public ModelProviderModelSelectionDialog(
        string providerLabel,
        IReadOnlyList<AgentModelInfo> models,
        string? selectedModelId,
        Action<string> selectModel,
        Func<Rectangle?> getBounds,
        Func<Visual?> getFocusTarget)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerLabel);
        ArgumentNullException.ThrowIfNull(models);
        ArgumentNullException.ThrowIfNull(selectModel);
        ArgumentNullException.ThrowIfNull(getBounds);
        ArgumentNullException.ThrowIfNull(getFocusTarget);

        _selectModel = selectModel;
        _getFocusTarget = getFocusTarget;
        _models = models
            .OrderBy(static model => model.DisplayName ?? model.Id, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static model => model.Id, StringComparer.OrdinalIgnoreCase)
            .Select(model => new ModelSelectionOption(
                model.Id,
                string.IsNullOrWhiteSpace(model.DisplayName) ? model.Id : model.DisplayName!,
                model.Description,
                IsSelectedModel(model.Id, selectedModelId)))
            .ToArray();

        _modelList = new ListBox<ModelSelectionOption>()
            .Stretch();
        _modelList.ItemTemplate = new DataTemplate<ModelSelectionOption>(
            (DataTemplateValue<ModelSelectionOption> value, in DataTemplateContext _) => BuildModelListItem(value.GetValue()),
            null);
        _modelList.Items.AddRange(_models);
        SelectInitialModel();

        var closeButton = new Button(new TextBlock($"{TerminalIcons.MdClose} {SR.T("Close")}"))
        {
            HorizontalAlignment = Align.End,
            VerticalAlignment = Align.Start,
            Tone = ControlTone.Default,
        };
        closeButton.Click(() => Close(restoreFocus: true));

        var selectButton = new Button(SR.T("Select"))
            .Tone(ControlTone.Primary)
            .Click(SelectHighlighted);
        var cancelButton = new Button(SR.T("Cancel"))
            .Tone(ControlTone.Default)
            .Click(() => Close(restoreFocus: true));

        var content = new Grid
        {
            HorizontalAlignment = Align.Stretch,
            VerticalAlignment = Align.Stretch,
        }
            .Rows(
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = GridLength.Star(1) },
                new RowDefinition { Height = GridLength.Auto })
            .Columns(new ColumnDefinition { Width = GridLength.Star(1) });
        content.Cell(new Markup($"[dim]{SR.T("Choose a model for this provider. No model turn is sent while listing or selecting models.")}[/]") { Wrap = true }, 0, 0);
        content.Cell(
            new Border(new ScrollViewer(_modelList.Stretch()).Stretch())
                .Style(BorderStyle.Rounded)
                .Padding(new Thickness(1, 0, 1, 0))
                .HorizontalAlignment(Align.Stretch)
                .VerticalAlignment(Align.Stretch),
            1,
            0);
        content.Cell(new HStack(cancelButton, selectButton)
        {
            HorizontalAlignment = Align.End,
            Spacing = 1,
        }, 2, 0);

        _dialog = new Dialog()
            .Title(SR.T("Select Model · {0}", providerLabel))
            .TopRightText(closeButton)
            .BottomRightText(new Markup($"[dim]{SR.T("Enter Select · Esc Close")}[/]"))
            .IsModal(true)
            .Padding(1)
            .Content(content)
            .Style(DialogStyle.Rounded);
        ResponsiveDialogSize.Apply(_dialog, getBounds(), minWidth: 48, minHeight: 12, widthFactor: 0.30, heightFactor: 0.30);
        _dialog.AddCommand(new Command
        {
            Id = "CodeAlta.Providers.ModelSelector.Close",
            LabelMarkup = SR.T("Close"),
            DescriptionMarkup = SR.T("Close the provider model selector."),
            Gesture = new KeyGesture(TerminalKey.Escape),
            Importance = CommandImportance.Primary,
            Execute = _ => Close(restoreFocus: true),
        });
        _dialog.AddCommand(new Command
        {
            Id = "CodeAlta.Providers.ModelSelector.Select",
            LabelMarkup = SR.T("Select"),
            DescriptionMarkup = SR.T("Select the highlighted model for this provider."),
            Gesture = new KeyGesture(TerminalKey.Enter),
            Importance = CommandImportance.Primary,
            Execute = _ => SelectHighlighted(),
        });
    }

    public bool IsOpen => _dialog.App is not null;

    public void Show()
    {
        _dialog.Show();
        _dialog.App?.Focus(_modelList);
    }

    public void Focus()
        => _dialog.App?.Focus(_modelList);

    private void SelectHighlighted()
    {
        var index = _modelList.SelectedIndex;
        if ((uint)index >= (uint)_models.Count)
        {
            return;
        }

        var model = _models[index];
        Close(restoreFocus: false);
        _selectModel(model.Id);
    }

    private void SelectInitialModel()
    {
        if (_models.Count == 0)
        {
            _modelList.SelectedIndex = -1;
            return;
        }

        var selectedIndex = _models
            .Select((model, index) => (model, index))
            .FirstOrDefault(static item => item.model.IsSelected)
            .index;
        _modelList.SelectedIndex = selectedIndex;
    }

    private void Close(bool restoreFocus)
    {
        var app = _dialog.App;
        _dialog.Close();
        if (restoreFocus && _getFocusTarget() is { } focusTarget)
        {
            app?.Focus(focusTarget);
        }
    }

    private static Visual BuildModelListItem(ModelSelectionOption model)
        => new Markup(BuildModelListItemMarkup(model))
        {
            Wrap = false,
        };

    private static string BuildModelListItemMarkup(ModelSelectionOption model)
    {
        var currentMarker = model.IsSelected ? "[primary]●[/] " : "  ";
        var description = string.IsNullOrWhiteSpace(model.Description)
            ? string.Empty
            : $" [dim]— {AnsiMarkup.Escape(model.Description!)}[/]";
        var id = string.Equals(model.DisplayName, model.Id, StringComparison.Ordinal)
            ? string.Empty
            : $" [dim]({AnsiMarkup.Escape(model.Id)})[/]";
        return $"{currentMarker}[bold]{AnsiMarkup.Escape(model.DisplayName)}[/]{id}{description}";
    }

    private static bool IsSelectedModel(string modelId, string? selectedModelId)
        => !string.IsNullOrWhiteSpace(selectedModelId) &&
           string.Equals(modelId, selectedModelId.Trim(), StringComparison.Ordinal);

    private readonly record struct ModelSelectionOption(
        string Id,
        string DisplayName,
        string? Description,
        bool IsSelected);
}
