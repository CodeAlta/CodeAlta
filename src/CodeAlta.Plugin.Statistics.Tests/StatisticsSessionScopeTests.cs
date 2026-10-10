using CodeAlta.Agent;
using CodeAlta.Plugin.Statistics.Query;
using CodeAlta.Plugins;
using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Plugin.Statistics.Tests;

/// <summary>
/// The statistics of one session and its sub-agents in the window: the canvas a key opens for it, the line of the menu of a session,
/// and the button of the card of a turn. The filter its questions carry is in <see cref="StatisticsSessionFilterTests"/>.
/// </summary>
[TestClass]
public sealed class StatisticsSessionScopeTests
{
    // ---- the canvas of a session ----

    [TestMethod]
    public async Task TheLineOfASessionMenu_AndTheCommand_OpenTheCanvasOfThatSession()
    {
        await using var harness = await CanvasPluginHarness.CreateAsync();
        var buttons = harness.Plugin.GetUiContributions().OfType<PluginButtonContribution>().ToList();
        var commands = harness.Plugin.GetCommands().ToDictionary(command => command.Name);
        PluginCommandContext Context(string? session, string? project = null) => new()
        {
            Plugin = PluginDescriptorFactory.FromType(typeof(StatisticsPlugin)), Services = harness.Services, SessionId = session, ProjectId = project,
        };

        var menu = buttons.Single(button => button.Place == PluginButtonPlace.SessionMenu);
        Assert.AreEqual(("statistics-session", null, "statistics-session", "Statistics of this session", "chart-column"), (menu.Id, menu.Canvas, menu.Command, menu.Label, menu.Icon));
        Assert.IsNull(menu.Validate());
        var command = commands[menu.Command!];
        Assert.IsTrue(command.Availability.RequiresSession, "a line of a session menu, a card of a session: there is always one");
        Assert.IsFalse(command.Availability.RequiresProject, "a chat has no project");
        Assert.IsFalse(command.ShowInCommandPalette || command.ShowInHelp || command.ShowInCommandBar);
        Assert.IsNull(command.KeyBinding);

        // The session is the one the host gives the command: the row that was clicked, not the selection.
        Assert.AreEqual(PluginCommandResult.Handled, await command.Handler(Context("session-of-the-row", "project-0"), CancellationToken.None));
        var opened = harness.Services.RecordingCanvases.Opened.Single();
        Assert.AreEqual(("statistics", "session:session-of-the-row"), (opened.CanvasId, opened.Options!.Key));
        Assert.IsNull(opened.Options.ProjectId, "the canvas of the application, told apart by its key");
        Assert.IsNull(opened.Options.SessionId);
        Assert.AreEqual("session-of-the-row", StatisticsPlugin.SessionOfKey(opened.Options.Key));
        Assert.IsNull(StatisticsPlugin.ProjectOfKey(opened.Options.Key), "a session key is not a project key");
        Assert.IsNull(StatisticsPlugin.SessionOfKey("session:"));
        Assert.IsNull(StatisticsPlugin.SessionOfKey("project:project-0"));
        Assert.IsNull(StatisticsPlugin.SessionOfKey(null));

        foreach (var none in new[] { null, string.Empty, "  " })
        {
            Assert.AreNotEqual(PluginCommandResult.Handled, await command.Handler(Context(none), CancellationToken.None));
        }

        Assert.AreEqual(1, harness.Services.RecordingCanvases.Opened.Count, "no session, no canvas: never the canvas of every session");
        harness.Services.RecordingCanvases.Status = PluginCanvasOpenStatus.Unavailable;
        Assert.AreNotEqual(PluginCommandResult.Handled, await command.Handler(Context("session-of-the-row"), CancellationToken.None), "a host that cannot show it says so");
    }

    [TestMethod]
    public async Task TheCanvasOfASession_IsTitledWithTheSession_AndItsQuestionsAreLimitedToIt()
    {
        await using var query = await StatisticsSessionFilterTests.TreeAsync();
        await using var harness = await CanvasPluginHarness.CreateAsync(query.Store);

        var view = await harness.OpenAsync("session:session-1");
        Assert.AreEqual("Statistics: A title", view.Title, "the title the statistics read for the session");
        Assert.AreEqual("statistics", view.Script!.AppModule, "the same canvas, the same script");
        var unread = await harness.OpenAsync("session:01a00000-0000-7000-8000-000000000000");
        Assert.AreEqual("Statistics: 01a00000", unread.Title, "a session that is not read yet is named by the start of its identifier");
        var described = await harness.Plugin.GetCanvases().Single().Describe!(harness.Canvas, CancellationToken.None);
        StringAssert.Contains(described, "session:<session id>");

        // What the page of that canvas sends: the filter of the session with its sub-agents, on every question.
        var queries = harness.Plugin.Statistics!.Queries;
        const string Scope = """{"period":"all","frequency":"month","comparison":"none","filter":{"session":"session-1","withChildren":true}}""";
        var request = StatisticsJson.ParseRequest(Scope);
        var summary = await harness.Rpc.InvokeAsync("statistics.summary", "{\"request\":" + Scope + "}");
        Assert.AreEqual(StatisticsJson.Serialize(await queries.SummaryAsync(request)), summary.GetRawText());
        var sessions = await harness.Rpc.InvokeAsync("statistics.sessions", "{\"request\":" + Scope + ",\"sort\":\"tokens\"}");
        CollectionAssert.AreEquivalent(
            new[] { "session-1", "session-2", "grandchild" },
            sessions.GetProperty("rows").EnumerateArray().Select(static row => row.GetProperty("sessionId").GetString()).ToArray());

        var unknown = await harness.Rpc.InvokeAsync("statistics.summary", """{"request":{"period":"all","filter":{"session":"nobody","withChildren":true}}}""");
        Assert.IsTrue(unknown.GetProperty("tiles").EnumerateArray().All(static tile => tile.GetProperty("value").GetDouble() == 0));
        Assert.AreEqual("session-not-found", unknown.GetProperty("query").GetProperty("notes").EnumerateArray().Single().GetString());
        var blank = await harness.Rpc.FailureAsync("statistics.summary", """{"request":{"period":"all","filter":{"session":" "}}}""");
        Assert.AreEqual("invalid_request", blank!.Code);
    }

    [TestMethod]
    public void TheTitleOfATab_IsTheTitleOfTheSession_CutToALine()
    {
        Assert.AreEqual("Fix the parser", StatisticsPlugin.SessionLabel("Fix the parser", "01a124b3-fcbd"));
        Assert.AreEqual("Fix the parser", StatisticsPlugin.SessionLabel("  Fix\r\n the\tparser ", "01a124b3-fcbd"), "one line, single spaces");
        Assert.AreEqual("01a124b3", StatisticsPlugin.SessionLabel(null, "01a124b3-fcbd-7fa9"));
        Assert.AreEqual("01a124b3", StatisticsPlugin.SessionLabel("   ", "01a124b3-fcbd-7fa9"));
        Assert.AreEqual("short", StatisticsPlugin.SessionLabel(null, "short"));
        var cut = StatisticsPlugin.SessionLabel(new string('a', 200), "id");
        Assert.AreEqual(StatisticsPlugin.MaximumSessionTitleLength, cut.Length);
        Assert.IsTrue(cut.EndsWith('…'));
        // A pair of surrogates is never cut in two.
        var emoji = StatisticsPlugin.SessionLabel(new string('a', StatisticsPlugin.MaximumSessionTitleLength - 2) + "😀😀", "id");
        Assert.IsFalse(emoji.Any(char.IsSurrogate), emoji);
    }

    // ---- the card of a turn ----

    [TestMethod]
    public void TheDetailsOfACard_KeepTheirMarkdown_AndEndWithTheButtonOfTheSession()
    {
        const string Markdown = "| Metric | Value |\n| --- | ---: |\n| Tools <fast> & \"quoted\" | 2 |";

        var html = StatisticsPlugin.LinkedDetailsHtml(Markdown)!;

        // The Markdown is text of the fragment, which the window draws with its own component: nothing of it is markup.
        StringAssert.StartsWith(html, "<div class=\"alta-column\"><div class=\"alta-markdown\">| Metric | Value |");
        StringAssert.Contains(html, "Tools &lt;fast&gt; &amp; &quot;quoted&quot;");
        Assert.IsFalse(html.Contains("<fast>", StringComparison.Ordinal));
        // The foot: a button that names the command of the session, and nothing that runs.
        StringAssert.EndsWith(html, "</div><div class=\"alta-row\"><button type=\"button\" data-alta-command=\"statistics-session\">Session statistics</button></div></div>");
        Assert.IsFalse(html.Contains("<script", StringComparison.OrdinalIgnoreCase) || html.Contains("href", StringComparison.OrdinalIgnoreCase) || html.Contains(" on", StringComparison.OrdinalIgnoreCase));
        Assert.IsNull(StatisticsPlugin.LinkedDetailsHtml(new string('x', StatisticsPlugin.MaximumLinkedDetailsLength + 1)), "details too long to be given twice stay Markdown");
    }

    [TestMethod]
    public async Task TheCardOfATurn_HasTheButtonOfTheSession_WhateverTheApplication()
    {
        var start = new DateTimeOffset(2026, 6, 1, 10, 0, 0, TimeSpan.Zero);
        // A prompt, an answer and the idle update that ends the turn.
        AgentEvent[] events =
        [
            new AgentContentCompletedEvent(new("codex"), "card-session", start, new("run-1"), AgentContentKind.User, "prompt", null, "do it"),
            new AgentContentCompletedEvent(new("codex"), "card-session", start.AddSeconds(2), new("run-1"), AgentContentKind.Assistant, "answer", null, "done"),
            new AgentSessionUpdateEvent(new("codex"), "card-session", start.AddSeconds(4), new("run-1"), AgentSessionUpdateKind.Idle, null),
        ];
        // The card is made by a plugin that reads no session (the desktop application derives it with an instance of its own): it has the button too.
        var plugin = new StatisticsPlugin();
        var projection = plugin.GetSessionEventProjections().Single();

        var cards = await projection.ProjectAsync(
            new PluginSessionEventProjectionContext
            {
                Handle = new PluginContributionHandle { PluginRuntimeKey = "builtin:statistics", PluginTypeName = typeof(StatisticsPlugin).FullName!, Point = PluginPoint.SessionEventProjection, RuntimeContributionKey = "statistics", NaturalName = "statistics" },
                SessionId = "card-session",
                Events = events,
                IsReplay = true,
                IsCompleteBatch = true,
            },
            CancellationToken.None);

        var card = cards.Single();
        Assert.IsNull(card.Html, "the row of the card stays its summary");
        var details = card.DetailSections.Single();
        Assert.AreEqual("Detailed statistics", details.Header);
        Assert.IsFalse(string.IsNullOrWhiteSpace(details.Markdown), "CodeAlta TUI and Copy keep the Markdown");
        Assert.AreEqual(StatisticsPlugin.LinkedDetailsHtml(details.Markdown), details.Html);
        StringAssert.Contains(details.Html, "data-alta-command=\"statistics-session\"");
        Assert.IsFalse(details.Markdown.Contains("statistics-session", StringComparison.Ordinal), "the Markdown names no command");
    }
}
