using CodeAlta.Agent;
using CodeAlta.Desktop.Rpc;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Desktop.Tests;

// The questions of runs whose conversation another process holds, as the page lists and answers them: an answer must
// name the host epoch and the exact question. No host, provider or native surface is constructed.
[TestClass]
public sealed class ConversationHeldRpcTests
{
    private const string Epoch = "abcdefab-1234-5678-9abc-abcdefabcdef";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [TestMethod]
    public async Task List_ShowsTheQuestion_WithItsHolderAndChoices()
    {
        await using var owner = new Owner();
        _ = owner.Ask();
        var service = new ConversationHeldService(owner.Service, Epoch);

        var page = await owner.WaitForPageAsync(service);

        Assert.AreEqual("ok", page.Status);
        Assert.AreEqual(Epoch, page.HostEpoch);
        var entry = page.Entries.Single();
        Assert.AreEqual("claude-code", entry.ProviderId);
        Assert.AreEqual("conversation-1", entry.ConversationId);
        Assert.AreEqual("session", entry.Handle.SessionId);
        Assert.AreEqual("run-1", entry.Handle.RunId);
        Assert.AreEqual("request-1", entry.Handle.RequestId);
        Assert.AreEqual(owner.Attachment.Ordinal.ToString(), entry.Handle.AttachmentGeneration);
        Assert.AreEqual(new ConversationHeldHolder("vsCode", "VS Code", 4100, "claude-vscode", "interactive", "idle", @"C:\work", "my-session"), entry.Holder);
        CollectionAssert.AreEqual(new[] { "wait", "continueAsCopy", "openAnyway", "cancel" }, entry.Choices);
    }

    [TestMethod]
    public async Task Resolve_WithAWrongEpochSessionRequestOrAttempt_IsRefused_AndTheRunKeepsWaiting()
    {
        await using var owner = new Owner();
        var ask = owner.Ask();
        var service = new ConversationHeldService(owner.Service, Epoch);
        var handle = (await owner.WaitForPageAsync(service)).Entries.Single().Handle;

        Assert.AreEqual("stale_epoch", (await service.ResolveAsync(new("bbbbbbbb-1234-5678-9abc-abcdefabcdef", handle, "openAnyway"), default)).Status);
        foreach (var other in new[]
                 {
                     handle with { SessionId = "other-session" },
                     handle with { RequestId = "request-2" },
                     handle with { RunId = "run-2" },
                     handle with { RunId = null },
                     handle with { AttemptId = Guid.NewGuid().ToString("D") },
                     handle with { RuntimeInstanceId = Guid.NewGuid().ToString("D") },
                     handle with { AttachmentGeneration = (owner.Attachment.Ordinal + 1).ToString() },
                 })
        {
            Assert.AreEqual("rejected", (await service.ResolveAsync(new(Epoch, other, "openAnyway"), default)).Status);
        }

        Assert.IsFalse(ask.IsCompleted);
        Assert.AreEqual(1, (await service.ListAsync(new(Epoch, "session"), default)).Entries.Length);

        var resolved = await service.ResolveAsync(new(Epoch, handle, "openAnyway"), default);
        Assert.AreEqual("resolved", resolved.Status);
        Assert.AreEqual(handle, resolved.Handle);
        Assert.AreEqual(AgentConversationHeldChoice.OpenAnyway, await ask.WaitAsync(Timeout));
        Assert.AreEqual("rejected", (await service.ResolveAsync(new(Epoch, handle, "wait"), default)).Status, "A question is answered once.");
    }

    [TestMethod]
    public async Task Resolve_WithAChoiceNotOffered_IsRefused()
    {
        await using var owner = new Owner();
        var ask = owner.Ask([AgentConversationHeldChoice.Wait, AgentConversationHeldChoice.ContinueAsCopy, AgentConversationHeldChoice.Cancel]);
        var service = new ConversationHeldService(owner.Service, Epoch);
        var entry = (await owner.WaitForPageAsync(service)).Entries.Single();
        CollectionAssert.AreEqual(new[] { "wait", "continueAsCopy", "cancel" }, entry.Choices);

        Assert.AreEqual("rejected", (await service.ResolveAsync(new(Epoch, entry.Handle, "openAnyway"), default)).Status);
        Assert.AreEqual("invalid_request", (await service.ResolveAsync(new(Epoch, entry.Handle, "OpenAnyway"), default)).Status);
        Assert.AreEqual("resolved", (await service.ResolveAsync(new(Epoch, entry.Handle, "continueAsCopy"), default)).Status);
        Assert.AreEqual(AgentConversationHeldChoice.ContinueAsCopy, await ask.WaitAsync(Timeout));
    }

    [TestMethod]
    public async Task MalformedRequests_AndAStaleList_DoNotReachTheOwner()
    {
        var service = new ConversationHeldService((_, _) => throw new AssertFailedException("List forbidden."),
            (_, _, _) => throw new AssertFailedException("Resolve forbidden."), Epoch);
        var handle = new ConversationHeldHandle(Guid.NewGuid().ToString("D"), "1", "session", "run-1", "request-1", Guid.NewGuid().ToString("D"));

        Assert.AreEqual("invalid_request", (await service.ListAsync(null!, default)).Status);
        Assert.AreEqual("invalid_request", (await service.ListAsync(new(Epoch, " session"), default)).Status);
        Assert.AreEqual("stale_epoch", (await service.ListAsync(new(Guid.NewGuid().ToString("D"), "session"), default)).Status);
        Assert.AreEqual("invalid_request", (await service.ResolveAsync(new(Epoch, null!, "wait"), default)).Status);
        Assert.AreEqual("invalid_request", (await service.ResolveAsync(new(Epoch, handle, "later"), default)).Status);
        foreach (var bad in new[]
                 {
                     handle with { RuntimeInstanceId = Guid.Empty.ToString("D") }, handle with { AttemptId = "not-a-guid" },
                     handle with { AttachmentGeneration = "01" }, handle with { AttachmentGeneration = "0" }, handle with { SessionId = "" },
                     handle with { RequestId = "request\n1" },
                 })
        {
            Assert.AreEqual("invalid_request", (await service.ResolveAsync(new(Epoch, bad, "wait"), default)).Status);
        }

        Assert.AreEqual("stale_epoch", (await service.ResolveAsync(new(Guid.NewGuid().ToString("D"), handle, "wait"), default)).Status);
    }

    // The owner of the questions with one live attachment of the session "session".
    private sealed class Owner : IAsyncDisposable
    {
        private readonly OwnedProviderEventForwarding _forwarding = new();
        private readonly Guid _runtime = Guid.NewGuid();

        public Owner()
        {
            Attachment = _forwarding.RegisterAttachment("session", "handle", static () => Task.CompletedTask, static () => Task.CompletedTask);
            Attachment.CompleteSetup();
            Service.ResolveTarget = sessionId => sessionId == "session" ? new(_runtime, Attachment, new ModelProviderId("claude-code")) : null;
        }

        public SessionConversationHeldService Service { get; } = new();

        public OwnedProviderEventForwarding.Attachment Attachment { get; }

        public Task<AgentConversationHeldChoice> Ask(IReadOnlyList<AgentConversationHeldChoice>? choices = null)
            => Service.Handler(new AgentConversationHeldRequest(new ModelProviderId("claude-code"), "session", new AgentRunId("run-1"), "request-1",
                DateTimeOffset.UnixEpoch, "conversation-1",
                new AgentConversationHolder(AgentConversationHolderOrigin.VsCode, "VS Code", 4100, "claude-vscode", "interactive", "idle", @"C:\work", "my-session"),
                choices ?? [AgentConversationHeldChoice.Wait, AgentConversationHeldChoice.ContinueAsCopy, AgentConversationHeldChoice.OpenAnyway, AgentConversationHeldChoice.Cancel]),
                CancellationToken.None);

        public async Task<ConversationHeldPage> WaitForPageAsync(ConversationHeldService service)
        {
            using var timeout = new CancellationTokenSource(Timeout);
            while (true)
            {
                var page = await service.ListAsync(new(Epoch, "session"), default);
                if (page.Entries.Length > 0) return page;
                await Task.Delay(10, timeout.Token);
            }
        }

        public ValueTask DisposeAsync() => Service.DisposeAsync();
    }
}
