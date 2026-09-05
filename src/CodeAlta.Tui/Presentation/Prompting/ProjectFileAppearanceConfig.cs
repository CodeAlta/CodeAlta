namespace CodeAlta.Tui.Presentation.Prompting;

internal sealed class ProjectFileAppearanceConfig
{
    public Dictionary<string, ProjectFileAppearanceDescriptor> Extensions { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, ProjectFileAppearanceDescriptor> Files { get; } = new(StringComparer.OrdinalIgnoreCase);

    public ProjectFileAppearanceDescriptor? Directory { get; init; }
}
