using CodeAlta.Agent;
using CodeAlta.Catalog;
using CodeAlta.Desktop.Rpc;

namespace CodeAlta.Desktop.Tests;

[TestClass]
public sealed class GlobalConfigRpcTests
{
    private const string Epoch = "epoch-1";

    [TestMethod]
    public void WithoutAnOwnedHost_EveryOperationIsUnavailableButValidationStillWorks()
    {
        var service = new GlobalConfigService();
        Assert.AreEqual("unavailable", service.Read(new(Epoch)).Status);
        Assert.AreEqual("unavailable", service.Save(new(Epoch, "", "revision", false)).Status);
        Assert.IsTrue(service.Validate(new(CodeAltaConfigStore.GetDefaultGlobalConfigContent())).Valid);
    }

    [TestMethod]
    public async Task Read_ReturnsTheTemplateForAMissingFileAndRefusesAnotherEpoch()
    {
        await using var fixture = new Fixture();
        var read = fixture.Service.Read(new(Epoch));
        Assert.AreEqual("ok", read.Status);
        Assert.AreEqual(CodeAltaConfigStore.GetDefaultGlobalConfigContent(), read.Content);
        Assert.AreEqual(GlobalConfigService.Revision(read.Content!), read.Revision);
        Assert.IsFalse(File.Exists(fixture.ConfigPath), "Reading must not create the file.");
        Assert.AreEqual("stale_epoch", fixture.Service.Read(new("another")).Status);
        Assert.AreEqual("stale_epoch", fixture.Service.Read(new(null)).Status);
    }

    [TestMethod]
    public async Task Validate_ReportsTheFirstDiagnosticWithoutWriting()
    {
        await using var fixture = new Fixture();
        var invalid = fixture.Service.Validate(new("[chat\n"));
        Assert.IsFalse(invalid.Valid);
        Assert.IsFalse(string.IsNullOrWhiteSpace(invalid.Message));
        Assert.IsFalse(fixture.Service.Validate(new(null)).Valid);
        Assert.IsFalse(fixture.Service.Validate(new(new string('#', GlobalConfigService.MaximumContentLength + 1))).Valid);
        Assert.IsFalse(File.Exists(fixture.ConfigPath));
    }

    [TestMethod]
    public async Task Save_WritesValidContentOnlyForTheRevisionThatWasRead()
    {
        await using var fixture = new Fixture();
        var read = fixture.Service.Read(new(Epoch));
        var edited = read.Content + "\n# edited in the desktop\n";

        Assert.AreEqual("stale_epoch", fixture.Service.Save(new("another", edited, read.Revision, false)).Status);
        Assert.AreEqual("invalid", fixture.Service.Save(new(Epoch, "[chat\n", read.Revision, false)).Status);
        Assert.AreEqual("conflict", fixture.Service.Save(new(Epoch, edited, "not-the-revision", false)).Status);
        Assert.AreEqual("too_large", fixture.Service.Save(new(Epoch, new string('#', GlobalConfigService.MaximumContentLength + 1), read.Revision, false)).Status);
        Assert.IsFalse(File.Exists(fixture.ConfigPath), "Refused saves must not write.");

        var saved = fixture.Service.Save(new(Epoch, edited, read.Revision, false));
        Assert.AreEqual("ok", saved.Status);
        Assert.AreEqual(GlobalConfigService.Revision(edited), saved.Revision);
        Assert.AreEqual(0, saved.ProvidersApplied);
        Assert.AreEqual(edited, File.ReadAllText(fixture.ConfigPath));

        // The editor's revision is now stale: an external change (or a second save) is a conflict.
        Assert.AreEqual("conflict", fixture.Service.Save(new(Epoch, edited + "#", read.Revision, false)).Status);
        File.WriteAllText(fixture.ConfigPath, edited + "\n# changed elsewhere\n");
        Assert.AreEqual("conflict", fixture.Service.Save(new(Epoch, edited + "#", saved.Revision, false)).Status);
    }

    [TestMethod]
    public async Task Save_CanApplyProvidersAndUnregistersTheOnesNoLongerConfigured()
    {
        await using var fixture = new Fixture();
        fixture.Registry.RegisterOrReplace(new ModelProviderDescriptor(new ModelProviderId("removed-provider"), "Removed", "openai-chat"),
            () => throw new AssertFailedException("Applying configuration must not create a runtime."));
        var read = fixture.Service.Read(new(Epoch));

        var saved = fixture.Service.Save(new(Epoch, read.Content, read.Revision, true));

        Assert.AreEqual("ok", saved.Status);
        var registered = fixture.Registry.ListProviders(includeDisabled: true);
        Assert.AreEqual(saved.ProvidersApplied, registered.Count);
        Assert.IsFalse(registered.Any(provider => provider.ProviderId.Value == "removed-provider"));
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "CodeAlta-global-config-" + Guid.NewGuid().ToString("N"));

        public Fixture()
        {
            Directory.CreateDirectory(_root);
            var options = new CatalogOptions { GlobalRoot = _root };
            ConfigPath = options.ConfigPath;
            Service = new GlobalConfigService(new CodeAltaConfigStore(options), Registry, _root, Epoch);
        }

        public ModelProviderRegistry Registry { get; } = new();
        public GlobalConfigService Service { get; }
        public string ConfigPath { get; }

        public async ValueTask DisposeAsync()
        {
            await Registry.DisposeAsync();
            try { Directory.Delete(_root, recursive: true); }
            catch (IOException) { /* Best-effort cleanup of a temporary directory. */ }
        }
    }
}
