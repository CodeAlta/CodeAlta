using CodeAlta.Presentation.Chat;
using CodeAlta.Presentation.Prompting;
using CodeAlta.ViewModels;
using XenoAtom.Terminal;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Geometry;
using XenoAtom.Terminal.UI.Styling;

namespace CodeAlta.Views;

internal static class QueuedPromptListView
{
    public static Visual Build(
        ThreadWorkspaceViewModel workspaceViewModel,
        Action<string> convertQueuedPromptToSteer,
        Action<string> deleteQueuedPrompt,
        Action<string, int> updateQueuedPromptCount,
        Action<string, string> updateQueuedPromptText,
        Func<Action<string>, string?, ChatPromptEditor> createPromptEditor)
    {
        ArgumentNullException.ThrowIfNull(workspaceViewModel);
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

        return new VStack(
            workspaceViewModel.QueuedPrompts
                .Select(
                    queuedPrompt => BuildRow(
                        queuedPrompt,
                        convertQueuedPromptToSteer,
                        deleteQueuedPrompt,
                        updateQueuedPromptCount,
                        updateQueuedPromptText,
                        createPromptEditor))
                .ToArray())
        {
            Spacing = 0,
            HorizontalAlignment = Align.Stretch,
        };
    }

    private static Visual BuildRow(
        QueuedPromptListItem queuedPrompt,
        Action<string> convertQueuedPromptToSteer,
        Action<string> deleteQueuedPrompt,
        Action<string, int> updateQueuedPromptCount,
        Action<string, string> updateQueuedPromptText,
        Func<Action<string>, string?, ChatPromptEditor> createPromptEditor)
    {
        var promptText = new TextBlock(queuedPrompt.PreviewText)
        {
            Wrap = false,
            IsSelectable = false,
        }.Trimming(TextTrimming.EndEllipsis);

        var left = new HStack(
        [
            new TextBlock($"{NerdFont.MdMessageTextOutline}")
            {
                Wrap = false,
                IsSelectable = false,
            },
            promptText,
        ])
        {
            Spacing = 1,
            HorizontalAlignment = Align.Stretch,
        };

        var actions = new HStack(
        [
            CreateIconButton(
                $"{NerdFont.MdSquareEditOutline}",
                "Edit queued prompt",
                () => ShowEditorDialog(queuedPrompt, updateQueuedPromptText, createPromptEditor)),
            CreateCounter(queuedPrompt, updateQueuedPromptCount),
            CreateIconButton(
                $"{NerdFont.MdArrowRightThinCircleOutline}",
                "Send immediately as a steering prompt",
                () => convertQueuedPromptToSteer(queuedPrompt.Id)),
            CreateIconButton(
                $"{NerdFont.MdTrashCanOutline}",
                "Delete queued prompt",
                () => deleteQueuedPrompt(queuedPrompt.Id)),
        ])
        {
            Spacing = 1,
        };

        return new StatusBar()
            .LeftText(left)
            .RightText(actions);
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
                $"{NerdFont.MdMinus}",
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
            .Click(onClick)
            .Style(ButtonStyle.Default with { Padding = new Thickness(0) });
        button.IsEnabled = isEnabled;
        return button.Tooltip(new TextBlock(tooltipText));
    }

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
