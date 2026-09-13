namespace CodeAlta.Tests;

/// <summary>Strict literal-only restoration of twelve complete b4003d3c inputs; no reads or rebased anchors.</summary>
internal static class RuntimeFileSearchInvalidationSourceInverse
{
    private const string Runtime = "CodeAlta.Orchestration/Runtime/SessionRuntimeService.cs";
    private const string Host = "CodeAlta.Orchestration/Hosting/CodeAltaHost.cs";
    private const string Coordinator = "CodeAlta.Tui/App/SessionRuntimeEventCoordinator.cs";
    private const string InputInverse = "CodeAlta.Tests/OwnedSessionUserInputSourceInverse.cs";
    private const string ProjectionTests = "CodeAlta.Tests/PluginSessionEventProjectionSourceTests.cs";
    private const string Architecture = "CodeAlta.Tests/ArchitectureGuardrailTests.cs";
    private const string CoordinatorTests = "CodeAlta.Tests/SessionRuntimeEventCoordinatorTests.cs";
    private const string Project = "CodeAlta.Desktop.Tests/CodeAlta.Desktop.Tests.csproj";
    private const string Reminder = "CodeAlta.Tests/AltaReminderLifetimeSourceTests.cs";
    private const string Models = "CodeAlta.Tests/ModelsDevCatalogLifetimeSourceTests.cs";
    private const string OwnerInverse = "CodeAlta.Tests/OwnedSessionCommandSourceInverse.cs";
    private const string DesktopInverse = "CodeAlta.Tests/DesktopOwnedSessionSourceInverse.cs";
    internal static IReadOnlyList<string> Paths => [Runtime, Host, Coordinator, InputInverse, ProjectionTests, Architecture, CoordinatorTests, Project, Reminder, Models, OwnerInverse, DesktopInverse];
    internal static string RestoreInput(string path, string source) => Paths.Contains(path) ? Restore(path, source) : source;
    // Architecture is restored at the validated direct-read boundary. Reminder's own changed source
    // is restored only in dependent historical routes; do not pre-restore Host/Runtime here twice.
    internal static string RestoreArchitectureInput(string path, string source) => path == Architecture ? Restore(path, source) : source;
    internal static string RestoreLifetimeInput(string path, string source) => path is Architecture or Reminder ? Restore(path, source) : source;
    internal static string Restore(string path, string source)
    {
        source = SourceTestText.Canonicalize(source);
        foreach (var (before, after, count) in Edits(path))
        {
            Assert.IsTrue(after.Length > 0, path);
            Assert.AreEqual(count, source.Split(after, StringSplitOptions.None).Length - 1, path + ": " + after);
            source = source.Replace(after, before, StringComparison.Ordinal);
        }
        Assert.AreEqual(Original(path), OwnedSessionAskSourceInverse.GitObjectId(source), path);
        return source;
    }
    internal static string Original(string path) => path switch
    {
        Runtime => "fd56793fe8d61d8ebb7cce0c5425c3f0fcd2c653",
        Host => "e90a42aeb8584074095e9c641ffe8e6a176852ac",
        Coordinator => "7c146dbdc8f5a056f73ce90ce90f8bcb60e44b25",
        InputInverse => "64460770b21e3b20b2c77d55e0b498e9ebe32f5a",
        ProjectionTests => "4a57a9c43dcfea8c47c42325cafe1f7bc7faa2ec",
        Architecture => "d1f6f6dcba80008b3628ad058a4b2de22f1a479a",
        CoordinatorTests => "1b707949ece0822602f290f6208cc0f92a88f90b",
        Project => "9b1b4958a83962a849316b6b7147f488c08ed68e",
        Reminder => "1f1b736fdc649790b522ec03293f881911a8a416",
        Models => "f196c77ad6bc2bae423922f10db2f31d15241955",
        OwnerInverse => "9aa310d9b977018f2e623507e9e10c7c998357b8",
        DesktopInverse => "8385654b4f82dd6c1773433d33e4c5c29accd9d9",
        _ => throw new ArgumentException("Unlisted cache input.", nameof(path)),
    };
    private static IEnumerable<(string Before, string After, int Count)> Edits(string path)
    {
        switch (path)
        {
            case Runtime:
                yield return ("        => await AdmitAsync(() => AppendSessionEventOwnedBodyAsync(session, @event, cancellationToken), cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);\n", AppendEntry + "\n", 1);
                yield return ("    private async Task AppendSessionEventOwnedBodyAsync(SessionViewDescriptor session, AgentEvent @event, CancellationToken cancellationToken)\n", "    private async Task AppendSessionEventOwnedBodyAsync(SessionViewDescriptor session, AgentEvent @event, string effectWorkingDirectory, CancellationToken cancellationToken)\n", 1);
                yield return ("", "        await InvalidateFileSearchCacheAsync(@event, effectWorkingDirectory).ConfigureAwait(false);\n", 1);
                yield return ("            var parentNotifications = await actor.QueryAsync(_ =>\n", "            var projection = await actor.QueryAsync(_ =>\n", 1);
                yield return ("                    return ValueTask.FromResult(notifications);\n", "                    return ValueTask.FromResult((Event: sanitized, WorkingDirectory: projector.Entry.WorkingDirectory, Notifications: notifications));\n", 1);
                yield return ("            projectionUse.Dispose();\n            foreach (var notification in parentNotifications)\n", "            projectionUse.Dispose();\n            await InvalidateFileSearchCacheAsync(projection.Event, projection.WorkingDirectory).ConfigureAwait(false);\n            foreach (var notification in projection.Notifications)\n", 1);
                break;
            case Host:
                yield return ("", "            var projectFileSnapshotCache = new ProjectFileSnapshotCache();\n", 1);
                yield return ("                skillCatalog);\n            var projectFileSearchService", "                skillCatalog)\n            {\n                FileSearchCache = projectFileSnapshotCache,\n            };\n            var projectFileSearchService", 1);
                yield return ("                new ProjectFileSnapshotCache(),\n", "                projectFileSnapshotCache,\n", 1);
                break;
            case Coordinator:
                yield return ("    private readonly IProjectFileSearchService _projectFileSearchService;\n    private readonly Action<SessionViewDescriptor> _upsertRuntimeSession;", "    private readonly Action<SessionViewDescriptor> _upsertRuntimeSession;", 1);
                yield return ("        _projectFileSearchService = projectFileSearchService;\n", "        // Retain the validated constructor parameter for frontend composition compatibility only.\n        // Runtime owns live cache invalidation; this coordinator must not repeat it during history replay.\n", 1);
                yield return ("            InvalidateProjectFileSearchIfNeeded(session, agentRuntimeEvent.Event);\n            ObservePluginAgentEvent(session, agentRuntimeEvent.Event);", "\n            ObservePluginAgentEvent(session, agentRuntimeEvent.Event);", 1);
                yield return ("        InvalidateProjectFileSearchIfNeeded(session, @event);\n        ObservePluginAgentEvent(session, @event);", "\n        ObservePluginAgentEvent(session, @event);", 2);
                yield return (OldCacheHelpers + "\n\n    private void ApplyReduction(", "    private void ApplyReduction(", 1);
                break;
            case InputInverse:
                yield return ("", "        source = RuntimeFileSearchInvalidationSourceInverse.RestoreInput(path, source);\n", 1);
                break;
            case ProjectionTests:
                yield return ("        return SourceTestText.DecodeSource(File.ReadAllBytes(Path.Combine(directory.FullName, PluginStatisticsBackendSeparationSourceInverse.CurrentPath(path))));\n", "        return RuntimeFileSearchInvalidationSourceInverse.RestoreInput(path, SourceTestText.DecodeSource(File.ReadAllBytes(Path.Combine(directory.FullName, PluginStatisticsBackendSeparationSourceInverse.CurrentPath(path)))));\n", 1);
                break;
            case Architecture:
                yield return ("            \"App/SessionRuntimeEventCoordinator.cs:281:Task.Run(async () =>\",\n            \"App/SessionRuntimeEventCoordinator.cs:624:_ = InvalidateProjectFileSearchAsync(session.WorkingDirectory);\",\n", "            \"App/SessionRuntimeEventCoordinator.cs:281:Task.Run(async () =>\",\n", 1);
                break;
            case CoordinatorTests:
                yield return ("", "using CodeAlta.Orchestration.Runtime.SystemPrompts;\n", 1);
                yield return ("HandleAgentEvent_InvalidatesProjectFileSearchWhenFileChangesArrive", "HandleAgentEvent_DoesNotInvalidateRuntimeOwnedCacheWhenFileChangesArrive", 1);
                yield return ("        Assert.AreEqual(1, searchService.Invalidations.Count);\n        Assert.AreEqual(session.WorkingDirectory, searchService.Invalidations[0].ProjectRoot);\n        Assert.AreEqual(ProjectFileInvalidationReason.FileSystemWrite, searchService.Invalidations[0].Reason);", "        Assert.AreEqual(0, searchService.Invalidations.Count);", 1);
                yield return ("", HistoryFixture + "\n\n", 1);
                break;
            case Project:
                yield return ("", "    <Compile Include=\"../CodeAlta.Tests/RuntimeFileSearchInvalidationSourceInverse.cs\" Link=\"RuntimeFileSearchInvalidationSourceInverse.cs\" />\n", 1);
                break;
            case Reminder:
                yield return ("        return SourceTestText.DecodeSource(bytes);\n", "        return RuntimeFileSearchInvalidationSourceInverse.RestoreArchitectureInput(baseline.Path, SourceTestText.DecodeSource(bytes));\n", 1);
                break;
            case Models:
                yield return ("            var restored = CodeAltaStartupAdmissionSourceTests.RestoreCatalogRoute(baseline.Path, source.Text);\n", "            var historicalSource = RuntimeFileSearchInvalidationSourceInverse.RestoreArchitectureInput(baseline.Path, source.Text);\n            var restored = CodeAltaStartupAdmissionSourceTests.RestoreCatalogRoute(baseline.Path, historicalSource);\n", 1);
                break;
            case OwnerInverse:
                yield return ("    internal static string RestoreLifetimeInput(string path, string source)\n        => path is Lifetime ? RestoreCurrentInput(path, source) : source;\n", "    internal static string RestoreLifetimeInput(string path, string source)\n        => path is Lifetime ? RestoreCurrentInput(path, source) : RuntimeFileSearchInvalidationSourceInverse.RestoreLifetimeInput(path, source);\n", 1);
                break;
            case DesktopInverse:
                yield return ("", "        if (path == OwnerInverse) source = RuntimeFileSearchInvalidationSourceInverse.Restore(path, source);\n", 1);
                break;
            default: throw new ArgumentException("Unlisted cache input.", nameof(path));
        }
    }

    private const string AppendEntry = """
        {
            ArgumentNullException.ThrowIfNull(session);
            ArgumentNullException.ThrowIfNull(@event);
            var effectWorkingDirectory = session.WorkingDirectory;
            await AdmitAsync(() => AppendSessionEventOwnedBodyAsync(session, @event, effectWorkingDirectory, cancellationToken), cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    """;
    private const string OldCacheHelpers = """
        private void InvalidateProjectFileSearchIfNeeded(SessionViewDescriptor session, AgentEvent @event)
        {
            ArgumentNullException.ThrowIfNull(session);
            ArgumentNullException.ThrowIfNull(@event);

            if (!ShouldInvalidateProjectFileSearch(@event) ||
                string.IsNullOrWhiteSpace(session.WorkingDirectory))
            {
                return;
            }

            _ = InvalidateProjectFileSearchAsync(session.WorkingDirectory);
        }

        private async Task InvalidateProjectFileSearchAsync(string projectRoot)
        {
            try
            {
                await _projectFileSearchService.InvalidateAsync(projectRoot, ProjectFileInvalidationReason.FileSystemWrite);
            }
            catch
            {
            }
        }

        private static bool ShouldInvalidateProjectFileSearch(AgentEvent @event)
            => @event switch
            {
                AgentActivityEvent { Kind: AgentActivityKind.FileChange } => true,
                AgentSessionUpdateEvent { Kind: AgentSessionUpdateKind.DiffUpdated } => true,
                _ => false,
            };
    """;
    private const string HistoryFixture = """
        [TestMethod]
        public Task HistoryRebuild_DoesNotInvalidateCache_PreservesHistoryLoadingPluginObservation()
            => new HistoryCacheFixture().Run();

        // Cached LoadEarlierAsync exercises the real rebuild without providers, journal access, plugin
        // instances, or background projection. The fake observer returns already-completed tasks.
        private sealed class HistoryCacheFixture
        {
            private readonly string _root = Path.Combine(Path.GetTempPath(), "CodeAlta-cache-history-" + Guid.NewGuid().ToString("N"));
            private readonly List<Task> _originals = [];
            private readonly List<Task<Exception?>> _outcomes = [];
            private Task? _original;
            private Task<Exception?>? _outcome;
            private CodeAlta.Agent.ModelProviderRegistry? _registry;
            private CodeAlta.Orchestration.Runtime.AgentHub? _hub;
            private SessionRuntimeService? _runtime;

            internal async Task Run()
            {
                var launch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _original = Core(launch.Task); _outcome = Observe(_original); launch.SetResult();
                try { await _original.WaitAsync(TimeSpan.FromSeconds(30)); await _outcome; }
                catch (Exception ex) { ex.Data["RetainedFixture"] = this; ex.Data["RetainedRoot"] = _root; throw; }
                finally { Console.WriteLine("Retained cache history fixture root: " + _root); }
            }

            private async Task Core(Task launch)
            {
                await launch;
                Exception? primary = null, cleanup = null;
                try { await Keep(Exercise); }
                catch (Exception ex) { primary = ex; }
                // No held gates, external callbacks or cancellation dependencies: the original rebuild
                // must actually return before releasing its borrowed runtime. A deadline never releases it.
                try
                {
                    if (_runtime is not null) await Keep(() => _runtime.DisposeAsync().AsTask());
                    if (_hub is not null) await Keep(() => _hub.DisposeAsync().AsTask());
                    if (_registry is not null) await Keep(() => _registry.DisposeAsync().AsTask());
                }
                catch (Exception ex) { cleanup = ex; }
                await Task.WhenAll(_outcomes);
                if (primary is not null || cleanup is not null)
                {
                    var failure = new AggregateException(new[] { primary, cleanup }.OfType<Exception>());
                    failure.Data["Primary"] = primary; failure.Data["Cleanup"] = cleanup;
                    throw failure;
                }
            }

            private Task Keep(Func<Task> operation)
            {
                var launch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var original = Invoke(); _originals.Add(original); _outcomes.Add(Observe(original)); launch.SetResult();
                return original;
                async Task Invoke() { await launch.Task.ConfigureAwait(false); await operation().ConfigureAwait(false); }
            }
            private static async Task<Exception?> Observe(Task original)
            { try { await original.ConfigureAwait(false); return null; } catch (Exception ex) { return ex; } }

            private async Task Exercise()
            {
                for (var parent = new DirectoryInfo(Path.GetDirectoryName(_root)!); parent is not null; parent = parent.Parent)
                    if (parent.Exists && (parent.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Reparse fixture ancestry.");
                if (Directory.Exists(_root) || File.Exists(_root)) throw new IOException("Fixture root exists.");
                Directory.CreateDirectory(_root);
                var options = new CatalogOptions { GlobalRoot = Path.Combine(_root, "global") };
                var views = new SessionViewCatalog(options);
                var forbidden = new NoHistoryDiscovery();
                var skills = new CodeAlta.Catalog.Skills.SkillCatalog([forbidden]);
                _registry = new(); // Empty registry; no provider registration, probing or execution.
                _hub = new(_registry, options.GlobalRoot);
                _runtime = new(_hub, new CodeAlta.Agent.AgentSessionCatalog(views.JournalStore.CreateSessionStore()),
                    new ProjectCatalog(options), views,
                    new AgentInstructionTemplateProvider(skills, options, forbidden, null, new(Path.Combine(_root, "home"), _root)), options, skills);
                var session = CreateSession();
                session.WorkingDirectory = _root; session.ProviderId = "cache-history-inert"; session.ProviderKey = "cache-history-inert";
                session.Kind = SessionViewKind.GlobalSession; session.ProjectRef = null;
                var tab = CreateOpenSessionState(session);
                AgentEvent[] events =
                [
                    new AgentActivityEvent(new("cache-history-inert"), session.SessionId, DateTimeOffset.UnixEpoch, null,
                        AgentActivityKind.FileChange, AgentActivityPhase.Completed, "file", null, "fixture", "fixture"),
                    new AgentSessionUpdateEvent(new("cache-history-inert"), session.SessionId, DateTimeOffset.UnixEpoch, null, AgentSessionUpdateKind.DiffUpdated, null),
                ];
                tab.HistoryEvents = events.ToList();
                tab.Timeline.CreateTruncatedHistoryItem(1, static () => { });
                var search = new FakeProjectFileSearchService();
                var observer = new RecordingPluginAgentEventObserver();
                var coordinator = CreateCoordinator(session, tab, search, observer);
                var observations = new List<(AgentEvent? Event, bool Loading, bool ProjectionSuppressed)>();
                var projectedLoadedHistory = false;
                var history = new SessionHistoryCoordinator(_runtime, _ => tab, _ => session, _ => tab,
                    _ => true, (_, _) => new SessionExecutionOptions { ProviderId = new("cache-history-inert"), WorkingDirectory = _root, OnPermissionRequest = _runtime.Permissions.OwnedDefaultPermissionHandler },
                    (_, _, _, _) => { }, _ => { }, t => t.RenderedHistoryEvents.Clear(),
                    async (s, t, e) =>
                    {
                        await coordinator.HandleAgentEventAsync(s, t, e);
                        observations.Add((observer.ObservedEvent, t.HistoryLoading, observer.ProjectedEvents is null));
                    }, _ => Task.CompletedTask,
                    projectLoadedHistory: (_, _, loaded) => projectedLoadedHistory = loaded.SequenceEqual(events));
                await Keep(() => history.LoadEarlierAsync(session.SessionId));
                Assert.IsTrue(tab.HistoryLoaded);
                Assert.IsFalse(tab.HistoryLoading);
                Assert.IsTrue(projectedLoadedHistory);
                Assert.AreEqual(events.Length, observations.Count);
                for (var i = 0; i < events.Length; i++)
                {
                    Assert.AreSame(events[i], observations[i].Event);
                    Assert.IsTrue(observations[i].Loading);
                    Assert.IsTrue(observations[i].ProjectionSuppressed);
                }
                Assert.AreEqual(0, search.Invalidations.Count);
            }

            private sealed class NoHistoryDiscovery : CodeAlta.Catalog.Skills.ISkillRootProvider, ISystemPromptContentLocator
            {
                public ValueTask<IReadOnlyList<CodeAlta.Catalog.Skills.SkillRootRegistration>> GetRootsAsync(CodeAlta.Catalog.Skills.SkillDiscoveryContext context, CancellationToken cancellationToken = default)
                    => throw new AssertFailedException("No history skill discovery.");
                public SystemPromptContentRoots GetRoots(SystemPromptDiscoveryContext context) => throw new AssertFailedException("No history prompt discovery.");
                public string ResolveBuiltInPromptPath(string relativePromptPath) => throw new AssertFailedException("No history prompts.");
                public string ResolveBuiltInDocPath(string fileName) => throw new AssertFailedException("No history docs.");
            }
        }
    """;
}
