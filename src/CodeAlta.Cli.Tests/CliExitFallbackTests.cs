using System.Text;
using System.Text.Json;
using CodeAlta.Acp;

namespace CodeAlta.Cli.Tests;

/// <summary>The process-level fallback of a run asked to end that does not.</summary>
[TestClass]
public sealed class CliExitFallbackTests
{
    [TestMethod]
    public async Task Fallback_WritesTheResultAndExitsWithItsCode_OnceTheBoundHasPassed()
    {
        using var stdout = new MemoryStream();
        var output = new CliOutput(new CliStreams { Stdin = TextReader.Null, Stdout = stdout, Stderr = TextWriter.Null });
        var jsonl = new CliJsonlWriter(output);
        var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var fallback = new CliExitFallback(output, jsonl, code => exited.TrySetResult(code));

        fallback.Arm(TimeSpan.FromMilliseconds(50), new CliRunReport("aborted", CliExitCodes.Aborted) { SessionId = "session-1", AbortReason = "signal" });

        Assert.AreEqual(CliExitCodes.Aborted, await exited.Task.WaitAsync(TimeSpan.FromSeconds(30)));
        var line = JsonDocument.Parse(Encoding.UTF8.GetString(stdout.ToArray()).TrimEnd('\n')).RootElement;
        Assert.AreEqual(CodeAltaAcpExtensions.ResultMethod, line.GetProperty("method").GetString());
        Assert.AreEqual("signal", line.GetProperty("params").GetProperty("abortReason").GetString());
    }

    [TestMethod]
    public async Task Fallback_AfterTheResult_ExitsWithItsCode_AndWritesNothingMore()
    {
        using var stdout = new MemoryStream();
        var output = new CliOutput(new CliStreams { Stdin = TextReader.Null, Stdout = stdout, Stderr = TextWriter.Null });
        var jsonl = new CliJsonlWriter(output);
        var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var fallback = new CliExitFallback(output, jsonl, code => exited.TrySetResult(code));
        var completed = new CliRunReport("completed", CliExitCodes.Completed) { SessionId = "session-1" };
        jsonl.WriteResult(completed);
        fallback.Settle(completed);

        // The shutdown of the host outlived the bound of the timeout armed at the start.
        fallback.Arm(TimeSpan.FromMilliseconds(50), new CliRunReport("timeout", CliExitCodes.Timeout));

        Assert.AreEqual(CliExitCodes.Completed, await exited.Task.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.AreEqual(1, Encoding.UTF8.GetString(stdout.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
    }

    [TestMethod]
    public async Task Fallback_Disarmed_DoesNotExit()
    {
        var output = new CliOutput(new CliStreams { Stdin = TextReader.Null, Stdout = Stream.Null, Stderr = TextWriter.Null });
        var exited = 0;
        var fallback = new CliExitFallback(output, null, _ => Interlocked.Increment(ref exited));
        fallback.Arm(TimeSpan.FromMilliseconds(100), new CliRunReport("aborted", CliExitCodes.Aborted));

        fallback.Dispose();
        await Task.Delay(300);

        Assert.AreEqual(0, Volatile.Read(ref exited));
    }
}
