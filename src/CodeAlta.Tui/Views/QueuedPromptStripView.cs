using CodeAlta.Tui.Presentation.Prompting;
using CodeAlta.Tui.ViewModels;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;

namespace CodeAlta.Tui.Views;

internal sealed class QueuedPromptStripView
{
    public QueuedPromptStripView(
        SessionWorkspaceViewModel workspaceViewModel,
        QueuedPromptStripController controller)
    {
        ArgumentNullException.ThrowIfNull(workspaceViewModel);
        ArgumentNullException.ThrowIfNull(controller);

        Root = new ComputedVisual(
            () => QueuedPromptListView.Build(
                workspaceViewModel.PromptStripItems,
                controller.CopyMarkdown,
                controller.ConvertQueuedPromptToSteer,
                controller.DeletePendingSteer,
                controller.DeleteQueuedPrompt,
                controller.UpdateQueuedPromptCount,
                controller.UpdateQueuedPromptText,
                controller.CreatePromptEditor));
    }

    public Visual Root { get; }
}
