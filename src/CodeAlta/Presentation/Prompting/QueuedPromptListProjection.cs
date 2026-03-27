using CodeAlta.App.State;

namespace CodeAlta.Presentation.Prompting;

public enum PromptStripItemKind
{
    PendingSteer,
    QueuedPrompt,
}

public readonly record struct PromptStripItem(
    PromptStripItemKind Kind,
    string Id,
    string Text,
    string PreviewText,
    int? RemainingCount);

internal readonly record struct QueuedPromptListProjection(
    IReadOnlyList<PromptStripItem> Items,
    bool HasQueuedPrompts)
{
    public bool HasItems => Items.Count > 0;
}

internal static class QueuedPromptListProjectionBuilder
{
    public static QueuedPromptListProjection Build(OpenThreadState? tab)
    {
        if (tab is null)
        {
            return new QueuedPromptListProjection([], HasQueuedPrompts: false);
        }

        lock (tab.PromptStripSyncRoot)
        {
            if (tab.PendingSteers.Count == 0 && tab.QueuedPrompts.Count == 0)
            {
                return new QueuedPromptListProjection([], HasQueuedPrompts: false);
            }

            var items = tab.PendingSteers
                .Select(
                    static prompt => new PromptStripItem(
                        PromptStripItemKind.PendingSteer,
                        prompt.Id,
                        prompt.Text,
                        BuildPreviewText(prompt.Text),
                        RemainingCount: null))
                .Concat(
                    tab.QueuedPrompts.Select(
                        static prompt => new PromptStripItem(
                            PromptStripItemKind.QueuedPrompt,
                            prompt.Id,
                            prompt.Text,
                            BuildPreviewText(prompt.Text),
                            prompt.RemainingCount)))
                .ToArray();
            return new QueuedPromptListProjection(items, HasQueuedPrompts: tab.QueuedPrompts.Count > 0);
        }
    }

    internal static string BuildPreviewText(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        var builder = new System.Text.StringBuilder(text.Length);
        var pendingWhitespace = false;
        foreach (var ch in text.Trim())
        {
            if (char.IsWhiteSpace(ch))
            {
                pendingWhitespace = builder.Length > 0;
                continue;
            }

            if (pendingWhitespace)
            {
                builder.Append(' ');
                pendingWhitespace = false;
            }

            builder.Append(ch);
        }

        return builder.ToString();
    }
}
