using CodeAlta.Models;
using XenoAtom.Terminal.UI;

namespace CodeAlta.ViewModels;

internal sealed partial class CodeAltaShellViewModel
{
    public CodeAltaShellViewModel()
    {
        HeaderText = "CodeAlta";
        StatusText = "Prompt ready";
        StatusIconMarkup = string.Empty;
        StatusTone = StatusTone.Ready;
        IsInitialized = false;
    }

    [Bindable]
    public partial string HeaderText { get; set; }

    [Bindable]
    public partial string StatusText { get; set; }

    [Bindable]
    public partial string StatusIconMarkup { get; set; }

    [Bindable]
    public partial bool StatusBusy { get; set; }

    [Bindable]
    public partial StatusTone StatusTone { get; set; }

    [Bindable]
    public partial bool IsInitialized { get; set; }
}
