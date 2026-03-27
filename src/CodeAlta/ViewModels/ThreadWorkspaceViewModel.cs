using XenoAtom.Terminal.UI;
using CodeAlta.Presentation.Prompting;

namespace CodeAlta.ViewModels;

public sealed partial class ThreadWorkspaceViewModel
{
    public ThreadWorkspaceViewModel()
    {
        BackendStatusMarkup = string.Empty;
        AutoScroll = true;
        SelectedTabIndex = -1;
        PromptStripItems = [];
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
    public partial bool AutoScroll { get; set; }

    [Bindable]
    public partial int SelectedTabIndex { get; set; }

    [Bindable]
    public partial bool HasQueuedPrompts { get; set; }

    [Bindable]
    public partial IReadOnlyList<PromptStripItem> PromptStripItems { get; set; }

    internal void SetPromptStripItems(IReadOnlyList<PromptStripItem> promptStripItems, bool hasQueuedPrompts)
    {
        ArgumentNullException.ThrowIfNull(promptStripItems);

        PromptStripItems = promptStripItems;
        HasQueuedPrompts = hasQueuedPrompts;
    }
}
