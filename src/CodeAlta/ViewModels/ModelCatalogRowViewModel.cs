using XenoAtom.Terminal.UI;

namespace CodeAlta.ViewModels;

internal sealed partial class ModelCatalogRowViewModel
{
    public ModelCatalogRowViewModel()
    {
        CurrentMarker = string.Empty;
        ProviderKey = string.Empty;
        ProviderDisplayName = string.Empty;
        ProviderStatus = string.Empty;
        ModelId = string.Empty;
        ModelDisplayName = string.Empty;
        ReasoningText = string.Empty;
        ToolCallText = string.Empty;
        StructuredOutputText = string.Empty;
        ImageInputText = string.Empty;
        ModelsDevRef = string.Empty;
        Status = string.Empty;
    }

    [Bindable]
    public partial string CurrentMarker { get; set; }

    [Bindable]
    public partial string ProviderKey { get; set; }

    [Bindable]
    public partial string ProviderDisplayName { get; set; }

    [Bindable]
    public partial string ProviderStatus { get; set; }

    [Bindable]
    public partial string ModelId { get; set; }

    [Bindable]
    public partial string ModelDisplayName { get; set; }

    [Bindable]
    public partial long? ContextWindowTokens { get; set; }

    [Bindable]
    public partial long? InputTokenLimit { get; set; }

    [Bindable]
    public partial long? OutputTokenLimit { get; set; }

    [Bindable]
    public partial string ReasoningText { get; set; }

    [Bindable]
    public partial string ToolCallText { get; set; }

    [Bindable]
    public partial string StructuredOutputText { get; set; }

    [Bindable]
    public partial string ImageInputText { get; set; }

    [Bindable]
    public partial string ModelsDevRef { get; set; }

    [Bindable]
    public partial string Status { get; set; }
}
