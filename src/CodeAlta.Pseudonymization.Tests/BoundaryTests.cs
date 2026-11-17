using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;

namespace CodeAlta.Pseudonymization.Tests;

[TestClass]
public sealed class BoundaryTests
{
    private const string Email = "sophie.martin@exemple.fr";
    private const string Iban = "FR76 3000 6000 0112 3456 7890 189";

    [TestMethod]
    public async Task ARequest_SendsPseudonyms_AndTheAnswerGetsTheRealValuesBack()
    {
        var boundary = CreateBoundary();
        var request = Request(User($"Écris à {Email} pour l'IBAN {Iban}."));

        var sent = await boundary.PrepareRequestAsync(request, CancellationToken.None);

        var text = Text(sent);
        Assert.IsFalse(text.Contains(Email, StringComparison.Ordinal), text);
        Assert.IsFalse(text.Contains(Iban, StringComparison.Ordinal), text);
        StringAssert.Contains(text, "[EMAIL_001]");
        StringAssert.Contains(text, "[IBAN_001]");
        StringAssert.Contains(sent.SessionInstructions, "Pseudonymization: on");

        var answer = boundary.RestoreResponse(sent, Answer("C'est fait : [EMAIL_001] a reçu le RIB [iban_1]."));
        Assert.AreEqual($"C'est fait : {Email} a reçu le RIB {Iban}.", ((AgentMessagePart.Text)answer.AssistantMessage.Parts[0]).Value);
    }

    [TestMethod]
    public async Task TheNextRequest_SendsTheSameBytes_ForWhatWasSentAndAnswered()
    {
        var boundary = CreateBoundary();
        var first = Request(User($"Écris à {Email}."));
        var sent = await boundary.PrepareRequestAsync(first, CancellationToken.None);
        const string written = "Bien sûr, j'écris à [EMAIL_001] (Paris, 3 mars 2026).";
        var answer = boundary.RestoreResponse(sent, Answer(written));

        var second = Request(User($"Écris à {Email}."), answer.AssistantMessage, User("Merci."));
        var sentAgain = await boundary.PrepareRequestAsync(second, CancellationToken.None);

        Assert.AreEqual(Text(sent.Conversation[0]), Text(sentAgain.Conversation[0]));
        Assert.AreEqual(written, ((AgentMessagePart.Text)sentAgain.Conversation[1].Parts[0]).Value, "The answer goes back as the provider wrote it.");
    }

    [TestMethod]
    public async Task TheArgumentsOfATool_GetTheRealValues_UnlessItSendsThemAway()
    {
        var boundary = CreateBoundary(static tool => tool != "webget");
        var sent = await boundary.PrepareRequestAsync(Request(User($"Le client est {Email}.")), CancellationToken.None);
        var response = new AgentTurnResponse
        {
            AssistantMessage = new AgentConversationMessage(AgentConversationRole.Assistant,
            [
                new AgentMessagePart.ToolCall("write", "write_file", JsonSerializer.SerializeToElement(new { path = "lettre.md", content = "Écrire à \"[EMAIL_001]\"." })),
                new AgentMessagePart.ToolCall("fetch", "webget", JsonSerializer.SerializeToElement(new { url = "https://exemple.fr/?q=[EMAIL_001]" })),
                new AgentMessagePart.ToolCall("bad", "write_file", JsonSerializer.SerializeToElement(new { path = "x.md", content = "[PERSONNE_007]" })),
            ]),
        };

        var restored = boundary.RestoreResponse(sent, response);

        var write = (AgentMessagePart.ToolCall)restored.AssistantMessage.Parts[0];
        Assert.AreEqual($"Écrire à \"{Email}\".", write.Arguments.GetProperty("content").GetString());
        var fetch = (AgentMessagePart.ToolCall)restored.AssistantMessage.Parts[1];
        Assert.AreEqual("https://exemple.fr/?q=[EMAIL_001]", fetch.Arguments.GetProperty("url").GetString());
        Assert.IsNotNull(restored.UnrunnableToolCalls);
        Assert.IsTrue(restored.UnrunnableToolCalls.ContainsKey("bad"), "A call that names a pseudonym the session does not have does not run.");
        Assert.IsFalse(restored.UnrunnableToolCalls.ContainsKey("write"));

        var again = await boundary.PrepareRequestAsync(Request(User($"Le client est {Email}."), restored.AssistantMessage), CancellationToken.None);
        var sentWrite = (AgentMessagePart.ToolCall)again.Conversation[1].Parts[0];
        Assert.AreEqual(((AgentMessagePart.ToolCall)response.AssistantMessage.Parts[0]).Arguments.GetRawText(), sentWrite.Arguments.GetRawText());
    }

    [TestMethod]
    public async Task ToolOutputs_ArePseudonymized()
    {
        var boundary = CreateBoundary();
        var request = Request(
            User("Lis le fichier."),
            new AgentConversationMessage(AgentConversationRole.Assistant, [new AgentMessagePart.ToolCall("read", "read_file", JsonSerializer.SerializeToElement(new { path = "client.txt" }))]),
            new AgentConversationMessage(AgentConversationRole.Tool, [new AgentMessagePart.ToolResult("read", new AgentToolResult(true, [new AgentToolResultItem.Text($"Courriel : {Email}")]))]));

        var sent = await boundary.PrepareRequestAsync(request, CancellationToken.None);

        var output = ((AgentToolResultItem.Text)((AgentMessagePart.ToolResult)sent.Conversation[2].Parts[0]).Result.Items[0]).Value;
        Assert.AreEqual("Courriel : [EMAIL_001]", output);
    }

    [TestMethod]
    public async Task APseudonymSplitBetweenTheParts_OfAStream_IsRestoredWhole()
    {
        var boundary = CreateBoundary();
        await boundary.PrepareRequestAsync(Request(User($"Écris à {Email}.")), CancellationToken.None);
        const string written = "J'écris à [EMAIL_001] demain.";
        for (var cut = 1; cut < written.Length; cut++)
        {
            var stream = boundary.CreateResponseStream(AgentContentKind.Assistant, "text");
            var shown = stream.Write(written[..cut]) + stream.Write(written[cut..]) + stream.Flush();
            Assert.AreEqual($"J'écris à {Email} demain.", shown, $"cut at {cut}");
        }
    }

    [TestMethod]
    public async Task ValuesTheUserAlwaysHides_AreHidden_AndOrdinaryDatesAreKept()
    {
        var boundary = CreateBoundary(alwaysHide: [(PiiEntities.Person, "Dupont")]);

        var sent = await boundary.PrepareRequestAsync(Request(User("M. DUPONT signe l'acte le 3 mars 2026.")), CancellationToken.None);

        Assert.AreEqual("M. [PERSONNE_001] signe l'acte le 3 mars 2026.", Text(sent));
    }

    [TestMethod]
    public async Task ATry_PseudonymizesOneText_AsARequestWould()
    {
        var boundary = CreateBoundary();

        var hidden = await boundary.PseudonymizeAsync($"Écrire à {Email} (IBAN {Iban}).", CancellationToken.None);

        Assert.AreEqual("Écrire à [EMAIL_001] (IBAN [IBAN_001]).", hidden);
        CollectionAssert.AreEqual(new[] { Email, Iban }, boundary.Table.Entries.Select(static entry => entry.Value).ToArray());
    }

    [TestMethod]
    public async Task ARecognizerThatFails_SendsNothing()
    {
        var boundary = new PseudonymizingBoundary(new PiiAnalyzer([new FailingRecognizer()]), new PseudonymTable(), new PseudonymCache(), new PseudonymizerOptions());

        await Assert.ThrowsExactlyAsync<AgentProviderBoundaryException>(() => boundary.PrepareRequestAsync(Request(User("Bonjour")), CancellationToken.None).AsTask());
    }

    [TestMethod]
    public void TheTable_KeepsItsNumbers_WhenItIsReadAgain()
    {
        var table = new PseudonymTable();
        Assert.AreEqual("[PERSONNE_001]", table.TokenFor(PiiEntities.Person, "Dupont"));
        Assert.AreEqual("[PERSONNE_002]", table.TokenFor(PiiEntities.Person, "Martin"));
        Assert.AreEqual("[PERSONNE_001]", table.TokenFor(PiiEntities.Person, "Dupont"));

        var read = PseudonymTable.FromJson(table.ToJson());

        Assert.AreEqual("[PERSONNE_003]", read.TokenFor(PiiEntities.Person, "Durand"));
        Assert.AreEqual("[PERSONNE_002]", read.Canonical("PERSONNE-2"));
        Assert.AreEqual("[PERSONNE_001]", read.Canonical("[PERSONE_001]"));
        Assert.IsNull(read.Canonical("personne 1"), "Without brackets, a space is ordinary French.");
        Assert.IsNull(read.Canonical("[PERSONNE_009]"));
    }

    private static PseudonymizingBoundary CreateBoundary(Func<string, bool>? tools = null, IReadOnlyList<(string, string)>? alwaysHide = null)
        => new(new PiiAnalyzer(PiiRecognizerSets.French()), new PseudonymTable(), new PseudonymCache(), new PseudonymizerOptions
        {
            ToolReceivesRealValues = tools ?? (static _ => true),
            AlwaysHide = alwaysHide ?? [],
        });

    private static AgentConversationMessage User(string text) => new(AgentConversationRole.User, [new AgentMessagePart.Text(text)]);

    private static AgentTurnResponse Answer(string text)
        => new() { AssistantMessage = new AgentConversationMessage(AgentConversationRole.Assistant, [new AgentMessagePart.Text(text)]) };

    private static string Text(AgentTurnRequest request) => string.Join("\n", request.Conversation.Select(Text));

    private static string Text(AgentConversationMessage message) => string.Concat(message.Parts.OfType<AgentMessagePart.Text>().Select(static part => part.Value));

    private static AgentTurnRequest Request(params AgentConversationMessage[] conversation)
    {
        var provider = new ModelProviderRuntimeDescriptor
        {
            ProtocolFamily = "openai-responses",
            ProviderKey = "fixture",
            DisplayName = "Fixture",
            TransportKind = AgentTransportKind.OpenAIResponses,
            BaseUri = new Uri("https://provider.invalid/v1"),
            Profile = new AgentProviderProfile(),
        };
        return new AgentTurnRequest
        {
            Provider = provider,
            ProviderId = new ModelProviderId("fixture"),
            SessionId = "session",
            RunId = new AgentRunId("run"),
            Conversation = conversation,
            Tools = [],
            State = new AgentSessionState { SessionId = "session", ProtocolFamily = provider.ProtocolFamily, ProviderKey = provider.ProviderKey, UpdatedAt = DateTimeOffset.UtcNow },
        };
    }

    private sealed class FailingRecognizer : PiiRecognizer
    {
        public override string Name => "failing";

        public override ValueTask<IReadOnlyList<PiiSpan>> AnalyzeAsync(string text, CancellationToken cancellationToken)
            => throw new InvalidOperationException("The model is not loaded.");
    }
}
