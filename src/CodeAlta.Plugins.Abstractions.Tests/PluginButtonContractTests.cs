namespace CodeAlta.Plugins.Abstractions.Tests;

[TestClass]
public sealed class PluginButtonContractTests
{
    [TestMethod]
    public void Factory_CreatesAButtonThatNamesItselfByItsIdentifier()
    {
        var button = PluginUi.Button(PluginButtonPlace.TitleBar, "statistics", "chart-column", "Statistics", order: 3) with { Canvas = "statistics" };

        Assert.AreEqual(PluginButtonPlace.TitleBar, button.Place);
        Assert.AreEqual("statistics", button.Id);
        Assert.AreEqual("statistics", button.Name, "the identifier is the natural name of the contribution");
        Assert.AreEqual("chart-column", button.Icon);
        Assert.AreEqual("Statistics", button.Label);
        Assert.AreEqual(3, button.Order);
        Assert.IsNull(button.Command);
        Assert.IsNull(button.GetState);
        Assert.IsNull(button.Validate());
        Assert.IsInstanceOfType<PluginUiContribution>(button);
        Assert.ThrowsExactly<ArgumentException>(() => PluginUi.Button(PluginButtonPlace.Rail, " ", "box", "x"));
        Assert.ThrowsExactly<ArgumentException>(() => PluginUi.Button(PluginButtonPlace.Rail, "x", "", "x"));
        Assert.ThrowsExactly<ArgumentNullException>(() => PluginUi.Button(PluginButtonPlace.Rail, "x", "box", null!));
    }

    [TestMethod]
    public void AButton_NamesExactlyOneOfACommandAndACanvas()
    {
        var button = PluginUi.Button(PluginButtonPlace.Rail, "open", "box", "Open");

        StringAssert.Contains(button.Validate(), "exactly one");
        Assert.IsNull((button with { Command = "release.open" }).Validate());
        Assert.IsNull((button with { Canvas = "board" }).Validate());
        StringAssert.Contains((button with { Command = "release.open", Canvas = "board" }).Validate(), "exactly one");
        StringAssert.Contains((button with { Command = " " }).Validate(), "exactly one");
    }

    [TestMethod]
    public void AButton_NeedsAWellFormedIdentifierLabelAndIcon()
    {
        var ready = PluginUi.Button(PluginButtonPlace.ProjectMenu, "release", "box", "Release") with { Command = "go" };

        Assert.IsNull(ready.Validate());
        StringAssert.Contains((ready with { Id = "has space" }).Validate(), "identifier");
        StringAssert.Contains((ready with { Id = string.Empty }).Validate(), "identifier");
        StringAssert.Contains((ready with { Id = new string('a', PluginButtonLimits.MaximumIdLength + 1) }).Validate(), "identifier");
        StringAssert.Contains((ready with { Label = " " }).Validate(), "label");
        StringAssert.Contains((ready with { Label = new string('a', PluginButtonLimits.MaximumLabelLength + 1) }).Validate(), "label");
        StringAssert.Contains((ready with { Icon = string.Empty }).Validate(), "icon");
        StringAssert.Contains((ready with { Place = (PluginButtonPlace)99 }).Validate(), "place");
        Assert.IsTrue(PluginButtonContribution.IsValidId("a-b_c.1"));
        Assert.IsFalse(PluginButtonContribution.IsValidId("a/b"));
        Assert.IsFalse(PluginButtonContribution.IsValidId(null));
    }

    [TestMethod]
    public void TheLimitsOfAPlace_AreTwoInTheTitleBarOneInTheRailAndAHandfulInMenus()
    {
        Assert.AreEqual(2, PluginButtonLimits.For(PluginButtonPlace.TitleBar));
        Assert.AreEqual(1, PluginButtonLimits.For(PluginButtonPlace.Rail));
        Assert.AreEqual(PluginButtonLimits.Menu, PluginButtonLimits.For(PluginButtonPlace.ProjectMenu));
        Assert.AreEqual(PluginButtonLimits.Menu, PluginButtonLimits.For(PluginButtonPlace.SessionMenu));
    }

    [TestMethod]
    public void ABadge_IsANumberADotOrABusyRing_AndAnIntegerConvertsToANumber()
    {
        PluginButtonBadge three = 3;
        PluginButtonBadge none = 0;

        Assert.AreEqual((PluginButtonBadgeKind.Count, 3), (three.Kind, three.Count));
        Assert.AreEqual(PluginButtonBadgeKind.None, none.Kind, "zero shows nothing");
        Assert.AreEqual(PluginButtonBadgeKind.None, PluginButtonBadge.Of(-4).Kind);
        Assert.AreEqual(PluginButtonBadgeKind.Dot, PluginButtonBadge.Dot.Kind);
        Assert.AreEqual(PluginButtonBadgeKind.Busy, PluginButtonBadge.Busy.Kind);
        Assert.AreEqual(PluginButtonBadgeKind.None, new PluginButtonState().Badge.Kind);
        Assert.AreEqual(PluginStatusTone.Info, PluginButtonState.Default.Tone);
        Assert.AreEqual(5, new PluginButtonState { Badge = 5 }.Badge.Count);
    }

    [TestMethod]
    public void AServiceThatDrawsNoButtons_IgnoresTheirInvalidation()
    {
        IPluginUiService ui = new NoopPluginUiService();

        ui.InvalidateButtons();
    }

    [TestMethod]
    public void TheContextOfAButton_NamesThePlaceAndTheSpaceProjectAndSession()
    {
        var context = new PluginButtonContext(PluginButtonPlace.SessionMenu, "space", "project", "session");

        Assert.AreEqual((PluginButtonPlace.SessionMenu, "space", "project", "session"), (context.Place, context.SpaceId, context.ProjectId, context.SessionId));
    }
}
