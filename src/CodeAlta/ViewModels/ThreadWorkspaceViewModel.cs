using XenoAtom.Terminal.UI;
using CodeAlta.Presentation.Prompting;

namespace CodeAlta.ViewModels;

public sealed partial class ThreadWorkspaceViewModel
{
    private IReadOnlyList<QueuedPromptListItem> _queuedPrompts = [];

    public ThreadWorkspaceViewModel()
    {
        BackendStatusMarkup = string.Empty;
    }

    [Bindable]
    public partial string BackendStatusMarkup { get; set; }

    [Bindable]
    public partial bool CanSelectBackend { get; set; }

    [Bindable]
    public partial bool CanSelectModel { get; set; }

    [Bindable]
    public partial bool CanSelectReasoning { get; set; }

    [Bindable]
    public partial bool CanToggleAutoScroll { get; set; }

    [Bindable]
    public partial bool HasQueuedPrompts { get; set; }

    [Bindable]
    public partial int QueuedPromptsVersion { get; set; }

    internal IReadOnlyList<QueuedPromptListItem> QueuedPrompts => _queuedPrompts;

    internal void SetQueuedPrompts(IReadOnlyList<QueuedPromptListItem> queuedPrompts)
    {
        ArgumentNullException.ThrowIfNull(queuedPrompts);

        _queuedPrompts = queuedPrompts;
        HasQueuedPrompts = queuedPrompts.Count > 0;
        QueuedPromptsVersion++;
    }
}
