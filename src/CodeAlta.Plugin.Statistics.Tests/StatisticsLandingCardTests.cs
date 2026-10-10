using CodeAlta.Plugin.Statistics.History;
using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Plugin.Statistics.Tests;

/// <summary>The card the Statistics plugin pins on the landing page: a few figures, and never a reading it would start itself.</summary>
[TestClass]
public sealed class StatisticsLandingCardTests
{
    private static readonly PluginLandingCardContext Anywhere = new(null, null);

    private static async Task WaitAsync(Func<bool> condition, string what)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            Assert.IsTrue(watch.Elapsed < TimeSpan.FromSeconds(60), $"Timed out waiting for {what}.");
            await Task.Delay(10);
        }
    }

    [TestMethod]
    public async Task TheDesktopPlugin_PinsOneCard_ThatOpensItsCanvas_AndNoneWhereItReadsNothing()
    {
        await using var harness = await CanvasPluginHarness.CreateAsync();

        var card = harness.Plugin.GetLandingCards().Single();

        Assert.AreEqual(("overview", "Statistics", "chart-column"), (card.Id, card.Title, card.Icon));
        Assert.IsNull(card.Validate());
        Assert.IsTrue(card.Order < 0, "before the cards of other plugins");
        var shown = (await card.GetCard(Anywhere, CancellationToken.None))!;
        var open = shown.Actions.Single();
        Assert.AreEqual((StatisticsPlugin.CanvasId, (string?)null, true, "Open Statistics"), (open.Canvas, open.Command, open.Primary, open.Label));
        Assert.IsNull(open.Validate());
        Assert.IsTrue(harness.Plugin.GetCanvases().Any(canvas => canvas.Id == open.Canvas), "the action names a canvas the plugin has");

        // CodeAlta TUI, and a window whose host has no database: the plugin reads nothing and pins nothing.
        var store = await StoreHarness.CreateAsync();
        await using var _ = store;
        var tui = new StatisticsPlugin();
        tui.AttachRuntimeContext(new WindowServices(store.Database).Context(PluginFrontends.Terminal));
        var noDatabase = StatisticsPlugin.CreateForDesktop(new FakeJournalCatalog());
        noDatabase.AttachRuntimeContext(new WindowServices(NoopPluginServices.Create().Database).Context(PluginFrontends.Desktop));
        Assert.AreEqual(0, tui.GetLandingCards().Count());
        Assert.AreEqual(0, noDatabase.GetLandingCards().Count());
    }

    [TestMethod]
    public async Task BeforeTheStatisticsStarted_TheCardSaysSo()
    {
        // The engine waits an hour before it looks at anything.
        await using var harness = await CanvasPluginHarness.CreateAsync();

        var shown = (await harness.Plugin.GetLandingCardAsync(Anywhere, CancellationToken.None))!;

        StringAssert.Contains(shown.Html, "The statistics are starting.");
        Assert.IsNull(shown.Status);
        Assert.AreEqual(HistoryState.Starting, harness.Plugin.Statistics!.Status.State);
    }

    [TestMethod]
    public async Task WhileTheChoiceWaits_TheCardSendsToTheCanvas_AndNeverChoosesOrStartsAReading()
    {
        await using var harness = await CanvasPluginHarness.CreateAsync(startDelay: TimeSpan.Zero);
        var builder = EngineHarness.Session("s1", DateTimeOffset.UtcNow.AddDays(-1), runs: 3);
        harness.Journals.Set(builder, EngineHarness.EndOf(builder, DateTimeOffset.UtcNow.AddDays(-1), 3));
        await WaitAsync(() => harness.Plugin.Statistics!.Status.State == HistoryState.NeedsChoice, "the first-time state");

        PluginLandingCard? shown = null;
        for (var asked = 0; asked < 3; asked++)
        {
            shown = await harness.Plugin.GetLandingCardAsync(new PluginLandingCardContext("work", null), CancellationToken.None);
        }

        StringAssert.Contains(shown!.Html, "Choose in Statistics how much of your history to read.");
        Assert.AreEqual(("Not set up", PluginStatusTone.Warning), (shown.Status, shown.Tone));
        Assert.AreEqual(StatisticsPlugin.CanvasId, shown.Actions.Single().Canvas);
        Assert.IsFalse(shown.Html.Contains(PluginHtml.StatClass, StringComparison.Ordinal), "no figure before the user chose");
        var status = harness.Plugin.Statistics!.Status;
        Assert.AreEqual((HistoryState.NeedsChoice, (string?)null, 0), (status.State, status.Choice, status.SessionsDone), "asking for the card chose nothing and read nothing");
    }

    [TestMethod]
    public async Task OnceTheHistoryIsRead_TheCardShowsTheFiguresOfTheLastDays()
    {
        await using var harness = await CanvasPluginHarness.CreateAsync(startDelay: TimeSpan.Zero);
        // The card counts the last seven days of the clock of the computer: the session is of yesterday.
        var start = DateTimeOffset.UtcNow.AddDays(-1);
        var builder = EngineHarness.Session("s1", start, runs: 3);
        harness.Journals.Set(builder, EngineHarness.EndOf(builder, start, 3));
        await WaitAsync(() => harness.Plugin.Statistics!.Status.State == HistoryState.NeedsChoice, "the first-time state");
        await harness.Plugin.Statistics!.ChooseHistoryAsync(HistoryChoice.All);
        await WaitAsync(() => harness.Plugin.Statistics!.Status.State == HistoryState.Done, "the end of the reading");

        var shown = (await harness.Plugin.GetLandingCardAsync(Anywhere, CancellationToken.None))!;

        Assert.AreEqual(("Last 7 days", PluginStatusTone.Muted), (shown.Status, shown.Tone));
        StringAssert.Contains(shown.Html, PluginHtml.Stat("1", "Sessions"));
        foreach (var label in new[] { "Your prompts", "Tokens", "Active time" }) StringAssert.Contains(shown.Html, $"<span class=\"alta-muted\">{label}</span>");
        Assert.AreEqual(StatisticsPlugin.CanvasId, shown.Actions.Single().Canvas);
        // A space the directory does not know filters nothing; the space that holds every project neither.
        StringAssert.Contains((await harness.Plugin.GetLandingCardAsync(new PluginLandingCardContext("not-a-space", null), CancellationToken.None))!.Html, PluginHtml.Stat("1", "Sessions"));
    }

    [TestMethod]
    public async Task WithNothingInTheLastDays_TheCardSaysSo()
    {
        await using var harness = await CanvasPluginHarness.CreateAsync(startDelay: TimeSpan.Zero);
        var start = DateTimeOffset.UtcNow.AddDays(-40);
        var builder = EngineHarness.Session("old", start, runs: 2);
        harness.Journals.Set(builder, EngineHarness.EndOf(builder, start, 2));
        await WaitAsync(() => harness.Plugin.Statistics!.Status.State == HistoryState.NeedsChoice, "the first-time state");
        await harness.Plugin.Statistics!.ChooseHistoryAsync(HistoryChoice.All);
        await WaitAsync(() => harness.Plugin.Statistics!.Status.State == HistoryState.Done, "the end of the reading");

        var shown = (await harness.Plugin.GetLandingCardAsync(Anywhere, CancellationToken.None))!;

        StringAssert.Contains(shown.Html, "No activity in the last 7 days.");
        Assert.IsFalse(shown.Html.Contains(PluginHtml.StatClass, StringComparison.Ordinal));
        Assert.AreEqual("Last 7 days", shown.Status);
        Assert.AreEqual(1, shown.Actions.Count, "the canvas has the older days");
    }

    [TestMethod]
    public async Task TheCard_IsAskedAgainWhenTheStateOfTheHistoryChanges_NotForEachStepOfAReading()
    {
        await using var harness = await CanvasPluginHarness.CreateAsync();
        var invalidations = () => harness.Services.RecordingUi.LandingInvalidations;
        var before = invalidations();

        harness.Plugin.OnLandingStatusChanged(new StatisticsStatus { State = HistoryState.NeedsChoice });
        Assert.AreEqual(before + 1, invalidations());
        harness.Plugin.OnLandingStatusChanged(new StatisticsStatus { State = HistoryState.NeedsChoice, SessionsTotal = 9 });
        Assert.AreEqual(before + 1, invalidations(), "the same state is not told again");
        harness.Plugin.OnLandingStatusChanged(new StatisticsStatus { State = HistoryState.Reading });
        for (var done = 1; done <= 20; done++)
        {
            harness.Plugin.OnLandingStatusChanged(new StatisticsStatus { State = HistoryState.Reading, SessionsTotal = 20, SessionsDone = done });
        }

        Assert.AreEqual(before + 2, invalidations(), "the progress of a reading never asks the page to read the card");
        harness.Plugin.OnLandingStatusChanged(new StatisticsStatus { State = HistoryState.Done });
        Assert.AreEqual(before + 3, invalidations(), "the figures are there: the card is read again");
    }

    [TestMethod]
    public void ACount_IsWrittenInAFewCharacters()
    {
        Assert.AreEqual("0", StatisticsPlugin.Compact(0));
        Assert.AreEqual("0", StatisticsPlugin.Compact(-5));
        Assert.AreEqual("950", StatisticsPlugin.Compact(950));
        Assert.AreEqual("9,999", StatisticsPlugin.Compact(9999));
        Assert.AreEqual("12.3K", StatisticsPlugin.Compact(12_345));
        Assert.AreEqual("4.1M", StatisticsPlugin.Compact(4_100_000));
        Assert.AreEqual("1.2B", StatisticsPlugin.Compact(1_234_000_000));
        Assert.AreEqual("999.9K", StatisticsPlugin.Compact(999_949));
        Assert.AreEqual("1M", StatisticsPlugin.Compact(999_960));
        Assert.AreEqual("1B", StatisticsPlugin.Compact(999_960_000));
    }
}
