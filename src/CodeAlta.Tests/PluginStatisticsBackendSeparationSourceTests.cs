using System.Xml.Linq;
using Inverse = CodeAlta.Tests.PluginStatisticsBackendSeparationSourceInverse;

namespace CodeAlta.Tests;

/// <summary>Named source reads and mandatory historical reconstruction; no production, native or configuration execution.</summary>
[TestClass]
public sealed class PluginStatisticsBackendSeparationSourceTests
{
    [TestMethod]
    public void Composition_TuiDecoratesCandidatesFromSameBackend()
    {
        var backend = Read(Inverse.BackendPath);
        RequireOnce(backend, Inverse.CacheOwner + Inverse.Construction);
        RequireOnce(backend, Inverse.NewProjection);
        RequireOnce(Read("CodeAlta.Tui/App/CodeAltaBuiltInPlugins.cs"), Inverse.NewRegistration);
        Assert.AreEqual(SourceTestText.Canonicalize(PresentationContract), Read("CodeAlta.Plugin.Statistics/StatisticsPresentation.cs"));
        Assert.AreEqual(SourceTestText.Canonicalize(Decorator), Read(TerminalPath));
        RequireOnce(Read("CodeAlta.Plugins.Abstractions/PluginBase.cs"), "    private PluginRuntimeContext? _context;\n");
        RequireOnce(Read("CodeAlta.Plugins/PluginRuntimeManager.cs"), "BuiltInFactory = builtIn.Factory");
        RequireOnce(Read("CodeAlta.Plugins/PluginRuntimeLifecycle.cs"), "instance = CreateInstance(options.BuiltInFactory, () => (PluginBase?)Activator.CreateInstance(discoveredType.Type));");
        RequireOnce(Read("CodeAlta.Plugins/BuiltInPlugins.cs"), "    public required Func<PluginBase> Factory { get; init; }\n");
    }

    [TestMethod]
    public void Projection_PreservesCalculationsPayloadAndCacheSemantics()
    {
        var backend = Read(Inverse.BackendPath);
        RequireOnce(backend, Inverse.NewProjection);
        RequireOnce(backend, "        var cacheKey = FormattableString.Invariant($\"{sessionId}:{turn.Key}:{turn.Fingerprint}\");\n        return _turnProjectionCache.GetOrAdd(cacheKey, _ => CreateProjection(sessionId, turn));\n");
        RequireOnce(backend, "        ArgumentNullException.ThrowIfNull(context);\n        cancellationToken.ThrowIfCancellationRequested();\n");
        RequireOnce(backend, "        foreach (var turn in turns.Where(static item => item.IsComplete))\n");
        RequireOnce(backend, Inverse.MarkdownClass);
        RequireOnce(backend, Inverse.NewTitle);
        RequireOnce(backend, Inverse.NewSuffixSignature);
        // Complete-source restoration guards every unchanged calculation, portable formatter, payload type,
        // initializer, fingerprint, CLI body and cache owner; it does not claim concurrent exactly-once decoration.
        Inverse.Restore(Inverse.BackendPath, backend);
    }

    [TestMethod]
    public void Rendering_PreservesDeferredFormattingOrderAndStyles()
    {
        AssertRenderer();
        Assert.AreEqual(SourceTestText.Canonicalize(Decorator), Read(TerminalPath));
        var backend = Read(Inverse.BackendPath);
        foreach (var forbidden in new[] { "AnsiMarkup", "[bold]", "RenderTurnSummaryMarkup", "StatisticsVisualRenderer", "VisualFactory" })
            Assert.IsFalse(backend.Contains(forbidden, StringComparison.Ordinal), forbidden);
    }

    [TestMethod]
    [Ignore("Source-shape dependency inventory; plugin tests now build native terminal samples explicitly.")]
    public void Dependencies_StatisticsAndPluginTestsRemainTerminalFree()
    {
        foreach (var path in new[] { Inverse.BackendPath, "CodeAlta.Plugin.Statistics/StatisticsPresentation.cs", "CodeAlta.Plugin.Statistics/CodeAlta.Plugin.Statistics.csproj", "CodeAlta.Plugins.Tests/CodeAlta.Plugins.Tests.csproj" })
        {
            var source = Read(path);
            Assert.IsFalse(source.Contains("XenoAtom.Terminal", StringComparison.Ordinal), path);
            Assert.IsFalse(source.Contains("CodeAlta.Plugins.Tui", StringComparison.Ordinal), path);
        }
        var backend = XDocument.Parse(Read("CodeAlta.Plugin.Statistics/CodeAlta.Plugin.Statistics.csproj"));
        CollectionAssert.AreEqual(Array.Empty<string>(), References(backend, "PackageReference"));
        CollectionAssert.AreEqual(new[] { "..\\CodeAlta.Plugins.Abstractions\\CodeAlta.Plugins.Abstractions.csproj" }, References(backend, "ProjectReference"));
        CollectionAssert.AreEqual(new[] { "altatui", "CodeAlta.Tests" }, References(backend, "InternalsVisibleTo"));
        var pluginsTests = XDocument.Parse(Read("CodeAlta.Plugins.Tests/CodeAlta.Plugins.Tests.csproj"));
        CollectionAssert.AreEqual(new[] { "..\\CodeAlta.Plugin.Statistics\\CodeAlta.Plugin.Statistics.csproj", "..\\CodeAlta.Plugins\\CodeAlta.Plugins.csproj" }, References(pluginsTests, "ProjectReference"));
        var tests = XDocument.Parse(Read("CodeAlta.Tests/CodeAlta.Tests.csproj"));
        CollectionAssert.Contains(References(tests, "ProjectReference"), "..\\CodeAlta.Tui\\CodeAlta.Tui.csproj");
        var tui = XDocument.Parse(Read("CodeAlta.Tui/CodeAlta.Tui.csproj"));
        Assert.AreEqual("altatui", tui.Descendants("AssemblyName").Single().Value);
        CollectionAssert.Contains(References(tui, "ProjectReference"), "..\\CodeAlta.Plugin.Statistics\\CodeAlta.Plugin.Statistics.csproj");
        CollectionAssert.Contains(References(tui, "PackageReference"), "XenoAtom.Terminal.UI.Extensions.Markdown");
        foreach (var path in new[] { "CodeAlta.Plugins/CodeAlta.Plugins.csproj", "CodeAlta.Plugins.Abstractions/CodeAlta.Plugins.Abstractions.csproj" })
        {
            var source = Read(path);
            Assert.IsFalse(source.Contains("XenoAtom.Terminal", StringComparison.Ordinal), path);
            Assert.IsFalse(source.Contains("CodeAlta.Plugins.Tui", StringComparison.Ordinal), path);
        }
    }

    [TestMethod]
    [Ignore("Code-shape preservation check; disabled in favor of behavioral coverage.")]
    public void LegacyTests_MoveFixtureAndComposeOnlyNativeCase()
    {
        Assert.IsFalse(File.Exists(SourcePath(Inverse.LegacyPath)));
        var tests = Read(Inverse.MovedPath);
        RequireOnce(tests, "namespace CodeAlta.Plugins.Tests;\n");
        RequireOnce(tests, Inverse.NewRichTest);
        RequireOnce(tests, "StatisticsTerminalContributions.DecorateProjection");
        Inverse.Restore(Inverse.LegacyPath, tests);
        RequireOnce(Read("CodeAlta.Tests/PluginSessionEventProjectionSourceTests.cs"), Inverse.NewNativeRead);
        RequireOnce(Read("CodeAlta.Tests/PluginSessionEventProjectionSourceTests.cs"), Inverse.NewProjectionRead);
        RequireOnce(Read("CodeAlta.Tests/PluginGitHubBackendSeparationSourceTests.cs"), Inverse.NewGitHubAssertion);
        RequireOnce(Read("CodeAlta.Tests/PluginNeutralContractSourceTests.cs"), Inverse.NewPackageAssertion);
    }

    [TestMethod]
    public void Routing_PreservesDeferredNativeFactorySelection()
    {
        new PluginSessionEventProjectionSourceTests().TuiRouting_PreservesFactoryPrecedenceAndDeferredContexts();
        var optional = Read("CodeAlta.Plugins.Tui/PluginTerminalSessionEventProjection.cs");
        RequireOnce(optional, "public sealed record PluginTerminalDerivedSessionEvent : PluginDerivedSessionEvent\n");
        RequireOnce(optional, "public sealed record PluginTerminalDerivedSessionEventDetailSection : PluginDerivedSessionEventDetailSection\n");
        var neutral = Read("CodeAlta.Plugins.Abstractions/PluginSessionEventProjection.cs");
        Assert.IsFalse(neutral.Contains("VisualFactory", StringComparison.Ordinal));
        var coordinatorTests = Read("CodeAlta.Tests/SessionRuntimeEventCoordinatorTests.cs");
        RequireOnce(coordinatorTests, "HandleAgentEvent_RendersPluginProjectionOutsidePluginProjectionLock()");
        RequireOnce(coordinatorTests, "DynamicPluginProjectionRefresh_RendersOutsidePluginProjectionLock()");
    }

    [TestMethod]
    [Ignore("Code-shape preservation check; disabled in favor of behavioral coverage.")]
    public void Preservation_RestoresCompleteOriginalsAcrossNewlineVariants()
    {
        Assert.AreEqual(10, Inverse.Originals.Count);
        Assert.AreEqual(17, Inverse.Originals.Sum(item => Inverse.Edits(item.Path).Count));
        foreach (var (path, hash) in Inverse.Originals)
            foreach (var representation in PluginNeutralContractSourceInverse.Representations(Read(path)))
                Assert.AreEqual(hash, Inverse.Hash(Inverse.Restore(path, representation)), path);
        AssertRenderer(); // Actual extracted code, not just frozen reinsertion into the backend.
    }

    [TestMethod]
    [Ignore("Code-shape preservation check; disabled in favor of behavioral coverage.")]
    public void Preservation_ComposesEveryFrozenHistoricalChain()
    {
        var mcp = new PluginMcpBackendSeparationSourceTests();
        mcp.Preservation_RestoresCompleteOriginalsAcrossNewlineVariants();
        mcp.Preservation_ComposesEveryFrozenHistoricalChain();
        RequireOnce(Read("CodeAlta.Tests/PluginMcpBackendSeparationSourceInverse.cs"), Inverse.NewMcpPrelude);
        RequireOnce(Read("CodeAlta.Tests/PluginNeutralContractSourceInverse.cs"), Inverse.NeutralPrelude + Inverse.NeutralHook);
        RequireOnce(Read("CodeAlta.Tests/PluginSessionEventProjectionSourceTests.cs"), Inverse.ProjectionPrelude + Inverse.ProjectionHook);
        RequireOnce(Read("CodeAlta.Desktop.Tests/CodeAlta.Desktop.Tests.csproj"), Inverse.McpLink + Inverse.StatisticsLink);
        foreach (var path in new[] { "CodeAlta.Tui/App/CodeAltaBuiltInPlugins.cs", "CodeAlta.Tests/PluginNeutralContractSourceInverse.cs", "CodeAlta.Tests/PluginNeutralContractSourceTests.cs", "CodeAlta.Tests/PluginGitHubBackendSeparationSourceTests.cs", "CodeAlta.Desktop.Tests/CodeAlta.Desktop.Tests.csproj" })
            Assert.AreEqual(Inverse.Restore(path, Read(path)), Inverse.RestoreMcpInput(path, Read(path)), path);
        foreach (var path in new[] { "CodeAlta.Plugin.Statistics/CodeAlta.Plugin.Statistics.csproj", "CodeAlta.Tests/PluginSessionEventProjectionSourceTests.cs" })
            Assert.AreEqual(Inverse.Restore(path, Read(path)), Inverse.RestoreNeutralInput(path, Read(path)), path);
        foreach (var path in new[] { Inverse.BackendPath, Inverse.LegacyPath })
            Assert.AreEqual(Inverse.Restore(path, Read(path)), Inverse.RestoreProjectionInput(path, Read(path)), path);
        Assert.AreEqual("unmapped\n", Inverse.RestoreMcpInput(Inverse.BackendPath, "unmapped\n"));
        Assert.AreEqual("unmapped\n", Inverse.RestoreNeutralInput(Inverse.BackendPath, "unmapped\n"));
        Assert.AreEqual("unmapped\n", Inverse.RestoreProjectionInput("CodeAlta.Plugin.Statistics/CodeAlta.Plugin.Statistics.csproj", "unmapped\n"));
        // Actual Desktop history/workspace and assembly-boundary admission remain separately parent-owned.
    }

    private static void AssertRenderer()
    {
        var body = SourceTestText.Canonicalize(Inverse.OldRenderer);
        body = Adapt(body, "    private static class StatisticsVisualRenderer\n", "    internal static class StatisticsVisualRenderer\n");
        body = Adapt(body, "(TurnStatistics turn)", "(StatisticsPresentation turn)", 2);
        body = Adapt(body, "StatisticsMarkdownRenderer.RenderTurnSummaryMarkup(turn)", "RenderTurnSummaryMarkup(turn)");
        foreach (var name in new[] { "RenderMetricTable", "RenderUsageTable", "RenderToolBucketTable" })
            body = Adapt(body, "StatisticsMarkdownRenderer." + name + "(turn)", "turn." + name + "()");
        body = Adapt(body, "            };\n    }\n\n", "            };\n    }\n");
        body = string.Join('\n', body.Split('\n').Select(line =>
        {
            if (line.Length == 0) return line;
            Assert.IsTrue(line.StartsWith("    ", StringComparison.Ordinal));
            return line[4..]; // Only the extracted class's one nesting level changes.
        }));
        var markup = SourceTestText.Canonicalize(Inverse.OldMarkup);
        markup = Adapt(markup, "        public static string RenderTurnSummaryMarkup(TurnStatistics turn)\n", "    private static string RenderTurnSummaryMarkup(StatisticsPresentation turn)\n");
        markup = Adapt(markup, "            =>", "        =>");
        markup = Adapt(markup, "{TurnStatisticsTitle}", "{turn.Title}");
        markup = Adapt(markup, "RenderTurnSummarySuffix(turn)", "turn.RenderTurnSummarySuffix()");
        Assert.AreEqual(SourceTestText.Canonicalize(Inverse.NewMarkup), markup);
        const string header = "    private static Markup CreateHeaderMarkup(string markup)\n";
        body = Adapt(body, header, markup + header);
        Assert.AreEqual(SourceTestText.Canonicalize(RendererPrelude) + body, Read(RendererPath));
    }

    private static string Adapt(string source, string before, string after, int count = 1)
    {
        Assert.AreEqual(count, source.Split(before, StringSplitOptions.None).Length - 1, before);
        return source.Replace(before, after, StringComparison.Ordinal);
    }

    private const string TerminalPath = "CodeAlta.Tui/Plugins/Statistics/StatisticsTerminalContributions.cs";
    private const string RendererPath = "CodeAlta.Tui/Plugins/Statistics/StatisticsVisualRenderer.cs";
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
    namespace CodeAlta.Plugin.Statistics;

    /// <summary>Deferred portable formatters over one already-built turn, borrowed by host presentation.</summary>
    internal sealed record StatisticsPresentation(
        string Title,
        Func<string> RenderTurnSummarySuffix,
        Func<string> RenderMetricTable,
        Func<string> RenderUsageTable,
        Func<string> RenderToolBucketTable);
    """ + "\n";

    private const string RendererPrelude = "using XenoAtom.Ansi;\nusing XenoAtom.Terminal.UI;\nusing XenoAtom.Terminal.UI.Controls;\nusing XenoAtom.Terminal.UI.Extensions.Markdown;\nusing XenoAtom.Terminal.UI.Geometry;\nusing XenoAtom.Terminal.UI.Styling;\n\nnamespace CodeAlta.Plugin.Statistics;\n\n";

    private const string Decorator = """
    using CodeAlta.Plugins.Abstractions;
    using CodeAlta.Plugins.Tui;

    namespace CodeAlta.Plugin.Statistics;

    internal static class StatisticsTerminalContributions
    {
        internal static PluginDerivedSessionEvent DecorateProjection(PluginDerivedSessionEvent projection, StatisticsPresentation turn)
            => new PluginTerminalDerivedSessionEvent
            {
                EventId = projection.EventId,
                Timestamp = projection.Timestamp,
                Markdown = projection.Markdown,
                RenderTarget = projection.RenderTarget,
                Payload = projection.Payload,
                DynamicContent = projection.DynamicContent,
                Remove = projection.Remove,
                DetailSections = projection.DetailSections.Select(section => new PluginTerminalDerivedSessionEventDetailSection
                {
                    Header = section.Header,
                    Markdown = section.Markdown,
                    VisualFactory = _ => StatisticsVisualRenderer.RenderTurnDetails(turn),
                }).ToArray(),
                VisualFactory = _ => StatisticsVisualRenderer.RenderTurnCard(turn),
            };
    }
    """ + "\n";
}
