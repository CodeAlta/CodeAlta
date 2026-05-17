using CodeAlta.Presentation.Chat;
using CodeAlta.Presentation.Prompting;
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

internal static partial class QueuedPromptListView
{
    public static Visual Build(
        IReadOnlyList<PromptStripItem> promptStripItems,
        Action<string> copyQueuedPromptMarkdown,
        Action<string> convertQueuedPromptToSteer,
        Action<string> deletePendingSteer,
        Action<string> deleteQueuedPrompt,
        Action<string, int> updateQueuedPromptCount,
        Action<string, string> updateQueuedPromptText,
        Func<Action<string>, string?, ChatPromptEditor> createPromptEditor)
    {
        ArgumentNullException.ThrowIfNull(promptStripItems);
        ArgumentNullException.ThrowIfNull(copyQueuedPromptMarkdown);
        ArgumentNullException.ThrowIfNull(convertQueuedPromptToSteer);
        ArgumentNullException.ThrowIfNull(deletePendingSteer);
        ArgumentNullException.ThrowIfNull(deleteQueuedPrompt);
        ArgumentNullException.ThrowIfNull(updateQueuedPromptCount);
        ArgumentNullException.ThrowIfNull(updateQueuedPromptText);
        ArgumentNullException.ThrowIfNull(createPromptEditor);

        if (promptStripItems.Count == 0)
        {
            return new Placeholder { IsVisible = false };
        }

        var rows = new List<Visual>(promptStripItems.Count * 2);
        for (var index = 0; index < promptStripItems.Count; index++)
        {
            var item = promptStripItems[index];
            rows.Add(item.Kind switch
            {
                PromptStripItemKind.PendingSteer => BuildPendingSteerRow(item, copyQueuedPromptMarkdown, deletePendingSteer),
                PromptStripItemKind.QueuedPrompt => BuildQueuedPromptRow(
                    item,
                    copyQueuedPromptMarkdown,
                    convertQueuedPromptToSteer,
                    deleteQueuedPrompt,
                    updateQueuedPromptCount,
                    updateQueuedPromptText,
                    createPromptEditor),
                _ => new Placeholder { IsVisible = false },
            });

            if (index < promptStripItems.Count - 1)
            {
                rows.Add(CreateSeparator());
            }
        }

        return new VStack(rows.ToArray())
        {
            Spacing = 0,
            HorizontalAlignment = Align.Stretch,
        };
    }

    private static Visual BuildPendingSteerRow(
        PromptStripItem pendingSteer,
        Action<string> copyQueuedPromptMarkdown,
        Action<string> deletePendingSteer)
    {
        var icon = new TextBlock($"{NerdFont.MdArrowRightThinCircleOutline}")
        {
            Wrap = false,
            IsSelectable = false,
            Margin = new Thickness(0, 0, 1, 0),
        };

        var promptText = new TextBlock(pendingSteer.PreviewText)
        {
            HorizontalAlignment = Align.Stretch,
            IsSelectable = false,
            Wrap = false,
            Trimming = TextTrimming.EndEllipsis,
            Margin = new Thickness(0, 0, 1, 0),
        };

        var status = new TextBlock("Steer pending")
        {
            Wrap = false,
            IsSelectable = false,
        };

        var copyButton = CreateIconButton(
            $"{NerdFont.MdContentCopy}",
            "Copy pending steering prompt markdown to the clipboard",
            () => copyQueuedPromptMarkdown(pendingSteer.Text));
        copyButton.Margin = new Thickness(0, 0, 1, 0);

        var deleteButton = CreateIconButton(
            $"{NerdFont.MdTrashCanOutline}",
            "Delete pending steering prompt",
            () => deletePendingSteer(pendingSteer.Id));

        var row = new Grid
            {
                HorizontalAlignment = Align.Stretch,
            }
            .Rows(new RowDefinition { Height = GridLength.Auto })
            .Columns(
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = GridLength.Star(1) },
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = GridLength.Auto });
        row.Cell(icon, 0, 0);
        row.Cell(promptText, 0, 1);
        row.Cell(copyButton, 0, 2);
        row.Cell(deleteButton, 0, 3);
        row.Cell(status, 0, 4);
        return BuildStripRow(row, UiPalette.GetPendingSteerBackgroundColor);
    }

    private static Visual BuildQueuedPromptRow(
        PromptStripItem queuedPrompt,
        Action<string> copyQueuedPromptMarkdown,
        Action<string> convertQueuedPromptToSteer,
        Action<string> deleteQueuedPrompt,
        Action<string, int> updateQueuedPromptCount,
        Action<string, string> updateQueuedPromptText,
        Func<Action<string>, string?, ChatPromptEditor> createPromptEditor)
    {
        var icon = new TextBlock($"{NerdFont.MdMessageTextOutline}")
        {
            Wrap = false,
            IsSelectable = false,
            Margin = new Thickness(0, 0, 1, 0),
        };

        var promptText = new TextBlock(queuedPrompt.PreviewText)
        {
            HorizontalAlignment = Align.Stretch,
            IsSelectable = false,
            Wrap = false,
            Trimming = TextTrimming.EndEllipsis,
            Margin = new Thickness(0, 0, 1, 0),
        };

        var copyButton = CreateIconButton(
            $"{NerdFont.MdContentCopy}",
            "Copy queued prompt markdown to the clipboard",
            () => copyQueuedPromptMarkdown(queuedPrompt.Text));
        copyButton.Margin = new Thickness(0, 0, 1, 0);

        var editButton = CreateIconButton(
            $"{NerdFont.MdSquareEditOutline}",
            "Edit queued prompt",
            () => ShowEditorDialog(queuedPrompt, updateQueuedPromptText, createPromptEditor));
        editButton.Margin = new Thickness(0, 0, 1, 0);

        var counter = CreateCounter(queuedPrompt, updateQueuedPromptCount);
        counter.Margin = new Thickness(0, 0, 1, 0);

        var steerButton = CreateIconButton(
            $"{NerdFont.MdArrowRightThinCircleOutline}",
            "Send immediately as a steering prompt",
            () => convertQueuedPromptToSteer(queuedPrompt.Id));
        steerButton.Margin = new Thickness(0, 0, 1, 0);

        var deleteButton = CreateIconButton(
            $"{NerdFont.MdTrashCanOutline}",
            "Delete queued prompt",
            () => deleteQueuedPrompt(queuedPrompt.Id));

        var row = new Grid
            {
                HorizontalAlignment = Align.Stretch,
            }
            .Rows(new RowDefinition { Height = GridLength.Auto })
            .Columns(
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = GridLength.Star(1) },
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = GridLength.Auto });
        row.Cell(icon, 0, 0);
        row.Cell(promptText, 0, 1);
        row.Cell(copyButton, 0, 2);
        row.Cell(editButton, 0, 3);
        row.Cell(counter, 0, 4);
        row.Cell(steerButton, 0, 5);
        row.Cell(deleteButton, 0, 6);
        return BuildStripRow(row, UiPalette.GetQueuedPromptBackgroundColor);
    }

    private static Visual CreateCounter(
        PromptStripItem queuedPrompt,
        Action<string, int> updateQueuedPromptCount)
    {
        var countState = new QueuedPromptCountState(
            queuedPrompt.RemainingCount.GetValueOrDefault(1),
            value => updateQueuedPromptCount(queuedPrompt.Id, value));
        var countBox = new NumberBox<int>()
            .Value(countState.Bind.Value)
            .ValueValidator(static value => value >= 1 ? null : "Use >= 1.")
            .MinWidth(3)
            .MaxWidth(5);
        countBox.ShowValidationMessage = false;
        countBox.InvalidNumberMessage = "Enter a whole number.";
        countBox.TextAlignment = TextAlignment.Center;

        return new HStack(
            CreateIconButton(
                "-",
                "Decrease repeat count",
                () => countState.Value = Math.Max(1, countState.Value - 1),
                isEnabled: countState.Value > 1),
            countBox,
            CreateIconButton(
                $"{NerdFont.MdPlus}",
                "Increase repeat count",
                () => countState.Value++))
        {
            Spacing = 0,
        };
    }

    private static Visual CreateIconButton(string icon, string tooltipText, Action onClick, bool isEnabled = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(icon);
        ArgumentException.ThrowIfNullOrWhiteSpace(tooltipText);
        ArgumentNullException.ThrowIfNull(onClick);

        var button = new Button(new TextBlock(icon) { Wrap = false, IsSelectable = false })
            .Click(onClick);
        button.IsEnabled = isEnabled;
        return button.Tooltip(new TextBlock(tooltipText));
    }

    private static Visual CreateSeparator() => new Rule();

    private static void ShowEditorDialog(
        PromptStripItem queuedPrompt,
        Action<string, string> updateQueuedPromptText,
        Func<Action<string>, string?, ChatPromptEditor> createPromptEditor)
    {
        ArgumentNullException.ThrowIfNull(updateQueuedPromptText);
        ArgumentNullException.ThrowIfNull(createPromptEditor);

        Dialog? dialog = null;
        var editor = createPromptEditor(SaveQueuedPrompt, "Edit queued prompt...");
        editor.Text = queuedPrompt.Text;

        var saveButton = new Button("Save").Click(() => SaveQueuedPrompt(editor.Text ?? string.Empty));
        var cancelButton = new Button("Cancel").Click(() => dialog?.Close());
        var buttonRow = new HStack([saveButton, cancelButton])
        {
            Spacing = 1,
            HorizontalAlignment = Align.End,
        };

        dialog = new Dialog(
            new TextBlock($"{NerdFont.MdSquareEditOutline} Edit Queued Prompt"),
            new DockLayout(
                top: null,
                content: editor.Scrollable().IsTabStop(false).MinHeight(8),
                bottom: buttonRow))
        {
            Width = 90,
            Height = 18,
        };
        dialog.AddCommand(new Command
        {
            Id = "QueuedPromptEditorDialog.Cancel",
            LabelMarkup = "Cancel",
            DescriptionMarkup = "Close the queued prompt editor.",
            Gesture = new KeyGesture(TerminalKey.Escape),
            Importance = CommandImportance.Primary,
            Execute = _ => dialog?.Close(),
        });
        dialog.Show();

        void SaveQueuedPrompt(string text)
        {
            var trimmed = text.Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                return;
            }

            updateQueuedPromptText(queuedPrompt.Id, trimmed);
            dialog?.Close();
        }
    }

    private static Visual BuildStripRow(Visual content, Func<Theme, Color> getBackground)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(getBackground);

        Placeholder? background = null;
        background = new Placeholder()
            .HorizontalAlignment(Align.Stretch)
            .VerticalAlignment(Align.Stretch)
            .Style(() => PlaceholderStyle.Default with { Background = getBackground(background!.GetTheme()) });
        return new ZStack(
            background,
            new Padder(content)
            {
                Padding = new Thickness(1, 0, 1, 0),
            });
    }
}

public sealed partial class QueuedPromptCountState
{
    private readonly Action<int> _onChanged;
    private bool _isInitialized;

    public QueuedPromptCountState(int value, Action<int> onChanged)
    {
        ArgumentNullException.ThrowIfNull(onChanged);

        _onChanged = onChanged;
        Value = Math.Max(1, value);
        _isInitialized = true;
    }

    [Bindable]
    public partial int Value { get; set; }

    partial void OnValueChanged(int value)
    {
        if (!_isInitialized)
        {
            return;
        }

        _onChanged(value);
    }
}
