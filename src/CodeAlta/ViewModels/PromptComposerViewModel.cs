using CodeAlta.Presentation.Prompting;
using XenoAtom.Terminal.UI;

namespace CodeAlta.ViewModels;

public sealed partial class PromptComposerViewModel
{
    public PromptComposerViewModel()
    {
        Placeholder = PromptComposerProjectionBuilder.BuildDefaultPromptPlaceholder();
        IsEnabled = true;
        CanSend = true;
    }

    [Bindable]
    public partial string? Placeholder { get; set; }

    [Bindable]
    public partial bool IsEnabled { get; set; }

    [Bindable]
    public partial bool CanSend { get; set; }

    [Bindable]
    public partial bool CanSteer { get; set; }

    [Bindable]
    public partial bool CanDelegate { get; set; }

    [Bindable]
    public partial bool CanAbort { get; set; }

    [Bindable]
    public partial bool CanCompact { get; set; }

    [Bindable]
    public partial bool CanCloseTab { get; set; }

    [Bindable]
    public partial bool CanClearQueue { get; set; }

    [Bindable]
    public partial bool CanAlwaysEnqueue { get; set; }

    [Bindable]
    public partial bool AlwaysEnqueue { get; set; }

    [Bindable]
    public partial int PromptImageAttachmentVersion { get; set; }
}
