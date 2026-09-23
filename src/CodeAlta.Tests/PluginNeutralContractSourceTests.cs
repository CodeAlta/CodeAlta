using System.Text;
using System.Xml.Linq;

namespace CodeAlta.Tests;

/// <summary>Named source reads only; no plugin loading, host construction or native execution.</summary>
[TestClass]
public sealed class PluginNeutralContractSourceTests
{
    [TestMethod]
    public void Contracts_IsolateRemainingTerminalTypesInOptionalAssembly()
    {
        foreach (var name in new[] { "PluginServices.cs", "PluginFactories.cs", "PluginPrompting.cs", "PluginContributions.cs" })
            Assert.IsFalse(Read("CodeAlta.Plugins.Abstractions/" + name).Contains("XenoAtom.Terminal", StringComparison.Ordinal), name);
        var services = Read("CodeAlta.Plugins.Abstractions/PluginServices.cs");
        RequireOnce(services, "public record PluginDialogRequest\n");
        Assert.IsFalse(services.Contains("Visual", StringComparison.Ordinal));
        RequireOnce(services, PluginNeutralContractSourceInverse.ShowDialogRemarks + PluginNeutralContractSourceInverse.ShowDialogDeclaration);
        var prompting = Read("CodeAlta.Plugins.Abstractions/PluginPrompting.cs");
        Assert.IsFalse(prompting.Contains("Visual", StringComparison.Ordinal));
        RequireOnce(prompting, "public sealed record PluginPromptEditorContribution\n");
        RequireOnce(prompting, "public delegate IAsyncDisposable? PluginPromptEditorAttachHandler(IPluginPromptEditorHost host);\n");
        RequireOnce(Read("CodeAlta.Plugins.Tui/PluginTerminalDialogs.cs"), TerminalDialogContract);
        RequireOnce(Read("CodeAlta.Plugins.Tui/PluginTerminalPrompting.cs"), TerminalPromptContract);
        RequireOnce(Read("CodeAlta.Plugins.Tui/PluginVisualContributions.cs"), PluginNeutralContractSourceInverse.TuiFactories);
        Assert.IsFalse(Read("CodeAlta.Plugins.Abstractions/PluginFactories.cs").Contains("CustomDialog", StringComparison.Ordinal));
        Assert.IsFalse(File.Exists(SourcePath("CodeAlta.Plugins.Abstractions/PluginDialogLayout.cs")));
        PluginNeutralContractSourceInverse.Restore("CodeAlta.Plugins.Abstractions/PluginDialogLayout.cs", Read("CodeAlta.Plugins.Tui/PluginDialogLayout.cs"));
    }

    [TestMethod]
    public void Routes_UseTypedPromptAdmissionAndPreserveNativeConsumers()
    {
        var chat = Read("CodeAlta.Tui/Views/ChatPromptEditor.cs");
        RequireOnce(chat, "internal sealed class ChatPromptEditor : PromptEditor, IProjectFileReferencePopupHost, IPluginTerminalPromptEditorHost\n");
        RequireOnce(chat, "    Visual IPluginTerminalPromptEditorHost.Visual => this;\n");
        RequireOnce(chat, ChatAttachmentLoop);
        RequireOnce(PluginGitHubBackendSeparationSourceInverse.Restore("CodeAlta.Plugin.GitHub/GitHubPlugin.cs", Read("CodeAlta.Plugin.GitHub/GitHubPlugin.cs")), PluginNeutralContractSourceInverse.NewGitHubContribution);
        RequireOnce(Read("CodeAlta.Plugin.GitHub/GitHubIssuePromptAttachment.cs"), "    public GitHubIssuePromptAttachment(GitHubPlugin plugin, IPluginTerminalPromptEditorHost host)\n");
        foreach (var path in new[]
        {
            "CodeAlta.Tui/Views/ChatPromptEditor.cs", "CodeAlta.Plugin.GitHub/GitHubPlugin.cs",
            "CodeAlta.Plugin.GitHub/GitHubIssuePromptAttachment.cs", "CodeAlta.Plugin.Mcp/McpServersDialog.cs",
            "CodeAlta.Tui/App/DialogBoundsResolver.cs", "CodeAlta.Tui/Views/ResponsiveDialogSize.cs",
        })
            PluginNeutralContractSourceInverse.Restore(path, Read(path));
        RequireOnce(Read("CodeAlta.Tui/Plugins/Mcp/McpTerminalContributions.cs"), "            () => PluginDialogLayout.ResolveDialogBounds(focusTarget),\n");
        var noop = Read("CodeAlta.Plugins.Abstractions/NoopPluginServices.cs");
        Assert.AreEqual("A07952FA64A6570BD89E0D7842A22DEBF16484C1C7221FB01B693F3DE1A2C90E", PluginNeutralContractSourceInverse.Hash(noop));
        RequireOnce(noop, NoopUiLeaf);
        foreach (var path in new[] { "CodeAlta.Tui/Views/ChatPromptEditor.cs", "CodeAlta.Tui/Views/ResponsiveDialogSize.cs" })
            Assert.IsFalse(Read(path).Contains("ConfigureAwait(false)", StringComparison.Ordinal));
    }

    [TestMethod]
    [Ignore("Code-shape preservation check; disabled in favor of behavioral coverage.")]
    public void Dependencies_RemoveOnlyUnusedContractPackages()
    {
        var abstractions = Project("CodeAlta.Plugins.Abstractions/CodeAlta.Plugins.Abstractions.csproj");
        Assert.IsFalse(References(abstractions, "PackageReference").Any(name => name.StartsWith("XenoAtom.Terminal", StringComparison.Ordinal)));
        Assert.IsFalse(References(abstractions, "ProjectReference").Any(name => name.Contains("Plugins.Tui", StringComparison.Ordinal)));
        Assert.IsFalse(References(Project("CodeAlta.Plugin.GitHub/CodeAlta.Plugin.GitHub.csproj"), "ProjectReference").Any(name => name.Contains("CodeAlta.Plugins.Tui.csproj", StringComparison.Ordinal)));
        Assert.IsFalse(References(Project("CodeAlta.Plugin.Statistics/CodeAlta.Plugin.Statistics.csproj"), "PackageReference").Any(name => name.StartsWith("XenoAtom.Terminal", StringComparison.Ordinal)));
        Assert.IsTrue(References(Project("CodeAlta.Plugins.Abstractions.Tests/CodeAlta.Plugins.Abstractions.Tests.csproj"), "ProjectReference").Any(name => name.Contains("CodeAlta.Plugins.Tui.csproj", StringComparison.Ordinal)));
        Assert.IsTrue(References(Project("CodeAlta.Plugins.Tui/CodeAlta.Plugins.Tui.csproj"), "PackageReference").Contains("XenoAtom.Terminal.UI"));
        Assert.IsFalse(References(Project("CodeAlta.Plugin.Mcp/CodeAlta.Plugin.Mcp.csproj"), "PackageReference").Any(name => name.StartsWith("XenoAtom.Terminal", StringComparison.Ordinal)));
        Assert.IsFalse(References(Project("CodeAlta.Plugin.GitHub/CodeAlta.Plugin.GitHub.csproj"), "PackageReference").Any(name => name.StartsWith("XenoAtom.Terminal", StringComparison.Ordinal)));
        var terminalPackages = References(Project("CodeAlta.Tui/CodeAlta.Tui.csproj"), "PackageReference");
        foreach (var package in new[] { "XenoAtom.Terminal.UI", "XenoAtom.Terminal.UI.Extensions.CodeEditor.TextMateSharp", "XenoAtom.Terminal.UI.Extensions.Markdown", "XenoAtom.Terminal.UI.Extensions.Screenshot", "XenoAtom.Terminal.UI.Graphics" })
            Assert.IsTrue(terminalPackages.Contains(package), package);
        var shared = Project("CodeAlta.Plugins/CodeAlta.Plugins.csproj");
        Assert.IsFalse(References(shared, "ProjectReference").Any(name => name.Contains("Plugins.Tui", StringComparison.Ordinal)));
        Assert.IsFalse(References(shared, "PackageReference").Any(name => name.StartsWith("XenoAtom.Terminal", StringComparison.Ordinal)));
        var rootBuild = Read("CodeAlta.Plugins/PluginRootBuildFiles.cs");
        var loading = Read("CodeAlta.Plugins/PluginAssemblyLoading.cs");
        foreach (var name in new[] { "XenoAtom.Terminal.UI", "XenoAtom.Terminal.UI.Extensions.CodeEditor.TextMateSharp", "XenoAtom.Terminal.UI.Extensions.Markdown", "XenoAtom.Terminal.UI.Extensions.Screenshot", "XenoAtom.Terminal.UI.Graphics" })
        {
            RequireOnce(rootBuild, "        \"" + name + "\",\n");
            RequireOnce(loading, "        \"" + name + "\",\n");
        }
        RequireOnce(rootBuild, "            targets.AppendLine(\"      <ExcludeAssets>runtime;native</ExcludeAssets>\");\n");
        RequireOnce(loading, "        \"CodeAlta.Plugins.Tui\",\n");
        RequireOnce(Read("CodeAlta.Desktop.Tests/CodeAlta.Desktop.Tests.csproj"), PluginNeutralContractSourceInverse.SourceTextLink + PluginNeutralContractSourceInverse.InverseLink);
    }

    [TestMethod]
    [Ignore("Code-shape preservation check; disabled in favor of behavioral coverage.")]
    public void Preservation_RestoresCompleteOriginalsAcrossNewlineVariants()
    {
        Assert.AreEqual(24, PluginNeutralContractSourceInverse.Originals.Count);
        foreach (var (path, hash) in PluginNeutralContractSourceInverse.Originals)
        {
            // The moved original is reconstructed from the actual destination, never stored old source.
            var source = Read(path == "CodeAlta.Plugins.Abstractions/PluginDialogLayout.cs" ? "CodeAlta.Plugins.Tui/PluginDialogLayout.cs" : path);
            foreach (var representation in PluginNeutralContractSourceInverse.Representations(source))
                Assert.AreEqual(hash, PluginNeutralContractSourceInverse.Hash(PluginNeutralContractSourceInverse.Restore(path, representation)), path);
        }
        Assert.ThrowsExactly<ArgumentException>(() => SourceTestText.DecodeSource([0xEF, 0xBB, 0xBF, 0x0A]));
        Assert.ThrowsExactly<ArgumentException>(() => SourceTestText.DecodeSource(Encoding.UTF8.GetBytes("missing newline")));
        Assert.ThrowsExactly<ArgumentException>(() => SourceTestText.DecodeSource(Encoding.UTF8.GetBytes("lone\rCR\n")));
        Assert.ThrowsExactly<DecoderFallbackException>(() => SourceTestText.DecodeSource([0xFF, 0x0A]));
    }

    [TestMethod]
    [Ignore("Code-shape preservation check; disabled in favor of behavioral coverage.")]
    public void Preservation_ComposesEveryFrozenHistoricalChain()
    {
        RequireOnce(Read("CodeAlta.Tests/PluginKeyBindingExtractionSourceTests.cs"), PluginNeutralContractSourceInverse.KeyDecode + PluginNeutralContractSourceInverse.KeyPrelude);
        RequireOnce(Read("CodeAlta.Tests/PluginUiContentExtractionSourceTests.cs"), PluginNeutralContractSourceInverse.UiPrelude + PluginNeutralContractSourceInverse.UiAdapterAnchor);
        RequireOnce(Read("CodeAlta.Tests/PluginSessionEventProjectionSourceTests.cs"), PluginNeutralContractSourceInverse.NewProjectionInverse);
        RequireOnce(Read("CodeAlta.Desktop.Tests/DesktopWorkspaceSourceTests.cs"), PluginNeutralContractSourceInverse.NewWorkspaceRead);
        var history = Read("CodeAlta.Desktop.Tests/DesktopHistorySourceTests.cs");
        RequireOnce(history, PluginNeutralContractSourceInverse.NewHistoryInverse);
        RequireOnce(history, "        new DesktopWorkspaceSourceTests().Boundaries_PreserveTrustAndDocumentReadLimits();\n");
        // These actual source-only entry points retain every old hash, payload and whole-source chain.
        new PluginSessionEventProjectionSourceTests().Preservation_RestoresCompleteOriginalsAndFrozenChains();
        var keys = new PluginKeyBindingExtractionSourceTests();
        keys.Preservation_RestoresCompletePreExtractionSources();
        keys.Preservation_PreservesUiContentAndHistoricalChains();
        var ui = new PluginUiContentExtractionSourceTests();
        ui.Preservation_RestoresCompletePreExtractionSources();
        ui.Preservation_LeavesHistoricalChainsAndFrozenBoundariesUnchanged();
        new PluginFeedbackExtractionSourceTests().Preservation_RestoresCompletePreExtractionSourcesAndGuardChain();
        // Desktop.Tests separately executes its actual history -> workspace chain; no assembly reference is added here.
    }

    private static XDocument Project(string path) => XDocument.Parse(Read(path));
    private static string[] References(XDocument project, string item) => project.Descendants(item).Select(element => (string?)element.Attribute("Include") ?? "").ToArray();
    private static void RequireOnce(string source, string fragment)
    {
        fragment = SourceTestText.Canonicalize(fragment);
        Assert.IsTrue(fragment.Length > 0);
        Assert.AreEqual(1, source.Split(fragment, StringSplitOptions.None).Length - 1, fragment);
    }
    private static string Read(string path) => SourceTestText.DecodeSource(File.ReadAllBytes(SourcePath(path)));
    private static string SourcePath(string path)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CodeAlta.slnx"))) directory = directory.Parent;
        Assert.IsNotNull(directory);
        return Path.Combine(directory.FullName, PluginMcpBackendSeparationSourceInverse.CurrentPath(PluginGitHubBackendSeparationSourceInverse.CurrentPath(path)));
    }

    private const string TerminalDialogContract = """
    public sealed record PluginTerminalDialogRequest : PluginDialogRequest
    {
        /// <summary>Gets optional custom terminal content. Unsupported hosts need not consume it.</summary>
        public Visual? Content { get; init; }
    }
    """ + "\n";
    private const string TerminalPromptContract = """
    public interface IPluginTerminalPromptEditorHost : IPluginPromptEditorHost
    {
        /// <summary>Gets the editor visual used as an anchor for terminal-owned UI.</summary>
        Visual Visual { get; }
    }
    """ + "\n";
    private const string ChatAttachmentLoop = """
        public ChatPromptEditor EnablePromptEditorContributions(IReadOnlyList<PluginPromptEditorContribution> contributions)
        {
            ArgumentNullException.ThrowIfNull(contributions);
            foreach (var attachment in _promptEditorAttachments)
            {
                _ = attachment.DisposeAsync();
            }

            _promptEditorAttachments.Clear();
            foreach (var contribution in contributions)
            {
                var attachment = contribution.Attach(this);
                if (attachment is not null)
                {
                    _promptEditorAttachments.Add(attachment);
                }
            }

            return this;
        }
    """ + "\n";
    private const string NoopUiLeaf = """
    public sealed class NoopPluginUiService : IPluginUiService
    {
        /// <inheritdoc />
        public bool HasInteractiveUi => false;

        /// <inheritdoc />
        public ValueTask NotifyAsync(string message, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(message);
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }

        /// <inheritdoc />
        public ValueTask<bool> ConfirmAsync(string title, string message, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(title);
            ArgumentException.ThrowIfNullOrWhiteSpace(message);
            cancellationToken.ThrowIfCancellationRequested();
            return new ValueTask<bool>(false);
        }

        /// <inheritdoc />
        public ValueTask<string?> InputAsync(string title, string? initialText = null, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(title);
            cancellationToken.ThrowIfCancellationRequested();
            return new ValueTask<string?>((string?)null);
        }

        /// <inheritdoc />
        public ValueTask<string?> EditTextAsync(string title, string text, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(title);
            ArgumentNullException.ThrowIfNull(text);
            cancellationToken.ThrowIfCancellationRequested();
            return new ValueTask<string?>((string?)null);
        }

        /// <inheritdoc />
        public ValueTask<T?> SelectAsync<T>(string title, IReadOnlyList<PluginSelectItem<T>> items, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(title);
            ArgumentNullException.ThrowIfNull(items);
            cancellationToken.ThrowIfCancellationRequested();
            return new ValueTask<T?>((T?)default);
        }

        /// <inheritdoc />
        public ValueTask ShowDialogAsync(PluginDialogRequest request, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }

        /// <inheritdoc />
        public ValueTask<PluginDialogResponse?> ShowDialogForResultAsync(PluginDialogRequest request, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            cancellationToken.ThrowIfCancellationRequested();
            return new ValueTask<PluginDialogResponse?>((PluginDialogResponse?)null);
        }
    }
    """ + "\n";
}
