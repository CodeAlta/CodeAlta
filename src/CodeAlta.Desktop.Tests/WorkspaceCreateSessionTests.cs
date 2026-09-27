using System.Collections.Frozen;
using System.Runtime.CompilerServices;
using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Catalog;
using CodeAlta.Desktop.Rpc;
using CodeAlta.Orchestration.Hosting;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Desktop.Tests;

[TestClass]
public sealed class WorkspaceCreateSessionTests
{
    private const string Epoch = "11111111-1111-4111-8111-111111111111";
    private static readonly ModelProviderDescriptor Provider = new(new("desktop-draft-fixture"), "Draft fixture")
        { IsDefault = true, DefaultModelId = "fixture-model" };

    [TestMethod]
    public async Task ExplicitProviderIsExactEnabledAndNeverFallsBack()
    {
        var root = Path.Combine(Path.GetTempPath(), "codealta-provider-create-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var catalog = new ProjectCatalog(new CatalogOptions { GlobalRoot = root });
            await using var reads = new OwnedSessionWorkspace(catalog, new SessionViewJournalStore(catalog.Options));
            await using var providers = new ModelProviderRegistry();
            var alternate = new ModelProviderDescriptor(new("alternate"), "Alternate");
            providers.RegisterOrReplace(Provider, () => throw new AssertFailedException("Descriptor admission must not initialize providers."));
            providers.RegisterOrReplace(alternate, () => throw new AssertFailedException("Descriptor admission must not initialize providers."));
            var admitted = new List<ModelProviderDescriptor>();
            var service = new WorkspaceService(reads, catalog, Epoch, providers, (_, provider, _) =>
            {
                admitted.Add(provider);
                return Task.FromResult(new SessionViewDescriptor { SessionId = "created", Kind = SessionViewKind.GlobalSession,
                    WorkingDirectory = root });
            });
            // Deserialize the new optional wire field so the regression runs against the old implementation.
            WorkspaceCreateSessionRequest Request(string? id) => JsonSerializer.Deserialize(
                JsonSerializer.Serialize(new { expectedHostEpoch = Epoch, scope = "global", providerId = id }),
                DesktopJsonContext.Default.WorkspaceCreateSessionRequest)!;
            var result = await service.CreateSessionAsync(Request("alternate"), CancellationToken.None);
            Assert.AreEqual("ok", result.Status);
            Assert.AreSame(alternate, admitted.Single());
            foreach (var id in new[] { " alternate", "alternate ", "", "bad\nvalue", new string('x', 257) })
                Assert.AreEqual("invalid_scope", (await service.CreateSessionAsync(Request(id), CancellationToken.None)).Status, id);
            foreach (var id in new[] { "Alternate", "absent" })
                Assert.AreEqual("provider_unavailable", (await service.CreateSessionAsync(Request(id), CancellationToken.None)).Status, id);
            providers.RegisterOrReplace(alternate with { IsEnabled = false }, () => throw new AssertFailedException());
            Assert.AreEqual("provider_unavailable", (await service.CreateSessionAsync(Request("alternate"), CancellationToken.None)).Status);
            providers.Unregister(alternate.ProviderId);
            Assert.AreEqual("provider_unavailable", (await service.CreateSessionAsync(Request("alternate"), CancellationToken.None)).Status);
            Assert.HasCount(1, admitted);
            Assert.AreEqual("ok", (await service.CreateSessionAsync(Request(null), CancellationToken.None)).Status);
            Assert.AreSame(Provider, admitted[1]);
            providers.RegisterOrReplace(Provider with { IsEnabled = false }, () => throw new AssertFailedException());
            providers.RegisterOrReplace(alternate, () => throw new AssertFailedException());
            Assert.AreEqual("ok", (await service.CreateSessionAsync(Request(null), CancellationToken.None)).Status);
            Assert.AreSame(alternate, admitted[2], "Without an enabled default the first enabled descriptor remains the default route.");
            await service.CloseSessionsAsync(); await service.CloseImportsAsync();
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ArchiveAndCreationReserveSameOwnerThroughCanceledWaitAndDrain(bool archiveFirst)
    {
        var root = Path.Combine(Path.GetTempPath(), "codealta-archive-admission-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var catalog = new ProjectCatalog(new CatalogOptions { GlobalRoot = Path.Combine(root, "global") });
            var project = await catalog.UpsertFromPathAsync(root);
            await using var reads = new OwnedSessionWorkspace(catalog, new SessionViewJournalStore(catalog.Options));
            await using var providers = new ModelProviderRegistry();
            providers.RegisterOrReplace(Provider, () => new DraftRuntime());
            var calls = 0;
            var rpc = new WorkspaceService(reads, catalog, Epoch, providers, async (p, _, _) =>
            {
                calls++; entered.TrySetResult(); await release.Task;
                await catalog.EnsurePersistedAsync(p!);
                return new SessionViewDescriptor { SessionId = "created", Kind = SessionViewKind.ProjectSession,
                    ProjectRef = p!.Id, WorkingDirectory = p.ProjectPath };
            });
            rpc.ArchiveWriter = async (request, revision) =>
            {
                entered.TrySetResult(); await release.Task;
                return await catalog.SetArchivedAsync(request.ProjectId, request.ProjectPath, request.SourcePath!, revision,
                    request.ExpectedArchived, request.Archived);
            };
            var evidence = await catalog.ReadArchiveAsync(project.Id, root);
            Assert.IsNotNull(evidence, "Ordinary serializer-produced projects must support archive.");
            var archive = new WorkspaceArchiveProjectRequest(Epoch, project.Id, root, false, true, true, evidence.SourcePath, evidence.Revision.ContentHash);
            var create = new WorkspaceCreateSessionRequest(Epoch, "project", project.Id, root, null, Provider.ProviderId.Value);
            using var cancel = new CancellationTokenSource();
            Task original = archiveFirst ? rpc.ArchiveProjectAsync(archive, cancel.Token) : rpc.CreateSessionAsync(create, cancel.Token);
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                cancel.Cancel();
                try { await original; Assert.Fail("Canceled wait must stop waiting."); } catch (OperationCanceledException) { }
                Assert.AreEqual("busy", (await rpc.ArchiveProjectAsync(archive, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3))).Status);
                Assert.AreEqual("busy", (await rpc.CreateSessionAsync(create, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3))).Status);
                Assert.AreEqual("busy", (await rpc.OpenProjectAsync(new(Epoch, root, true), CancellationToken.None)).Status);
                Assert.AreEqual("busy", (await rpc.RenameProjectAsync(new(Epoch, project.Id, root, evidence.SourcePath,
                    evidence.Revision.ContentHash!, "Renamed"), CancellationToken.None)).Status);
                var drain = Task.WhenAll(rpc.CloseImportsAsync(), rpc.CloseSessionsAsync());
                Assert.IsFalse(drain.IsCompleted);
                release.TrySetResult();
                await drain.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.AreEqual(archiveFirst, (await catalog.GetByIdAsync(project.Id))!.Archived);
                Assert.AreEqual(archiveFirst ? 0 : 1, calls);
                if (archiveFirst)
                {
                    var fresh = new WorkspaceService(reads, catalog, Epoch, providers, (_, _, _) => throw new AssertFailedException("Archived create must refuse."));
                    Assert.AreEqual("project_missing", (await fresh.CreateSessionAsync(create, CancellationToken.None)).Status);
                    Assert.AreEqual("archived", (await fresh.OpenProjectAsync(new(Epoch, root, true), CancellationToken.None)).Status);
                    Assert.IsTrue((await catalog.GetByIdAsync(project.Id))!.Archived);
                    await fresh.CloseSessionsAsync(); await fresh.CloseImportsAsync();
                }
                Assert.AreEqual("closed", (await rpc.ArchiveProjectAsync(archive, CancellationToken.None)).Status);
                Assert.AreEqual("closed", (await rpc.CreateSessionAsync(create, CancellationToken.None)).Status);
            }
            finally { release.TrySetResult(); await rpc.CloseSessionsAsync(); await rpc.CloseImportsAsync(); }
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RealOwnedHostCreatesProjectAndGlobalSessionsVisibleInActualCatalog(bool explicitProvider)
    {
        var root = Path.Combine(Path.GetTempPath(), "codealta-session-create-" + Guid.NewGuid().ToString("N"));
        if (Directory.Exists(root)) throw new InvalidOperationException("Test root already exists.");
        Directory.CreateDirectory(root);
        try
        {
            var global = Path.Combine(root, "global"); var projectPath = Path.Combine(root, "project");
            var home = Path.Combine(root, "home"); var builtin = Path.Combine(root, "builtin");
            foreach (var path in new[] { global, projectPath, home, builtin }) Directory.CreateDirectory(path);
            var alternate = new ModelProviderDescriptor(new("alternate"), "Alternate");
            var activity = new DraftActivity();
            activity.ReleaseCreate.SetResult();
            await using var host = await CodeAltaHost.CreateAsync(new CodeAltaHostOptions
            {
                GlobalRoot = global, CurrentProjectPath = projectPath, DiscoveryScope = new(home, root), BuiltInSkillRoot = builtin,
                PluginEnvironment = FrozenDictionary<string, string?>.Empty, StartPlugins = false, IsHeadless = true, OwnsLogging = false,
                ConfigureModelProviders = registry => {
                    registry.RegisterOrReplace(Provider, () => new DraftRuntime());
                    registry.RegisterOrReplace(alternate, () => new DraftRuntime(activity, alternate));
                },
            });
            var project = await host.ProjectCatalog.UpsertFromPathAsync(projectPath);
            var service = new WorkspaceService(host, Epoch);
            var projectRequest = new WorkspaceCreateSessionRequest(Epoch, "project", project.Id, projectPath, "Project draft",
                explicitProvider ? alternate.ProviderId.Value : null);
            Assert.AreEqual("project_missing", (await service.CreateSessionAsync(projectRequest with { ProjectPath = global }, CancellationToken.None)).Status);
            Assert.AreEqual(projectRequest, JsonSerializer.Deserialize(
                JsonSerializer.Serialize(projectRequest, DesktopJsonContext.Default.WorkspaceCreateSessionRequest),
                DesktopJsonContext.Default.WorkspaceCreateSessionRequest));
            var created = await service.CreateSessionAsync(projectRequest, CancellationToken.None);
            Assert.AreEqual("ok", created.Status);
            Assert.AreEqual(projectRequest.ProviderId, created.ProviderId);
            Assert.AreEqual(explicitProvider ? 1 : 0, activity.Creates, "Explicit choice must reach the real runtime creation callback.");
            Assert.IsFalse(string.IsNullOrWhiteSpace(created.SessionId));
            Assert.AreEqual(projectPath, created.WorkspacePath);
            Assert.AreEqual(created, JsonSerializer.Deserialize(
                JsonSerializer.Serialize(created, DesktopJsonContext.Default.WorkspaceCreateSessionResponse),
                DesktopJsonContext.Default.WorkspaceCreateSessionResponse));
            var globalCreated = await service.CreateSessionAsync(new(Epoch, "global", null, null, null), CancellationToken.None);
            Assert.AreEqual("ok", globalCreated.Status);
            Assert.IsFalse(string.IsNullOrWhiteSpace(globalCreated.SessionId));
            Assert.AreNotEqual(created.SessionId, globalCreated.SessionId);
            Assert.AreEqual(global, globalCreated.WorkspacePath);
            var snapshot = await service.SnapshotAsync(new(), CancellationToken.None);
            Assert.IsTrue(snapshot.Sessions.Any(s => s.Id == created.SessionId && s.WorkspacePath == projectPath));
            Assert.AreEqual(explicitProvider ? alternate.ProviderId.Value : Provider.ProviderId.Value,
                snapshot.Sessions.Single(s => s.Id == created.SessionId).ProviderKey);
            Assert.IsTrue(snapshot.Sessions.Any(s => s.Id == globalCreated.SessionId && s.WorkspacePath == global));
            Assert.AreEqual(project.Id, (await host.RuntimeService.ResolveOwnedSessionAsync(created.SessionId!, CancellationToken.None))?.ProjectRef);
            Assert.AreEqual(SessionViewKind.GlobalSession,
                (await host.RuntimeService.ResolveOwnedSessionAsync(globalCreated.SessionId!, CancellationToken.None))?.Kind);
            Assert.HasCount(1, await host.ProjectCatalog.LoadAsync());
            await service.CloseSessionsAsync();
            await service.CloseImportsAsync();
            Assert.AreEqual("closed", (await service.CreateSessionAsync(projectRequest, CancellationToken.None)).Status);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task EffectfulDraftCreation_IsSingleFlightButSettledIdenticalRequestCreatesAgain(bool projectScope)
    {
        var root = Path.Combine(Path.GetTempPath(), "codealta-session-create-" + Guid.NewGuid().ToString("N"));
        if (Directory.Exists(root)) throw new InvalidOperationException("Test root already exists.");
        Directory.CreateDirectory(root);
        var activity = new DraftActivity();
        try
        {
            var global = Path.Combine(root, "global"); var projectPath = Path.Combine(root, "project");
            var home = Path.Combine(root, "home"); var builtin = Path.Combine(root, "builtin");
            foreach (var path in new[] { global, projectPath, home, builtin }) Directory.CreateDirectory(path);
            await using var host = await CodeAltaHost.CreateAsync(new CodeAltaHostOptions
            {
                GlobalRoot = global, CurrentProjectPath = projectPath, DiscoveryScope = new(home, root), BuiltInSkillRoot = builtin,
                PluginEnvironment = FrozenDictionary<string, string?>.Empty, StartPlugins = false, IsHeadless = true, OwnsLogging = false,
                ConfigureModelProviders = registry => registry.RegisterOrReplace(Provider, () => new DraftRuntime(activity)),
            });
            var project = projectScope ? await host.ProjectCatalog.UpsertFromPathAsync(projectPath) : null;
            var request = new WorkspaceCreateSessionRequest(Epoch, projectScope ? "project" : "global",
                project?.Id, project?.ProjectPath, "Effectful draft");
            var service = new WorkspaceService(host, Epoch);
            var startsBefore = Volatile.Read(ref activity.Starts);
            try
            {
                var original = service.CreateSessionAsync(request, CancellationToken.None);
                await activity.CreateEntered.Task.WaitAsync(TimeSpan.FromSeconds(30));
                Assert.IsFalse(original.IsCompleted);
                Assert.AreEqual(startsBefore + 1, Volatile.Read(ref activity.Starts));
                Assert.AreEqual(1, Volatile.Read(ref activity.Creates));
                Assert.AreEqual(0, Volatile.Read(ref activity.Sends));
                Assert.AreEqual("busy", (await service.CreateSessionAsync(request, CancellationToken.None)).Status);
                Assert.AreEqual(startsBefore + 1, Volatile.Read(ref activity.Starts));
                Assert.AreEqual(1, Volatile.Read(ref activity.Creates));

                activity.ReleaseCreate.TrySetResult();
                var first = await original.WaitAsync(TimeSpan.FromSeconds(30));
                Assert.AreEqual("ok", first.Status);
                Assert.IsNotNull(first.SessionId);
                var firstState = await host.RuntimeService.GetCurrentStateAsync(first.SessionId);
                Assert.IsNotNull(firstState.Entry);
                Assert.AreEqual(Provider.ProviderId.Value, firstState.Entry.ProviderId);

                // No receipt key exists: the identical settled request starts a new original.
                var second = await service.CreateSessionAsync(request, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));
                Assert.AreEqual("ok", second.Status);
                Assert.IsNotNull(second.SessionId);
                Assert.AreNotEqual(first.SessionId, second.SessionId);
                Assert.AreEqual(startsBefore + 2, Volatile.Read(ref activity.Starts));
                Assert.AreEqual(2, Volatile.Read(ref activity.Creates));
                Assert.AreEqual(0, Volatile.Read(ref activity.Sends));
                Assert.AreEqual(0, Volatile.Read(ref activity.Resumes));
                Assert.AreEqual(firstState, await host.RuntimeService.GetCurrentStateAsync(first.SessionId));
                var secondState = await host.RuntimeService.GetCurrentStateAsync(second.SessionId);
                Assert.IsNotNull(secondState.Entry);
                Assert.AreNotEqual(firstState.Entry.AttachmentGeneration, secondState.Entry.AttachmentGeneration);
                var snapshot = await service.SnapshotAsync(new(), CancellationToken.None);
                Assert.HasCount(2, snapshot.Sessions);
                foreach (var id in new[] { first.SessionId, second.SessionId })
                    Assert.IsTrue(snapshot.Sessions.Any(session => session.Id == id
                        && session.WorkspacePath == (project?.ProjectPath ?? global)
                        && session.ProviderKey == Provider.ProviderId.Value));
            }
            finally
            {
                // Release the real provider work before draining RPC admission and disposing the host,
                // including when an assertion fails while creation is held.
                activity.ReleaseCreate.TrySetResult();
                await service.CloseSessionsAsync();
                await service.CloseImportsAsync();
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public async Task InvalidScopeStaleHostAndMissingProjectNeverAdmitCreation()
    {
        var root = Path.Combine(Path.GetTempPath(), "codealta-session-create-" + Guid.NewGuid().ToString("N"));
        if (Directory.Exists(root)) throw new InvalidOperationException("Test root already exists.");
        Directory.CreateDirectory(root);
        try
        {
            var catalog = new ProjectCatalog(new CatalogOptions { GlobalRoot = Path.Combine(root, "global") });
            await using var reads = new OwnedSessionWorkspace(catalog, new SessionViewJournalStore(catalog.Options));
            await using var providers = new ModelProviderRegistry();
            providers.RegisterOrReplace(Provider, () => new DraftRuntime());
            var count = 0;
            var rpc = new WorkspaceService(reads, catalog, Epoch, providers, (_, _, _) =>
            { count++; throw new AssertFailedException("Invalid requests cannot create a session."); });
            var missing = Path.Combine(root, "missing");
            var samples = new[]
            {
                new WorkspaceCreateSessionRequest(Epoch, "global", "unexpected", null, null),
                new WorkspaceCreateSessionRequest(Epoch, "project", null, missing, null),
                new WorkspaceCreateSessionRequest(Epoch, "project", "unknown", null, null),
                new WorkspaceCreateSessionRequest(Epoch, "project", "unknown", "relative", null),
                new WorkspaceCreateSessionRequest(Epoch, "other", null, null, null),
                new WorkspaceCreateSessionRequest(Epoch, "global", null, null, " leading"),
                new WorkspaceCreateSessionRequest(Epoch, "global", null, null, new string('x', 257)),
            };
            foreach (var sample in samples)
            {
                var refused = await rpc.CreateSessionAsync(sample, CancellationToken.None);
                Assert.AreEqual("invalid_scope", refused.Status);
                Assert.IsNull(refused.ProjectPath);
            }
            Assert.AreEqual("stale_epoch", (await rpc.CreateSessionAsync(new(Guid.NewGuid().ToString("D"), "global", null, null, null), CancellationToken.None)).Status);
            Assert.AreEqual("project_missing", (await rpc.CreateSessionAsync(new(Epoch, "project", "unknown", missing, null), CancellationToken.None)).Status);
            var projectPath = Path.Combine(root, "archived");
            Directory.CreateDirectory(projectPath);
            var archived = await catalog.UpsertFromPathAsync(projectPath);
            archived.Archived = true;
            await catalog.SaveAsync(archived);
            Assert.AreEqual("project_missing", (await rpc.CreateSessionAsync(new(Epoch, "project", archived.Id, projectPath, null), CancellationToken.None)).Status);
            Assert.IsTrue((await catalog.GetByIdAsync(archived.Id))!.Archived);
            Assert.AreEqual("unconfigured", (await new WorkspaceService((string?)null)
                .CreateSessionAsync(new(Epoch, "global", null, null, null), CancellationToken.None)).Status);
            await using var unavailable = new ModelProviderRegistry();
            var noProvider = new WorkspaceService(reads, catalog, Epoch, unavailable, (_, _, _) =>
                throw new AssertFailedException("No provider means no admission."));
            Assert.AreEqual("provider_unavailable", (await noProvider.CreateSessionAsync(new(Epoch, "global", null, null, null), CancellationToken.None)).Status);
            await noProvider.CloseSessionsAsync();
            Assert.AreEqual(0, count);
            Assert.HasCount(1, await catalog.LoadAsync());
            await rpc.CloseSessionsAsync();
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public async Task DuplicateAdmissionAndCancelledWaitRetainOriginalUntilShutdownAndUncertainty()
    {
        var root = Path.Combine(Path.GetTempPath(), "codealta-session-create-" + Guid.NewGuid().ToString("N"));
        if (Directory.Exists(root)) throw new InvalidOperationException("Test root already exists.");
        Directory.CreateDirectory(root);
        var pending = new TaskCompletionSource<SessionViewDescriptor>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var catalog = new ProjectCatalog(new CatalogOptions { GlobalRoot = Path.Combine(root, "global") });
            await using var reads = new OwnedSessionWorkspace(catalog, new SessionViewJournalStore(catalog.Options));
            await using var providers = new ModelProviderRegistry();
            providers.RegisterOrReplace(Provider, () => new DraftRuntime());
            var count = 0;
            var rpc = new WorkspaceService(reads, catalog, Epoch, providers, (_, _, _) => { count++; return pending.Task; });
            var request = new WorkspaceCreateSessionRequest(Epoch, "global", null, null, null, Provider.ProviderId.Value);
            using var cancel = new CancellationTokenSource();
            var original = rpc.CreateSessionAsync(request, cancel.Token);
            cancel.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(async () => await original);
            providers.RegisterOrReplace(Provider with { IsEnabled = false }, () => throw new AssertFailedException("Replacement must not run."));
            Assert.AreEqual("busy", (await rpc.CreateSessionAsync(request, CancellationToken.None)).Status);
            var close = rpc.CloseSessionsAsync();
            Assert.IsFalse(close.IsCompleted);
            Assert.AreEqual("closed", (await rpc.CreateSessionAsync(request, CancellationToken.None)).Status);
            Assert.AreEqual(1, count);
            pending.SetException(new IOException("A journal write may have committed."));
            await close;
            providers.RegisterOrReplace(Provider, () => new DraftRuntime());
            var uncertain = new WorkspaceService(reads, catalog, Epoch, providers, (_, _, _) =>
                Task.FromException<SessionViewDescriptor>(new IOException("A write may have committed.")));
            Assert.AreEqual("create_unconfirmed", (await uncertain.CreateSessionAsync(request, CancellationToken.None)).Status);
            await uncertain.CloseSessionsAsync();
        }
        finally { pending.TrySetCanceled(); Directory.Delete(root, recursive: true); }
    }

    private sealed class DraftActivity
    {
        public int Starts;
        public int Creates;
        public int Sends;
        public int Resumes;
        public TaskCompletionSource CreateEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseCreate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class DraftRuntime(DraftActivity? activity = null, ModelProviderDescriptor? descriptor = null) : IModelProviderSessionRuntime
    {
        public ModelProviderDescriptor Descriptor => descriptor ?? Provider;
        public Task StartAsync(CancellationToken token = default)
        {
            if (activity is not null) Interlocked.Increment(ref activity.Starts);
            return Task.CompletedTask;
        }
        public Task StopAsync(CancellationToken token = default) => Task.CompletedTask;
        public Task<ModelProviderProbeResult> ProbeAsync(CancellationToken token = default) => throw new NotSupportedException();
        public IModelProviderTurnExecutor CreateTurnExecutor() => throw new NotSupportedException();
        public async Task<IAgentSession> CreateSessionAsync(AgentSessionCreateOptions options, CancellationToken token = default)
        {
            if (activity is not null)
            {
                Interlocked.Increment(ref activity.Creates);
                activity.CreateEntered.TrySetResult();
                await activity.ReleaseCreate.Task.WaitAsync(token);
            }
            return new DraftSession(options.SessionId!, options.WorkingDirectory, activity, Descriptor);
        }
        public Task<IAgentSession> ResumeSessionAsync(string id, AgentSessionResumeOptions options, CancellationToken token = default)
        {
            if (activity is not null) Interlocked.Increment(ref activity.Resumes);
            return Task.FromResult<IAgentSession>(new DraftSession(id, options.WorkingDirectory, activity, Descriptor));
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class DraftSession(string id, string? path, DraftActivity? activity = null, ModelProviderDescriptor? descriptor = null) : IAgentSession
    {
        public ModelProviderId ProviderId => (descriptor ?? Provider).ProviderId;
        public string SessionId => id;
        public string? WorkspacePath => path;
        public async IAsyncEnumerable<AgentEvent> StreamEventsAsync([EnumeratorCancellation] CancellationToken token = default)
        { await Task.CompletedTask; yield break; }
        public IDisposable Subscribe(Action<AgentEvent> handler) => new Subscription();
        public Task<AgentRunId> SendAsync(AgentSendOptions options, CancellationToken token = default)
        {
            if (activity is not null) Interlocked.Increment(ref activity.Sends);
            throw new NotSupportedException();
        }
        public Task AbortAsync(CancellationToken token = default) => Task.CompletedTask;
        public Task<AgentRunId> SteerAsync(AgentSteerOptions options, CancellationToken token = default) => throw new NotSupportedException();
        public Task CompactAsync(CancellationToken token = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<AgentEvent>> GetHistoryAsync(CancellationToken token = default) => Task.FromResult<IReadOnlyList<AgentEvent>>([]);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        private sealed class Subscription : IDisposable { public void Dispose() { } }
    }
}
