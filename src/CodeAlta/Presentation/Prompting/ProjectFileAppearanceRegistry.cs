using CodeAlta.Presentation.Styling;
using CodeAlta.Catalog;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Styling;

namespace CodeAlta.Presentation.Prompting;

internal sealed class ProjectFileAppearanceRegistry : IProjectFileAppearanceRegistry
{
    private static readonly ProjectFileAppearanceDescriptor DefaultDirectory = new(
        $"{TerminalIcons.MdFolderOutline}",
        Color.Default,
        "directory");

    private static readonly ProjectFileAppearanceDescriptor DefaultFile = new(
        $"{TerminalIcons.MdFileDocumentOutline}",
        Color.Default,
        "file");

    private static readonly IReadOnlyDictionary<string, ProjectFileAppearanceDescriptor> BuiltInExtensions =
        new Dictionary<string, ProjectFileAppearanceDescriptor>(StringComparer.OrdinalIgnoreCase)
        {
            [".cs"] = new($"{TerminalIcons.MdLanguageCsharp}", Color.Default, "csharp"),
            [".csproj"] = new($"{TerminalIcons.MdLanguageCsharp}", Color.Default, "dotnet"),
            [".sln"] = new($"{TerminalIcons.MdCodeBracesBox}", Color.Default, "dotnet"),
            [".json"] = new($"{TerminalIcons.MdCodeJson}", Color.Default, "json"),
            [".yml"] = new($"{TerminalIcons.MdFileCodeOutline}", Color.Default, "yaml"),
            [".yaml"] = new($"{TerminalIcons.MdFileCodeOutline}", Color.Default, "yaml"),
            [".md"] = new($"{TerminalIcons.MdLanguageMarkdown}", Color.Default, "markdown"),
            [".ts"] = new($"{TerminalIcons.MdLanguageTypescript}", Color.Default, "typescript"),
            [".tsx"] = new($"{TerminalIcons.MdLanguageTypescript}", Color.Default, "typescript"),
            [".js"] = new($"{TerminalIcons.MdLanguageJavascript}", Color.Default, "javascript"),
            [".jsx"] = new($"{TerminalIcons.MdLanguageJavascript}", Color.Default, "javascript"),
            [".py"] = new($"{TerminalIcons.MdLanguagePython}", Color.Default, "python"),
            [".go"] = new($"{TerminalIcons.MdLanguageGo}", Color.Default, "go"),
            [".rs"] = new($"{TerminalIcons.MdLanguageRust}", Color.Default, "rust"),
            [".java"] = new($"{TerminalIcons.MdLanguageJava}", Color.Default, "java"),
            [".kt"] = new($"{TerminalIcons.MdLanguageKotlin}", Color.Default, "kotlin"),
            [".cpp"] = new($"{TerminalIcons.MdLanguageCpp}", Color.Default, "cpp"),
            [".h"] = new($"{TerminalIcons.MdLanguageCpp}", Color.Default, "cpp"),
            [".html"] = new($"{TerminalIcons.MdLanguageHtml5}", Color.Default, "html"),
            [".css"] = new($"{TerminalIcons.MdLanguageCss3}", Color.Default, "css"),
            [".scss"] = new($"{TerminalIcons.MdLanguageCss3}", Color.Default, "scss"),
            [".toml"] = new($"{TerminalIcons.MdCodeBraces}", Color.Default, "toml"),
            [".xml"] = new($"{TerminalIcons.MdXml}", Color.Default, "xml"),
            [".sh"] = new($"{TerminalIcons.MdBash}", Color.Default, "shell"),
            [".ps1"] = new($"{TerminalIcons.MdPowershell}", Color.Default, "powershell"),
            [".sql"] = new($"{TerminalIcons.MdDatabase}", Color.Default, "sql"),
        };

    private static readonly IReadOnlyDictionary<string, ProjectFileAppearanceDescriptor> BuiltInFiles =
        new Dictionary<string, ProjectFileAppearanceDescriptor>(StringComparer.OrdinalIgnoreCase)
        {
            ["dockerfile"] = new($"{TerminalIcons.MdDocker}", Color.Default, "docker"),
        };

    public static ProjectFileAppearanceRegistry Default { get; } = new();

    private readonly IReadOnlyList<IProjectFileAppearanceContribution> _contributions;
    private readonly ProjectFileAppearanceConfig? _userOverrides;

    public ProjectFileAppearanceRegistry(
        IEnumerable<IProjectFileAppearanceContribution>? contributions = null,
        ProjectFileAppearanceConfig? userOverrides = null)
    {
        _contributions = contributions?.ToArray() ?? [];
        _userOverrides = userOverrides;
    }

    public ProjectFileAppearance GetAppearance(ProjectFileSearchItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        var descriptor = ResolveDescriptor(item) ?? DefaultFile;
        return new ProjectFileAppearance(
            descriptor.Icon,
            descriptor.Foreground,
            Style.None,
            descriptor.Category);
    }

    public ProjectFileAppearance GetDirectoryAppearance()
    {
        var descriptor = ResolveDirectoryDescriptor();
        return new ProjectFileAppearance(
            descriptor.Icon,
            descriptor.Foreground,
            Style.None,
            descriptor.Category);
    }

    private ProjectFileAppearanceDescriptor? ResolveDescriptor(ProjectFileSearchItem item)
    {
        if (item.Kind == ProjectFileSearchItemKind.Directory)
        {
            return ResolveDirectoryDescriptor();
        }

        var descriptor = ResolveFromMaps(BuiltInFiles, BuiltInExtensions, item.Basename, item.Extension) ?? DefaultFile;
        foreach (var contribution in _contributions)
        {
            descriptor = ResolveFromMaps(contribution.Files, contribution.Extensions, item.Basename, item.Extension) ?? descriptor;
        }

        return ResolveFromMaps(_userOverrides?.Files, _userOverrides?.Extensions, item.Basename, item.Extension) ?? descriptor;
    }

    private ProjectFileAppearanceDescriptor ResolveDirectoryDescriptor()
    {
        var descriptor = DefaultDirectory;
        foreach (var contribution in _contributions)
        {
            descriptor = contribution.Directory ?? descriptor;
        }

        return _userOverrides?.Directory ?? descriptor;
    }

    private static ProjectFileAppearanceDescriptor? ResolveFromMaps(
        IReadOnlyDictionary<string, ProjectFileAppearanceDescriptor>? fileMap,
        IReadOnlyDictionary<string, ProjectFileAppearanceDescriptor>? extensionMap,
        string basename,
        string extension)
    {
        if (fileMap is not null && fileMap.TryGetValue(basename, out var fileDescriptor))
        {
            return fileDescriptor;
        }

        if (extensionMap is not null &&
            !string.IsNullOrWhiteSpace(extension) &&
            extensionMap.TryGetValue(extension, out var extensionDescriptor))
        {
            return extensionDescriptor;
        }

        return null;
    }
}
