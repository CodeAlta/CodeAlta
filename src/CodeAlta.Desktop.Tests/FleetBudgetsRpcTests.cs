using System.Collections.Frozen;
using CodeAlta.Desktop.Fleet;
using CodeAlta.Desktop.Rpc;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Fleet.Protocol.Api;
using CodeAlta.Orchestration.Hosting;
using CodeAlta.Server.Hosting;

namespace CodeAlta.Desktop.Tests;

/// <summary>
/// The budgets of the <c>fleetServer</c> service of the window (<c>doc/fleet/observability.md</c>, budgets and alerts): a budget set
/// from the page is signed by this computer and read back in the units of the page (tokens, dollars), changed, removed; what does
/// not hold is refused before anything is signed.
/// </summary>
[TestClass]
public sealed class FleetBudgetsRpcTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task ABudgetSetFromThePage_IsSignedHere_AndReadBackInDollars()
    {
        var token = TestContext.CancellationToken;
        var root = Directory.CreateTempSubdirectory("codealta-fleet-budgets-rpc-").FullName;
        string At(string name) => Directory.CreateDirectory(Path.Combine(root, name)).FullName;
        try
        {
            await using var server = await CoordinatorServer.StartAsync(new CoordinatorServerOptions
            {
                DataRoot = At("server"), Urls = ["http://127.0.0.1:0"], ConfigureLogging = static _ => { }, EnvironmentVariable = static _ => null,
            }, token);
            var address = server.Addresses.First(static uri => uri.Scheme == "http");
            await using var host = await CodeAltaHost.CreateAsync(new CodeAltaHostOptions
            {
                GlobalRoot = At("global"), CurrentProjectPath = At("project"), DiscoveryScope = new(At("home"), root), BuiltInSkillRoot = At("builtin"),
                PluginEnvironment = FrozenDictionary<string, string?>.Empty, StartPlugins = false, IsHeadless = true, OwnsLogging = false,
            }, token);
            await using var servers = new DesktopFleetServers(host, "Desktop", "4.0.0");
            var epoch = Guid.NewGuid().ToString("D");
            var service = new FleetServerService(servers, epoch) { Spaces = host.SpaceCatalog };
            var created = await service.CreateAsync(new FleetServerCreateRequest(epoch, address.ToString(), server.SetupCode!.Code, "Alex", "Home"), token);
            Assert.AreEqual("ok", created.Status, created.Message);
            var link = new FleetServerLinkRequest(epoch, created.Server!.Id);

            var empty = await service.BudgetsAsync(link, token);
            Assert.AreEqual(("ok", 0, 0, 0), (empty.Status, empty.Budgets.Length, empty.Holds.Length, empty.Notifications.Length));
            Assert.AreEqual("stale_epoch", (await service.BudgetsAsync(link with { ExpectedEpoch = "other" }, token)).Status);

            // A cost in dollars: the server holds millionths, the page reads dollars again.
            var set = await service.SetBudgetAsync(new FleetBudgetSetRequest(epoch, link.LinkId, null, " API per day ", FleetBudgetScopeKinds.Person, null, FleetBudgetMeasures.Cost,
                12.5m, FleetBudgetPeriods.Day, FleetBudgetModes.Advisory, FleetBudgetReachedActions.Stop), token);
            Assert.AreEqual("ok", set.Status, set.Message);
            var budget = (await service.BudgetsAsync(link, token)).Budgets.Single();
            Assert.AreEqual(("API per day", 12.5m, 0m, FleetBudgetStates.Ok, 1), (budget.Name, budget.Limit, budget.Used, budget.State, budget.Version));
            Assert.AreEqual((FleetBudgetPeriods.Day, FleetBudgetModes.Advisory, FleetBudgetReachedActions.Stop), (budget.Period, budget.Mode, budget.Reached));
            using (var identity = CodeAlta.Fleet.Identity.DeviceIdentity.LoadOrCreate(host.CatalogOptions.FleetRoot))
            using (var client = new CodeAlta.Fleet.Server.FleetServerClient(address, identity))
            {
                var held = (await client.ListBudgetsAsync(token)).Budgets.Single();
                Assert.IsTrue(FleetBudgetDocuments.TryRead(held.Signed.Payload, out var definition, out _));
                Assert.AreEqual((12_500_000L, client.DeviceId), (definition.Limit, definition.SignerDeviceId), "signed by this computer, in millionths of a dollar");
            }

            // Changed: its next version; removed: gone.
            Assert.AreEqual("ok", (await service.SetBudgetAsync(new FleetBudgetSetRequest(epoch, link.LinkId, budget.Id, "API per day", FleetBudgetScopeKinds.Person, null,
                FleetBudgetMeasures.Cost, 20m, FleetBudgetPeriods.Day, FleetBudgetModes.Advisory, FleetBudgetReachedActions.Stop), token)).Status);
            Assert.AreEqual((20m, 2), (await service.BudgetsAsync(link, token)).Budgets.Single() is var changed ? (changed.Limit, changed.Version) : default);
            Assert.AreEqual("ok", (await service.RemoveBudgetAsync(new FleetSecretRefRequest(epoch, link.LinkId, budget.Id), token)).Status);
            Assert.AreEqual(0, (await service.BudgetsAsync(link, token)).Budgets.Length);

            // What does not hold is refused here; what the server does not know is its refusal.
            foreach (var wrong in new[]
            {
                new FleetBudgetSetRequest(epoch, link.LinkId, null, "Zero", FleetBudgetScopeKinds.Person, null, FleetBudgetMeasures.Tokens, 0m, FleetBudgetPeriods.Month,
                    FleetBudgetModes.Enforced, FleetBudgetReachedActions.Hold),
                new FleetBudgetSetRequest(epoch, link.LinkId, null, "Team", FleetBudgetScopeKinds.Team, "t1", FleetBudgetMeasures.Tokens, 10m, FleetBudgetPeriods.Month,
                    FleetBudgetModes.Enforced, FleetBudgetReachedActions.Hold),
                new FleetBudgetSetRequest(epoch, link.LinkId, null, "", FleetBudgetScopeKinds.Person, null, FleetBudgetMeasures.Tokens, 10m, FleetBudgetPeriods.Month,
                    FleetBudgetModes.Enforced, FleetBudgetReachedActions.Hold),
                new FleetBudgetSetRequest(epoch, link.LinkId, null, "Year", FleetBudgetScopeKinds.Person, null, FleetBudgetMeasures.Tokens, 10m, "year",
                    FleetBudgetModes.Enforced, FleetBudgetReachedActions.Hold),
            })
            {
                Assert.AreEqual("invalid_request", (await service.SetBudgetAsync(wrong, token)).Status, wrong.Name);
            }

            Assert.AreEqual("budget_hold.not_found", (await service.ContinueRunAsync(new FleetSecretRefRequest(epoch, link.LinkId, "no-such-hold"), token)).Status);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
