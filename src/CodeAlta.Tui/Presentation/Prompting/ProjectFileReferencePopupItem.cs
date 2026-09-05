using CodeAlta.Catalog;

namespace CodeAlta.Tui.Presentation.Prompting;

internal sealed record ProjectFileReferencePopupItem(
    ProjectFileSearchResult Result,
    ProjectFileAppearance Appearance)
{
    public string PrimaryText => Result.Item.Basename;

    public string SecondaryText => Result.Item.ParentPath;
}
