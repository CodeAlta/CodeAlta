using System.Collections.Frozen;
using CodeAlta.Desktop.Fleet;
using CodeAlta.Desktop.Rpc;
using CodeAlta.Orchestration.Hosting;
using CodeAlta.Server.Hosting;

namespace CodeAlta.Desktop.Tests;

/// <summary>
/// The audit log from the owner's computer (<c>doc/fleet/security-and-privacy.md</c>, audit log): pages newest first with
/// filters, the check of the chain, and an export saved where the person chose, whole or not at all.
/// </summary>
[TestClass]
public sealed class FleetAuditRpcTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task TheOwnersComputerReadsFiltersVerifiesAndExportsTheLog()
    {
        var token = TestContext.CancellationToken;
        var root = Directory.CreateTempSubdirectory("codealta-fleet-audit-rpc-").FullName;
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
            var exports = At("exports");
            string? answer = null;
            string? revealed = null;
            var service = new FleetServerService(servers, epoch)
            {
                Spaces = host.SpaceCatalog,
                ChooseSavePath = (_, name, _, _, _) => Task.FromResult(answer is null ? null : Path.Combine(exports, answer == "*" ? name : answer)),
                RevealFile = path => { revealed = path; return true; },
            };
            var created = await service.CreateAsync(new FleetServerCreateRequest(epoch, address.ToString(), server.SetupCode!.Code, "Alex", "Home"), token);
            Assert.AreEqual("ok", created.Status, created.Message);
            var linkId = created.Server!.Id;
            Assert.AreEqual("ok", (await service.InvitePersonAsync(new FleetAccessInviteRequest(epoch, linkId, "Bea", null), token)).Status);

            var page = await service.AuditAsync(new FleetAuditRequest(epoch, linkId, null, null, 2), token);
            Assert.AreEqual("ok", page.Status, page.Message);
            Assert.AreEqual(2, page.Items.Count);
            Assert.IsTrue(page.Items[0].Sequence > page.Items[1].Sequence, "the newest come first");
            Assert.AreEqual("person.invite", page.Items[0].Action);
            Assert.AreEqual(("Alex", "Desktop"), (page.Items[0].PersonName, page.Items[0].DeviceName));
            Assert.IsNotNull(page.Before);
            var older = await service.AuditAsync(new FleetAuditRequest(epoch, linkId, null, page.Before, 100), token);
            Assert.IsTrue(older.Items.All(item => item.Sequence < page.Before), "the next page is older");
            Assert.AreEqual("server.setup", older.Items[^1].Action);

            var filtered = await service.AuditAsync(new FleetAuditRequest(epoch, linkId, new FleetAuditFilters(["person."], null, null, "success", null, null, null, null, null),
                null, null), token);
            Assert.AreEqual("person.invite", filtered.Items.Single().Action);
            Assert.AreEqual("invalid_request", (await service.AuditAsync(new FleetAuditRequest(epoch, linkId,
                new FleetAuditFilters(null, null, null, null, null, null, "not a time", null, null), null, null), token)).Status);

            var check = await service.VerifyAuditAsync(new FleetServerLinkRequest(epoch, linkId), token);
            Assert.AreEqual(("ok", true), (check.Status, check.Valid));
            Assert.IsNotNull(check.HeadHash);

            // Closing the dialog exports nothing; choosing a file writes it whole.
            Assert.AreEqual("canceled", (await service.ExportAuditAsync(new FleetAuditExportRequest(epoch, linkId, null, "csv"), token)).Status);
            answer = "*";
            var export = await service.ExportAuditAsync(new FleetAuditExportRequest(epoch, linkId, null, "csv"), token);
            Assert.AreEqual("ok", export.Status, export.Message);
            StringAssert.EndsWith(export.Path, ".csv");
            var text = await File.ReadAllTextAsync(export.Path!, token);
            StringAssert.StartsWith(text, "sequence,recorded_at,action,");
            StringAssert.Contains(text, ",person.invite,success,");
            Assert.AreEqual((int)((new FileInfo(export.Path!).Length + 1023) / 1024), export.Kilobytes);
            Assert.AreEqual(1, Directory.GetFiles(exports).Length, "no part file is left beside it");
            Assert.AreEqual("ok", service.RevealAuditExport(new FleetServerRequest(epoch)).Status);
            Assert.AreEqual(export.Path, revealed);

            answer = "audit.jsonl";
            var lines = await service.ExportAuditAsync(new FleetAuditExportRequest(epoch, linkId, new FleetAuditFilters(["person."], null, null, null, null, null, null, null, null),
                "jsonl"), token);
            Assert.AreEqual("ok", lines.Status, lines.Message);
            Assert.AreEqual(1, File.ReadAllLines(lines.Path!).Length, "the filter selects one record");
            var exported = await service.AuditAsync(new FleetAuditRequest(epoch, linkId, new FleetAuditFilters(["audit.export"], null, null, null, null, null, null, null, null),
                null, null), token);
            Assert.AreEqual(2, exported.Items.Count, "each export is in the log");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
