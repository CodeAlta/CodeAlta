using CodeAlta.Agent;
using CodeAlta.Desktop.Rpc;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Desktop.Tests;

/// <summary>
/// The RPC of the window that opens a Claude Code session in VS Code or a terminal: the page names a session only, the
/// address that is opened is the one the host built, and the conversation is released before it is opened.
/// </summary>
[TestClass]
public sealed class SessionClaudeCodeConversationRpcTests
{
    private const string Conversation = "5b0c8b0e-3f7c-4c1e-9a55-6d1f0b2c3e01";

    [TestMethod]
    public async Task Requests_AreCheckedLikeEveryRequestOfThePage_BeforeTheHostIsAsked()
    {
        var host = new Host();
        var service = host.Service;
        var unconfigured = new SessionOperationsService();
        Assert.AreEqual("unconfigured", (await unconfigured.ClaudeCodeConversationAsync(new("epoch", "session"), CancellationToken.None)).Status);
        Assert.AreEqual("unconfigured", (await unconfigured.ReleaseClaudeCodeConversationAsync(new("epoch", "session"), CancellationToken.None)).Status);
        Assert.AreEqual("unconfigured", (await unconfigured.OpenInVsCodeAsync(new("epoch", "session"), CancellationToken.None)).Status);
        Assert.AreEqual("stale_epoch", (await service.ClaudeCodeConversationAsync(new("another", "session"), CancellationToken.None)).Status);
        Assert.AreEqual("stale_epoch", (await service.ReleaseClaudeCodeConversationAsync(new("another", "session"), CancellationToken.None)).Status);
        Assert.AreEqual("stale_epoch", (await service.OpenInVsCodeAsync(new("another", "session"), CancellationToken.None)).Status);
        foreach (var refused in new SessionClaudeCodeRequest[] { new("epoch", " "), new("epoch", " session"), new("epoch", new string('x', 257)) })
        {
            Assert.AreEqual("invalid_request", (await service.ClaudeCodeConversationAsync(refused, CancellationToken.None)).Status);
            Assert.AreEqual("invalid_request", (await service.ReleaseClaudeCodeConversationAsync(refused, CancellationToken.None)).Status);
            Assert.AreEqual("invalid_request", (await service.OpenInVsCodeAsync(refused, CancellationToken.None)).Status);
        }

        Assert.AreEqual(0, host.Asked.Count, "Nothing reached the host.");
        Assert.AreEqual(0, host.Opened.Count);
    }

    [TestMethod]
    public async Task Conversation_IsTheOneTheHostNames_WithItsHolder()
    {
        var host = new Host { Holder = new AgentConversationHolder(AgentConversationHolderOrigin.VsCode, "VS Code", 4242, "claude-vscode", "interactive", "idle") };
        var result = await host.Service.ClaudeCodeConversationAsync(new("epoch", "session"), CancellationToken.None);

        Assert.AreEqual(("ok", "epoch", "session", Conversation, $"claude --resume {Conversation}", "C:/work"),
            (result.Status, result.Epoch, result.SessionId, result.CliSessionId, result.ResumeCommand, result.WorkingDirectory));
        Assert.AreEqual(("vsCode", "VS Code", 4242), (result.Held!.Origin, result.Held.OriginText, result.Held.ProcessId));
        CollectionAssert.AreEqual(new[] { "describe:session" }, host.Asked.ToArray(), "Reading it releases nothing.");

        foreach (var status in new[] { "unavailable", "unsupported", "none", "closed" })
        {
            host.DescribeStatus = status;
            var refused = await host.Service.ClaudeCodeConversationAsync(new("epoch", "session"), CancellationToken.None);
            Assert.AreEqual((status, null, null), (refused.Status, refused.CliSessionId, refused.ResumeCommand));
        }
    }

    [TestMethod]
    public async Task OpenInVsCode_ReleasesTheConversation_ThenOpensTheAddressTheHostBuilt()
    {
        var host = new Host();
        var result = await host.Service.OpenInVsCodeAsync(new("epoch", "session"), CancellationToken.None);

        Assert.AreEqual(("opened", "session"), (result.Status, result.SessionId));
        CollectionAssert.AreEqual(new[] { "describe:session", "release:session" }, host.Asked.ToArray());
        CollectionAssert.AreEqual(new[] { $"vscode://anthropic.claude-code/open?session={Conversation}" }, host.Opened.ToArray());

        host.OpenSucceeds = false;
        Assert.AreEqual("open_failed", (await host.Service.OpenInVsCodeAsync(new("epoch", "session"), CancellationToken.None)).Status);
    }

    [TestMethod]
    public async Task OpenInVsCode_OpensNothing_WhenTheConversationIsNotReleased_OrIsNotOneOfClaudeCode()
    {
        foreach (var refusal in new[] { "running", "background_tasks", "remote_control", "interaction_pending", "busy", "release_unconfirmed" })
        {
            var refused = new Host { ReleaseStatus = refusal };
            Assert.AreEqual(refusal, (await refused.Service.OpenInVsCodeAsync(new("epoch", "session"), CancellationToken.None)).Status);
            Assert.AreEqual(0, refused.Opened.Count, refusal);
        }

        // A session of another provider, or none yet: nothing is released, nothing is opened.
        foreach (var status in new[] { "unsupported", "none", "unavailable" })
        {
            var other = new Host { DescribeStatus = status };
            Assert.AreEqual(status, (await other.Service.OpenInVsCodeAsync(new("epoch", "session"), CancellationToken.None)).Status);
            CollectionAssert.AreEqual(new[] { "describe:session" }, other.Asked.ToArray());
            Assert.AreEqual(0, other.Opened.Count);
        }

        // Only an address of the Claude Code extension, for the identifier the conversation has, is ever opened.
        foreach (var (id, address) in new[]
                 {
                     (Conversation, "https://example.com/"),
                     (Conversation, "vscode://anthropic.claude-code/open?session=" + Guid.NewGuid().ToString("D")),
                     (Conversation, $"vscode://other.extension/open?session={Conversation}"),
                     ("x & calc.exe", "vscode://anthropic.claude-code/open?session=x & calc.exe"),
                 })
        {
            var forged = new Host { ConversationId = id, EditorUri = address };
            Assert.AreEqual("unsupported", (await forged.Service.OpenInVsCodeAsync(new("epoch", "session"), CancellationToken.None)).Status, address);
            Assert.AreEqual("unsupported", (await forged.Service.ClaudeCodeConversationAsync(new("epoch", "session"), CancellationToken.None)).Status, address);
            Assert.AreEqual(0, forged.Opened.Count, address);
            Assert.IsFalse(forged.Asked.Contains("release:session"), "Nothing is released for it.");
        }
    }

    [TestMethod]
    public async Task Release_GivesTheConversationOnceReleased_OrWhyItIsNot()
    {
        var host = new Host();
        var released = await host.Service.ReleaseClaudeCodeConversationAsync(new("epoch", "session"), CancellationToken.None);
        Assert.AreEqual(("ok", $"claude --resume {Conversation}", "C:/work"), (released.Status, released.ResumeCommand, released.WorkingDirectory));
        CollectionAssert.AreEqual(new[] { "release:session" }, host.Asked.ToArray());
        Assert.AreEqual(0, host.Opened.Count, "Releasing opens nothing.");

        host.ReleaseStatus = "running";
        var refused = await host.Service.ReleaseClaudeCodeConversationAsync(new("epoch", "session"), CancellationToken.None);
        Assert.AreEqual(("running", null), (refused.Status, refused.ResumeCommand));
    }

    // What the host says of the conversation of the session, and what the window would open.
    private sealed class Host
    {
        public Host()
        {
            Service = new SessionOperationsService("epoch", new SessionOperationsService.SharedConversationSeam(
                sessionId => { Asked.Add($"describe:{sessionId}"); return Task.FromResult(Result(DescribeStatus)); },
                sessionId => { Asked.Add($"release:{sessionId}"); return Task.FromResult(Result(ReleaseStatus)); },
                address => { Opened.Add(address); return OpenSucceeds; }));
        }

        public SessionOperationsService Service { get; }

        public List<string> Asked { get; } = [];

        public List<string> Opened { get; } = [];

        public string DescribeStatus { get; set; } = "ok";

        public string ReleaseStatus { get; set; } = "ok";

        public bool OpenSucceeds { get; set; } = true;

        public string ConversationId { get; init; } = Conversation;

        public string? EditorUri { get; init; }

        public AgentConversationHolder? Holder { get; init; }

        private OwnedSharedConversationResult Result(string status)
            => status == "ok"
                ? new(status, new OwnedSharedConversation(ConversationId, $"claude --resume {ConversationId}", "C:/work",
                    EditorUri ?? $"vscode://anthropic.claude-code/open?session={ConversationId}", Holder))
                : new(status, null);
    }
}
