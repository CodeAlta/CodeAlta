using System.Text.RegularExpressions;

namespace CodeAlta.Tests;

/// <summary>
/// Keeps every time limit of the tests in <see cref="TestTime"/> (src/TestSupport/TestTime.cs), longer on CI, and every
/// timed wait of a task in <see cref="TestTimeExtensions.WithinAsync(Task, TimeSpan, string, string, int)"/>, which says
/// where it ran out. It reads the sources of the test projects and names each line that does otherwise.
/// </summary>
[TestClass]
public sealed partial class TestTimeGuardTests
{
    // A line that gives a time which is not a limit (what a test checks, as a delay a product is given) says so.
    private const string NotALimit = "not a limit";
    // A timed wait on what is not a Task (a SemaphoreSlim, an async stream) keeps WaitAsync and says so.
    private const string NotATask = "not a Task";

    [TestMethod]
    public void EveryTimeLimitOfTheTests_IsTakenFromTestTime_AndEveryTimedWaitOfATaskSaysWhereItRanOut()
    {
        var problems = new List<string>();
        foreach (var file in TestSources())
        {
            var lines = File.ReadAllLines(file);
            for (var index = 0; index < lines.Length; index++)
            {
                var line = lines[index];
                var code = line.TrimStart();
                if (code.StartsWith("//", StringComparison.Ordinal) || code.StartsWith('*')) continue;
                var where = $"{Path.GetRelativePath(SourceRoot, file).Replace('\\', '/')}:{index + 1}";
                if (!line.Contains(NotALimit, StringComparison.Ordinal) && BareLimit().IsMatch(line))
                    problems.Add($"{where}: a time limit as a bare TimeSpan; take it from TestTime (TestTime.Seconds(...)), or end the line with `// {NotALimit}` when the time is what the test checks: {code}");
                if (!line.Contains(NotATask, StringComparison.Ordinal) && TimedWaitAsync().IsMatch(line))
                    problems.Add($"{where}: a timed WaitAsync; wait for a task with WithinAsync, which says where it ran out, or end the line with `// {NotATask}` when it waits for something else: {code}");
            }
        }

        Assert.IsEmpty(problems, string.Join(Environment.NewLine, problems));
    }

    [TestMethod]
    public void TheFrontendTests_TakeTheirLimitsWithTheSameFactorOnCi()
    {
        var frontend = File.ReadAllText(Path.Combine(SourceRoot, "CodeAlta", "frontend", "src", "testTime.ts"));
        var factor = FrontendCiScale().Match(frontend);
        Assert.IsTrue(factor.Success, "testTime.ts declares testTimeCiScale");
        Assert.AreEqual(TestTime.CiScale, double.Parse(factor.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture));
    }

    [TestMethod]
    public void TheGuard_FindsTheBareLimits_AndLeavesTheTimesThatAreData()
    {
        foreach (var bare in new[]
        {
            "await task.WaitAsync(TimeSpan.FromSeconds(5));",
            "await task.WithinAsync(TimeSpan.FromSeconds(5));",
            "using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));",
            "cts.CancelAfter(TimeSpan.FromMilliseconds(500));",
            "Assert.IsTrue(gate.Wait(TimeSpan.FromSeconds(1)));",
            "Assert.IsTrue(gate.Wait(500));",
            "Assert.IsTrue(process.WaitForExit(10_000));",
            "SpinWait.SpinUntil(() => done, TimeSpan.FromSeconds(5));",
            "var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);",
            "while (watch.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(50);",
            "private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);",
            "var patience = TimeSpan.FromSeconds(60);",
            "await WaitUntilAsync(() => done, \"done\", TimeSpan.FromSeconds(3));",
            "private readonly CancellationTokenSource _deadline = new(TimeSpan.FromSeconds(30));",
            "if (watch.Elapsed >= TimeSpan.FromSeconds(5)) throw new TimeoutException();",
        })
            Assert.IsTrue(BareLimit().IsMatch(bare), bare);

        foreach (var timed in new[] { "await task.WaitAsync(TestTime.Seconds(5));", "await task.WaitAsync(Patience);", "return completion.Task.WaitAsync(timeout);" })
            Assert.IsTrue(TimedWaitAsync().IsMatch(timed), timed);

        foreach (var data in new[]
        {
            "clock.Advance(TimeSpan.FromSeconds(60));",
            "await Task.Delay(TimeSpan.FromMilliseconds(50));",
            "options with { PollInterval = TimeSpan.FromMilliseconds(10) }",
            "Assert.IsTrue(clock.Elapsed >= TimeSpan.FromMilliseconds(280));",
            "await task.WithinAsync(TestTime.Seconds(5));",
            "await task.WaitAsync(cancellationToken);",
            "await task.WaitAsync(timeout.Token);",
        })
        {
            Assert.IsFalse(BareLimit().IsMatch(data), data);
            Assert.IsFalse(TimedWaitAsync().IsMatch(data), data);
        }
    }

    private static string SourceRoot { get; } = FindSourceRoot();

    private static IEnumerable<string> TestSources()
        => Directory.EnumerateDirectories(SourceRoot, "CodeAlta*.Tests")
            .SelectMany(static project => Directory.EnumerateFiles(project, "*.cs", SearchOption.AllDirectories))
            .Where(static file => !file.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(static part => part is "bin" or "obj"))
            .Where(static file => !file.EndsWith(nameof(TestTimeGuardTests) + ".cs", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal);

    private static string FindSourceRoot()
    {
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
            if (File.Exists(Path.Combine(folder.FullName, "CodeAlta.slnx"))) return folder.FullName;
        throw new InvalidOperationException("The sources of the tests (the folder of CodeAlta.slnx) are not above " + AppContext.BaseDirectory);
    }

    // The shapes of a time limit: a wait, a token canceled after a while, a deadline, an upper bound on what elapsed,
    // the wait helpers of the tests, and a variable named as a limit.
    [GeneratedRegex("""
        (?:WaitAsync|WithinAsync|new\sCancellationTokenSource|CancelAfter|\.Wait|WaitForExit)\(\s*TimeSpan\.From(?:Seconds|Milliseconds|Minutes)\(
        |(?:\.Wait|WaitForExit)\(\s*\d
        |CancellationTokenSource\??\s+\w+\s*=\s*new\(\s*TimeSpan\.From(?:Seconds|Milliseconds|Minutes)\(
        |Elapsed\s*>=?\s*TimeSpan\.From(?:Seconds|Milliseconds|Minutes)\([^)]*\)\)\s*throw
        |SpinUntil\([^;]*,\s*TimeSpan\.From(?:Seconds|Milliseconds|Minutes)\(
        |UtcNow\s*\+\s*\(?(?:\w+\s*\?\?\s*)?TimeSpan\.From(?:Seconds|Milliseconds|Minutes)\(
        |Elapsed\s*<=?\s*TimeSpan\.From(?:Seconds|Milliseconds|Minutes)\(
        |(?:WaitForAsync|WaitUntilAsync|PumpTerminalUntil\w*|WaitForCondition)\([^;]*,\s*TimeSpan\.From(?:Seconds|Milliseconds|Minutes)\(
        |(?i:(?:TimeSpan\??|var)\s+_?\w*(?:timeout|patience|deadline|hang|wait|limit|grace|within)\w*\s*=\s*TimeSpan\.From(?:Seconds|Milliseconds|Minutes)\()
        """, RegexOptions.IgnorePatternWhitespace)]
    private static partial Regex BareLimit();

    [GeneratedRegex(@"export const testTimeCiScale = ([0-9.]+);")]
    private static partial Regex FrontendCiScale();

    // WaitAsync given a time (TestTime, a TimeSpan, or a variable named as a limit), not a token.
    [GeneratedRegex("""
        \.WaitAsync\(\s*(?:TestTime\.|TimeSpan\.|(?i:\w*(?:timeout|patience|deadline|hang|wait|limit|grace|within)|remaining|left)\b(?!\.Token))
        """, RegexOptions.IgnorePatternWhitespace)]
    private static partial Regex TimedWaitAsync();
}
