using CodeAlta.Catalog;
using CodeAlta.Models;

namespace CodeAlta.Presentation.Threads;

internal static class InitialThreadSelectionResolver
{
    public static InitialThreadSelection Resolve(
        WorkThreadViewState viewState,
        IReadOnlyList<WorkThreadDescriptor> threads)
    {
        ArgumentNullException.ThrowIfNull(viewState);
        ArgumentNullException.ThrowIfNull(threads);

        var selectedThreadId = viewState.SelectedThreadId ?? viewState.OpenThreadIds.FirstOrDefault();
        var selectedThread = string.IsNullOrWhiteSpace(selectedThreadId)
            ? null
            : threads.FirstOrDefault(thread => string.Equals(thread.ThreadId, selectedThreadId, StringComparison.OrdinalIgnoreCase));

        return new InitialThreadSelection(
            selectedThread?.ThreadId,
            selectedThread?.ThreadId);
    }
}