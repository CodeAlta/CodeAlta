using System.Security.Cryptography;
using System.Text;

namespace CodeAlta.Tests;

/// <summary>Pure-text mandatory inverses; compile-linked into Desktop.Tests without terminal dependencies.</summary>
internal static class PluginNeutralContractSourceInverse
{
    internal static string Restore(string path, string source)
    {
        source = SourceTestText.DecodeSource(new UTF8Encoding(false, true).GetBytes(source));
        source = PluginGitHubBackendSeparationSourceInverse.RestoreNeutralInput(path, source);
        source = PluginMcpBackendSeparationSourceInverse.RestoreNeutralInput(path, source);
        source = PluginStatisticsBackendSeparationSourceInverse.RestoreNeutralInput(path, source);
        source = PluginAuthoringProfileSourceInverse.RestoreNeutralInput(path, source);
        foreach (var (before, after) in Edits(path))
        {
            var canonicalAfter = SourceTestText.Canonicalize(after);
            Assert.IsTrue(canonicalAfter.Length > 0, path);
            Assert.AreEqual(1, source.Split(canonicalAfter, StringSplitOptions.None).Length - 1, path);
            source = source.Replace(canonicalAfter, SourceTestText.Canonicalize(before), StringComparison.Ordinal);
        }
        source = SourceTestText.DecodeSource(new UTF8Encoding(false, true).GetBytes(source));
        Assert.AreEqual(Originals.Single(entry => entry.Path == path).Hash, Hash(source), path);
        return source;
    }

    internal static string Hash(string source) => Convert.ToHexString(SHA256.HashData(new UTF8Encoding(false, true).GetBytes(source)));

    internal static IEnumerable<string> Representations(string source)
    {
        yield return source;
        yield return source.Replace("\n", "\r\n", StringComparison.Ordinal);
        var lines = source.Split('\n');
        yield return string.Concat(lines.Select((line, index) => index == lines.Length - 1 ? line : line + (index % 2 == 0 ? "\r\n" : "\n")));
    }

    // Exactly the parent-qualified canonical raw-Git 6f2c1967 originals; never generated/rebased.
    internal static IReadOnlyList<(string Path, string Hash)> Originals =>
    [
        ("CodeAlta.Plugins.Abstractions/PluginServices.cs", "760AC6EBA27906578F7B08FB45E39225A8186A5A673D2348441A39091C0874C7"),
        ("CodeAlta.Plugins.Abstractions/PluginFactories.cs", "FFD41CE4DC600542608CD856B3E3E1BEA74F8487D5B0E245F1C3FB01DD8DC958"),
        ("CodeAlta.Plugins.Abstractions/PluginDialogLayout.cs", "3974EA0B353B6426FF2223C317E1365F2F12D7C7E7C3D04DCEAB463FDD5D0194"),
        ("CodeAlta.Plugins.Abstractions/PluginPrompting.cs", "B3A1BC93ECF6BA1C328AEFFF721E4A1540FDEBC7FC4BB573E22DE52141F1E7C9"),
        ("CodeAlta.Plugins.Abstractions/PluginContributions.cs", "854A1AFD5A13791E6084AB73FB68220B7684CDEC4651A172506A7BC058C4FA7D"),
        ("CodeAlta.Plugins.Abstractions/CodeAlta.Plugins.Abstractions.csproj", "32BF9AB1949E27713BC0A2DCA6E312EDCF12B4CAB27DC6E59990D98EF42CEFB6"),
        ("CodeAlta.Plugins.Tui/PluginVisualContributions.cs", "5A7C684D4D36020D4921FBB323CF07AEAD0CEA51CD72B4A5D94E2FCABF00228C"),
        ("CodeAlta.Tui/Views/ChatPromptEditor.cs", "DB23F694614A36C773A64FA6C63C96ADC91E4F5E1D7832B4ED1E03EA32F7E5D4"),
        ("CodeAlta.Plugin.GitHub/GitHubPlugin.cs", "B247E80A62A73B8A4BAA67FAF13BC3AA68B973480AE068EBADD5D83D6BCEB96E"),
        ("CodeAlta.Plugin.GitHub/GitHubIssuePromptAttachment.cs", "56D046DDFCDFD03A4CCF4954E005161BE5244474FE815B91842591CEC6B587AC"),
        ("CodeAlta.Plugin.GitHub/CodeAlta.Plugin.GitHub.csproj", "7C1F1950365BC9F1F59ECDF52437438750DE15C82067E4B2BB2CE00503BDAFEC"),
        ("CodeAlta.Tui/App/DialogBoundsResolver.cs", "38D24E5F98743F07BBAF934B7EF39C7BAC0A7CB38BEEBD70081BAF6E4CDBD1BC"),
        ("CodeAlta.Tui/Views/ResponsiveDialogSize.cs", "1F3FA5E90856A61A647DA2B6D20FE651E238716A1E54C4BAC7A98140257B751D"),
        ("CodeAlta.Plugin.Mcp/McpServersDialog.cs", "F718497A257B20C5542CB045FACD2859B46B39D8816013C1B5B13E54E11F03F9"),
        ("CodeAlta.Plugin.Statistics/CodeAlta.Plugin.Statistics.csproj", "BAB8FA2737216952893083838B3F0DF0E37B294826BD04AFC516E5A722BC8D24"),
        ("CodeAlta.Plugins.Abstractions.Tests/CodeAlta.Plugins.Abstractions.Tests.csproj", "1C6A6DB74088C61EE225ED7EA84D39224F81D48497F7FE699D6C6E55EDAD09D2"),
        ("CodeAlta.Plugins.Abstractions.Tests/PluginAbstractionsTests.cs", "FC7406C8DE572873F1146D9EBC175A166081FADA397B2BDD91BF48D3E9411219"),
        ("CodeAlta.Tests/PluginKeyBindingExtractionSourceTests.cs", "12CC4C7C67A120AA96BE003D8DF6058F1B36AFAF2025A4594B9DCDCD1983B68C"),
        ("CodeAlta.Tests/PluginUiContentExtractionSourceTests.cs", "0D45FF57359D5A6AE0EDA352EF112384793DD451355A510B6B918F85EE11AF44"),
        ("CodeAlta.Tests/PluginSessionEventProjectionSourceTests.cs", "04BD21282B42D19CB1CB3966AC53F4ECB24A5C2B754978E0E180A90030E6CE48"),
        ("CodeAlta.Desktop.Tests/DesktopWorkspaceSourceTests.cs", "CF7D2CC8001A560D5328EA0E9919FA8A25EBE74573365EF53086A6265417E593"),
        ("CodeAlta.Desktop.Tests/DesktopHistorySourceTests.cs", "49DF8BB614A5355D8DA2CF52AB3EA751F7B0FD714C58D05AE640500E5C384994"),
        ("CodeAlta.Desktop.Tests/CodeAlta.Desktop.Tests.csproj", "C6E2EDA7B7178E08AEFB184D5A366569B735D08952E8464DE07A53A999AE70E6"),
        ("CodeAlta.Plugin.GitHub/GitHubIssuePickerDialog.cs", "389562CB69515EB3F14BAB3661BFE4B0239EB46936407F6E7E0D856573CD944C"),
    ];

    private static (string Before, string After)[] Edits(string path) => path switch
    {
        "CodeAlta.Plugins.Abstractions/PluginServices.cs" =>
        [
            ("using XenoAtom.Logging;\nusing XenoAtom.Terminal.UI;\n", "using XenoAtom.Logging;\n"),
            ("public sealed record PluginDialogRequest\n", "public record PluginDialogRequest\n"),
            (DialogContent + InitialTextAnchor, InitialTextAnchor),
            (ShowDialogDeclaration, ShowDialogRemarks + ShowDialogDeclaration),
        ],
        "CodeAlta.Plugins.Abstractions/PluginFactories.cs" =>
        [
            ("using CodeAlta.Agent;\nusing XenoAtom.Terminal.UI;\n", "using CodeAlta.Agent;\n"),
            (CustomFactory + DialogHelperAnchor, DialogHelperAnchor),
        ],
        "CodeAlta.Plugins.Abstractions/PluginDialogLayout.cs" =>
        [("namespace CodeAlta.Plugins.Abstractions;\n", "namespace CodeAlta.Plugins.Tui;\n")],
        "CodeAlta.Plugins.Abstractions/PluginPrompting.cs" =>
        [
            ("using CodeAlta.Agent;\nusing XenoAtom.Terminal.UI;\n", "using CodeAlta.Agent;\n"),
            (PromptVisual + ProjectPathAnchor, ProjectPathAnchor),
        ],
        "CodeAlta.Plugins.Abstractions/PluginContributions.cs" =>
        [("using CodeAlta.Agent;\nusing XenoAtom.Terminal.UI;\n", "using CodeAlta.Agent;\n")],
        "CodeAlta.Plugins.Abstractions/CodeAlta.Plugins.Abstractions.csproj" =>
        [(LoggingPackage + TerminalPackages, LoggingPackage)],
        "CodeAlta.Plugins.Tui/PluginVisualContributions.cs" => [(RendererTail, RendererTail + TuiFactories)],
        "CodeAlta.Tui/Views/ChatPromptEditor.cs" =>
        [
            (AbstractionsUsing, AbstractionsUsing + TuiUsing),
            ("internal sealed class ChatPromptEditor : PromptEditor, IProjectFileReferencePopupHost, IPluginPromptEditorHost\n", "internal sealed class ChatPromptEditor : PromptEditor, IProjectFileReferencePopupHost, IPluginTerminalPromptEditorHost\n"),
            ("    Visual IPluginPromptEditorHost.Visual => this;\n", "    Visual IPluginTerminalPromptEditorHost.Visual => this;\n"),
        ],
        "CodeAlta.Plugin.GitHub/GitHubPlugin.cs" => [(AbstractionsUsing, AbstractionsUsing + TuiUsing), (OldGitHubContribution, NewGitHubContribution)],
        "CodeAlta.Plugin.GitHub/GitHubIssuePromptAttachment.cs" =>
        [
            (AbstractionsUsing, TuiUsing),
            ("    private readonly IPluginPromptEditorHost _host;\n", "    private readonly IPluginTerminalPromptEditorHost _host;\n"),
            ("    public GitHubIssuePromptAttachment(GitHubPlugin plugin, IPluginPromptEditorHost host)\n", "    public GitHubIssuePromptAttachment(GitHubPlugin plugin, IPluginTerminalPromptEditorHost host)\n"),
        ],
        "CodeAlta.Plugin.GitHub/CodeAlta.Plugin.GitHub.csproj" => [(AbstractionsReference, AbstractionsReference + TuiReference)],
        "CodeAlta.Tui/App/DialogBoundsResolver.cs" or "CodeAlta.Tui/Views/ResponsiveDialogSize.cs" => [(AbstractionsUsing, TuiUsing)],
        "CodeAlta.Plugin.Mcp/McpServersDialog.cs" => [(AbstractionsUsing, AbstractionsUsing + TuiUsing)],
        "CodeAlta.Plugin.Statistics/CodeAlta.Plugin.Statistics.csproj" => [(AbstractionsReference, MarkdownPackage + AbstractionsReference)],
        "CodeAlta.Plugins.Abstractions.Tests/CodeAlta.Plugins.Abstractions.Tests.csproj" => [(AbstractionsReference, AbstractionsReference + TuiReference)],
        "CodeAlta.Plugins.Abstractions.Tests/PluginAbstractionsTests.cs" => [("using XenoAtom.Terminal.UI.Controls;\n", TuiUsing + "using XenoAtom.Terminal.UI.Controls;\n")],
        "CodeAlta.Tests/PluginKeyBindingExtractionSourceTests.cs" => [(KeyDecode, KeyDecode + KeyPrelude)],
        "CodeAlta.Tests/PluginUiContentExtractionSourceTests.cs" => [(UiAdapterAnchor, UiPrelude + UiAdapterAnchor)],
        "CodeAlta.Tests/PluginSessionEventProjectionSourceTests.cs" => [(OldProjectionInverse, NewProjectionInverse)],
        "CodeAlta.Desktop.Tests/DesktopWorkspaceSourceTests.cs" => [(OldWorkspaceRead, NewWorkspaceRead)],
        "CodeAlta.Desktop.Tests/DesktopHistorySourceTests.cs" => [(OldHistoryInverse, NewHistoryInverse)],
        "CodeAlta.Desktop.Tests/CodeAlta.Desktop.Tests.csproj" => [(SourceTextLink, SourceTextLink + InverseLink)],
        "CodeAlta.Plugin.GitHub/GitHubIssuePickerDialog.cs" =>
        [("using XenoAtom.Terminal;\nusing XenoAtom.Terminal.Graphics;\nusing XenoAtom.Terminal.UI;\n", "using XenoAtom.Terminal;\nusing XenoAtom.Terminal.UI;\n")],
        _ => throw new AssertFailedException($"No mandatory neutral-contract inverse for {path}."),
    };

    internal const string AbstractionsUsing = "using CodeAlta.Plugins.Abstractions;\n";
    internal const string TuiUsing = "using CodeAlta.Plugins.Tui;\n";
    internal const string AbstractionsReference = "    <ProjectReference Include=\"..\\CodeAlta.Plugins.Abstractions\\CodeAlta.Plugins.Abstractions.csproj\" />\n";
    internal const string TuiReference = "    <ProjectReference Include=\"..\\CodeAlta.Plugins.Tui\\CodeAlta.Plugins.Tui.csproj\" />\n";
    internal const string MarkdownPackage = "    <PackageReference Include=\"XenoAtom.Terminal.UI.Extensions.Markdown\" />\n";
    internal const string LoggingPackage = "    <PackageReference Include=\"XenoAtom.Logging\" />\n";
    internal const string TerminalPackages = "    <PackageReference Include=\"XenoAtom.Terminal.UI\" />\n    <PackageReference Include=\"XenoAtom.Terminal.UI.Extensions.CodeEditor.TextMateSharp\" />\n" + MarkdownPackage + "    <PackageReference Include=\"XenoAtom.Terminal.UI.Extensions.Screenshot\" />\n    <PackageReference Include=\"XenoAtom.Terminal.UI.Graphics\" />\n";
    internal const string DialogContent = "    /// <summary>Gets optional custom dialog content.</summary>\n    public Visual? Content { get; init; }\n\n";
    internal const string InitialTextAnchor = "    /// <summary>Gets initial text for input or editor dialogs.</summary>\n";
    internal const string ShowDialogDeclaration = "    ValueTask ShowDialogAsync(PluginDialogRequest request, CancellationToken cancellationToken = default);\n";
    internal const string ShowDialogRemarks = "    /// <remarks>Unsupported hosts may complete without presenting UI. Completion is not confirmation that the dialog was shown.</remarks>\n";
    internal const string PromptVisual = "    /// <summary>Gets the editor visual, used as an anchor for plugin-owned UI.</summary>\n    Visual Visual { get; }\n\n";
    internal const string ProjectPathAnchor = "    /// <summary>Gets the prompt editor project path, when known.</summary>\n";
    internal const string DialogHelperAnchor = "    private static PluginDialogRequest Dialog(PluginDialogKind kind, string title, string? message)\n";
    internal const string CustomFactory = """
        /// <summary>Creates a custom visual dialog request.</summary>
        public static PluginDialogRequest CustomDialog(string title, Visual content)
        {
            ArgumentNullException.ThrowIfNull(content);
            return new PluginDialogRequest
            {
                Kind = PluginDialogKind.Custom,
                Title = title,
                Content = content,
            };
        }
    """ + "\n\n";
    internal const string RendererTail = "        return new PluginTerminalRendererContribution { Region = region, Target = target, Name = name, Order = order, TerminalRenderer = terminalRenderer, Renderer = renderer };\n    }\n";
    internal const string TuiFactories = "\n" + """
        /// <summary>Creates a custom terminal dialog request, not a guarantee of host presentation.</summary>
        /// <param name="title">The dialog title.</param>
        /// <param name="content">The custom terminal content.</param>
        /// <returns>The terminal request. Unsupported hosts may ignore it or return no result.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="content"/> is null.</exception>
        public static PluginTerminalDialogRequest CustomDialog(string title, Visual content)
        {
            ArgumentNullException.ThrowIfNull(content);
            return new PluginTerminalDialogRequest
            {
                Kind = PluginDialogKind.Custom,
                Title = title,
                Content = content,
            };
        }

        /// <summary>Creates a prompt contribution that attaches only to a terminal prompt host.</summary>
        /// <param name="name">The contribution name.</param>
        /// <param name="attach">The deferred attachment callback.</param>
        /// <param name="placeholderText">Optional guidance for hosts where the contribution applies.</param>
        /// <param name="order">The ordering hint.</param>
        /// <returns>A neutral contribution whose attachment is declined on unsupported hosts.</returns>
        /// <remarks>
        /// A null attachment does not advertise a supported action. The caller owns any returned attachment;
        /// this factory does not dispose it. Native null results and exceptions never invoke a fallback.
        /// The returned attach handler rejects a null host before checking terminal support.
        /// </remarks>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="name"/> or <paramref name="attach"/> is null, or the returned handler receives a null host.</exception>
        /// <exception cref="ArgumentException">Thrown when <paramref name="name"/> is empty or whitespace.</exception>
        public static PluginPromptEditorContribution PromptEditor(string name, Func<IPluginTerminalPromptEditorHost, IAsyncDisposable?> attach, string? placeholderText = null, int order = 0)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            ArgumentNullException.ThrowIfNull(attach);
            return new PluginPromptEditorContribution
            {
                Name = name,
                PlaceholderText = placeholderText,
                Order = order,
                Attach = host =>
                {
                    ArgumentNullException.ThrowIfNull(host);
                    return host is IPluginTerminalPromptEditorHost terminal ? attach(terminal) : null;
                },
            };
        }
    """ + "\n";
    internal const string OldGitHubContribution = """
            yield return new PluginPromptEditorContribution
            {
                Name = "GitHub issue prompt picker",
                PlaceholderText = "[#] to reference a GitHub issue",
                Attach = host => new GitHubIssuePromptAttachment(this, host),
            };
    """ + "\n";
    internal const string NewGitHubContribution = """
            yield return PluginTui.PromptEditor(
                "GitHub issue prompt picker",
                host => new GitHubIssuePromptAttachment(this, host),
                "[#] to reference a GitHub issue");
    """ + "\n";
    internal const string KeyDecode = "        source = SourceTestText.DecodeSource(Encoding.UTF8.GetBytes(source));\n";
    internal const string KeyPrelude = "        if (path is \"CodeAlta.Plugins.Abstractions/PluginContributions.cs\" or \"CodeAlta.Tests/PluginUiContentExtractionSourceTests.cs\")\n            source = PluginNeutralContractSourceInverse.Restore(path, source);\n";
    internal const string UiAdapterAnchor = "                if (path == \"CodeAlta.Plugins/PluginContributionAdapters.cs\")\n";
    internal const string UiPrelude = "                if (path == \"CodeAlta.Plugins.Abstractions/PluginFactories.cs\")\n                    canonical = PluginNeutralContractSourceInverse.Restore(path, canonical);\n";
    internal const string OldProjectionInverse = "                return Undo(source, NewProjectReference, OldProjectReference);\n";
    internal const string NewProjectionInverse = "                return Undo(PluginNeutralContractSourceInverse.Restore(path, source), NewProjectReference, OldProjectReference);\n";
    internal const string OldWorkspaceRead = "    private static string Read(string path) => DesktopHistorySourceTests.RestoreWorkspaceSource(path, SourceTestText.DecodeSource(File.ReadAllBytes(Path.Combine(DesktopArchitectureTests.SourceRoot, path))));\n";
    internal const string NewWorkspaceRead = """
        private static string Read(string path)
        {
            var source = SourceTestText.DecodeSource(File.ReadAllBytes(Path.Combine(DesktopArchitectureTests.SourceRoot, path)));
            if (path is "CodeAlta.Tests/PluginKeyBindingExtractionSourceTests.cs" or "CodeAlta.Tests/PluginUiContentExtractionSourceTests.cs" or "CodeAlta.Desktop.Tests/CodeAlta.Desktop.Tests.csproj")
                source = PluginNeutralContractSourceInverse.Restore(path, source);
            return DesktopHistorySourceTests.RestoreWorkspaceSource(path, source);
        }
    """ + "\n";
    internal const string OldHistoryInverse = "            \"CodeAlta.Desktop.Tests/DesktopWorkspaceSourceTests.cs\" => Undo(source, NewSourceRead, OldSourceRead),\n";
    internal const string NewHistoryInverse = "            \"CodeAlta.Desktop.Tests/DesktopWorkspaceSourceTests.cs\" => Undo(PluginNeutralContractSourceInverse.Restore(path, source), NewSourceRead, OldSourceRead),\n";
    internal const string SourceTextLink = "    <Compile Include=\"../CodeAlta.Tests/SourceTestText.cs\" Link=\"SourceTestText.cs\" />\n";
    internal const string InverseLink = "    <Compile Include=\"../CodeAlta.Tests/PluginNeutralContractSourceInverse.cs\" Link=\"PluginNeutralContractSourceInverse.cs\" />\n";
}
