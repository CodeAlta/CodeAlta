using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using CodeAlta.CodeMode;

namespace CodeAlta.CodeMode.Tests;

/// <summary><c>sleep(duration)</c>: its durations, its keys, its clock, and the wake times a resume finds again.</summary>
[TestClass]
public sealed class CodeModeSleepTests
{
    private static Task<CodeModeRunResult> RunAsync(string code, CodeModeRunOptions? options = null, ICodeModeRunObserver? observer = null)
        => new CodeModeHost(CodeModeApi.Empty, new CodeModeScriptCache()).RunAsync(CodeModeSource.Prepare(code), new DelegateHandler(), options, observer);

    private static TimeSpan Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        Assert.IsTrue(CodeModeSleep.TryParse(document.RootElement, out var duration, out var error), error);
        return duration;
    }

    private static string Refusal(string json)
    {
        using var document = JsonDocument.Parse(json);
        Assert.IsFalse(CodeModeSleep.TryParse(document.RootElement, out _, out var error));
        return error!;
    }

    [TestMethod]
    public void ADuration_IsMilliseconds_OrANumberAndAUnit_FromZeroToSevenDays()
    {
        Assert.AreEqual(TimeSpan.FromMilliseconds(1500), Parse("1500"));
        Assert.AreEqual(TimeSpan.Zero, Parse("0"));
        Assert.AreEqual(TimeSpan.FromMilliseconds(250), Parse("\"250ms\""));
        Assert.AreEqual(TimeSpan.FromSeconds(30), Parse("\"30s\""));
        Assert.AreEqual(TimeSpan.FromMinutes(5), Parse("\"5m\""));
        Assert.AreEqual(TimeSpan.FromHours(2), Parse("\" 2h \""));
        Assert.AreEqual(TimeSpan.FromDays(1.5), Parse("\"1.5d\""));
        Assert.AreEqual(TimeSpan.FromDays(7), Parse("\"7d\""));
        StringAssert.Contains(Refusal("\"8d\""), "from 0 to 7 days");
        StringAssert.Contains(Refusal("-1"), "from 0 to 7 days");
        StringAssert.Contains(Refusal("\"2x\""), "\"30s\"");
        StringAssert.Contains(Refusal("null"), "milliseconds");
        StringAssert.Contains(Refusal("\"5 minutes\""), "\"5m\"");
    }

    [TestMethod]
    public async Task AWrongDuration_IsATypeErrorTheScriptCatches()
    {
        var result = await RunAsync("""
            const errors = [];
            for (const duration of ["2x", -1, "8d", undefined, {}]) {
              try { await sleep(duration); } catch (e) { errors.push(e.name); }
            }
            await sleep(0);
            return errors;
            """);

        Assert.AreEqual(CodeModeRunStatus.Completed, result.Status, result.Error?.Message);
        Assert.AreEqual("""["TypeError","TypeError","TypeError","TypeError","TypeError"]""", result.ValueJson);
    }

    [TestMethod]
    public async Task ASleep_CountsNeitherTowardTheTimeLimit_NorAsAStall()
    {
        var limits = CodeModeLimits.Default with { Timeout = TimeSpan.FromMilliseconds(1000), StallDelay = TimeSpan.FromMilliseconds(500) };

        var result = await RunAsync("""await sleep(2500); return "awake";""", new CodeModeRunOptions { Limits = limits });

        Assert.AreEqual(CodeModeRunStatus.Completed, result.Status, result.Error?.Message);
        Assert.AreEqual("\"awake\"", result.ValueJson);
    }

    [TestMethod]
    public async Task TheSleepsOfARun_CountByOccurrenceInTheirStepPath()
    {
        var observer = new SleepObserver();

        var result = await RunAsync("""
            await sleep(5);
            await step("poll", () => sleep(5));
            await step("poll", async () => { await sleep(5); await sleep("5ms"); });
            await sleep(5);
            """, observer: observer);

        Assert.AreEqual(CodeModeRunStatus.Completed, result.Status, result.Error?.Message);
        CollectionAssert.AreEqual(new[] { "sleep#0", "poll#0/sleep#0", "poll#1/sleep#0", "poll#1/sleep#1", "sleep#1" }, observer.Started.Select(static sleep => sleep.Key).ToArray());
        Assert.AreEqual("poll", observer.Started.ElementAt(1).StepId);
        Assert.AreEqual(observer.Started.Count, observer.Ended.Count);
    }

    [TestMethod]
    public async Task OnAResume_ASleepWhoseWakeTimePassed_ReturnsAtOnce_AndOnePendingWaitsOnlyTheRest()
    {
        var passed = new CodeModeReplay([], CodeModeReplayMode.Resume) { Sleeps = new Dictionary<string, DateTimeOffset> { ["sleep#0"] = DateTimeOffset.UtcNow.AddMinutes(-1) } };
        var clock = Stopwatch.StartNew();
        var woke = await RunAsync("""await sleep("1h"); return "woke";""", new CodeModeRunOptions { Replay = passed });
        var atOnce = clock.Elapsed;

        var pending = new CodeModeReplay([], CodeModeReplayMode.Resume) { Sleeps = new Dictionary<string, DateTimeOffset> { ["sleep#0"] = DateTimeOffset.UtcNow.AddSeconds(2) } };
        var observer = new SleepObserver();
        clock.Restart();
        var rested = await RunAsync("""await sleep("1h"); return "rested";""", new CodeModeRunOptions { Replay = pending }, observer);
        var rest = clock.Elapsed;

        Assert.AreEqual("\"woke\"", woke.ValueJson);
        Assert.IsLessThan(TimeSpan.FromSeconds(10), atOnce, "a passed wake time does not wait the hour");
        Assert.AreEqual("\"rested\"", rested.ValueJson);
        Assert.IsGreaterThan(TimeSpan.FromSeconds(1), rest, "a pending wake time is waited for");
        Assert.IsLessThan(TimeSpan.FromSeconds(30), rest, "only the rest of it");
        Assert.AreEqual(pending.Sleeps["sleep#0"], observer.Started.Single().WakeAt, "the resumed run records the same wake time");
    }

    [TestMethod]
    public async Task ASleepInAReplayedStep_IsSkippedWithTheStep()
    {
        const string code = """return await step("wait", async () => { await sleep("1h"); return "slept"; });""";
        var recorded = new[] { new CodeModeReplayStep(0, "wait#0", true) { HasResult = true, ResultJson = "\"slept\"" } };
        var observer = new SleepObserver();

        var result = await RunAsync(code, new CodeModeRunOptions { Replay = new CodeModeReplay(recorded, CodeModeReplayMode.Resume) }, observer);

        Assert.AreEqual("\"slept\"", result.ValueJson);
        Assert.IsEmpty(observer.Started);
    }

    private sealed class SleepObserver : ICodeModeRunObserver
    {
        public ConcurrentQueue<CodeModeSleep> Started { get; } = new();

        public ConcurrentQueue<CodeModeSleep> Ended { get; } = new();

        public void SleepStarted(CodeModeSleep sleep) => Started.Enqueue(sleep);

        public void SleepEnded(CodeModeSleep sleep) => Ended.Enqueue(sleep);
    }
}
