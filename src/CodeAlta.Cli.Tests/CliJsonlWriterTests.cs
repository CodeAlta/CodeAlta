using System.Text;
using System.Text.Json;
using CodeAlta.Acp;

namespace CodeAlta.Cli.Tests;

/// <summary>The lines of <c>--format jsonl</c>, and their order.</summary>
[TestClass]
public sealed class CliJsonlWriterTests
{
    [TestMethod]
    public async Task Result_IsLast_EvenWhenAnEventAdmittedBeforeItIsStillBeingWritten()
    {
        using var stdout = new MemoryStream();
        var output = new CliOutput(new CliStreams { Stdin = TextReader.Null, Stdout = stdout, Stderr = TextWriter.Null });
        using var admitted = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var writer = new CliJsonlWriter(output)
        {
            // The event stops between its admission and its write.
            Admitted = method =>
            {
                if (method != "session/update") return;
                admitted.Set();
                release.Wait(TimeSpan.FromSeconds(30));
            },
        };
        var update = SessionUpdate.FromJsonElement(JsonDocument.Parse("""{"sessionUpdate":"agent_message_chunk","content":{"type":"text","text":"late"}}""").RootElement.Clone());

        var updating = Task.Run(() => writer.SessionUpdateAsync("session-1", update, CancellationToken.None).AsTask());
        Assert.IsTrue(admitted.Wait(TimeSpan.FromSeconds(30)), "The event was admitted.");
        var closing = Task.Run(() => writer.WriteResult(new CliRunReport("completed", CliExitCodes.Completed) { SessionId = "session-1" }));
        // The result waits for the event admitted before it; it would have been written at once otherwise.
        await Task.WhenAny(closing, Task.Delay(TimeSpan.FromMilliseconds(300)));
        release.Set();
        await Task.WhenAll(updating, closing).WaitAsync(TimeSpan.FromSeconds(30));
        // Nothing is admitted after the result.
        await writer.SessionUpdateAsync("session-1", update, CancellationToken.None);

        var methods = Encoding.UTF8.GetString(stdout.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(static line => JsonDocument.Parse(line).RootElement.GetProperty("method").GetString()).ToArray();
        CollectionAssert.AreEqual(new[] { "session/update", CodeAltaAcpExtensions.ResultMethod }, methods);
    }
}
