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
            Assert.AreEqual(other.DisplayName, fresh.Sessions.Single(session => session.Id == unnamed.SessionId).Title,
                "A session that still has the title it was created with is listed by that title: the name of its new project.");
            Assert.AreEqual(otherPath, (await host.RuntimeService.ResolveOwnedSessionAsync(named.SessionId, CancellationToken.None))?.WorkingDirectory);
            await rpc.CloseSessionsAsync();
            await rpc.CloseImportsAsync();
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public async Task MovedSessionWithRemoteControl_IsAttachedAgainInItsNewFolder_WithRemoteControlOn()
    {
        var root = Path.Combine(Path.GetTempPath(), "codealta-session-move-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var global = Path.Combine(root, "global"); var projectPath = Path.Combine(root, "project");
            var otherPath = Path.Combine(root, "other"); var home = Path.Combine(root, "home");
            var builtin = Path.Combine(root, "builtin");
            foreach (var path in new[] { global, projectPath, otherPath, home, builtin }) Directory.CreateDirectory(path);
            var remote = new RemoteRecorder();
            await using var host = await CodeAltaHost.CreateAsync(new CodeAltaHostOptions
            {
                GlobalRoot = global, CurrentProjectPath = projectPath, DiscoveryScope = new(home, root), BuiltInSkillRoot = builtin,
                PluginEnvironment = FrozenDictionary<string, string?>.Empty, StartPlugins = false, IsHeadless = true, OwnsLogging = false,
                ConfigureModelProviders = registry => registry.RegisterOrReplace(RemoteProvider, () => new DraftRuntime(RemoteProvider, remote)),
            });
            var project = await host.ProjectCatalog.UpsertFromPathAsync(projectPath);
            var other = await host.ProjectCatalog.UpsertFromPathAsync(otherPath);
            var session = await host.Commands.CreateDraftSessionAsync(project, RemoteProvider, "Remote");
            var on = await host.Commands.SetRemoteControlAsync(session.SessionId, true);
            Assert.AreEqual("ok", on.Status);
            Assert.AreEqual((projectPath, true), remote.Requests.Single());

            var rpc = new WorkspaceService(host, Epoch);
            var moved = await rpc.MoveSessionAsync(new(Epoch, session.SessionId, project.Id, projectPath, other.Id, otherPath), CancellationToken.None);
            Assert.AreEqual("ok", moved.Status, string.Join(" | ", moved.Warnings));

            // Nobody waits for the reconnection: it comes after the answer, from an attachment in the new folder.
            await remote.Reconnected.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.AreEqual((otherPath, true), remote.Requests[^1], "The session is attached again where it was moved, and its remote control turned on there.");
            Assert.AreEqual(otherPath, (await host.RuntimeService.ResolveOwnedSessionAsync(session.SessionId, CancellationToken.None))?.WorkingDirectory);
            await rpc.CloseSessionsAsync();
            await rpc.CloseImportsAsync();
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    [DataRow(false, DisplayName = "The provider stops cleanly")]
    [DataRow(true, DisplayName = "The provider fails to stop")]
    public async Task TurnEndedByTheMove_ThatWritesTheOldSummaryBack_IsWrittenOverWithTheMovedOne(bool failStop)
    {
        var root = Path.Combine(Path.GetTempPath(), "codealta-session-move-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var global = Path.Combine(root, "global"); var projectPath = Path.Combine(root, "project");
            var otherPath = Path.Combine(root, "other"); var home = Path.Combine(root, "home");
            var builtin = Path.Combine(root, "builtin");
            foreach (var path in new[] { global, projectPath, otherPath, home, builtin }) Directory.CreateDirectory(path);
            CodeAltaHost? owner = null;
            CodeAlta.Agent.Runtime.AgentSessionSummary? held = null;
            var written = 0;
            // The attachment the move retires ends a turn as it goes (one started from the phone, with Remote Control
            // on): the summary that session held from before the move (old folder, old title, old preview) is
            // written then, and its provider may then fail to stop.
            async Task EndTurn(string id, string? path)
            {
                if (path != projectPath || Interlocked.Increment(ref written) != 1) return;
                var store = owner!.SessionViewCatalog.JournalStore.CreateSessionStore();
                await store.UpsertSessionAsync(held! with { UpdatedAt = DateTimeOffset.UtcNow }, CancellationToken.None);
                // Another reader lists the sessions meanwhile (another space, an agent): the catalog keeps what it read.
                await foreach (var _ in owner.AgentSessionCatalog.ListSessionsAsync()) { }
                if (failStop) throw new IOException("The process did not exit.");
            }

            var host = await CodeAltaHost.CreateAsync(new CodeAltaHostOptions
            {
                GlobalRoot = global, CurrentProjectPath = projectPath, DiscoveryScope = new(home, root), BuiltInSkillRoot = builtin,
                PluginEnvironment = FrozenDictionary<string, string?>.Empty, StartPlugins = false, IsHeadless = true, OwnsLogging = false,
                ConfigureModelProviders = registry => registry.RegisterOrReplace(Provider, () => new DraftRuntime(Provider, onDispose: EndTurn)),
            });
            owner = host;
            try
            {
                var project = await host.ProjectCatalog.UpsertFromPathAsync(projectPath);
                var other = await host.ProjectCatalog.UpsertFromPathAsync(otherPath);
                // Never named: the move names it after its new project.
                var session = await host.Commands.CreateDraftSessionAsync(project, Provider, null);
                var store = host.SessionViewCatalog.JournalStore.CreateSessionStore();
                held = await store.GetSessionSummaryAsync(session.SessionId, CancellationToken.None);
                var rpc = new WorkspaceService(host, Epoch);

                var moved = await rpc.MoveSessionAsync(new(Epoch, session.SessionId, project.Id, projectPath, other.Id, otherPath), CancellationToken.None);
                Assert.AreEqual("ok", moved.Status, string.Join(" | ", moved.Warnings));
                Assert.AreEqual(1, written, "The retired attachment wrote its summary.");
                Assert.AreEqual(failStop, moved.Warnings.Any(warning => warning.Contains("did not stop cleanly", StringComparison.Ordinal)), string.Join(" | ", moved.Warnings));
                var summary = (await store.GetSessionSummaryAsync(session.SessionId, CancellationToken.None))!;
                Assert.AreEqual(otherPath, summary.WorkingDirectory);
                Assert.AreEqual(other.DisplayName, summary.Title, "The name the move gave is written again over the old one.");
                Assert.AreEqual($"Project session for {other.DisplayName}.", summary.Summary);
                var listed = (await new WorkspaceService(global).SnapshotAsync(new(), CancellationToken.None)).Sessions.Single(value => value.Id == session.SessionId);
                Assert.AreEqual("project", listed.ScopeKind, "The summary and the header agree again: the session is listed in its new project.");
                Assert.AreEqual(other.Id, listed.ProjectId);
                Assert.AreEqual(other.DisplayName, listed.Title);
                // The catalog was told: it shows the session where it went, not where the ended turn put it.
                AgentSessionMetadata? cataloged = null;
                await foreach (var value in host.AgentSessionCatalog.ListSessionsAsync()) if (value.SessionId == session.SessionId) cataloged = value;
                Assert.AreEqual(otherPath, cataloged?.WorkspacePath);
                // A provider that did not stop leaves the runtime closed to new work: only the stored state is asked then.
                if (failStop) return;
                Assert.AreEqual(otherPath, (await host.RuntimeService.ResolveOwnedSessionAsync(session.SessionId, CancellationToken.None))?.WorkingDirectory);
                await rpc.CloseSessionsAsync();
                await rpc.CloseImportsAsync();
            }
            finally
            {
                // A provider that did not stop stays retained, and the host reports it when it closes.
                try { await host.DisposeAsync(); }
                catch (AgentDependencyRetentionException) when (failStop) { }
            }
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

    private static readonly ModelProviderDescriptor RemoteProvider = new(new("move-remote-fixture"), "Move remote fixture")
    {
        IsDefault = true, SupportsRemoteControl = true,
    };

    // What the sessions of the fixture were asked about their remote control, with the folder each works in.
    private sealed class RemoteRecorder
    {
        private readonly Lock _gate = new();
        private readonly List<(string? Path, bool Enabled)> _requests = [];
        public TaskCompletionSource Reconnected { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<(string? Path, bool Enabled)> Requests { get { lock (_gate) return [.. _requests]; } }

        public void Add(string? path, bool enabled)
        {
            lock (_gate)
            {
                _requests.Add((path, enabled));
                if (_requests.Count > 1) Reconnected.TrySetResult();
            }
        }
    }

    private sealed class DraftRuntime(ModelProviderDescriptor descriptor, RemoteRecorder? remote = null, Func<string, string?, Task>? onDispose = null)
        : IModelProviderSessionRuntime
    {
        public DraftRuntime() : this(Provider) { }
        public ModelProviderDescriptor Descriptor => descriptor;
        public Task StartAsync(CancellationToken token = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken token = default) => Task.CompletedTask;
        public Task<ModelProviderProbeResult> ProbeAsync(CancellationToken token = default) => throw new NotSupportedException();
        public IModelProviderTurnExecutor CreateTurnExecutor() => throw new NotSupportedException();
        public Task<IAgentSession> CreateSessionAsync(AgentSessionCreateOptions options, CancellationToken token = default)
            => Task.FromResult<IAgentSession>(new DraftSession(descriptor, options.SessionId!, options.WorkingDirectory, remote, onDispose));
        public Task<IAgentSession> ResumeSessionAsync(string id, AgentSessionResumeOptions options, CancellationToken token = default)
            => Task.FromResult<IAgentSession>(new DraftSession(descriptor, id, options.WorkingDirectory, remote, onDispose));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class DraftSession(ModelProviderDescriptor descriptor, string id, string? path, RemoteRecorder? remote,
        Func<string, string?, Task>? onDispose) : IAgentSession, IAgentRemoteControlProvider
    {
        public bool SupportsRemoteControl => remote is not null;
        public AgentRemoteControl RemoteControl { get; private set; } = AgentRemoteControl.Off;

        public Task<AgentRemoteControl> SetRemoteControlAsync(bool enabled, string? name, CancellationToken cancellationToken = default)
        {
            if (remote is null) throw new NotSupportedException();
            remote.Add(path, enabled);
            RemoteControl = enabled ? new AgentRemoteControl(AgentRemoteControlStatus.Connected, "https://claude.ai/code/session_fixture") : AgentRemoteControl.Off;
            return Task.FromResult(RemoteControl);
        }

        public ModelProviderId ProviderId => descriptor.ProviderId;
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
        public async ValueTask DisposeAsync() { if (onDispose is not null) await onDispose(id, path); }
        private sealed class Subscription : IDisposable { public void Dispose() { } }
    }
}
