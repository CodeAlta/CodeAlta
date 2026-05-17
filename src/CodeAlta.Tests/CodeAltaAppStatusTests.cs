using CodeAlta.Agent;
using CodeAlta.Models;
using CodeAlta.Presentation.Shell;
using XenoAtom.Terminal.UI.Styling;

namespace CodeAlta.Tests;

[TestClass]
public sealed class CodeAltaAppStatusTests
{
    [TestMethod]
    public void ResolveSelectionStatus_PrefersThreadSpecificStatus()
    {
        var snapshot = SelectionStatusResolver.Resolve(
            readyMessage: "Prompt ready",
            hasThreadStatus: true,
            threadStatusMessage: "Thinking...",
            threadStatusBusy: true,
            threadStatusTone: StatusTone.Info,
            promptEdited: false,
            promptUnavailable: true,
            promptUnavailableMessage: "Codex is unavailable.",
            promptUnavailableTone: StatusTone.Warning);

        Assert.AreEqual("Thinking...", snapshot.Message);
        Assert.IsTrue(snapshot.Busy);
        Assert.AreEqual(StatusTone.Info, snapshot.Tone);
    }

    [TestMethod]
    public void ResolveSelectionStatus_FallsBackToPromptUnavailableWhenThreadHasNoCustomStatus()
    {
        var snapshot = SelectionStatusResolver.Resolve(
            readyMessage: "Prompt ready",
            hasThreadStatus: false,
            threadStatusMessage: "Stopped",
            threadStatusBusy: false,
            threadStatusTone: StatusTone.Warning,
            promptEdited: false,
            promptUnavailable: true,
            promptUnavailableMessage: "Codex is unavailable.",
            promptUnavailableTone: StatusTone.Warning);

        Assert.AreEqual("Codex is unavailable.", snapshot.Message);
        Assert.IsFalse(snapshot.Busy);
        Assert.AreEqual(StatusTone.Warning, snapshot.Tone);
    }

    [TestMethod]
    public void ResolveSelectionStatus_UsesReadyMessageWhenNothingOverridesIt()
    {
        var snapshot = SelectionStatusResolver.Resolve(
            readyMessage: "Prompt ready",
            hasThreadStatus: false,
            threadStatusMessage: null,
            threadStatusBusy: false,
            threadStatusTone: StatusTone.Info,
            promptEdited: false,
            promptUnavailable: false,
            promptUnavailableMessage: null,
            promptUnavailableTone: StatusTone.Warning);

        Assert.AreEqual("Prompt ready", snapshot.Message);
        Assert.IsFalse(snapshot.Busy);
        Assert.AreEqual(StatusTone.Ready, snapshot.Tone);
    }

    [TestMethod]
    public void ResolveSelectionStatus_UsesEditedPromptStateWhenReady()
    {
        var snapshot = SelectionStatusResolver.Resolve(
            readyMessage: "Prompt ready",
            hasThreadStatus: false,
            threadStatusMessage: null,
            threadStatusBusy: false,
            threadStatusTone: StatusTone.Info,
            promptEdited: true,
            promptUnavailable: false,
            promptUnavailableMessage: null,
            promptUnavailableTone: StatusTone.Warning);

        Assert.AreEqual(StatusVisualFormatter.BuildPromptEditedStatusText(), snapshot.Message);
        Assert.IsFalse(snapshot.Busy);
        Assert.AreEqual(StatusTone.Info, snapshot.Tone);
        Assert.AreEqual(StatusVisualFormatter.BuildPromptEditedIconMarkup(), snapshot.IconMarkup);
    }

    [TestMethod]
    public void BuildStatusTextStyle_UsesGradientBrushForThinking()
    {
        var style = StatusVisualFormatter.BuildStatusTextStyle(
            Theme.Default,
            StatusVisualFormatter.BuildThinkingStatusText(TimeSpan.FromSeconds(5)),
            busy: true,
            tone: StatusTone.Info,
            thinkingAnimationPhase01: 0.25f);

        Assert.IsNotNull(style.ForegroundBrush);
        Assert.IsNull(style.Foreground);
    }

    [TestMethod]
    public void BuildStatusTextStyle_UsesSolidToneColorWhenIdle()
    {
        var style = StatusVisualFormatter.BuildStatusTextStyle(
            Theme.Default,
            "Prompt ready",
            busy: false,
            tone: StatusTone.Ready,
            thinkingAnimationPhase01: 0f);

        Assert.IsNull(style.ForegroundBrush);
        Assert.IsNotNull(style.Foreground);
    }

    [TestMethod]
    public void BuildThinkingStatusText_IncludesHumanFriendlyElapsedTime()
    {
        Assert.AreEqual("Thinking...", StatusVisualFormatter.BuildThinkingStatusText(TimeSpan.FromMilliseconds(500)));
        Assert.AreEqual("Thinking for 5 seconds...", StatusVisualFormatter.BuildThinkingStatusText(TimeSpan.FromSeconds(5)));
        Assert.AreEqual("Thinking for 4 minutes 30 seconds...", StatusVisualFormatter.BuildThinkingStatusText(TimeSpan.FromMinutes(4) + TimeSpan.FromSeconds(30)));
        Assert.AreEqual("Thinking for 1 hour 2 minutes...", StatusVisualFormatter.BuildThinkingStatusText(TimeSpan.FromHours(1) + TimeSpan.FromMinutes(2) + TimeSpan.FromSeconds(3)));
    }
}
