using XenoAtom.Terminal.UI;

namespace CodeAlta.Tui.ViewModels;

public sealed partial class ProjectDetailsDialogViewModel
{
    public ProjectDetailsDialogViewModel()
    {
        Id = string.Empty;
        Slug = string.Empty;
        Name = string.Empty;
        DisplayName = string.Empty;
        ProjectPath = string.Empty;
        DefaultBranch = "main";
        Description = string.Empty;
        TagsText = string.Empty;
        CheckoutPathTemplate = string.Empty;
        SourcePath = string.Empty;
    }

    [Bindable]
    public partial string? Id { get; set; }

    [Bindable]
    public partial string? Slug { get; set; }

    [Bindable]
    public partial string? Name { get; set; }

    [Bindable]
    public partial string? DisplayName { get; set; }

    [Bindable]
    public partial string? ProjectPath { get; set; }

    [Bindable]
    public partial string? DefaultBranch { get; set; }

    [Bindable]
    public partial string? Description { get; set; }

    [Bindable]
    public partial string? TagsText { get; set; }

    [Bindable]
    public partial string? CheckoutPathTemplate { get; set; }

    [Bindable]
    public partial string? SourcePath { get; set; }

    [Bindable]
    public partial bool Archived { get; set; }
}
