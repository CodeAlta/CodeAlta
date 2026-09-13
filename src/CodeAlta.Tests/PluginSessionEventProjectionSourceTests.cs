using System.Security.Cryptography;
using System.Text;

namespace CodeAlta.Tests;

/// <summary>Named checkout source reads and mandatory complete-original inverses only; no runtime or visual acquisition.</summary>
[TestClass]
public sealed class PluginSessionEventProjectionSourceTests
{
    [TestMethod]
    public void Contracts_IsolateTerminalFactoriesInOptionalAssembly()
    {
        var neutral = Read("CodeAlta.Plugins.Abstractions/PluginSessionEventProjection.cs");
        foreach (var forbidden in new[] { "XenoAtom.Terminal", "VisualFactory", "PluginSessionEventVisualContext", "Plugins.Tui" })
            Assert.IsFalse(neutral.Contains(forbidden, StringComparison.Ordinal), forbidden);
        var optional = Read("CodeAlta.Plugins.Tui/PluginTerminalSessionEventProjection.cs");
        foreach (var required in new[] { "PluginTerminalDerivedSessionEvent : PluginDerivedSessionEvent", "PluginTerminalDerivedSessionEventDetailSection : PluginDerivedSessionEventDetailSection", "PluginTerminalDynamicDerivedSessionEventContent : PluginDynamicDerivedSessionEventContent", "public virtual PluginSessionEventVisualFactory? VisualFactory => null;", "object? Payload", "not an RPC" })
            StringAssert.Contains(optional, required);
        StringAssert.Contains(Read("CodeAlta.Plugins.Tui/CodeAlta.Plugins.Tui.csproj"), "CodeAlta.Plugins.Abstractions.csproj");
        Assert.IsFalse(Read("CodeAlta.Plugins.Abstractions/CodeAlta.Plugins.Abstractions.csproj").Contains("Plugins.Tui", StringComparison.Ordinal));
    }

    [TestMethod]
    public void SharedProjectionAndStatistics_RetainBackendLogic()
    {
        var shared = Read("CodeAlta.Orchestration/Runtime/Plugins/SessionPluginDerivedEventProjector.cs");
        StringAssert.Contains(shared, "var contributionEvents = await contribution.ProjectAsync(");
        StringAssert.Contains(shared, "projected.AddRange(contributionEvents);");
        Assert.IsFalse(shared.Contains("Plugins.Tui", StringComparison.Ordinal));
        Assert.IsFalse(shared.Contains("VisualFactory", StringComparison.Ordinal));
        var statistics = PluginStatisticsBackendSeparationSourceInverse.Restore("CodeAlta.Plugin.Statistics/StatisticsPlugin.cs", Read("CodeAlta.Plugin.Statistics/StatisticsPlugin.cs"));
        StringAssert.Contains(statistics, "return new PluginTerminalDerivedSessionEvent");
        StringAssert.Contains(statistics, "new PluginTerminalDerivedSessionEventDetailSection");
        StringAssert.Contains(statistics, "VisualFactory = _ => StatisticsVisualRenderer.RenderTurnCard(statistics)");
        StringAssert.Contains(statistics, "VisualFactory = _ => StatisticsVisualRenderer.RenderTurnDetails(statistics)");
        AssertOriginal("CodeAlta.Plugin.Statistics/StatisticsPlugin.cs");
        AssertOriginal("CodeAlta.Plugin.Statistics/CodeAlta.Plugin.Statistics.csproj");
    }

    [TestMethod]
    public void TuiRouting_PreservesFactoryPrecedenceAndDeferredContexts()
    {
        var store = Read("CodeAlta.Tui/App/PluginTransientEventProjectionStore.cs");
        StringAssert.Contains(store, NewApply);
        StringAssert.Contains(store, NewRefresh);
        var coordinator = Read("CodeAlta.Tui/App/SessionRuntimeEventCoordinator.cs");
        foreach (var (before, after) in CoordinatorEdits())
        {
            StringAssert.Contains(coordinator, SourceTestText.Canonicalize(after));
            Assert.AreEqual(SourceTestText.Canonicalize(before).Count(c => c == '\n'), SourceTestText.Canonicalize(after).Count(c => c == '\n'));
        }
        var lines = coordinator.Split('\n');
        StringAssert.Contains(lines[280], "Task.Run(async () =>");
        StringAssert.Contains(lines[623], "_ = InvalidateProjectFileSearchAsync(session.WorkingDirectory);");
        AssertOriginal("CodeAlta.Tui/App/PluginTransientEventProjectionStore.cs");
        AssertOriginal("CodeAlta.Tui/App/SessionRuntimeEventCoordinator.cs");
    }

    [TestMethod]
    public void Preservation_RestoresCompleteOriginalsAndFrozenChains()
    {
        Assert.AreEqual(7, Originals.Count);
        foreach (var (path, hash) in Originals)
            foreach (var representation in Representations(Read(path)))
                Assert.AreEqual(hash, Hash(Restore(path, SourceTestText.DecodeSource(Encoding.UTF8.GetBytes(representation)))), path);
        // Existing mandatory chains remain active, with no hash/payload or reader modifications.
        var keybindings = new PluginKeyBindingExtractionSourceTests();
        keybindings.Preservation_RestoresCompletePreExtractionSources();
        keybindings.Preservation_PreservesUiContentAndHistoricalChains();
        new PluginFeedbackExtractionSourceTests().Preservation_RestoresCompletePreExtractionSourcesAndGuardChain();
    }

    private static void AssertOriginal(string path) => Assert.AreEqual(Originals.Single(value => value.Path == path).Hash, Hash(Restore(path, Read(path))), path);

    private static string Restore(string path, string source)
    {
        source = PluginStatisticsBackendSeparationSourceInverse.RestoreProjectionInput(path, source);
        switch (path)
        {
            case "CodeAlta.Plugins.Abstractions/PluginSessionEventProjection.cs":
                source = Undo(source, "using CodeAlta.Agent;\n\n", "using CodeAlta.Agent;\nusing XenoAtom.Terminal.UI;\n\n");
                source = Undo(source, ContributionAnchor, VisualDelegate + ContributionAnchor);
                source = Undo(source, EventAnchor, VisualContext + EventAnchor);
                source = Undo(source, "public record PluginDerivedSessionEvent\n", "public sealed record PluginDerivedSessionEvent\n");
                source = Undo(source, "public record PluginDerivedSessionEventDetailSection\n", "public sealed record PluginDerivedSessionEventDetailSection\n");
                source = Undo(source, DynamicAnchor, EventFactory + DynamicAnchor);
                source = Undo(source, NotifyAnchor, DynamicFactory + NotifyAnchor);
                return Undo(source, DetailTail, OldDetailTail);
            case "CodeAlta.Tui/App/PluginTransientEventProjectionStore.cs":
                source = Undo(source, NewUsing, OldUsing);
                source = Undo(source, NewApply, OldApply);
                return Undo(source, NewRefresh, OldRefresh);
            case "CodeAlta.Tui/App/SessionRuntimeEventCoordinator.cs":
                foreach (var (before, after) in CoordinatorEdits()) source = Undo(source, after, before);
                return source;
            case "CodeAlta.Plugin.Statistics/StatisticsPlugin.cs":
                source = Undo(source, NewUsing, OldUsing);
                source = Undo(source, "        return new PluginTerminalDerivedSessionEvent\n", "        return new PluginDerivedSessionEvent\n");
                return Undo(source, "                new PluginTerminalDerivedSessionEventDetailSection\n", "                new PluginDerivedSessionEventDetailSection\n");
            case "CodeAlta.Plugin.Statistics/CodeAlta.Plugin.Statistics.csproj":
                return Undo(PluginNeutralContractSourceInverse.Restore(path, source), NewProjectReference, OldProjectReference);
            case "CodeAlta.Plugins.Tests/StatisticsPluginTests.cs":
                source = Undo(source, NewUsing, OldUsing);
                source = Undo(source, "var cardVisualFactory = Assert.IsInstanceOfType<PluginTerminalDerivedSessionEvent>(result[0]).VisualFactory;", "var cardVisualFactory = result[0].VisualFactory;");
                return Undo(source, "var visualFactory = Assert.IsInstanceOfType<PluginTerminalDerivedSessionEventDetailSection>(completed.DetailSections[0]).VisualFactory;", "var visualFactory = completed.DetailSections[0].VisualFactory;");
            case "CodeAlta.Tests/SessionRuntimeEventCoordinatorTests.cs":
                source = Undo(source, NewUsing, OldUsing);
                source = Undo(source, "            () => new PluginTerminalDerivedSessionEvent\n", "            () => new PluginDerivedSessionEvent\n");
                return Undo(source, "private sealed class RecordingDynamicProjectionContent(OpenSessionState tab) : PluginTerminalDynamicDerivedSessionEventContent", "private sealed class RecordingDynamicProjectionContent(OpenSessionState tab) : PluginDynamicDerivedSessionEventContent");
            default: throw new AssertFailedException($"No complete-original inverse for {path}.");
        }
    }

    private static string Undo(string source, string after, string before)
    {
        after = SourceTestText.Canonicalize(after);
        before = SourceTestText.Canonicalize(before);
        Assert.IsTrue(after.Length > 0);
        Assert.AreEqual(1, source.Split(after, StringSplitOptions.None).Length - 1, after);
        return source.Replace(after, before, StringComparison.Ordinal);
    }

    private static string Read(string path)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CodeAlta.slnx"))) directory = directory.Parent;
        Assert.IsNotNull(directory);
        return RuntimeFileSearchInvalidationSourceInverse.RestoreInput(path, SourceTestText.DecodeSource(File.ReadAllBytes(Path.Combine(directory.FullName, PluginStatisticsBackendSeparationSourceInverse.CurrentPath(path)))));
    }

    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    private static IEnumerable<string> Representations(string text)
    {
        yield return text;
        yield return text.Replace("\n", "\r\n", StringComparison.Ordinal);
        var lines = text.Split('\n');
        yield return string.Concat(lines.Select((line, i) => i == lines.Length - 1 ? line : line + (i % 2 == 0 ? "\r\n" : "\n")));
    }

    // Parent-provided canonical raw-Git bbe583e8 anchors, captured before production edits.
    private static IReadOnlyList<(string Path, string Hash)> Originals =>
    [
        ("CodeAlta.Plugins.Abstractions/PluginSessionEventProjection.cs", "6E368CE2CA2F3918D00E061037BDD3B63DAEA10004ABAD62E356169000C7A5A9"),
        ("CodeAlta.Tui/App/PluginTransientEventProjectionStore.cs", "86CE18C0A35733A902446402A45FD7BD488186BCE03C3855AD9F2E5EB8874512"),
        ("CodeAlta.Tui/App/SessionRuntimeEventCoordinator.cs", "CD8615ED52201BBD0A6CC123D5FA481D0FC5F184E377C0B24E6559401FB01691"),
        ("CodeAlta.Plugin.Statistics/StatisticsPlugin.cs", "C60F233449CBF3171F200E23AAF9C45FA3EB1BDBAB6780E7732322801C92BA25"),
        ("CodeAlta.Plugin.Statistics/CodeAlta.Plugin.Statistics.csproj", "98B57A7D14E069C83B64CB044358664BA8D459429C88F6F64851D25C5AF9DAD6"),
        ("CodeAlta.Plugins.Tests/StatisticsPluginTests.cs", "9C579C30E234D6F1F0BBDDC92B3B6FB8A09E99FAC43888C2ACD280ACCA67A101"),
        ("CodeAlta.Tests/SessionRuntimeEventCoordinatorTests.cs", "CCA692A61D61F64E1FA4E28B89CE392360789F561F10FC39E120D94EE49F39EC"),
    ];

    private const string OldUsing = "using CodeAlta.Plugins.Abstractions;\n";
    private const string NewUsing = OldUsing + "using CodeAlta.Plugins.Tui;\n";
    private const string OldApply = "        => derivedEvent.DynamicContent?.VisualFactory ?? derivedEvent.VisualFactory;";
    private const string NewApply = "        => (derivedEvent.DynamicContent as PluginTerminalDynamicDerivedSessionEventContent)?.VisualFactory ?? (derivedEvent as PluginTerminalDerivedSessionEvent)?.VisualFactory;";
    private const string OldRefresh = "                VisualFactory = existing.DynamicContent.VisualFactory,";
    private const string NewRefresh = "                VisualFactory = (existing.DynamicContent as PluginTerminalDynamicDerivedSessionEventContent)?.VisualFactory,";
    private const string OldProjectReference = "    <ProjectReference Include=\"..\\CodeAlta.Plugins.Abstractions\\CodeAlta.Plugins.Abstractions.csproj\" />\n";
    private const string NewProjectReference = OldProjectReference + "    <ProjectReference Include=\"..\\CodeAlta.Plugins.Tui\\CodeAlta.Plugins.Tui.csproj\" />\n";
    private const string ContributionAnchor = "/// <summary>\n/// Describes a plugin contribution that can project replayed and live canonical session events into transient events.\n";
    private const string EventAnchor = "/// <summary>\n/// Describes a plugin-owned transient session event projection result.\n";
    private const string DynamicAnchor = "    /// <summary>\n    /// Gets optional dynamic Markdown content for projections that complete asynchronously after the event is first rendered.\n";
    private const string NotifyAnchor = "    /// <summary>Raises the <see cref=\"Changed\"/> event.</summary>\n";
    private const string VisualDelegate = """
    /// <summary>
    /// Creates a host-rendered visual for a plugin-derived session event or detail section.
    /// </summary>
    /// <param name="context">The visual rendering context.</param>
    /// <returns>The visual to render.</returns>
    public delegate Visual PluginSessionEventVisualFactory(PluginSessionEventVisualContext context);


    """;
    private const string VisualContext = """
    /// <summary>
    /// Provides host context to a plugin visual factory.
    /// </summary>
    public sealed record PluginSessionEventVisualContext
    {
        /// <summary>Gets the plugin-stable derived event identifier.</summary>
        public required string EventId { get; init; }

        /// <summary>Gets the optional renderer target/schema name.</summary>
        public string? RenderTarget { get; init; }

        /// <summary>Gets the current fallback Markdown for the visual being rendered.</summary>
        public string? Markdown { get; init; }

        /// <summary>Gets the optional structured payload.</summary>
        public object? Payload { get; init; }

        /// <summary>Gets the detail section header when rendering a detail section.</summary>
        public string? DetailHeader { get; init; }
    }


    """;
    private const string EventFactory = """
        /// <summary>
        /// Gets an optional visual factory for advanced frontend rendering that replaces the default Markdown card content. <see cref="Markdown"/> remains the clipboard and fallback representation.
        /// </summary>
        public PluginSessionEventVisualFactory? VisualFactory { get; init; }


    """;
    private const string DynamicFactory = """
        /// <summary>
        /// Gets an optional visual factory for advanced frontend rendering that replaces the default Markdown card content. <see cref="Markdown"/> remains the clipboard and fallback representation.
        /// </summary>
        public virtual PluginSessionEventVisualFactory? VisualFactory => null;


    """;
    private const string DetailTail = "    public required string Markdown { get; init; }\n}\n";
    private const string OldDetailTail = """
        public required string Markdown { get; init; }

        /// <summary>
        /// Gets an optional visual factory for advanced frontend rendering. <see cref="Markdown"/> remains the clipboard and fallback representation.
        /// </summary>
        public PluginSessionEventVisualFactory? VisualFactory { get; init; }

        /// <summary>
        /// Gets an optional visual factory for rendering the collapsible detail header. <see cref="Header"/> remains the fallback header text.
        /// </summary>
        public PluginSessionEventVisualFactory? HeaderVisualFactory { get; init; }
    }

    """;

    private static IEnumerable<(string Before, string After)> CoordinatorEdits()
    {
        yield return (CardFactory, CardFactory.Replace("private static", "internal static", StringComparison.Ordinal)
            .Replace("new PluginSessionEventVisualContext", "new CodeAlta.Plugins.Tui.PluginSessionEventVisualContext", StringComparison.Ordinal));
        yield return (DetailFactory, DetailFactory.Replace("private static", "internal static", StringComparison.Ordinal)
            .Replace("section.VisualFactory is null", "section is not CodeAlta.Plugins.Tui.PluginTerminalDerivedSessionEventDetailSection { VisualFactory: { } factory }", StringComparison.Ordinal)
            .Replace("section.VisualFactory(new PluginSessionEventVisualContext", "factory(new CodeAlta.Plugins.Tui.PluginSessionEventVisualContext", StringComparison.Ordinal));
        yield return (HeaderFactory, HeaderFactory.Replace("private static", "internal static", StringComparison.Ordinal)
            .Replace("section.HeaderVisualFactory is null", "section is not CodeAlta.Plugins.Tui.PluginTerminalDerivedSessionEventDetailSection { HeaderVisualFactory: { } factory }", StringComparison.Ordinal)
            .Replace("section.HeaderVisualFactory(new PluginSessionEventVisualContext", "factory(new CodeAlta.Plugins.Tui.PluginSessionEventVisualContext", StringComparison.Ordinal));
    }

    private const string CardFactory = """
        private static Func<Visual>? CreatePluginVisualFactory(PluginTransientEventProjection projection)
            => projection.VisualFactory is null
                ? null
                : () => projection.VisualFactory(new PluginSessionEventVisualContext
                {
                    EventId = projection.EventId,
                    RenderTarget = projection.RenderTarget,
                    Markdown = projection.Markdown,
                    Payload = projection.Payload,
                });
    """;
    private const string DetailFactory = """
        private static Func<Visual>? CreatePluginDetailVisualFactory(
            PluginTransientEventProjection projection,
            PluginDerivedSessionEventDetailSection section)
            => section.VisualFactory is null
                ? null
                : () => section.VisualFactory(new PluginSessionEventVisualContext
                {
                    EventId = projection.EventId,
                    RenderTarget = projection.RenderTarget,
                    Markdown = section.Markdown,
                    Payload = projection.Payload,
                    DetailHeader = section.Header,
                });
    """;
    private const string HeaderFactory = """
        private static Func<Visual>? CreatePluginDetailHeaderVisualFactory(
            PluginTransientEventProjection projection,
            PluginDerivedSessionEventDetailSection section)
            => section.HeaderVisualFactory is null
                ? null
                : () => section.HeaderVisualFactory(new PluginSessionEventVisualContext
                {
                    EventId = projection.EventId,
                    RenderTarget = projection.RenderTarget,
                    Markdown = section.Markdown,
                    Payload = projection.Payload,
                    DetailHeader = section.Header,
                });
    """;
}
