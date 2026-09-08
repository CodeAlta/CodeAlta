using System.Security.Cryptography;
using System.Text;

namespace CodeAlta.Tests;

/// <summary>Mandatory complete-source inverses of parent-supplied fba989a9; no production or filesystem calls.</summary>
internal static class PluginStatisticsBackendSeparationSourceInverse
{
    internal static string CurrentPath(string path) => path == LegacyPath ? MovedPath : path;

    internal static string RestoreMcpInput(string path, string source) => path is
        "CodeAlta.Tui/App/CodeAltaBuiltInPlugins.cs" or "CodeAlta.Tests/PluginNeutralContractSourceInverse.cs" or
        "CodeAlta.Tests/PluginNeutralContractSourceTests.cs" or "CodeAlta.Tests/PluginGitHubBackendSeparationSourceTests.cs" or
        "CodeAlta.Desktop.Tests/CodeAlta.Desktop.Tests.csproj"
        ? Restore(path, source) : source;

    internal static string RestoreNeutralInput(string path, string source) => path is
        "CodeAlta.Plugin.Statistics/CodeAlta.Plugin.Statistics.csproj" or "CodeAlta.Tests/PluginSessionEventProjectionSourceTests.cs"
        ? Restore(path, source) : source;

    internal static string RestoreProjectionInput(string path, string source) => path is BackendPath or LegacyPath
        ? Restore(path, source) : source;

    internal static string Restore(string path, string source)
    {
        source = SourceTestText.DecodeSource(new UTF8Encoding(false, true).GetBytes(source));
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
        (BackendPath, "0F830333C3F3587B883EE1DB5AA75E42D0BEE3B3DDA64F297EBAAE6DD78DCC8F"),
        ("CodeAlta.Plugin.Statistics/CodeAlta.Plugin.Statistics.csproj", "6FB298E1BAA87ECCEFED7971468DB5AD884E7B059F135CBD80990237FC0A4968"),
        (LegacyPath, "FE86BFB0ED5FB6E4E6370EA3F39EDA60DB7B83DF2F3C465DF0C30194B902DBA1"),
        ("CodeAlta.Tui/App/CodeAltaBuiltInPlugins.cs", "2AB1630312337B42AE7F1C1987B276CE190CD875BB15DE4CBABF1A6F7ECBD04C"),
        ("CodeAlta.Tests/PluginSessionEventProjectionSourceTests.cs", "DE3BDC439DE6B959A499AB136716EC0DF3F3C520D0A00BF4B0BF15CAED85BB7C"),
        ("CodeAlta.Tests/PluginNeutralContractSourceInverse.cs", "48C89D89BC0288FC04F72660BFDCCFEBD4B8FDD6AFF032F91109B2D7B058E275"),
        ("CodeAlta.Tests/PluginNeutralContractSourceTests.cs", "EEFAB32EA089E41DB78810EB6B4C80F13005AE7E79843BD7AB587FAE1CCBB3D0"),
        ("CodeAlta.Tests/PluginGitHubBackendSeparationSourceTests.cs", "060FB602FA7F95926F4300547C16BC2DEB65C2E52291A923DB71DCA87BAA6D7C"),
        ("CodeAlta.Tests/PluginMcpBackendSeparationSourceInverse.cs", "049739229302448D62163EB034ADD7BB83177574DAA8F0F56165E25E364CA619"),
        ("CodeAlta.Desktop.Tests/CodeAlta.Desktop.Tests.csproj", "52CAA310F0FF3B869BAF70BE949CD1071A11524943399F038B60D2F98B71ADC7"),
    ];

    internal static IReadOnlyList<(string Before, string After)> Edits(string path) => path switch
    {
        BackendPath =>
        [
            (OldUsings, NewUsings),
            (CacheOwner, CacheOwner + Construction),
            (OldProjection, NewProjection),
            (OldRenderer + MarkdownClass, MarkdownClass),
            (OldTitle, NewTitle),
            (OldMarkup + OldSuffixSignature, NewSuffixSignature),
        ],
        "CodeAlta.Plugin.Statistics/CodeAlta.Plugin.Statistics.csproj" => [(OldReferences, NewReferences)],
        LegacyPath => [(OldRichTest, NewRichTest)],
        "CodeAlta.Tui/App/CodeAltaBuiltInPlugins.cs" => [(OldRegistration, NewRegistration)],
        "CodeAlta.Tests/PluginSessionEventProjectionSourceTests.cs" =>
        [
            (OldNativeRead, NewNativeRead),
            (ProjectionPrelude, ProjectionPrelude + ProjectionHook),
            (OldProjectionRead, NewProjectionRead),
        ],
        "CodeAlta.Tests/PluginNeutralContractSourceInverse.cs" => [(NeutralPrelude, NeutralPrelude + NeutralHook)],
        "CodeAlta.Tests/PluginNeutralContractSourceTests.cs" => [(OldPackageAssertion, NewPackageAssertion)],
        "CodeAlta.Tests/PluginGitHubBackendSeparationSourceTests.cs" => [(OldGitHubAssertion, NewGitHubAssertion)],
        "CodeAlta.Tests/PluginMcpBackendSeparationSourceInverse.cs" => [(McpPrelude, NewMcpPrelude)],
        "CodeAlta.Desktop.Tests/CodeAlta.Desktop.Tests.csproj" => [(McpLink, McpLink + StatisticsLink)],
        _ => throw new AssertFailedException("No Statistics backend inverse for " + path),
    };

    internal const string BackendPath = "CodeAlta.Plugin.Statistics/StatisticsPlugin.cs";
    internal const string LegacyPath = "CodeAlta.Plugins.Tests/StatisticsPluginTests.cs";
    internal const string MovedPath = "CodeAlta.Tests/StatisticsPluginTests.cs";
    internal const string OldUsings = "using CodeAlta.Plugins.Abstractions;\nusing CodeAlta.Plugins.Tui;\nusing XenoAtom.Ansi;\nusing XenoAtom.CommandLine;\nusing XenoAtom.Terminal.UI;\nusing XenoAtom.Terminal.UI.Controls;\nusing XenoAtom.Terminal.UI.Extensions.Markdown;\nusing XenoAtom.Terminal.UI.Geometry;\nusing XenoAtom.Terminal.UI.Styling;\n";
    internal const string NewUsings = "using CodeAlta.Plugins.Abstractions;\nusing XenoAtom.CommandLine;\n";
    internal const string CacheOwner = "    private readonly ConcurrentDictionary<string, PluginDerivedSessionEvent> _turnProjectionCache = new(StringComparer.Ordinal);\n";
    internal const string Construction = """
        private readonly Func<PluginDerivedSessionEvent, StatisticsPresentation, PluginDerivedSessionEvent>? _decorateProjection;

        /// <summary>Initializes a Statistics backend without terminal presentation.</summary>
        public StatisticsPlugin()
        {
        }

        // Decoration is deferred to each existing cache candidate, not contribution enumeration.
        internal StatisticsPlugin(Func<PluginDerivedSessionEvent, StatisticsPresentation, PluginDerivedSessionEvent> decorateProjection)
        {
            ArgumentNullException.ThrowIfNull(decorateProjection);
            _decorateProjection = decorateProjection;
        }
    """ + "\n";
    internal const string OldProjection = """
        private static PluginDerivedSessionEvent CreateProjection(string sessionId, PendingTurn turn)
        {
            var statistics = TurnStatisticsBuilder.BuildTurn(turn.Key, turn.SessionId, turn.RunId, turn.Events);
            return new PluginTerminalDerivedSessionEvent
            {
                EventId = $"statistics:{EscapeEventId(sessionId)}:{EscapeEventId(turn.Key)}",
                Timestamp = turn.Timestamp,
                Markdown = StatisticsMarkdownRenderer.RenderTurnSummary(statistics),
                DetailSections =
                [
                    new PluginTerminalDerivedSessionEventDetailSection
                    {
                        Header = "Detailed statistics",
                        Markdown = StatisticsMarkdownRenderer.RenderTurnDetails(statistics),
                        VisualFactory = _ => StatisticsVisualRenderer.RenderTurnDetails(statistics),
                    },
                ],
                VisualFactory = _ => StatisticsVisualRenderer.RenderTurnCard(statistics),
    """ + "\n" + Payload + "        };\n    }\n";
    internal const string NewProjection = """
        private PluginDerivedSessionEvent CreateProjection(string sessionId, PendingTurn turn)
        {
            var statistics = TurnStatisticsBuilder.BuildTurn(turn.Key, turn.SessionId, turn.RunId, turn.Events);
            var projection = new PluginDerivedSessionEvent
            {
                EventId = $"statistics:{EscapeEventId(sessionId)}:{EscapeEventId(turn.Key)}",
                Timestamp = turn.Timestamp,
                Markdown = StatisticsMarkdownRenderer.RenderTurnSummary(statistics),
                DetailSections =
                [
                    new PluginDerivedSessionEventDetailSection
                    {
                        Header = "Detailed statistics",
                        Markdown = StatisticsMarkdownRenderer.RenderTurnDetails(statistics),
                    },
                ],
    """ + "\n" + Payload + """
            };
            if (_decorateProjection is null)
            {
                return projection;
            }

            return _decorateProjection(projection, new StatisticsPresentation(
                StatisticsMarkdownRenderer.TurnStatisticsTitle,
                () => StatisticsMarkdownRenderer.RenderTurnSummarySuffix(statistics),
                () => StatisticsMarkdownRenderer.RenderMetricTable(statistics),
                () => StatisticsMarkdownRenderer.RenderUsageTable(statistics),
                () => StatisticsMarkdownRenderer.RenderToolBucketTable(statistics)));
        }
    """ + "\n";
    private const string Payload = """
                RenderTarget = RenderTarget,
                Payload = new
                {
                    statistics.Key,
                    statistics.SessionId,
                    statistics.RunId,
                    statistics.Duration,
                    ToolCalls = statistics.Tools.Count,
                    statistics.ReportedOperationCount,
                    statistics.ReportedInputTokens,
                    statistics.ReportedFreshInputTokens,
                    statistics.ReportedOutputTokens,
                    statistics.ReportedReasoningTokens,
                    statistics.EstimatedInputTokens,
                    statistics.EstimatedOutputTokens,
                    ToolInputCharacters = statistics.ToolInput.Characters,
                    GeneratedOutputCharacters = statistics.GeneratedOutput.Characters,
                    statistics.CompactionCount,
                    statistics.CompactionDuration,
                    Status = "ready",
                },
    """ + "\n";
    internal const string OldRenderer = """
        private static class StatisticsVisualRenderer
        {
            public static Visual RenderTurnCard(TurnStatistics turn)
                => new Collapsible(
                    CreateHeaderMarkup(StatisticsMarkdownRenderer.RenderTurnSummaryMarkup(turn)),
                    RenderTurnDetails(turn))
                {
                    IsExpanded = false,
                };

            public static Visual RenderTurnDetails(TurnStatistics turn)
            {
                var tables = new List<Visual>
                {
                    CreateTable(StatisticsMarkdownRenderer.RenderMetricTable(turn)),
                };

                var usageTable = StatisticsMarkdownRenderer.RenderUsageTable(turn);
                if (!string.IsNullOrWhiteSpace(usageTable))
                {
                    tables.Add(CreateTable(usageTable));
                }

                var toolBucketTable = StatisticsMarkdownRenderer.RenderToolBucketTable(turn);
                if (!string.IsNullOrWhiteSpace(toolBucketTable))
                {
                    tables.Add(CreateTable(toolBucketTable));
                }

                return new WrapHStack(tables.ToArray())
                    .Spacing(1)
                    .RunSpacing(1)
                    .MeasureMode(WrapMeasureMode.ConstrainToRun)
                    .HorizontalAlignment(Align.Stretch);
            }

            private static Markup CreateHeaderMarkup(string markup)
                => new(markup)
                {
                    Wrap = false,
                    HorizontalAlignment = Align.Stretch,
                    VerticalAlignment = Align.Start,
                };

            private static Visual CreateTable(string markdown)
                => new MarkdownControl(markdown.Trim())
                {
                    HorizontalAlignment = Align.Start,
                    VerticalAlignment = Align.Start,
                    Options = MarkdownRenderOptions.Default with
                    {
                        TableStyle = TableStyle.Minimal,
                        WrapCodeBlocks = true,
                        MaxCodeBlockHeight = 14,
                    },
                };
        }

    """ + "\n";
    internal const string MarkdownClass = "    private static class StatisticsMarkdownRenderer\n";
    internal const string OldTitle = "        private const string TurnStatisticsTitle = \"Turn statistics\";\n";
    internal const string NewTitle = "        internal const string TurnStatisticsTitle = \"Turn statistics\";\n";
    internal const string OldMarkup = "        public static string RenderTurnSummaryMarkup(TurnStatistics turn)\n            => $\"[bold]{TurnStatisticsTitle}[/]{AnsiMarkup.Escape(RenderTurnSummarySuffix(turn))}\";\n\n";
    internal const string NewMarkup = "    private static string RenderTurnSummaryMarkup(StatisticsPresentation turn)\n        => $\"[bold]{turn.Title}[/]{AnsiMarkup.Escape(turn.RenderTurnSummarySuffix())}\";\n\n";
    internal const string OldSuffixSignature = "        private static string RenderTurnSummarySuffix(TurnStatistics turn)\n";
    internal const string NewSuffixSignature = "        internal static string RenderTurnSummarySuffix(TurnStatistics turn)\n";
    internal const string OldReferences = "  <ItemGroup>\n    <PackageReference Include=\"XenoAtom.Terminal.UI.Extensions.Markdown\" />\n    <ProjectReference Include=\"..\\CodeAlta.Plugins.Abstractions\\CodeAlta.Plugins.Abstractions.csproj\" />\n    <ProjectReference Include=\"..\\CodeAlta.Plugins.Tui\\CodeAlta.Plugins.Tui.csproj\" />\n  </ItemGroup>\n";
    internal const string NewReferences = "  <ItemGroup>\n    <ProjectReference Include=\"..\\CodeAlta.Plugins.Abstractions\\CodeAlta.Plugins.Abstractions.csproj\" />\n    <InternalsVisibleTo Include=\"altatui\" />\n    <InternalsVisibleTo Include=\"CodeAlta.Tests\" />\n  </ItemGroup>\n";
    internal const string OldRichTest = "    public async Task Projection_EmitsCompletedTurnCardWithEstimatedStatsAndShellBucket()\n    {\n        var plugin = new StatisticsPlugin();\n";
    internal const string NewRichTest = "    public async Task Projection_EmitsCompletedTurnCardWithEstimatedStatsAndShellBucket()\n    {\n        var plugin = new StatisticsPlugin(StatisticsTerminalContributions.DecorateProjection);\n";
    internal const string OldRegistration = "            PluginType = typeof(StatisticsPlugin),\n            Factory = static () => new StatisticsPlugin(),\n";
    internal const string NewRegistration = "            PluginType = typeof(StatisticsPlugin),\n            Factory = static () => new StatisticsPlugin(StatisticsTerminalContributions.DecorateProjection),\n";
    internal const string OldNativeRead = "        var statistics = Read(\"CodeAlta.Plugin.Statistics/StatisticsPlugin.cs\");\n";
    internal const string NewNativeRead = "        var statistics = PluginStatisticsBackendSeparationSourceInverse.Restore(\"CodeAlta.Plugin.Statistics/StatisticsPlugin.cs\", Read(\"CodeAlta.Plugin.Statistics/StatisticsPlugin.cs\"));\n";
    internal const string ProjectionPrelude = "    private static string Restore(string path, string source)\n    {\n";
    internal const string ProjectionHook = "        source = PluginStatisticsBackendSeparationSourceInverse.RestoreProjectionInput(path, source);\n";
    internal const string OldProjectionRead = "        return SourceTestText.DecodeSource(File.ReadAllBytes(Path.Combine(directory.FullName, path)));\n";
    internal const string NewProjectionRead = "        return SourceTestText.DecodeSource(File.ReadAllBytes(Path.Combine(directory.FullName, PluginStatisticsBackendSeparationSourceInverse.CurrentPath(path))));\n";
    internal const string NeutralPrelude = "        source = PluginMcpBackendSeparationSourceInverse.RestoreNeutralInput(path, source);\n";
    internal const string NeutralHook = "        source = PluginStatisticsBackendSeparationSourceInverse.RestoreNeutralInput(path, source);\n";
    internal const string OldPackageAssertion = "        Assert.IsTrue(References(Project(\"CodeAlta.Plugin.Statistics/CodeAlta.Plugin.Statistics.csproj\"), \"PackageReference\").Contains(\"XenoAtom.Terminal.UI.Extensions.Markdown\"));\n";
    internal const string NewPackageAssertion = "        Assert.IsFalse(References(Project(\"CodeAlta.Plugin.Statistics/CodeAlta.Plugin.Statistics.csproj\"), \"PackageReference\").Any(name => name.StartsWith(\"XenoAtom.Terminal\", StringComparison.Ordinal)));\n";
    internal const string OldGitHubAssertion = "        RequireOnce(registration, \"            Factory = static () => new StatisticsPlugin(),\\n\");\n";
    internal const string NewGitHubAssertion = "        RequireOnce(PluginStatisticsBackendSeparationSourceInverse.Restore(\"CodeAlta.Tui/App/CodeAltaBuiltInPlugins.cs\", registration), \"            Factory = static () => new StatisticsPlugin(),\\n\");\n";
    internal const string McpPrelude = "        source = SourceTestText.DecodeSource(new UTF8Encoding(false, true).GetBytes(source));\n        foreach (var (before, after) in Edits(path))\n";
    internal const string NewMcpPrelude = "        source = SourceTestText.DecodeSource(new UTF8Encoding(false, true).GetBytes(source));\n        source = PluginStatisticsBackendSeparationSourceInverse.RestoreMcpInput(path, source);\n        foreach (var (before, after) in Edits(path))\n";
    internal const string McpLink = "    <Compile Include=\"../CodeAlta.Tests/PluginMcpBackendSeparationSourceInverse.cs\" Link=\"PluginMcpBackendSeparationSourceInverse.cs\" />\n";
    internal const string StatisticsLink = "    <Compile Include=\"../CodeAlta.Tests/PluginStatisticsBackendSeparationSourceInverse.cs\" Link=\"PluginStatisticsBackendSeparationSourceInverse.cs\" />\n";
}
