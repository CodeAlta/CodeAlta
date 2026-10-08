using System.Collections.Frozen;
using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Catalog;
using CodeAlta.Desktop.Rpc;
using CodeAlta.Orchestration.Hosting;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Desktop.Tests;

[TestClass]
public sealed class WorkspaceDeleteSessionTests
{
    private const string Epoch = "11111111-1111-4111-8111-111111111111";

    [TestMethod]
    public async Task ExactConfirmedDeletionRemovesOnlyItsJournalAndRefreshesCatalog()
    {
        var root = Path.Combine(Path.GetTempPath(), "codealta-session-delete-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var global = Path.Combine(root, "global"); var projectPath = Path.Combine(root, "project");
            var otherPath = Path.Combine(root, "other"); var home = Path.Combine(root, "home");
            var builtin = Path.Combine(root, "builtin");
            foreach (var path in new[] { global, projectPath, otherPath, home, builtin }) Directory.CreateDirectory(path);
            var projectMarker = Path.Combine(projectPath, "keep.txt");
            await File.WriteAllTextAsync(projectMarker, "not a session artifact");
            await using var host = await CodeAltaHost.CreateAsync(new CodeAltaHostOptions
            {
                GlobalRoot = global, CurrentProjectPath = projectPath, DiscoveryScope = new(home, root), BuiltInSkillRoot = builtin,
                PluginEnvironment = FrozenDictionary<string, string?>.Empty, StartPlugins = false, IsHeadless = true, OwnsLogging = false,
            });
            var project = await host.ProjectCatalog.UpsertFromPathAsync(projectPath);
            var other = await host.ProjectCatalog.UpsertFromPathAsync(otherPath);
            var store = host.SessionViewCatalog.JournalStore.CreateSessionStore();
            var created = DateTimeOffset.UtcNow;
            Task Add(string id, string path, string title, string? parent = null) => store.UpsertSessionAsync(new AgentSessionSummary
            {
                SessionId = id, ProviderId = new("delete-fixture"), ProtocolFamily = "delete-fixture", ProviderKey = "delete-fixture",
                WorkingDirectory = path, Title = title, ParentSessionId = parent, CreatedAt = created, UpdatedAt = created,
            });
            await Add("project-parent", projectPath, "Project parent");
            await Add("project-child", projectPath, "Project child", "project-parent");
            await Add("other-session", otherPath, "Other session");
            await Add("global-session", global, "Global session");
            var rpc = new WorkspaceService(host, Epoch);
            WorkspaceDeleteSessionRequest Request(string id, string? projectId, string path, string title) =>
                new(Epoch, projectId is null ? "global" : "project", projectId, path, id, title);
            var parent = Request("project-parent", project.Id, projectPath, "Project parent");
            Assert.AreEqual("stale_epoch", (await rpc.DeleteSessionAsync(parent with { ExpectedHostEpoch = Guid.NewGuid().ToString("D") }, CancellationToken.None)).Status);
            Assert.AreEqual("invalid_scope", (await rpc.DeleteSessionAsync(parent with { ConfirmedTitle = "" }, CancellationToken.None)).Status);
            Assert.AreEqual("scope_missing", (await rpc.DeleteSessionAsync(parent with { ProjectPath = otherPath }, CancellationToken.None)).Status);
            Assert.AreEqual("session_missing", (await rpc.DeleteSessionAsync(parent with { ConfirmedTitle = "Project child" }, CancellationToken.None)).Status);
            Assert.AreEqual("session_missing", (await rpc.DeleteSessionAsync(Request("other-session", project.Id, projectPath, "Other session"), CancellationToken.None)).Status);
            Assert.AreEqual("session_missing", (await rpc.DeleteSessionAsync(Request("global-session", project.Id, projectPath, "Global session"), CancellationToken.None)).Status);
            Assert.AreEqual("has_children", (await rpc.DeleteSessionAsync(parent, CancellationToken.None)).Status);
            Assert.AreEqual(parent, JsonSerializer.Deserialize(JsonSerializer.Serialize(parent, DesktopJsonContext.Default.WorkspaceDeleteSessionRequest),
                DesktopJsonContext.Default.WorkspaceDeleteSessionRequest));
            var child = await rpc.DeleteSessionAsync(Request("project-child", project.Id, projectPath, "Project child"), CancellationToken.None);
            Assert.AreEqual("ok", child.Status);
            Assert.AreEqual(child, JsonSerializer.Deserialize(JsonSerializer.Serialize(child, DesktopJsonContext.Default.WorkspaceDeleteSessionResponse),
                DesktopJsonContext.Default.WorkspaceDeleteSessionResponse));
            Assert.AreEqual("ok", (await rpc.DeleteSessionAsync(parent, CancellationToken.None)).Status);
            var fresh = await new WorkspaceService(global).SnapshotAsync(new(), CancellationToken.None);
            Assert.IsFalse(fresh.Sessions.Any(session => session.Id is "project-child" or "project-parent"));
            Assert.AreEqual("Other session", fresh.Sessions.Single(session => session.Id == "other-session").Title);
            Assert.AreEqual("Global session", fresh.Sessions.Single(session => session.Id == "global-session").Title);
            Assert.AreEqual("ok", (await rpc.DeleteSessionAsync(Request("global-session", null, global, "Global session"), CancellationToken.None)).Status);
            Assert.IsTrue(File.Exists(projectMarker));
            Assert.AreEqual("not a session artifact", await File.ReadAllTextAsync(projectMarker));
            Assert.IsTrue(Directory.Exists(projectPath));
            Assert.IsNotNull(await host.ProjectCatalog.GetByIdAsync(project.Id));
            Assert.IsNotNull(await host.ProjectCatalog.GetByIdAsync(other.Id));
            await rpc.CloseSessionsAsync();
            await rpc.CloseImportsAsync();
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public async Task CancelledWaitAndFailedAdmittedRemovalNeverRetryAndDrainBeforeClose()
    {
        var root = Path.Combine(Path.GetTempPath(), "codealta-session-delete-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var global = Path.Combine(root, "global"); Directory.CreateDirectory(global);
            var catalog = new ProjectCatalog(new CatalogOptions { GlobalRoot = global });
            await using var reads = new OwnedSessionWorkspace(catalog, new SessionViewJournalStore(catalog.Options));
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var count = 0;
            Func<string, string?, string, string, Task<string>> failingDelete =
                async (string _, string? _, string _, string _) =>
                {
                    count++; entered.TrySetResult(); await release.Task; throw new IOException("Journal might already be gone");
                };
            var rpc = new WorkspaceService(reads, catalog, Epoch, failingDelete);
            var request = new WorkspaceDeleteSessionRequest(Epoch, "global", null, global, "exact", "Exact");
            using var cancel = new CancellationTokenSource();
            var pending = rpc.DeleteSessionAsync(request, cancel.Token);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.AreEqual("busy", (await rpc.DeleteSessionAsync(request, CancellationToken.None)).Status);
            cancel.Cancel();
            await Assert.ThrowsExactlyAsync<TaskCanceledException>(async () => await pending);
            var closing = rpc.CloseSessionsAsync();
            Assert.IsFalse(closing.IsCompleted);
            release.TrySetResult();
            await closing;
            Assert.AreEqual(1, count);
            Assert.AreEqual("closed", (await rpc.DeleteSessionAsync(request, CancellationToken.None)).Status);
            await rpc.CloseImportsAsync();
            Func<string, string?, string, string, Task<string>> uncertainDelete = (_, _, _, _) =>
                throw new IOException("Post-removal cache failure");
            var uncertain = new WorkspaceService(reads, catalog, Epoch, uncertainDelete);
            Assert.AreEqual("delete_unconfirmed", (await uncertain.DeleteSessionAsync(request, CancellationToken.None)).Status);
            await uncertain.CloseSessionsAsync();
            await uncertain.CloseImportsAsync();
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public async Task NeverNamedSession_IsDeletedWithTheTitleTheListShows()
    {
        var root = Path.Combine(Path.GetTempPath(), "codealta-session-delete-" + Guid.NewGuid().ToString("N"));
        var global = Directory.CreateDirectory(Path.Combine(root, "global")).FullName;
        var projectPath = Directory.CreateDirectory(Path.Combine(root, "project")).FullName;
        try
        {
            var provider = new AnsweringProvider();
            await using var host = await CodeAltaHost.CreateAsync(new CodeAltaHostOptions
            {
                GlobalRoot = global, CurrentProjectPath = projectPath, IsHeadless = true, AutoApproveOwnedPermissions = true,
                OwnedCommandReceiptCapacity = 32, OwnsLogging = false,
                ConfigureModelProviders = registry => registry.RegisterOrReplace(provider.Descriptor, () => provider),
            });
            var project = host.CurrentProject;
            var sessionId = (await host.Commands.CreateDraftSessionAsync(project, provider.Descriptor, null)).SessionId;
            var receipt = host.Commands.AdmitSend(new(Guid.NewGuid().ToString("N"), sessionId, "one")).Receipt;
            Assert.IsNotNull(receipt);
            Assert.AreEqual(OwnedSessionCommandOutcome.Completed, (await receipt.Completion.WaitAsync(TimeSpan.FromSeconds(10))).Outcome);

            // The session was never named: the list shows the first line of what it last said, 80 characters at most.
            var rpc = new WorkspaceService(host, Epoch);
            var listed = (await rpc.SnapshotAsync(new(), CancellationToken.None)).Sessions.Single(session => session.Id == sessionId);
            Assert.AreEqual(AnsweringProvider.FirstLine[..79], listed.Title);
            Assert.AreEqual(listed.Title, listed.FullTitle);

            // The title it was created with is not the one the list shows; the one the list shows deletes it.
            WorkspaceDeleteSessionRequest Request(string title) => new(Epoch, "project", project.Id, project.ProjectPath, sessionId, title);
            Assert.AreEqual("session_missing", (await rpc.DeleteSessionAsync(Request(project.DisplayName), CancellationToken.None)).Status);
            Assert.AreEqual("ok", (await rpc.DeleteSessionAsync(Request(listed.Title), CancellationToken.None)).Status);
            Assert.IsFalse((await rpc.SnapshotAsync(new(), CancellationToken.None)).Sessions.Any(session => session.Id == sessionId));
            await rpc.CloseSessionsAsync();
            await rpc.CloseImportsAsync();
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    // A model that answers every prompt with a first line of more than 80 characters, whose 80th is a space.
    private sealed class AnsweringProvider : IAgentModelProviderRuntime, IModelProviderTurnExecutor
    {
        public static readonly string FirstLine = string.Concat(Enumerable.Repeat("word ", 30)).TrimEnd();

        public ModelProviderDescriptor Descriptor { get; } = new(new ModelProviderId("delete-answers"), "Delete Answers") { DefaultModelId = "fake-model" };

        public ModelProviderRuntimeDescriptor RuntimeDescriptor { get; } = new()
        {
            ProtocolFamily = "test", ProviderKey = "delete-answers", DisplayName = "Delete Answers", TransportKind = AgentTransportKind.OpenAIResponses,
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
                AssistantMessage = new AgentConversationMessage(AgentConversationRole.Assistant, [new AgentMessagePart.Text(FirstLine + "\nA second line.")]),
            });

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
