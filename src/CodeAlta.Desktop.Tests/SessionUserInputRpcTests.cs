using System.Text.Json;
using CodeAlta.Desktop.Rpc;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Desktop.Tests;

[TestClass]
public sealed class SessionUserInputRpcTests
{
    private const string Epoch = "11111111-1111-4111-8111-111111111111";
    [TestMethod]
    public async Task InvalidAndDisabledRequests_DoNotReachOwner()
    {
        var service = new SessionUserInputService(_ => throw new AssertFailedException("No list."), (_, _) => throw new AssertFailedException("No resolve."), _ => throw new AssertFailedException("No cancel."), Epoch, false);
        foreach (var id in new string?[] { null, "", " x", "\ud800", new('x', 129) })
        {
            var result = await service.ListAsync(new(Epoch, id!), default);
            Assert.AreEqual("invalid_request", result.Status); Assert.IsNull(result.SessionId);
        }
        Assert.AreEqual("invalid_request", (await service.ListAsync(new(Epoch + "\n", "session"), default)).Status);
        Assert.AreEqual("disabled", (await service.ListAsync(new(Epoch, "session"), default)).Status);
    }

    [TestMethod]
    public void GeneratedInbound_StrictAttachmentAndDuplicatePairs()
    {
        var handle = new UserInputHandle(Epoch, Epoch, "1", "session", null, "interaction", Epoch);
        foreach (var generation in new[] { "0", "01", "+1", "1.0", "9007199254740992" })
            Assert.IsFalse(SessionUserInputService.TryHandle(handle with { AttachmentGeneration = generation }, out _));
        Assert.IsTrue(SessionUserInputService.TryHandle(handle with { AttachmentGeneration = "9007199254740991" }, out _));
        var request = new UserInputResolveRequest(Epoch, handle, [new("q", "a"), new("q", "b")]);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(request, DesktopJsonContext.Default.UserInputResolveRequest);
        var incoming = JsonSerializer.Deserialize(bytes, DesktopJsonContext.Default.UserInputResolveRequest);
        Assert.IsNotNull(incoming); Assert.IsFalse(SessionUserInputService.ValidAnswers(incoming.Answers));
    }

    [TestMethod]
    public async Task FullNestedWorstEscaping_GeneratedPageAndResolveBudgets()
    {
        // All eight prompts and eight options/prompt, exactly 8192 aggregate form UTF-16 units.
        // U+0200 and distinct non-ASCII suffixes exercise six-byte escaping without identity controls.
        string Escaped(int count, int suffix = 0) => new string('\u0200', count - 1) + (char)(0x300 + suffix);
        var prompts = Enumerable.Range(0, 8).Select(i => new CodeAlta.Agent.AgentUserInputPrompt(Escaped(128, i), Escaped(64), Escaped(32),
            Array.AsReadOnly(Enumerable.Range(0, 8).Select(j => new CodeAlta.Agent.AgentUserInputOption(Escaped(32, j), Escaped(i == 0 && j == 0 ? 320 : 64))).ToArray()))).ToArray();
        var form = new CodeAlta.Agent.AgentUserInputForm(Array.AsReadOnly(prompts));
        Assert.IsNotNull(OwnedUserInputValidation.Snapshot(form));
        Assert.AreEqual(8192, prompts.Sum(p => p.Id.Length + p.Question.Length + p.Header!.Length + p.Options!.Sum(o => o.Label.Length + o.Description!.Length)));
        var id = Escaped(128); var operation = Guid.Parse(Epoch);
        var entries = Enumerable.Range(0, 4).Select(i => new SessionOwnedUserInputSnapshot(
            new(operation, operation, 9007199254740991, id, id, id, new Guid(i + 1, 0, 0, new byte[8])), id, form)).ToArray();
        var service = new SessionUserInputService(_ => ValueTask.FromResult(new SessionOwnedUserInputPage(Array.AsReadOnly(entries), true)),
            (_, _) => throw new AssertFailedException("No resolution."), _ => throw new AssertFailedException("No cancellation."), Epoch, true);
        var original = service.ListAsync(new(Epoch, id), default);
        var page = await original;
        Assert.AreEqual("ok", page.Status); Assert.AreEqual(4, page.Entries.Length);
        Assert.IsTrue(page.Entries.All(e => e.Prompts.Length == 8 && e.Prompts.All(p => p.Options.Length == 8)));
        var pageBytes = JsonSerializer.SerializeToUtf8Bytes(page, DesktopJsonContext.Default.UserInputPage);
        var resolve = new UserInputResolveRequest(Epoch, page.Entries[0].Handle, prompts.Select(p => new UserInputAnswer(p.Id, Escaped(1024))).ToArray());
        Assert.IsTrue(SessionUserInputService.ValidAnswers(resolve.Answers));
        var requestBytes = JsonSerializer.SerializeToUtf8Bytes(resolve, DesktopJsonContext.Default.UserInputResolveRequest);
        Console.WriteLine($"Full nested page framed={pageBytes.Length + 4096}; resolve framed={requestBytes.Length + 4096}. Not a parser/heap/latency bound.");
        Assert.IsTrue(pageBytes.Length + 4096 <= SessionUserInputService.MaximumPageBytes);
        Assert.IsTrue(requestBytes.Length + 4096 <= SessionUserInputService.MaximumResolveBytes);
    }

    [TestMethod]
    public async Task GeneratedMalformedInboundAndSanitizedDecisions()
    {
        foreach (var json in new[] { "{\"expectedHostEpoch\":null,\"handle\":null,\"answers\":null}",
            "{\"expectedHostEpoch\":\"" + Epoch + "\",\"handle\":{},\"answers\":[null]}" })
        {
            var value = JsonSerializer.Deserialize(json, DesktopJsonContext.Default.UserInputResolveRequest);
            var service = new SessionUserInputService(_ => throw new AssertFailedException(), (_, _) => throw new AssertFailedException(), _ => throw new AssertFailedException(), Epoch, true);
            var result = await service.ResolveAsync(value!, default);
            Assert.AreEqual("invalid_request", result.Status); Assert.IsNull(result.Handle);
        }
        var handle = new UserInputHandle(Epoch, Epoch, "1", "session", null, "interaction", Epoch);
        var numeric = JsonSerializer.Serialize(new UserInputResolveRequest(Epoch, handle, [new("q", "a")]), DesktopJsonContext.Default.UserInputResolveRequest)
            .Replace("\"attachmentGeneration\":\"1\"", "\"attachmentGeneration\":1", StringComparison.Ordinal);
        StringAssert.Contains(numeric, "\"attachmentGeneration\":1");
        Assert.ThrowsExactly<JsonException>(() => JsonSerializer.Deserialize(numeric, DesktopJsonContext.Default.UserInputResolveRequest));
        foreach (var answer in new[] { "\ud800", new string('x', 2049) }) Assert.IsFalse(SessionUserInputService.ValidAnswers([new("q", answer)]));
        var calls = 0;
        var uncertain = new SessionUserInputService(_ => throw new AssertFailedException(), (_, _) => { calls++; throw new InvalidOperationException("must not echo"); },
            _ => ValueTask.FromResult(true), Epoch, true);
        Assert.AreEqual("uncertain", (await uncertain.ResolveAsync(new(Epoch, handle, [new("q", "do not echo")]), default)).Status);
        Assert.AreEqual(1, calls);
        var cancelled = await uncertain.CancelAsync(new(Epoch, handle), default);
        Assert.AreEqual("cancelled", cancelled.Status); Assert.AreEqual(handle, cancelled.Handle);
    }
}
