using CodeAlta.Desktop.Rpc;

namespace CodeAlta.Desktop.Tests;

[TestClass]
public sealed class DesktopClosingTests
{
    [TestMethod]
    public void AHostThatDoesNotClose_SaysNothing_AndCannotBeForced()
    {
        var closing = new DesktopClosing(new Clock());
        Assert.IsFalse(closing.IsClosing);
        Assert.IsFalse(closing.CanForceExit);
        Assert.IsNull(closing.Snapshot());
        Assert.IsNull(new BootService("00000000-0000-0000-0000-000000000001") { Closing = closing }.Status(new()).Closing);
    }

    [TestMethod]
    public void AClose_SaysTheStepItWaitsFor_AndOffersTheForceExitAfterItsDelay()
    {
        var clock = new Clock();
        var closing = new DesktopClosing(clock);
        closing.Begin();
        Assert.AreEqual(new BootClosing(null, 0, false), closing.Snapshot());
        closing.Step("sessions");
        clock.Now += TimeSpan.FromSeconds(4);
        Assert.AreEqual(new BootClosing("sessions", 4, false), closing.Snapshot());
        Assert.IsFalse(closing.CanForceExit);
        // A second request keeps the time of the first.
        closing.Begin();
        clock.Now += DesktopClosing.ForceExitDelay;
        Assert.IsTrue(closing.CanForceExit);
        Assert.AreEqual(new BootClosing("sessions", 14, true),
            new BootService("00000000-0000-0000-0000-000000000001") { Closing = closing }.Status(new()).Closing);
    }

    [TestMethod]
    public void ForceExit_IsUnavailableWithoutAHost_AndRefusedWhenTheHostDoesNotAllowIt()
    {
        Assert.AreEqual("unavailable", new DesktopShellService().ForceExit(new()).Status);
        var asked = 0;
        Assert.AreEqual("refused", new DesktopShellService { ForceExitHost = () => { asked++; return false; } }.ForceExit(new()).Status);
        Assert.AreEqual(1, asked);
    }

    private sealed class Clock : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = new(2026, 10, 19, 8, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
