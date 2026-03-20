using CodeAlta.App.State;

namespace CodeAlta.Presentation.Prompting;

internal readonly record struct QueuedPromptListItem(
    string Id,
    string Text,
    string PreviewText,
    int RemainingCount);

internal readonly record struct QueuedPromptListProjection(
    IReadOnlyList<QueuedPromptListItem> Items)
{
    public bool HasItems => Items.Count > 0;
}

internal static class QueuedPromptListProjectionBuilder
{
    public static QueuedPromptListProjection Build(OpenThreadState? tab)
    {
        if (tab is null)
        {
            return new QueuedPromptListProjection([]);
        }

        lock (tab.QueuedPromptsSyncRoot)
        {
            if (tab.QueuedPrompts.Count == 0)
            {
                return new QueuedPromptListProjection([]);
            }

            var items = tab.QueuedPrompts
                .Select(
                    static prompt => new QueuedPromptListItem(
                        prompt.Id,
                        prompt.Text,
                        BuildPreviewText(prompt.Text),
                        prompt.RemainingCount))
                .ToArray();
            return new QueuedPromptListProjection(items);
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
