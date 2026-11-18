using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Catalog;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Orchestration.Tests;

[TestClass]
public sealed class ConfidentialSessionTests
{
    private const string Name = "Dupont";
    private const string Token = "[PERSON_001]";

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
        var options = HeadlessTestRoot.Options(provider, root.ProjectRoot);
        var session = await host.RuntimeService.CreateProjectSessionAsync(host.CurrentProject, options, "Confidential");
        // Whether the session is recorded as confidential when its first request reaches the boundary, the project asking for it no longer.
        Task<bool>? recordedAtTheBoundary = null;
        host.RuntimeService.ProviderBoundaryOf = _ =>
        {
            File.Delete(Path.Combine(root.ProjectRoot, ".alta", "config.toml"));
            recordedAtTheBoundary ??= host.RuntimeService.IsConfidentialAsync(session, CancellationToken.None);
            return new ReplacingBoundary();
        };
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

        // Once confidential, it stays so when the project no longer asks for it: it was recorded before anything was sent.
        Assert.IsNotNull(recordedAtTheBoundary);
        Assert.IsTrue(await recordedAtTheBoundary, "The session records that it is confidential before its first request.");
        Assert.IsTrue(await host.RuntimeService.IsConfidentialAsync(session, CancellationToken.None));

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

    [TestMethod]
    public async Task ASessionOfAProjectWhoseConfigurationCannotBeRead_SendsNothing_UntilItCanBeRead()
    {
        await using var root = new HeadlessTestRoot();
        var provider = new ScriptedTurnProvider();
        var host = await root.CreateHostAsync(provider);
        host.RuntimeService.ProviderBoundaryOf = _ => new ReplacingBoundary();
        var options = HeadlessTestRoot.Options(provider, root.ProjectRoot);
        var session = await host.RuntimeService.CreateProjectSessionAsync(host.CurrentProject, options, "Unknown");
        var config = Path.Combine(root.ProjectRoot, ".alta", "config.toml");
        Directory.CreateDirectory(Path.GetDirectoryName(config)!);
        File.WriteAllText(config, "[privacy\npseudonymize = ");

        try
        {
            await host.RuntimeService.SendAsync(session, options, new AgentSendOptions { Input = AgentInput.Text($"Le dossier de M. {Name}") }).WithinAsync(TestDeadlines.Hang);
        }
        catch (Exception exception) when (exception is not AssertFailedException)
        {
        }

        Assert.AreEqual(0, provider.Requests.Count, "Nothing reaches the provider while the project may be pseudonymized.");
        Assert.IsTrue(await host.RuntimeService.IsConfidentialAsync(session, CancellationToken.None));

        // The file that could not be read did not make the session confidential for good.
        File.WriteAllText(config, "# nothing\n");
        Assert.IsFalse(await host.RuntimeService.IsConfidentialAsync(session, CancellationToken.None));
        await host.RuntimeService.SendAsync(session, options, new AgentSendOptions { Input = AgentInput.Text($"Le dossier de M. {Name}") }).WithinAsync(TestDeadlines.Hang);
        StringAssert.Contains(string.Join("\n", provider.Requests.Single().Conversation.Select(static message => message.ToJson())), Name);
    }

    [TestMethod]
    public async Task ASessionAConfidentialSessionCreated_IsConfidential_WhateverItsParent()
    {
        await using var root = new HeadlessTestRoot();
        Pseudonymize(root.ProjectRoot);
        var provider = new ScriptedTurnProvider();
        var host = await root.CreateHostAsync(provider);
        var options = HeadlessTestRoot.Options(provider, root.ProjectRoot);
        var chatOptions = new SessionExecutionOptions
        {
            ProviderId = options.ProviderId,
            ProviderKey = options.ProviderKey,
            WorkingDirectory = root.GlobalRoot,
            Model = options.Model,
            OnPermissionRequest = options.OnPermissionRequest,
        };
        var confidential = await host.RuntimeService.CreateProjectSessionAsync(host.CurrentProject, options, "Confidential");
        var plain = await host.RuntimeService.CreateGlobalSessionAsync(chatOptions, "Plain");
        var by = new AltaActorProvenance { Kind = "agent", SourceSessionId = confidential.SessionId };

        var child = await host.RuntimeService.CreateGlobalSessionAsync(chatOptions, "Child", plain.SessionId, by);
        // A parent that no longer exists ends its branch of the lineage: it makes nothing confidential.
        var orphan = await host.RuntimeService.CreateGlobalSessionAsync(chatOptions, "Orphan", "no-such-session", null);

        Assert.IsFalse(await host.RuntimeService.IsConfidentialAsync(plain, CancellationToken.None));
        Assert.IsTrue(await host.RuntimeService.IsConfidentialAsync(child, CancellationToken.None), "The session that created it is confidential, its parent is not.");
        Assert.IsFalse(await host.RuntimeService.IsConfidentialAsync(orphan, CancellationToken.None));
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
