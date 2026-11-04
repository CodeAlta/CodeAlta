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
/// An automation given to another person (<c>doc/fleet/access-control.md</c>, ownership over time), between two computers and a
/// server: the owner's computer seals what it does for the computer of the person it gives it to; she accepts it, and Resume
/// takes it on her computer, which opens it with its own key; the owner's computer then takes it off, and so off its machines.
/// </summary>
[TestClass]
public sealed class FleetTransferHandoverTests
{
    private const string Id = "01a1a662-bcde-7c1b-8b0f-30c06af2790e";
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task AnAutomationGivenToAPersonIsTakenOnHerComputerAtResume_AndLeavesTheComputerOfItsFormerOwner()
    {
        var token = TestContext.CancellationToken;
        var root = Directory.CreateTempSubdirectory("codealta-fleet-handover-").FullName;
        string At(string name) => Directory.CreateDirectory(Path.Combine(root, name)).FullName;
        try
        {
            await using var server = await CoordinatorServer.StartAsync(new CoordinatorServerOptions
            {
                DataRoot = At("server"), Urls = ["http://127.0.0.1:0"], ConfigureLogging = static _ => { }, EnvironmentVariable = static _ => null,
            }, token);
            var address = server.Addresses.First(static uri => uri.Scheme == "http");

            // Alex's computer sets the server up; his machine joins, and holds his automation "nightly".
            await using var alexHost = await HostAsync(At("alex"), root, token);
            var alexAutomations = new FakeHandover(new FleetAutomationContent("[automations.nightly]\nname = \"Nightly\"\n", null));
            await using var alexServers = new DesktopFleetServers(alexHost, "Alex desktop", "4.0.0") { Handover = alexAutomations };
            var alexEpoch = Guid.NewGuid().ToString("D");
            var alex = new FleetServerService(alexServers, alexEpoch) { Spaces = alexHost.SpaceCatalog, IsPaired = static _ => false };
            var created = await alex.CreateAsync(new FleetServerCreateRequest(alexEpoch, address.ToString(), server.SetupCode!.Code, "Alex", "Home"), token);
            Assert.AreEqual("ok", created.Status, created.Message);
            var linkId = created.Server!.Id;
            using var nodeKey = DeviceIdentity.LoadOrCreate(At("node"));
            var enrollment = await alex.CreateTokenAsync(new FleetServerTokenRequest(alexEpoch, linkId, 2, "Mac mini"), token);
            using (var node = new FleetServerClient(address, nodeKey)) await node.EnrollAsync(enrollment.Token!, "Mac mini", FleetDeviceKinds.Node, token);
            Assert.AreEqual("ok", (await alex.ApproveAsync(new FleetServerDeviceRequest(alexEpoch, linkId, nodeKey.DeviceId, null, FleetDeviceKinds.Node), token)).Status);
            using (var alexKey = DeviceIdentity.LoadOrCreate(alexHost.CatalogOptions.FleetRoot))
            using (var alexClient = new FleetServerClient(address, alexKey))
            {
                var organization = (await alexClient.GetOrganizationAsync(token)).Id;
                await alexClient.PutAutomationAsync(FleetAutomationSigning.Sign(alexKey, new FleetAutomationVersion(FleetAutomationVersion.CurrentVersion, organization, "nightly", 1,
                    nodeKey.DeviceId, false, true, ["hourly"], null, null, "{\"sealed\":\"for the machine\"}", string.Empty, string.Empty, DateTimeOffset.UtcNow)), token);
            }

            // Bea is invited; her computer joins with the code, and Alex's approves it.
            var invitation = await alex.InvitePersonAsync(new FleetAccessInviteRequest(alexEpoch, linkId, "Bea", null), token);
            Assert.AreEqual("ok", invitation.Status, invitation.Message);
            await using var beaHost = await HostAsync(At("bea"), root, token);
            var beaAutomations = new FakeHandover(null);
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
            var beaId = (await alex.AccessAsync(new FleetServerLinkRequest(alexEpoch, linkId), token)).People.Single(static person => person.Name == "Bea").Id;

            // Alex gives "nightly" to Bea: what it does goes sealed for her computer only.
            Assert.AreEqual("ok", (await alex.OfferTransferAsync(new FleetAccessTransferRequest(alexEpoch, linkId, FleetResourceKinds.Automation, "nightly",
                FleetPrincipalKinds.Person, beaId), token)).Status);
            var offered = (await alexServers.AccessCallAsync(linkId, static (client, inner) => client.ListTransfersAsync(inner), token)).Items.Single();
            Assert.IsTrue(offered.HasHandover, "the owner's computer sealed what it does");
            Assert.AreEqual("ok", (await bea.DecideTransferAsync(new FleetAccessDecideRequest(beaEpoch, beaLink, offered.Id, "accept"), token)).Status);

            // A first Resume that cannot take it here leaves it paused; the next one takes it on her computer and resumes it.
            beaAutomations.Refusal = "Add the project first.";
            var refused = await bea.ResumeResourceAsync(new FleetAccessResourceRequest(beaEpoch, beaLink, FleetResourceKinds.Automation, "nightly"), token);
            Assert.AreEqual(("refused", "Add the project first."), (refused.Status, refused.Message));
            Assert.IsTrue((await bea.AccessAsync(new FleetServerLinkRequest(beaEpoch, beaLink), token)).Owners.Single(static owner => owner.Id == "nightly").Paused);
            beaAutomations.Refusal = null;
            var resumed = await bea.ResumeResourceAsync(new FleetAccessResourceRequest(beaEpoch, beaLink, FleetResourceKinds.Automation, "nightly"), token);
            Assert.AreEqual("ok", resumed.Status, resumed.Message);
            Assert.AreEqual(alexAutomations.Content, beaAutomations.Taken.Last(), "her computer opened what his sealed");
            Assert.IsFalse((await bea.AccessAsync(new FleetServerLinkRequest(beaEpoch, beaLink), token)).Owners.Single(static owner => owner.Id == "nightly").Paused);

            // Alex's computer, told that access changed, takes it off.
            await WaitForAsync(() => Task.FromResult(alexAutomations.GivenUp.Contains("nightly")), "the former owner's computer gives it up");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    [TestMethod]
    public async Task WhatIsHandedOverIsTheAutomationWithoutItsMachineOrFolder_TakenInTheProjectOfItsRepository()
    {
        var root = Directory.CreateTempSubdirectory("codealta-handover-config-").FullName;
        try
        {
            var project = Directory.CreateDirectory(Path.Combine(root, "checkout")).FullName;
            var alexConfig = Path.Combine(root, "alex.toml");
            File.WriteAllText(alexConfig, $$"""
                [automations.01a1a662-bcde-7c1b-8b0f-30c06af2790e]
                name = "Nightly"
                runs_on = "mini"
                project = '{{project}}'
                prompt = "Summarize the day."
                triggers = [{ type = "daily", at = ["23:00"] }]
                """);
            var projects = new List<CodeAlta.Catalog.ProjectDescriptor> { new() { Id = "p1", ProjectPath = project } };
            Task<string?> Repository(string folder, CancellationToken _) => Task.FromResult<string?>(folder == project ? "github.com/codealta/codealta" : null);
            await using var alexAutomations = new CodeAlta.Automations.AutomationService(alexConfig, _ => Task.FromResult<IReadOnlyList<CodeAlta.Catalog.ProjectDescriptor>>(projects),
                new CodeAlta.Automations.AutomationStateStore(Path.Combine(root, "alex-state.json"), false), new FakeAutomationRunner(), TimeProvider.System, TimeZoneInfo.Utc);
            await alexAutomations.RefreshAsync();
            var alex = new DesktopAutomationHandover(alexAutomations, _ => Task.FromResult<IReadOnlyList<CodeAlta.Catalog.ProjectDescriptor>>(projects), Repository);
            var held = alexAutomations.Snapshot.Find(Id);
            Assert.IsNotNull(held, string.Join("; ", alexAutomations.Snapshot.Faults.Select(static fault => fault.Message)));
            Assert.IsNull(held.Problem, held.Problem);
            Assert.IsNull(await alex.ReadAsync("other", CancellationToken.None));
            var content = (await alex.ReadAsync(Id, CancellationToken.None))!;
            Assert.AreEqual("github.com/codealta/codealta", content.Repository);
            Assert.IsFalse(content.Definition.Contains("mini", StringComparison.Ordinal), "where it runs is this computer's");
            Assert.IsFalse(content.Definition.Contains(project, StringComparison.Ordinal), "so is the folder of its project");

            var beaConfig = Path.Combine(root, "bea.toml");
            await using var beaAutomations = new CodeAlta.Automations.AutomationService(beaConfig, _ => Task.FromResult<IReadOnlyList<CodeAlta.Catalog.ProjectDescriptor>>(projects),
                new CodeAlta.Automations.AutomationStateStore(Path.Combine(root, "bea-state.json"), false), new FakeAutomationRunner(), TimeProvider.System, TimeZoneInfo.Utc);
            await beaAutomations.RefreshAsync();
            var none = new DesktopAutomationHandover(beaAutomations, _ => Task.FromResult<IReadOnlyList<CodeAlta.Catalog.ProjectDescriptor>>([]), Repository);
            StringAssert.Contains(await none.TakeAsync(content, CancellationToken.None), "github.com/codealta/codealta", "it runs in its project, which she adds first");
            var bea = new DesktopAutomationHandover(beaAutomations, _ => Task.FromResult<IReadOnlyList<CodeAlta.Catalog.ProjectDescriptor>>(projects), Repository);
            Assert.IsNull(await bea.TakeAsync(content, CancellationToken.None));
            await beaAutomations.RefreshAsync();
            var taken = beaAutomations.Snapshot.Find(Id)!;
            Assert.AreEqual(("Nightly", null, project), (taken.Definition.Name, taken.Definition.RunsOn, taken.ProjectPath));
            Assert.IsNotNull(await bea.TakeAsync(content, CancellationToken.None), "taken once");

            Assert.IsTrue(await alex.GiveUpAsync(Id, CancellationToken.None));
            await alexAutomations.RefreshAsync();
            Assert.IsNull(alexAutomations.Snapshot.Find(Id));
            Assert.IsFalse(await alex.GiveUpAsync(Id, CancellationToken.None));
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

    // The automations of a computer, as a transfer moves them.
    private sealed class FakeHandover(FleetAutomationContent? content) : IDesktopAutomationHandover
    {
        public FleetAutomationContent? Content { get; } = content;

        public string? Refusal { get; set; }

        public ConcurrentQueue<FleetAutomationContent> Taken { get; } = new();

        public ConcurrentBag<string> GivenUp { get; } = [];

        public Task<FleetAutomationContent?> ReadAsync(string automationId, CancellationToken cancellationToken) => Task.FromResult(automationId == "nightly" ? Content : null);

        public Task<string?> TakeAsync(FleetAutomationContent content, CancellationToken cancellationToken)
        {
            if (Refusal is null) Taken.Enqueue(content);
            return Task.FromResult(Refusal);
        }

        public Task<bool> GiveUpAsync(string automationId, CancellationToken cancellationToken)
        {
            var had = Content is not null && automationId == "nightly" && !GivenUp.Contains(automationId);
            if (had) GivenUp.Add(automationId);
            return Task.FromResult(had);
        }
    }
}
