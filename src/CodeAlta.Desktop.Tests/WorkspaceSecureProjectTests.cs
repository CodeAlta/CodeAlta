using CodeAlta.Catalog;
using CodeAlta.Desktop.Rpc;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Desktop.Tests;

[TestClass]
public sealed class WorkspaceSecureProjectTests
{
    private const string Epoch = "11111111-1111-4111-8111-111111111111";

    [TestMethod]
    public async Task AProjectMadeSecure_HasAPadlock_AndANormalOneHasNone()
    {
        var root = Path.Combine(Path.GetTempPath(), "codealta-secure-rpc-" + Guid.NewGuid().ToString("N"));
        var secured = Directory.CreateDirectory(Path.Combine(root, "secured")).FullName;
        var normal = Directory.CreateDirectory(Path.Combine(root, "normal")).FullName;
        try
        {
            var catalog = new ProjectCatalog(new CatalogOptions { GlobalRoot = Path.Combine(root, "global") });
            var project = await catalog.UpsertFromPathAsync(secured);
            var other = await catalog.UpsertFromPathAsync(normal);
            await using var reads = new OwnedSessionWorkspace(catalog, new SessionViewJournalStore(catalog.Options));
            var rpc = new WorkspaceService(reads, catalog, Epoch);
            var config = new CodeAltaConfigStore(catalog.Options);

            Assert.AreEqual("stale_epoch", (await rpc.SetProjectSecureAsync(new(Guid.NewGuid().ToString("D"), project.Id, true), default)).Status);
            Assert.AreEqual("not_found", (await rpc.SetProjectSecureAsync(new(Epoch, "unknown", true), default)).Status);
            Assert.IsFalse(config.IsProjectPseudonymized(secured), "Nothing was written for a refused request.");

            var made = await rpc.SetProjectSecureAsync(new(Epoch, project.Id, true), default);
            Assert.AreEqual(("ok", true), (made.Status, made.Secure));
            Assert.IsTrue(config.IsProjectPseudonymized(secured), "The choice is in the configuration of the project.");
            var snapshot = WorkspaceService.WithPrivacy(new WorkspaceSnapshot(true,
                [new WorkspaceProject(project.Id, project.DisplayName, secured, false), new WorkspaceProject(other.Id, other.DisplayName, normal, false)], [], false, false, false), config);
            Assert.IsTrue(snapshot.Projects[0].Secure);
            Assert.IsNull(snapshot.Projects[1].Secure, "A normal project works as before.");

            var undone = await rpc.SetProjectSecureAsync(new(Epoch, project.Id, false), default);
            Assert.AreEqual(("ok", false), (undone.Status, undone.Secure));
            Assert.IsFalse(config.IsProjectPseudonymized(secured));
        }
        finally { Directory.Delete(root, true); }
    }
}
