using CodeAlta.Catalog;

namespace CodeAlta.Tui.Presentation.Prompting;

internal interface IProjectFileAppearanceRegistry
{
    ProjectFileAppearance GetAppearance(ProjectFileSearchItem item);
}
