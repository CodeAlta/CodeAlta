using System.Text;
using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Catalog;
using CodeAlta.Catalog.Skills;
using CodeAlta.Orchestration.Runtime;
using CodeAlta.Orchestration.Runtime.SystemPrompts;

namespace CodeAlta.Tests;

/// <summary>New tiny task-owned journals; no Host, provider, discovery, native or default-profile execution.</summary>
[TestClass]
public sealed class OwnedSessionNotesReadTests
{
    [TestMethod]
    public Task ContainedRead_PreservesJournalOrderEmptyClearAndExactText() => Fixture.Run(async f =>
    {
        Assert.IsNull(await f.Keep(() => f.Store.ReadLatestNotesContainedAsync("session")));
        var newer = f.Note("newer timestamp", AgentNotesUpdateKind.Set, 2);
        var last = f.Note("# Exact\r\n😀\u0085\ufeff  ", AgentNotesUpdateKind.Set, 1);
        await f.Append(newer, last);
        Assert.AreEqual(last, await f.Keep(() => f.Store.ReadLatestNotesContainedAsync("session")));
        Assert.AreEqual(last, await f.Keep(() => f.Store.ReadLatestNotesAsync("session")));
        var empty = f.Note("", AgentNotesUpdateKind.Set, 3);
        await f.Append(empty);
        Assert.AreEqual(empty, await f.Keep(() => f.Store.ReadLatestNotesContainedAsync("session")));
        var clear = f.Note("", AgentNotesUpdateKind.Cleared, 4);
        await f.Append(clear);
        Assert.AreEqual(clear, await f.Keep(() => f.Store.ReadLatestNotesContainedAsync("session")));
    });

    [TestMethod]
    public Task ContainedRead_PreservesTrailingToleranceAndRejectsMalformedInterior() => Fixture.Run(async f =>
    {
        var note = f.Note("retained", AgentNotesUpdateKind.Set, 1);
        await f.Append(note);
        await f.Keep(() => File.AppendAllTextAsync(f.Journal, "{broken", new UTF8Encoding(false)));
        Assert.AreEqual(note, await f.Keep(() => f.Store.ReadLatestNotesContainedAsync("session")));
        await f.Keep(() => File.AppendAllTextAsync(f.Journal, "\n" + note.ToJson() + "\n", new UTF8Encoding(false)));
        var read = f.Keep(() => f.Store.ReadLatestNotesContainedAsync("session"));
        await Assert.ThrowsAsync<JsonException>(() => read);
    });

    [TestMethod]
    public Task ContainedRead_RejectsCachedOutsidePathWithoutChangingLegacyReader() => Fixture.Run(async f =>
    {
        // Outside the sessions subtree, but still entirely inside this fixture's retained root.
        var outside = Path.Combine(f.Root, "copied-cache-target.jsonl");
        var note = f.Note("fixture-only outside content", AgentNotesUpdateKind.Set, 1);
        await f.Keep(() => File.WriteAllTextAsync(outside, note.ToJson() + "\n", new UTF8Encoding(false)));
        var cache = new LiteralCache(new(outside, new(DateTime.UnixEpoch, 0), f.Summary("session", f.Root), null));
        var store = new FileSystemAgentSessionStore(new(f.Root), new AgentSessionJournalFile(), cache);
        var read = f.Keep(() => store.ReadLatestNotesContainedAsync("session"));
        Assert.AreEqual("outside_root", (await Assert.ThrowsAsync<AgentSessionHistoryException>(() => read)).Code);
        Assert.AreEqual(note, await f.Keep(() => store.ReadLatestNotesAsync("session")));
        Assert.AreEqual(1, cache.Calls); // Containment did not re-resolve or retry another path.
    });

    [TestMethod]
    public Task OwnedRuntimeRead_UsesKnownRootedScopeAndPreservesEmptyCollapse() => Fixture.Run(async f =>
    {
        var runtime = f.CreateRuntime();
        Assert.AreEqual("", await f.Keep(() => runtime.GetOwnedNotesMarkdownAsync("session")));
        await f.Append(f.Note("", AgentNotesUpdateKind.Set, 1));
        Assert.AreEqual("", await f.Keep(() => runtime.GetOwnedNotesMarkdownAsync("session")));
        await f.Append(f.Note("", AgentNotesUpdateKind.Cleared, 2));
        Assert.AreEqual("", await f.Keep(() => runtime.GetOwnedNotesMarkdownAsync("session")));
        await f.Append(f.Note("durable\r\n😀  ", AgentNotesUpdateKind.Set, 3));
        Assert.AreEqual("durable\r\n😀  ", await f.Keep(() => runtime.GetOwnedNotesMarkdownAsync("session")));
        Assert.AreEqual("durable\r\n😀  ", await f.Keep(() => runtime.GetNotesMarkdownAsync("session")));
        foreach (var id in new[] { "missing", "../session", f.Journal })
        {
            var read = f.Keep(() => runtime.GetOwnedNotesMarkdownAsync(id));
            await Assert.ThrowsAsync<SessionNotesSessionNotFoundException>(() => read);
        }
        var unregistered = Path.Combine(f.Root, "unregistered-project");
        Directory.CreateDirectory(unregistered);
        await f.Keep(() => f.Store.UpsertSessionAsync(f.Summary("outside-scope", unregistered)));
        var outside = f.Keep(() => runtime.GetOwnedNotesMarkdownAsync("outside-scope"));
        await Assert.ThrowsAsync<SessionNotesSessionNotFoundException>(() => outside);
    });

    [TestMethod]
    public Task RuntimeClear_WritesOnlyTheExplicitTargetAndLeavesOtherNotesIntact() => Fixture.Run(async f =>
    {
        var runtime = f.CreateRuntime();
        await f.Append(f.Note("# Target", AgentNotesUpdateKind.Set, 1));
        await f.Keep(() => f.Store.UpsertSessionAsync(f.Summary("other", f.Root)));
        await f.Keep(() => f.Store.AppendEventsAsync("fixture", "notes-provider", "other",
            [new AgentNotesEvent(new("notes-provider"), "other", DateTimeOffset.UnixEpoch, null, AgentNotesUpdateKind.Set, "# Preserve")]));
        Assert.AreEqual("# Target", await f.Keep(() => runtime.GetOwnedNotesMarkdownAsync("session")));
        Assert.AreEqual("# Preserve", await f.Keep(() => runtime.GetOwnedNotesMarkdownAsync("other")));
        var committed = new List<AgentNotesEvent>();
        await f.Keep(() => runtime.UpdateNotesAsync("session", "", AgentNotesUpdateKind.Cleared, committed.Add));
        Assert.AreEqual(1, committed.Count);
        Assert.AreEqual("session", committed[0].SessionId);
        Assert.AreEqual(AgentNotesUpdateKind.Cleared, committed[0].Kind);
        Assert.AreEqual("", await f.Keep(() => runtime.GetOwnedNotesMarkdownAsync("session")));
        Assert.AreEqual("# Preserve", await f.Keep(() => runtime.GetOwnedNotesMarkdownAsync("other")));
    });

    private sealed class Fixture
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "CodeAlta-owned-notes-" + Guid.NewGuid().ToString("N"));
        internal FileSystemAgentSessionStore Store { get; private set; } = null!; // Assigned by retained setup before the body is launched.
        internal string Journal => new AgentRuntimePathLayout(Root).GetSessionFilePath("session", DateTimeOffset.UnixEpoch);
        private readonly List<Task> _originals = [];
        private Task? _lifetime;
        private Task _lifetimeObserver = Task.CompletedTask;
        private AgentHub? _hub;
        private SessionRuntimeService? _runtime;
        private volatile bool _closing;
        internal Task Keep(Func<Task> start) => Keep(async () => { await start().ConfigureAwait(false); return true; });
        internal Task<T> Keep<T>(Func<Task<T>> start)
        {
            var acquisition = new TaskCompletionSource<Task<T>>(TaskCreationOptions.RunContinuationsAsynchronously);
            var observer = Observe(acquisition.Task);
            _originals.Add(observer); // Own acquisition and observer before setup/body/I/O/disposal can launch.
            try { acquisition.SetResult(start()); } catch (Exception ex) { acquisition.SetException(ex); }
            return observer;
        }
        private static async Task<T> Observe<T>(Task<Task<T>> acquisition) => await (await acquisition.ConfigureAwait(false)).ConfigureAwait(false);
        internal AgentNotesEvent Note(string text, AgentNotesUpdateKind kind, int seconds)
            => new(new("notes-provider"), "session", DateTimeOffset.UnixEpoch.AddSeconds(seconds), null, kind, text);
        internal AgentSessionSummary Summary(string id, string cwd) => new()
        {
            SessionId = id, ProviderId = new("notes-provider"), ProviderKey = "notes-provider", ProtocolFamily = "fixture",
            WorkingDirectory = cwd, CreatedAt = DateTimeOffset.UnixEpoch, UpdatedAt = DateTimeOffset.UnixEpoch, Title = "Notes fixture",
        };
        internal Task Append(params AgentEvent[] events) => Keep(() => Store.AppendEventsAsync("fixture", "notes-provider", "session", events));

        internal SessionRuntimeService CreateRuntime()
        {
            var options = new CatalogOptions { GlobalRoot = Root };
            var catalog = new SessionViewCatalog(options);
            var registry = new ModelProviderRegistry();
            registry.RegisterOrReplace(new(new("notes-provider"), "Forbidden"), static () => throw new AssertFailedException("No provider construction."));
            _hub = new AgentHub(registry, Root);
            var discovery = new ForbiddenDiscovery();
            var skills = new SkillCatalog([discovery]);
            _runtime = new(_hub, new AgentSessionCatalog(catalog.JournalStore.CreateSessionStore()), new ProjectCatalog(options), catalog,
                new AgentInstructionTemplateProvider(skills, options, discovery), options, skills);
            return _runtime;
        }

        internal static async Task Run(Func<Fixture, Task> body)
        {
            var fixture = new Fixture();
            var launch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            fixture._lifetime = fixture.RunCore(body, launch.Task);
            fixture._lifetimeObserver = Settle(fixture._lifetime);
            launch.TrySetResult();
            try { await fixture._lifetime.WaitAsync(TimeSpan.FromSeconds(30)); await fixture._lifetimeObserver; }
            catch (Exception ex)
            {
                fixture._closing = true;
                ex.Data["RetainedFixture"] = fixture; ex.Data["RetainedRoot"] = fixture.Root;
                ex.Data["OriginalLifetime"] = fixture._lifetime;
                throw; // Permanent failure; no root deletion or timeout-as-termination.
            }
        }

        private async Task RunCore(Func<Fixture, Task> body, Task launch)
        {
            await launch;
            Exception? primary = null;
            try
            {
                for (var node = new DirectoryInfo(Path.GetDirectoryName(Root)!); node is not null; node = node.Parent)
                    if ((node.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Reparse ancestry.");
                if (Directory.Exists(Root) || File.Exists(Root)) throw new IOException("Fixture root exists.");
                Directory.CreateDirectory(Root);
                Console.WriteLine("Owned notes fixture retained root: " + Root);
                Store = new(new AgentRuntimePathLayout(Root));
                await Keep(() => Store.UpsertSessionAsync(Summary("session", Root)));
                if (_closing) return;
                var originalBody = Keep(() => body(this));
                await originalBody;
            }
            catch (Exception ex) { primary = ex; throw; }
            finally
            {
                // No runs/providers or cancellation-dependent gates exist in this fixture. The original
                // sequential body has settled before dependency disposal; late setup still owns cleanup.
                await Task.WhenAll(_originals.Select(Settle));
                try
                {
                    if (_runtime is not null) await Keep(() => _runtime.DisposeAsync().AsTask());
                    if (_hub is not null) await Keep(() => _hub.DisposeAsync().AsTask());
                }
                catch (Exception ex) { if (primary is not null) throw new AggregateException(primary, ex); throw; }
                // Task-owned roots are deliberately retained even on success.
            }
        }

        private static async Task Settle(Task original) { try { await original; } catch { /* Outcomes inspected in the original body. */ } }
    }

    private sealed class ForbiddenDiscovery : ISystemPromptContentLocator, ISkillRootProvider
    {
        public SystemPromptContentRoots GetRoots(SystemPromptDiscoveryContext context) => throw new AssertFailedException("No prompt discovery.");
        public string ResolveBuiltInPromptPath(string path) => throw new AssertFailedException("No shipped prompt lookup.");
        public string ResolveBuiltInDocPath(string file) => throw new AssertFailedException("No shipped doc lookup.");
        public ValueTask<IReadOnlyList<SkillRootRegistration>> GetRootsAsync(SkillDiscoveryContext context, CancellationToken cancellationToken = default)
            => throw new AssertFailedException("No skill discovery.");
    }

    private sealed class LiteralCache(AgentSessionCacheProjection projection) : IAgentSessionProjectionCache
    {
        internal int Calls;
        public Task<AgentSessionCacheProjection?> GetSessionAsync(string id, AgentSessionCacheProjectionContext context, CancellationToken cancellationToken = default)
        { Calls++; return Task.FromResult<AgentSessionCacheProjection?>(projection); }
        public IAsyncEnumerable<AgentSessionCacheProjection> ListSessionsAsync(AgentSessionCacheProjectionContext context, CancellationToken cancellationToken = default) => throw new AssertFailedException("No list.");
        public Task UpsertSessionAsync(AgentSessionCacheProjection value, CancellationToken cancellationToken = default) => throw new AssertFailedException("No write.");
        public Task RemoveSessionAsync(string id, CancellationToken cancellationToken = default) => throw new AssertFailedException("No removal.");
        public Task<AgentSessionCacheReconciliationResult> ReconcileAsync(AgentSessionCacheProjectionContext context, CancellationToken cancellationToken = default) => throw new AssertFailedException("No reconciliation.");
    }
}
