using CodeAlta.App.State;
using CodeAlta.Catalog;

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
    int ImageCount,
    int? RemainingCount);

internal readonly record struct QueuedPromptListProjection(
    IReadOnlyList<PromptStripItem> Items,
    bool HasQueuedPrompts)
{
    public bool HasItems => Items.Count > 0;
}

internal static class QueuedPromptListProjectionBuilder
{
    public static QueuedPromptListProjection Build(OpenSessionState? tab)
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
                        BuildPreviewText(prompt.Submission),
                        prompt.Images.Count,
                        RemainingCount: null))
                .Concat(
                    tab.QueuedPrompts.Select(
                        static prompt => new PromptStripItem(
                            PromptStripItemKind.QueuedPrompt,
                            prompt.Id,
                            prompt.Text,
                            BuildPreviewText(prompt.Submission),
                            prompt.Images.Count,
                            prompt.RemainingCount)))
                .ToArray();
            return new QueuedPromptListProjection(items, HasQueuedPrompts: tab.QueuedPrompts.Count > 0);
        }
    }

    internal static string BuildPreviewText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

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

    private static string BuildPreviewText(PromptSubmission submission)
    {
        var text = BuildPreviewText(submission.Text);
        if (submission.Images.Count == 0)
        {
            return string.IsNullOrWhiteSpace(text) ? SR.T("Prompt") : text;
        }

        var imageSuffix = submission.Images.Count == 1 ? SR.T("1 image") : SR.T("{0} images", submission.Images.Count);
        return string.IsNullOrWhiteSpace(text) ? imageSuffix : $"{text} · {imageSuffix}";
    }
}
