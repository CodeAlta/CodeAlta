using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Desktop.Rpc;

namespace CodeAlta.Desktop.Tests;

[TestClass]
public sealed class DesktopMessageTextTests
{
    private const string Epoch = "0199a000-0000-7000-8000-000000000001";
    private const string Session = "session";

    [TestMethod]
    public async Task AMessageTheHistoryShortens_IsReadWhole_InParts()
    {
        var text = string.Concat(Enumerable.Range(0, 30_000).Select(static index => $"line {index}\n"));
        var message = new AgentContentCompletedEvent(new("provider"), Session, DateTimeOffset.UnixEpoch, new("run"), AgentContentKind.Assistant, "content", null, text);
        var service = new ToolCallsService((_, offset, _) => Task.FromResult<AgentEvent?>(offset == 10 ? message : null), null, Epoch);

        // The row of the message has its start only, and says so.
        var row = WorkspaceService.ProjectHistory(new([new AgentSessionHistoryEntry(10, message)], null, false)).Entries.Single();
        Assert.IsTrue(row.TextTruncated);
        Assert.IsLessThan(text.Length, row.Text!.Length);

        var read = new System.Text.StringBuilder();
        var parts = 0;
        MessageTextResponse part;
        do
        {
            part = await service.ReadMessageAsync(new(Epoch, Session, "10", read.Length), CancellationToken.None);
            Assert.AreEqual("ok", part.Status);
            Assert.AreEqual(text.Length, part.Text!.Length);
            read.Append(part.Text.Text);
            parts++;
        }
        while (part.Text.More);

        Assert.AreEqual(text, read.ToString());
        Assert.AreEqual(2, parts, "the text is longer than one part");
    }

    [TestMethod]
    public async Task OnlyAMessage_OfTheCurrentHost_FromAPositionInIt_IsRead()
    {
        var message = new AgentContentCompletedEvent(new("provider"), Session, DateTimeOffset.UnixEpoch, new("run"), AgentContentKind.User, "content", null, "short");
        var call = new AgentActivityEvent(new("provider"), Session, DateTimeOffset.UnixEpoch, new("run"), AgentActivityKind.ToolCall, AgentActivityPhase.Completed,
            "activity", null, "shell_command", null, JsonSerializer.SerializeToElement(new { command = "dir" }));
        var service = new ToolCallsService((_, offset, _) => Task.FromResult<AgentEvent?>(offset switch { 1 => message, 2 => call, _ => null }), null, Epoch);

        Assert.AreEqual("short", (await service.ReadMessageAsync(new(Epoch, Session, "1", 0), CancellationToken.None)).Text!.Text);
        Assert.AreEqual("missing_record", (await service.ReadMessageAsync(new(Epoch, Session, "2", 0), CancellationToken.None)).Status, "a tool call is read by its window");
        Assert.AreEqual("missing_record", (await service.ReadMessageAsync(new(Epoch, Session, "3", 0), CancellationToken.None)).Status);
        Assert.AreEqual("invalid", (await service.ReadMessageAsync(new(Epoch, Session, "1", 6), CancellationToken.None)).Status);
        Assert.AreEqual("invalid", (await service.ReadMessageAsync(new(Epoch, Session, "-1", 0), CancellationToken.None)).Status);
        Assert.AreEqual("stale_epoch", (await service.ReadMessageAsync(new("0199a000-0000-7000-8000-000000000002", Session, "1", 0), CancellationToken.None)).Status);
        Assert.AreEqual("unavailable", (await new ToolCallsService().ReadMessageAsync(new(Epoch, Session, "1", 0), CancellationToken.None)).Status);
    }
}
