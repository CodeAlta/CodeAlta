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
public sealed class WorkspaceMoveSessionTests
{
    private const string Epoch = "11111111-1111-4111-8111-111111111111";
    private static readonly ModelProviderDescriptor Provider = new(new("move-fixture"), "Move fixture") { IsDefault = true };

    [TestMethod]
    public async Task OwnedMoveTakesAnAttachedSessionToTheOtherProject_AndAFreshReadListsItThere()
    {
        var root = Path.Combine(Path.GetTempPath(), "codealta-session-move-" + Guid.NewGuid().ToString("N"));
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
            // Draft creation attaches the session: the move retires that attachment.
            var named = await host.Commands.CreateDraftSessionAsync(project, Provider, "Named");
            var unnamed = await host.Commands.CreateDraftSessionAsync(project, Provider, null);
            var rpc = new WorkspaceService(host, Epoch);
            WorkspaceMoveSessionRequest Request(string id) => new(Epoch, id, project.Id, projectPath, other.Id, otherPath);

            Assert.AreEqual("stale_epoch", (await rpc.MoveSessionAsync(Request(named.SessionId) with { ExpectedHostEpoch = Guid.NewGuid().ToString("D") },
                CancellationToken.None)).Status);
            Assert.AreEqual("invalid_scope", (await rpc.MoveSessionAsync(Request(named.SessionId) with { TargetProjectPath = "" }, CancellationToken.None)).Status);
            Assert.AreEqual("scope_missing", (await rpc.MoveSessionAsync(Request(named.SessionId) with { ProjectPath = otherPath }, CancellationToken.None)).Status);
            Assert.AreEqual("same_project", (await rpc.MoveSessionAsync(Request(named.SessionId) with { TargetProjectId = project.Id, TargetProjectPath = projectPath },
                CancellationToken.None)).Status);

            var moved = await rpc.MoveSessionAsync(Request(named.SessionId), CancellationToken.None);
            Assert.AreEqual("ok", moved.Status, string.Join(" | ", moved.Warnings));
            Assert.AreEqual(new WorkspaceMovedSession(named.SessionId, other.Id, otherPath, null), moved.Moved.Single());
            Assert.AreEqual(moved.Status, JsonSerializer.Deserialize(JsonSerializer.Serialize(moved, DesktopJsonContext.Default.WorkspaceMoveSessionResponse),
                DesktopJsonContext.Default.WorkspaceMoveSessionResponse)!.Status);
            Assert.AreEqual("ok", (await rpc.MoveSessionAsync(Request(unnamed.SessionId), CancellationToken.None)).Status);

            var fresh = await new WorkspaceService(global).SnapshotAsync(new(), CancellationToken.None);
            var listed = fresh.Sessions.Single(session => session.Id == named.SessionId);
            Assert.AreEqual(other.Id, listed.ProjectId);
            Assert.AreEqual(otherPath, listed.WorkspacePath);
            Assert.AreEqual("project", listed.ScopeKind, "The header and the summary agree: the session is listed in the scope of its new project.");
            Assert.AreEqual("Named", listed.Title);
            Assert.AreEqual($"Project session for {other.DisplayName}.", fresh.Sessions.Single(session => session.Id == unnamed.SessionId).Title,
                "A session that was never named is listed by the summary it was created with: that of its new project.");
            Assert.AreEqual(otherPath, (await host.RuntimeService.ResolveOwnedSessionAsync(named.SessionId, CancellationToken.None))?.WorkingDirectory);
            await rpc.CloseSessionsAsync();
            await rpc.CloseImportsAsync();
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public async Task AdmittedMoveIsDrainedAfterCancelledWait_AndFailureIsUncertain()
    {
        var root = Path.Combine(Path.GetTempPath(), "codealta-session-move-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var global = Path.Combine(root, "global"); Directory.CreateDirectory(global);
            var catalog = new ProjectCatalog(new CatalogOptions { GlobalRoot = global });
            var source = await catalog.UpsertFromPathAsync(Directory.CreateDirectory(Path.Combine(root, "source")).FullName);
            await using var reads = new OwnedSessionWorkspace(catalog, new SessionViewJournalStore(catalog.Options));
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var count = 0;
            Func<string, string, string, string, string, Task<OwnedSessionMoveResult>> failMove = async (_, _, _, _, _) =>
            {
                count++; entered.TrySetResult(); await release.Task; throw new IOException("Result lost after the commit");
            };
            var rpc = new WorkspaceService(reads, catalog, Epoch, failMove);
            var request = new WorkspaceMoveSessionRequest(Epoch, "exact", source.Id, source.ProjectPath, "target", Path.Combine(root, "target"));
            using var cancel = new CancellationTokenSource();
            var pending = rpc.MoveSessionAsync(request, cancel.Token);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.AreEqual("busy", (await rpc.MoveSessionAsync(request, CancellationToken.None)).Status);
            cancel.Cancel();
            await Assert.ThrowsExactlyAsync<TaskCanceledException>(async () => await pending);
            var closing = rpc.CloseSessionsAsync();
            Assert.IsFalse(closing.IsCompleted);
            release.SetResult(true);
            await closing;
            Assert.AreEqual(1, count);
            Assert.AreEqual("closed", (await rpc.MoveSessionAsync(request, CancellationToken.None)).Status);
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
