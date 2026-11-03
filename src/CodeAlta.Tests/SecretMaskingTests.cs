using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Catalog.Secrets;

namespace CodeAlta.Tests;

[TestClass]
public sealed class SecretMaskingTests
{
    private const string Secret = "sk-live-4f9a2c7e1b";

    [TestMethod]
    public void Text_IsMasked_LongestSecretFirst_AndShortValuesAreLeft()
    {
        var masker = new AgentSecretMasker(() => [Secret, Secret + "-extended", "abc"]);

        Assert.AreEqual("key=*** and ***", masker.MaskText($"key={Secret}-extended and {Secret}"));
        Assert.AreEqual("abc stays: shorter than four characters", masker.MaskText("abc stays: shorter than four characters"));
        var unchanged = "nothing secret";
        Assert.AreSame(unchanged, masker.MaskText(unchanged));
        Assert.IsTrue(masker.Contains($"x{Secret}x"));
    }

    [TestMethod]
    public void Json_IsMaskedInsideItsStringsOnly_AndStaysValid()
    {
        const string word = "type";
        var masker = new AgentSecretMasker(() => [word, "p+ss\"w0rd"]);
        var json = JsonSerializer.Serialize(new { type = "the type is here", other = "p+ss\"w0rd" });

        var masked = masker.MaskJson(json);

        using var document = JsonDocument.Parse(masked);
        Assert.AreEqual("the *** is here", document.RootElement.GetProperty("type").GetString(), "The name of a property is no value: it stays.");
        Assert.AreEqual("***", document.RootElement.GetProperty("other").GetString(), "A secret escaped in the JSON is masked as it is written there.");
    }

    [TestMethod]
    public void InPlace_ShortensTheText()
    {
        var masker = new AgentSecretMasker(() => [Secret]);
        var text = $"[INF] token {Secret} sent; again {Secret}.".ToCharArray();

        var length = masker.MaskInPlace(text);

        Assert.AreEqual("[INF] token *** sent; again ***.", new string(text, 0, length));
    }

    [TestMethod]
    public void AToolResult_IsMaskedInItsTextErrorDisplayAndValue()
    {
        var masker = new AgentSecretMasker(() => [Secret]);
        var result = new AgentToolResult(false, [new AgentToolResultItem.Text($"printed {Secret}")], $"failed with {Secret}")
        {
            Display = $"whole {Secret}",
            Structured = JsonSerializer.SerializeToElement(new { output = Secret }),
        };

        var masked = masker.MaskResult(result);

        Assert.AreEqual("printed ***", ((AgentToolResultItem.Text)masked.Items[0]).Value);
        Assert.AreEqual("failed with ***", masked.Error);
        Assert.AreEqual("whole ***", masked.Display);
        Assert.AreEqual("***", masked.Structured!.Value.GetProperty("output").GetString());
        var clean = new AgentToolResult(true, [new AgentToolResultItem.Text("fine")]);
        Assert.AreSame(clean, masker.MaskResult(clean));
    }

    [TestMethod]
    public void TheValues_AreReadAgain_WhenTheStoreChanges()
    {
        var values = new List<string>();
        var masker = new AgentSecretMasker(() => [.. values]);
        Assert.AreEqual(Secret, masker.MaskText(Secret));

        values.Add(Secret);
        masker.Refresh();

        Assert.AreEqual("***", masker.MaskText(Secret));
    }

    [TestMethod]
    public async Task ASecretATool_Prints_NeverLandsInTheJournal_NorReachesTheModelOrTheEvents()
    {
        var root = Path.Combine(Path.GetTempPath(), "codealta-masking-" + Guid.NewGuid().ToString("N"));
        var store = new LocalSecretStore(root, new MasterKeySecretProtector(new FileSecretKeyStore(Path.Combine(root, "secrets", "key"))));
        store.SetSecret("GRAFANA_TOKEN", Secret, "tests");
        try
        {
            var executor = new EchoingTurnExecutor();
            var runtime = CreateRuntime(Path.Combine(root, "state"), executor, new AgentSecretMasker(store.GetSecretValues));
            var printed = new AgentToolDefinition(
                new AgentToolSpec("print_token", "Prints the token.", JsonDocument.Parse("""{"type":"object"}""").RootElement.Clone()),
                static (_, _) => Task.FromResult(new AgentToolResult(true, [new AgentToolResultItem.Text($"GRAFANA_TOKEN={Secret}")])));
            var published = new List<string>();
            await using (var session = await runtime.CreateSessionAsync(new AgentSessionCreateOptions
            {
                ProviderKey = "fixture",
                Model = "model",
                WorkingDirectory = root,
                Tools = [printed],
                OnPermissionRequest = static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce)),
            }))
            {
                using var subscription = session.Subscribe(@event => published.Add(@event.ToJson()));
                await session.SendAsync(new AgentSendOptions { Input = AgentInput.Text($"Use the token {Secret} to check Grafana.") });
            }

            var journals = Directory.EnumerateFiles(Path.Combine(root, "state"), "*", SearchOption.AllDirectories).ToArray();
            Assert.IsTrue(journals.Length > 0);
            foreach (var journal in journals)
            {
                var text = File.ReadAllText(journal);
                Assert.IsFalse(text.Contains(Secret, StringComparison.Ordinal), $"{journal} holds the secret.");
            }

            Assert.IsTrue(journals.Any(journal => File.ReadAllText(journal).Contains("GRAFANA_TOKEN=***", StringComparison.Ordinal)), "The output of the tool is journaled, masked.");
            Assert.IsTrue(published.Count > 0);
            Assert.IsFalse(published.Any(static json => json.Contains(Secret, StringComparison.Ordinal)), "What the user is shown holds no secret.");
            Assert.IsFalse(executor.Seen.Any(static text => text.Contains("GRAFANA_TOKEN=" + Secret, StringComparison.Ordinal)), "The model is never sent the secret a tool printed.");
            Assert.IsTrue(executor.Seen.Any(static text => text.Contains("GRAFANA_TOKEN=***", StringComparison.Ordinal)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static AgentRuntime CreateRuntime(string stateRoot, IModelProviderTurnExecutor executor, AgentSecretMasker masker)
        => new(new ModelProviderId("fixture"), "Fixture", new AgentRuntimeOptions
        {
            StateRootPath = stateRoot,
            SecretMasker = masker,
            Providers =
            [
                new AgentRuntimeProviderRegistration
                {
                    Provider = new ModelProviderRuntimeDescriptor
                    {
                        ProtocolFamily = "openai-responses",
                        ProviderKey = "fixture",
                        DisplayName = "Fixture",
                        TransportKind = AgentTransportKind.OpenAIResponses,
                        BaseUri = new Uri("https://provider.invalid/v1"),
                        IsDefault = true,
                        Profile = new AgentProviderProfile { MaxTokensFieldName = "max_output_tokens" },
                    },
                    TurnExecutor = executor,
                },
            ],
        });

    // Calls the tool once, then answers; it records the text of every message it is sent after the first turn.
    private sealed class EchoingTurnExecutor : IModelProviderTurnExecutor
    {
        private int _turns;

        public List<string> Seen { get; } = [];

        public Task<AgentTurnResponse> ExecuteTurnAsync(AgentTurnRequest request, Func<AgentTurnDelta, CancellationToken, ValueTask> onUpdate, CancellationToken cancellationToken = default)
        {
            if (++_turns > 1)
            {
                Seen.AddRange(request.Conversation.Select(static message => message.ToJson()));
            }

            return Task.FromResult(new AgentTurnResponse
            {
                AssistantMessage = _turns == 1
                    ? new AgentConversationMessage(AgentConversationRole.Assistant, [new AgentMessagePart.ToolCall("call-1", "print_token", JsonSerializer.SerializeToElement(new { }))])
                    : new AgentConversationMessage(AgentConversationRole.Assistant, [new AgentMessagePart.Text("Grafana answered.")]),
            });
        }
    }
}
