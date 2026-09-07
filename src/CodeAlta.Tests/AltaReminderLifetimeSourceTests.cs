using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;

namespace CodeAlta.Tests;

/// <summary>Source-only gates for the accepted returned-App reminder worker contract.</summary>
/// <remarks>
/// No production type or runtime core is invoked. History, Lazy sharing and actual admission,
/// pruning and cancellation atomicity are source-wiring obligations, not runtime evidence.
/// Fixed read map: tests 1-8 read the service once each; test 9 reads App/composition/Shell;
/// test 10 reads those four production files and five old guards; test 11 reads twelve frozen
/// routes. Thus a complete traversal has 32 reads of 21 unique named files (failures can stop
/// earlier). ReadSource is the sole filesystem operation: one File.ReadAllBytes per call,
/// resolved from this file's CallerFilePath to ../, never enumeration or ancestor discovery.
/// All remaining helpers operate on strings/bytes only. No guide, plan, config, home, artifact,
/// process, concrete owner, timer, CTS, provider, registry, UI, logger or fixture is activated.
/// Future functional additions and XML are exact literals; there is no XML stripping interval.
/// Whole-file inverses preserve old helper suffixes and OriginalShell, not merely substrings.
/// These tests require future production/guard changes; missing contracts are not success.
/// </remarks>
[TestClass]
public sealed class AltaReminderLifetimeSourceTests
{
    [TestMethod]
    public void Admission_RetainsBeforeOutsideGateInvocation()
    {
        var source = ReadSource(Service);
        RequireOnce(source, CreateAdmission);
        AssertBaseline(Invert(source, ServiceEdits()), Service);
    }

    [TestMethod]
    public void Publication_PublishesTheAssignedOriginal()
    {
        var source = ReadSource(Service);
        RequireOnce(source, NewEntry);
        RequireOnce(source, StartupCore);
        AssertBaseline(Invert(source, ServiceEdits()), Service);
    }

    [TestMethod]
    public void Pruning_RequiresCompletedOriginalAndRetainsFirstHistory()
    {
        var source = ReadSource(Service);
        RequireOnce(source, ObservationAndPruning);
        AssertBaseline(Invert(source, ServiceEdits()), Service);
    }

    [TestMethod]
    public void Stop_SnapshotsUnderGateAndDisablesPruning()
    {
        var source = ReadSource(Service);
        RequireOnce(source, DisposalMembers);
        AssertBaseline(Invert(source, ServiceEdits()), Service);
    }

    [TestMethod]
    public void SharedDisposal_UsesOneInstanceLazy()
    {
        var source = ReadSource(Service);
        RequireOnce(source, "        _disposeTask = new Lazy<Task>(DisposeCoreAsync);");
        RequireOnce(source, "    public ValueTask DisposeAsync() => new(_disposeTask.Value);");
        AssertBaseline(Invert(source, ServiceEdits()), Service);
    }

    [TestMethod]
    public void Cancellation_ClaimsOnceAndFencesWorkerRelease()
    {
        var source = ReadSource(Service);
        RequireOnce(source, CancellationAdapters);
        RequireOnce(source, WorkerFinally);
        AssertBaseline(Invert(source, ServiceEdits()), Service);
    }

    [TestMethod]
    public void MutationsAndWorkerChecks_PreserveTheScopedContract()
    {
        var source = ReadSource(Service);
        RequireCount(source, StoppedWorkerCheck, 3);
        RequireCount(source, MutationExceptionXml, 6);
        AssertBaseline(Invert(source, ServiceEdits()), Service);
    }

    [TestMethod]
    public void StaticCores_UseAcceptedValidationAndErrorPolicies()
    {
        var source = ReadSource(Service);
        RequireOnce(source, JoinCore);
        RequireOnce(source, StartupCore);
        RequireOnce(source, CancelCore);
        RequireOnce(source, ReleaseCore);
        AssertBaseline(Invert(source, ServiceEdits()), Service);
    }

    [TestMethod]
    public void Ownership_TransfersToShellAndUsesBestEffortCleanup()
    {
        var app = ReadSource(App);
        Assert.AreEqual(47_026, Encode(app, App).Length);
        Assert.IsTrue(Encode(app, App).Length < 47_064);
        AssertBaseline(Invert(app, AppEdits()), App);
        var composition = ReadSource(Composition);
        AssertBaseline(Invert(composition, CompositionEdits()), Composition);
        var shell = ReadSource(Shell);
        RequireOnce(shell, ShellAdapterAndCore);
        RequireOnce(shell, RenamedShellTraversal);
        AssertBaseline(Invert(shell, ShellEdits()), Shell);
    }

    [TestMethod]
    public void Preservation_InvertsOnlyApprovedProductionAndGuardChanges()
    {
        AssertBaseline(Invert(ReadSource(Service), ServiceEdits()), Service);
        AssertBaseline(Invert(ReadSource(Shell), ShellEdits()), Shell);
        AssertBaseline(Invert(ReadSource(App), AppEdits()), App);
        AssertBaseline(Invert(ReadSource(Composition), CompositionEdits()), Composition);
        AssertBaseline(Invert(ReadSource(FrontendGuard), FrontendGuardEdits()), FrontendGuard);
        AssertBaseline(Invert(ReadSource(WorkspaceGuard), WorkspaceGuardEdits()), WorkspaceGuard);
        AssertBaseline(Invert(ReadSource(DeferredGuard), DeferredGuardEdits()), DeferredGuard);
        AssertBaseline(Invert(ReadSource(PromptGuard), PromptGuardEdits()), PromptGuard);
        AssertBaseline(Invert(ReadSource(ArchitectureGuard), ArchitectureEdits()), ArchitectureGuard);
    }

    [TestMethod]
    public void Preservation_UnchangedRuntimeAndFrontendRoutesRemainFrozen()
    {
        // This case is independent of missing future members and can pass the old baseline.
        // Checksums cover entire files; the bounded contributor literals also expose the
        // unchanged ownerless fallback and detached-send limits directly to review.
        foreach (var baseline in FrozenRoutes())
        {
            var source = ReadSource(baseline);
            AssertBaseline(source, baseline);
            if (string.Equals(baseline.Path,
                "CodeAlta.LiveTool/BuiltInAltaCommandContributor.cs", StringComparison.Ordinal))
            {
                RequireOnce(source, FallbackRoute);
                RequireOnce(source, DetachedSendRoute);
                RequireOnce(source, DetachedObserverRoute);
            }
        }
    }

    private readonly record struct Baseline(string Path, int Bytes, int Lines, bool CrLf, string Hash);
    private readonly record struct Edit(string Before, string After, int Count = 1);

    private static Baseline Service => new("CodeAlta.LiveTool/AltaReminderService.cs", 27562, 623, true,
        "4D1F9860E82357690576FB398661A9307D2EDD4CB62DE08E3F5558F494B146C0");
    private static Baseline Shell => new("CodeAlta.Tui/App/ShellFrontendHost.cs", 6324, 189, true,
        "0232FC562532CB7554EEC548694E73488D271013C56ECE0689B429D7A609EB0E");
    private static Baseline App => new("CodeAlta.Tui/App/CodeAltaApp.cs", 46998, 810, true,
        "8C3D9111E9DD35C9E3442D15F873570A4EE55BB26750B2FAB832D9C5446F9A72");
    private static Baseline Composition => new("CodeAlta.Tui/App/CodeAltaFrontendComposition.cs", 28803, 529, true,
        "41331F9161D3D5C0D7BDC2EDE2BEFDCE733C0493E7CCE0ECC98211F784737DC4");
    private static Baseline FrontendGuard => new("CodeAlta.Tests/CodeAltaFrontendCleanupSourceTests.cs", 20722, 441, false,
        "F651116A74B06D7BB07137F21F8B221C0C8152526CDD46D90C4FCDBE635F089F");
    private static Baseline WorkspaceGuard => new("CodeAlta.Tests/FileEditorWorkspaceCleanupSourceTests.cs", 63586, 1176, false,
        "792902BE71FEF30908CF9EBCE8ACA9406EE68A8EC945212C0442DE9D916F6D6D");
    private static Baseline DeferredGuard => new("CodeAlta.Tests/DeferredCodeAltaAppSourceTests.cs", 27695, 493, false,
        "FBACA2F7CB84947AB049B4A27A814E4B5027E1F836BE77E1077DCB052262226A");
    private static Baseline PromptGuard => new("CodeAlta.Tests/PromptDraftPrerequisiteSourceTests.cs", 53845, 1099, false,
        "F9B5614B005895F4DFA087A288855541811AF95653452486694DAF62BA710A50");
    private static Baseline ArchitectureGuard => new("CodeAlta.Tests/ArchitectureGuardrailTests.cs", 147905, 2320, true,
        "CFCED866C9471AA655F867C16C801E4B4CE756DB5B89B627F2DBED01CD7ECC51");

    private static Baseline[] FrozenRoutes() =>
    [
        new("CodeAlta.Tui/App/ReminderUiCoordinator.cs", 2576, 85, false,
            "71DEB269F18D4E5C9F034E4CB9366E0562B2BE2DC3D7D762C2726E68EDF9C486"),
        new("CodeAlta.LiveTool/AltaServiceCollection.cs", 997, 38, false,
            "592373C9FEE130D77CBD89D5C09E571330B0A3B33A1FCBA5E449D4485015D7E5"),
        new("CodeAlta.LiveTool/BuiltInAltaCommandContributor.cs", 254499, 5509, true,
            "38B0BB7AD266F02243E0B5C4D41AC336B8BAB39310FC6BD232C47B4FEEAA6130"),
        new("CodeAlta.Tui/Program.cs", 21957, 500, true,
            "46C722923838E1D25BE806A1C39684F20FA53261A1DD744DB45707E17A9BD2D3"),
        new("CodeAlta.Tui/Views/DeferredCodeAltaApp.cs", 20372, 522, true,
            "2A2D27C0A854DD1FCD77E113FD00167C33A64EF6CEE7DBDD9DD182BCDFD2FFA6"),
        new("CodeAlta.Tui/App/CodeAltaOwnedServices.cs", 17484, 379, true,
            "D2AD29EEF926D2D5F3BF6E99367D2D4C67F9C14BC4855ECD508D1676D49CBE61"),
        new("CodeAlta.Orchestration/Hosting/CodeAltaHost.cs", 23754, 543, true,
            "C806B9F1F2BC0B8D3CA0DF881159A85053BD1F4B64964B8563C702FDA086CBCA"),
        new("CodeAlta.Tui/App/SessionPromptDraftPersistenceCoordinator.cs", 13375, 350, false,
            "E79370DFDB435E537718188BFE78C5821A5F2EDFB067086DD60077A3BA6A2482"),
        new("CodeAlta.Tui/Views/FileEditorWorkspaceCoordinator.cs", 15495, 358, true,
            "EA84A794E41DDF2F8652C653B9AC6CD1985C33B99F980071DE79AAEC2E0FA5A1"),
        new("CodeAlta.Tui/App/CodeAltaShellController.cs", 30242, 698, true,
            "030B411FA8A24CEC10300C40962B08E58270E48F0AA8C37BD14BABE29EEA9A97"),
        new("CodeAlta.Tui/App/RuntimeEventPump.cs", 7120, 173, true,
            "871BF59F3145096F98308E40A71A896642F6A65C3BCD9A7B791D3ECA8A02C645"),
        new("CodeAlta.Tests/RuntimeEventPumpSourceTests.cs", 27444, 566, false,
            "94DAF88C49D933909A332D0DB0FFA341370CCD996BCEBC7501F7756C28BFF46E"),
    ];

    private static Edit[] ServiceEdits()
    {
        const string entries = "    private readonly Dictionary<string, ReminderEntry> _entries = new(StringComparer.OrdinalIgnoreCase);";
        const string record = "    private sealed record NotificationContext(";
        const string createDescriptorEnd = "            ContentPreview = CreatePreview(request.Content),\n        };";
        const string deleteClock = "        NotificationContext context;\n        var now = _timeProvider.GetUtcNow();";
        const string deleteGate = "        lock (_gate)\n        {\n            if (!_entries.Remove(reminderId, out entry))";
        const string updateGate = "        NotificationContext context;\n        lock (_gate)\n        {\n            if (!_entries.TryGetValue(reminderId, out var entry))";
        var edits = new List<Edit>
        {
            new("using System.Globalization;", "using System.Globalization;\nusing System.Runtime.ExceptionServices;"),
            new("public sealed class AltaReminderService\n", "public sealed class AltaReminderService : IAsyncDisposable\n"),
            new(entries, entries + "\n" + WorkerFields),
            new("        _timeProvider = timeProvider;", "        _timeProvider = timeProvider;\n        _disposeTask = new Lazy<Task>(DisposeCoreAsync);"),
            new(record, LifetimeBlock + "\n\n" + record),
            new(OldEntry, NewEntry),
            new("        var now = _timeProvider.GetUtcNow();\n        var descriptor = new AltaReminderDescriptor",
                InitialStoppedCheck + "\n\n        var now = _timeProvider.GetUtcNow();\n        var descriptor = new AltaReminderDescriptor"),
            new(createDescriptorEnd + "\n\n" + OldCreateAdmission, createDescriptorEnd + "\n\n" + CreateAdmission),
            new(deleteClock, "        NotificationContext context;\n" + InitialStoppedCheck + "\n        var now = _timeProvider.GetUtcNow();"),
            new(deleteGate, "        lock (_gate)\n        {\n" + MutationGateCheck + "\n            if (!_entries.Remove(reminderId, out entry))"),
            new("        entry.Cancel();", DeleteCancellation),
            new(updateGate, "        NotificationContext context;\n        lock (_gate)\n        {\n" + MutationGateCheck + "\n            if (!_entries.TryGetValue(reminderId, out var entry))"),
            new("            return _lastNotificationFailure;", "            PruneCompletedWorkersUnderGate();\n            return _lastNotificationFailure;"),
            new("            return _entries.Values", "            PruneCompletedWorkersUnderGate();\n            return _entries.Values"),
            new("            if (_entries.TryGetValue(reminderId, out var entry))", "            PruneCompletedWorkersUnderGate();\n            if (_entries.TryGetValue(reminderId, out var entry))"),
            new(OldWorkerCheck, StoppedWorkerCheck, 3),
            new("        finally\n        {\n            entry.DisposeCancellation();\n        }", WorkerFinally),
            new("    /// not ordered event replay. Publishing diagnostics does not raise <see cref=\"Changed\" />.",
                "    /// not ordered event replay. Publishing diagnostics does not raise <see cref=\"Changed\" />.\n    /// Worker disposal leaves queries available; retained reminder descriptors can be historical."),
            new("    public IReadOnlyList<AltaReminderDescriptor> List(", QueryRemarks + "\n    public IReadOnlyList<AltaReminderDescriptor> List("),
            new("    public bool TryGetContent(", QueryRemarks + "\n    public bool TryGetContent("),
            new("    /// deletion does not retract queued prompts or abort a run.",
                "    /// deletion does not retract queued prompts or abort a run.\n" + DeleteRaceRemarks),
            new("    /// <remarks>An already captured delivery may still send; deletion does not retract queued work or abort a run.</remarks>",
                "    /// <remarks>\n    /// An already captured delivery may still send; deletion does not retract queued work or abort a run.\n" +
                DeleteRaceRemarks + "\n    /// </remarks>"),
        };
        // Each exact signature includes its whole parameter list: no prefix matching of overloads.
        foreach (var signature in MutationSignatures())
        {
            edits.Add(new Edit(signature, MutationExceptionXml + "\n" + signature));
        }

        return edits.ToArray();
    }

    private static string[] MutationSignatures() =>
    [
        "    public AltaReminderDescriptor Create(AltaReminderCreateRequest request)",
        "    public AltaReminderDescriptor Create(AltaReminderCreateRequest request, out AltaReminderNotificationFailure? notificationFailure)",
        "    public bool TryDelete(string reminderId, out AltaReminderDescriptor? descriptor)",
        "    public bool TryDelete(string reminderId, out AltaReminderDescriptor? descriptor, out AltaReminderNotificationFailure? notificationFailure)",
        "    public bool TryUpdateContent(string reminderId, string content, out AltaReminderDescriptor? descriptor)",
        "    public bool TryUpdateContent(string reminderId, string content, out AltaReminderDescriptor? descriptor, out AltaReminderNotificationFailure? notificationFailure)",
    ];

    private const string MutationExceptionXml = "    /// <exception cref=\"ObjectDisposedException\">The service has started worker disposal.</exception>";
    private const string QueryRemarks = "    /// <remarks>Queries remain available after worker disposal; retained Active descriptors can be historical and do not imply scheduling.</remarks>";
    private const string DeleteRaceRemarks = """
        /// Cancellation is requested at most once across deletion and worker disposal. A losing
        /// deletion neither retries nor waits for the winner and does not receive its later error.
        /// Synchronous deletion notifications are not drained by worker disposal.
    """;
    private const string WorkerFields = "    private readonly List<ReminderEntry> _workers = [];\n    private readonly Lazy<Task> _disposeTask;\n    private bool _stopping;\n    private Exception? _retiredFailure;";
    private const string MutationGateCheck = "            PruneCompletedWorkersUnderGate();\n            ObjectDisposedException.ThrowIf(_stopping, this);";
    private const string InitialStoppedCheck = "        lock (_gate)\n        {\n" + MutationGateCheck + "\n        }";
    private const string OldWorkerCheck = "                    if (!ReferenceEquals(_entries.GetValueOrDefault(entry.Descriptor.ReminderId), entry) || entry.IsCancellationRequested)";
    private const string StoppedWorkerCheck = "                    if (_stopping ||\n                        !ReferenceEquals(_entries.GetValueOrDefault(entry.Descriptor.ReminderId), entry) ||\n                        entry.IsCancellationRequested)";

    private const string OldCreateAdmission = """
            var entry = new ReminderEntry(descriptor, request.Content);
            NotificationContext context;
            lock (_gate)
            {
                _entries.Add(descriptor.ReminderId, entry);
                context = CaptureNotification(descriptor, AltaReminderChangeKind.Created);
            }

            _ = RunReminderAsync(entry);
            notificationFailure = OnChanged(context);
            lock (_gate)
            {
                return entry.Descriptor;
            }
    """;

    private const string CreateAdmission = """
            var entry = new ReminderEntry(this, descriptor, request.Content);
            NotificationContext context;
            var admitted = false;
            try
            {
                lock (_gate)
                {
                    PruneCompletedWorkersUnderGate();
                    ObjectDisposedException.ThrowIf(_stopping, this);

                    var addedToEntries = false;
                    var addedToWorkers = false;
                    try
                    {
                        _entries.Add(descriptor.ReminderId, entry);
                        addedToEntries = true;
                        _workers.Add(entry);
                        addedToWorkers = true;
                        context = CaptureNotification(descriptor, AltaReminderChangeKind.Created);
                        admitted = true;
                    }
                    catch
                    {
                        if (addedToWorkers)
                        {
                            _workers.RemoveAt(_workers.Count - 1);
                        }

                        if (addedToEntries)
                        {
                            _entries.Remove(descriptor.ReminderId);
                        }

                        throw;
                    }
                }
            }
            finally
            {
                if (!admitted)
                {
                    entry.DisposeCancellation();
                }
            }

            StartAndPublishReminderWorker(entry.StartAndRetainOriginal, entry.Publish);
            notificationFailure = OnChanged(context);
            lock (_gate)
            {
                return entry.Descriptor;
            }
    """;

    private const string DeleteCancellation = """
            var cancellationFailure = RequestCancellation(entry);
            if (cancellationFailure is not null)
            {
                ExceptionDispatchInfo.Throw(cancellationFailure);
            }
    """;

    private const string WorkerFinally = """
            finally
            {
                Task cancellationReturned;
                lock (_gate)
                {
                    entry.SourceClosing = true;
                    cancellationReturned = entry.CancellationIssued
                        ? entry.CancellationReturned.Task
                        : Task.CompletedTask;
                }

                await FinishReminderSourceAsync(
                    cancellationReturned, entry.ReleaseSource).ConfigureAwait(false);
            }
    """;

    private static string LifetimeBlock => string.Join("\n\n",
        DisposalMembers, JoinCore, StartupCore, CancelCore, ReleaseCore,
        CancellationAdapters, ObservationAndPruning);

    private const string DisposalMembers = """
        /// <summary>
        /// Stops new reminder-worker admission and capture, and joins retained published workers.
        /// </summary>
        /// <remarks>
        /// Repeated calls share one disposal task, snapshot and cancellation pass, including its
        /// terminal outcome. Recursive disposal and callbacks waiting for their own disposal
        /// are unsupported and can throw or deadlock. There is no termination timeout.
        ///
        /// This disposes worker resources, not all concurrent service activity. The join includes
        /// each selected worker's returned delivery, worker-fired observer pass and finally.
        /// Pre-admission preparation, rejected local acquisition cleanup and synchronous CRUD
        /// notification passes can outlive disposal. Enclosing command/output work, posted UI
        /// closures, dialogs and detached send/provider/queue descendants are not joined.
        ///
        /// Valid mutations reject after stopping; queries remain available. Stopping itself
        /// synthesizes no descriptor changes or events, so retained Active descriptors can be
        /// historical rather than evidence of continued scheduling. Ordinary completed reminder
        /// descriptors remain retained. Only the first failure retired through opportunistic
        /// pruning is retained for disposal; this is not exhaustive historical failure replay.
        /// Completed deleted worker graphs may remain until an existing-call pruning opportunity;
        /// this is not eager reclamation or an absolute byte bound. Failed disposal can retain
        /// its finite stopped worker snapshot.
        ///
        /// Invocation/publication/snapshot allocation failures do not certify termination of
        /// unknown work. This operation does not establish full service quietness or preserve
        /// externally disposed dependencies.
        /// </remarks>
        /// <returns>The shared worker-disposal operation.</returns>
        /// <exception cref="Exception">A lone observed cleanup failure is rethrown.</exception>
        /// <exception cref="OperationCanceledException">The sole observed failure is cancellation.</exception>
        /// <exception cref="AggregateException">Multiple direct failures are reported in order.</exception>
        public ValueTask DisposeAsync() => new(_disposeTask.Value);

        private async Task DisposeCoreAsync()
        {
            ReminderWorkerJoin[] workers;
            Exception? retiredFailure;
            lock (_gate)
            {
                _stopping = true;
                workers = new ReminderWorkerJoin[_workers.Count];
                for (var index = 0; index < workers.Length; index++)
                {
                    workers[index] = _workers[index].Join;
                }

                retiredFailure = _retiredFailure;
            }

            await DisposeReminderWorkersAsync(workers, retiredFailure).ConfigureAwait(false);

            lock (_gate)
            {
                _workers.Clear();
            }
        }
    """;

    private const string JoinCore = """
        internal readonly record struct ReminderWorkerJoin(
            Func<Exception?> RequestCancellation,
            Task<Task> Publication,
            Func<Exception?> ReadCancellationFailure);

        /// <summary>Snapshots supplied join inputs, requests cancellation, then joins actual originals.</summary>
        /// <remarks>
        /// Shallow-copy once before ordered validation and callbacks; concurrent mutation during
        /// the copy is not made atomic. Null originals fail. Readers cannot replace worker errors.
        /// Direct order is history, indexed cancellation/read errors, indexed publication/worker
        /// errors. No flattening or deduplication. A sole observed OCE can cancel this operation
        /// even when its input task was faulted. Pending work can prevent further joins.
        /// </remarks>
        /// <exception cref="ArgumentNullException">The input array is null.</exception>
        /// <exception cref="ArgumentException">An indexed mandatory member is null.</exception>
        /// <exception cref="Exception">A lone observed failure is rethrown through EDI.</exception>
        /// <exception cref="AggregateException">Multiple direct failures are retained in order.</exception>
        internal static Task DisposeReminderWorkersAsync(
            ReminderWorkerJoin[] workers,
            Exception? retiredFailure)
        {
            ArgumentNullException.ThrowIfNull(workers);

            var snapshot = (ReminderWorkerJoin[])workers.Clone();
            for (var index = 0; index < snapshot.Length; index++)
            {
                if (snapshot[index].RequestCancellation is null)
                {
                    throw new ArgumentException(
                        $"Worker {index} has no cancellation operation.", nameof(workers));
                }

                if (snapshot[index].Publication is null)
                {
                    throw new ArgumentException(
                        $"Worker {index} has no publication task.", nameof(workers));
                }

                if (snapshot[index].ReadCancellationFailure is null)
                {
                    throw new ArgumentException(
                        $"Worker {index} has no cancellation-failure reader.", nameof(workers));
                }
            }

            var cancellationFailures = new Exception?[snapshot.Length];
            var workerFailures = new Exception?[snapshot.Length];
            var failures = new List<Exception>(checked(snapshot.Length * 2 + 1));
            return CoreAsync();

            async Task CoreAsync()
            {
                for (var index = 0; index < snapshot.Length; index++)
                {
                    try
                    {
                        cancellationFailures[index] = snapshot[index].RequestCancellation();
                    }
                    catch (Exception ex)
                    {
                        cancellationFailures[index] = ex;
                    }
                }

                for (var index = 0; index < snapshot.Length; index++)
                {
                    try
                    {
                        var original = await snapshot[index].Publication.ConfigureAwait(false);
                        if (original is null)
                        {
                            throw new InvalidOperationException(
                                $"Worker {index} published a null original task.");
                        }

                        await original.ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        workerFailures[index] = ex;
                    }

                    if (cancellationFailures[index] is null)
                    {
                        try
                        {
                            cancellationFailures[index] =
                                snapshot[index].ReadCancellationFailure();
                        }
                        catch (Exception ex)
                        {
                            cancellationFailures[index] = ex;
                        }
                    }
                }

                if (retiredFailure is not null)
                {
                    failures.Add(retiredFailure);
                }

                foreach (var failure in cancellationFailures)
                {
                    if (failure is not null)
                    {
                        failures.Add(failure);
                    }
                }

                foreach (var failure in workerFailures)
                {
                    if (failure is not null)
                    {
                        failures.Add(failure);
                    }
                }

                if (failures.Count == 1)
                {
                    ExceptionDispatchInfo.Throw(failures[0]);
                }

                if (failures.Count > 1)
                {
                    throw new AggregateException(failures);
                }
            }
        }
    """;

    private const string StartupCore = """
        /// <summary>Runs the original-retaining action before its publication-completion action.</summary>
        /// <remarks>
        /// Mandatory actions validate synchronously. Completion failure supersedes start failure;
        /// otherwise a start failure is rethrown through EDI. Completion failure is not publication
        /// or proof of termination. The production start action assigns the actual original.
        /// </remarks>
        /// <exception cref="ArgumentNullException">A mandatory action is null.</exception>
        /// <exception cref="Exception">Completion failure, or the earlier start failure, escapes.</exception>
        internal static void StartAndPublishReminderWorker(
            Action startAndRetainOriginal,
            Action<Exception?> completePublication)
        {
            ArgumentNullException.ThrowIfNull(startAndRetainOriginal);
            ArgumentNullException.ThrowIfNull(completePublication);

            Exception? failure = null;
            try
            {
                startAndRetainOriginal();
            }
            catch (Exception ex)
            {
                failure = ex;
            }

            completePublication(failure);

            if (failure is not null)
            {
                ExceptionDispatchInfo.Throw(failure);
            }
        }
    """;

    private const string CancelCore = """
        /// <summary>Reports cancellation traversal return to the private completion action.</summary>
        /// <remarks>
        /// Completion failure supersedes cancellation failure and does not certify a return
        /// fence or permit release. Successful completion returns the cancellation error or null.
        /// A blocking traversal is unbounded; no ordinary throwing private registration is assumed.
        /// </remarks>
        /// <exception cref="ArgumentNullException">A mandatory action is null.</exception>
        /// <exception cref="Exception">The completion action throws.</exception>
        internal static Exception? CancelAndComplete(
            Action cancel,
            Action<Exception?> complete)
        {
            ArgumentNullException.ThrowIfNull(cancel);
            ArgumentNullException.ThrowIfNull(complete);

            Exception? failure = null;
            try
            {
                cancel();
            }
            catch (Exception ex)
            {
                failure = ex;
            }

            complete(failure);
            return failure;
        }
    """;

    private const string ReleaseCore = """
        /// <summary>Releases the worker source only after a successful traversal-return fence.</summary>
        /// <exception cref="ArgumentNullException">A mandatory input is null.</exception>
        /// <exception cref="Exception">The fence or release action fails; release is not retried.</exception>
        internal static Task FinishReminderSourceAsync(
            Task cancellationReturned,
            Action releaseSource)
        {
            ArgumentNullException.ThrowIfNull(cancellationReturned);
            ArgumentNullException.ThrowIfNull(releaseSource);
            return CoreAsync();

            async Task CoreAsync()
            {
                await cancellationReturned.ConfigureAwait(false);
                releaseSource();
            }
        }
    """;

    private const string CancellationAdapters = """
        private Exception? RequestCancellation(ReminderEntry entry)
        {
            lock (_gate)
            {
                if (entry.SourceClosing || entry.CancellationIssued)
                {
                    return null;
                }

                entry.CancellationIssued = true;
            }

            return CancelAndComplete(entry.CancelSource, entry.CancellationCompleted);
        }

        private void CompleteCancellation(ReminderEntry entry, Exception? failure)
        {
            lock (_gate)
            {
                entry.CancellationFailure = failure;
            }

            entry.CancellationReturned.TrySetResult();
        }

        private Exception? ReadCancellationFailure(ReminderEntry entry)
        {
            lock (_gate)
            {
                return entry.CancellationFailure;
            }
        }
    """;

    private const string ObservationAndPruning = """
        /// <summary>Synchronously observes only an actually completed supplied original.</summary>
        /// <remarks>Pending tasks return false without GetResult; this core does not mutate history.</remarks>
        /// <exception cref="ArgumentNullException">The original is null.</exception>
        internal static bool TryObserveCompletedReminder(
            Task original,
            out Exception? failure)
        {
            ArgumentNullException.ThrowIfNull(original);
            failure = null;
            if (!original.IsCompleted)
            {
                return false;
            }

            try
            {
                original.GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                failure = ex;
            }

            return true;
        }

        private void PruneCompletedWorkersUnderGate()
        {
            if (_stopping)
            {
                return;
            }

            for (var index = 0; index < _workers.Count;)
            {
                var entry = _workers[index];
                if (!entry.Publication.Task.IsCompletedSuccessfully ||
                    entry.Original is not { } original)
                {
                    index++;
                    continue;
                }

                if (!TryObserveCompletedReminder(original, out var workerFailure))
                {
                    index++;
                    continue;
                }

                _retiredFailure ??= entry.CancellationFailure ?? workerFailure;
                _workers.RemoveAt(index);
            }
        }
    """;

    private const string OldEntry = """
        private sealed class ReminderEntry(AltaReminderDescriptor descriptor, string content)
        {
            private readonly CancellationTokenSource _cancellation = new();

            public AltaReminderDescriptor Descriptor { get; set; } = descriptor;

            public string Content { get; set; } = content;

            public CancellationToken CancellationToken => _cancellation.Token;

            public bool IsCancellationRequested => _cancellation.IsCancellationRequested;

            public void Cancel()
            {
                try
                {
                    _cancellation.Cancel();
                }
                catch (ObjectDisposedException)
                {
                }
            }

            public void DisposeCancellation() => _cancellation.Dispose();
        }
    """;

    private const string NewEntry = """
        private sealed class ReminderEntry
        {
            private readonly CancellationTokenSource _cancellation;

            public ReminderEntry(
                AltaReminderService owner,
                AltaReminderDescriptor descriptor,
                string content)
            {
                ArgumentNullException.ThrowIfNull(owner);
                ArgumentNullException.ThrowIfNull(descriptor);
                ArgumentNullException.ThrowIfNull(content);

                Descriptor = descriptor;
                Content = content;
                Publication = new TaskCompletionSource<Task>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                CancellationReturned = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);

                StartAndRetainOriginal = () => Original = owner.RunReminderAsync(this);
                Publish = CompletePublication;
                CancelSource = Cancel;
                ReleaseSource = DisposeCancellation;
                CancellationCompleted = failure => owner.CompleteCancellation(this, failure);
                Join = new ReminderWorkerJoin(
                    () => owner.RequestCancellation(this),
                    Publication.Task,
                    () => owner.ReadCancellationFailure(this));

                // Last acquisition: no subsequent adapter/signal allocation before return.
                _cancellation = new CancellationTokenSource();
            }

            public AltaReminderDescriptor Descriptor { get; set; }

            public string Content { get; set; }

            public Task? Original { get; set; }

            public TaskCompletionSource<Task> Publication { get; }

            public TaskCompletionSource CancellationReturned { get; }

            public bool CancellationIssued { get; set; }

            public bool SourceClosing { get; set; }

            public Exception? CancellationFailure { get; set; }

            public Action StartAndRetainOriginal { get; }

            public Action<Exception?> Publish { get; }

            public Action CancelSource { get; }

            public Action ReleaseSource { get; }

            public Action<Exception?> CancellationCompleted { get; }

            public ReminderWorkerJoin Join { get; }

            public CancellationToken CancellationToken => _cancellation.Token;

            public bool IsCancellationRequested => _cancellation.IsCancellationRequested;

            private void CompletePublication(Exception? failure)
            {
                if (Original is { } original)
                {
                    Publication.TrySetResult(original);
                }
                else
                {
                    Publication.TrySetException(failure ?? new InvalidOperationException(
                        "Reminder invocation returned without an original task."));
                }
            }

            public void Cancel()
            {
                try
                {
                    _cancellation.Cancel();
                }
                catch (ObjectDisposedException)
                {
                }
            }

            public void DisposeCancellation() => _cancellation.Dispose();
        }
    """;

    private static Edit[] AppEdits() =>
    [
        new("            this,\n            ownedServices?.CurrentProject,",
            "            this,\n            _frontendHost,\n            ownedServices?.CurrentProject,"),
    ];

    private static Edit[] CompositionEdits() =>
    [
        new("        CodeAltaApp frontend,", "        CodeAltaApp frontend,\n        ShellFrontendHost reminderOwner,"),
        new("        ArgumentNullException.ThrowIfNull(frontend);",
            "        ArgumentNullException.ThrowIfNull(frontend);\n        ArgumentNullException.ThrowIfNull(reminderOwner);"),
        new("        var reminderService = new AltaReminderService(altaServices);",
            "        var reminderService = new AltaReminderService(altaServices);\n        reminderOwner.OwnReminders(reminderService);"),
    ];

    private static Edit[] ShellEdits() =>
    [
        new("using System.Runtime.ExceptionServices;", "using System.Runtime.ExceptionServices;\nusing CodeAlta.LiveTool;"),
        new("    private readonly IShellFrontendHostLifecycle _lifecycle;",
            "    private readonly IShellFrontendHostLifecycle _lifecycle;\n    private AltaReminderService? _reminders;"),
        new("    public async Task RunAsync(", ShellOwnership + "\n\n    public async Task RunAsync("),
        new(OldShellTraversal, ShellAdapterAndCore + "\n\n" + RenamedShellTraversal),
    ];

    private const string ShellOwnership = """
        internal void OwnReminders(AltaReminderService reminders)
        {
            ArgumentNullException.ThrowIfNull(reminders);
            if (_reminders is not null)
            {
                throw new InvalidOperationException("Reminder ownership already transferred.");
            }

            _reminders = reminders;
        }
    """;

    private const string ShellAdapterAndCore = """
        public async ValueTask DisposeAsync()
            => await DisposeRemindersThenFrontendAsync(
                () => _reminders?.DisposeAsync() ?? ValueTask.CompletedTask,
                DisposeFrontendAndOwnedServicesAsync);

        internal static Task DisposeRemindersThenFrontendAsync(
            Func<ValueTask> disposeReminders,
            Func<ValueTask> disposeExisting)
        {
            ArgumentNullException.ThrowIfNull(disposeReminders);
            ArgumentNullException.ThrowIfNull(disposeExisting);
            return CoreAsync();

            async Task CoreAsync()
            {
                Exception? reminderFailure = null;
                try
                {
                    await disposeReminders();
                }
                catch (Exception ex)
                {
                    reminderFailure = ex;
                }

                try
                {
                    await disposeExisting();
                }
                catch (Exception ex) when (reminderFailure is not null)
                {
                    throw new AggregateException(reminderFailure, ex);
                }

                if (reminderFailure is not null)
                {
                    ExceptionDispatchInfo.Throw(reminderFailure);
                }
            }
        }
    """;

    private const string OldShellTraversal = """
        public async ValueTask DisposeAsync()
        {
            Exception? frontendFailure = null;
            try
            {
                await _lifecycle.DisposeFrontendAsync();
            }
            catch (Exception ex)
            {
                frontendFailure = ex;
            }

            // A failed draft acknowledgement must not abandon runtime/provider/plugin ownership.
            try
            {
                if (_lifecycle.OwnedServices is { } ownedServices)
                {
                    await ownedServices.DisposeAsync();
                }
            }
            catch (Exception ex) when (frontendFailure is not null)
            {
                throw new AggregateException(frontendFailure, ex);
            }

            if (frontendFailure is not null)
            {
                ExceptionDispatchInfo.Throw(frontendFailure);
            }
        }
    """;

    private static string RenamedShellTraversal => OldShellTraversal.Replace(
        "    public async ValueTask DisposeAsync()",
        "    private async ValueTask DisposeFrontendAndOwnedServicesAsync()", StringComparison.Ordinal);

    private static string TraversalGuard(string traversal)
        => "        RequireOnce(shell, \"\"\"\n" + Indent(traversal, 12) + "\n            \"\"\");";

    private static Edit[] FrontendGuardEdits() =>
    [
        new(TraversalGuard(OldShellTraversal), TraversalGuard(RenamedShellTraversal)),
        new(OldPublicScope + "\n" + OldScopeReject, AdapterAssertion + "\n\n" + FrontendScopes),
    ];

    private static Edit[] WorkspaceGuardEdits() =>
    [
        new(TraversalGuard(OldShellTraversal), TraversalGuard(RenamedShellTraversal) + "\n" + AdapterAssertion),
    ];

    private static Edit[] DeferredGuardEdits() =>
    [
        new(OldPublicScope, AdapterAssertion + "\n\n" + RenamedScope),
    ];

    private const string OldPublicScope = "        var shellCleanup = Scope(shell, \"    public async ValueTask DisposeAsync()\", \"\\n    }\\n\");";
    private const string OldScopeReject = "        Reject(shellCleanup, \"ConfigureAwait(\", \".Flatten(\", \"Task.Run(\", \"Task.WhenAny(\", \".Wait(\", \".WaitAsync(\", \"GetAwaiter().GetResult()\");";
    private const string RenamedScope = """
            var shellCleanup = Scope(
                shell,
                "    private async ValueTask DisposeFrontendAndOwnedServicesAsync()",
                "\n    }\n");
    """;

    private const string AdapterAssertion = """"
            RequireOnce(shell, """
                    public async ValueTask DisposeAsync()
                        => await DisposeRemindersThenFrontendAsync(
                            () => _reminders?.DisposeAsync() ?? ValueTask.CompletedTask,
                            DisposeFrontendAndOwnedServicesAsync);
                """);
    """";

    private const string FrontendScopes = RenamedScope + "\n" + """
            Reject(shellCleanup,
                "ConfigureAwait(", ".Flatten(", "Task.Run(", "Task.WhenAny(",
                ".Wait(", ".WaitAsync(", "GetAwaiter().GetResult()");

            var reminderCleanup = Scope(
                shell,
                "    internal static Task DisposeRemindersThenFrontendAsync(",
                "\n    }\n");
            Reject(reminderCleanup,
                "ConfigureAwait(", ".Flatten(", "Task.Run(", "Task.WhenAny(",
                ".Wait(", ".WaitAsync(", "GetAwaiter().GetResult()");
    """;

    private static Edit[] PromptGuardEdits()
    {
        const string originalShellAnchor = "    private const string OriginalShell = \"\"\"";
        var additions = ConstantDeclaration("ExpectedReminderOwnership", ShellOwnership) + "\n\n" +
            ConstantDeclaration("ExpectedReminderAdapterAndCore", ShellAdapterAndCore);
        return
        [
            new("        Assert.IsTrue(string.Equals(OriginalShell + \"\\n\", shell, StringComparison.Ordinal));", PromptShellInverse),
            new(originalShellAnchor, additions + "\n\n" + originalShellAnchor),
        ];
    }

    private const string PromptShellInverse = """
            const string reminderImport = "using CodeAlta.LiveTool;\n";
            const string reminderField = "    private AltaReminderService? _reminders;\n";
            const string renamedTraversal =
                "    private async ValueTask DisposeFrontendAndOwnedServicesAsync()";

            RequireOnce(shell, reminderImport);
            RequireOnce(shell, reminderField);
            RequireOnce(shell, ExpectedReminderOwnership + "\n\n");
            RequireOnce(shell, ExpectedReminderAdapterAndCore + "\n\n");
            RequireOnce(shell, renamedTraversal);

            var originalShell = shell
                .Replace(reminderImport, "", StringComparison.Ordinal)
                .Replace(reminderField, "", StringComparison.Ordinal)
                .Replace(ExpectedReminderOwnership + "\n\n", "", StringComparison.Ordinal)
                .Replace(ExpectedReminderAdapterAndCore + "\n\n", "", StringComparison.Ordinal)
                .Replace(renamedTraversal,
                    "    public async ValueTask DisposeAsync()", StringComparison.Ordinal);

            Assert.IsTrue(string.Equals(
                OriginalShell + "\n", originalShell, StringComparison.Ordinal));
            Assert.AreEqual(
                6_324,
                System.Text.Encoding.UTF8.GetByteCount(
                    originalShell.Replace("\n", "\r\n", StringComparison.Ordinal)));
    """;

    private static Edit[] ArchitectureEdits() =>
    [
        new("App/CodeAltaApp.cs:348:_ = PersistViewStateAsync();", "App/CodeAltaApp.cs:349:_ = PersistViewStateAsync();"),
        new("App/CodeAltaApp.cs:379:_ = PersistViewStateAsync();", "App/CodeAltaApp.cs:380:_ = PersistViewStateAsync();"),
        new("App/CodeAltaApp.cs:457:_ = OpenModelProvidersAsync();", "App/CodeAltaApp.cs:458:_ = OpenModelProvidersAsync();"),
    ];

    private const string FallbackRoute = """
        private static bool TryGetReminderService(AltaCommandContext context, out AltaReminderService reminderService)
        {
            reminderService = context.Services.Get<AltaReminderService>()!;
            if (reminderService is not null)
            {
                return true;
            }

            if (context.Services is AltaServiceCollection services)
            {
                reminderService = new AltaReminderService(services);
                services.Add(reminderService);
                return true;
            }

            AltaJsonlWriter.WriteError(
                context.Stderr,
                context.CorrelationId,
                "service.unavailable",
                AltaExitCodes.ServiceUnavailable,
                "Required in-process service 'AltaReminderService' is unavailable.");
            return false;
        }
    """;

    private const string DetachedSendRoute = """
                var sendTask = runtime.SendAsync(
                    info.Session,
                    executionOptions,
                    new AgentSendOptions { Input = agentInput },
                    ShouldDetachPromptSubmission(context) ? CancellationToken.None : context.CancellationToken);

                if (ShouldDetachPromptSubmission(context) && await WaitForAgentSubmissionAckAsync(runtime, info.Session, sendTask).ConfigureAwait(false) && !sendTask.IsCompleted)
                {
                    _ = ObserveDetachedPromptSubmissionAsync(sendTask);
                    await runtime.PersistSessionLocalStateAsync(info.Session, CancellationToken.None).ConfigureAwait(false);
                    await PersistPromptProvenanceAsync(context, info.Session, runId: null, queued: false, kind, inputText).ConfigureAwait(false);
                    WritePromptResult(context, kind is PromptDispatchKind.Message or PromptDispatchKind.Request ? "alta.session.message.sent" : "alta.session.submitted", info.Session, runId: null, queueItemId: null, queued: false, kind, inputText);
                    return AltaExitCodes.Success;
                }
    """;

    private const string DetachedObserverRoute = """
        private static async Task ObserveDetachedPromptSubmissionAsync(Task<AgentRunId> sendTask)
        {
            try
            {
                await sendTask.ConfigureAwait(false);
            }
            catch
            {
                // SessionRuntimeService publishes runtime failure events for observers; this continuation
                // prevents detached live-tool submissions from surfacing as unobserved task exceptions.
            }
        }
    """;

    // Helpers below are source/byte operations only; no delegates from inspected code are run.
    private static string ReadSource(Baseline baseline, [CallerFilePath] string sourceFile = "")
    {
        var root = Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(sourceFile) ?? throw new AssertFailedException("Missing fixture source directory."), ".."));
        var bytes = File.ReadAllBytes(Path.Combine(root, baseline.Path));
        var text = new UTF8Encoding(false, true).GetString(bytes);
        Assert.IsFalse(text.StartsWith("\uFEFF", StringComparison.Ordinal), baseline.Path + ": BOM");
        Assert.IsTrue(text.EndsWith("\n", StringComparison.Ordinal), baseline.Path + ": final newline");
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        Assert.IsFalse(normalized.Contains('\r'), baseline.Path + ": lone CR");
        Assert.IsTrue(string.Equals(text,
            baseline.CrLf ? normalized.Replace("\n", "\r\n", StringComparison.Ordinal) : normalized,
            StringComparison.Ordinal), baseline.Path + ": mixed or changed line endings");
        return normalized;
    }

    private static byte[] Encode(string normalized, Baseline baseline)
    {
        Assert.IsFalse(normalized.StartsWith("\uFEFF", StringComparison.Ordinal));
        Assert.IsFalse(normalized.Contains('\r'));
        Assert.IsTrue(normalized.EndsWith("\n", StringComparison.Ordinal));
        return new UTF8Encoding(false, true).GetBytes(
            baseline.CrLf ? normalized.Replace("\n", "\r\n", StringComparison.Ordinal) : normalized);
    }

    private static void AssertBaseline(string normalized, Baseline baseline)
    {
        var bytes = Encode(normalized, baseline);
        Assert.AreEqual(baseline.Bytes, bytes.Length, baseline.Path + ": baseline byte count");
        Assert.AreEqual(baseline.Lines, normalized.Count(static character => character == '\n'), baseline.Path + ": baseline lines");
        Assert.IsTrue(string.Equals(baseline.Hash, Convert.ToHexString(SHA256.HashData(bytes)),
            StringComparison.Ordinal), baseline.Path + ": whole baseline hash");
    }

    private static string Invert(string future, Edit[] edits)
    {
        // Later edits may contain earlier additions (XML and inserted constants, for example).
        // Reverse exact replacements; never remove an unvalidated source-derived interval.
        for (var index = edits.Length - 1; index >= 0; index--)
        {
            var edit = edits[index];
            RequireCount(future, edit.After, edit.Count);
            future = future.Replace(edit.After, edit.Before, StringComparison.Ordinal);
        }

        return future;
    }

    private static void RequireOnce(string source, string expected) => RequireCount(source, expected, 1);

    private static void RequireCount(string source, string expected, int count)
    {
        Assert.IsTrue(expected.Length > 0);
        var actual = 0;
        var offset = 0;
        while ((offset = source.IndexOf(expected, offset, StringComparison.Ordinal)) >= 0)
        {
            actual++;
            offset += expected.Length;
        }

        Assert.AreEqual(count, actual, "Exact source occurrence count: " + expected);
    }

    private static string Indent(string text, int spaces)
    {
        var padding = new string(' ', spaces);
        // Preserve empty lines as empty: baseline raw-string literals have no blank-line spaces.
        return string.Join("\n", text.Split('\n').Select(line => line.Length == 0 ? "" : padding + line));
    }

    private static string ConstantDeclaration(string name, string value)
        => "    private const string " + name + " = \"\"\"\n" + Indent(value, 8) + "\n        \"\"\";";
}
