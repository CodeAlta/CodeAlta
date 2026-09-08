using System.Xml.Linq;

namespace CodeAlta.Tests;

/// <summary>Named current-source reads and explicit historical reconstruction, not runtime/native qualification.</summary>
[TestClass]
public sealed class PluginGitHubBackendSeparationSourceTests
{
    [TestMethod]
    public void Activation_BuiltInsSupplyFactoryAndSourcesRetainReflection()
    {
        var manager = Read("CodeAlta.Plugins/PluginRuntimeManager.cs");
        RequireOnce(manager, PluginGitHubBackendSeparationSourceInverse.NewBuiltInActivation);
        RequireOnce(manager, "BuiltInFactory = builtIn.Factory");
        // The other options construction belongs to the dynamic-source activation path, not a second built-in owner.
        RequireOnce(manager, "new PluginActivationOptions { HostInfo = hostInfo, Services = options.Services, ActivationGeneration = ++_activationGeneration },");
        var lifecycle = Read("CodeAlta.Plugins/PluginRuntimeLifecycle.cs");
        RequireOnce(lifecycle, PluginGitHubBackendSeparationSourceInverse.NewConstruction);
        RequireOnce(lifecycle, PluginGitHubBackendSeparationSourceInverse.SelectionSeam);
        RequireOnce(lifecycle, "() => (PluginBase?)Activator.CreateInstance(discoveredType.Type)");
        RequireOnce(lifecycle, PluginGitHubBackendSeparationSourceInverse.FactoryOption);
        var definition = Read("CodeAlta.Plugins/BuiltInPlugins.cs");
        RequireOnce(definition, "        => PluginType ?? (Factory.Method.ReturnType == typeof(PluginBase) ? Factory().GetType() : Factory.Method.ReturnType);\n");
        RequireOnce(definition, PluginGitHubBackendSeparationSourceInverse.NewFactoryDocs);
        RequireOnce(definition, PluginGitHubBackendSeparationSourceInverse.ResolveRemarks);
    }

    [TestMethod]
    public void Activation_PreservesIdentityOrderingAndFailureDisposalBoundary()
    {
        var lifecycle = Read("CodeAlta.Plugins/PluginRuntimeLifecycle.cs");
        RequireOnce(lifecycle, PluginGitHubBackendSeparationSourceInverse.TypeAdmission + PluginGitHubBackendSeparationSourceInverse.LoggerAnchor);
        Before(lifecycle, "instance = CreateInstance(options.BuiltInFactory,", "            if (instance is null)\n");
        Before(lifecycle, "            if (instance is null)\n", "if (instance.GetType() != discoveredType.Type)");
        Before(lifecycle, "if (instance.GetType() != discoveredType.Type)", "var logger = LogManager.GetLogger(");
        Before(lifecycle, "instance.AttachRuntimeContext(context);", "await instance.InitializeAsync(cancellationToken)");
        Before(lifecycle, "await instance.InitializeAsync(cancellationToken)", "var contributions = CollectContributions(");
        Before(lifecycle, "var contributions = CollectContributions(", "await instance.OnActivatedAsync(cancellationToken)");
        RequireOnce(lifecycle, """
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (instance is not null)
                {
                    try
                    {
                        await instance.DisposeAsync().ConfigureAwait(false);
    """ + "\n");
        RequireOnce(lifecycle, "        Add(PluginPoint.PromptEditor, instance.GetPromptEditorContributions());\n");
        // Complete reconstruction covers the remaining lifecycle, metadata and registry bodies, not selected snippets alone.
        foreach (var path in new[] { "CodeAlta.Plugins/PluginRuntimeLifecycle.cs", "CodeAlta.Plugins/PluginRuntimeManager.cs", "CodeAlta.Plugins/BuiltInPlugins.cs", "CodeAlta.Tui/App/CodeAltaBuiltInPlugins.cs" })
            PluginGitHubBackendSeparationSourceInverse.Restore(path, Read(path));
        var registration = Read("CodeAlta.Tui/App/CodeAltaBuiltInPlugins.cs");
        foreach (var name in new[] { "GitHub", "Mcp", "Statistics" })
            RequireOnce(registration, "            PluginType = typeof(" + name + "Plugin),\n");
        RequireOnce(PluginMcpBackendSeparationSourceInverse.Restore("CodeAlta.Tui/App/CodeAltaBuiltInPlugins.cs", registration), "            Factory = static () => new McpPlugin(),\n");
        RequireOnce(registration, "            Factory = static () => new StatisticsPlugin(),\n");
    }

    [TestMethod]
    public void Composition_TuiInjectsPickerIntoSameBackendInstance()
    {
        var plugin = Read("CodeAlta.Plugin.GitHub/GitHubPlugin.cs");
        RequireOnce(plugin, PluginGitHubBackendSeparationSourceInverse.GitHubFieldsAnchor + PluginGitHubBackendSeparationSourceInverse.GitHubConstruction);
        RequireOnce(plugin, PluginGitHubBackendSeparationSourceInverse.NewGitHubContribution);
        RequireOnce(plugin, PluginGitHubBackendSeparationSourceInverse.NewSelectedPath);
        Assert.IsFalse(plugin.Contains("PluginTui", StringComparison.Ordinal));
        RequireOnce(Read("CodeAlta.Tui/App/CodeAltaBuiltInPlugins.cs"), "            Factory = static () => new GitHubPlugin(GitHubTerminalContributions.CreatePromptEditorContributions),\n");
        Assert.AreEqual(SourceTestText.Canonicalize(TerminalContributions), Read("CodeAlta.Tui/Plugins/GitHub/GitHubTerminalContributions.cs"));
        var attachment = Read("CodeAlta.Plugin.GitHub/GitHubIssuePromptAttachment.cs");
        RequireOnce(attachment, "        _plugin = plugin;\n");
        RequireOnce(attachment, "        => _host.ProjectPath ?? _plugin.GetSelectedProjectPath();\n");
        Assert.IsFalse(attachment.Contains("new GitHubPlugin", StringComparison.Ordinal));
        Assert.IsFalse(attachment.Contains("_plugin.Dispose", StringComparison.Ordinal));
        Assert.IsFalse(attachment.Contains("_plugin.Initialize", StringComparison.Ordinal));
        var baseSource = Read("CodeAlta.Plugins.Abstractions/PluginBase.cs");
        RequireOnce(baseSource, "    private PluginRuntimeContext? _context;\n");
        RequireOnce(baseSource, "    protected IPluginServices Services => Context.Services;\n");
    }

    [TestMethod]
    public void PromptEditor_UsesExistingTypedAdmissionAndBorrowedAttachmentLifetime()
    {
        var optional = Read("CodeAlta.Plugins.Tui/PluginVisualContributions.cs");
        RequireOnce(optional, "                return host is IPluginTerminalPromptEditorHost terminal ? attach(terminal) : null;\n");
        RequireOnce(Read("CodeAlta.Plugins.Abstractions/PluginPrompting.cs"), "public delegate IAsyncDisposable? PluginPromptEditorAttachHandler(IPluginPromptEditorHost host);\n");
        RequireOnce(Read("CodeAlta.Plugins.Tui/PluginTerminalPrompting.cs"), "public interface IPluginTerminalPromptEditorHost : IPluginPromptEditorHost\n");
        RequireOnce(Read("CodeAlta.Plugins/PluginContributionAdapters.cs"), "        => GetRegistrations(point, options).Where(static registration => registration.Contribution is TContribution).ToArray();\n");
        RequireOnce(Read("CodeAlta.Tui/App/PluginFrontendBridge.cs"), "        => _runtime.Adapter.GetContributions<PluginPromptEditorContribution>(PluginPoint.PromptEditor, CreateOptions())\n");
        var editor = Read("CodeAlta.Tui/Views/ChatPromptEditor.cs");
        RequireOnce(editor, "            _ = attachment.DisposeAsync();\n");
        RequireOnce(editor, "            var attachment = contribution.Attach(this);\n");
        RequireOnce(editor, "                _promptEditorAttachments.Add(attachment);\n");
        Assert.IsFalse(editor.Contains("ConfigureAwait(false)", StringComparison.Ordinal));
        PluginGitHubBackendSeparationSourceInverse.Restore("CodeAlta.Plugin.GitHub/GitHubIssuePromptAttachment.cs", Read("CodeAlta.Plugin.GitHub/GitHubIssuePromptAttachment.cs"));
        RequireOnce(Read("CodeAlta.Tests/GitHubPluginTests.cs"), PluginGitHubBackendSeparationSourceInverse.NewExistingTest);
    }

    [TestMethod]
    public void Dependencies_GitHubBackendContainsNoTerminalReferences()
    {
        foreach (var path in new[] { "CodeAlta.Plugin.GitHub/GitHubPlugin.cs", "CodeAlta.Plugin.GitHub/GitHubIssueReferenceItem.cs" })
        {
            var source = Read(path);
            Assert.IsFalse(source.Contains("XenoAtom.Terminal", StringComparison.Ordinal), path);
            Assert.IsFalse(source.Contains("CodeAlta.Plugins.Tui", StringComparison.Ordinal), path);
            Assert.IsFalse(source.Contains("BindingAccessor", StringComparison.Ordinal), path);
        }
        var backend = XDocument.Parse(Read("CodeAlta.Plugin.GitHub/CodeAlta.Plugin.GitHub.csproj"));
        CollectionAssert.AreEqual(new[] { "XenoAtom.Logging" }, References(backend, "PackageReference"));
        CollectionAssert.AreEqual(new[] { "..\\CodeAlta.Agent\\CodeAlta.Agent.csproj", "..\\CodeAlta.Plugins.Abstractions\\CodeAlta.Plugins.Abstractions.csproj" }, References(backend, "ProjectReference"));
        var tui = XDocument.Parse(Read("CodeAlta.Tui/CodeAlta.Tui.csproj"));
        CollectionAssert.Contains(References(tui, "ProjectReference"), "..\\CodeAlta.Plugin.GitHub\\CodeAlta.Plugin.GitHub.csproj");
        CollectionAssert.Contains(References(tui, "ProjectReference"), "..\\CodeAlta.Plugins.Tui\\CodeAlta.Plugins.Tui.csproj");
        CollectionAssert.Contains(References(tui, "PackageReference"), "XenoAtom.Terminal.UI");
        var runtime = Read("CodeAlta.Plugins/CodeAlta.Plugins.csproj");
        Assert.IsFalse(runtime.Contains("CodeAlta.Plugins.Tui", StringComparison.Ordinal));
        Assert.IsFalse(runtime.Contains("XenoAtom.Terminal", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Presentation_MovesParserDialogAndAccessorsWithoutBehaviorChanges()
    {
        foreach (var name in new[] { "GitHubIssuePromptAttachment", "GitHubIssuePickerDialog", "GitHubIssueReferenceParser" })
        {
            var original = "CodeAlta.Plugin.GitHub/" + name + ".cs";
            Assert.IsFalse(File.Exists(SourcePath(original)), original);
            var source = Read(original);
            RequireOnce(source, "namespace CodeAlta.Plugin.GitHub;\n");
            PluginGitHubBackendSeparationSourceInverse.Restore(original, source);
        }
        var item = Read("CodeAlta.Plugin.GitHub/GitHubIssueReferenceItem.cs");
        PluginGitHubBackendSeparationSourceInverse.Restore("CodeAlta.Plugin.GitHub/GitHubIssueReferenceItem.cs", item);
        var accessors = Read("CodeAlta.Tui/Plugins/GitHub/GitHubIssueReferenceAccessors.cs");
        // Compare the entire moved holder against the frozen nested holder with only nesting/name changed.
        var holder = string.Join('\n', SourceTestText.Canonicalize(PluginGitHubBackendSeparationSourceInverse.OldAccessors).Split('\n').Select(static line => line.StartsWith("    ", StringComparison.Ordinal) ? line[4..] : line));
        var expected = "using XenoAtom.Terminal.UI;\n\nnamespace CodeAlta.Plugin.GitHub;\n\n" + holder.Replace("internal static class Accessor", "internal static class GitHubIssueReferenceAccessors", StringComparison.Ordinal);
        Assert.AreEqual(expected, accessors);
        Assert.AreEqual(SourceTestText.Canonicalize(TerminalContributions), Read("CodeAlta.Tui/Plugins/GitHub/GitHubTerminalContributions.cs"));
    }

    [TestMethod]
    public void Preservation_RestoresCompleteOriginalsAcrossNewlineVariants()
    {
        Assert.AreEqual(15, PluginGitHubBackendSeparationSourceInverse.Originals.Count);
        foreach (var (path, hash) in PluginGitHubBackendSeparationSourceInverse.Originals)
        {
            foreach (var representation in PluginNeutralContractSourceInverse.Representations(Read(path)))
                Assert.AreEqual(hash, PluginGitHubBackendSeparationSourceInverse.Hash(PluginGitHubBackendSeparationSourceInverse.Restore(path, representation)), path);
        }
    }

    [TestMethod]
    public void Preservation_ComposesEveryFrozenHistoricalChain()
    {
        var profile = new PluginAuthoringProfileSourceTests();
        profile.Loader_PreflightsMetadataBeforeDiscoveryWithoutActivation();
        profile.Preservation_RestoresCompleteOriginalsAcrossNewlineVariants();
        profile.Preservation_ComposesEveryFrozenHistoricalChain();
        new PluginNeutralContractSourceTests().Routes_UseTypedPromptAdmissionAndPreserveNativeConsumers();
        // The profile entry calls the actual neutral -> projection, both keybinding, both UI and feedback chains.
        // Desktop.Tests separately runs its actual history -> workspace and assembly boundary methods.
        var desktop = Read("CodeAlta.Desktop.Tests/CodeAlta.Desktop.Tests.csproj");
        RequireOnce(desktop, PluginGitHubBackendSeparationSourceInverse.ProfileLink + PluginGitHubBackendSeparationSourceInverse.GitHubLink);
        RequireOnce(Read("CodeAlta.Tests/PluginAuthoringProfileSourceInverse.cs"), PluginGitHubBackendSeparationSourceInverse.ProfileHook);
        RequireOnce(Read("CodeAlta.Tests/PluginNeutralContractSourceInverse.cs"), PluginGitHubBackendSeparationSourceInverse.NeutralHook);
        var inverse = Read("CodeAlta.Tests/PluginGitHubBackendSeparationSourceInverse.cs");
        RequireOnce(inverse, """
        internal static string RestoreProfileInput(string path, string source) => path is
            "CodeAlta.Plugins/PluginRuntimeManager.cs" or "CodeAlta.Tests/PluginNeutralContractSourceInverse.cs" or
            "CodeAlta.Desktop.Tests/CodeAlta.Desktop.Tests.csproj"
            ? Restore(path, source) : source;
    """ + "\n");
        RequireOnce(inverse, """
        internal static string RestoreNeutralInput(string path, string source) => path is
            "CodeAlta.Plugin.GitHub/GitHubPlugin.cs" or "CodeAlta.Plugin.GitHub/CodeAlta.Plugin.GitHub.csproj" or
            "CodeAlta.Plugin.GitHub/GitHubIssuePromptAttachment.cs" or "CodeAlta.Plugin.GitHub/GitHubIssuePickerDialog.cs"
            ? Restore(path, source) : source;
    """ + "\n");
    }

    private static string[] References(XDocument project, string kind) => project.Descendants(kind).Select(static element => (string?)element.Attribute("Include") ?? "").ToArray();
    private static void RequireOnce(string source, string fragment) => Assert.AreEqual(1, source.Split(SourceTestText.Canonicalize(fragment), StringSplitOptions.None).Length - 1, fragment);
    private static void Before(string source, string first, string second)
    {
        var firstIndex = source.IndexOf(first, StringComparison.Ordinal);
        var secondIndex = source.IndexOf(second, StringComparison.Ordinal);
        Assert.IsTrue(firstIndex >= 0 && secondIndex > firstIndex, first + " before " + second);
    }
    private static string Read(string path) => SourceTestText.DecodeSource(File.ReadAllBytes(SourcePath(PluginGitHubBackendSeparationSourceInverse.CurrentPath(path))));
    private static string SourcePath(string path)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CodeAlta.slnx"))) directory = directory.Parent;
        Assert.IsNotNull(directory);
        return Path.Combine(directory.FullName, path);
    }

    private const string TerminalContributions = """
    using CodeAlta.Plugins.Abstractions;
    using CodeAlta.Plugins.Tui;

    namespace CodeAlta.Plugin.GitHub;

    internal static class GitHubTerminalContributions
    {
        internal static IEnumerable<PluginPromptEditorContribution> CreatePromptEditorContributions(GitHubPlugin plugin)
        {
            yield return PluginTui.PromptEditor(
                "GitHub issue prompt picker",
                host => new GitHubIssuePromptAttachment(plugin, host),
                "[#] to reference a GitHub issue");
        }
    }
    """ + "\n";
}
