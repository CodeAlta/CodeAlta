using CodeAlta.Catalog;
using XenoAtom.Terminal.UI;

namespace CodeAlta.ViewModels;

public sealed partial class ProjectSessionsDialogRowViewModel
{
    public ProjectSessionsDialogRowViewModel()
    {
        SessionId = string.Empty;
        Title = string.Empty;
        ProviderId = string.Empty;
        ProviderDisplayName = string.Empty;
        LastUpdatedRelative = SR.T("never");
        LastUpdatedExact = SR.T("Never");
    }

    [Bindable]
    public partial bool IsSelected { get; set; }

    [Bindable]
    public partial string SessionId { get; set; }

    [Bindable]
    public partial string Title { get; set; }

    [Bindable]
    public partial string ProviderId { get; set; }

    [Bindable]
    public partial string ProviderDisplayName { get; set; }

    [Bindable]
    public partial SessionViewKind SessionKind { get; set; }

    [Bindable]
    public partial DateTimeOffset? LastUpdatedAt { get; set; }

    [Bindable]
    public partial string LastUpdatedRelative { get; set; }

    [Bindable]
    public partial string LastUpdatedExact { get; set; }

    [Bindable]
    public partial int? MessageCount { get; set; }

    [Bindable]
    public partial string? ProjectId { get; set; }
}
