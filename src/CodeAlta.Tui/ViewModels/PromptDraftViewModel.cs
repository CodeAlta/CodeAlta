using XenoAtom.Terminal.UI;

namespace CodeAlta.Tui.ViewModels;

public sealed partial class PromptDraftViewModel
{
    private readonly Action<string?> _onPromptTextChanged;

    public PromptDraftViewModel(Action<string?> onPromptTextChanged)
    {
        ArgumentNullException.ThrowIfNull(onPromptTextChanged);

        _onPromptTextChanged = onPromptTextChanged;
        PromptText = string.Empty;
    }

    [Bindable]
    public partial string? PromptText { get; set; }

    partial void OnPromptTextChanged(string? value)
        => _onPromptTextChanged(value);
}
