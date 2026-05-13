using System.Collections;
using System.Globalization;
using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Models;
using CodeAlta.ViewModels;
using XenoAtom.Terminal;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Commands;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.DataGrid;
using XenoAtom.Terminal.UI.Geometry;
using XenoAtom.Terminal.UI.Input;
using XenoAtom.Terminal.UI.Styling;
using XenoAtom.Terminal.UI.Templating;

namespace CodeAlta.Views;

internal sealed class ModelCatalogDialog
{
    private static readonly string[] ContextWindowKeys =
    [
        "contextWindow",
        "contextWindowTokens",
        "context_length",
        "contextLength",
        "tokenLimit",
    ];

    private static readonly string[] InputTokenLimitKeys =
    [
        "inputTokenLimit",
        "maxInputTokens",
    ];

    private static readonly string[] OutputTokenLimitKeys =
    [
        "outputTokenLimit",
        "maxOutputTokens",
        "maxTokens",
    ];

    private readonly IReadOnlyList<ModelCatalogRowViewModel> _rows;
    private readonly Func<ModelCatalogRowViewModel, Task> _selectModelAsync;
    private readonly Func<Visual?> _getFocusTarget;
    private readonly Dialog _dialog;
    private readonly DataGridControl _grid;

    public ModelCatalogDialog(
        IReadOnlyDictionary<string, ChatBackendState> chatBackendStates,
        string? selectedProviderKey,
        string? selectedModelId,
        Func<ModelCatalogRowViewModel, Task> selectModelAsync,
        Func<Rectangle?> getBounds,
        Func<Visual?> getFocusTarget)
    {
        ArgumentNullException.ThrowIfNull(chatBackendStates);
        ArgumentNullException.ThrowIfNull(selectModelAsync);
        ArgumentNullException.ThrowIfNull(getBounds);
        ArgumentNullException.ThrowIfNull(getFocusTarget);

        _selectModelAsync = selectModelAsync;
        _getFocusTarget = getFocusTarget;
        _rows = BuildRows(chatBackendStates, selectedProviderKey, selectedModelId);

        var document = new DataGridListDocument<ModelCatalogRowViewModel>();
        using (document.BeginUpdate())
        {
            document
                .AddColumn(new DataGridColumnInfo<string>("current", "", true, ModelCatalogRowViewModel.Accessor.CurrentMarker))
                .AddColumn(new DataGridColumnInfo<string>("provider", "Provider", true, ModelCatalogRowViewModel.Accessor.ProviderDisplayName))
                .AddColumn(new DataGridColumnInfo<string>("providerKey", "Provider Id", true, ModelCatalogRowViewModel.Accessor.ProviderKey))
                .AddColumn(new DataGridColumnInfo<string>("model", "Model", true, ModelCatalogRowViewModel.Accessor.ModelDisplayName))
                .AddColumn(new DataGridColumnInfo<string>("modelId", "Model Id", true, ModelCatalogRowViewModel.Accessor.ModelId))
                .AddColumn(new DataGridColumnInfo<long?>("context", "Context", true, ModelCatalogRowViewModel.Accessor.ContextWindowTokens))
                .AddColumn(new DataGridColumnInfo<long?>("input", "Input", true, ModelCatalogRowViewModel.Accessor.InputTokenLimit))
                .AddColumn(new DataGridColumnInfo<long?>("output", "Output", true, ModelCatalogRowViewModel.Accessor.OutputTokenLimit))
                .AddColumn(new DataGridColumnInfo<string>("reasoning", "Reason", true, ModelCatalogRowViewModel.Accessor.ReasoningText))
                .AddColumn(new DataGridColumnInfo<string>("tools", "Tools", true, ModelCatalogRowViewModel.Accessor.ToolCallText))
                .AddColumn(new DataGridColumnInfo<string>("structured", "Struct", true, ModelCatalogRowViewModel.Accessor.StructuredOutputText))
                .AddColumn(new DataGridColumnInfo<string>("images", "Images", true, ModelCatalogRowViewModel.Accessor.ImageInputText))
                .AddColumn(new DataGridColumnInfo<string>("modelsDev", "models.dev", true, ModelCatalogRowViewModel.Accessor.ModelsDevRef))
                .AddColumn(new DataGridColumnInfo<string>("status", "Status", true, ModelCatalogRowViewModel.Accessor.Status));

            foreach (var row in _rows)
            {
                document.AddRow(row);
            }
        }

        var view = new DataGridDocumentView(document);
        _grid = new DataGridControl { View = view }
            .SelectionMode(DataGridSelectionMode.Row)
            .EditMode(DataGridEditMode.OnEnter)
            .ReadOnly(true)
            .FilterRowVisible(false)
            .ShowHeader(true)
            .ShowRowAnchor(false)
            .HorizontalAlignment(Align.Stretch)
            .VerticalAlignment(Align.Stretch);
        ConfigureColumns(_grid);
        SelectInitialRow();

        var closeButton = new Button(new TextBlock($"{NerdFont.MdClose} Close"))
        {
            HorizontalAlignment = Align.End,
            VerticalAlignment = Align.Start,
            Tone = ControlTone.Default,
        };
        closeButton.Click(() => Close(restoreFocus: true));

        var filterRowVisible = new State<bool>(false);
        _grid.FilterRowVisible(filterRowVisible);

        var toolbar = new HStack(
            new CheckBox("Filter row").IsChecked(filterRowVisible),
            new Markup("[dim]Ctrl+F search · F4 filter · Enter select · Esc close[/]"),
            new TextBlock(() => $"{_rows.Count.ToString(CultureInfo.InvariantCulture)} model(s)"))
        {
            Spacing = 2,
        };

        var content = new Grid
        {
            HorizontalAlignment = Align.Stretch,
            VerticalAlignment = Align.Stretch,
        }
            .Rows(
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = GridLength.Star(1) })
            .Columns(new ColumnDefinition { Width = GridLength.Star(1) });
        content.Cell(toolbar, 0, 0);
        content.Cell(
            new Border(new ScrollViewer(_grid.Stretch()).Stretch())
                .Style(BorderStyle.Rounded)
                .Padding(new Thickness(1, 0, 1, 0))
                .HorizontalAlignment(Align.Stretch)
                .VerticalAlignment(Align.Stretch),
            1,
            0);

        _dialog = new Dialog()
            .Title("Models")
            .TopRightText(closeButton)
            .BottomRightText(new Markup("[dim]Enter Select · Esc Close[/]"))
            .IsModal(true)
            .Padding(1)
            .Content(content);
        ResponsiveDialogSize.Apply(_dialog, getBounds(), minWidth: 112, minHeight: 26, widthFactor: 0.92, heightFactor: 0.84);
        _dialog.AddCommand(new Command
        {
            Id = "CodeAlta.Models.Close",
            LabelMarkup = "Close",
            DescriptionMarkup = "Close the model catalog.",
            Gesture = new KeyGesture(TerminalKey.Escape),
            Importance = CommandImportance.Primary,
            Execute = _ => Close(restoreFocus: true),
        });
        _dialog.AddCommand(new Command
        {
            Id = "CodeAlta.Models.Select",
            LabelMarkup = "Select",
            DescriptionMarkup = "Select the highlighted provider/model for the current prompt or thread.",
            Gesture = new KeyGesture(TerminalKey.Enter),
            Importance = CommandImportance.Primary,
            Execute = _ => SelectHighlighted(),
        });
    }

    public void Show()
    {
        _dialog.Show();
        _dialog.App?.Focus(_grid);
    }

    private async Task SelectHighlightedAsync()
    {
        var row = GetSelectedRow();
        if (row is null)
        {
            return;
        }

        Close(restoreFocus: false);
        await _selectModelAsync(row);
    }

    private void SelectHighlighted()
        => _ = SelectHighlightedAsync();

    private ModelCatalogRowViewModel? GetSelectedRow()
    {
        var rowIndex = _grid.SelectedRow >= 0 ? _grid.SelectedRow : _grid.CurrentCell.Row;
        return (uint)rowIndex < (uint)_rows.Count ? _rows[rowIndex] : null;
    }

    private void SelectInitialRow()
    {
        if (_rows.Count == 0)
        {
            _grid.SelectedRow = -1;
            _grid.CurrentCell = DataGridCell.None;
            return;
        }

        var selectedIndex = _rows
            .Select((row, index) => (row, index))
            .FirstOrDefault(static item => item.row.CurrentMarker.Length > 0)
            .index;
        _grid.SelectedRow = selectedIndex;
        _grid.CurrentCell = new DataGridCell(selectedIndex, 0);
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

    private static IReadOnlyList<ModelCatalogRowViewModel> BuildRows(
        IReadOnlyDictionary<string, ChatBackendState> chatBackendStates,
        string? selectedProviderKey,
        string? selectedModelId)
    {
        return chatBackendStates.Values
            .OrderBy(static state => state.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static state => state.BackendId.Value, StringComparer.OrdinalIgnoreCase)
            .SelectMany(state => state.Models
                .OrderBy(static model => model.DisplayName ?? model.Id, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static model => model.Id, StringComparer.OrdinalIgnoreCase)
                .Select(model => CreateRow(state, model, selectedProviderKey, selectedModelId)))
            .ToArray();
    }

    private static ModelCatalogRowViewModel CreateRow(
        ChatBackendState state,
        AgentModelInfo model,
        string? selectedProviderKey,
        string? selectedModelId)
    {
        var capabilities = model.Capabilities;
        var contextWindow = TryReadInt64(capabilities, ContextWindowKeys);
        var inputLimit = TryReadInt64(capabilities, InputTokenLimitKeys);
        var outputLimit = TryReadInt64(capabilities, OutputTokenLimitKeys);
        var modelsDevProviderId = TryReadString(capabilities, "modelsDevProviderId");
        var modelsDevModelId = TryReadString(capabilities, "modelsDevModelId");
        var status = TryReadString(capabilities, "status");
        var isSelected = string.Equals(state.BackendId.Value, selectedProviderKey, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(model.Id, selectedModelId, StringComparison.OrdinalIgnoreCase);

        return new ModelCatalogRowViewModel
        {
            CurrentMarker = isSelected ? "●" : string.Empty,
            ProviderKey = state.BackendId.Value,
            ProviderDisplayName = state.DisplayName,
            ProviderStatus = state.Availability.ToString(),
            ModelId = model.Id,
            ModelDisplayName = string.IsNullOrWhiteSpace(model.DisplayName) ? model.Id : model.DisplayName!,
            ContextWindowTokens = contextWindow,
            InputTokenLimit = inputLimit,
            OutputTokenLimit = outputLimit,
            ReasoningText = FormatBoolean(ResolveReasoningSupport(model, capabilities)),
            ToolCallText = FormatBoolean(TryReadBoolean(capabilities, "supportsToolCall", "toolCall", "tool_call")),
            StructuredOutputText = FormatBoolean(TryReadBoolean(capabilities, "supportsStructuredOutput", "structuredOutput", "structured_output")),
            ImageInputText = FormatBoolean(ResolveImageInputSupport(capabilities)),
            ModelsDevRef = string.IsNullOrWhiteSpace(modelsDevProviderId) && string.IsNullOrWhiteSpace(modelsDevModelId)
                ? string.Empty
                : string.IsNullOrWhiteSpace(modelsDevProviderId)
                    ? modelsDevModelId!
                    : string.IsNullOrWhiteSpace(modelsDevModelId)
                        ? modelsDevProviderId!
                        : $"{modelsDevProviderId}:{modelsDevModelId}",
            Status = string.IsNullOrWhiteSpace(status) ? state.Availability.ToString() : status!,
        };
    }

    private static void ConfigureColumns(DataGridControl grid)
    {
        grid.Columns.Add(new DataGridColumn<string> { Key = "current", TypedValueAccessor = ModelCatalogRowViewModel.Accessor.CurrentMarker, Width = GridLength.Fixed(2), Sortable = true });
        grid.Columns.Add(new DataGridColumn<string> { Key = "provider", Header = new TextBlock("Provider"), TypedValueAccessor = ModelCatalogRowViewModel.Accessor.ProviderDisplayName, Width = GridLength.Star(1), Sortable = true });
        grid.Columns.Add(new DataGridColumn<string> { Key = "providerKey", Header = new TextBlock("Provider Id"), TypedValueAccessor = ModelCatalogRowViewModel.Accessor.ProviderKey, Width = GridLength.Auto, Sortable = true });
        grid.Columns.Add(new DataGridColumn<string> { Key = "model", Header = new TextBlock("Model"), TypedValueAccessor = ModelCatalogRowViewModel.Accessor.ModelDisplayName, Width = GridLength.Star(2), Sortable = true });
        grid.Columns.Add(new DataGridColumn<string> { Key = "modelId", Header = new TextBlock("Model Id"), TypedValueAccessor = ModelCatalogRowViewModel.Accessor.ModelId, Width = GridLength.Star(2), Sortable = true });
        grid.Columns.Add(CreateNullableLongColumn("context", "Context", ModelCatalogRowViewModel.Accessor.ContextWindowTokens));
        grid.Columns.Add(CreateNullableLongColumn("input", "Input", ModelCatalogRowViewModel.Accessor.InputTokenLimit));
        grid.Columns.Add(CreateNullableLongColumn("output", "Output", ModelCatalogRowViewModel.Accessor.OutputTokenLimit));
        grid.Columns.Add(new DataGridColumn<string> { Key = "reasoning", Header = new TextBlock("Reason"), TypedValueAccessor = ModelCatalogRowViewModel.Accessor.ReasoningText, Width = GridLength.Auto, Sortable = true });
        grid.Columns.Add(new DataGridColumn<string> { Key = "tools", Header = new TextBlock("Tools"), TypedValueAccessor = ModelCatalogRowViewModel.Accessor.ToolCallText, Width = GridLength.Auto, Sortable = true });
        grid.Columns.Add(new DataGridColumn<string> { Key = "structured", Header = new TextBlock("Struct"), TypedValueAccessor = ModelCatalogRowViewModel.Accessor.StructuredOutputText, Width = GridLength.Auto, Sortable = true });
        grid.Columns.Add(new DataGridColumn<string> { Key = "images", Header = new TextBlock("Images"), TypedValueAccessor = ModelCatalogRowViewModel.Accessor.ImageInputText, Width = GridLength.Auto, Sortable = true });
        grid.Columns.Add(new DataGridColumn<string> { Key = "modelsDev", Header = new TextBlock("models.dev"), TypedValueAccessor = ModelCatalogRowViewModel.Accessor.ModelsDevRef, Width = GridLength.Star(1), Sortable = true });
        grid.Columns.Add(new DataGridColumn<string> { Key = "status", Header = new TextBlock("Status"), TypedValueAccessor = ModelCatalogRowViewModel.Accessor.Status, Width = GridLength.Auto, Sortable = true });
    }

    private static DataGridColumn<long?> CreateNullableLongColumn(string key, string header, BindingAccessor<long?> accessor)
        => new()
        {
            Key = key,
            Header = new TextBlock(header),
            TypedValueAccessor = accessor,
            Width = GridLength.Auto,
            CellAlignment = TextAlignment.Right,
            Sortable = true,
            SortComparer = Comparer<long?>.Create(static (left, right) => Nullable.Compare(left, right)),
            CellTemplate = new DataTemplate<long?>(static (value, in _) => new TextBlock(FormatTokenCount(value.GetValue())), null),
        };

    private static string FormatTokenCount(long? value)
        => value is > 0 ? value.Value.ToString("N0", CultureInfo.InvariantCulture) : "—";

    private static string FormatBoolean(bool? value)
        => value switch
        {
            true => "Yes",
            false => "No",
            _ => "—",
        };

    private static bool? ResolveReasoningSupport(AgentModelInfo model, IReadOnlyDictionary<string, object?>? capabilities)
    {
        if (model.SupportedReasoningEfforts is { Count: > 0 } || model.DefaultReasoningEffort is not null)
        {
            return true;
        }

        return TryReadBoolean(capabilities, "supportsReasoning", "reasoning");
    }

    private static bool? ResolveImageInputSupport(IReadOnlyDictionary<string, object?>? capabilities)
    {
        if (TryReadBoolean(capabilities, "supportsImageInput", "imageInput", "supportsImages", "supportsVision", "vision") is { } explicitSupport)
        {
            return explicitSupport;
        }

        if (TryReadValue(capabilities, out var modalities, "inputModalities", "input_modalities"))
        {
            return EnumerateStrings(modalities).Any(static modality =>
                string.Equals(modality, "image", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(modality, "vision", StringComparison.OrdinalIgnoreCase));
        }

        return null;
    }

    private static long? TryReadInt64(IReadOnlyDictionary<string, object?>? capabilities, params string[] keys)
    {
        if (!TryReadValue(capabilities, out var value, keys))
        {
            return null;
        }

        return TryConvertToInt64(value, out var converted) ? converted : null;
    }

    private static bool? TryReadBoolean(IReadOnlyDictionary<string, object?>? capabilities, params string[] keys)
    {
        if (!TryReadValue(capabilities, out var value, keys))
        {
            return null;
        }

        return TryConvertToBoolean(value, out var converted) ? converted : null;
    }

    private static string? TryReadString(IReadOnlyDictionary<string, object?>? capabilities, params string[] keys)
    {
        if (!TryReadValue(capabilities, out var value, keys))
        {
            return null;
        }

        return value switch
        {
            null => null,
            string text => string.IsNullOrWhiteSpace(text) ? null : text.Trim(),
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture),
        };
    }

    private static bool TryReadValue(IReadOnlyDictionary<string, object?>? capabilities, out object? value, params string[] keys)
    {
        if (capabilities is not { Count: > 0 })
        {
            value = null;
            return false;
        }

        foreach (var key in keys)
        {
            if (capabilities.TryGetValue(key, out value))
            {
                return true;
            }

            foreach (var entry in capabilities)
            {
                if (string.Equals(entry.Key, key, StringComparison.OrdinalIgnoreCase))
                {
                    value = entry.Value;
                    return true;
                }
            }
        }

        value = null;
        return false;
    }

    private static bool TryConvertToInt64(object? value, out long converted)
    {
        switch (value)
        {
            case byte byteValue:
                converted = byteValue;
                return true;
            case sbyte sbyteValue:
                converted = sbyteValue;
                return true;
            case short shortValue:
                converted = shortValue;
                return true;
            case ushort ushortValue:
                converted = ushortValue;
                return true;
            case int intValue:
                converted = intValue;
                return true;
            case uint uintValue:
                converted = uintValue;
                return true;
            case long longValue:
                converted = longValue;
                return true;
            case ulong ulongValue when ulongValue <= long.MaxValue:
                converted = (long)ulongValue;
                return true;
            case float floatValue when floatValue is >= long.MinValue and <= long.MaxValue:
                converted = (long)floatValue;
                return true;
            case double doubleValue when doubleValue is >= long.MinValue and <= long.MaxValue:
                converted = (long)doubleValue;
                return true;
            case decimal decimalValue when decimalValue is >= long.MinValue and <= long.MaxValue:
                converted = (long)decimalValue;
                return true;
            case string text when long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed):
                converted = parsed;
                return true;
            case JsonElement element when element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out var parsed):
                converted = parsed;
                return true;
            default:
                converted = 0;
                return false;
        }
    }

    private static bool TryConvertToBoolean(object? value, out bool converted)
    {
        switch (value)
        {
            case bool boolean:
                converted = boolean;
                return true;
            case string text when bool.TryParse(text, out var parsed):
                converted = parsed;
                return true;
            case JsonElement { ValueKind: JsonValueKind.True }:
                converted = true;
                return true;
            case JsonElement { ValueKind: JsonValueKind.False }:
                converted = false;
                return true;
            default:
                converted = false;
                return false;
        }
    }

    private static IEnumerable<string> EnumerateStrings(object? value)
    {
        switch (value)
        {
            case null:
                yield break;
            case string stringValue:
                yield return stringValue;
                yield break;
            case JsonElement element when element.ValueKind == JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String && item.GetString() is { } arrayText)
                    {
                        yield return arrayText;
                    }
                }

                yield break;
            case JsonElement element when element.ValueKind == JsonValueKind.String:
                if (element.GetString() is { } elementText)
                {
                    yield return elementText;
                }

                yield break;
            case IEnumerable enumerable:
                foreach (var item in enumerable)
                {
                    if (item is string enumerableText)
                    {
                        yield return enumerableText;
                    }
                    else if (item is JsonElement { ValueKind: JsonValueKind.String } itemElement && itemElement.GetString() is { } enumerableElementText)
                    {
                        yield return enumerableElementText;
                    }
                }

                yield break;
        }
    }
}
