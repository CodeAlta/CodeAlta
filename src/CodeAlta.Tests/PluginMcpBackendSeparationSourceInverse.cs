using System.Security.Cryptography;
using System.Text;

namespace CodeAlta.Tests;

/// <summary>Mandatory whole-source inverses of parent-supplied 60a18cd; no production or filesystem calls.</summary>
internal static class PluginMcpBackendSeparationSourceInverse
{
    internal static string CurrentPath(string path) => path switch
    {
        "CodeAlta.Plugin.Mcp/McpServersDialog.cs" => "CodeAlta.Tui/Plugins/Mcp/McpServersDialog.cs",
        "CodeAlta.Plugin.Mcp/TerminalIcons.cs" => "CodeAlta.Tui/Plugins/Mcp/TerminalIcons.cs",
        _ => path,
    };

    // Each entry point owns a disjoint newest-step map; existing older forwarding remains mandatory.
    internal static string RestoreGitHubInput(string path, string source) => path is
        "CodeAlta.Tui/App/CodeAltaBuiltInPlugins.cs" or "CodeAlta.Tests/PluginAuthoringProfileSourceInverse.cs" or
        "CodeAlta.Tests/PluginNeutralContractSourceInverse.cs" or "CodeAlta.Tests/PluginNeutralContractSourceTests.cs" or
        "CodeAlta.Desktop.Tests/CodeAlta.Desktop.Tests.csproj"
        ? Restore(path, source) : source;

    internal static string RestoreProfileInput(string path, string source) => path is
        "CodeAlta.Tests/PluginUiContentExtractionSourceTests.cs"
        ? Restore(path, source) : source;

    internal static string RestoreNeutralInput(string path, string source) => path is
        "CodeAlta.Tests/PluginKeyBindingExtractionSourceTests.cs" or "CodeAlta.Plugin.Mcp/McpServersDialog.cs"
        ? Restore(path, source) : source;

    internal static string RestoreKeyBindingInput(string path, string source) => path is
        "CodeAlta.Plugin.Mcp/McpPlugin.cs"
        ? Restore(path, source) : source;

    internal static string RestoreUiContentInput(string path, string source) => path is
        "CodeAlta.Plugin.Mcp/CodeAlta.Plugin.Mcp.csproj" or "CodeAlta.Tests/McpConfigTests.cs"
        ? Restore(path, source) : source;

    internal static string Restore(string path, string source)
    {
        source = PluginAgentEventOwnershipSourceInverse.RestoreMcpInput(path, source);
        source = SessionDiscoveryScopeSourceInverse.RestoreMcpInput(path, source);
        source = SourceTestText.DecodeSource(new UTF8Encoding(false, true).GetBytes(source));
        source = PluginStatisticsBackendSeparationSourceInverse.RestoreMcpInput(path, source);
        foreach (var (before, after) in Edits(path))
        {
            var expected = SourceTestText.Canonicalize(after);
            Assert.IsTrue(expected.Length > 0, path);
            Assert.AreEqual(1, source.Split(expected, StringSplitOptions.None).Length - 1, path + ": " + expected);
            source = source.Replace(expected, SourceTestText.Canonicalize(before), StringComparison.Ordinal);
        }
        source = SourceTestText.DecodeSource(new UTF8Encoding(false, true).GetBytes(source));
        Assert.AreEqual(Originals.Single(item => item.Path == path).Hash, Hash(source), path);
        return source;
    }

    internal static string Hash(string source) => Convert.ToHexString(SHA256.HashData(new UTF8Encoding(false, true).GetBytes(source)));

    internal static IReadOnlyList<(string Path, string Hash)> Originals =>
    [
        ("CodeAlta.Plugin.Mcp/McpPlugin.cs", "E4B538212CC6B7F7CEA93667188439F03BAB2F7471360D66056EC75F2FC55BB2"),
        ("CodeAlta.Plugin.Mcp/CodeAlta.Plugin.Mcp.csproj", "7F022CCE1A91DF56B5A205E738907D62C9FF47DBEDCBA32DC91C5BEADA81BBE0"),
        ("CodeAlta.Plugin.Mcp/McpServersDialog.cs", "F64A4256299132FB63B7EA4191B7DA083A908FFF85444C59BBC4DA737D7DA1C8"),
        ("CodeAlta.Plugin.Mcp/TerminalIcons.cs", "0A3837F1DA26B1E545C68BA469FD1C67250F54B6BD0678CA32D592B77A0AC39B"),
        ("CodeAlta.Tui/App/CodeAltaBuiltInPlugins.cs", "B1AE2B6FA1128ADDA7E6B086A8967B2208F67A6E1F6E21A4E02013296118C4A5"),
        ("CodeAlta.Tests/McpConfigTests.cs", "B7D0610B9717DBB2BCC6149022844AB20C444D4EEAC58B6D6FC56049BDE5657B"),
        ("CodeAlta.Tests/PluginUiContentExtractionSourceTests.cs", "D6A1AD22B4BB42E9FF9B6D8E9BF2476B8201D3DA5CBADF2094F8EC2DCC02E4A8"),
        ("CodeAlta.Tests/PluginKeyBindingExtractionSourceTests.cs", "7A4921A67FA87F8A21D404CDD4E4B964276E5F01311DA57DE546FE4D8453BAED"),
        ("CodeAlta.Tests/PluginAuthoringProfileSourceInverse.cs", "C4D276B86CDB4F46FB9015963831F4ED83143B56388E0FC863366A580CE8CB6D"),
        ("CodeAlta.Tests/PluginGitHubBackendSeparationSourceInverse.cs", "5FD98D12D26AFB6DB4C7523B588D24FC6D5D198BE381186FEC5C83EDC93333F4"),
        ("CodeAlta.Tests/PluginGitHubBackendSeparationSourceTests.cs", "449708701558205C11520A24B1C384F89026B3797908DA55E14739027A0A293B"),
        ("CodeAlta.Tests/PluginNeutralContractSourceInverse.cs", "8625BFC67075CA2956BC41FD4099307221BC28A702C0EC30CE053708D0AE196C"),
        ("CodeAlta.Tests/PluginNeutralContractSourceTests.cs", "1C453F562439CDA8290965122271FC35450B680F4DF83C5475D6C3BC2BCFAE46"),
        ("CodeAlta.Desktop.Tests/CodeAlta.Desktop.Tests.csproj", "AFC7AD1E87ECC91051D54424A3DBB897642E54016BE57DF7FBC0B93A20E944CF"),
    ];

    private static IReadOnlyList<(string Before, string After)> Edits(string path) => path switch
    {
        "CodeAlta.Plugin.Mcp/McpPlugin.cs" =>
        [
            (OldUsings, NewUsings),
            (OldConstruction, NewConstruction),
            (OldCommands, NewCommands),
            (OldStatus, NewStatus),
            (OldNative + LabelSignature, LabelSignature),
            (OldMarkup + ToolLabelSignature, ToolLabelSignature),
            (OldToolMarkup + OldDiscoverySignature, NewDiscoverySignature),
            (OldSnapshotSignature, NewSnapshotSignature),
        ],
        "CodeAlta.Plugin.Mcp/CodeAlta.Plugin.Mcp.csproj" =>
        [
            (TomlynReference + TerminalPackage, TomlynReference),
            (AbstractionsReference + TerminalReference, AbstractionsReference),
            (TestsFriend, TestsFriend + TuiFriend),
        ],
        "CodeAlta.Plugin.Mcp/McpServersDialog.cs" or "CodeAlta.Plugin.Mcp/TerminalIcons.cs" => [],
        "CodeAlta.Tui/App/CodeAltaBuiltInPlugins.cs" => [(OldRegistration, NewRegistration)],
        "CodeAlta.Tests/McpConfigTests.cs" =>
        [
            (OldRichTest, NewRichTest),
            ("        var visual = McpPlugin.CreateStatusIndicator(\n", "        var visual = McpTerminalContributions.CreateStatusIndicator(\n"),
            ("        var pendingVisual = McpPlugin.CreateStatusIndicator(CreateVisualContext(project.Path, \"session-a\"), new McpManagementService(), activationState, statusRevision)!;\n", "        var pendingVisual = McpTerminalContributions.CreateStatusIndicator(CreateVisualContext(project.Path, \"session-a\"), new McpManagementService(), activationState, statusRevision)!;\n"),
            ("        var activeVisual = McpPlugin.CreateStatusIndicator(CreateVisualContext(project.Path, \"session-a\"), new McpManagementService(), activationState, statusRevision)!;\n", "        var activeVisual = McpTerminalContributions.CreateStatusIndicator(CreateVisualContext(project.Path, \"session-a\"), new McpManagementService(), activationState, statusRevision)!;\n"),
        ],
        "CodeAlta.Tests/PluginUiContentExtractionSourceTests.cs" => [(OldUiRead, NewUiRead)],
        "CodeAlta.Tests/PluginKeyBindingExtractionSourceTests.cs" => [(KeyPrelude, KeyPrelude + KeyHook)],
        "CodeAlta.Tests/PluginAuthoringProfileSourceInverse.cs" => [(ProfilePrelude, ProfilePrelude + ProfileHook)],
        "CodeAlta.Tests/PluginGitHubBackendSeparationSourceInverse.cs" => [(GitHubPrelude, GitHubPrelude + GitHubHook)],
        "CodeAlta.Tests/PluginGitHubBackendSeparationSourceTests.cs" => [(OldGitHubAssertion, NewGitHubAssertion)],
        "CodeAlta.Tests/PluginNeutralContractSourceInverse.cs" => [(NeutralPrelude, NeutralPrelude + NeutralHook)],
        "CodeAlta.Tests/PluginNeutralContractSourceTests.cs" =>
        [
            (OldNativeAssertion, NewNativeAssertion),
            (OldPackages, NewPackages),
            (OldNeutralPath, NewNeutralPath),
        ],
        "CodeAlta.Desktop.Tests/CodeAlta.Desktop.Tests.csproj" => [(GitHubLink, GitHubLink + McpLink)],
        _ => throw new AssertFailedException("No MCP backend inverse for " + path),
    };

    internal const string OldUsings = "using CodeAlta.Plugins.Abstractions;\nusing CodeAlta.Plugins.Tui;\nusing XenoAtom.Terminal;\nusing XenoAtom.Terminal.UI;\nusing CodeAlta.Plugin.Mcp;\n\nusing XenoAtom.Terminal.UI.Controls;\nusing XenoAtom.Terminal.UI.Input;\nusing XenoAtom.Terminal.UI.Styling;\n";
    internal const string NewUsings = "using CodeAlta.Plugins.Abstractions;\n";
    internal const string Owners = "    private readonly McpActivationState _activationState = new();\n    private readonly McpManagementService _managementService = new();\n";
    internal const string Binding = "    private static readonly PluginKeyBinding ManageServersKeyBinding = new(\n        new PluginKeyGesture('G', PluginKeyModifiers.Ctrl),\n        new PluginKeyGesture('Y', PluginKeyModifiers.Ctrl));\n";
    internal const string OldConstruction = Owners + "    private readonly State<int> _statusRevision = new(0);\n\n" + Binding + "\n" + "    /// <summary>\n    /// Initializes a new instance of the <see cref=\"McpPlugin\"/> class.\n    /// </summary>\n    public McpPlugin()\n    {\n        _activationState.Changed += _ => IncrementStatusRevision();\n    }\n";
    internal const string NewConstruction = Owners + """
        private readonly McpPluginPresentation? _presentation;

        /// <summary>Initializes an MCP backend without terminal presentation.</summary>
        public McpPlugin()
        {
        }

        // The terminal host borrows these exact owners; presentation construction precedes contribution enumeration.
        internal McpPlugin(Func<McpManagementService, McpActivationState, McpPluginPresentation> createPresentation)
        {
            ArgumentNullException.ThrowIfNull(createPresentation);
            _presentation = createPresentation(_managementService, _activationState)
                ?? throw new InvalidOperationException("The MCP presentation factory returned null.");
        }
    """ + "\n";
    internal const string OldCommands = "    public override IEnumerable<PluginCommandContribution> GetCommands()\n" + CommandBody;
    internal const string TerminalCommands = "    private IEnumerable<PluginCommandContribution> CreateCommands()\n" + CommandBody;
    private const string CommandBody = """
        {
            yield return new PluginCommandContribution
            {
                Name = "mcp",
                Label = "MCP Servers",
                Description = "Inspect and manage configured Model Context Protocol servers.",
                Placement = PluginCommandPlacement.ShellRoot | PluginCommandPlacement.PromptEditor | PluginCommandPlacement.WorkspaceRoot,
                SearchText = "model context protocol servers tools",
                KeyBinding = ManageServersKeyBinding,
                Availability = PluginCommandAvailability.InteractiveUi,
                Handler = (context, cancellationToken) =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ShowManagementDialog(context, _managementService);
                    return new ValueTask<PluginCommandResult>(PluginCommandResult.Handled);
                },
            };
        }
    """ + "\n";
    internal const string NewCommands = """
        public override IEnumerable<PluginCommandContribution> GetCommands()
        {
            if (_presentation is null)
            {
                yield break;
            }

            foreach (var contribution in _presentation.CreateCommands())
            {
                yield return contribution;
            }
        }
    """ + "\n";
    internal const string OldStatus = "    public override IEnumerable<PluginUiContribution> GetUiContributions()\n    {\n        yield return new PluginVisualContribution\n        {\n" + StatusMetadata + "            CreateVisual = context => CreateStatusIndicator(context, _managementService, _activationState, _statusRevision),\n" + NeutralContent + "        };\n    }\n";
    internal const string NewStatus = "    public override IEnumerable<PluginUiContribution> GetUiContributions()\n    {\n        var status = new PluginContentContribution\n        {\n" + StatusMetadata + NeutralContent + "        };\n        yield return _presentation is null ? status : _presentation.DecorateStatus(status);\n    }\n";
    private const string StatusMetadata = "            Region = PluginUiRegion.SessionStatus,\n            Name = \"mcp-status\",\n            Order = 100,\n";
    internal const string NeutralContent = """
                CreateContent = context =>
                {
                    var projectPath = ResolveProjectPath(context.ProjectPath, context.Services.Workspace.SelectedProjectPath, null);
                    var snapshot = ResolveStatusSnapshot(_managementService, projectPath);
                    if (!snapshot.Summary.HasConfiguration && snapshot.Summary.ConfiguredServerCount == 0 && snapshot.Summary.InvalidSourceCount == 0)
                    {
                        return null;
                    }

                    var activationScope = ResolveActivationScopeKey(context, projectPath);
                    return new PluginRenderResult
                    {
                        Text = CreateStatusLabel(snapshot, _activationState.GetToolCounts(activationScope), _activationState.GetActiveServers(activationScope)),
                    };
                },
    """ + "\n";
    internal const string DialogAndIndicator = """
        private static void ShowManagementDialog(PluginOperationContext context, McpManagementService managementService, Visual? focusTarget = null)
        {
            var projectPath = ResolveProjectPath(context.ProjectPath, context.Services.Workspace.SelectedProjectPath, null);
            new McpServersDialog(
                managementService,
                () => new McpManagementRequest { ProjectDirectory = projectPath },
                static (_, _) => Task.CompletedTask,
                () => PluginDialogLayout.ResolveDialogBounds(focusTarget),
                () => focusTarget)
                .Show();
        }

        internal static Visual? CreateStatusIndicator(PluginVisualContext context, McpManagementService managementService, McpActivationState activationState, State<int> statusRevision)
        {
            ArgumentNullException.ThrowIfNull(context);
            ArgumentNullException.ThrowIfNull(managementService);
            ArgumentNullException.ThrowIfNull(activationState);
            ArgumentNullException.ThrowIfNull(statusRevision);

            var projectPath = ResolveProjectPath(context.ProjectPath, context.Services.Workspace.SelectedProjectPath, null);
            var snapshot = ResolveStatusSnapshot(managementService, projectPath);
            if (!snapshot.Summary.HasConfiguration && snapshot.Summary.ConfiguredServerCount == 0 && snapshot.Summary.InvalidSourceCount == 0)
            {
                return null;
            }

            var activationScope = ResolveActivationScopeKey(context, projectPath);
            var button = new Button(new Markup(() =>
                {
                    _ = statusRevision.Value;
                    return CreateStatusVisualState(managementService, activationState, activationScope, projectPath).Markup;
                })
                {
                    Wrap = false,
                    IsSelectable = false,
                })
                .Tone(() =>
                {
                    _ = statusRevision.Value;
                    return CreateStatusVisualState(managementService, activationState, activationScope, projectPath).Tone;
                });
            button.Click(() => ShowManagementDialog(context, managementService, button));
            return button;
        }

    """ + "\n";
    internal const string OldRevision = """
        private void IncrementStatusRevision()
        {
            var dispatcher = _statusRevision.Dispatcher;
            if (dispatcher.CheckAccess())
            {
                _statusRevision.Value++;
                return;
            }

            try
            {
                dispatcher.Post(() => _statusRevision.Value++);
            }
            catch (InvalidOperationException)
            {
                // No interactive TerminalApp is attached. The in-memory activation state is still current,
                // and the next UI composition will read it directly.
            }
        }

    """ + "\n";
    internal const string NativeState = """
        private static McpStatusVisualState CreateStatusVisualState(
            McpManagementService managementService,
            McpActivationState activationState,
            string activationScope,
            string? projectPath)
        {
            var currentSnapshot = ResolveStatusSnapshot(managementService, projectPath);
            var activeServers = activationState.GetActiveServers(activationScope);
            return new McpStatusVisualState(
                CreateStatusMarkup(currentSnapshot, activationState.GetToolCounts(activationScope), activeServers),
                currentSnapshot.Summary.UnavailableServerCount > 0 ? ControlTone.Warning : ControlTone.Default);
        }

        private readonly record struct McpStatusVisualState(string Markup, ControlTone Tone);

    """ + "\n";
    internal const string OldNative = DialogAndIndicator + OldRevision + NativeState;
    internal const string LabelSignature = "    internal static string CreateStatusLabel(\n";
    internal const string ToolLabelSignature = "    private static string CreateStatusToolLabel(\n";
    internal const string OldMarkup = """
        internal static string CreateStatusMarkup(
            McpManagementSnapshot snapshot,
            IReadOnlyDictionary<string, int> activatedToolCounts,
            IReadOnlyCollection<string> activeServers)
        {
            ArgumentNullException.ThrowIfNull(snapshot);
            ArgumentNullException.ThrowIfNull(activatedToolCounts);
            ArgumentNullException.ThrowIfNull(activeServers);

            var summary = snapshot.Summary;
            var serverTone = summary.ConfiguredServerCount > 0 ? "success" : "muted";
            var activeServerTone = summary.ActiveServerCount > 0 ? serverTone : "muted";
            var builder = new StringBuilder();
            builder.Append('[')
                .Append(serverTone)
                .Append(']')
                .Append(McpTerminalIcons.MdServerNetwork)
                .Append(" MCP[/] [")
                .Append(activeServerTone)
                .Append(']')
                .Append(summary.ActiveServerCount)
                .Append("[/][muted]/")
                .Append(summary.ConfiguredServerCount)
                .Append("[/]");
            if (summary.UnavailableServerCount > 0)
            {
                builder.Append(" · [warning]")
                    .Append(summary.UnavailableServerCount)
                    .Append(" unavailable[/]");
            }

            builder.Append(" · ")
                .Append(CreateStatusToolMarkup(snapshot, activatedToolCounts, activeServers));
            return builder.ToString();
        }

    """ + "\n";
    internal const string OldToolMarkup = """
        private static string CreateStatusToolMarkup(
            McpManagementSnapshot snapshot,
            IReadOnlyDictionary<string, int> activatedToolCounts,
            IReadOnlyCollection<string> activeServers)
        {
            var summary = snapshot.Summary;
            if (summary.TotalToolCount > 0 || HasCompletedManagementToolDiscovery(snapshot))
            {
                return $"tools [accent]{summary.ExposedToolCount}[/][muted]/{summary.TotalToolCount}[/]";
            }

            var loadedActiveServerCount = activeServers.Count(server => activatedToolCounts.ContainsKey(server));
            if (loadedActiveServerCount > 0)
            {
                return $"active tools [accent]{activeServers.Sum(server => activatedToolCounts.TryGetValue(server, out var count) ? count : 0)}[/]";
            }

            return activeServers.Count > 0 ? "tools [warning]pending[/]" : "tools [muted]not loaded[/]";
        }

    """ + "\n";
    internal const string OldDiscoverySignature = "    private static bool HasCompletedManagementToolDiscovery(McpManagementSnapshot snapshot)\n";
    internal const string NewDiscoverySignature = "    internal static bool HasCompletedManagementToolDiscovery(McpManagementSnapshot snapshot)\n";
    internal const string OldSnapshotSignature = "    private static McpManagementSnapshot ResolveStatusSnapshot(McpManagementService managementService, string? projectPath)\n";
    internal const string NewSnapshotSignature = "    internal static McpManagementSnapshot ResolveStatusSnapshot(McpManagementService managementService, string? projectPath)\n";
    internal const string TomlynReference = "    <PackageReference Include=\"Tomlyn\" />\n";
    internal const string TerminalPackage = "    <PackageReference Include=\"XenoAtom.Terminal.UI\" />\n";
    internal const string AbstractionsReference = "    <ProjectReference Include=\"..\\CodeAlta.Plugins.Abstractions\\CodeAlta.Plugins.Abstractions.csproj\" />\n";
    internal const string TerminalReference = "    <ProjectReference Include=\"..\\CodeAlta.Plugins.Tui\\CodeAlta.Plugins.Tui.csproj\" />\n";
    internal const string TestsFriend = "    <InternalsVisibleTo Include=\"CodeAlta.Tests\" />\n";
    internal const string TuiFriend = "    <InternalsVisibleTo Include=\"altatui\" />\n";
    internal const string OldRegistration = "            PluginType = typeof(McpPlugin),\n            Factory = static () => new McpPlugin(),\n";
    internal const string NewRegistration = "            PluginType = typeof(McpPlugin),\n            Factory = static () => new McpPlugin(McpTerminalContributions.CreatePresentation),\n";
    internal const string OldRichTest = "        var plugin = new McpPlugin();\n        var statusContribution = plugin.GetUiContributions().OfType<PluginVisualContribution>().Single();\n";
    internal const string NewRichTest = "        var plugin = new McpPlugin(McpTerminalContributions.CreatePresentation);\n        var statusContribution = plugin.GetUiContributions().OfType<PluginVisualContribution>().Single();\n";
    internal const string OldUiRead = "    private static string Read(string path)\n        => PluginAuthoringProfileSourceInverse.RestoreUiContentInput(path, SourceTestText.DecodeSource(File.ReadAllBytes(SourcePath(path))));\n";
    internal const string NewUiRead = "    private static string Read(string path)\n        => PluginAuthoringProfileSourceInverse.RestoreUiContentInput(path, PluginMcpBackendSeparationSourceInverse.RestoreUiContentInput(path, SourceTestText.DecodeSource(File.ReadAllBytes(SourcePath(path)))));\n";
    internal const string KeyPrelude = "        source = SourceTestText.DecodeSource(Encoding.UTF8.GetBytes(source));\n        if (path is \"CodeAlta.Plugins.Abstractions/PluginContributions.cs\" or \"CodeAlta.Tests/PluginUiContentExtractionSourceTests.cs\")\n            source = PluginNeutralContractSourceInverse.Restore(path, source);\n";
    internal const string KeyHook = "        source = PluginMcpBackendSeparationSourceInverse.RestoreKeyBindingInput(path, source);\n";
    internal const string ProfilePrelude = "        source = SourceTestText.DecodeSource(new UTF8Encoding(false, true).GetBytes(source));\n        source = PluginGitHubBackendSeparationSourceInverse.RestoreProfileInput(path, source);\n";
    internal const string ProfileHook = "        source = PluginMcpBackendSeparationSourceInverse.RestoreProfileInput(path, source);\n";
    internal const string GitHubPrelude = "    internal static string Restore(string path, string source)\n    {\n        source = SourceTestText.DecodeSource(new UTF8Encoding(false, true).GetBytes(source));\n";
    internal const string GitHubHook = "        source = PluginMcpBackendSeparationSourceInverse.RestoreGitHubInput(path, source);\n";
    internal const string NeutralPrelude = "        source = SourceTestText.DecodeSource(new UTF8Encoding(false, true).GetBytes(source));\n        source = PluginGitHubBackendSeparationSourceInverse.RestoreNeutralInput(path, source);\n";
    internal const string NeutralHook = "        source = PluginMcpBackendSeparationSourceInverse.RestoreNeutralInput(path, source);\n";
    internal const string OldGitHubAssertion = "        RequireOnce(registration, \"            Factory = static () => new McpPlugin(),\\n\");\n";
    internal const string NewGitHubAssertion = "        RequireOnce(PluginMcpBackendSeparationSourceInverse.Restore(\"CodeAlta.Tui/App/CodeAltaBuiltInPlugins.cs\", registration), \"            Factory = static () => new McpPlugin(),\\n\");\n";
    internal const string OldNativeAssertion = "        RequireOnce(Read(\"CodeAlta.Plugin.Mcp/McpPlugin.cs\"), \"            () => PluginDialogLayout.ResolveDialogBounds(focusTarget),\\n\");\n";
    internal const string NewNativeAssertion = "        RequireOnce(Read(\"CodeAlta.Tui/Plugins/Mcp/McpTerminalContributions.cs\"), \"            () => PluginDialogLayout.ResolveDialogBounds(focusTarget),\\n\");\n";
    internal const string OldPackages = "        foreach (var path in new[] { \"CodeAlta.Plugins.Tui/CodeAlta.Plugins.Tui.csproj\", \"CodeAlta.Plugin.Mcp/CodeAlta.Plugin.Mcp.csproj\" })\n            Assert.IsTrue(References(Project(path), \"PackageReference\").Contains(\"XenoAtom.Terminal.UI\"), path);\n";
    internal const string NewPackages = "        Assert.IsTrue(References(Project(\"CodeAlta.Plugins.Tui/CodeAlta.Plugins.Tui.csproj\"), \"PackageReference\").Contains(\"XenoAtom.Terminal.UI\"));\n        Assert.IsFalse(References(Project(\"CodeAlta.Plugin.Mcp/CodeAlta.Plugin.Mcp.csproj\"), \"PackageReference\").Any(name => name.StartsWith(\"XenoAtom.Terminal\", StringComparison.Ordinal)));\n";
    internal const string OldNeutralPath = "        return Path.Combine(directory.FullName, PluginGitHubBackendSeparationSourceInverse.CurrentPath(path));\n";
    internal const string NewNeutralPath = "        return Path.Combine(directory.FullName, PluginMcpBackendSeparationSourceInverse.CurrentPath(PluginGitHubBackendSeparationSourceInverse.CurrentPath(path)));\n";
    internal const string GitHubLink = "    <Compile Include=\"../CodeAlta.Tests/PluginGitHubBackendSeparationSourceInverse.cs\" Link=\"PluginGitHubBackendSeparationSourceInverse.cs\" />\n";
    internal const string McpLink = "    <Compile Include=\"../CodeAlta.Tests/PluginMcpBackendSeparationSourceInverse.cs\" Link=\"PluginMcpBackendSeparationSourceInverse.cs\" />\n";
}
