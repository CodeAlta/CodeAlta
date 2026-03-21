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

internal static class QueuedPromptListView
{
    public static Visual Build(
        ThreadWorkspaceViewModel workspaceViewModel,
        Action<string> copyQueuedPromptMarkdown,
        Action<string> convertQueuedPromptToSteer,
        Action<string> deleteQueuedPrompt,
        Action<string, int> updateQueuedPromptCount,
        Action<string, string> updateQueuedPromptText,
        Func<Action<string>, string?, ChatPromptEditor> createPromptEditor)
    {
        ArgumentNullException.ThrowIfNull(workspaceViewModel);
        ArgumentNullException.ThrowIfNull(copyQueuedPromptMarkdown);
        ArgumentNullException.ThrowIfNull(convertQueuedPromptToSteer);
        ArgumentNullException.ThrowIfNull(deleteQueuedPrompt);
        ArgumentNullException.ThrowIfNull(updateQueuedPromptCount);
        ArgumentNullException.ThrowIfNull(updateQueuedPromptText);
        ArgumentNullException.ThrowIfNull(createPromptEditor);

        var _ = workspaceViewModel.QueuedPromptsVersion;
        if (!workspaceViewModel.HasQueuedPrompts)
        {
            return new Placeholder { IsVisible = false };
        }

        var rows = new List<Visual>(workspaceViewModel.QueuedPrompts.Count * 2);
        for (var index = 0; index < workspaceViewModel.QueuedPrompts.Count; index++)
        {
            rows.Add(
                BuildRow(
                    workspaceViewModel.QueuedPrompts[index],
                    copyQueuedPromptMarkdown,
                    convertQueuedPromptToSteer,
                    deleteQueuedPrompt,
                    updateQueuedPromptCount,
                    updateQueuedPromptText,
                    createPromptEditor));

            if (index < workspaceViewModel.QueuedPrompts.Count - 1)
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

    private static Visual BuildRow(
        QueuedPromptListItem queuedPrompt,
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
        return new ZStack(
            new Placeholder()
                .HorizontalAlignment(Align.Stretch)
                .VerticalAlignment(Align.Stretch)
                .Style(PlaceholderStyle.Default with { Background = UiPalette.QueuedPromptBackgroundColor }),
            row.Padding(new Thickness(1, 0, 1, 0)));
    }

    private static Visual CreateCounter(
        QueuedPromptListItem queuedPrompt,
        Action<string, int> updateQueuedPromptCount)
    {
        var currentCount = queuedPrompt.RemainingCount;
        var countBox = new NumberBox<int>
        {
            Value = currentCount,
            ShowValidationMessage = false,
            InvalidNumberMessage = "Enter a whole number.",
            TextAlignment = TextAlignment.Center,
            MinWidth = 3,
            MaxWidth = 5,
        };
        countBox.RegisterDynamicUpdate(
            _ =>
            {
                if (countBox.Value <= 0)
                {
                    countBox.Value = currentCount;
                    return;
                }

                if (countBox.Value == currentCount)
                {
                    return;
                }

                currentCount = countBox.Value;
                updateQueuedPromptCount(queuedPrompt.Id, currentCount);
            });

        return new HStack(
        [
            CreateIconButton(
                "-",
                "Decrease repeat count",
                () => updateQueuedPromptCount(queuedPrompt.Id, Math.Max(1, currentCount - 1)),
                isEnabled: currentCount > 1),
            countBox,
            CreateIconButton(
                $"{NerdFont.MdPlus}",
                "Increase repeat count",
                () => updateQueuedPromptCount(queuedPrompt.Id, currentCount + 1)),
        ])
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
        QueuedPromptListItem queuedPrompt,
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
                content: editor.Scrollable().MinHeight(8),
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
}
