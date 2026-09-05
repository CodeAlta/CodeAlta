namespace CodeAlta.Tui.Presentation.Prompting;

internal interface IProjectFileAppearanceContribution
{
    IReadOnlyDictionary<string, ProjectFileAppearanceDescriptor> Extensions { get; }

    IReadOnlyDictionary<string, ProjectFileAppearanceDescriptor> Files { get; }

    ProjectFileAppearanceDescriptor? Directory { get; }
}
