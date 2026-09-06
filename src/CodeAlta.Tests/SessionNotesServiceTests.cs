using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Catalog;
using CodeAlta.Catalog.Skills;
using CodeAlta.LiveTool;
using CodeAlta.Orchestration.Runtime;
using CodeAlta.Orchestration.Runtime.SystemPrompts;
using CodeAlta.Tui.App;

namespace CodeAlta.Tests;

// Owned journals only: no app, provider execution, discovery, plugins, or default profile.
[TestClass]
public sealed class SessionNotesServiceTests
{
    [TestMethod]
    public async Task PersistedSessionWithoutOpenTab_RoundTripsNotes()
    {
        await using var fixture = new NotesFixture();
        await fixture.SeedAsync("closed");
        var service = new RuntimeAltaNotesService(fixture.Runtime);
        var caller = new AltaCallerIdentity { Kind = "agent", SourceSessionId = "closed" };

        await service.SetMarkdownAsync("# Closed\r\n\nExact  ", caller);
        Assert.AreEqual("# Closed\r\n\nExact  ", await service.GetMarkdownAsync(caller));

        await using var restarted = fixture.CreateRuntime(new SessionViewCatalog(new CatalogOptions { GlobalRoot = fixture.Root }));
        var reopened = new RuntimeAltaNotesService(restarted);
        Assert.AreEqual("# Closed\r\n\nExact  ", await reopened.GetMarkdownAsync(caller));
        var history = await fixture.Catalog.JournalStore.CreateSessionStore().ReadEventsAsync("closed");
        Assert.AreEqual("# Closed\r\n\nExact  ", SessionHistoryCoordinator.RecoverNotesMarkdownFromHistory(history));
        var notes = history.OfType<AgentNotesEvent>().Single();
        Assert.AreEqual("notes-provider", notes.ProviderId.Value);
        Assert.IsNull(notes.RunId);
        await reopened.ClearAsync(caller);
        Assert.AreEqual(string.Empty, await service.GetMarkdownAsync(caller));
        history = await fixture.Catalog.JournalStore.CreateSessionStore().ReadEventsAsync("closed");
        Assert.AreEqual(AgentNotesUpdateKind.Cleared, history.OfType<AgentNotesEvent>().Last().Kind);
        Assert.AreEqual(string.Empty, SessionHistoryCoordinator.RecoverNotesMarkdownFromHistory(history));
    }

    [TestMethod]
    [DataRow("notes")]
    [DataRow("note")]
    public async Task ActualCommands_PreserveAliasesJsonlAndEmptyMarkdown(string alias)
    {
        await using var fixture = new NotesFixture();
        await fixture.SeedAsync("closed");
        var service = new RuntimeAltaNotesService(fixture.Runtime);
        var dispatcher = fixture.Dispatcher(service);
        var caller = Caller("closed");
        foreach (var text in new[] { "## Plan\r\n- [ ] Exact  \n\n", string.Empty })
        {
            var set = await dispatcher.InvokeAsync([alias, "set", "--stdin"], text, caller);
            AssertRecord(set, "alta.notes.updated", text);
            var get = await dispatcher.InvokeAsync([alias, "get"], caller: caller);
            AssertRecord(get, "alta.notes.current", text);
        }

        AssertRecord(await dispatcher.InvokeAsync([alias, "clear"], caller: caller), "alta.notes.updated", string.Empty);
    }

    [TestMethod]
    public async Task CaseInsensitiveLookup_PreservesCanonicalSessionIdentity()
    {
        await using var fixture = new NotesFixture();
        await fixture.SeedAsync("canonical");
        var service = new RuntimeAltaNotesService(fixture.Runtime);
        AltaNotesChangedEventArgs? changed = null;
        service.Changed += (_, args) => changed = args;
        await service.SetMarkdownAsync("identity", Caller("CANONICAL"));
        Assert.AreEqual("identity", await service.GetMarkdownAsync(Caller("canonical")));
        Assert.IsNotNull(changed);
        Assert.AreEqual("canonical", changed.SessionId);
        var notes = await fixture.Catalog.JournalStore.CreateSessionStore().ReadLatestNotesAsync("canonical");
        Assert.IsNotNull(notes);
        Assert.AreEqual("canonical", notes.SessionId);
        Assert.AreEqual("notes-provider", notes.ProviderId.Value);
        Assert.AreEqual(1, Directory.GetFiles(fixture.Root, "*.jsonl", SearchOption.AllDirectories).Length);
    }

    [TestMethod]
    [DataRow("unknown")]
    [DataRow("../closed")]
    public async Task ExplicitUnknownSource_DoesNotFallBackOrCreateJournal(string source)
    {
        await using var fixture = new NotesFixture();
        await fixture.SeedAsync("closed");
        var service = new RuntimeAltaNotesService(fixture.Runtime, () => "closed");
        await service.SetMarkdownAsync("retained", Caller("closed"));
        var changes = 0;
        service.Changed += (_, _) => changes++;
        foreach (var command in new[] { "get", "set", "clear" })
        {
            var result = await fixture.Dispatcher(service).InvokeAsync(["notes", command], "wrong", Caller(source));
            Assert.AreEqual(AltaExitCodes.Usage, result.ExitCode, result.Stdout + result.Stderr);
            StringAssert.Contains(result.Stdout + result.Stderr, "usage.missingSession");
            Assert.IsFalse(result.Stdout.Contains("alta.notes.updated", StringComparison.Ordinal));
        }

        Assert.AreEqual("retained", await service.GetMarkdownAsync(Caller("closed")));
        Assert.AreEqual(0, changes);
        Assert.AreEqual(1, Directory.GetFiles(fixture.Root, "*.jsonl", SearchOption.AllDirectories).Length);
    }

    [TestMethod]
    [DataRow("get")]
    [DataRow("set")]
    [DataRow("clear")]
    public async Task NoCallerAndNoFallback_ReturnsMissingSessionUsage(string command)
    {
        await using var fixture = new NotesFixture();
        var result = await fixture.Dispatcher(new RuntimeAltaNotesService(fixture.Runtime)).InvokeAsync(["notes", command], "ignored");
        Assert.AreEqual(AltaExitCodes.Usage, result.ExitCode);
        StringAssert.Contains(result.Stdout + result.Stderr, "usage.missingSession");
        Assert.AreEqual(0, Directory.GetFiles(fixture.Root, "*.jsonl", SearchOption.AllDirectories).Length);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Set_CapturesIdentityBeforeAwaitingStdinAndLosingView(bool explicitSource)
    {
        await using var fixture = new NotesFixture();
        await fixture.SeedAsync("first");
        await fixture.SeedAsync("second");
        string? selected = "first";
        var fallbackCalls = 0;
        var service = new RuntimeAltaNotesService(fixture.Runtime, () => { fallbackCalls++; return selected; });
        AltaNotesChangedEventArgs? changed = null;
        service.Changed += (_, args) => changed = args;
        using var input = new ControlledReader();
        var caller = explicitSource ? Caller("first") : AltaCallerIdentity.Host;
        var pending = fixture.InvokeWithReaderAsync(service, ["notes", "set", "--stdin"], input, caller);
        await input.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        selected = "second";
        // Closing the old view and then losing all selection cannot disable the captured operation.
        selected = null;
        input.Content.SetResult("captured");
        AssertRecord(await pending, "alta.notes.updated", "captured");
        Assert.AreEqual(explicitSource ? 0 : 1, fallbackCalls);
        Assert.AreEqual("captured", await service.GetMarkdownAsync(Caller("first")));
        Assert.AreEqual(string.Empty, await service.GetMarkdownAsync(Caller("second")));
        Assert.IsNotNull(changed);
        Assert.AreEqual("first", changed.SessionId);
        Assert.AreEqual("first", changed.Caller.SourceSessionId);
        Assert.AreEqual(explicitSource ? "first" : null, caller.SourceSessionId, "The original immutable caller must not be changed.");
    }

    [TestMethod]
    public async Task RootsAreIsolated_IdentifiersAndPathsAreNotGrants()
    {
        await using var first = new NotesFixture();
        await using var second = new NotesFixture();
        await first.SeedAsync("shared");
        await second.SeedAsync("shared");
        await first.SeedAsync("only-first");
        var a = new RuntimeAltaNotesService(first.Runtime);
        var b = new RuntimeAltaNotesService(second.Runtime);
        await a.SetMarkdownAsync("one", Caller("shared"));
        await b.SetMarkdownAsync("two", Caller("shared"));
        Assert.AreEqual("one", await a.GetMarkdownAsync(Caller("shared")));
        Assert.AreEqual("two", await b.GetMarkdownAsync(Caller("shared")));
        await Assert.ThrowsExactlyAsync<AltaNotesSessionRequiredException>(() => b.SetMarkdownAsync("bad", Caller("only-first")).AsTask());
        await Assert.ThrowsExactlyAsync<AltaNotesSessionRequiredException>(() => b.SetMarkdownAsync("bad", Caller(first.JournalPath("shared"))).AsTask());
        Assert.AreEqual(1, Directory.GetFiles(second.Root, "*.jsonl", SearchOption.AllDirectories).Length);
    }

    [TestMethod]
    public async Task PersistedProjectSession_RequiresRootedCatalogAssociationNotCallerWorkspace()
    {
        await using var fixture = new NotesFixture();
        var projectPath = Path.Combine(fixture.Root, "owned-project");
        Directory.CreateDirectory(projectPath);
        await fixture.SeedAsync("project-notes", projectPath);
        var service = new RuntimeAltaNotesService(fixture.Runtime);
        var before = await File.ReadAllBytesAsync(fixture.JournalPath("project-notes"));
        await Assert.ThrowsExactlyAsync<AltaNotesSessionRequiredException>(() => service.SetMarkdownAsync("wrong", Caller("project-notes")).AsTask());
        CollectionAssert.AreEqual(before, await File.ReadAllBytesAsync(fixture.JournalPath("project-notes")));
        await new ProjectCatalog(new CatalogOptions { GlobalRoot = fixture.Root }).UpsertFromPathAsync(projectPath);
        await service.SetMarkdownAsync("project", Caller("project-notes"));
        Assert.AreEqual("project", await service.GetMarkdownAsync(Caller("project-notes")));
    }

    [TestMethod]
    public async Task PreCanceledOperations_DoNotReadWriteOrNotify()
    {
        await using var fixture = new NotesFixture();
        await fixture.SeedAsync("closed");
        var service = new RuntimeAltaNotesService(fixture.Runtime);
        var caller = Caller("closed");
        await service.SetMarkdownAsync("retained", caller);
        var before = await File.ReadAllBytesAsync(fixture.JournalPath("closed"));
        var changes = 0;
        service.Changed += (_, _) => changes++;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => service.GetMarkdownAsync(caller, cancellation.Token).AsTask());
        await Assert.ThrowsAsync<OperationCanceledException>(() => service.SetMarkdownAsync("wrong", caller, cancellation.Token).AsTask());
        await Assert.ThrowsAsync<OperationCanceledException>(() => service.ClearAsync(caller, cancellation.Token).AsTask());
        CollectionAssert.AreEqual(before, await File.ReadAllBytesAsync(fixture.JournalPath("closed")));
        Assert.AreEqual(0, changes);
    }

    [TestMethod]
    public async Task StdinCancellation_DoesNotMutateCapturedSessionOrAnnounceSuccess()
    {
        await using var fixture = new NotesFixture();
        await fixture.SeedAsync("closed");
        var service = new RuntimeAltaNotesService(fixture.Runtime, () => "closed");
        await service.SetMarkdownAsync("retained", Caller("closed"));
        var changes = 0;
        service.Changed += (_, _) => changes++;
        using var input = new ControlledReader();
        using var cancellation = new CancellationTokenSource();
        var pending = fixture.InvokeWithReaderAsync(service, ["notes", "set"], input, AltaCallerIdentity.Host, cancellation.Token);
        await input.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        var result = await pending;
        Assert.AreEqual(AltaExitCodes.TimeoutOrCancellation, result.ExitCode);
        Assert.IsFalse(result.Stdout.Contains("alta.notes.updated", StringComparison.Ordinal));
        Assert.AreEqual("retained", await service.GetMarkdownAsync(Caller("closed")));
        Assert.AreEqual(0, changes);
    }

    [TestMethod]
    public async Task JournalGate_OrdersCompetingAdaptersAndDoesNotBlockAnotherSession()
    {
        var catalog = new ControlledCatalog();
        await using var fixture = new NotesFixture(catalog);
        await fixture.SeedAsync("first");
        await fixture.SeedAsync("other");
        var a = new RuntimeAltaNotesService(fixture.Runtime);
        var b = new RuntimeAltaNotesService(fixture.Runtime);
        var changes = new List<string>();
        a.Changed += (_, args) => changes.Add(args.Markdown);
        b.Changed += (_, args) => changes.Add(args.Markdown);
        var first = a.SetMarkdownAsync("one", Caller("first")).AsTask();
        await catalog.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = b.SetMarkdownAsync("two", Caller("first")).AsTask();
        try
        {
            Assert.IsFalse(first.IsCompleted);
            Assert.IsFalse(second.IsCompleted);
            Assert.AreEqual(0, changes.Count, "No Changed before acknowledged feedback.");
            await b.SetMarkdownAsync("other", Caller("other")).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { catalog.Release.TrySetResult(); }
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
        CollectionAssert.AreEqual(new[] { "other", "one", "two" }, changes);
        Assert.AreEqual("two", await a.GetMarkdownAsync(Caller("first")));
        var history = await fixture.Catalog.JournalStore.CreateSessionStore().ReadEventsAsync("first");
        CollectionAssert.AreEqual(new[] { "one", "two" }, history.OfType<AgentNotesEvent>().Select(note => note.Markdown).ToArray());
    }

    [TestMethod]
    public async Task CancellationWhileAwaitingReadAndWrite_DoesNotClearCommittedNotes()
    {
        var catalog = new ControlledCatalog();
        await using var fixture = new NotesFixture(catalog);
        await fixture.SeedAsync("first");
        var service = new RuntimeAltaNotesService(fixture.Runtime);
        var changes = new List<string>();
        service.Changed += (_, args) => changes.Add(args.Markdown);
        using var committedCancellation = new CancellationTokenSource();
        var committed = service.SetMarkdownAsync("retained", Caller("first"), committedCancellation.Token).AsTask();
        await catalog.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var cancellation = new CancellationTokenSource();
        var read = service.GetMarkdownAsync(Caller("first"), cancellation.Token).AsTask();
        var clear = service.ClearAsync(Caller("first"), cancellation.Token).AsTask();
        try
        {
            Assert.IsFalse(read.IsCompleted);
            Assert.IsFalse(clear.IsCompleted);
            cancellation.Cancel();
            committedCancellation.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => read);
            await Assert.ThrowsAsync<OperationCanceledException>(() => clear);
        }
        finally { catalog.Release.TrySetResult(); }
        await committed.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual("retained", await service.GetMarkdownAsync(Caller("first")));
        CollectionAssert.AreEqual(new[] { "retained" }, changes);
    }

    [TestMethod]
    public async Task PostCommitFailure_IsExplicitAndDoesNotClaimRollbackOrCommandSuccess()
    {
        var catalog = new ControlledCatalog { Fail = true };
        await using var fixture = new NotesFixture(catalog);
        await fixture.SeedAsync("first");
        var service = new RuntimeAltaNotesService(fixture.Runtime);
        var changes = 0;
        service.Changed += (_, _) => changes++;
        var result = await fixture.Dispatcher(service).InvokeAsync(["notes", "set"], "committed", Caller("first"));
        Assert.AreNotEqual(AltaExitCodes.Success, result.ExitCode);
        StringAssert.Contains(result.Stdout + result.Stderr, "were committed");
        Assert.IsFalse(result.Stdout.Contains("alta.notes.updated", StringComparison.Ordinal));
        Assert.AreEqual(0, changes);
        Assert.AreEqual("committed", await service.GetMarkdownAsync(Caller("first")));
    }

    [TestMethod]
    public async Task MalformedRead_IsVisibleAndDoesNotReplaceNotesWithEmpty()
    {
        await using var fixture = new NotesFixture();
        await fixture.SeedAsync("closed");
        var service = new RuntimeAltaNotesService(fixture.Runtime);
        await service.SetMarkdownAsync("retained", Caller("closed"));
        await File.AppendAllTextAsync(fixture.JournalPath("closed"), "not-json\n{}\n");
        var before = await File.ReadAllBytesAsync(fixture.JournalPath("closed"));
        var changes = 0;
        service.Changed += (_, _) => changes++;
        var result = await fixture.Dispatcher(service).InvokeAsync(["notes", "get"], caller: Caller("closed"));
        Assert.AreNotEqual(AltaExitCodes.Success, result.ExitCode);
        StringAssert.Contains(result.Stdout + result.Stderr, "alta.error");
        Assert.IsFalse(result.Stdout.Contains("alta.notes.current", StringComparison.Ordinal));
        CollectionAssert.AreEqual(before, await File.ReadAllBytesAsync(fixture.JournalPath("closed")));
        Assert.AreEqual(0, changes);
    }

    [TestMethod]
    [DataRow("{\"$type\":\"notes\",\"markdown\":\"truncated", false)]
    [DataRow("{\"$type\":\"notes\",\"markdown\":\"truncated", true)]
    [DataRow("not-json", false)]
    [DataRow("not-json", true)]
    public async Task MalformedTail_SetRefusesWithoutSuccessChangedOrByteMutation(string tail, bool newline)
    {
        await using var fixture = new NotesFixture();
        await fixture.SeedAsync("closed");
        var service = new RuntimeAltaNotesService(fixture.Runtime);
        await service.SetMarkdownAsync("retained", Caller("closed"));
        await File.AppendAllTextAsync(fixture.JournalPath("closed"), tail + (newline ? "\n" : string.Empty));
        var before = await File.ReadAllBytesAsync(fixture.JournalPath("closed"));
        var changes = 0;
        service.Changed += (_, _) => changes++;

        var result = await fixture.Dispatcher(service).InvokeAsync(["notes", "set"], "wrong", Caller("closed"));

        Assert.AreNotEqual(AltaExitCodes.Success, result.ExitCode);
        StringAssert.Contains(result.Stdout + result.Stderr, "alta.error");
        Assert.IsFalse(result.Stdout.Contains("alta.notes.updated", StringComparison.Ordinal));
        Assert.AreEqual(0, changes);
        CollectionAssert.AreEqual(before, await File.ReadAllBytesAsync(fixture.JournalPath("closed")));
    }

    private static AltaCallerIdentity Caller(string sessionId) => new() { Kind = "agent", SourceSessionId = sessionId };

    private static void AssertRecord(AltaCommandResult result, string type, string markdown)
    {
        Assert.AreEqual(AltaExitCodes.Success, result.ExitCode, result.Stdout + result.Stderr);
        var records = result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => { using var document = JsonDocument.Parse(line); return document.RootElement.Clone(); }).ToArray();
        Assert.AreEqual("alta.result", records[0].GetProperty("type").GetString());
        var record = records.Single(record => record.GetProperty("type").GetString() == type);
        Assert.AreEqual(markdown, record.GetProperty("markdown").GetString());
        Assert.AreEqual(markdown.Length, record.GetProperty("length").GetInt32());
        Assert.AreEqual(markdown.Length == 0, record.GetProperty("empty").GetBoolean());
        Assert.AreEqual(1, record.GetProperty("version").GetInt32());
        Assert.IsFalse(string.IsNullOrEmpty(record.GetProperty("correlationId").GetString()));
    }

    private sealed class NotesFixture : IAsyncDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "CodeAlta-notes-" + Guid.NewGuid().ToString("N"));
        public SessionViewCatalog Catalog { get; }
        public AgentHub Hub { get; }
        public SessionRuntimeService Runtime { get; }

        public NotesFixture(IAgentSessionCatalog? agentCatalog = null)
        {
            Directory.CreateDirectory(Root);
            var options = new CatalogOptions { GlobalRoot = Root };
            Catalog = new SessionViewCatalog(options);
            var registry = new ModelProviderRegistry();
            registry.RegisterOrReplace(new ModelProviderDescriptor(new ModelProviderId("notes-provider"), "Forbidden"),
                () => throw new AssertFailedException("Notes must not start providers."));
            Hub = new AgentHub(registry, Root);
            Runtime = CreateRuntime(Catalog, agentCatalog);
        }

        public SessionRuntimeService CreateRuntime(SessionViewCatalog catalog, IAgentSessionCatalog? agentCatalog = null)
        {
            var options = new CatalogOptions { GlobalRoot = Root };
            var discovery = new ForbiddenDiscovery();
            var skills = new SkillCatalog([discovery]);
            return new SessionRuntimeService(Hub, agentCatalog ?? new AgentSessionCatalog(catalog.JournalStore.CreateSessionStore()),
                new ProjectCatalog(options), catalog,
                new AgentInstructionTemplateProvider(skills, options, discovery), options, skills);
        }

        public string JournalPath(string sessionId) => new AgentRuntimePathLayout(Root)
            .GetSessionFilePath(sessionId, DateTimeOffset.Parse("2026-09-01T00:00:00Z"));

        public AltaCommandDispatcher Dispatcher(IAltaNotesService service) => new(new AltaCommandRegistry(),
            new AltaServiceCollection().Add(new CatalogOptions { GlobalRoot = Root }).Add<IAltaNotesService>(service));

        public async ValueTask<AltaCommandResult> InvokeWithReaderAsync(IAltaNotesService service, string[] args, TextReader input, AltaCallerIdentity caller, CancellationToken cancellationToken = default)
            => AltaTranscriptFormatter.FlattenForLiveTool(await new AltaCommandRegistry().InvokeAsync(args, new AltaCommandContext
            {
                Caller = caller,
                Services = new AltaServiceCollection().Add(new CatalogOptions { GlobalRoot = Root }).Add<IAltaNotesService>(service),
                Stdin = input,
                Stdout = new StringWriter(),
                Stderr = new StringWriter(),
                CorrelationId = "notes-fixture",
                CancellationToken = cancellationToken,
            }));

        public async Task SeedAsync(string sessionId, string? workingDirectory = null)
        {
            var createdAt = DateTimeOffset.Parse("2026-09-01T00:00:00Z");
            await Catalog.JournalStore.EnsureHeaderAsync(new SessionViewDescriptor
            {
                SessionId = sessionId, ProviderId = "notes-provider", ProviderKey = "notes-provider",
                Kind = SessionViewKind.GlobalSession, WorkingDirectory = workingDirectory ?? Root, CreatedAt = createdAt, Title = "Notes fixture",
            });
            await Catalog.JournalStore.CreateSessionStore().UpsertSessionAsync(new AgentSessionSummary
            {
                SessionId = sessionId, ProviderId = new ModelProviderId("notes-provider"), ProviderKey = "notes-provider",
                ProtocolFamily = "test-protocol", WorkingDirectory = workingDirectory ?? Root, CreatedAt = createdAt, UpdatedAt = createdAt, Title = "Notes fixture",
            });
        }

        public async ValueTask DisposeAsync()
        {
            await Runtime.DisposeAsync();
            await Hub.DisposeAsync();
            Directory.Delete(Root, recursive: true);
        }
    }

    private sealed class ControlledReader : TextReader
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<string> Content { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override Task<string> ReadToEndAsync(CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            return Content.Task.WaitAsync(cancellationToken);
        }
    }

    private sealed class ForbiddenDiscovery : ISystemPromptContentLocator, ISkillRootProvider
    {
        public SystemPromptContentRoots GetRoots(SystemPromptDiscoveryContext context) => throw new AssertFailedException("No prompt discovery.");
        public string ResolveBuiltInPromptPath(string relativePromptPath) => throw new AssertFailedException("No prompt discovery.");
        public string ResolveBuiltInDocPath(string fileName) => throw new AssertFailedException("No documentation discovery.");
        public ValueTask<IReadOnlyList<SkillRootRegistration>> GetRootsAsync(SkillDiscoveryContext context, CancellationToken cancellationToken = default)
            => throw new AssertFailedException("No skill discovery.");
    }

    private sealed class ControlledCatalog : IAgentSessionCatalog
    {
        private int _calls;
        public bool Fail { get; init; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IAsyncEnumerable<AgentSessionMetadata> ListSessionsAsync(AgentSessionListFilter? filter = null, CancellationToken cancellationToken = default)
            => throw new AssertFailedException("Notes must use targeted backend lookup, not catalog loading or provider discovery.");
        public Task InvalidateAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public async Task InvalidateAsync(string sessionId, CancellationToken cancellationToken = default)
        {
            if (Fail) throw new IOException("Controlled invalidation failure.");
            if (Interlocked.Increment(ref _calls) == 1)
            {
                Entered.TrySetResult();
                await Release.Task;
            }
        }
        public Task NotifySessionCreatedAsync(string sessionId, CancellationToken cancellationToken = default) => throw new AssertFailedException("No session creation.");
        public Task NotifySessionResumedAsync(string sessionId, CancellationToken cancellationToken = default) => throw new AssertFailedException("No session resume.");
        public Task NotifySessionDeletedAsync(string sessionId, CancellationToken cancellationToken = default) => throw new AssertFailedException("No session deletion.");
        public Task NotifySessionUpdatedAsync(string sessionId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> DeleteSessionAsync(string sessionId, CancellationToken cancellationToken = default) => throw new AssertFailedException("No session deletion.");
    }

}
