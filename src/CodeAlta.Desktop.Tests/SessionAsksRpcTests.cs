using System.Text.Json;
using System.Text.Json.Nodes;
using CodeAlta.Desktop.Rpc;
using CodeAlta.LiveTool;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Desktop.Tests;

/// <summary>Inert owned-ask transport and generated serialization budget regressions.</summary>
[TestClass]
public sealed class SessionAsksRpcTests
{
    [TestMethod]
    public async Task EpochAndClosedAdmission_RejectWithoutInvokingOwner()
    {
        var owner = new OwnedSessionAskService(true, static (_, _) => throw new AssertFailedException("Unexpected dispatch."));
        var epoch = Guid.NewGuid().ToString("D");
        var rpc = new SessionAsksService(owner, epoch);
        Assert.AreEqual("stale_epoch", (await rpc.ListAsync(new(Guid.NewGuid().ToString("D"), "session"), default)).Status);
        rpc.CloseAdmission();
        Assert.AreEqual("closed", (await rpc.ListAsync(new(epoch, "session"), default)).Status);
        owner.CloseAdmission();
        await owner.DrainAsync();
    }

    [TestMethod]
    public async Task InvalidSession_IsNotReflectedIntoBoundedErrorPage()
    {
        var owner = new OwnedSessionAskService(false, static (_, _) => throw new AssertFailedException("No dispatch."));
        try
        {
            var epoch = Guid.NewGuid().ToString("D");
            var page = await new SessionAsksService(owner, epoch).ListAsync(new(epoch, new string('x', 65536)), default);
            Assert.AreEqual("invalid_request", page.Status);
            Assert.IsNull(page.SessionId);
        }
        finally { owner.CloseAdmission(); await owner.DrainAsync(); }
    }

    [TestMethod]
    public void GeneratedPageAndAction_WithEscapingFitFramedBudget()
    {
        var epoch = Guid.NewGuid().ToString("D");
        var handle = new OwnedAskHandle(Guid.NewGuid(), Guid.NewGuid(), 9007199254740991, new string('\u4e00', 256),
            new string('\u4e00', 256), new string('\u4e00', 256), Guid.NewGuid().ToString("D"), 256);
        var request = new AltaAskRequest
        {
            Questions = [new() { Title = new string('\u0001', 120), Question = new string('\u0001', 4000),
                Description = new string('\u0001', 4000), Freeform = new() { Title = new string('\u0001', 72) } }],
        };
        var disposition = new OwnedAskDisposition(Guid.NewGuid(), handle, "indeterminate", new string('\u4e00', 256));
        var wireHandle = SessionAsksService.ToWire(handle);
        var page = new SessionAsksPage("ok", epoch, handle.SessionId, SessionAsksService.ToWire(new OwnedAskHead(handle, request, "pending")), SessionAsksService.ToWire(disposition), true);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(page, DesktopJsonContext.Default.SessionAsksPage);
        Console.WriteLine($"Owned ask page: {bytes.Length}; with framing: {bytes.Length + 4096}; budget: {SessionAsksService.MaximumResponseBytes}.");
        Assert.IsTrue(bytes.Length + 4096 <= SessionAsksService.MaximumResponseBytes, $"{bytes.Length} + 4096");
        var action = new SessionAskActionRequest(epoch, new(Guid.NewGuid(), wireHandle,
            [new() { QuestionIndex = 0, FreeformText = new string('\u0001', 8192) }]));
        var input = JsonSerializer.SerializeToUtf8Bytes(action, DesktopJsonContext.Default.SessionAskActionRequest);
        Console.WriteLine($"Owned ask action: {input.Length}; with framing: {input.Length + 4096}; budget: {208 * 1024}.");
        Assert.IsTrue(input.Length + 4096 <= 208 * 1024, $"{input.Length} + 4096");

        // Maximize nested framing independently of the one-question escaped-text case.
        var questions = Enumerable.Range(0, 12).Select(index => new AltaAskQuestion
        {
            Title = "\u0001", Question = new string('\u0001', index == 0 ? 4000 : index == 1 ? 3930 : 1), Description = "",
            Choices = Enumerable.Range(0, 20).Select(_ => new AltaAskChoice { Title = "\u0001", Description = "" }).ToArray(),
            Freeform = new() { Title = "", Placeholder = "" },
        }).ToArray();
        var nested = page with { Head = SessionAsksService.ToWire(new OwnedAskHead(handle, new() { Questions = questions }, "pending")) };
        var nestedBytes = JsonSerializer.SerializeToUtf8Bytes(nested, DesktopJsonContext.Default.SessionAsksPage);
        Console.WriteLine($"Owned ask nested page: {nestedBytes.Length}; with framing: {nestedBytes.Length + 4096}; budget: {SessionAsksService.MaximumResponseBytes}.");
        Assert.IsTrue(nestedBytes.Length + 4096 <= SessionAsksService.MaximumResponseBytes, $"{nestedBytes.Length} + 4096");
        var nestedAction = action with { Action = action.Action with { Answers = Enumerable.Range(0, 12).Select(index => new AltaAskAnswer
        {
            QuestionIndex = index, SelectedChoiceIndexes = Enumerable.Range(0, 20).ToArray(),
            FreeformText = index == 0 ? new string('\u0001', 8192) : null,
        }).ToArray() } };
        var nestedInput = JsonSerializer.SerializeToUtf8Bytes(nestedAction, DesktopJsonContext.Default.SessionAskActionRequest);
        Console.WriteLine($"Owned ask nested action: {nestedInput.Length}; with framing: {nestedInput.Length + 4096}; budget: {208 * 1024}.");
        Assert.IsTrue(nestedInput.Length + 4096 <= 208 * 1024, $"{nestedInput.Length} + 4096");
    }

    [TestMethod]
    public async Task GeneratedInboundHandles_CanonicalBoundariesRoundTripAndValidate()
    {
        var owner = new OwnedSessionAskService(true, static (_, _) => throw new AssertFailedException("No dispatch expected."));
        var epoch = Guid.NewGuid().ToString("D");
        var rpc = new SessionAsksService(owner, epoch);
        var originals = new List<Task>();
        Exception? primary = null;
        try
        {
            foreach (var attachment in new[] { 1L, 9007199254740991L })
            foreach (var response in new[] { 0L, 256L })
            {
                var domain = Handle(attachment, response);
                var wire = SessionAsksService.ToWire(domain);
                Assert.IsTrue(SessionAsksService.TryHandle(wire, out var parsed));
                Assert.AreEqual(domain, parsed);
                var action = new SessionAskActionRequest(epoch, new(Guid.NewGuid(), wire, []));
                var json = JsonSerializer.Serialize(action, DesktopJsonContext.Default.SessionAskActionRequest);
                using (var document = JsonDocument.Parse(json))
                {
                    var value = document.RootElement.GetProperty("action").GetProperty("handle");
                    Assert.AreEqual(JsonValueKind.String, value.GetProperty("attachmentGeneration").ValueKind);
                    Assert.AreEqual(JsonValueKind.String, value.GetProperty("responseGeneration").ValueKind);
                }
                var incoming = JsonSerializer.Deserialize(json, DesktopJsonContext.Default.SessionAskActionRequest);
                Assert.IsNotNull(incoming);
                Assert.AreEqual(action.Action.Handle, incoming.Action.Handle);
                Assert.AreEqual(json, JsonSerializer.Serialize(incoming, DesktopJsonContext.Default.SessionAskActionRequest));
                Assert.IsTrue(SessionAsksService.TryHandle(incoming.Action.Handle, out var incomingDomain));
                Assert.AreEqual(domain, incomingDomain);
                var answer = rpc.AnswerAsync(incoming, default);
                originals.Add(answer);
                var answerResult = await answer.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.AreEqual("ok", answerResult.Status);
                Assert.IsNotNull(answerResult.Disposition);
                Assert.AreEqual("rejected", answerResult.Disposition.Status); // A valid DTO is not queue authority.
                Assert.AreEqual(wire, answerResult.Disposition.Handle);

                var observation = new SessionAskObservationRequest(epoch, action.Action.ActionId, wire);
                var observationJson = JsonSerializer.Serialize(observation, DesktopJsonContext.Default.SessionAskObservationRequest);
                var incomingObservation = JsonSerializer.Deserialize(observationJson, DesktopJsonContext.Default.SessionAskObservationRequest);
                Assert.IsNotNull(incomingObservation);
                Assert.AreEqual(observation, incomingObservation);
                Assert.AreEqual(observationJson, JsonSerializer.Serialize(incomingObservation, DesktopJsonContext.Default.SessionAskObservationRequest));
                Assert.IsTrue(SessionAsksService.TryHandle(incomingObservation.Handle, out var observationDomain));
                Assert.AreEqual(domain, observationDomain);
                var observed = rpc.ObserveAsync(incomingObservation, default);
                originals.Add(observed);
                Assert.AreEqual("not_found", (await observed.WaitAsync(TimeSpan.FromSeconds(5))).Status);
            }
        }
        catch (Exception ex) { primary = ex; throw; }
        finally { rpc.CloseAdmission(); owner.CloseAdmission(); await Join(owner, originals, primary); }
    }

    [TestMethod]
    public async Task GeneratedInboundHandles_RejectNonCanonicalOrMissingGenerationText()
    {
        var owner = new OwnedSessionAskService(true, static (_, _) => throw new AssertFailedException("No dispatch expected."));
        var epoch = Guid.NewGuid().ToString("D");
        var rpc = new SessionAsksService(owner, epoch);
        var originals = new List<Task>();
        Exception? primary = null;
        try
        {
            foreach (var field in new[] { "attachmentGeneration", "responseGeneration" })
            foreach (var invalid in new string?[] { null, "null", "\"\"", "\" \"", "\" 1\"", "\"1 \"", "\"+1\"", "\"-1\"", "\"1e0\"", "\"1.0\"", "\"01\"", "\"00\"", "\"1\\n\"", "\"1\\r\\n\"", "\"1\\u0000\"", "\"\\u00851\"", "\"1\\ufeff\"", "\"\\u0661\"", "\"9007199254740992\"", "\"9223372036854775808\"", "\"9999999999999999999999999999999999999999\"",
                field == "attachmentGeneration" ? "\"0\"" : "\"257\"" })
            {
                var actionJson = InboundJson(epoch, field, invalid, observation: false);
                var action = JsonSerializer.Deserialize(actionJson, DesktopJsonContext.Default.SessionAskActionRequest);
                Assert.IsNotNull(action);
                Assert.IsFalse(SessionAsksService.TryHandle(action.Action.Handle, out _), actionJson);
                var answer = rpc.AnswerAsync(action, default);
                originals.Add(answer);
                Assert.AreEqual("invalid_request", (await answer.WaitAsync(TimeSpan.FromSeconds(5))).Status, actionJson);
                var cancel = rpc.CancelAsync(action, default);
                originals.Add(cancel);
                Assert.AreEqual("invalid_request", (await cancel.WaitAsync(TimeSpan.FromSeconds(5))).Status, actionJson);

                var observationJson = InboundJson(epoch, field, invalid, observation: true);
                var observation = JsonSerializer.Deserialize(observationJson, DesktopJsonContext.Default.SessionAskObservationRequest);
                Assert.IsNotNull(observation);
                Assert.IsFalse(SessionAsksService.TryHandle(observation.Handle, out _), observationJson);
                var observed = rpc.ObserveAsync(observation, default);
                originals.Add(observed);
                Assert.AreEqual("invalid_request", (await observed.WaitAsync(TimeSpan.FromSeconds(5))).Status, observationJson);
            }
        }
        catch (Exception ex) { primary = ex; throw; }
        finally { rpc.CloseAdmission(); owner.CloseAdmission(); await Join(owner, originals, primary); }
    }

    [TestMethod]
    public void GeneratedInboundHandles_RejectWrongJsonGenerationTypes()
    {
        var epoch = Guid.NewGuid().ToString("D");
        foreach (var field in new[] { "attachmentGeneration", "responseGeneration" })
        foreach (var value in new[] { "0", "1", "256", "9007199254740991", "1.0", "1e0", "true", "[]", "{}" })
        {
            var action = InboundJson(epoch, field, value, observation: false);
            Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(action, DesktopJsonContext.Default.SessionAskActionRequest));
            var observation = InboundJson(epoch, field, value, observation: true);
            Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(observation, DesktopJsonContext.Default.SessionAskObservationRequest));
        }
    }

    private static OwnedAskHandle Handle(long attachment, long response) => new(Guid.NewGuid(), Guid.NewGuid(), attachment,
        "fixture", "session", "run", Guid.NewGuid().ToString("D"), response);

    private static string InboundJson(string epoch, string field, string? replacementJson, bool observation)
    {
        var handle = SessionAsksService.ToWire(Handle(1, 0));
        var json = observation
            ? JsonSerializer.Serialize(new SessionAskObservationRequest(epoch, Guid.NewGuid(), handle), DesktopJsonContext.Default.SessionAskObservationRequest)
            : JsonSerializer.Serialize(new SessionAskActionRequest(epoch, new(Guid.NewGuid(), handle, [])), DesktopJsonContext.Default.SessionAskActionRequest);
        var root = JsonNode.Parse(json);
        Assert.IsNotNull(root);
        var target = observation ? root["handle"] : root["action"]?["handle"];
        Assert.IsNotNull(target);
        if (replacementJson is null) target.AsObject().Remove(field); // Missing, distinct from explicit JSON null.
        else target[field] = JsonNode.Parse(replacementJson);
        return root.ToJsonString();
    }

    private static async Task Join(OwnedSessionAskService owner, IReadOnlyList<Task> originals, Exception? primary)
    {
        var drain = owner.DrainAsync();
        var all = Task.WhenAll(originals.Append(drain));
        try { await all.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (Exception ex)
        {
            ex.Data["OriginalJoin"] = all;
            ex.Data["AskOwner"] = owner;
            if (primary is not null) throw new AggregateException(primary, ex);
            throw;
        }
    }
}
