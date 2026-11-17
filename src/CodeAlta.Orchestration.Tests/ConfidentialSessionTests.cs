using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;

namespace CodeAlta.Orchestration.Tests;

[TestClass]
public sealed class ConfidentialSessionTests
{
    private const string Name = "Dupont";
    private const string Token = "[PERSONNE_001]";

    [TestMethod]
    public async Task ASessionOfAPseudonymizedProject_WithoutAPlugin_SendsNothing()
    {
        await using var root = new HeadlessTestRoot();
        Pseudonymize(root.ProjectRoot);
        var provider = new ScriptedTurnProvider();
        var host = await root.CreateHostAsync(provider);
        var options = HeadlessTestRoot.Options(provider, root.ProjectRoot);
        var session = await host.RuntimeService.CreateProjectSessionAsync(host.CurrentProject, options, "Confidential");

        Exception? failure = null;
        try
        {
            await host.RuntimeService.SendAsync(session, options, new AgentSendOptions { Input = AgentInput.Text($"Le dossier de M. {Name}") }).WithinAsync(TestDeadlines.Hang);
        }
        catch (Exception exception) when (exception is not AssertFailedException)
        {
            failure = exception;
        }

        Assert.AreEqual(0, provider.Requests.Count, "Nothing reaches the provider.");
        Assert.IsTrue(await host.RuntimeService.IsConfidentialAsync(session, CancellationToken.None));
        _ = failure;
    }

    [TestMethod]
    public async Task ASessionOfAPseudonymizedProject_SendsThroughTheBoundaryOfThePlugin_AndStaysConfidential()
    {
        await using var root = new HeadlessTestRoot();
        Pseudonymize(root.ProjectRoot);
        var provider = new ScriptedTurnProvider();
        var host = await root.CreateHostAsync(provider);
        host.RuntimeService.ProviderBoundaryOf = _ => new ReplacingBoundary();
        var options = HeadlessTestRoot.Options(provider, root.ProjectRoot);
        var session = await host.RuntimeService.CreateProjectSessionAsync(host.CurrentProject, options, "Confidential");
        provider.Turns.Enqueue(ScriptedTurnProvider.Reply($"Bonjour {Token}."));

        await host.RuntimeService.SendAsync(session, options, new AgentSendOptions { Input = AgentInput.Text($"Le dossier de M. {Name}") }).WithinAsync(TestDeadlines.Hang);

        var request = provider.Requests.Single();
        var sent = string.Join("\n", request.Conversation.Select(static message => message.ToJson()));
        Assert.IsFalse(sent.Contains(Name, StringComparison.Ordinal), sent);
        StringAssert.Contains(sent, Token);
        var history = await host.RuntimeService.TryReadStoredHistoryAsync(session);
        Assert.IsNotNull(history);
        var journaled = string.Join("\n", history.Select(static @event => @event.ToJson()));
        StringAssert.Contains(journaled, $"Bonjour {Name}.");

        // Once confidential, it stays so when the project no longer asks for it.
        File.Delete(Path.Combine(root.ProjectRoot, ".alta", "config.toml"));
        var deadline = DateTime.UtcNow + TestDeadlines.Hang;
        while (!await host.RuntimeService.IsConfidentialAsync(session, CancellationToken.None))
        {
            Assert.IsTrue(DateTime.UtcNow < deadline, "The session records that it is confidential.");
            await Task.Delay(50);
        }

        await Assert.ThrowsExactlyAsync<NotSupportedException>(() => host.RuntimeService.AskSideQuestionAsync(session.SessionId, "side question", static (_, _) => ValueTask.CompletedTask));
    }

    [TestMethod]
    public async Task ASessionOfAProjectThatDoesNotPseudonymize_SendsAsItIs()
    {
        await using var root = new HeadlessTestRoot();
        var provider = new ScriptedTurnProvider();
        var host = await root.CreateHostAsync(provider);
        host.RuntimeService.ProviderBoundaryOf = _ => new ReplacingBoundary();
        var options = HeadlessTestRoot.Options(provider, root.ProjectRoot);
        var session = await host.RuntimeService.CreateProjectSessionAsync(host.CurrentProject, options, "Plain");

        await host.RuntimeService.SendAsync(session, options, new AgentSendOptions { Input = AgentInput.Text($"Le dossier de M. {Name}") }).WithinAsync(TestDeadlines.Hang);

        StringAssert.Contains(string.Join("\n", provider.Requests.Single().Conversation.Select(static message => message.ToJson())), Name);
        Assert.IsFalse(await host.RuntimeService.IsConfidentialAsync(session, CancellationToken.None));
    }

    private static void Pseudonymize(string projectRoot)
    {
        Directory.CreateDirectory(Path.Combine(projectRoot, ".alta"));
        File.WriteAllText(Path.Combine(projectRoot, ".alta", "config.toml"), "[privacy]\npseudonymize = true\n");
    }

    private sealed class ReplacingBoundary : IAgentProviderBoundary
    {
        public ValueTask<AgentTurnRequest> PrepareRequestAsync(AgentTurnRequest request, CancellationToken cancellationToken)
            => ValueTask.FromResult(request with
            {
                Conversation = [.. request.Conversation.Select(static message => message with
                {
                    Parts = [.. message.Parts.Select(static part => part is AgentMessagePart.Text text ? new AgentMessagePart.Text(text.Value.Replace(Name, Token, StringComparison.Ordinal)) : part)],
                })],
            });

        public IAgentBoundaryTextStream CreateResponseStream(AgentContentKind kind, string contentId) => new PassThrough();

        public AgentTurnResponse RestoreResponse(AgentTurnRequest request, AgentTurnResponse response)
            => response with
            {
                AssistantMessage = response.AssistantMessage with
                {
                    Parts = [.. response.AssistantMessage.Parts.Select(static part => part is AgentMessagePart.Text text ? new AgentMessagePart.Text(text.Value.Replace(Token, Name, StringComparison.Ordinal)) : part)],
                },
            };

        private sealed class PassThrough : IAgentBoundaryTextStream
        {
            public string Write(string part) => part.Replace(Token, Name, StringComparison.Ordinal);

            public string Flush() => string.Empty;
        }
    }
}
