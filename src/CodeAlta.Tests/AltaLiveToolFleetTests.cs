using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Catalog;
using CodeAlta.LiveTool;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Tests;

/// <summary>`alta fleet` over fakes of the fleet of a host, and the refusals of what needs a person.</summary>
public sealed partial class AltaLiveToolTests
{
    private static readonly AltaCallerIdentity FleetAgent = new() { Kind = "agent", SourceSessionId = "session-a" };

    [TestMethod]
    public async Task FleetMachines_AreListedFilteredAndShown_WithTheirSessionsAndDevices()
    {
        var fleet = new FakeFleet();
        var dispatcher = CreateDispatcher(new AltaServiceCollection().Add<IAltaFleet>(fleet));

        var list = await dispatcher.InvokeAsync(["fleet", "machine", "list"], caller: FleetAgent);
        Assert.AreEqual(AltaExitCodes.Success, list.ExitCode, list.Transcript);
        var machines = Records(list, "alta.fleet.machine");
        CollectionAssert.AreEqual(new[] { "pc", "mac-mini", "build-box" }, machines.Select(static line => line.GetProperty("name").GetString()).ToArray());
        Assert.IsTrue(machines[0].GetProperty("thisComputer").GetBoolean());
        Assert.AreEqual(("ready", 1, "macos"), (machines[1].GetProperty("readiness").GetString(), machines[1].GetProperty("working").GetInt32(), machines[1].GetProperty("os").GetString()));
        Assert.AreEqual(3, Records(list, "alta.fleet.machine.summary").Single().GetProperty("count").GetInt32());

        // What a task needs: ready, a label, a capability, a provider.
        var ready = await dispatcher.InvokeAsync(["fleet", "machine", "list", "--ready", "--label", "GPU", "--capability", "sandbox"], caller: FleetAgent);
        CollectionAssert.AreEqual(new[] { "mac-mini" }, Records(ready, "alta.fleet.machine").Select(static line => line.GetProperty("name").GetString()).ToArray());
        var online = await dispatcher.InvokeAsync(["fleet", "machine", "list", "--online", "--provider", "codex"], caller: FleetAgent);
        CollectionAssert.AreEqual(new[] { "mac-mini" }, Records(online, "alta.fleet.machine").Select(static line => line.GetProperty("name").GetString()).ToArray());

        var show = await dispatcher.InvokeAsync(["fleet", "machine", "show", "mac-mini"], caller: FleetAgent);
        Assert.AreEqual(AltaExitCodes.Success, show.ExitCode, show.Transcript);
        var shown = Records(show, "alta.fleet.machine").Single();
        StringAssert.Contains(shown.GetProperty("note").GetString(), "alta session create --machine mac-mini");
        Assert.AreEqual("4.0.1", shown.GetProperty("update").GetProperty("pending").GetString());
        Assert.AreEqual(("p1", "github.com/a/app"), (Records(show, "alta.fleet.machine.project").Single().GetProperty("id").GetString(),
            Records(show, "alta.fleet.machine.project").Single().GetProperty("repository").GetString()));
        Assert.AreEqual("codex", Records(show, "alta.fleet.machine.provider").Single().GetProperty("key").GetString());
        var unknown = await dispatcher.InvokeAsync(["fleet", "machine", "show", "nowhere"], caller: FleetAgent);
        Assert.AreEqual(AltaExitCodes.NotFound, unknown.ExitCode);
        StringAssert.Contains(unknown.Transcript, "fleet.notFound");
        Assert.AreEqual(AltaExitCodes.Usage, (await dispatcher.InvokeAsync(["fleet", "machine", "show"], caller: FleetAgent)).ExitCode);

        var sessions = await dispatcher.InvokeAsync(["fleet", "machine", "sessions", "mac-mini", "--limit", "1"], caller: FleetAgent);
        Assert.AreEqual(AltaExitCodes.Success, sessions.ExitCode, sessions.Transcript);
        var session = Records(sessions, "alta.fleet.session").Single();
        Assert.AreEqual(("child-1", true), (session.GetProperty("id").GetString(), session.GetProperty("yours").GetBoolean()), "A child of the caller is marked as its own.");
        Assert.IsTrue(Records(sessions, "alta.fleet.session.summary").Single().GetProperty("truncated").GetBoolean());
        Assert.AreEqual(AltaExitCodes.Usage, (await dispatcher.InvokeAsync(["fleet", "machine", "sessions", "mac-mini", "--limit", "0"], caller: FleetAgent)).ExitCode);

        var devices = await dispatcher.InvokeAsync(["fleet", "machine", "devices", "mac-mini"], caller: FleetAgent);
        Assert.AreEqual(AltaExitCodes.Success, devices.ExitCode, devices.Transcript);
        CollectionAssert.AreEqual(new[] { "manage", "start" }, Records(devices, "alta.fleet.device").Single(static line => line.GetProperty("thisComputer").GetBoolean())
            .GetProperty("permissions").EnumerateArray().Select(static value => value.GetString()).ToArray());
        StringAssert.Contains(Records(devices, "alta.fleet.device.summary").Single().GetProperty("note").GetString(), "Read only");
        var notManaged = await dispatcher.InvokeAsync(["fleet", "machine", "devices", "build-box"], caller: FleetAgent);
        Assert.AreEqual(AltaExitCodes.PolicyDenied, notManaged.ExitCode);
        StringAssert.Contains(notManaged.Transcript, "fleet.notAllowed");

        // Without a fleet there is no command.
        Assert.AreNotEqual(AltaExitCodes.Success, (await CreateDispatcher(new AltaServiceCollection()).InvokeAsync(["fleet", "machine", "list"], caller: FleetAgent)).ExitCode);
    }

    [TestMethod]
    public async Task FleetMoves_SayWhatAMoveMeets_AndStartTheMoveOfSessionMove()
    {
        var fleet = new FakeFleet();
        var moves = new RecordingMachineMoves();
        var dispatcher = CreateDispatcher(new AltaServiceCollection().Add<IAltaFleet>(fleet).Add<IAltaSessionMachineMoves>(moves));

        var status = await dispatcher.InvokeAsync(["fleet", "move", "status", "session-b", "--machine", "mac-mini"], caller: FleetAgent);
        Assert.AreEqual(AltaExitCodes.Success, status.ExitCode, status.Transcript);
        var record = Records(status, "alta.fleet.move").Single();
        Assert.AreEqual(("pc", "mac-mini", "refused", "project_missing", true), (record.GetProperty("machine").GetString(), record.GetProperty("target").GetString(),
            record.GetProperty("targetStatus").GetString(), record.GetProperty("targetCode").GetString(), record.GetProperty("canClone").GetBoolean()));
        Assert.AreEqual(("session-b", "mac-mini"), fleet.MoveStatusAsked[0]);
        Assert.AreEqual(AltaExitCodes.Success, (await dispatcher.InvokeAsync(["fleet", "move", "status"], caller: FleetAgent)).ExitCode);
        Assert.AreEqual(("session-a", (string?)null), fleet.MoveStatusAsked[^1], "Without a session id, the calling session.");
        Assert.AreEqual(AltaExitCodes.NotFound, (await dispatcher.InvokeAsync(["fleet", "move", "status", "gone"], caller: FleetAgent)).ExitCode);

        // The caller moves itself when its turn ends.
        var moved = await dispatcher.InvokeAsync(["fleet", "move", "start", "--machine", "mac-mini", "--clone"], caller: FleetAgent);
        Assert.AreEqual(AltaExitCodes.Success, moved.ExitCode, moved.Transcript);
        Assert.AreEqual(("after-turn", "session-a", "mac-mini", true), moves.Calls.Single());
        var missing = await dispatcher.InvokeAsync(["fleet", "move", "start"], caller: FleetAgent);
        Assert.AreEqual(AltaExitCodes.Usage, missing.ExitCode);
        StringAssert.Contains(missing.Transcript, "alta fleet move start --help");
    }

    [TestMethod]
    public async Task FleetMoveStart_OfAnotherSession_KeepsToWhatTheCallerCouldDriveHere()
    {
        using var root = TempDirectory.Create();
        var options = new CatalogOptions { GlobalRoot = root.Path };
        var provider = new StatefulProviderRuntime(new ModelProviderId("fleet-move-provider")) { Models = [new AgentModelInfo("synthetic-model")] };
        var registry = new ModelProviderRegistry();
        registry.RegisterOrReplaceSessionRuntime(new ModelProviderDescriptor(provider.ProviderId, "Synthetic"), () => provider);
        await using var runtime = CreateRuntime(options, registry);
        var parent = await runtime.CreateGlobalSessionAsync(new SessionExecutionOptions
        {
            ProviderId = provider.ProviderId, ProviderKey = provider.ProviderId.Value, WorkingDirectory = root.Path, ProjectRoots = [],
            OnPermissionRequest = static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce)),
        }, "Parent");
        var remote = new RecordingRemoteChildren();
        runtime.RemoteChildren = remote;
        var moves = new RecordingMachineMoves();
        var dispatcher = CreateDispatcher(new AltaServiceCollection().Add(options).Add(runtime).Add(new SessionViewCatalog(options)).Add(new ProjectCatalog(options))
            .Add<IAltaFleet>(new FakeFleet()).Add<IAltaSessionMachineMoves>(moves));
        var agent = new AltaCallerIdentity { Kind = "agent", SourceSessionId = parent.SessionId };

        // A session of another machine that is not its child: the agent does not move it.
        var stranger = await dispatcher.InvokeAsync(["fleet", "move", "start", "someone-else", "--machine", "pc"], caller: agent);
        Assert.AreEqual(AltaExitCodes.PolicyDenied, stranger.ExitCode, stranger.Transcript);
        StringAssert.Contains(stranger.Transcript, "session.moveDenied");
        Assert.AreEqual(AltaExitCodes.PolicyDenied, (await dispatcher.InvokeAsync(["session", "move", "someone-else", "--machine", "pc"], caller: agent)).ExitCode,
            "`alta session move` keeps the same rule.");
        Assert.IsEmpty(moves.Calls);

        // Its child on another machine: it moves it.
        Assert.AreEqual(AltaExitCodes.Success, (await dispatcher.InvokeAsync(["session", "create", "--machine", "mac-mini", "--message", "Build it."], caller: agent)).ExitCode);
        var child = await dispatcher.InvokeAsync(["fleet", "move", "start", "child-1", "--machine", "pc"], caller: agent);
        Assert.AreEqual(AltaExitCodes.Success, child.ExitCode, child.Transcript);
        Assert.AreEqual(("now", "child-1", "pc", false), moves.Calls.Single());

        // The user, with no calling session, moves what they want.
        Assert.AreEqual(AltaExitCodes.Success, (await dispatcher.InvokeAsync(["fleet", "move", "start", "someone-else", "--machine", "pc"], caller: AltaCallerIdentity.Cli)).ExitCode);
    }

    [TestMethod]
    public async Task FleetAutomations_ArePlacedDisabledOnly_RunWhereTheyRun_AndTheirMachineRunsAreRead()
    {
        var fleet = new FakeFleet();
        var automations = new FakeAutomations();
        var dispatcher = CreateDispatcher(new AltaServiceCollection().Add<IAltaFleet>(fleet).Add<IAltaAutomations>(automations));

        var list = await dispatcher.InvokeAsync(["fleet", "automation", "list"], caller: FleetAgent);
        Assert.AreEqual(AltaExitCodes.Success, list.ExitCode, list.Transcript);
        var listed = Records(list, "alta.fleet.automation");
        Assert.AreEqual(("here", "copied", "build-box"), (listed[0].GetProperty("state").GetString(), listed[1].GetProperty("state").GetString(),
            listed[1].GetProperty("machine").GetString()));

        // What would run unattended is a person's choice: an enabled automation is not placed by an agent.
        var enabled = await dispatcher.InvokeAsync(["fleet", "automation", "place", "a-nightly", "--machine", "build-box"], caller: FleetAgent);
        Assert.AreEqual(AltaExitCodes.PolicyDenied, enabled.ExitCode, enabled.Transcript);
        StringAssert.Contains(enabled.Transcript, "fleet.needsPerson");
        Assert.IsEmpty(fleet.Placed);

        var placed = await dispatcher.InvokeAsync(["fleet", "automation", "place", "Weekly report", "--machine", "build-box"], caller: FleetAgent);
        Assert.AreEqual(AltaExitCodes.Success, placed.ExitCode, placed.Transcript);
        Assert.AreEqual(("a-weekly", "build-box"), fleet.Placed.Single());
        StringAssert.Contains(Records(placed, "alta.fleet.automation.placed").Single().GetProperty("note").GetString(), "a person enables it");
        Assert.AreEqual(AltaExitCodes.Success, (await dispatcher.InvokeAsync(["fleet", "automation", "place", "a-weekly", "--here"], caller: FleetAgent)).ExitCode);
        Assert.AreEqual(("a-weekly", (string?)null), fleet.Placed[^1]);
        Assert.AreEqual(AltaExitCodes.Usage, (await dispatcher.InvokeAsync(["fleet", "automation", "place", "a-weekly"], caller: FleetAgent)).ExitCode);
        Assert.AreEqual(AltaExitCodes.Usage, (await dispatcher.InvokeAsync(["fleet", "automation", "place", "a-weekly", "--machine", "x", "--here"], caller: FleetAgent)).ExitCode);
        Assert.AreEqual(AltaExitCodes.NotFound, (await dispatcher.InvokeAsync(["fleet", "automation", "place", "nothing", "--here"], caller: FleetAgent)).ExitCode);
        fleet.PlaceRefusal = "not_allowed";
        var notAllowed = await dispatcher.InvokeAsync(["fleet", "automation", "place", "a-weekly", "--machine", "mac-mini"], caller: FleetAgent);
        Assert.AreEqual(AltaExitCodes.PolicyDenied, notAllowed.ExitCode);
        StringAssert.Contains(notAllowed.Transcript, "fleet.notAllowed");

        // A session an automation started changes no automation.
        automations.StartedSessions.Add("session-a");
        Assert.AreEqual(AltaExitCodes.PolicyDenied, (await dispatcher.InvokeAsync(["fleet", "automation", "place", "a-weekly", "--here"], caller: FleetAgent)).ExitCode);
        automations.StartedSessions.Clear();

        // The user runs one now; the runs are those here and those its machine reported.
        var run = await dispatcher.InvokeAsync(["fleet", "automation", "run", "a-nightly"], caller: AltaCallerIdentity.Cli);
        Assert.AreEqual(AltaExitCodes.Success, run.ExitCode, run.Transcript);
        Assert.AreEqual("a-nightly", automations.Ran.Single());
        var runs = await dispatcher.InvokeAsync(["fleet", "automation", "runs", "--limit", "5"], caller: FleetAgent);
        Assert.AreEqual(AltaExitCodes.Success, runs.ExitCode, runs.Transcript);
        CollectionAssert.AreEqual(new[] { "run-machine", "run-here" }, Records(runs, "alta.automation.run").Select(static line => line.GetProperty("id").GetString()).ToArray(),
            "Newest first, from here and from the machines.");
    }

    [TestMethod]
    public async Task FleetStatisticsUpdatesAndHealth_AreRead_AndACampaignNeedsAPerson()
    {
        var fleet = new FakeFleet();
        var dispatcher = CreateDispatcher(new AltaServiceCollection().Add<IAltaFleet>(fleet));

        var statistics = await dispatcher.InvokeAsync(["fleet", "statistics", "--to", "2026-11-03", "--group-by", "machine,Provider"], caller: FleetAgent);
        Assert.AreEqual(AltaExitCodes.Success, statistics.ExitCode, statistics.Transcript);
        var query = fleet.Queries.Single();
        Assert.AreEqual(("2026-10-28", "2026-11-03", null, null), (query.From, query.To, query.TimeZone, query.Server), "Seven days up to --to by default.");
        CollectionAssert.AreEqual(new[] { "machine", "provider" }, query.GroupBy.ToArray());
        Assert.AreEqual(1200, Records(statistics, "alta.fleet.statistics").Single().GetProperty("totals").GetProperty("inputTokens").GetInt32());
        Assert.AreEqual("mac-mini", Records(statistics, "alta.fleet.statistics.group").Single().GetProperty("machine").GetString());
        Assert.AreEqual(AltaExitCodes.Usage, (await dispatcher.InvokeAsync(["fleet", "statistics", "--group-by", "colour"], caller: FleetAgent)).ExitCode);
        Assert.AreEqual(AltaExitCodes.Usage, (await dispatcher.InvokeAsync(["fleet", "statistics", "--from", "2026-12-01", "--to", "2026-11-01"], caller: FleetAgent)).ExitCode);
        fleet.NoServer = true;
        var none = await dispatcher.InvokeAsync(["fleet", "statistics"], caller: FleetAgent);
        Assert.AreEqual(AltaExitCodes.Unsupported, none.ExitCode);
        StringAssert.Contains(none.Transcript, "fleet.noServer");
        fleet.NoServer = false;

        var updates = await dispatcher.InvokeAsync(["fleet", "update", "status"], caller: FleetAgent);
        Assert.AreEqual(AltaExitCodes.Success, updates.ExitCode, updates.Transcript);
        Assert.AreEqual(("4.0.0", "manual"), (Records(updates, "alta.fleet.updates").Single().GetProperty("serverVersion").GetString(),
            Records(updates, "alta.fleet.updates").Single().GetProperty("policy").GetString()));
        Assert.AreEqual("4.0.1", Records(updates, "alta.fleet.update.campaign").Single().GetProperty("targetVersion").GetString());
        Assert.AreEqual(2, Records(updates, "alta.fleet.update.machine").Count);

        // A campaign replaces CodeAlta on the machines: a person starts it, whoever asks.
        foreach (var caller in new[] { FleetAgent, AltaCallerIdentity.Cli })
        {
            var start = await dispatcher.InvokeAsync(["fleet", "update", "start"], caller: caller);
            Assert.AreEqual(AltaExitCodes.PolicyDenied, start.ExitCode, start.Transcript);
            StringAssert.Contains(start.Transcript, "fleet.needsPerson");
        }

        var health = await dispatcher.InvokeAsync(["fleet", "health"], caller: FleetAgent);
        Assert.AreEqual(AltaExitCodes.Success, health.ExitCode, health.Transcript);
        Assert.AreEqual("online", Records(health, "alta.fleet.server").Single().GetProperty("connection").GetString());
        CollectionAssert.AreEqual(new[] { "build-box" }, Records(health, "alta.fleet.machine").Select(static line => line.GetProperty("name").GetString()).ToArray(),
            "Only the machines that do not take sessions now.");
        var summary = Records(health, "alta.fleet.health").Single();
        Assert.AreEqual((3, 2, 1), (summary.GetProperty("machines").GetInt32(), summary.GetProperty("ready").GetInt32(), summary.GetProperty("notReady").GetInt32()));
    }

    [TestMethod]
    public void FleetCommands_ThatStartOrChangeWorkElsewhere_AreRefusedToASandboxedSession_TheReadsAreNot()
    {
        foreach (var read in new[] { "machine list", "machine show", "machine sessions", "machine devices", "move status", "automation list", "automation runs", "statistics", "update status", "health" })
            Assert.IsTrue(AltaSandboxGate.Allows($"alta fleet {read}"), read);
        foreach (var change in new[] { "move start", "automation place", "automation run", "update start" })
            Assert.IsFalse(AltaSandboxGate.Allows($"alta fleet {change}"), change);
        Assert.IsFalse(AltaSandboxGate.Allows("alta session move"));
    }

    [TestMethod]
    public async Task FleetCommands_EachHaveHelpWithExamples_AndAPolicy()
    {
        using var root = TempDirectory.Create();
        var registry = new AltaCommandRegistry();
        var dispatcher = new AltaCommandDispatcher(registry, new AltaServiceCollection().Add(new CatalogOptions { GlobalRoot = root.Path }).Add(registry)
            .Add<IAltaFleet>(new FakeFleet()).Add<IAltaAutomations>(new FakeAutomations()).Add<IAltaSessionMachineMoves>(new RecordingMachineMoves()));
        var commands = dispatcher.Describe(AltaCallerIdentity.Cli, root.Path).Where(static command => command.Path.StartsWith("fleet ", StringComparison.Ordinal)).ToArray();
        Assert.HasCount(14, commands);
        foreach (var command in commands)
        {
            Assert.IsNotNull(command.Policy, command.Path);
            var help = await dispatcher.InvokeAsync([.. command.Path.Split(' '), "--help"], caller: AltaCallerIdentity.Cli);
            Assert.AreEqual(AltaExitCodes.Success, help.ExitCode, command.Path);
        }

        var group = await dispatcher.InvokeAsync(["fleet", "--help"], caller: AltaCallerIdentity.Cli);
        StringAssert.Contains(group.Stdout, "alta session create --machine");
        StringAssert.Contains(group.Stdout, "never a way out of a sandbox");
    }

    private static List<JsonElement> Records(AltaCommandResult result, string type)
        => [.. ReadJsonLines(result.Stdout).Where(line => line.GetProperty("type").GetString() == type)];

    private sealed class FakeFleet : IAltaFleet
    {
        public List<(string SessionId, string? Machine)> MoveStatusAsked { get; } = [];

        public List<(string Id, string? Machine)> Placed { get; } = [];

        public List<AltaFleetStatisticsQuery> Queries { get; } = [];

        public string? PlaceRefusal { get; set; }

        public bool NoServer { get; set; }

        private static readonly AltaFleetMachine Mac = new("m1", "mac-mini", "online", "ready")
        {
            Os = "macos", Version = "4.0.0", Capabilities = ["sandbox", "moves"], Labels = ["gpu"], Providers = ["codex"], Permissions = ["start", "manage"], Sessions = 2, Working = 1,
            Paired = true, Channel = "direct",
        };

        public IReadOnlyList<AltaFleetMachine> ListMachines() =>
        [
            new("", "pc", "online", "ready") { IsThisComputer = true, Os = "windows", Labels = ["gpu"] },
            Mac,
            new("m2", "build-box", "offline", "offline") { Os = "linux", Labels = ["gpu"], Capabilities = ["sandbox"], Problem = "No address answered." },
        ];

        public Task<AltaFleetMachineDetail> ShowMachineAsync(string machine, CancellationToken cancellationToken)
            => Task.FromResult(machine == "mac-mini"
                ? new AltaFleetMachineDetail("ok", null, Mac)
                {
                    Projects = [new("p1", "app", "github.com/a/app")],
                    Providers = [new("codex", "Codex", true, "gpt", ["gpt"])],
                    Update = new("4.0.0", "4.0.0", "4.0.1", "draining", null),
                }
                : new AltaFleetMachineDetail("not_found", null, null));

        public Task<AltaFleetSessions> ListSessionsAsync(string machine, int limit, CancellationToken cancellationToken)
            => Task.FromResult(new AltaFleetSessions("ok", null, "mac-mini",
            [
                new("child-1", "iOS tests", "working", "p1", "codex", "gpt", "session-a", DateTimeOffset.UnixEpoch.AddDays(2)),
                new("other", "Docs", "idle", null, "codex", "gpt", null, DateTimeOffset.UnixEpoch),
            ]));

        public Task<AltaFleetDevices> ListDevicesAsync(string machine, CancellationToken cancellationToken)
            => Task.FromResult(machine == "mac-mini"
                ? new AltaFleetDevices("ok", null, "mac-mini", [new("d1", "pc", ["manage", "start"], DateTimeOffset.UnixEpoch, null, true), new("d2", "phone", ["view"], DateTimeOffset.UnixEpoch, null, false)])
                : new AltaFleetDevices("not_manager", null, machine, []));

        public Task<AltaFleetMoveStatus> GetMoveStatusAsync(string sessionId, string? machine, CancellationToken cancellationToken)
        {
            MoveStatusAsked.Add((sessionId, machine));
            return Task.FromResult(sessionId == "gone"
                ? new AltaFleetMoveStatus("session_missing", null, sessionId, null)
                : new AltaFleetMoveStatus("ok", null, sessionId, "pc") { RunsHere = true, Target = machine, TargetStatus = machine is null ? null : "refused", TargetCode = machine is null ? null : "project_missing", CanClone = true });
        }

        public IReadOnlyList<AltaFleetAutomationPlacement> ListAutomationPlacements() =>
        [
            new("a-nightly", "Nightly review", true, null, null, "here", null),
            new("a-weekly", "Weekly report", false, "m2", "build-box", "copied", null) { CopiedAt = DateTimeOffset.UnixEpoch },
        ];

        public IReadOnlyList<AltaAutomationRun> ListPlacedRuns(string? automationId, int limit)
            => [new("run-machine", "a-weekly", "Weekly report", "s-9", null, DateTimeOffset.UnixEpoch.AddDays(3), null, "weekly", null, "completed", null)];

        public Task<AltaFleetChange> PlaceAutomationAsync(string automationId, string? machine, CancellationToken cancellationToken)
        {
            if (PlaceRefusal is { } refusal) return Task.FromResult(new AltaFleetChange(refusal, "The owner did not allow it."));
            Placed.Add((automationId, machine));
            return Task.FromResult(new AltaFleetChange("ok"));
        }

        public Task<AltaFleetStatistics> QueryStatisticsAsync(AltaFleetStatisticsQuery query, CancellationToken cancellationToken)
        {
            Queries.Add(query);
            var measures = new AltaFleetMeasures(1000, 4, 0, 10, 1200, 300, 7, 0, new Dictionary<string, decimal> { ["usd"] = 0.12m });
            return Task.FromResult(NoServer ? new AltaFleetStatistics("no_server", null, null, [])
                : new AltaFleetStatistics("ok", null, measures, [new("mac-mini", null, "codex", null, null, measures)]) { Server = "https://fleet.example" });
        }

        public Task<AltaFleetUpdates> ReadUpdatesAsync(string? server, CancellationToken cancellationToken)
            => Task.FromResult(new AltaFleetUpdates("ok", null)
            {
                Server = "https://fleet.example", ServerVersion = "4.0.0", Policy = "manual",
                Campaigns = [new("c1", "running", "4.0.1", DateTimeOffset.UnixEpoch, false, ["mac-mini: updated", "build-box: pending"])],
                Machines = [new("m1", "mac-mini", "4.0.1", true), new("m2", "build-box", "4.0.0", false)],
            });

        public IReadOnlyList<AltaFleetServer> ListServers() => [new("link-1", "https://fleet.example", "Contoso", "online", null)];
    }

    private sealed class FakeAutomations : IAltaAutomations
    {
        public List<string> Ran { get; } = [];

        public HashSet<string> StartedSessions { get; } = [];

        private static AltaAutomationRun Run(string id, string automationId, int day)
            => new(id, automationId, "Nightly review", "s-1", null, DateTimeOffset.UnixEpoch.AddDays(day), null, "manual", null, "completed", null);

        public IReadOnlyList<AltaAutomation> List() =>
        [
            new("a-nightly", "Nightly review", true, "Review.", null, null, "config.toml", null, null, ["daily@23:00"], null, null, false),
            new("a-weekly", "Weekly report", false, "Report.", null, null, "config.toml", null, null, ["weekly@mon@08:00"], null, null, false),
        ];

        public IReadOnlyList<AltaAutomationRun> ListRuns(string? id, int limit) => [Run("run-here", "a-nightly", 1)];

        public AltaAutomationRun? FindRunOfSession(string sessionId) => StartedSessions.Contains(sessionId) ? Run("run-here", "a-nightly", 1) : null;

        public Task<AltaAutomationRun?> RunAsync(string id, CancellationToken cancellationToken)
        {
            Ran.Add(id);
            return Task.FromResult<AltaAutomationRun?>(Run("run-now", id, 4));
        }

        public Task<AltaAutomationChange> CreateAsync(AltaAutomationRequest request, CancellationToken cancellationToken) => Task.FromResult(new AltaAutomationChange("refused"));

        public Task<AltaAutomationChange> SetEnabledAsync(string id, bool enabled, CancellationToken cancellationToken) => Task.FromResult(new AltaAutomationChange("ok", id));

        public Task<AltaAutomationChange> DeleteAsync(string id, CancellationToken cancellationToken) => Task.FromResult(new AltaAutomationChange("ok", id));
    }
}
