using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Catalog;
using CodeAlta.Orchestration.Hosting;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Orchestration.Tests;

/// <summary>
/// The labels of a session, kept by the state records of its journal and by the list of the sessions, and the archive
/// policies that archive the sessions of a label.
/// </summary>
[TestClass]
public sealed class SessionLabelsRuntimeTests
{
    [TestMethod]
    public async Task Labels_AreKeptByTheJournal_ThroughOtherChanges_AndAfterARestart()
    {
        using var temp = TempDirectory.Create();
        var provider = new IdleProvider();
        string sessionId;
        await using (var host = await CreateHostAsync(temp, provider))
        {
            sessionId = (await host.Commands.CreateDraftSessionAsync(host.CurrentProject, provider.Descriptor, null)).SessionId;
            CollectionAssert.AreEqual(new[] { "audit", "bug" }, (await host.RuntimeService.SetSessionLabelsAsync(sessionId, ["audit", "bug", "audit", "Not an id!"], null))!.ToArray());
            CollectionAssert.AreEqual(new[] { "audit", "sre" }, (await host.RuntimeService.SetSessionLabelsAsync(sessionId, ["sre"], ["bug"]))!.ToArray(),
                "The session keeps its labels in their order and gets the new ones after them.");
            Assert.IsNull(await host.RuntimeService.SetSessionLabelsAsync("missing-session", ["audit"], null));
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => host.RuntimeService.SetSessionLabelsAsync(sessionId,
                [.. Enumerable.Range(0, SessionRuntimeService.MaximumSessionLabels).Select(index => $"label-{index}")], null));

            // Another change of the state of the session writes its labels again.
            Assert.AreEqual("ok", (await host.Commands.SetReviewBeforeCommitAsync(sessionId, true)).Status);
            var session = (await host.RuntimeService.TryGetRecoverableSessionAsync(sessionId))!;
            CollectionAssert.AreEqual(new[] { "audit", "sre" }, (await host.RuntimeService.GetSessionLabelsAsync(session)).ToArray());
        }

        await using (var host = await CreateHostAsync(temp, provider))
        {
            var listed = await FindAsync(host, sessionId);
            CollectionAssert.AreEqual(new[] { "audit", "sre" }, (await host.RuntimeService.GetSessionLabelsAsync(listed)).ToArray());
            CollectionAssert.AreEqual(new[] { "audit", "sre" }, listed.CachedViewState?.Labels?.ToArray() ?? [], "The list of the sessions has them without reading the journal.");
            Assert.HasCount(0, (await host.RuntimeService.SetSessionLabelsAsync(sessionId, null, ["audit", "sre"]))!);
            Assert.HasCount(0, await host.RuntimeService.GetSessionLabelsAsync((await host.RuntimeService.TryGetRecoverableSessionAsync(sessionId))!));
        }
    }

    [TestMethod]
    public async Task AnArchivePolicyOfAProject_ArchivesTheOlderSessionsOfALabel_AndKeepsWhatIsProtected()
    {
        using var temp = TempDirectory.Create();
        Directory.CreateDirectory(Path.Combine(temp.ProjectRoot, ".alta"));
        File.WriteAllText(Path.Combine(temp.ProjectRoot, ".alta", "config.toml"), "[[sessions.archive_policies]]\nlabels = [\"audit\"]\nkeep_last = 1\n");
        var provider = new IdleProvider();
        await using var host = await CreateHostAsync(temp, provider);
        var ids = new List<string>();
        for (var index = 0; index < 3; index++)
        {
            var id = (await host.Commands.CreateDraftSessionAsync(host.CurrentProject, provider.Descriptor, null)).SessionId;
            await host.RuntimeService.SetSessionLabelsAsync(id, ["audit"], null);
            ids.Add(id);
            await Task.Delay(20);
        }

        var unlabeled = (await host.Commands.CreateDraftSessionAsync(host.CurrentProject, provider.Descriptor, null)).SessionId;
        // Nothing is archived before the windows and the bookmarks said what they hold.
        var protection = new SessionRetentionProtection();
        // As the desktop applies them: through the owner of the commands, which refuses what a manual archive refuses.
        var service = new SessionArchivePolicyService(host.RuntimeService, new CodeAltaConfigStore(host.CatalogOptions), host.SpaceCatalog, protection,
            archive: host.Commands);
        Assert.AreEqual("skipped", (await service.RunOnceAsync(DateTimeOffset.UtcNow)).Status);

        // The first one is shown in a window: it is kept. The last one is the most recent: the policy keeps it.
        protection.SealParticipants(["test"]);
        protection.Publish("test", [ids[0]], ready: true);
        var pass = await service.RunOnceAsync(DateTimeOffset.UtcNow);
        Assert.AreEqual("completed", pass.Status);
        CollectionAssert.AreEqual(new[] { ids[1] }, pass.Archived.ToArray());
        Assert.AreEqual(1, pass.Kept);
        Assert.IsTrue((await FindAsync(host, ids[1])).CachedViewState?.Archived ?? false);
        foreach (var kept in new[] { ids[0], ids[2], unlabeled }) Assert.IsFalse((await FindAsync(host, kept)).CachedViewState?.Archived ?? false, kept);
    }

    private static async Task<SessionViewDescriptor> FindAsync(CodeAltaHost host, string sessionId)
    {
        await foreach (var session in host.RuntimeService.ListRecoverableSessionsAsync())
        {
            if (session.SessionId == sessionId) return session;
        }

        throw new AssertFailedException($"The session {sessionId} is not listed.");
    }

    private static Task<CodeAltaHost> CreateHostAsync(TempDirectory temp, IdleProvider provider)
        => CodeAltaHost.CreateAsync(
            new CodeAltaHostOptions
            {
                GlobalRoot = temp.GlobalRoot,
                CurrentProjectPath = temp.ProjectRoot,
                IsHeadless = true,
                AutoApproveOwnedPermissions = true,
                OwnedCommandReceiptCapacity = 32,
                ConfigureModelProviders = registry => registry.RegisterOrReplace(provider.Descriptor, () => provider),
            },
            CancellationToken.None);

    // A provider whose sessions never run a turn: the tests only create and change sessions.
    private sealed class IdleProvider : IAgentModelProviderRuntime, IModelProviderTurnExecutor
    {
        public ModelProviderDescriptor Descriptor { get; } = new(new ModelProviderId("fake-labels"), "Fake Labels") { DefaultModelId = "fake-model" };

        public ModelProviderRuntimeDescriptor RuntimeDescriptor { get; } = new()
        {
            ProtocolFamily = "test", ProviderKey = "fake-labels", DisplayName = "Fake Labels", TransportKind = AgentTransportKind.OpenAIResponses,
        };

        public IModelProviderModelCatalog? ModelCatalog => null;

        public AgentRuntimeProviderRegistration CreateProviderRegistration() => new() { Provider = RuntimeDescriptor, TurnExecutor = this };

        public IModelProviderTurnExecutor CreateTurnExecutor() => this;

        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<ModelProviderProbeResult> ProbeAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new ModelProviderProbeResult
            {
                ProviderId = Descriptor.ProviderId,
                Availability = ModelProviderAvailability.Ready,
                Models = [new AgentModelInfo("fake-model", DisplayName: "Fake Model")],
                SelectedModelId = "fake-model",
            });

        public Task<AgentTurnResponse> ExecuteTurnAsync(AgentTurnRequest request, Func<AgentTurnDelta, CancellationToken, ValueTask> onUpdate, CancellationToken cancellationToken = default)
            => Task.FromResult(new AgentTurnResponse
            {
                AssistantMessage = new AgentConversationMessage(AgentConversationRole.Assistant, [new AgentMessagePart.Text("Done.")]),
            });

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TempDirectory : IDisposable
    {
        private TempDirectory(string root)
        {
            Root = root;
            GlobalRoot = Directory.CreateDirectory(Path.Combine(root, "global")).FullName;
            ProjectRoot = Directory.CreateDirectory(Path.Combine(root, "project")).FullName;
        }

        public string Root { get; }

        public string GlobalRoot { get; }

        public string ProjectRoot { get; }

        public static TempDirectory Create() => new(Path.Combine(Path.GetTempPath(), $"CodeAlta.SessionLabels.{Guid.NewGuid():N}"));

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A folder still held by a process that is ending is left to the cleanup of the temporary folder.
            }
        }
    }
}
