using System.Collections.Concurrent;
using System.Collections.Frozen;
using CodeAlta.Desktop.Fleet;
using CodeAlta.Desktop.Rpc;
using CodeAlta.Fleet.Identity;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Fleet.Protocol.Api;
using CodeAlta.Fleet.Server;
using CodeAlta.Orchestration.Hosting;
using CodeAlta.Server.Hosting;

namespace CodeAlta.Desktop.Tests;

/// <summary>
/// An automation of a team changed by another maintainer than the one who gave it (<c>doc/fleet/orchestration.md</c>, scopes):
/// her computer lists it, opens what it does with its own key, and takes it into her editor as its machine holds it; the
/// version her editor saves is signed by her computer, taken by the server, and opened by the computer of the first maintainer.
/// </summary>
[TestClass]
public sealed class FleetTeamAutomationEditTests
{
    private const string Id = "01a1a7c0-3b2e-7d41-9c55-6e0f2a7b9c13";
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

    public TestContext TestContext { get; set; } = null!;

    private static string Definition(string name) => $$"""
        [automations.{{Id}}]
        name = "{{name}}"
        prompt = "Summarize the builds of the night."
        triggers = [{ type = "daily", at = ["06:00"] }]
        """;

    [TestMethod]
    public async Task AnotherMaintainerOpensAnAutomationOfTheTeamInHerEditor_AndTheVersionSheSavesIsTheTeams()
    {
        var token = TestContext.CancellationToken;
        var root = Directory.CreateTempSubdirectory("codealta-fleet-team-edit-").FullName;
        string At(string name) => Directory.CreateDirectory(Path.Combine(root, name)).FullName;
        try
        {
            await using var server = await CoordinatorServer.StartAsync(new CoordinatorServerOptions
            {
                DataRoot = At("server"), Urls = ["http://127.0.0.1:0"], ConfigureLogging = static _ => { }, EnvironmentVariable = static _ => null,
            }, token);
            var address = server.Addresses.First(static uri => uri.Scheme == "http");

            // Alex's computer sets the server up and his machine joins it.
            await using var alexHost = await HostAsync(At("alex"), root, token);
            var alexAutomations = new FakeHandover();
            await using var alexServers = new DesktopFleetServers(alexHost, "Alex desktop", "4.0.0") { Handover = alexAutomations };
            var alexEpoch = Guid.NewGuid().ToString("D");
            var alex = new FleetServerService(alexServers, alexEpoch) { Spaces = alexHost.SpaceCatalog, IsPaired = static _ => false };
            var created = await alex.CreateAsync(new FleetServerCreateRequest(alexEpoch, address.ToString(), server.SetupCode!.Code, "Alex", "Home"), token);
            Assert.AreEqual("ok", created.Status, created.Message);
            var linkId = created.Server!.Id;
            using var nodeKey = DeviceIdentity.LoadOrCreate(At("node"));
            var enrollment = await alex.CreateTokenAsync(new FleetServerTokenRequest(alexEpoch, linkId, 2, "Build box"), token);
            using (var node = new FleetServerClient(address, nodeKey)) await node.EnrollAsync(enrollment.Token!, "Build box", FleetDeviceKinds.Node, token);
            Assert.AreEqual("ok", (await alex.ApproveAsync(new FleetServerDeviceRequest(alexEpoch, linkId, nodeKey.DeviceId, null, FleetDeviceKinds.Node), token)).Status);

            // Bea's computer joins.
            var invitation = await alex.InvitePersonAsync(new FleetAccessInviteRequest(alexEpoch, linkId, "Bea", null), token);
            Assert.AreEqual("ok", invitation.Status, invitation.Message);
            await using var beaHost = await HostAsync(At("bea"), root, token);
            var beaAutomations = new FakeHandover();
            await using var beaServers = new DesktopFleetServers(beaHost, "Bea laptop", "4.0.0") { Handover = beaAutomations };
            var beaEpoch = Guid.NewGuid().ToString("D");
            var bea = new FleetServerService(beaServers, beaEpoch) { Spaces = beaHost.SpaceCatalog, IsPaired = static _ => false };
            var joined = await bea.JoinAsync(new FleetServerJoinRequest(beaEpoch, address.ToString(), invitation.Token!), token);
            Assert.AreEqual("ok", joined.Status, joined.Message);
            var words = (await bea.StatusAsync(new FleetServerRequest(beaEpoch), token)).Words;
            var pending = (await alex.DevicesAsync(new FleetServerLinkRequest(alexEpoch, linkId), token)).Devices.Single(static device => device.Status == "pending");
            Assert.AreEqual("ok", (await alex.ApproveAsync(new FleetServerDeviceRequest(alexEpoch, linkId, pending.Id, words, FleetDeviceKinds.Controller), token)).Status);
            var beaLink = joined.Server!.Id;
            await WaitForAsync(async () => (await bea.StatusAsync(new FleetServerRequest(beaEpoch), token)).Servers.Single().Link == "approved", "Bea's computer is approved");

            // Team Infra: Alex and Bea maintain it, the machine is the team's, and the access document says so.
            Assert.AreEqual("ok", (await alex.CreateTeamAsync(new FleetAccessNameRequest(alexEpoch, linkId, "Infra"), token)).Status);
            var overview = await alex.AccessAsync(new FleetServerLinkRequest(alexEpoch, linkId), token);
            var team = overview.Teams.Single();
            var beaId = overview.People.Single(static person => person.Name == "Bea").Id;
            foreach (var person in new[] { overview.Me!.Id, beaId })
                Assert.AreEqual("ok", (await alex.SetTeamMemberAsync(new FleetAccessTeamMemberRequest(alexEpoch, linkId, team.Id, person, FleetTeamRoles.Maintainer), token)).Status);
            Assert.AreEqual("ok", (await alex.OfferTransferAsync(new FleetAccessTransferRequest(alexEpoch, linkId, FleetResourceKinds.Machine, nodeKey.DeviceId,
                FleetPrincipalKinds.Team, team.Id), token)).Status);
            var offered = (await alexServers.AccessCallAsync(linkId, static (client, inner) => client.ListTransfersAsync(inner), token)).Items.Single();
            var accepted = await bea.DecideTransferAsync(new FleetAccessDecideRequest(beaEpoch, beaLink, offered.Id, "accept"), token);
            Assert.AreEqual("ok", accepted.Status, accepted.Message);
            var preview = await alex.PrepareAccessDocumentAsync(new FleetServerLinkRequest(alexEpoch, linkId), token);
            Assert.AreEqual("ok", preview.Status, preview.Message);
            Assert.AreEqual("ok", (await alex.SignAccessDocumentAsync(new FleetAccessSignRequest(alexEpoch, linkId, preview.Changes), token)).Status);
            var alexConnector = alexServers.Connections.Single().Connector!;
            var beaConnector = beaServers.Connections.Single().Connector!;
            await WaitForAsync(async () => (await beaConnector.Access.RefreshAsync(token)).TeamOf(nodeKey.DeviceId)?.Maintainers.Count == 2, "Bea's computer verifies the team");

            // Alex gives the machine an automation of the team, sealed for its maintainers.
            using var alexKey = DeviceIdentity.LoadOrCreate(alexHost.CatalogOptions.FleetRoot);
            using var beaKey = DeviceIdentity.LoadOrCreate(beaHost.CatalogOptions.FleetRoot);
            async Task<AutomationDto> GiveAsync(FleetConnector connector, DeviceIdentity key, long number, string name)
            {
                var list = await connector.Devices.RefreshAsync(token);
                var access = await connector.Access.RefreshAsync(token);
                var sealedText = FleetAutomationSigning.SealForTeam(new FleetAutomationContent(Definition(name), null), Id, number, nodeKey.DeviceId, team.Id, list, access, key);
                using var client = new FleetServerClient(address, key);
                return await client.PutAutomationAsync(FleetAutomationSigning.Sign(key, new FleetAutomationVersion(FleetAutomationVersion.CurrentVersion, list.OrganizationId!, Id,
                    number, nodeKey.DeviceId, false, true, ["daily"], null, null, sealedText, string.Empty, string.Empty, DateTimeOffset.UtcNow)
                {
                    Scope = FleetAutomationVersion.TeamScopePrefix + team.Id, Provider = "gateway",
                }), token);
            }

            Assert.AreEqual(1, (await GiveAsync(alexConnector, alexKey, 1, "Nightly")).Number);

            // Bea's computer lists it, opened with her own key, and takes it into her editor as the machine holds it.
            var listed = (await beaServers.TeamAutomationsAsync(token)).Single();
            Assert.AreEqual((beaLink, Id, nodeKey.DeviceId, "Nightly", "team:" + team.Id, "Infra"),
                (listed.LinkId, listed.Id, listed.MachineId, listed.Name, listed.Scope, listed.ScopeName));
            Assert.IsNull(await beaServers.OpenTeamAutomationAsync(beaLink, Id, nodeKey.DeviceId, token));
            var opened = beaAutomations.Edited.Single();
            StringAssert.Contains(opened.Content.Definition, "Nightly");
            Assert.AreEqual((nodeKey.DeviceId, "team:" + team.Id, beaLink, 1L), (opened.MachineId, opened.Scope, opened.LinkId, opened.Number));
            beaAutomations.Held.Add(Id);
            Assert.AreEqual(0, (await beaServers.TeamAutomationsAsync(token)).Count, "an automation this computer holds is changed in its editor");

            // What her editor saves is a newer version signed by her computer; the server takes it, and Alex's computer opens it.
            var saved = await GiveAsync(beaConnector, beaKey, 2, "Nightly, by Bea");
            Assert.AreEqual((2L, beaKey.DeviceId), (saved.Number, saved.SignerDeviceId));
            Assert.AreEqual("Nightly, by Bea", (await alexServers.TeamAutomationsAsync(token)).Single().Name);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    [TestMethod]
    public async Task OpenedInTheEditor_ItRunsOnItsMachinePublishedToItsTeam_RecordedAsGivenAtItsVersion()
    {
        var root = Directory.CreateTempSubdirectory("codealta-team-edit-config-").FullName;
        try
        {
            var project = Directory.CreateDirectory(Path.Combine(root, "checkout")).FullName;
            var projects = new List<CodeAlta.Catalog.ProjectDescriptor> { new() { Id = "p1", ProjectPath = project } };
            Task<string?> Repository(string folder, CancellationToken _) => Task.FromResult<string?>(folder == project ? "github.com/codealta/codealta" : null);
            await using var automations = new CodeAlta.Automations.AutomationService(Path.Combine(root, "config.toml"),
                _ => Task.FromResult<IReadOnlyList<CodeAlta.Catalog.ProjectDescriptor>>(projects),
                new CodeAlta.Automations.AutomationStateStore(Path.Combine(root, "state.json"), false), new FakeAutomationRunner(), TimeProvider.System, TimeZoneInfo.Utc);
            await automations.RefreshAsync();
            var fleetRoot = Directory.CreateDirectory(Path.Combine(root, "fleet")).FullName;
            await using var placement = new DesktopAutomationPlacement(automations, static _ => null, fleetRoot, static () => "desktop",
                static _ => throw new InvalidOperationException("Opening it signs nothing."), Repository);
            var handover = new DesktopAutomationHandover(automations, _ => Task.FromResult<IReadOnlyList<CodeAlta.Catalog.ProjectDescriptor>>(projects), Repository)
            {
                Placement = placement,
            };

            var outside = new FleetAutomationContent(Definition("Nightly"), "github.com/other/repository");
            StringAssert.Contains(await handover.EditAsync(outside, "box", "team:infra", "link-1", 5, CancellationToken.None), "github.com/other/repository");
            Assert.IsFalse(File.Exists(Path.Combine(fleetRoot, DesktopAutomationPlacement.FileName)), "nothing is recorded for what is not taken");

            var content = new FleetAutomationContent(Definition("Nightly"), "github.com/codealta/codealta");
            Assert.IsFalse(handover.Holds(Id));
            Assert.IsNull(await handover.EditAsync(content, "box", "team:infra", "link-1", 5, CancellationToken.None));
            await automations.RefreshAsync();
            var taken = automations.Snapshot.Find(Id)!;
            Assert.AreEqual(("Nightly", "box", "team:infra", project), (taken.Definition.Name, taken.Definition.RunsOn, taken.Definition.PublishTo, taken.ProjectPath));
            Assert.IsTrue(handover.Holds(Id));
            var record = File.ReadAllText(Path.Combine(fleetRoot, DesktopAutomationPlacement.FileName));
            StringAssert.Contains(record, Id);
            StringAssert.Contains(record, "link-1");
            Assert.IsTrue(placement.NextNumber(Id) > 5, "the next version follows the one the machine holds");
            Assert.IsNotNull(await handover.EditAsync(content, "box", "team:infra", "link-1", 5, CancellationToken.None), "taken once");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static async Task WaitForAsync(Func<Task<bool>> condition, string what)
    {
        var deadline = DateTime.UtcNow + Patience;
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline) Assert.Fail($"Timed out: {what}.");
            await Task.Delay(100);
        }
    }

    private static Task<CodeAltaHost> HostAsync(string globalRoot, string root, CancellationToken token)
        => CodeAltaHost.CreateAsync(new CodeAltaHostOptions
        {
            GlobalRoot = globalRoot, CurrentProjectPath = Directory.CreateDirectory(Path.Combine(globalRoot, "project")).FullName,
            DiscoveryScope = new(Directory.CreateDirectory(Path.Combine(globalRoot, "home")).FullName, root),
            BuiltInSkillRoot = Directory.CreateDirectory(Path.Combine(globalRoot, "builtin")).FullName,
            PluginEnvironment = FrozenDictionary<string, string?>.Empty, StartPlugins = false, IsHeadless = true, OwnsLogging = false,
        }, token);

    // The automations of a computer, as far as a team automation taken into the editor goes.
    private sealed class FakeHandover : IDesktopAutomationHandover
    {
        public ConcurrentBag<string> Held { get; } = [];

        public ConcurrentQueue<(FleetAutomationContent Content, string MachineId, string Scope, string LinkId, long Number)> Edited { get; } = new();

        public Task<FleetAutomationContent?> ReadAsync(string automationId, CancellationToken cancellationToken) => Task.FromResult<FleetAutomationContent?>(null);

        public Task<string?> TakeAsync(FleetAutomationContent content, CancellationToken cancellationToken) => Task.FromResult<string?>("Not here.");

        public bool Holds(string automationId) => Held.Contains(automationId);

        public Task<string?> EditAsync(FleetAutomationContent content, string machineId, string scope, string linkId, long number, CancellationToken cancellationToken)
        {
            Edited.Enqueue((content, machineId, scope, linkId, number));
            return Task.FromResult<string?>(null);
        }

        public Task<bool> GiveUpAsync(string automationId, CancellationToken cancellationToken) => Task.FromResult(false);
    }
}
