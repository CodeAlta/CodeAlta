using CodeAlta.Desktop.Rpc;

namespace CodeAlta.Desktop.Tests;

[TestClass]
public sealed class DesktopUiWatchdogTests
{
    [TestMethod]
    public void AThreadThatRuns_IsNotReported()
    {
        var clock = new Clock();
        var log = new List<string>();
        using var watchdog = new DesktopUiWatchdog([("thread of the window", static callback => callback())], clock, log.Add, log.Add, start: false);
        for (var index = 0; index < 10; index++)
        {
            watchdog.Check();
            clock.Now += TimeSpan.FromSeconds(1);
        }

        Assert.IsEmpty(log);
    }

    [TestMethod]
    public void AStall_IsLoggedOnceWhenItStarts_AndWithItsLengthWhenTheThreadRunsAgain()
    {
        var clock = new Clock();
        var warnings = new List<string>();
        var infos = new List<string>();
        Action? waiting = null;
        using var watchdog = new DesktopUiWatchdog([("thread of the window", callback => waiting = callback)], clock, warnings.Add, infos.Add, start: false);
        watchdog.Check();
        clock.Now += TimeSpan.FromSeconds(1.5);
        watchdog.Check();
        Assert.IsEmpty(warnings, "A heartbeat late by less than the threshold is no stall.");
        clock.Now += TimeSpan.FromSeconds(1);
        watchdog.Check();
        for (var step = 0; step < 10; step++)
        {
            clock.Now += TimeSpan.FromSeconds(0.5);
            watchdog.Check();
        }

        CollectionAssert.AreEqual(new[] { "The thread of the window has not run for 2.5 s: the calls of the page wait for it." }, warnings);
        waiting!();
        CollectionAssert.AreEqual(new[] { "The thread of the window runs again after 7.5 s." }, infos);
        // The next heartbeat is posted at the next check.
        waiting = null;
        watchdog.Check();
        Assert.IsNotNull(waiting);
    }

    [TestMethod]
    public void AHeartbeatThatRunsLateBeforeAnyCheckSawIt_IsAStallAllTheSame()
    {
        // A process held as a whole: when it runs again, the heartbeat may run before the watchdog looks.
        var clock = new Clock();
        var warnings = new List<string>();
        Action? waiting = null;
        using var watchdog = new DesktopUiWatchdog([("thread of the window", callback => waiting = callback)], clock, warnings.Add, _ => { }, start: false);
        watchdog.Check();
        clock.Now += TimeSpan.FromSeconds(45);
        waiting!();
        CollectionAssert.AreEqual(new[] { "The thread of the window did not run for 45.0 s: the calls of the page waited for it." }, warnings);
    }

    [TestMethod]
    public void ACheckThatComesLongAfterTheLastOne_SaysTheWholeProcessWasHeld()
    {
        var clock = new Clock();
        var warnings = new List<string>();
        using var watchdog = new DesktopUiWatchdog([("thread of the window", static callback => callback())], clock, warnings.Add, _ => { }, start: false);
        watchdog.Check();
        clock.Now += TimeSpan.FromSeconds(1);
        watchdog.Check();
        Assert.IsEmpty(warnings);
        clock.Now += TimeSpan.FromSeconds(45);
        watchdog.Check();
        CollectionAssert.AreEqual(new[] { "CodeAlta did not run for 45.0 s: the whole process was held, the calls of the page with it." }, warnings);
    }

    [TestMethod]
    public void EachProbeIsWatchedOnItsOwn_AndAWindowThatTakesNoCallbackStopsItsProbe()
    {
        var clock = new Clock();
        var warnings = new List<string>();
        var infos = new List<string>();
        var posts = 0;
        using var watchdog = new DesktopUiWatchdog(
            [("thread of the window", _ => { posts++; throw new ObjectDisposedException("window"); }), ("thread pool", _ => { })],
            clock, warnings.Add, infos.Add, start: false);
        watchdog.Check();
        for (var step = 0; step < 6; step++)
        {
            clock.Now += TimeSpan.FromSeconds(0.5);
            watchdog.Check();
        }

        Assert.AreEqual(1, posts);
        CollectionAssert.AreEqual(new[] { "The thread pool has not run for 2.0 s: the calls of the page wait for it." }, warnings);
        CollectionAssert.AreEqual(new[] { "The thread of the window takes no heartbeat any more (ObjectDisposedException)." }, infos);
    }

    [TestMethod]
    public void TheRealWatchdog_RunsOnItsOwnThread()
    {
        var beats = 0;
        using var ran = new ManualResetEventSlim();
        using (new DesktopUiWatchdog(callback => { Interlocked.Increment(ref beats); callback(); ran.Set(); }))
            Assert.IsTrue(ran.Wait(TimeSpan.FromSeconds(10)));
        Assert.IsGreaterThan(0, beats);
    }

    [TestMethod]
    public void ThePool_AddsAtLeast64ThreadsWithoutWaiting()
    {
        Assert.AreEqual(64, DesktopThreadPool.MinimumWorkers(4));
        Assert.AreEqual(128, DesktopThreadPool.MinimumWorkers(16));
    }

    [TestMethod]
    public void TheReportsOfThePage_AreBounded_OneLine_AndLimitedPerMinute()
    {
        var clock = new Clock();
        var boot = new BootService("00000000-0000-0000-0000-000000000001") { Clock = clock };
        Assert.AreEqual("invalid_request", boot.Report(new("error", "x")).Status);
        Assert.AreEqual("invalid_request", boot.Report(new("warning", "")).Status);
        Assert.AreEqual("ok", boot.Report(new("warning", "boot.status has not answered for 12 s\r\nforged line")).Status);
        for (var index = 1; index < BootService.ReportsPerMinute; index++) Assert.AreEqual("ok", boot.Report(new("information", "x")).Status);
        Assert.AreEqual("limited", boot.Report(new("information", "x")).Status);
        clock.Now += TimeSpan.FromMinutes(1);
        Assert.AreEqual("ok", boot.Report(new("information", new string('x', 5000))).Status);
    }

    private sealed class Clock : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = new(2026, 10, 19, 13, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
