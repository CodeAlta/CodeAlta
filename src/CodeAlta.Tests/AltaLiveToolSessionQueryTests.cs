using System.Diagnostics;
using System.Globalization;
using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Catalog;
using CodeAlta.Catalog.Skills;
using CodeAlta.LiveTool;
using CodeAlta.Orchestration.Runtime;
using CodeAlta.Orchestration.Runtime.SystemPrompts;

namespace CodeAlta.Tests;

public sealed partial class AltaLiveToolTests
{
    private const string SessionQueryBenchmarkVariable = "CODEALTA_TEST_SESSION_QUERY_BENCHMARK";

    [TestMethod]
    public async Task SessionReport_ReadsTheSessionsAskedForWithoutListingTheOthers()
    {
        using var root = TempDirectory.Create();
        var options = new CatalogOptions { GlobalRoot = root.Path };
        var providerId = new ModelProviderId("query");
        var catalog = new SessionViewCatalog(options);
        var store = catalog.JournalStore.CreateSessionStore();
        var start = new DateTimeOffset(2026, 10, 1, 8, 0, 0, TimeSpan.Zero);
        var ids = new List<string>();
        for (var index = 0; index < 4; index++)
        {
            var sessionId = Guid.CreateVersion7(start.AddMinutes(index)).ToString();
            ids.Add(sessionId);
            await WriteBenchmarkSessionAsync(catalog, store, providerId, root.Path, sessionId, start.AddMinutes(index), index == 0 ? null : ids[0],
                runs: 2, toolCalls: 2, toolOutputBytes: 256).ConfigureAwait(false);
        }

        var listings = new CountingSessionCatalog(new AgentSessionCatalog(catalog.JournalStore.CreateSessionStore()));
        var runtime = CreateRuntime(options, new ModelProviderRegistry(), listings);
        await using var _ = runtime.ConfigureAwait(false);
        var services = new AltaServiceCollection().Add(options).Add(new ProjectCatalog(options)).Add(catalog).Add(runtime);
        var dispatcher = CreateDispatcher(services);
        var listingDispatcher = CreateDispatcher(new AltaServiceCollection().Add(options).Add(new ProjectCatalog(options)).Add(catalog).Add(runtime)
            .Add<IAltaSessionQueryService>(new ListingSessionQueryService()));

        // The sessions asked for are read by their identifiers.
        var report = await dispatcher.InvokeAsync(["session", "report", ids[1], ids[2], ids[1], "--include=result,metrics"], caller: AltaCallerIdentity.Cli).ConfigureAwait(false);
        var result = await dispatcher.InvokeAsync(["session", "result", ids[3]], caller: AltaCallerIdentity.Cli).ConfigureAwait(false);
        var metrics = await dispatcher.InvokeAsync(["session", "metrics", ids[3], "--scope", "session"], caller: AltaCallerIdentity.Cli).ConfigureAwait(false);
        Assert.AreEqual(0, listings.ListCount, report.Stdout);
        Assert.AreEqual(AltaExitCodes.Success, report.ExitCode, report.Stdout);
        CollectionAssert.AreEqual(new[] { ids[1], ids[2], ids[1] }, ReadJsonLines(report.Stdout)
            .Where(static line => line.GetProperty("type").GetString() == "alta.session.report.item")
            .Select(static line => line.GetProperty("sessionId").GetString()).ToArray());

        // An identifier that is not found as it is written is looked for in the listing, as before.
        var missing = await dispatcher.InvokeAsync(["session", "report", ids[1], "missing-session"], caller: AltaCallerIdentity.Cli).ConfigureAwait(false);
        Assert.AreEqual(1, listings.ListCount);
        var otherCase = await dispatcher.InvokeAsync(["session", "report", ids[2].ToUpperInvariant()], caller: AltaCallerIdentity.Cli).ConfigureAwait(false);
        Assert.AreEqual(2, listings.ListCount);
        Assert.AreEqual(ids[2], ReadJsonLines(otherCase.Stdout).Single(static line => line.GetProperty("type").GetString() == "alta.session.report.item")
            .GetProperty("sessionId").GetString());

        // What is written is what the listing of every session gives.
        foreach (var (fast, args) in new (AltaCommandResult, string[])[]
        {
            (report, ["session", "report", ids[1], ids[2], ids[1], "--include=result,metrics"]),
            (result, ["session", "result", ids[3]]),
            (metrics, ["session", "metrics", ids[3], "--scope", "session"]),
            (missing, ["session", "report", ids[1], "missing-session"]),
            (otherCase, ["session", "report", ids[2].ToUpperInvariant()]),
        })
        {
            var listed = await listingDispatcher.InvokeAsync(args, caller: AltaCallerIdentity.Cli).ConfigureAwait(false);
            AssertSameRecords(listed, fast, string.Join(' ', args));
        }
    }

    [TestMethod]
    public async Task SessionQueries_WriteWhatTheListingOfEverySessionGives()
    {
        using var root = TempDirectory.Create();
        var options = new CatalogOptions { GlobalRoot = root.Path };
        var agents = Path.Combine(root.Path, "prompts", "agents");
        Directory.CreateDirectory(agents);
        await File.WriteAllTextAsync(Path.Combine(agents, "worker.prompt.md"), "---\nname: Worker\n---\nYou work.\n").ConfigureAwait(false);
        var providerId = new ModelProviderId("equivalence");
        var runtime = CreateRuntime(options, new StatefulProviderRuntime(providerId) { PublishRunEventOnSend = true });
        await using var _ = runtime.ConfigureAwait(false);
        var catalog = new SessionViewCatalog(options);
        var execution = new SessionExecutionOptions
        {
            ProviderId = providerId, ProviderKey = providerId.Value, WorkingDirectory = root.Path, ProjectRoots = [],
            OnPermissionRequest = static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce)),
        };
        {
            var parent = await runtime.CreateGlobalSessionAsync(execution, "Parent").ConfigureAwait(false);
            var idle = await runtime.CreateGlobalSessionAsync(execution, "Idle child", parent.SessionId, createdBy: null).ConfigureAwait(false);
            var running = await runtime.CreateGlobalSessionAsync(execution, "Running child", parent.SessionId, createdBy: null).ConfigureAwait(false);
            await runtime.CreateGlobalSessionAsync(execution, "Grandchild", idle.SessionId, createdBy: null).ConfigureAwait(false);
            var archived = await runtime.CreateGlobalSessionAsync(execution, "Archived child", parent.SessionId, createdBy: null).ConfigureAwait(false);
            await AppendJournalStateAsync(catalog, archived, new SessionViewLocalState { Archived = true, ArchivedAt = DateTimeOffset.UtcNow, AgentPromptId = "worker" }).ConfigureAwait(false);
            Assert.IsTrue(await runtime.DetachRuntimeSessionAsync(archived.SessionId).ConfigureAwait(false));
            var store = catalog.JournalStore.CreateSessionStore();
            var stored = Guid.CreateVersion7().ToString();
            await WriteBenchmarkSessionAsync(catalog, store, providerId, root.Path, stored, DateTimeOffset.UtcNow.AddMinutes(-5), parent.SessionId,
                runs: 6, toolCalls: 3, toolOutputBytes: 2048).ConfigureAwait(false);
            await runtime.SendAsync(running, execution, new AgentSendOptions { Input = AgentInput.Text("keep running") }).ConfigureAwait(false);

            var dispatcher = CreateDispatcher(new AltaServiceCollection().Add(options).Add(new ProjectCatalog(options)).Add(catalog).Add(runtime));
            var listingDispatcher = CreateDispatcher(new AltaServiceCollection().Add(options).Add(new ProjectCatalog(options)).Add(catalog).Add(runtime)
                .Add<IAltaSessionQueryService>(new ListingSessionQueryService()));
            string[] all = [parent.SessionId, idle.SessionId, running.SessionId, archived.SessionId, stored];
            List<string[]> commands =
            [
                ["session", "report", .. all, "--include=result,metrics"],
                ["session", "report", .. all, "--scope", "session"],
                ["session", "info", parent.SessionId],
                ["session", "info", stored],
                ["session", "detail", archived.SessionId],
                ["session", "status", running.SessionId],
                ["session", "children", parent.SessionId],
                ["session", "children", parent.SessionId, "--recursive"],
                ["session", "list"],
                ["session", "list", "--state", "all", "--metrics"],
                ["session", "list", "--state", "running"],
                ["session", "list", "--state", "idle"],
                ["session", "list", "--state", "inactive"],
                ["session", "list", "--state", "archived"],
                ["session", "list", "--limit", "2"],
            ];
            foreach (var sessionId in all)
            {
                commands.Add(["session", "result", sessionId]);
                commands.Add(["session", "metrics", sessionId]);
                commands.Add(["session", "model", sessionId]);
            }

            foreach (var args in commands)
            {
                // The sessions the runtime holds still write their journals: a record is compared with the listing read
                // just before it and with the one read just after it.
                var before = await listingDispatcher.InvokeAsync(args, caller: AltaCallerIdentity.Cli).ConfigureAwait(false);
                var fast = await dispatcher.InvokeAsync(args, caller: AltaCallerIdentity.Cli).ConfigureAwait(false);
                var after = await listingDispatcher.InvokeAsync(args, caller: AltaCallerIdentity.Cli).ConfigureAwait(false);
                if (!NormalizeRecords(before.Stdout).SequenceEqual(NormalizeRecords(fast.Stdout)))
                {
                    AssertSameRecords(after, fast, string.Join(' ', args));
                }
            }

            var runningItem = ReadJsonLines((await dispatcher.InvokeAsync(["session", "list", "--state", "running"], caller: AltaCallerIdentity.Cli).ConfigureAwait(false)).Stdout)
                .Single(static line => line.GetProperty("type").GetString() == "alta.session.item");
            Assert.AreEqual(running.SessionId, runningItem.GetProperty("sessionId").GetString());
            var archivedItem = ReadJsonLines((await dispatcher.InvokeAsync(["session", "info", archived.SessionId], caller: AltaCallerIdentity.Cli).ConfigureAwait(false)).Stdout)
                .Single(static line => line.GetProperty("type").GetString() == "alta.session.info");
            Assert.AreEqual("worker", archivedItem.GetProperty("agentPromptId").GetString());
        }
    }

    [TestMethod]
    public async Task SessionList_ReadsTheSessionProjection_NeverAJournal()
    {
        using var root = TempDirectory.Create();
        var options = new CatalogOptions { GlobalRoot = root.Path };
        var providerId = new ModelProviderId("projection");
        var catalog = new SessionViewCatalog(options);
        var store = catalog.JournalStore.CreateSessionStore();
        var start = new DateTimeOffset(2026, 10, 3, 8, 0, 0, TimeSpan.Zero);
        var ids = new string[3];
        for (var index = 0; index < ids.Length; index++)
        {
            ids[index] = Guid.CreateVersion7(start.AddMinutes(index)).ToString();
            await WriteBenchmarkSessionAsync(catalog, store, providerId, root.Path, ids[index], start.AddMinutes(index), index == 0 ? null : ids[0],
                runs: 1, toolCalls: 1, toolOutputBytes: 64).ConfigureAwait(false);
        }

        // An archived session that chose its model, and a session with a prompt that waits and one that was sent.
        var archivedAt = new DateTimeOffset(2026, 10, 3, 9, 30, 0, TimeSpan.Zero);
        await catalog.JournalStore.AppendStateAsync(CreateBenchmarkDescriptor(providerId, root.Path, ids[1], start.AddMinutes(1)), new SessionViewLocalState
        {
            Archived = true, ArchivedAt = archivedAt, ModelId = "chosen-model", ReasoningEffort = AgentReasoningEffort.High, ParentSessionId = ids[0], MessageCount = 2,
        }).ConfigureAwait(false);
        await catalog.JournalStore.AppendStateAsync(CreateBenchmarkDescriptor(providerId, root.Path, ids[2], start.AddMinutes(2)), new SessionViewLocalState
        {
            ParentSessionId = ids[0], MessageCount = 2,
            QueuedPrompts = [CreateQueuedPrompt("waits", "queued", start), CreateQueuedPrompt("sent", "submitted", start)],
        }).ConfigureAwait(false);

        string[] list = ["session", "list", "--state", "all"];
        AltaCommandResult expected;
        {
            // What the listing gives when it reads the journals.
            var runtime = CreateRuntime(options, providerId);
            await using var _ = runtime.ConfigureAwait(false);
            expected = await CreateDispatcher(new AltaServiceCollection().Add(options).Add(new ProjectCatalog(options)).Add(new SessionViewCatalog(options)).Add(runtime))
                .InvokeAsync(list, caller: AltaCallerIdentity.Cli).ConfigureAwait(false);
        }

        Assert.AreEqual(AltaExitCodes.Success, expected.ExitCode, expected.Stdout);
        var items = ReadJsonLines(expected.Stdout).Where(static line => line.GetProperty("type").GetString() == "alta.session.item")
            .ToDictionary(static line => line.GetProperty("sessionId").GetString()!);
        Assert.HasCount(3, items);
        Assert.AreEqual("archived", items[ids[1]].GetProperty("state").GetString());
        Assert.AreEqual(archivedAt, items[ids[1]].GetProperty("archivedAt").GetDateTimeOffset());
        Assert.AreEqual("chosen-model", items[ids[1]].GetProperty("modelSelection").GetProperty("modelId").GetString());
        Assert.AreEqual(1, items[ids[2]].GetProperty("queuedPromptCount").GetInt32());
        Assert.AreEqual(0, items[ids[0]].GetProperty("queuedPromptCount").GetInt32());

        // As at a start of the application: nothing is remembered in memory, and no journal can be opened.
        var locks = new List<FileStream>();
        try
        {
            foreach (var journal in Directory.EnumerateFiles(new AgentRuntimePathLayout(options.StateRoot).SessionsRootPath, "*.jsonl", SearchOption.AllDirectories))
                locks.Add(new FileStream(journal, FileMode.Open, FileAccess.Read, FileShare.None));
            Assert.HasCount(3, locks);

            var runtime = CreateRuntime(options, providerId);
            await using var _ = runtime.ConfigureAwait(false);
            var services = new AltaServiceCollection().Add(options).Add(new ProjectCatalog(options)).Add(new SessionViewCatalog(options)).Add(runtime);
            // A session read by its identifier, before any listing, is read from the database too.
            var resolved = await new AltaSessionQueryService().TryResolveAsync(new AltaCommandContext
            {
                Caller = AltaCallerIdentity.Cli,
                Services = services,
                Stdin = new StringReader(string.Empty),
                Stdout = new StringWriter(CultureInfo.InvariantCulture),
                Stderr = new StringWriter(CultureInfo.InvariantCulture),
                CorrelationId = "test",
            }, [ids[1], ids[2]]).ConfigureAwait(false);
            Assert.IsNotNull(resolved);
            Assert.AreEqual(archivedAt, resolved[0].ArchivedAt);
            Assert.AreEqual(SessionViewStatus.Archived, resolved[0].Session.Status);
            Assert.AreEqual("chosen-model", resolved[0].Session.ModelId);
            Assert.AreEqual(1, resolved[1].QueuedPromptCount);

            var actual = await CreateDispatcher(services).InvokeAsync(list, caller: AltaCallerIdentity.Cli).ConfigureAwait(false);
            AssertSameRecords(expected, actual, string.Join(' ', list));
        }
        finally
        {
            foreach (var stream in locks) await stream.DisposeAsync().ConfigureAwait(false);
        }
    }

    [TestMethod]
    public async Task SessionQueryService_ListsTheAgentPromptsOfAFolderOncePerCommand()
    {
        using var root = TempDirectory.Create();
        var options = new CatalogOptions { GlobalRoot = root.Path };
        var providerId = new ModelProviderId("prompts");
        var catalog = new SessionViewCatalog(options);
        var store = catalog.JournalStore.CreateSessionStore();
        var ids = new List<string>();
        for (var index = 0; index < 5; index++)
        {
            var sessionId = Guid.CreateVersion7().ToString();
            ids.Add(sessionId);
            await WriteBenchmarkSessionAsync(catalog, store, providerId, root.Path, sessionId, DateTimeOffset.UtcNow.AddMinutes(index), null,
                runs: 1, toolCalls: 1, toolOutputBytes: 64).ConfigureAwait(false);
        }

        var runtime = CreateRuntime(options, providerId);
        await using var _ = runtime.ConfigureAwait(false);
        var context = new AltaCommandContext
        {
            Caller = AltaCallerIdentity.Cli,
            Services = new AltaServiceCollection().Add(options).Add(new ProjectCatalog(options)).Add(catalog).Add(runtime),
            Stdin = new StringReader(string.Empty),
            Stdout = new StringWriter(CultureInfo.InvariantCulture),
            Stderr = new StringWriter(CultureInfo.InvariantCulture),
            CorrelationId = "test",
        };
        var listings = 0;
        var service = new AltaSessionQueryService(query =>
        {
            Interlocked.Increment(ref listings);
            return [new AgentPromptDescriptor("agent3", "Agent", null, "default", "Body", AgentPromptSourceKind.UserGlobal, 1, "agent3.prompt.md", "sha256:0",
                PromptCompositionMode.Replace, IsShadowed: false, ShadowedByPath: null)];
        });

        var infos = new List<AltaSessionInfo>();
        await foreach (var info in service.LoadAsync(context).ConfigureAwait(false))
        {
            infos.Add(info);
        }

        Assert.AreEqual(5, infos.Count);
        Assert.IsTrue(infos.All(static info => info.Session.AgentPromptId == "agent3"));
        Assert.AreEqual(1, listings, "The sessions of one folder share one listing of its prompts.");

        var resolved = await service.TryResolveAsync(context, ids).ConfigureAwait(false);
        Assert.IsNotNull(resolved);
        CollectionAssert.AreEqual(ids, resolved.Select(static info => info.Session.SessionId).ToArray());
        Assert.AreEqual(2, listings);
        Assert.IsNull(await service.TryResolveAsync(context, [ids[0], "missing-session"]).ConfigureAwait(false));
        Assert.AreEqual(string.Empty, context.Stderr.ToString());
    }

    [TestMethod]
    [DataRow("completed", true)]
    [DataRow("failed", true)]
    [DataRow("cancelled", true)]
    [DataRow("running", true)]
    [DataRow("runningBeforeAnswer", true)]
    [DataRow("steered", true)]
    [DataRow("compacted", true)]
    [DataRow("answerOfAnEarlierRun", true)]
    [DataRow("answerOfTheProvider", false)]
    [DataRow("answerWithoutRun", true)]
    [DataRow("oneTurn", false)]
    [DataRow("noAnswer", false)]
    [DataRow("empty", false)]
    public async Task StoredLastTurnHistory_HoldsTheLastTurnOfTheWholeHistory(string scenario, bool bounded)
    {
        using var root = TempDirectory.Create();
        var options = new CatalogOptions { GlobalRoot = root.Path };
        var providerId = new ModelProviderId("turns");
        var runtime = CreateRuntime(options, providerId);
        await using var _ = runtime.ConfigureAwait(false);
        var sessionId = await WriteTurnScenarioAsync(new SessionViewCatalog(options), providerId, root.Path, scenario).ConfigureAwait(false);
        var session = await runtime.TryGetRecoverableSessionAsync(sessionId).ConfigureAwait(false);
        Assert.IsNotNull(session);

        var full = await runtime.TryReadStoredHistoryAsync(session, onUnavailable: null).ConfigureAwait(false);
        var tail = await runtime.TryReadStoredLastTurnHistoryAsync(session, onUnavailable: null).ConfigureAwait(false);

        Assert.IsNotNull(full);
        Assert.IsNotNull(tail);
        CollectionAssert.AreEqual(ToJson(full.Skip(full.Count - tail.Count)), ToJson(tail), "The read is the end of the stored history.");
        CollectionAssert.AreEqual(ToJson(LastTurnOf(full)), ToJson(LastTurnOf(tail)), "The last turn is the same.");
        Assert.AreEqual(bounded, tail.Count < full.Count, $"{tail.Count} of {full.Count} events were read.");
    }

    [TestMethod]
    public async Task StoredLastTurnHistory_ReportsAJournalThatCannotBeReadAsTheWholeReadDoes()
    {
        using var root = TempDirectory.Create();
        var options = new CatalogOptions { GlobalRoot = root.Path };
        var providerId = new ModelProviderId("turns");
        var runtime = CreateRuntime(options, providerId);
        await using var _ = runtime.ConfigureAwait(false);
        var catalog = new SessionViewCatalog(options);
        var inTurn = await WriteTurnScenarioAsync(catalog, providerId, root.Path, "corruptInLastTurn").ConfigureAwait(false);
        var beforeTurn = await WriteTurnScenarioAsync(catalog, providerId, root.Path, "corruptBeforeLastTurn").ConfigureAwait(false);

        // A record of the last turn that cannot be parsed: the whole read reports the journal, and so does this one.
        var session = await runtime.TryGetRecoverableSessionAsync(inTurn).ConfigureAwait(false);
        Exception? fullFailure = null;
        Exception? tailFailure = null;
        Assert.IsNull(await runtime.TryReadStoredHistoryAsync(session!, error => fullFailure = error).ConfigureAwait(false));
        Assert.IsNull(await runtime.TryReadStoredLastTurnHistoryAsync(session!, error => tailFailure = error).ConfigureAwait(false));
        Assert.IsNotNull(fullFailure);
        Assert.IsNotNull(tailFailure);
        Assert.AreEqual(fullFailure.GetType(), tailFailure.GetType());

        // One further back than the pages of the last turn is not read: the last turn is, where the whole read fails.
        session = await runtime.TryGetRecoverableSessionAsync(beforeTurn).ConfigureAwait(false);
        Assert.IsNull(await runtime.TryReadStoredHistoryAsync(session!, onUnavailable: null).ConfigureAwait(false));
        var tail = await runtime.TryReadStoredLastTurnHistoryAsync(session!, onUnavailable: null).ConfigureAwait(false);
        Assert.IsNotNull(tail);
        Assert.AreEqual("answer 3", LastTurnOf(tail).OfType<AgentContentCompletedEvent>().Last(static content => content.Kind == AgentContentKind.Assistant).Content);
        Assert.AreEqual(6, LastTurnOf(tail).Count);
    }

    [TestMethod]
    public async Task SessionReport_OfALongSessionWritesItsLastTurn()
    {
        using var root = TempDirectory.Create();
        var options = new CatalogOptions { GlobalRoot = root.Path };
        var providerId = new ModelProviderId("turns");
        var runtime = CreateRuntime(options, providerId);
        await using var _ = runtime.ConfigureAwait(false);
        var catalog = new SessionViewCatalog(options);
        var sessionId = await WriteTurnScenarioAsync(catalog, providerId, root.Path, "failed").ConfigureAwait(false);
        var dispatcher = CreateDispatcher(new AltaServiceCollection().Add(options).Add(new ProjectCatalog(options)).Add(catalog).Add(runtime));

        var report = await dispatcher.InvokeAsync(["session", "report", sessionId], caller: AltaCallerIdentity.Cli).ConfigureAwait(false);
        var whole = await dispatcher.InvokeAsync(["session", "report", sessionId, "--scope", "session"], caller: AltaCallerIdentity.Cli).ConfigureAwait(false);

        Assert.AreEqual(AltaExitCodes.Success, report.ExitCode, report.Stdout);
        var item = ReadJsonLines(report.Stdout).Single(static line => line.GetProperty("type").GetString() == "alta.session.report.item");
        // The last turn failed before it answered: the last turn with an answer is the fourth one.
        Assert.AreEqual("completed", item.GetProperty("status").GetString());
        Assert.AreEqual("answer 4", item.GetProperty("finalAnswer").GetProperty("text").GetString());
        Assert.AreEqual(1, item.GetProperty("metrics").GetProperty("runCount").GetInt32());
        Assert.AreEqual(1, item.GetProperty("toolCallCount").GetInt32());
        var wholeItem = ReadJsonLines(whole.Stdout).Single(static line => line.GetProperty("type").GetString() == "alta.session.report.item");
        Assert.AreEqual("failed", wholeItem.GetProperty("status").GetString());
        Assert.AreEqual(5, wholeItem.GetProperty("metrics").GetProperty("runCount").GetInt32());
    }

    [TestMethod]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public async Task SessionEventsAndTail_ReadOnlyTheTurnsTheyShow_NumberedAsInTheWholeHistory()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("A range of a file is locked against the reads of other handles on Windows only.");
        }

        using var root = TempDirectory.Create();
        var options = new CatalogOptions { GlobalRoot = root.Path };
        var providerId = new ModelProviderId("turns");
        var runtime = CreateRuntime(options, providerId);
        await using var _ = runtime.ConfigureAwait(false);
        var catalog = new SessionViewCatalog(options);
        var sessionId = await WriteTurnScenarioAsync(catalog, providerId, root.Path, "completed").ConfigureAwait(false);
        var dispatcher = CreateDispatcher(new AltaServiceCollection().Add(options).Add(new ProjectCatalog(options)).Add(catalog).Add(runtime));
        var session = await runtime.TryGetRecoverableSessionAsync(sessionId).ConfigureAwait(false);
        var full = (await runtime.TryReadStoredHistoryAsync(session!, onUnavailable: null).ConfigureAwait(false))!;
        var turns = (await catalog.JournalStore.CreateSessionStore().ReadTurnsAsync(sessionId).ConfigureAwait(false)).Turns;
        var journal = Directory.EnumerateFiles(new AgentRuntimePathLayout(options.StateRoot).SessionsRootPath, sessionId + ".jsonl", SearchOption.AllDirectories).Single();
        var length = new FileInfo(journal).Length;

        long[] Sequences(string stdout) => ReadJsonLines(stdout)
            .Where(static line => line.GetProperty("type").GetString() == "alta.session.event")
            .Select(static line => line.GetProperty("sequenceNumber").GetInt64())
            .ToArray();

        // The last two events are in the last turn: nothing before it is read.
        using (var held = new FileStream(journal, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete))
        {
            held.Lock(0, turns[^1].StartOffset);
            var tail = await dispatcher.InvokeAsync(["session", "tail", sessionId, "--last", "2"], caller: AltaCallerIdentity.Cli).ConfigureAwait(false);
            Assert.AreEqual(AltaExitCodes.Success, tail.ExitCode, tail.Stderr);
            CollectionAssert.AreEqual(new long[] { full.Count - 1, full.Count }, Sequences(tail.Stdout), tail.Stdout);
        }

        // The first three events are in the first turn: nothing after it is read.
        using (var held = new FileStream(journal, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete))
        {
            held.Lock(turns[1].StartOffset, length - turns[1].StartOffset);
            var events = await dispatcher.InvokeAsync(["session", "events", sessionId, "--limit", "3"], caller: AltaCallerIdentity.Cli).ConfigureAwait(false);
            Assert.AreEqual(AltaExitCodes.Success, events.ExitCode, events.Stderr);
            CollectionAssert.AreEqual(new long[] { 1, 2, 3 }, Sequences(events.Stdout), events.Stdout);
        }

        // After an event of the second turn: that turn alone is read, and the numbers are those of the whole history.
        var secondTurnFirst = full.ToList().FindIndex(item => item.RunId?.Value == turns[1].RunId) + 1;
        using (var held = new FileStream(journal, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete))
        {
            held.Lock(0, turns[1].StartOffset);
            held.Lock(turns[2].StartOffset, length - turns[2].StartOffset);
            var since = await dispatcher.InvokeAsync(["session", "events", sessionId, "--since", secondTurnFirst.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "--limit", "2"], caller: AltaCallerIdentity.Cli).ConfigureAwait(false);
            Assert.AreEqual(AltaExitCodes.Success, since.ExitCode, since.Stderr);
            CollectionAssert.AreEqual(new long[] { secondTurnFirst + 1, secondTurnFirst + 2 }, Sequences(since.Stdout), since.Stdout);
        }
    }

    [TestMethod]
    public async Task SessionSend_FromAgentCallerStopsWaitingForTheRunWhenTheCommandIsCanceled()
    {
        using var root = TempDirectory.Create();
        var options = new CatalogOptions { GlobalRoot = root.Path };
        var providerId = new ModelProviderId("ack-cancel");
        var resumeBlocker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var providerRuntime = new StatefulProviderRuntime(providerId) { ResumeBlocker = resumeBlocker };
        var runtime = CreateRuntime(options, providerRuntime);
        await using var _ = runtime.ConfigureAwait(false);
        var dispatcher = CreateDispatcher(new AltaServiceCollection()
            .Add(options)
            .Add(new ProjectCatalog(options))
            .Add(new SessionViewCatalog(options))
            .Add(runtime));
        var created = await dispatcher.InvokeAsync(["session", "create", "--global", "--provider", providerId.Value], caller: AltaCallerIdentity.Cli).ConfigureAwait(false);
        var sessionId = ReadJsonLines(created.Stdout).Single(static line => line.GetProperty("type").GetString() == "alta.session.created").GetProperty("sessionId").GetString()!;
        Assert.IsTrue(await runtime.DetachRuntimeSessionAsync(sessionId).ConfigureAwait(false));
        var caller = new AltaCallerIdentity { Kind = "agent", SourceSessionId = "parent-session" };

        try
        {
            // The session is attached again before its run starts, which does not happen here: the send waits for
            // the run, up to its time limit, unless the command is canceled.
            using var cancellation = new CancellationTokenSource();
            var watch = Stopwatch.StartNew();
            var sendTask = dispatcher.InvokeAsync(["session", "send", sessionId, "--message", "work"], caller: caller, cancellationToken: cancellation.Token).AsTask();
            await WaitUntilAsync(() => providerRuntime.ResumedOptions.Count == 1).ConfigureAwait(false);
            await Task.Delay(200).ConfigureAwait(false);
            Assert.IsFalse(sendTask.IsCompleted);
            await cancellation.CancelAsync().ConfigureAwait(false);
            try
            {
                await sendTask.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }

            Assert.IsTrue(sendTask.IsCompleted, "The canceled command ends without waiting for the time limit of the acknowledgement.");
            Assert.IsTrue(watch.Elapsed < TimeSpan.FromSeconds(4.5), $"The command took {watch.Elapsed}.");
        }
        finally
        {
            resumeBlocker.TrySetResult();
        }
    }

    private static void AssertSameRecords(AltaCommandResult expected, AltaCommandResult actual, string command)
    {
        Assert.AreEqual(expected.ExitCode, actual.ExitCode, command);
        CollectionAssert.AreEqual(NormalizeRecords(expected.Stdout), NormalizeRecords(actual.Stdout), $"{command}{Environment.NewLine}{expected.Stdout}{Environment.NewLine}{actual.Stdout}");
    }

    // The records without what differs from one invocation to the next: the session the runtime holds is updated at
    // the time it is read.
    private static string[] NormalizeRecords(string transcript)
        => [.. ReadJsonLines(transcript).Select(static line =>
        {
            var record = System.Text.Json.Nodes.JsonNode.Parse(line.GetRawText())!.AsObject();
            record.Remove("correlationId");
            record.Remove("durationMs");
            record.Remove("updatedAt");
            return record.ToJsonString();
        })];

    private static string[] ToJson(IEnumerable<AgentEvent> events) => [.. events.Select(static agentEvent => agentEvent.ToJson())];

    // The scope of `--scope last-turn`, as the live tool selects it: the events of the run of the last assistant message.
    private static IReadOnlyList<AgentEvent> LastTurnOf(IReadOnlyList<AgentEvent> history)
    {
        var lastRunId = history.OfType<AgentContentCompletedEvent>()
            .Where(static content => content.Kind == AgentContentKind.Assistant && content.RunId is not null)
            .Select(static content => content.RunId!.Value.Value)
            .LastOrDefault();
        if (string.IsNullOrWhiteSpace(lastRunId))
        {
            lastRunId = history.Where(static agentEvent => agentEvent.RunId is not null).Select(static agentEvent => agentEvent.RunId!.Value.Value).LastOrDefault();
        }

        return string.IsNullOrWhiteSpace(lastRunId)
            ? history
            : history.Where(agentEvent => string.Equals(agentEvent.RunId?.Value, lastRunId, StringComparison.Ordinal)).ToArray();
    }

    // Writes a stored session whose journal holds the turns of a scenario, the earlier ones large enough for the last
    // ones to be less than half of the journal.
    private static async Task<string> WriteTurnScenarioAsync(SessionViewCatalog catalog, ModelProviderId providerId, string workingDirectory, string scenario)
    {
        var store = catalog.JournalStore.CreateSessionStore();
        var sessionId = Guid.CreateVersion7().ToString();
        var createdAt = new DateTimeOffset(2026, 10, 2, 9, 0, 0, TimeSpan.Zero);
        await store.UpsertSessionAsync(new AgentSessionSummary
        {
            SessionId = sessionId,
            ProviderId = providerId,
            ProtocolFamily = "openai",
            ProviderKey = providerId.Value,
            ModelId = "turn-model",
            WorkingDirectory = workingDirectory,
            Title = scenario,
            Summary = scenario,
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
        }).ConfigureAwait(false);
        var at = createdAt;
        // The tests keep their state in the global root, the working folder of their chats.
        var journal = new AgentRuntimePathLayout(workingDirectory).GetSessionFilePath(sessionId, createdAt);

        AgentEvent Content(string? runId, AgentContentKind kind, string content)
            => new AgentContentCompletedEvent(providerId, sessionId, at = at.AddSeconds(1), runId is null ? null : new AgentRunId(runId), kind, Guid.NewGuid().ToString("N"), null, content);
        AgentEvent Update(string? runId, AgentSessionUpdateKind kind, AgentSessionUsage? usage = null)
            => new AgentSessionUpdateEvent(providerId, sessionId, at = at.AddSeconds(1), runId is null ? null : new AgentRunId(runId), kind, kind.ToString(), Usage: usage);
        List<AgentEvent> Turn(string runId, int number, int toolCalls, int outputBytes, bool answer = true, bool idle = true)
        {
            var events = new List<AgentEvent> { Content(runId, AgentContentKind.User, $"prompt {number}") };
            for (var call = 0; call < toolCalls; call++)
            {
                events.Add(new AgentActivityEvent(providerId, sessionId, at = at.AddSeconds(1), new AgentRunId(runId), AgentActivityKind.ToolCall, AgentActivityPhase.Requested,
                    $"{runId}-{call}", null, "read_file", null));
                events.Add(Content(runId, AgentContentKind.ToolOutput, new string('t', outputBytes)));
                events.Add(Update(runId, AgentSessionUpdateKind.UsageUpdated, new AgentSessionUsage(
                    Window: new AgentWindowUsageSnapshot(100 * (call + 1), 1000, call, "window"),
                    LastOperation: new AgentOperationUsageSnapshot(Model: "turn-model", InputTokens: 100 + call, OutputTokens: 10, Cost: 0.5, CostUnit: "USD"),
                    Scope: AgentUsageScope.CurrentWindow, Source: AgentUsageSource.ProviderUsage, UpdatedAt: at)));
            }

            if (answer)
            {
                events.Add(Content(runId, AgentContentKind.Assistant, $"answer {number}"));
            }

            if (idle)
            {
                events.Add(Update(runId, AgentSessionUpdateKind.Idle));
            }

            return events;
        }

        async Task AppendAsync(IReadOnlyList<AgentEvent> events)
        {
            if (events.Count > 0)
            {
                await store.AppendEventsAsync("openai", providerId.Value, sessionId, events).ConfigureAwait(false);
            }
        }

        // The earlier turns: large ones, so that the last turns are a small part of the journal.
        async Task EarlierTurnsAsync(int count)
        {
            for (var number = 1; number <= count; number++)
            {
                await AppendAsync(Turn(Guid.CreateVersion7().ToString(), number, toolCalls: 3, outputBytes: 16 * 1024)).ConfigureAwait(false);
            }
        }

        switch (scenario)
        {
            case "completed":
                await EarlierTurnsAsync(3).ConfigureAwait(false);
                await AppendAsync(Turn(Guid.CreateVersion7().ToString(), 4, toolCalls: 2, outputBytes: 512)).ConfigureAwait(false);
                break;
            case "failed":
            case "cancelled":
            {
                await EarlierTurnsAsync(3).ConfigureAwait(false);
                await AppendAsync(Turn(Guid.CreateVersion7().ToString(), 4, toolCalls: 1, outputBytes: 512)).ConfigureAwait(false);
                var last = Guid.CreateVersion7().ToString();
                var events = Turn(last, 5, toolCalls: 1, outputBytes: 512, answer: false, idle: false);
                events.Add(new AgentErrorEvent(providerId, sessionId, at = at.AddSeconds(1), scenario == "failed" ? "The provider failed." : "Run cancelled.",
                    exception: scenario == "failed" ? new InvalidOperationException("The provider failed.") : null, runId: new AgentRunId(last)));
                await AppendAsync(events).ConfigureAwait(false);
                break;
            }
            case "running":
                await EarlierTurnsAsync(3).ConfigureAwait(false);
                await AppendAsync(Turn(Guid.CreateVersion7().ToString(), 4, toolCalls: 2, outputBytes: 512, idle: false)).ConfigureAwait(false);
                break;
            case "runningBeforeAnswer":
                await EarlierTurnsAsync(3).ConfigureAwait(false);
                await AppendAsync(Turn(Guid.CreateVersion7().ToString(), 4, toolCalls: 2, outputBytes: 512, answer: false, idle: false)).ConfigureAwait(false);
                break;
            case "steered":
            {
                await EarlierTurnsAsync(3).ConfigureAwait(false);
                var last = Guid.CreateVersion7().ToString();
                var events = Turn(last, 4, toolCalls: 1, outputBytes: 512, answer: false, idle: false);
                events.Add(Content(last, AgentContentKind.User, "steer"));
                events.AddRange(Turn(last, 4, toolCalls: 1, outputBytes: 512).Skip(1));
                await AppendAsync(events).ConfigureAwait(false);
                break;
            }
            case "compacted":
            {
                await EarlierTurnsAsync(3).ConfigureAwait(false);
                var compaction = $"provider-compaction:{Guid.CreateVersion7()}";
                await AppendAsync([Update(null, AgentSessionUpdateKind.CompactionStarted), Update(compaction, AgentSessionUpdateKind.CompactionCompleted)]).ConfigureAwait(false);
                await AppendAsync(Turn(Guid.CreateVersion7().ToString(), 4, toolCalls: 1, outputBytes: 512)).ConfigureAwait(false);
                await AppendAsync([Update(null, AgentSessionUpdateKind.Warning)]).ConfigureAwait(false);
                break;
            }
            case "answerOfAnEarlierRun":
            {
                // Runs that do not follow one another: a run answers again after a later one started.
                await EarlierTurnsAsync(3).ConfigureAwait(false);
                var earlier = Guid.CreateVersion7().ToString();
                await AppendAsync(Turn(earlier, 4, toolCalls: 1, outputBytes: 512)).ConfigureAwait(false);
                await AppendAsync(Turn(Guid.CreateVersion7().ToString(), 5, toolCalls: 1, outputBytes: 512)).ConfigureAwait(false);
                await AppendAsync([Content(earlier, AgentContentKind.Assistant, "late answer")]).ConfigureAwait(false);
                break;
            }
            case "answerOfTheProvider":
            {
                await EarlierTurnsAsync(3).ConfigureAwait(false);
                var provider = $"provider-remote-control:{Guid.CreateVersion7()}";
                await AppendAsync([Content(provider, AgentContentKind.User, "remote"), Content(provider, AgentContentKind.Assistant, "remote answer")]).ConfigureAwait(false);
                break;
            }
            case "answerWithoutRun":
                await EarlierTurnsAsync(3).ConfigureAwait(false);
                await AppendAsync([Content(null, AgentContentKind.Assistant, "note"), Update(null, AgentSessionUpdateKind.Warning)]).ConfigureAwait(false);
                break;
            case "oneTurn":
                await AppendAsync(Turn(Guid.CreateVersion7().ToString(), 1, toolCalls: 6, outputBytes: 16 * 1024)).ConfigureAwait(false);
                break;
            case "noAnswer":
                await AppendAsync(Turn(Guid.CreateVersion7().ToString(), 1, toolCalls: 3, outputBytes: 16 * 1024, answer: false)).ConfigureAwait(false);
                await AppendAsync(Turn(Guid.CreateVersion7().ToString(), 2, toolCalls: 1, outputBytes: 512, answer: false)).ConfigureAwait(false);
                break;
            case "empty":
                break;
            case "corruptInLastTurn":
            case "corruptBeforeLastTurn":
            {
                if (scenario == "corruptBeforeLastTurn")
                {
                    await File.AppendAllTextAsync(journal, "{not-json\n").ConfigureAwait(false);
                }

                // More than the window of a page of the end of the journal: 256 KiB and 100 records.
                await EarlierTurnsAsync(scenario == "corruptBeforeLastTurn" ? 10 : 2).ConfigureAwait(false);
                var last = Guid.CreateVersion7().ToString();
                var events = Turn(last, 3, toolCalls: 1, outputBytes: 512);
                await AppendAsync(events.Take(2).ToArray()).ConfigureAwait(false);
                if (scenario == "corruptInLastTurn")
                {
                    await File.AppendAllTextAsync(journal, "{not-json\n").ConfigureAwait(false);
                }

                await AppendAsync(events.Skip(2).ToArray()).ConfigureAwait(false);
                break;
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null);
        }

        return sessionId;
    }

    private static SessionRuntimeService CreateRuntime(CatalogOptions options, ModelProviderRegistry registry, IAgentSessionCatalog agentSessionCatalog)
    {
        var skillCatalog = new SkillCatalog();
        return new SessionRuntimeService(
            new AgentHub(registry),
            agentSessionCatalog,
            new ProjectCatalog(options),
            new SessionViewCatalog(options),
            new AgentInstructionTemplateProvider(skillCatalog, options),
            options,
            skillCatalog);
    }

    // Gives the infos of the listing of every session, as the commands read them before they read sessions by their
    // identifiers and asked the runtime only about the sessions they write.
    private sealed class ListingSessionQueryService : IAltaSessionQueryService
    {
        public IAsyncEnumerable<AltaSessionInfo> LoadAsync(AltaCommandContext context) => new AltaSessionQueryService().LoadAsync(context);
    }

    private sealed class CountingSessionCatalog(IAgentSessionCatalog inner) : IAgentSessionCatalog
    {
        private int _listCount;

        public int ListCount => Volatile.Read(ref _listCount);

        public IAsyncEnumerable<AgentSessionMetadata> ListSessionsAsync(AgentSessionListFilter? filter = null, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _listCount);
            return inner.ListSessionsAsync(filter, cancellationToken);
        }

        public Task InvalidateAsync(CancellationToken cancellationToken = default) => inner.InvalidateAsync(cancellationToken);

        public Task InvalidateAsync(string sessionId, CancellationToken cancellationToken = default) => inner.InvalidateAsync(sessionId, cancellationToken);

        public Task NotifySessionCreatedAsync(string sessionId, CancellationToken cancellationToken = default) => inner.NotifySessionCreatedAsync(sessionId, cancellationToken);

        public Task NotifySessionResumedAsync(string sessionId, CancellationToken cancellationToken = default) => inner.NotifySessionResumedAsync(sessionId, cancellationToken);

        public Task NotifySessionDeletedAsync(string sessionId, CancellationToken cancellationToken = default) => inner.NotifySessionDeletedAsync(sessionId, cancellationToken);

        public Task<bool> DeleteSessionAsync(string sessionId, CancellationToken cancellationToken = default) => inner.DeleteSessionAsync(sessionId, cancellationToken);

        public Task NotifySessionUpdatedAsync(string sessionId, CancellationToken cancellationToken = default) => inner.NotifySessionUpdatedAsync(sessionId, cancellationToken);
    }

    // Measures what the session queries of `alta` cost over many stored sessions: 500 small sessions that each name an
    // agent prompt (8 prompt files), one session of many turns and one session of one long turn. It prints the median
    // and the fastest time of each command; run it with `CODEALTA_TEST_SESSION_QUERY_BENCHMARK=1` and a detailed
    // console logger, for example `dotnet test -c Release --filter SessionQueryBenchmark
    // --logger "console;verbosity=detailed"`.
    [TestMethod]
    public async Task SessionQueryBenchmark_MeasuresTheSessionCommandsOverManyStoredSessions()
    {
        if (Environment.GetEnvironmentVariable(SessionQueryBenchmarkVariable) is not { Length: > 0 })
        {
            Assert.Inconclusive($"Set {SessionQueryBenchmarkVariable}=1 to measure the session commands of alta over 500 stored sessions.");
        }

        using var root = TempDirectory.Create();
        var options = new CatalogOptions { GlobalRoot = root.Path };
        var providerId = new ModelProviderId("bench");
        var agents = Path.Combine(root.Path, "prompts", "agents");
        Directory.CreateDirectory(agents);
        for (var index = 0; index < 8; index++)
        {
            await File.WriteAllTextAsync(Path.Combine(agents, $"agent{index}.prompt.md"),
                $"---\nname: Agent {index}\ndescription: Benchmark agent {index}.\n---\nYou are agent {index}.\n{new string('x', 4000)}\n").ConfigureAwait(false);
        }

        var catalog = new SessionViewCatalog(options);
        var store = catalog.JournalStore.CreateSessionStore();
        var start = new DateTimeOffset(2026, 10, 1, 8, 0, 0, TimeSpan.Zero);
        var setup = Stopwatch.StartNew();
        string? parentId = null;
        string? firstId = null;
        var ids = new List<string>();
        for (var index = 0; index < 500; index++)
        {
            var sessionId = Guid.CreateVersion7(start.AddMinutes(index)).ToString();
            firstId ??= sessionId;
            ids.Add(sessionId);
            if (index % 10 == 0)
            {
                parentId = sessionId;
            }

            await WriteBenchmarkSessionAsync(catalog, store, providerId, root.Path, sessionId, start.AddMinutes(index),
                index % 10 == 0 ? null : parentId, runs: 1, toolCalls: 2, toolOutputBytes: 512).ConfigureAwait(false);
        }

        var manyTurnsId = Guid.CreateVersion7(start.AddDays(1)).ToString();
        await WriteBenchmarkSessionAsync(catalog, store, providerId, root.Path, manyTurnsId, start.AddDays(1), parentSessionId: firstId,
            runs: 20, toolCalls: 30, toolOutputBytes: 16 * 1024).ConfigureAwait(false);
        var oneTurnId = Guid.CreateVersion7(start.AddDays(2)).ToString();
        await WriteBenchmarkSessionAsync(catalog, store, providerId, root.Path, oneTurnId, start.AddDays(2), parentSessionId: firstId,
            runs: 1, toolCalls: 500, toolOutputBytes: 16 * 1024).ConfigureAwait(false);
        Console.WriteLine($"setup: {setup.Elapsed.TotalSeconds:F1} s, 502 sessions");

        var runtime = CreateRuntime(options, providerId);
        await using var _ = runtime.ConfigureAwait(false);
        var dispatcher = CreateDispatcher(new AltaServiceCollection()
            .Add(options)
            .Add(new ProjectCatalog(options))
            .Add(catalog)
            .Add(runtime));
        var smallId = ids[255];
        foreach (var (label, args, repeat) in new (string, string[], int)[]
        {
            ("report <small session>", ["session", "report", smallId, "--include=result,metrics"], 15),
            ("report <5 small sessions>", ["session", "report", smallId, ids[256], ids[257], ids[258], ids[259], "--include=result,metrics"], 15),
            ("report <20 turns, 10 MiB>", ["session", "report", manyTurnsId, "--include=result,metrics"], 7),
            ("report <1 turn, 8 MiB>", ["session", "report", oneTurnId, "--include=result,metrics"], 7),
            ("result <small session>", ["session", "result", smallId], 15),
            ("info <parent session>", ["session", "info", parentId!], 15),
            ("children <parent session>", ["session", "children", parentId!], 15),
            ("list --limit 20", ["session", "list", "--limit", "20"], 15),
        })
        {
            var times = new List<double>();
            for (var iteration = 0; iteration < repeat + 2; iteration++)
            {
                var watch = Stopwatch.StartNew();
                var result = await dispatcher.InvokeAsync(args, caller: AltaCallerIdentity.Cli).ConfigureAwait(false);
                watch.Stop();
                Assert.AreEqual(AltaExitCodes.Success, result.ExitCode, result.Stdout);
                Assert.IsFalse(result.Stdout.Contains("not_found", StringComparison.Ordinal), result.Stdout);
                if (iteration >= 2)
                {
                    times.Add(watch.Elapsed.TotalMilliseconds);
                }
            }

            times.Sort();
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{label,-28} median {times[times.Count / 2],9:F1} ms   fastest {times[0],9:F1} ms"));
        }
    }

    private static SessionViewDescriptor CreateBenchmarkDescriptor(ModelProviderId providerId, string workingDirectory, string sessionId, DateTimeOffset createdAt)
        => new()
        {
            SessionId = sessionId,
            Kind = SessionViewKind.GlobalSession,
            ProviderId = providerId.Value,
            ProviderKey = providerId.Value,
            WorkingDirectory = workingDirectory,
            Title = "Benchmark " + sessionId,
            Status = SessionViewStatus.Active,
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
            LastActiveAt = createdAt,
        };

    private static SessionViewQueuedPrompt CreateQueuedPrompt(string id, string state, DateTimeOffset createdAt)
        => new() { QueueItemId = id, Kind = "prompt", Prompt = "Prompt " + id, PromptPreview = "Prompt " + id, State = state, CreatedAt = createdAt };

    private static async Task WriteBenchmarkSessionAsync(SessionViewCatalog catalog, FileSystemAgentSessionStore store, ModelProviderId providerId,
        string workingDirectory, string sessionId, DateTimeOffset createdAt, string? parentSessionId, int runs, int toolCalls, int toolOutputBytes)
    {
        var descriptor = CreateBenchmarkDescriptor(providerId, workingDirectory, sessionId, createdAt);
        var state = new SessionViewLocalState { AgentPromptId = "agent3", ParentSessionId = parentSessionId, MessageCount = runs * 2 };
        await catalog.JournalStore.AppendStateAsync(descriptor, state).ConfigureAwait(false);
        var at = createdAt;
        await store.UpsertSessionAsync(new AgentSessionSummary
        {
            SessionId = sessionId,
            ProviderId = providerId,
            ProtocolFamily = "openai",
            ProviderKey = providerId.Value,
            ModelId = "bench-model",
            WorkingDirectory = workingDirectory,
            Title = descriptor.Title,
            Summary = "Benchmark session",
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
        }).ConfigureAwait(false);
        // Source text, as a tool output usually is: lines, indentation and quotes the journal escapes.
        var line = "    var value = \"text\"; // a line of a file\n";
        var output = string.Concat(Enumerable.Repeat(line, Math.Max(1, toolOutputBytes / line.Length)));
        for (var run = 0; run < runs; run++)
        {
            var runId = new AgentRunId(Guid.CreateVersion7(at).ToString());
            var events = new List<AgentEvent>
            {
                new AgentContentCompletedEvent(providerId, sessionId, at, runId, AgentContentKind.User, $"user-{run}", null, "Please do the next part of the work."),
            };
            for (var call = 0; call < toolCalls; call++)
            {
                at = at.AddSeconds(1);
                var activityId = $"tool-{run}-{call}";
                events.Add(new AgentActivityEvent(providerId, sessionId, at, runId, AgentActivityKind.ToolCall, AgentActivityPhase.Requested, activityId, null, "read_file", null));
                events.Add(new AgentContentCompletedEvent(providerId, sessionId, at, runId, AgentContentKind.ToolOutput, $"output-{run}-{call}", activityId, output));
                events.Add(new AgentSessionUpdateEvent(providerId, sessionId, at, runId, AgentSessionUpdateKind.UsageUpdated, "Usage updated.",
                    Usage: new AgentSessionUsage(
                        Window: new AgentWindowUsageSnapshot(1000 + call, 200_000, call + 1, "window"),
                        LastOperation: new AgentOperationUsageSnapshot(Model: "bench-model", InputTokens: 1000 + call, OutputTokens: 40, CachedInputTokens: 500, Cost: 0.01, CostUnit: "USD"),
                        Scope: AgentUsageScope.CurrentWindow,
                        Source: AgentUsageSource.ProviderUsage,
                        UpdatedAt: at)));
                events.Add(new AgentContentCompletedEvent(providerId, sessionId, at, runId, AgentContentKind.Assistant, $"assistant-{run}-{call}", null, $"Step {call} of turn {run} is done."));
            }

            at = at.AddSeconds(1);
            events.Add(new AgentSessionUpdateEvent(providerId, sessionId, at, runId, AgentSessionUpdateKind.Idle, "Idle"));
            await store.AppendEventsAsync("openai", providerId.Value, sessionId, events).ConfigureAwait(false);
            at = at.AddMinutes(1);
        }

        await catalog.JournalStore.AppendStateAsync(descriptor, state).ConfigureAwait(false);
    }
}
