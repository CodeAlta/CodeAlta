using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Plugins.Tests;

[TestClass]
public sealed class PluginLandingCardRegistrationTests
{
    [TestMethod]
    public async Task Activation_KeepsTheValidCards_AndLeavesOutTheExtrasAndTheInvalidOnesWithAWarning()
    {
        var registry = new PluginContributionRegistry();
        var activator = new PluginRuntimeActivator(registry);
        var discovered = new DiscoveredPluginType { Type = typeof(TooManyCardsPlugin), Descriptor = PluginDescriptorFactory.FromType(typeof(TooManyCardsPlugin)) };

        var result = await activator.ActivateAsync(discovered, null, null, new PluginActivationOptions { HostInfo = HostInfo() });

        Assert.IsTrue(result.Succeeded, "a card that is left out is a warning, not a failure of the plugin");
        var kept = registry.GetSnapshot().Where(static item => item.Handle.Point == PluginPoint.LandingCard).ToArray();
        CollectionAssert.AreEqual(new[] { "first", "second" }, kept.Select(static item => ((PluginLandingCardContribution)item.Contribution).Id).ToArray(), "in the order of the cards, the lower first");
        CollectionAssert.AreEqual(new[] { "first", "second" }, kept.Select(static item => item.Handle.NaturalName).ToArray());
        var warnings = result.Diagnostics.Where(static diagnostic => diagnostic.Severity == PluginDiagnosticSeverity.Warning).Select(static diagnostic => diagnostic.Message).ToArray();
        Assert.AreEqual(4, warnings.Length, string.Join(Environment.NewLine, warnings));
        Assert.IsTrue(warnings.Any(static message => message.Contains("'bad id'", StringComparison.Ordinal) && message.Contains("identifier", StringComparison.Ordinal)));
        Assert.IsTrue(warnings.Any(static message => message.Contains("'untitled'", StringComparison.Ordinal) && message.Contains("title", StringComparison.Ordinal)));
        Assert.IsTrue(warnings.Any(static message => message.Contains("'first'", StringComparison.Ordinal) && message.Contains("repeats", StringComparison.Ordinal)), "an identifier is used once");
        Assert.IsTrue(warnings.Any(static message => message.Contains("'third'", StringComparison.Ordinal) && message.Contains("one too many", StringComparison.Ordinal)), "a plugin pins two cards");

        await result.ActivePlugin!.DeactivateAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(0, registry.GetSnapshot().Count(static item => item.Handle.Point == PluginPoint.LandingCard), "the cards go with the plugin");
    }

    [TestMethod]
    public async Task TwoPluginsMayUseTheSameCardIdentifier_WithoutShadowingEachOther()
    {
        var registry = new PluginContributionRegistry();
        var activator = new PluginRuntimeActivator(registry);
        foreach (var type in new[] { typeof(CardsPlugin), typeof(OtherCardsPlugin) })
        {
            var discovered = new DiscoveredPluginType { Type = type, Descriptor = PluginDescriptorFactory.FromType(type) };
            await activator.ActivateAsync(discovered, null, null, new PluginActivationOptions { HostInfo = HostInfo() });
        }

        Assert.AreEqual(2, registry.GetSnapshot().Count(static item => item.Contribution is PluginLandingCardContribution { Id: "numbers" }));
        Assert.AreEqual(0, registry.GetDiagnostics().Count(static diagnostic => diagnostic.Message.Contains("shadows", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task TheAdapter_AsksEachCardForTheSpace_LeavesOutTheHiddenOnes_AndIsolatesTheOnesThatFail()
    {
        var registry = new PluginContributionRegistry();
        var activator = new PluginRuntimeActivator(registry);
        var cards = await ActivateAsync(activator, typeof(CardsPlugin));
        var failing = await ActivateAsync(activator, typeof(FailingCardsPlugin));
        var adapter = new PluginContributionAdapterService(registry);
        var plugin = (CardsPlugin)cards.Instance!;

        var entries = await adapter.GetLandingCardEntriesAsync([cards, failing], "space-1", spaceProjectIds: null, TimeSpan.FromSeconds(30));

        CollectionAssert.AreEqual(new[] { "numbers", "throws", "throws-later" }, entries.Select(static entry => entry.Contribution.Id).ToArray(), "the hidden card is not listed");
        var numbers = entries[0];
        Assert.IsFalse(numbers.Failed);
        Assert.AreEqual("<p>3 open</p>", numbers.Card!.Html);
        Assert.AreEqual(new PluginLandingCardContext("space-1", null), plugin.LastContext, "a plugin of the application has no project");
        Assert.IsTrue(entries[1].Failed && entries[1].Card is null, "a card that throws at once is a failed card");
        Assert.IsTrue(entries[2].Failed && entries[2].Card is null, "and so is one that throws after it waited");

        plugin.Hide = true;
        var hidden = await adapter.GetLandingCardEntriesAsync([cards, failing], null, null, TimeSpan.FromSeconds(30));
        Assert.IsFalse(hidden.Any(static entry => entry.Contribution.Id == "numbers"), "the plugin leaves its card out by returning nothing");
        Assert.AreEqual(new PluginLandingCardContext(null, null), plugin.LastContext);

        // A plugin that is not among the active ones has no card, whatever the registry still holds.
        Assert.AreEqual(0, (await adapter.GetLandingCardEntriesAsync([], "space-1", null, TimeSpan.FromSeconds(30))).Count);
        await cards.DeactivateAsync(TimeSpan.FromSeconds(5));
        var after = await adapter.GetLandingCardEntriesAsync([failing], "space-1", null, TimeSpan.FromSeconds(30));
        Assert.IsFalse(after.Any(static entry => entry.Contribution.Id is "numbers" or "quiet"), "the cards of a plugin that stopped are gone");
        await failing.DeactivateAsync(TimeSpan.FromSeconds(5));
    }

    [TestMethod]
    public async Task ACardThatNeverAnswers_FailsAtTheLimit_AndDoesNotHoldTheOthers()
    {
        var registry = new PluginContributionRegistry();
        var activator = new PluginRuntimeActivator(registry);
        var cards = await ActivateAsync(activator, typeof(CardsPlugin));
        var stuck = await ActivateAsync(activator, typeof(StuckCardPlugin));
        var diagnostics = new PluginRuntimeDiagnosticStore();
        var adapter = new PluginContributionAdapterService(registry, diagnostics);
        var plugin = (StuckCardPlugin)stuck.Instance!;
        try
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var entries = await adapter.GetLandingCardEntriesAsync([cards, stuck], "space-1", null, TimeSpan.FromSeconds(2));

            Assert.IsTrue(watch.Elapsed < TimeSpan.FromSeconds(30), "the reading ends at the limit, whatever the plugin does with its token");
            Assert.IsFalse(entries.Single(static entry => entry.Contribution.Id == "numbers").Failed);
            Assert.IsTrue(entries.Single(static entry => entry.Contribution.Id == "stuck").Failed);
            Assert.AreEqual(1, diagnostics.GetSnapshot().Count(static diagnostic => diagnostic.Message.Contains("did not answer in time", StringComparison.Ordinal)));

            // The page asks again while the handler still runs: the plugin is not called a second time, and the failure is not told twice.
            var again = await adapter.GetLandingCardEntriesAsync([cards, stuck], "space-1", null, TimeSpan.FromMilliseconds(200));
            Assert.IsTrue(again.Single(static entry => entry.Contribution.Id == "stuck").Failed);
            Assert.AreEqual(1, plugin.Calls, "one call runs at a time for a card and a space");
            Assert.AreEqual(1, diagnostics.GetSnapshot().Count(static diagnostic => diagnostic.Message.Contains("did not answer in time", StringComparison.Ordinal)));
            // Another space is another question.
            await adapter.GetLandingCardEntriesAsync([stuck], "space-2", null, TimeSpan.FromMilliseconds(200));
            Assert.AreEqual(2, plugin.Calls);

            // Once the handler returned, the card is asked again and shown.
            plugin.Release.TrySetResult();
            var watchRelease = System.Diagnostics.Stopwatch.StartNew();
            PluginLandingCardEntry? shown = null;
            while (shown is not { Failed: false } && watchRelease.Elapsed < TimeSpan.FromSeconds(30))
            {
                shown = (await adapter.GetLandingCardEntriesAsync([stuck], "space-1", null, TimeSpan.FromSeconds(2))).Single();
            }

            Assert.AreEqual("<p>late</p>", shown!.Card?.Html);
        }
        finally
        {
            plugin.Release.TrySetResult();
            await cards.DeactivateAsync(TimeSpan.FromSeconds(5));
            await stuck.DeactivateAsync(TimeSpan.FromSeconds(5));
        }
    }

    [TestMethod]
    public async Task TheCardOfAPluginOfAProject_IsAskedForTheSpacesThatHaveItsProject_AndIsAboutThatProject()
    {
        var registry = new PluginContributionRegistry();
        var activator = new PluginRuntimeActivator(registry);
        var cards = await ActivateAsync(activator, typeof(CardsPlugin));
        var seen = new List<PluginLandingCardContext>();
        registry.Register(cards.Descriptor, PluginScope.Project, "Project-1", Path.GetTempPath(), PluginPoint.LandingCard,
        [
            new PluginLandingCardContribution
            {
                Id = "of-project", Title = "Of the project",
                GetCard = (context, _) => { lock (seen) seen.Add(context); return new ValueTask<PluginLandingCard?>(PluginLandingCard.Of("<p>project</p>")); },
            },
        ], 1);
        var adapter = new PluginContributionAdapterService(registry);

        var everywhere = await adapter.GetLandingCardEntriesAsync([cards], "default", spaceProjectIds: null, TimeSpan.FromSeconds(30));
        var inSpace = await adapter.GetLandingCardEntriesAsync([cards], "work", new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "project-1" }, TimeSpan.FromSeconds(30));
        var elsewhere = await adapter.GetLandingCardEntriesAsync([cards], "other", new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "project-2" }, TimeSpan.FromSeconds(30));

        Assert.AreEqual(new PluginLandingCardContext("default", "Project-1"), everywhere.Single(static entry => entry.Contribution.Id == "of-project").Context, "the space that holds every project");
        Assert.AreEqual(new PluginLandingCardContext("work", "Project-1"), inSpace.Single(static entry => entry.Contribution.Id == "of-project").Context);
        Assert.IsFalse(elsewhere.Any(static entry => entry.Contribution.Id == "of-project"), "a space that does not have the project does not show its card");
        Assert.IsTrue(elsewhere.Any(static entry => entry.Contribution.Id == "numbers"), "the card of a plugin of the application is in every space");
        Assert.AreEqual(2, seen.Count, "the plugin is not asked for a space its project is not in");
        await cards.DeactivateAsync(TimeSpan.FromSeconds(5));
    }

    [TestMethod]
    public async Task TheAdapter_RefusesANullListOfPluginsAndALimitThatIsNotPositive()
    {
        var adapter = new PluginContributionAdapterService(new PluginContributionRegistry());

        await Assert.ThrowsExactlyAsync<ArgumentNullException>(async () => await adapter.GetLandingCardEntriesAsync(null!, null, null, TimeSpan.FromSeconds(1)));
        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(async () => await adapter.GetLandingCardEntriesAsync([], null, null, TimeSpan.Zero));
    }

    private static async Task<ActivePluginInstance> ActivateAsync(PluginRuntimeActivator activator, Type type)
    {
        var result = await activator.ActivateAsync(new DiscoveredPluginType { Type = type, Descriptor = PluginDescriptorFactory.FromType(type) }, null, null,
            new PluginActivationOptions { HostInfo = HostInfo() });
        Assert.IsTrue(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.Message)));
        return result.ActivePlugin!;
    }

    private static PluginHostInfo HostInfo() => new()
    {
        ApplicationName = "CodeAlta.Tests", Version = "1.0.0", HostApiVersion = "1.0.0", UserDataDirectory = Path.GetTempPath(), IsHeadless = true,
    };

    private static PluginLandingCardContribution Card(string id, string title, int order = 0)
        => new() { Id = id, Title = title, Order = order, GetCard = static (_, _) => new ValueTask<PluginLandingCard?>(PluginLandingCard.Of("<p>card</p>")) };

    public sealed class TooManyCardsPlugin : PluginBase
    {
        public override IEnumerable<PluginLandingCardContribution> GetLandingCards()
        {
            yield return Card("second", "Second", order: 2);
            yield return Card("bad id", "Bad");
            yield return Card("untitled", " ");
            yield return Card("first", "First", order: 1);
            yield return Card("first", "First again");
            yield return Card("third", "Third");
        }
    }

    public sealed class CardsPlugin : PluginBase
    {
        /// <summary>The context the card was last asked for.</summary>
        public PluginLandingCardContext? LastContext { get; private set; }

        public bool Hide { get; set; }

        public override IEnumerable<PluginLandingCardContribution> GetLandingCards()
        {
            yield return new PluginLandingCardContribution
            {
                Id = "numbers", Title = "Numbers",
                GetCard = (context, _) =>
                {
                    LastContext = context;
                    return new ValueTask<PluginLandingCard?>(Hide ? null : PluginLandingCard.Of("<p>3 open</p>"));
                },
            };
            yield return new PluginLandingCardContribution { Id = "quiet", Title = "Quiet", Order = 1, GetCard = static (_, _) => new ValueTask<PluginLandingCard?>((PluginLandingCard?)null) };
        }
    }

    public sealed class OtherCardsPlugin : PluginBase
    {
        public override IEnumerable<PluginLandingCardContribution> GetLandingCards()
        {
            yield return Card("numbers", "Other numbers");
        }
    }

    public sealed class FailingCardsPlugin : PluginBase
    {
        public override IEnumerable<PluginLandingCardContribution> GetLandingCards()
        {
            yield return new PluginLandingCardContribution { Id = "throws", Title = "Throws", Order = 5, GetCard = static (_, _) => throw new InvalidOperationException("boom") };
            yield return new PluginLandingCardContribution
            {
                Id = "throws-later", Title = "Throws later", Order = 6,
                GetCard = static async (_, token) => { await Task.Delay(10, token); throw new InvalidOperationException("boom later"); },
            };
        }
    }

    public sealed class StuckCardPlugin : PluginBase
    {
        /// <summary>Completed by the test when it is over: the handler ignores its token until then.</summary>
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private int _calls;

        /// <summary>Gets how many times the handler was called.</summary>
        public int Calls => Volatile.Read(ref _calls);

        public override IEnumerable<PluginLandingCardContribution> GetLandingCards()
        {
            yield return new PluginLandingCardContribution
            {
                Id = "stuck", Title = "Stuck", Order = 9,
                GetCard = async (_, _) =>
                {
                    Interlocked.Increment(ref _calls);
                    await Release.Task;
                    return PluginLandingCard.Of("<p>late</p>");
                },
            };
        }
    }
}
