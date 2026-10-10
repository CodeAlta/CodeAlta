namespace CodeAlta.Plugins.Abstractions.Tests;

[TestClass]
public sealed class PluginLandingCardContractTests
{
    [TestMethod]
    public void APlugin_PinsNoCardUnlessItSaysSo_AndTheCardsHaveTheirOwnPoint()
    {
        Assert.AreEqual(0, new Bare().GetLandingCards().Count());
        Assert.IsTrue(Enum.IsDefined(PluginPoint.LandingCard));
    }

    [TestMethod]
    public void ACard_NeedsAnIdentifierAndATitle()
    {
        Assert.IsNull(Card("numbers", "Numbers").Validate());
        Assert.IsNull(Card("a-b_c.1", new string('t', PluginLandingCardLimits.MaximumTitleLength)).Validate());
        StringAssert.Contains(Card("with space", "Numbers").Validate(), "identifier");
        StringAssert.Contains(Card(string.Empty, "Numbers").Validate(), "identifier");
        StringAssert.Contains(Card(new string('i', PluginLandingCardLimits.MaximumIdLength + 1), "Numbers").Validate(), "identifier");
        StringAssert.Contains(Card("numbers", " ").Validate(), "title");
        StringAssert.Contains(Card("numbers", new string('t', PluginLandingCardLimits.MaximumTitleLength + 1)).Validate(), "title");
        Assert.AreEqual(0, Card("numbers", "Numbers").Order);
        Assert.IsNull(Card("numbers", "Numbers").Icon);
    }

    [TestMethod]
    public void AnAction_RunsACommandOrOpensACanvas_NeverBothOrNeither()
    {
        var run = PluginLandingCardAction.RunCommand("Refresh", "numbers.refresh", "refresh-cw");
        var open = PluginLandingCardAction.OpenCanvas("Open", "board", "project:1");

        Assert.AreEqual(("Refresh", "numbers.refresh", (string?)null, "refresh-cw", false), (run.Label, run.Command, run.Canvas, run.Icon, run.Primary));
        Assert.AreEqual(("Open", "board", "project:1", (string?)null), (open.Label, open.Canvas, open.Key, open.Command));
        Assert.IsNull(run.Validate());
        Assert.IsNull(open.Validate());
        Assert.IsNull((open with { Primary = true, Key = null }).Validate());
        StringAssert.Contains(new PluginLandingCardAction { Label = "Both", Command = "go", Canvas = "board" }.Validate(), "exactly one");
        StringAssert.Contains(new PluginLandingCardAction { Label = "Neither" }.Validate(), "exactly one");
        StringAssert.Contains(new PluginLandingCardAction { Label = "Key", Command = "go", Key = "k" }.Validate(), "key");
        StringAssert.Contains(new PluginLandingCardAction { Label = " ", Command = "go" }.Validate(), "label");
        StringAssert.Contains(new PluginLandingCardAction { Label = new string('l', PluginLandingCardLimits.MaximumLabelLength + 1), Command = "go" }.Validate(), "label");
        Assert.ThrowsExactly<ArgumentException>(() => PluginLandingCardAction.RunCommand(" ", "go"));
        Assert.ThrowsExactly<ArgumentException>(() => PluginLandingCardAction.RunCommand("Go", " "));
        Assert.ThrowsExactly<ArgumentException>(() => PluginLandingCardAction.OpenCanvas("Open", " "));
    }

    [TestMethod]
    public void WhatACardShows_IsAFragmentAStatusAndItsActions()
    {
        var open = PluginLandingCardAction.OpenCanvas("Open", "board");

        var card = PluginLandingCard.Of("<p>3 open</p>", open) with { Status = "3 open", Tone = PluginStatusTone.Warning };

        Assert.AreEqual(("<p>3 open</p>", "3 open", PluginStatusTone.Warning), (card.Html, card.Status, card.Tone));
        Assert.AreSame(open, card.Actions.Single());
        Assert.AreEqual((string.Empty, (string?)null, PluginStatusTone.Info, 0), (new PluginLandingCard().Html, new PluginLandingCard().Status, new PluginLandingCard().Tone, new PluginLandingCard().Actions.Count));
        Assert.AreEqual(0, PluginLandingCard.Of("<p>alone</p>").Actions.Count);
        Assert.ThrowsExactly<ArgumentNullException>(() => PluginLandingCard.Of(null!));
        Assert.ThrowsExactly<ArgumentNullException>(() => PluginLandingCard.Of("<p/>", null!));
    }

    [TestMethod]
    public void TheContextOfACard_NamesTheSpaceAndTheProjectOfItsPlugin()
    {
        var context = new PluginLandingCardContext("space", "project");

        Assert.AreEqual(("space", "project"), (context.SpaceId, context.ProjectId));
        Assert.AreEqual(new PluginLandingCardContext(null, null), new PluginLandingCardContext(null, null));
    }

    [TestMethod]
    public void AServiceWithoutALandingPage_IgnoresTheInvalidationOfTheCards()
    {
        IPluginUiService ui = new NoopPluginUiService();

        ui.InvalidateLandingCards();
    }

    [TestMethod]
    public void AFigure_IsAValueAboveItsLabel_WithBothEncoded()
    {
        var html = PluginHtml.Stat("1.2M", "Tokens <in>");

        Assert.AreEqual("<div class=\"alta-stat\"><strong>1.2M</strong><span class=\"alta-muted\">Tokens &lt;in&gt;</span></div>", html);
        Assert.ThrowsExactly<ArgumentException>(() => PluginHtml.Stat(" ", "Tokens"));
        Assert.ThrowsExactly<ArgumentException>(() => PluginHtml.Stat("1", " "));
    }

    private static PluginLandingCardContribution Card(string id, string title)
        => new() { Id = id, Title = title, GetCard = static (_, _) => new ValueTask<PluginLandingCard?>((PluginLandingCard?)null) };

    private sealed class Bare : PluginBase;
}
