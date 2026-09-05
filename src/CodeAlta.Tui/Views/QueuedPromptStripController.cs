using CodeAlta.Tui.Presentation.Prompting;

namespace CodeAlta.Tui.Views;

internal sealed record QueuedPromptStripController(
    Action<string> CopyMarkdown,
    Action<string> ConvertQueuedPromptToSteer,
    Action<string> DeletePendingSteer,
    Action<string> DeleteQueuedPrompt,
    Action<string, int> UpdateQueuedPromptCount,
    Action<string, string> UpdateQueuedPromptText,
    Func<Action<string>, string?, ChatPromptEditor> CreatePromptEditor)
{
    public static QueuedPromptStripController Create(
        Action<string> copyMarkdown,
        Action<string> convertQueuedPromptToSteer,
        Action<string> deletePendingSteer,
        Action<string> deleteQueuedPrompt,
        Action<string, int> updateQueuedPromptCount,
        Action<string, string> updateQueuedPromptText,
        Func<Action<string>, string?, ChatPromptEditor> createPromptEditor)
    {
        ArgumentNullException.ThrowIfNull(copyMarkdown);
        ArgumentNullException.ThrowIfNull(convertQueuedPromptToSteer);
        ArgumentNullException.ThrowIfNull(deletePendingSteer);
        ArgumentNullException.ThrowIfNull(deleteQueuedPrompt);
        ArgumentNullException.ThrowIfNull(updateQueuedPromptCount);
        ArgumentNullException.ThrowIfNull(updateQueuedPromptText);
        ArgumentNullException.ThrowIfNull(createPromptEditor);
        return new QueuedPromptStripController(
            copyMarkdown,
            convertQueuedPromptToSteer,
            deletePendingSteer,
            deleteQueuedPrompt,
            updateQueuedPromptCount,
            updateQueuedPromptText,
            createPromptEditor);
    }
}
