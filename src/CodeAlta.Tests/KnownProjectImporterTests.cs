using CodeAlta.Agent;
using CodeAlta.App;
using CodeAlta.Catalog;
using CodeAlta.Orchestration;
using CodeAlta.Orchestration.Runtime;
using CodeAlta.Persistence;

namespace CodeAlta.Tests;

[TestClass]
public sealed class KnownProjectImporterTests
{
    [TestMethod]
    public async Task ImportAsync_MarksSkippedFailedProvidersAsComplete()
    {
        using var temp = TempDirectory.Create();
        var backendId = new AgentBackendId("failed");
        var createBackendCount = 0;
        var backendFactory = new AgentBackendFactory();
        backendFactory.Register(
            backendId,
            () =>
            {
                createBackendCount++;
                throw new InvalidOperationException("The failed provider should be skipped.");
            });

        var db = new CodeAltaDb(new CodeAltaDbOptions { DatabasePath = Path.Combine(temp.Path, "state.db") });
        await db.InitializeAsync().ConfigureAwait(false);
        await using var hub = new AgentHub(backendFactory, new AgentRepository(db));
        var importer = new KnownProjectImporter(
            hub,
            [new AgentBackendDescriptor(backendId, "Failed Provider")],
            new ProjectCatalog(new CatalogOptions { GlobalRoot = temp.Path }))
        {
            ShouldLoadProviderSessions = _ => false,
        };
        var progress = new List<ProviderSessionLoadProgress>();

        await importer.ImportAsync(progress.Add, CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(0, createBackendCount);
        Assert.AreEqual(2, progress.Count);
        Assert.AreEqual(0, progress[0].CompletedProviderCount);
        Assert.AreEqual(1, progress[1].CompletedProviderCount);
        Assert.AreEqual(0, progress[1].LoadingProviderDisplayNames.Count);
    }

    private sealed class TempDirectory(string path) : IDisposable
    {
        public string Path { get; } = path;

        public static TempDirectory Create()
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"codealta-importer-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(path);
            return new TempDirectory(path);
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
