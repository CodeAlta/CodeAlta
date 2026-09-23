using System.Collections.Frozen;
using System.Runtime.CompilerServices;
using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Catalog;
using CodeAlta.Desktop.Rpc;
using CodeAlta.Orchestration.Hosting;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Desktop.Tests;

[TestClass]
public sealed class WorkspaceRenameSessionTests
{
    private const string Epoch = "11111111-1111-4111-8111-111111111111";
    private static readonly ModelProviderDescriptor Provider = new(new("rename-fixture"), "Rename fixture") { IsDefault = true };

    [TestMethod]
    public async Task OwnedRenamePreservesExactCatalogIdentityAndPersistsAcrossFreshRead()
    {
        var root = Path.Combine(Path.GetTempPath(), "codealta-session-rename-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var global = Path.Combine(root, "global"); var projectPath = Path.Combine(root, "project");
            var otherPath = Path.Combine(root, "other"); var home = Path.Combine(root, "home");
            var builtin = Path.Combine(root, "builtin");
            foreach (var path in new[] { global, projectPath, otherPath, home, builtin }) Directory.CreateDirectory(path);
            await using var host = await CodeAltaHost.CreateAsync(new CodeAltaHostOptions
            {
                GlobalRoot = global, CurrentProjectPath = projectPath, DiscoveryScope = new(home, root), BuiltInSkillRoot = builtin,
                PluginEnvironment = FrozenDictionary<string, string?>.Empty, StartPlugins = false, IsHeadless = true, OwnsLogging = false,
                ConfigureModelProviders = registry => registry.RegisterOrReplace(Provider, () => new DraftRuntime()),
            });
            var project = await host.ProjectCatalog.UpsertFromPathAsync(projectPath);
            var other = await host.ProjectCatalog.UpsertFromPathAsync(otherPath);
            // Draft creation persists the real provider-independent journal without starting a run.
            var first = await host.Commands.CreateDraftSessionAsync(project, Provider, "First");
            var second = await host.Commands.CreateDraftSessionAsync(project, Provider, "Second");
            var globalSession = await host.Commands.CreateDraftSessionAsync(null, Provider, "Global");
            var rpc = new WorkspaceService(host, Epoch);
            await Assert.ThrowsExactlyAsync<ArgumentException>(async () =>
                await host.Commands.RenameSessionAsync(first.SessionId, project.Id, projectPath, " padded"));
            WorkspaceRenameSessionRequest Request(string id, string? projectId, string path, string title) =>
                new(Epoch, projectId is null ? "global" : "project", projectId, path, id, title);
            var request = Request(first.SessionId, project.Id, projectPath, "Updated title");
            Assert.AreEqual("session_missing", (await rpc.RenameSessionAsync(Request(second.SessionId, other.Id, otherPath, "Wrong"), CancellationToken.None)).Status);
            Assert.AreEqual("session_missing", (await rpc.RenameSessionAsync(Request(globalSession.SessionId, project.Id, projectPath, "Wrong"), CancellationToken.None)).Status);
            Assert.AreEqual("scope_missing", (await rpc.RenameSessionAsync(Request(first.SessionId, project.Id, otherPath, "Wrong"), CancellationToken.None)).Status);
            Assert.AreEqual("stale_epoch", (await rpc.RenameSessionAsync(request with { ExpectedHostEpoch = Guid.NewGuid().ToString("D") }, CancellationToken.None)).Status);
            foreach (var title in new[] { "", " ", " padded", "line\nbreak", "\ud800", new string('x', 257) })
                Assert.AreEqual("invalid_scope", (await rpc.RenameSessionAsync(request with { Title = title }, CancellationToken.None)).Status);
            var runtimeBefore = await host.RuntimeService.GetCurrentStateAsync(first.SessionId);
            var renamed = await rpc.RenameSessionAsync(request, CancellationToken.None);
            Assert.AreEqual("ok", renamed.Status);
            Assert.AreEqual(runtimeBefore, await host.RuntimeService.GetCurrentStateAsync(first.SessionId));
            Assert.AreEqual("Updated title", (await host.RuntimeService.TryGetActiveSessionDescriptorAsync(first.SessionId))?.Title);
            Assert.AreEqual(renamed, JsonSerializer.Deserialize(JsonSerializer.Serialize(renamed, DesktopJsonContext.Default.WorkspaceRenameSessionResponse),
                DesktopJsonContext.Default.WorkspaceRenameSessionResponse));
            Assert.AreEqual(request, JsonSerializer.Deserialize(JsonSerializer.Serialize(request, DesktopJsonContext.Default.WorkspaceRenameSessionRequest),
                DesktopJsonContext.Default.WorkspaceRenameSessionRequest));
            Assert.AreEqual("ok", (await rpc.RenameSessionAsync(Request(globalSession.SessionId, null, global, "Global updated"), CancellationToken.None)).Status);
            var fresh = await new WorkspaceService(global).SnapshotAsync(new(), CancellationToken.None);
            Assert.AreEqual("Updated title", fresh.Sessions.Single(s => s.Id == first.SessionId).Title);
            Assert.AreEqual("Second", fresh.Sessions.Single(s => s.Id == second.SessionId).Title);
            Assert.AreEqual("Global updated", fresh.Sessions.Single(s => s.Id == globalSession.SessionId).Title);
            Assert.AreEqual(first.SessionId, (await host.RuntimeService.ResolveOwnedSessionAsync(first.SessionId, CancellationToken.None))?.SessionId);
            await rpc.CloseSessionsAsync();
            await rpc.CloseImportsAsync();
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public async Task AdmittedWorkIsDrainedAfterCancelledWaitAndFailureIsUncertain()
    {
        var root = Path.Combine(Path.GetTempPath(), "codealta-session-rename-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var global = Path.Combine(root, "global"); Directory.CreateDirectory(global);
            var catalog = new ProjectCatalog(new CatalogOptions { GlobalRoot = global });
            await using var reads = new OwnedSessionWorkspace(catalog, new SessionViewJournalStore(catalog.Options));
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var count = 0;
            Func<string, string?, string, string, Task<bool>> failRename = async (_, _, _, _) =>
            {
                count++; entered.TrySetResult(); await release.Task; throw new IOException("Result lost after append");
            };
            var rpc = new WorkspaceService(reads, catalog, Epoch, failRename);
            var request = new WorkspaceRenameSessionRequest(Epoch, "global", null, global, "exact", "New");
            using var cancel = new CancellationTokenSource();
            var pending = rpc.RenameSessionAsync(request, cancel.Token);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.AreEqual("busy", (await rpc.RenameSessionAsync(request, CancellationToken.None)).Status);
            cancel.Cancel();
            await Assert.ThrowsExactlyAsync<TaskCanceledException>(async () => await pending);
            var closing = rpc.CloseSessionsAsync();
            Assert.IsFalse(closing.IsCompleted);
            release.SetResult(true);
            await closing;
            Assert.AreEqual(1, count);
            Assert.AreEqual("closed", (await rpc.RenameSessionAsync(request, CancellationToken.None)).Status);
            Func<string, string?, string, string, Task<bool>> uncertainRename = (_, _, _, _) =>
                throw new IOException("Append may have committed");
            var uncertain = new WorkspaceService(reads, catalog, Epoch, uncertainRename);
            Assert.AreEqual("rename_unconfirmed", (await uncertain.RenameSessionAsync(request, CancellationToken.None)).Status);
            await uncertain.CloseSessionsAsync();
            await rpc.CloseImportsAsync();
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private sealed class DraftRuntime : IModelProviderSessionRuntime
    {
        public ModelProviderDescriptor Descriptor => Provider;
        public Task StartAsync(CancellationToken token = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken token = default) => Task.CompletedTask;
        public Task<ModelProviderProbeResult> ProbeAsync(CancellationToken token = default) => throw new NotSupportedException();
        public IModelProviderTurnExecutor CreateTurnExecutor() => throw new NotSupportedException();
        public Task<IAgentSession> CreateSessionAsync(AgentSessionCreateOptions options, CancellationToken token = default)
            => Task.FromResult<IAgentSession>(new DraftSession(options.SessionId!, options.WorkingDirectory));
        public Task<IAgentSession> ResumeSessionAsync(string id, AgentSessionResumeOptions options, CancellationToken token = default)
            => Task.FromResult<IAgentSession>(new DraftSession(id, options.WorkingDirectory));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class DraftSession(string id, string? path) : IAgentSession
    {
        public ModelProviderId ProviderId => Provider.ProviderId;
        public string SessionId => id;
        public string? WorkspacePath => path;
        public async IAsyncEnumerable<AgentEvent> StreamEventsAsync([EnumeratorCancellation] CancellationToken token = default)
        { await Task.CompletedTask; yield break; }
        public IDisposable Subscribe(Action<AgentEvent> handler) => new Subscription();
        public Task<AgentRunId> SendAsync(AgentSendOptions options, CancellationToken token = default) => throw new NotSupportedException();
        public Task AbortAsync(CancellationToken token = default) => Task.CompletedTask;
        public Task<AgentRunId> SteerAsync(AgentSteerOptions options, CancellationToken token = default) => throw new NotSupportedException();
        public Task CompactAsync(CancellationToken token = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<AgentEvent>> GetHistoryAsync(CancellationToken token = default) => Task.FromResult<IReadOnlyList<AgentEvent>>([]);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        private sealed class Subscription : IDisposable { public void Dispose() { } }
    }
}
