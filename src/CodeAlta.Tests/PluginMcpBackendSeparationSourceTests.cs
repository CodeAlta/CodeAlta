using System.Xml.Linq;
using Inverse = CodeAlta.Tests.PluginMcpBackendSeparationSourceInverse;

namespace CodeAlta.Tests;

/// <summary>Explicit named source reads and historical inverses only; not native/configuration/runtime qualification.</summary>
[TestClass]
public sealed class PluginMcpBackendSeparationSourceTests
{
    [TestMethod]
    public void Composition_TuiCreatesOnePresentationFromOwnedBackendServices()
    {
        var backend = Read("CodeAlta.Plugin.Mcp/McpPlugin.cs");
        RequireOnce(backend, Inverse.NewConstruction);
        RequireOnce(backend, Inverse.NewCommands);
        RequireOnce(backend, Inverse.NewStatus);
        RequireOnce(Read("CodeAlta.Tui/App/CodeAltaBuiltInPlugins.cs"), Inverse.NewRegistration);
        Assert.AreEqual(SourceTestText.Canonicalize(PresentationContract), Read("CodeAlta.Plugin.Mcp/McpPluginPresentation.cs"));
        var terminal = Read(TerminalPath);
        RequireOnce(terminal, TerminalConstruction);
        Assert.IsFalse(terminal.Contains("new McpPlugin(", StringComparison.Ordinal));
        Assert.IsFalse(terminal.Contains("new McpManagementService(", StringComparison.Ordinal));
        Assert.IsFalse(terminal.Contains("new McpActivationState(", StringComparison.Ordinal));
        Assert.IsFalse(terminal.Contains("Dispose", StringComparison.Ordinal));
        RequireOnce(Read("CodeAlta.Plugins.Abstractions/PluginBase.cs"), "    private PluginRuntimeContext? _context;\n");
        RequireOnce(Read("CodeAlta.Plugin.Mcp/McpManagementService.cs"), "    private McpManagementSnapshot? _cachedSnapshot;\n    private McpManagementRequest _lastRequest = new();\n");
        RequireOnce(Read("CodeAlta.Plugins/PluginRuntimeManager.cs"), "BuiltInFactory = builtIn.Factory");
        RequireOnce(Read("CodeAlta.Plugins/PluginRuntimeLifecycle.cs"), "instance = CreateInstance(options.BuiltInFactory, () => (PluginBase?)Activator.CreateInstance(discoveredType.Type));");
        RequireOnce(Read("CodeAlta.Plugins/BuiltInPlugins.cs"), "    public required Func<PluginBase> Factory { get; init; }\n");
    }

    [TestMethod]
    public void Commands_PreserveMetadataAndDeferredDialogInvocation()
    {
        var terminal = Read(TerminalPath);
        RequireOnce(terminal, Inverse.Binding);
        RequireOnce(terminal, Inverse.TerminalCommands);
        RequireOnce(terminal, Inverse.DialogAndIndicator);
        var backend = Read("CodeAlta.Plugin.Mcp/McpPlugin.cs");
        RequireOnce(backend, "            CreateCommandNode = context => McpCommandFactory.CreateCommand(context, _activationState),\n");
        RequireOnce(backend, "        var snapshot = new McpManagementService().RefreshSnapshot(new McpManagementRequest { ProjectDirectory = projectPath });\n");
        // The complete backend inverse also checks every untouched command, prompt, runtime and agent-tool body.
        Inverse.Restore("CodeAlta.Plugin.Mcp/McpPlugin.cs", backend);
    }

    [TestMethod]
    public void Status_PreservesNeutralFallbackAndSnapshotReadOrder()
    {
        var backend = Read("CodeAlta.Plugin.Mcp/McpPlugin.cs");
        var terminal = Read(TerminalPath);
        RequireOnce(backend, Inverse.NeutralContent);
        RequireOnce(backend, Inverse.NewSnapshotSignature);
        RequireOnce(backend, Inverse.NewDiscoverySignature);
        RequireOnce(terminal, Inverse.DialogAndIndicator);
        RequireOnce(terminal, Inverse.NativeState);
        RequireOnce(terminal, Inverse.OldMarkup);
        RequireOnce(terminal, Inverse.OldToolMarkup);
        RequireOnce(terminal, Decorator);
        // Exact native bodies above preserve initial visibility resolution before controls, and independent
        // later markup/tone snapshot -> active servers -> counts reads. Neutral content retains counts -> servers.
        Assert.IsFalse(terminal.Contains("CreateStatusLabel(", StringComparison.Ordinal));
        Assert.IsFalse(terminal.Contains(".CreateContent(", StringComparison.Ordinal));
        Assert.IsFalse(terminal.Contains("RenderTarget", StringComparison.Ordinal));
        RequireOnce(Read("CodeAlta.Plugins.Abstractions/PluginUiResourcesCompaction.cs"), "    public required Func<PluginVisualContext, PluginRenderResult?> CreateContent { get; init; }\n");
        RequireOnce(Read("CodeAlta.Plugins.Tui/PluginVisualContributions.cs"), "public sealed record PluginVisualContribution : PluginContentContribution\n");
        RequireOnce(Read("CodeAlta.Plugins.Abstractions/PluginContributions.cs"), "public sealed record PluginCommandContribution\n");
        RequireOnce(Read("CodeAlta.Plugins/PluginContributionAdapters.cs"), "        => GetRegistrations(point, options).Where(static registration => registration.Contribution is TContribution).ToArray();\n");
        RequireOnce(Read("CodeAlta.Tui/Plugins/TerminalPluginContributionAdapter.cs"), "        return supportsTerminal && hasTerminalPresentation ? direct ?? createNative?.Invoke(createContext()) : createPortable(createContext());\n");
        Assert.IsTrue(Read("CodeAlta.Tui/App/PluginFrontendBridge.cs").Contains("SupportsTerminalVisuals = true", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Revision_MovesStateAndSynchronousSubscriptionWithDispatchPolicy()
    {
        var backend = Read("CodeAlta.Plugin.Mcp/McpPlugin.cs");
        Assert.IsFalse(backend.Contains("_statusRevision", StringComparison.Ordinal));
        Assert.IsFalse(backend.Contains(".Changed +=", StringComparison.Ordinal));
        var terminal = Read(TerminalPath);
        RequireOnce(terminal, "    private readonly State<int> _statusRevision = new(0);\n");
        RequireOnce(terminal, TerminalConstruction);
        RequireOnce(terminal, RevisionWrapper);
        RequireOnce(terminal, RevisionPolicy);
        RequireOnce(terminal, "// Stateless seams have no native field initializers; tests do not construct the presentation above.\ninternal static class McpTerminalPresentation\n{\n");
        Assert.IsFalse(terminal.Contains("Changed -=", StringComparison.Ordinal));
        var activation = Read("CodeAlta.Plugin.Mcp/McpActivationState.cs");
        Assert.AreEqual(3, activation.Split("            Changed?.Invoke(scopeKey);\n", StringSplitOptions.None).Length - 1);
        Assert.IsFalse(activation.Contains("catch (", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Dependencies_KeepNativeDialogRowsAndIconsInTui()
    {
        foreach (var path in BackendSources)
        {
            var source = Read(path);
            Assert.IsFalse(source.Contains("XenoAtom.Terminal", StringComparison.Ordinal), path);
            Assert.IsFalse(source.Contains("CodeAlta.Plugins.Tui", StringComparison.Ordinal), path);
        }
        foreach (var path in new[] { "CodeAlta.Plugin.Mcp/McpServersDialog.cs", "CodeAlta.Plugin.Mcp/TerminalIcons.cs" })
        {
            Assert.IsFalse(File.Exists(SourcePath(path)), path);
            Inverse.Restore(path, Read(path));
        }
        var dialog = Read("CodeAlta.Plugin.Mcp/McpServersDialog.cs");
        RequireOnce(dialog, "internal sealed partial class McpToolGridRow\n");
        Assert.IsTrue(dialog.Contains("[Bindable]", StringComparison.Ordinal));
        var backend = XDocument.Parse(Read("CodeAlta.Plugin.Mcp/CodeAlta.Plugin.Mcp.csproj"));
        CollectionAssert.AreEqual(new[] { "ModelContextProtocol.Core", "Tomlyn" }, References(backend, "PackageReference"));
        CollectionAssert.AreEqual(new[] { "..\\CodeAlta.Plugins.Abstractions\\CodeAlta.Plugins.Abstractions.csproj" }, References(backend, "ProjectReference"));
        CollectionAssert.AreEqual(new[] { "CodeAlta.Tests", "altatui" }, References(backend, "InternalsVisibleTo"));
        var tui = XDocument.Parse(Read("CodeAlta.Tui/CodeAlta.Tui.csproj"));
        Assert.AreEqual("altatui", tui.Descendants("AssemblyName").Single().Value);
        CollectionAssert.Contains(References(tui, "ProjectReference"), "..\\CodeAlta.Plugin.Mcp\\CodeAlta.Plugin.Mcp.csproj");
        CollectionAssert.Contains(References(tui, "PackageReference"), "XenoAtom.Terminal.UI");
        foreach (var path in new[] { "CodeAlta.Plugins/CodeAlta.Plugins.csproj", "CodeAlta.Plugins.Abstractions/CodeAlta.Plugins.Abstractions.csproj" })
        {
            var source = Read(path);
            Assert.IsFalse(source.Contains("XenoAtom.Terminal", StringComparison.Ordinal), path);
            Assert.IsFalse(source.Contains("CodeAlta.Plugins.Tui", StringComparison.Ordinal), path);
        }
    }

    [TestMethod]
    public void LegacyTests_RerouteOnlyNativeStatusEntryPoints()
    {
        var tests = Read("CodeAlta.Tests/McpConfigTests.cs");
        RequireOnce(tests, Inverse.NewRichTest);
        Assert.AreEqual(3, tests.Split("McpTerminalContributions.CreateStatusIndicator(", StringSplitOptions.None).Length - 1);
        Assert.IsFalse(tests.Contains("McpPlugin.CreateStatusIndicator(", StringComparison.Ordinal));
        Inverse.Restore("CodeAlta.Tests/McpConfigTests.cs", tests);
        RequireOnce(Read("CodeAlta.Tests/McpManagementServiceTests.cs"), "    private static IReadOnlyList<object> GetDialogRows(McpServersDialog dialog)\n");
        RequireOnce(Read("CodeAlta.Tests/PluginGitHubBackendSeparationSourceTests.cs"), Inverse.NewGitHubAssertion);
        RequireOnce(Read("CodeAlta.Tests/PluginNeutralContractSourceTests.cs"), Inverse.NewNativeAssertion);
    }

    [TestMethod]
    [Ignore("Code-shape preservation check; disabled in favor of behavioral coverage.")]
    public void Preservation_RestoresCompleteOriginalsAcrossNewlineVariants()
    {
        Assert.AreEqual(14, Inverse.Originals.Count);
        foreach (var (path, hash) in Inverse.Originals)
            foreach (var representation in PluginNeutralContractSourceInverse.Representations(Read(path)))
                Assert.AreEqual(hash, Inverse.Hash(Inverse.Restore(path, representation)), path);
        // Frozen reinsertion is insufficient evidence for extraction: compare against actual new native bodies too.
        var terminal = Read(TerminalPath);
        foreach (var body in new[] { Inverse.TerminalCommands, Inverse.DialogAndIndicator, Inverse.NativeState, Inverse.OldMarkup, Inverse.OldToolMarkup })
            RequireOnce(terminal, body);
    }

    [TestMethod]
    [Ignore("Code-shape preservation check; disabled in favor of behavioral coverage.")]
    public void Preservation_ComposesEveryFrozenHistoricalChain()
    {
        var github = new PluginGitHubBackendSeparationSourceTests();
        github.Activation_PreservesIdentityOrderingAndFailureDisposalBoundary();
        github.Preservation_RestoresCompleteOriginalsAcrossNewlineVariants();
        github.Preservation_ComposesEveryFrozenHistoricalChain();
        RequireOnce(Read("CodeAlta.Tests/PluginGitHubBackendSeparationSourceInverse.cs"), Inverse.GitHubPrelude + Inverse.GitHubHook);
        RequireOnce(Read("CodeAlta.Tests/PluginAuthoringProfileSourceInverse.cs"), Inverse.ProfilePrelude + Inverse.ProfileHook);
        RequireOnce(Read("CodeAlta.Tests/PluginNeutralContractSourceInverse.cs"), Inverse.NeutralPrelude + Inverse.NeutralHook);
        RequireOnce(Read("CodeAlta.Tests/PluginKeyBindingExtractionSourceTests.cs"), Inverse.KeyPrelude + Inverse.KeyHook);
        RequireOnce(Read("CodeAlta.Tests/PluginUiContentExtractionSourceTests.cs"), Inverse.NewUiRead);
        RequireOnce(Read("CodeAlta.Desktop.Tests/CodeAlta.Desktop.Tests.csproj"), Inverse.GitHubLink + Inverse.McpLink);
        RequireOnce(Read("CodeAlta.Tests/PluginMcpBackendSeparationSourceInverse.cs"), "    // Each entry point owns a disjoint newest-step map; existing older forwarding remains mandatory.\n");
        // Direct input assertions prove each map independently, including paths shared by older baselines.
        foreach (var path in new[] { "CodeAlta.Tui/App/CodeAltaBuiltInPlugins.cs", "CodeAlta.Tests/PluginAuthoringProfileSourceInverse.cs", "CodeAlta.Tests/PluginNeutralContractSourceInverse.cs", "CodeAlta.Tests/PluginNeutralContractSourceTests.cs", "CodeAlta.Desktop.Tests/CodeAlta.Desktop.Tests.csproj" })
            Assert.AreEqual(Inverse.Restore(path, Read(path)), Inverse.RestoreGitHubInput(path, Read(path)), path);
        const string ui = "CodeAlta.Tests/PluginUiContentExtractionSourceTests.cs";
        Assert.AreEqual(Inverse.Restore(ui, Read(ui)), Inverse.RestoreProfileInput(ui, Read(ui)));
        foreach (var path in new[] { "CodeAlta.Tests/PluginKeyBindingExtractionSourceTests.cs", "CodeAlta.Plugin.Mcp/McpServersDialog.cs" })
            Assert.AreEqual(Inverse.Restore(path, Read(path)), Inverse.RestoreNeutralInput(path, Read(path)), path);
        const string plugin = "CodeAlta.Plugin.Mcp/McpPlugin.cs";
        Assert.AreEqual(Inverse.Restore(plugin, Read(plugin)), Inverse.RestoreKeyBindingInput(plugin, Read(plugin)));
        foreach (var path in new[] { "CodeAlta.Plugin.Mcp/CodeAlta.Plugin.Mcp.csproj", "CodeAlta.Tests/McpConfigTests.cs" })
            Assert.AreEqual(Inverse.Restore(path, Read(path)), Inverse.RestoreUiContentInput(path, Read(path)), path);
        Assert.AreEqual("unmapped\n", Inverse.RestoreGitHubInput(plugin, "unmapped\n"));
        Assert.AreEqual("unmapped\n", Inverse.RestoreProfileInput(plugin, "unmapped\n"));
        Assert.AreEqual("unmapped\n", Inverse.RestoreNeutralInput(plugin, "unmapped\n"));
        Assert.AreEqual("unmapped\n", Inverse.RestoreKeyBindingInput(ui, "unmapped\n"));
        Assert.AreEqual("unmapped\n", Inverse.RestoreUiContentInput(plugin, "unmapped\n"));
        // The existing actual profile -> neutral -> projection/keybinding/UI/feedback chains remain in use.
        // Parent separately runs actual Desktop history -> workspace and assembly-boundary methods.
    }

    private const string TerminalPath = "CodeAlta.Tui/Plugins/Mcp/McpTerminalContributions.cs";
    private static IReadOnlyList<string> BackendSources =>
    [
        "CodeAlta.Plugin.Mcp/McpPlugin.cs",
        "CodeAlta.Plugin.Mcp/McpPluginPresentation.cs",
        "CodeAlta.Plugin.Mcp/McpActivationState.cs",
        "CodeAlta.Plugin.Mcp/McpCommandFactory.cs",
        "CodeAlta.Plugin.Mcp/McpConfigDiscovery.cs",
        "CodeAlta.Plugin.Mcp/McpConfigFormatAdapter.cs",
        "CodeAlta.Plugin.Mcp/McpConfigModels.cs",
        "CodeAlta.Plugin.Mcp/McpConfigWriter.cs",
        "CodeAlta.Plugin.Mcp/McpManagementService.cs",
        "CodeAlta.Plugin.Mcp/McpOAuthOptions.cs",
        "CodeAlta.Plugin.Mcp/McpPolicyOptions.cs",
        "CodeAlta.Plugin.Mcp/McpPolicyWriter.cs",
        "CodeAlta.Plugin.Mcp/McpRedactor.cs",
        "CodeAlta.Plugin.Mcp/McpRuntimeService.cs",
    ];
    private static string[] References(XDocument project, string kind) => project.Descendants(kind).Select(static element => (string?)element.Attribute("Include") ?? "").ToArray();
    private static void RequireOnce(string source, string fragment) => Assert.AreEqual(1, source.Split(SourceTestText.Canonicalize(fragment), StringSplitOptions.None).Length - 1, fragment);
    private static string Read(string path) => SourceTestText.DecodeSource(File.ReadAllBytes(SourcePath(Inverse.CurrentPath(path))));
    private static string SourcePath(string path)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CodeAlta.slnx"))) directory = directory.Parent;
        Assert.IsNotNull(directory);
        return Path.Combine(directory.FullName, path);
    }

    private const string PresentationContract = """
    using CodeAlta.Plugins.Abstractions;

    namespace CodeAlta.Plugin.Mcp;

    /// <summary>Host-owned presentation factories borrowing the backend's management and activation owners.</summary>
    internal sealed record McpPluginPresentation(
        Func<IEnumerable<PluginCommandContribution>> CreateCommands,
        Func<PluginContentContribution, PluginContentContribution> DecorateStatus);
    """ + "\n";
    private const string TerminalConstruction = """
        private McpTerminalContributions(McpManagementService managementService, McpActivationState activationState)
        {
            _managementService = managementService;
            _activationState = activationState;
            _activationState.Changed += _ => IncrementStatusRevision();
        }

        internal static McpPluginPresentation CreatePresentation(McpManagementService managementService, McpActivationState activationState)
        {
            var presentation = new McpTerminalContributions(managementService, activationState);
            return new McpPluginPresentation(presentation.CreateCommands, presentation.DecorateStatus);
        }

        private PluginContentContribution DecorateStatus(PluginContentContribution content)
            => McpTerminalPresentation.DecorateStatus(content, context => CreateStatusIndicator(context, _managementService, _activationState, _statusRevision));
    """ + "\n";
    private const string Decorator = """
        internal static PluginVisualContribution DecorateStatus(PluginContentContribution content, Func<PluginVisualContext, Visual?> createVisual)
            => new()
            {
                Region = content.Region,
                Name = content.Name,
                Order = content.Order,
                CreateContent = content.CreateContent,
                CreateVisual = createVisual,
            };
    """ + "\n";
    private const string RevisionWrapper = """
        private void IncrementStatusRevision()
        {
            var dispatcher = _statusRevision.Dispatcher;
            McpTerminalPresentation.IncrementRevision(dispatcher.CheckAccess, () => _statusRevision.Value++, action => dispatcher.Post(action));
        }
    """ + "\n";
    private const string RevisionPolicy = """
        internal static void IncrementRevision(Func<bool> checkAccess, Action increment, Action<Action> post)
        {
            if (checkAccess())
            {
                increment();
                return;
            }

            try
            {
                post(increment);
            }
            catch (InvalidOperationException)
            {
                // No interactive TerminalApp is attached. The in-memory activation state is still current,
                // and the next UI composition will read it directly.
            }
        }
    """ + "\n";
}
