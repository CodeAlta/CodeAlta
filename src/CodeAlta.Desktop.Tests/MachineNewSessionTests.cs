using System.ComponentModel;
using System.Diagnostics;
using CodeAlta.Agent;
using CodeAlta.Catalog.SessionMoves;
using CodeAlta.Desktop.Fleet;
using CodeAlta.Desktop.Rpc;
using CodeAlta.Desktop.SessionHosts;
using CodeAlta.Fleet.Client;
using CodeAlta.Fleet.NodeApi;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Desktop.Tests;

/// <summary>
/// A new session on a paired machine (the machines service against a real node in this process), and the project of
/// this catalog a session of a machine goes under (its repository key).
/// </summary>
[TestClass]
public sealed class MachineNewSessionTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    private static readonly RemoteSessionHostOptions Quick = new()
    {
        MinimumBackoff = TimeSpan.FromMilliseconds(50), MaximumBackoff = TimeSpan.FromMilliseconds(200), ListInterval = TimeSpan.FromMilliseconds(200),
    };

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task TheProvidersOfAMachine_AreListedForAnAutomation_ThoughThisComputerMayNotStartSessionsThere()
    {
        var token = TestContext.CancellationToken;
        await using var fixture = await FleetNodeFixture.StartAsync(token);
        var registry = new SessionHostRegistry(new SessionHostRegistryTests.RecordingSessionHost(null));
        await using var fleet = new DesktopFleet(Path.Combine(fixture.Root, "desktop-fleet"), registry.TakeMembership(), "desktop",
            static (machine, identity) => new RemoteSessionHost(machine, cancellation => FleetNodeClient.ConnectAsync(machine, identity, cancellation), Quick,
                localDeviceId: identity.DeviceId));
        var machine = await fleet.PairAsync(fixture.CreatePairingLink(), token);
        var host = fleet.Find(machine.DeviceId)!;
        await WaitForAsync(() => host.State == RemoteMachineState.Online, "the machine to be online", token);
        // As a machine reached through a server grants: this computer may give it automations, not start sessions there.
        fixture.Service.TrustedDevices.SetPermissions(fleet.DeviceId, CodeAlta.Fleet.Identity.DevicePermissions.View | CodeAlta.Fleet.Identity.DevicePermissions.Automate);
        var epoch = Guid.NewGuid().ToString("D");
        var service = new MachinesService(fleet, epoch);

        Assert.AreEqual("not_allowed", (await service.NewSessionChoicesAsync(new MachineNewSessionChoicesRequest(epoch, machine.DeviceId, null), token)).Status,
            "A session is not started there.");
        var providers = await service.NewSessionChoicesAsync(new MachineNewSessionChoicesRequest(epoch, machine.DeviceId, null) { ProvidersOnly = true }, token);
        Assert.AreEqual("ok", providers.Status, providers.Message);
        Assert.AreEqual(("scripted", "Scripted"), (providers.Providers.Single().Key, providers.Providers.Single().Name), "What an automation placed there runs with.");
        Assert.AreEqual(0, providers.Projects.Length, "Its projects are not what an automation is placed with.");
    }

    [TestMethod]
    public async Task NewSession_OnAMachine_ChoosesItsProjectProviderAndModel_AndIsListedUnderTheSameRepositoryHere()
    {
        var token = TestContext.CancellationToken;
        await using var fixture = await FleetNodeFixture.StartAsync(token);
        // The same repository on both computers, cloned from two forms of its URL into folders of other names.
        var nodeCheckout = CreateRepository(fixture.Root, "node-sample", "git@github.com:CodeAlta/Sample.git");
        var localCheckout = CreateRepository(fixture.Root, "my-copy", "https://github.com/codealta/sample");
        var nodeProject = await fixture.Host.ProjectCatalog.UpsertFromPathAsync(nodeCheckout, token);
        var other = await fixture.Host.ProjectCatalog.UpsertFromPathAsync(Directory.CreateDirectory(Path.Combine(fixture.Root, "notes")).FullName, token);

        var registry = new SessionHostRegistry(new SessionHostRegistryTests.RecordingSessionHost(null));
        await using var fleet = new DesktopFleet(Path.Combine(fixture.Root, "desktop-fleet"), registry.TakeMembership(), "desktop",
            static (machine, identity) => new RemoteSessionHost(machine, cancellation => FleetNodeClient.ConnectAsync(machine, identity, cancellation), Quick,
                localDeviceId: identity.DeviceId));
        var machine = await fleet.PairAsync(fixture.CreatePairingLink(), token);
        var host = fleet.Find(machine.DeviceId)!;
        await WaitForAsync(() => host.State == RemoteMachineState.Online, "the machine to be online", token);

        var epoch = Guid.NewGuid().ToString("D");
        var service = new MachinesService(fleet, epoch)
        {
            LocalLocation = (id, cancellation) => id == "local-sample" ? fleet.Locations.GetAsync(localCheckout, cancellation) : Task.FromResult<RepositoryLocation?>(null),
        };

        var choices = await service.NewSessionChoicesAsync(new MachineNewSessionChoicesRequest(epoch, machine.DeviceId, "local-sample"), token);
        Assert.AreEqual("ok", choices.Status, choices.Message);
        Assert.AreEqual("test-node", choices.MachineName);
        Assert.AreEqual("github.com/codealta/sample", choices.Projects.Single(project => project.Id == nodeProject.Id).RepositoryKey);
        Assert.IsNull(choices.Projects.Single(project => project.Id == other.Id).RepositoryKey, "A folder of no repository has no key.");
        Assert.AreEqual(nodeProject.Id, choices.SuggestedProjectId, "The project of the machine in the same repository as the one here is suggested.");
        var provider = choices.Providers.Single();
        Assert.AreEqual("scripted", provider.Key);
        Assert.IsTrue(provider.IsDefault);
        Assert.AreEqual(ScriptedFleetProvider.Model, provider.Models.Single().Id, "The models of a ready provider of the machine are offered.");
        Assert.IsNull((await service.NewSessionChoicesAsync(new MachineNewSessionChoicesRequest(epoch, machine.DeviceId, null), token)).SuggestedProjectId);
        Assert.AreEqual("stale_epoch", (await service.NewSessionChoicesAsync(new MachineNewSessionChoicesRequest("other", machine.DeviceId, null), token)).Status);
        Assert.AreEqual("not_found", (await service.NewSessionChoicesAsync(new MachineNewSessionChoicesRequest(epoch, "unknown", null), token)).Status);

        Assert.AreEqual("invalid_request", (await service.NewSessionAsync(new MachineNewSessionRequest(epoch, machine.DeviceId, "new-0", nodeProject.Id, "Hello",
            null, ScriptedFleetProvider.Model), token)).Status, "A model is named with its provider.");
        Assert.AreNotEqual("ok", (await service.NewSessionAsync(new MachineNewSessionRequest(epoch, machine.DeviceId, "new-x", nodeProject.Id, "Hello", "missing"), token)).Status,
            "A provider the machine does not have is refused.");

        var created = await service.NewSessionAsync(new MachineNewSessionRequest(epoch, machine.DeviceId, "new-1", nodeProject.Id, "Hello from the desktop",
            "scripted", ScriptedFleetProvider.Model), token);
        Assert.AreEqual("ok", created.Status, created.Message);
        Assert.IsTrue(host.Lists(created.SessionId!), "The answer comes once the machine lists the session: the next list of the page has it.");
        // The machine names its project once the journal of the session records it, with its first prompt.
        await WaitForAsync(() => host.Listing.Sessions.Single(session => session.Id == created.SessionId).ProjectId is not null, "the project of the session", token);
        var listed = host.Listing.Sessions.Single(session => session.Id == created.SessionId);
        Assert.AreEqual(nodeProject.Id, listed.ProjectId);
        Assert.AreEqual("scripted", listed.ProviderKey);
        var again = await service.NewSessionAsync(new MachineNewSessionRequest(epoch, machine.DeviceId, "new-1", nodeProject.Id, "Hello from the desktop",
            "scripted", ScriptedFleetProvider.Model), token);
        Assert.AreEqual(created.SessionId, again.SessionId, "A retry of the same creation is the same session.");

        // The Explorer: the session goes under the project here of the same repository, whatever its name and folder.
        var local = new WorkspaceSnapshot(true, [new WorkspaceProject("local-sample", "my-copy", localCheckout, false), new WorkspaceProject("p-notes", "notes", "/elsewhere", false)],
            [], false, false, false);
        var located = await WorkspaceService.ReadLocalLocationsAsync(local, [host], fleet.Locations, token);
        var row = WorkspaceService.WithRemoteSessions(local, [host], located).Sessions.Single(session => session.Id == created.SessionId);
        Assert.AreEqual("local-sample", row.ProjectId);
        Assert.AreEqual(localCheckout, row.WorkspacePath);
        Assert.AreEqual(machine.DeviceId, row.MachineId);
    }

    // A session started on a machine is given what a session started here is: the effort of its model, its agent prompt,
    // its permission mode, its sandbox and its options, among what that machine offers, never what asks less than it would.
    [TestMethod]
    public async Task NewSession_OnAMachine_TakesTheEffortAgentPromptModeSandboxAndOptionsTheMachineOffers()
    {
        var token = TestContext.CancellationToken;
        await using var fixture = await FleetNodeFixture.StartAsync(token);
        // Its sessions run in acceptEdits when they have no mode of their own.
        fixture.Provider.Descriptor = fixture.Provider.Descriptor with
        {
            PermissionModes = [SessionPermissionModes.Ask, SessionPermissionModes.AcceptEdits, SessionPermissionModes.Bypass], DefaultPermissionMode = SessionPermissionModes.AcceptEdits,
        };
        fixture.Provider.Register(fixture.Host.ModelProviderRegistry);
        var registry = new SessionHostRegistry(new SessionHostRegistryTests.RecordingSessionHost(null));
        await using var fleet = new DesktopFleet(Path.Combine(fixture.Root, "desktop-fleet"), registry.TakeMembership(), "desktop",
            static (machine, identity) => new RemoteSessionHost(machine, cancellation => FleetNodeClient.ConnectAsync(machine, identity, cancellation), Quick,
                localDeviceId: identity.DeviceId));
        var machine = await fleet.PairAsync(fixture.CreatePairingLink(), token);
        var host = fleet.Find(machine.DeviceId)!;
        await WaitForAsync(() => host.State == RemoteMachineState.Online, "the machine to be online", token);
        var epoch = Guid.NewGuid().ToString("D");
        var service = new MachinesService(fleet, epoch);

        var choices = await service.NewSessionChoicesAsync(new MachineNewSessionChoicesRequest(epoch, machine.DeviceId, null), token);
        Assert.AreEqual("ok", choices.Status, choices.Message);
        var provider = choices.Providers.Single();
        var model = provider.Models.Single();
        CollectionAssert.AreEqual(new[] { "Low", "High" }, model.Efforts, "The efforts of its model, as this computer names them.");
        Assert.AreEqual("High", model.DefaultEffort);
        CollectionAssert.AreEqual(new[] { SessionPermissionModes.Ask, SessionPermissionModes.AcceptEdits }, provider.PermissionModes,
            "The modes that ask at least as much as the one its sessions run in: never one that asks less.");
        Assert.AreEqual(SessionPermissionModes.AcceptEdits, provider.DefaultPermissionMode);
        Assert.IsTrue(choices.Prompts!.Any(static prompt => prompt.Id == "plan"), "Its agent prompts.");
        Assert.IsNotNull(choices.DefaultPromptId);
        Assert.IsNotNull(choices.SandboxProfiles, "Its sandbox profiles, empty where it cannot start a sandbox.");

        var before = (await fixture.Host.WorkspaceReads.ReadSnapshotAsync(token)).Sessions.Count;
        async Task RefusedAsync(string key, MachineNewSessionRequest request, string expected)
        {
            var refused = await service.NewSessionAsync(request with { ClientRequestId = key }, token);
            Assert.IsNull(refused.SessionId, key);
            Assert.AreNotEqual("ok", refused.Status, key);
            StringAssert.Contains(refused.Message, expected, key);
        }

        var basic = new MachineNewSessionRequest(epoch, machine.DeviceId, "", null, "Hello", "scripted", ScriptedFleetProvider.Model);
        await RefusedAsync("bypass", basic with { PermissionMode = SessionPermissionModes.Bypass }, "cannot ask less");
        await RefusedAsync("unknown-mode", basic with { PermissionMode = "plan" }, "offers the modes");
        await RefusedAsync("unknown-sandbox", basic with { Sandbox = "missing" }, "no sandbox profile 'missing'");
        await RefusedAsync("unknown-effort", basic with { ReasoningEffort = "Loud" }, "not a reasoning effort");
        await RefusedAsync("unknown-prompt", basic with { AgentPromptId = "missing" }, "no agent prompt 'missing'");
        Assert.IsTrue(fixture.Provider.Prompts.IsEmpty, "Nothing was sent.");
        Assert.AreEqual(before, (await fixture.Host.WorkspaceReads.ReadSnapshotAsync(token)).Sessions.Count, "A refusal leaves no session behind.");

        var created = await service.NewSessionAsync(basic with
        {
            ClientRequestId = "new-1", ReasoningEffort = "Low", AgentPromptId = "plan", PermissionMode = SessionPermissionModes.Ask, Sandbox = "off", SubSessions = false,
        }, token);
        Assert.AreEqual("ok", created.Status, created.Message);
        var id = created.SessionId!;
        await WaitForAsync(() => !fixture.Provider.Prompts.IsEmpty, "the first prompt", token);
        var current = (await fixture.Host.Commands.GetSelectionChoicesAsync(id, token))!.Current;
        Assert.AreEqual((ScriptedFleetProvider.Model, AgentReasoningEffort.Low, "plan"), (current.ModelId, current.ReasoningEffort, current.AgentPromptId));
        Assert.AreEqual(SessionPermissionModes.Ask, await fixture.Host.RuntimeService.GetPermissionModeAsync(id, token));
        Assert.AreEqual(CodeAlta.Sandbox.SandboxScope.Session, (await fixture.Host.RuntimeService.GetSessionSandboxAsync(id, token)).Choice.Source, "The sandbox chosen is the session's.");
        var snapshot = await fixture.Host.WorkspaceReads.ReadSnapshotAsync(token);
        Assert.IsFalse(snapshot.Sessions.Single(session => session.SessionId == id).ViewState?.Options?.SubSessions ?? true, "It starts no session of its own.");
    }

    [TestMethod]
    public void MatchProject_OfAProjectWithARepositoryKey_IsTheSameFolderOfTheSameRepository_AndNothingElse()
    {
        var sample = new WorkspaceProject("p1", "sample", "C:/code/sample", false);
        var copy = new WorkspaceProject("p2", "copy", "D:/work/copy", false);
        var tools = new WorkspaceProject("p3", "tools", "C:/code/sample/tools", false);
        var named = new WorkspaceProject("r1", "remote-name", "C:/code/remote-name", false);
        var locations = new Dictionary<string, RepositoryLocation>
        {
            ["p1"] = new("github.com/a/sample", ""), ["p2"] = new("github.com/a/sample", ""), ["p3"] = new("github.com/a/sample", "tools"),
        };
        RepositoryLocation? Location(WorkspaceProject project) => locations.GetValueOrDefault(project.Id);
        var remote = new FleetProjectInfo("r1", "remote-name", "/Users/me/x", []) { RepositoryKey = "github.com/a/sample", RepositoryPath = "" };

        Assert.AreSame(sample, WorkspaceService.MatchProject("r1", remote, [sample, tools, named], Location), "The same repository, whatever the id and the name.");
        Assert.AreSame(tools, WorkspaceService.MatchProject("r1", remote with { RepositoryPath = "Tools" }, [sample, tools], Location), "A subfolder project is matched by its path in the repository.");
        Assert.IsNull(WorkspaceService.MatchProject("r1", remote with { RepositoryKey = "github.com/a/other" }, [sample, named], Location),
            "A repository this catalog does not have goes under its machine, even beside a project of the same id and name.");
        // Several checkouts of one repository here: the same id, else the only one of the same name, else the first.
        Assert.AreSame(copy, WorkspaceService.MatchProject("p2", remote with { Id = "p2" }, [sample, copy], Location));
        Assert.AreSame(copy, WorkspaceService.MatchProject("r1", remote with { Name = "Copy" }, [sample, copy], Location));
        Assert.AreSame(sample, WorkspaceService.MatchProject("r1", remote, [sample, copy], Location));
        Assert.IsNull(WorkspaceService.MatchProject("r1", remote, [sample with { MachineId = "m" }], Location), "A row of a machine is no project of this catalog.");
    }

    [TestMethod]
    public void MatchProject_OfAProjectWithoutRepositoryKey_FallsBackToTheIdThenTheOnlyProjectOfTheName()
    {
        var sample = new WorkspaceProject("p1", "sample", "C:/code/sample", false);
        var twin = new WorkspaceProject("p2", "Sample", "D:/sample", false);
        var notes = new WorkspaceProject("p3", "notes", "C:/notes", false);
        var remote = new FleetProjectInfo("r1", "notes", "/Users/me/notes", []);
        static RepositoryLocation? None(WorkspaceProject _) => null;

        Assert.AreSame(sample, WorkspaceService.MatchProject("p1", remote with { Id = "p1" }, [sample, notes], None));
        Assert.AreSame(notes, WorkspaceService.MatchProject("r1", remote, [sample, notes], None));
        Assert.IsNull(WorkspaceService.MatchProject("r1", remote with { Name = "sample" }, [sample, twin], None), "Two projects of that name: none is chosen.");
        Assert.AreSame(sample, WorkspaceService.MatchProject("p1", null, [sample], None), "A project the machine does not describe is found by its id.");
        Assert.IsNull(WorkspaceService.MatchProject(null, null, [sample], None));
    }

    [TestMethod]
    public void Choices_SuggestTheOnlyProjectOfTheSameRepository_AndListTheReadyProvidersOfTheMachine()
    {
        var info = new FleetNodeInfo("mac-mini", "device", "macos", "4.0.0", [],
            [
                new FleetProjectInfo("b", "beta", "/x/beta", []) { RepositoryKey = "github.com/a/beta", RepositoryPath = "" },
                new FleetProjectInfo("a", "alpha", "/x/alpha", []) { RepositoryKey = "github.com/a/alpha", RepositoryPath = "" },
                new FleetProjectInfo("a2", "alpha-2", "/x/alpha-2", []) { RepositoryKey = "github.com/a/alpha", RepositoryPath = "" },
                new FleetProjectInfo("t", "beta-tools", "/x/beta/tools", []) { RepositoryKey = "github.com/a/beta", RepositoryPath = "tools" },
            ], ["view", "drive", "start"])
        {
            Providers = [new FleetProviderInfo("codex", "Codex", true, "gpt", [new FleetModelInfo("gpt", "GPT") { Efforts = ["Low", "High"], DefaultEffort = "High" }])
            {
                PermissionModes = ["default"], DefaultPermissionMode = "default", Sandbox = "workspace",
            }],
            AgentPrompts = [new FleetAgentPromptInfo("default", "Default"), new FleetAgentPromptInfo("plan", "Plan")], DefaultAgentPromptId = "default",
            SandboxProfiles = ["workspace"],
        };

        var choices = MachinesService.Choices(info, new RepositoryLocation("github.com/a/beta", ""));
        CollectionAssert.AreEqual(new[] { "alpha", "alpha-2", "beta", "beta-tools" }, choices.Projects.Select(project => project.Name).ToArray(), "By name.");
        Assert.AreEqual("b", choices.SuggestedProjectId);
        Assert.IsNull(MachinesService.Choices(info, new RepositoryLocation("github.com/a/alpha", "")).SuggestedProjectId, "Two checkouts there: the person chooses.");
        Assert.IsNull(MachinesService.Choices(info, null).SuggestedProjectId);
        var provider = choices.Providers.Single();
        Assert.AreEqual(("codex", "Codex", true, "gpt"), (provider.Key, provider.Name, provider.IsDefault, provider.DefaultModelId));
        Assert.AreEqual("GPT", provider.Models.Single().Name);
        CollectionAssert.AreEqual(new[] { "Low", "High" }, provider.Models.Single().Efforts);
        Assert.AreEqual(("default", "default", "workspace"), (provider.PermissionModes!.Single(), provider.DefaultPermissionMode, provider.Sandbox));
        CollectionAssert.AreEqual(new[] { "default", "plan" }, choices.Prompts!.Select(static prompt => prompt.Id).ToArray());
        CollectionAssert.AreEqual(new[] { "workspace" }, choices.SandboxProfiles);
        // An older machine says none of them: the page then leaves them to this computer, or out.
        var older = MachinesService.Choices(info with { AgentPrompts = null, SandboxProfiles = null, Providers = [new FleetProviderInfo("codex", "Codex", true, "gpt", [new FleetModelInfo("gpt", "GPT")])] }, null);
        Assert.IsNull(older.Prompts);
        Assert.IsNull(older.SandboxProfiles);
        Assert.IsNull(older.Providers.Single().PermissionModes);
        Assert.IsNull(older.Providers.Single().Models.Single().Efforts);
    }

    private static string CreateRepository(string root, string name, string origin)
    {
        var folder = Directory.CreateDirectory(Path.Combine(root, name)).FullName;
        try
        {
            Git(folder, "init", "-q");
        }
        catch (Win32Exception)
        {
            Assert.Inconclusive("git is not installed.");
        }

        Git(folder, "remote", "add", "origin", origin);
        return folder;
    }

    private static void Git(string folder, params string[] arguments)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = folder, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var error = process.StandardError.ReadToEndAsync();
        _ = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Assert.AreEqual(0, process.ExitCode, $"git {string.Join(' ', arguments)}: {error.Result}");
    }

    private static async Task WaitForAsync(Func<bool> condition, string what, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + Patience;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) Assert.Fail($"Waited too long for {what}.");
            await Task.Delay(50, cancellationToken);
        }
    }
}
