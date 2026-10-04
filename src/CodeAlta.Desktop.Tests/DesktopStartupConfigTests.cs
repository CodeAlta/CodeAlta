using CodeAlta.Catalog;
using CodeAlta.Desktop.Rpc;

namespace CodeAlta.Desktop.Tests;

[TestClass]
public sealed class DesktopStartupConfigTests
{
    private const string Broken = "[providers.codex]\nenabled = true\nreasoning_effort = high\n";
    private const string Repaired = "[providers.codex]\nenabled = true\nreasoning_effort = \"high\"\n";

    [TestMethod]
    public void BrokenConfiguration_IsRepairedInPlaceBeforeTheApplicationStarts()
    {
        using var profile = new Profile(Broken);
        var recovery = profile.Recovery();
        Assert.IsFalse(recovery.IsReady);
        int continued = 0, left = 0;
        var service = new StartupConfigService(recovery) { Continue = () => continued++, Exit = () => left++ };

        var document = service.Read(new());
        Assert.AreEqual("ok", document.Status);
        Assert.AreEqual(profile.ConfigPath, document.Path);
        Assert.AreEqual(Broken, document.Content);
        Assert.IsFalse(document.Validation!.Valid);
        Assert.AreEqual(3, document.Validation.Line);
        Assert.IsNotNull(document.Validation.Message);

        // Text is checked without being written, and text that is still wrong is not saved.
        Assert.IsTrue(service.Validate(new(Repaired)).Valid);
        Assert.IsFalse(service.Validate(new(null)).Valid);
        var refused = service.Save(new(Broken));
        Assert.AreEqual("invalid", refused.Status);
        Assert.AreEqual(3, refused.Validation!.Line);
        Assert.AreEqual("too_large", service.Save(new(new string('#', StartupConfigService.MaximumContentLength + 1))).Status);
        Assert.AreEqual(Broken, File.ReadAllText(profile.ConfigPath));
        Assert.AreEqual(0, continued);

        Assert.AreEqual("ok", service.Save(new(Repaired)).Status);
        Assert.AreEqual(Repaired, File.ReadAllText(profile.ConfigPath));
        Assert.IsTrue(recovery.IsReady);
        // The application starts once, whatever the window still sends.
        Assert.AreEqual("ok", service.Save(new(Repaired)).Status);
        Assert.AreEqual("ok", service.Leave(new()).Status);
        Assert.AreEqual(1, continued);
        Assert.AreEqual(0, left);
    }

    [TestMethod]
    public void ConfigurationChangedOnDisk_IsNotOverwritten_AndReloadShowsIt()
    {
        using var profile = new Profile(Broken);
        var continued = 0;
        var service = new StartupConfigService(profile.Recovery()) { Continue = () => continued++ };

        // Another editor repaired the file while this one was open.
        const string elsewhere = "[providers.codex]\nenabled = false\n";
        File.WriteAllText(profile.ConfigPath, elsewhere);
        var save = service.Save(new(Repaired));
        Assert.AreEqual("failed", save.Status);
        Assert.IsNotNull(save.Failure);
        Assert.AreEqual(elsewhere, File.ReadAllText(profile.ConfigPath));
        Assert.AreEqual(0, continued);

        var reloaded = service.Reload(new());
        Assert.AreEqual(elsewhere, reloaded.Content);
        Assert.IsTrue(reloaded.Validation!.Valid);
        Assert.IsNull(reloaded.Failure);
        Assert.AreEqual("ok", service.Save(new(elsewhere)).Status);
        Assert.AreEqual(1, continued);
    }

    [TestMethod]
    public void LeavingWithoutRepair_ClosesOnceAndWritesNothing()
    {
        using var profile = new Profile(Broken);
        int continued = 0, left = 0;
        var service = new StartupConfigService(profile.Recovery()) { Continue = () => continued++, Exit = () => left++ };

        Assert.AreEqual("ok", service.Leave(new()).Status);
        Assert.AreEqual("ok", service.Leave(new()).Status);
        Assert.AreEqual("ok", service.Save(new(Repaired)).Status); // Too late: the window is closing.
        Assert.AreEqual(1, left);
        Assert.AreEqual(0, continued);
        Assert.AreEqual(Broken, File.ReadAllText(profile.ConfigPath));
    }

    [TestMethod]
    public void FirstStart_CreatesTheConfiguration_AndAsksForAProvider()
    {
        using var profile = new Profile(null);
        var recovery = profile.Recovery();
        Assert.IsTrue(recovery.IsReady);
        Assert.IsTrue(File.Exists(profile.ConfigPath));
        // The default configuration lists providers and enables none.
        var store = new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = profile.Root });
        Assert.IsTrue(DesktopApplication.NeedsProviderSetup(store));

        File.WriteAllText(profile.ConfigPath, "[providers.codex]\nenabled = false\ntype = \"codex\"\n\n[providers.anthropic]\nenabled = true\ntype = \"anthropic\"\n");
        Assert.IsFalse(DesktopApplication.NeedsProviderSetup(store));
        File.WriteAllText(profile.ConfigPath, "[providers.codex]\nenabled = false\ntype = \"codex\"\n");
        Assert.IsTrue(DesktopApplication.NeedsProviderSetup(store));
        // A file that cannot be loaded is the recovery screen's business, not a provider question.
        File.WriteAllText(profile.ConfigPath, Broken);
        Assert.IsFalse(DesktopApplication.NeedsProviderSetup(store));
    }

    [TestMethod]
    public void BootStatus_TellsThePageWhatTheWindowIsFor()
    {
        var recovery = new BootService { ConfigRecovery = true, Developer = true }.Status(new());
        Assert.IsTrue(recovery.ConfigRecovery);
        Assert.IsFalse(recovery.HostAvailable);
        Assert.IsNull(recovery.HostEpoch);
        Assert.IsTrue(recovery.DeveloperMode);
        Assert.IsFalse(recovery.ProviderSetup);

        var first = new BootService("epoch") { ProviderSetup = true }.Status(new());
        Assert.IsTrue(first.ProviderSetup);
        Assert.IsFalse(first.ConfigRecovery);
        Assert.IsTrue(first.HostAvailable);
        Assert.IsFalse(new BootService("epoch").Status(new()).ProviderSetup);
        Assert.IsFalse(new BootService().Status(new()).ConfigRecovery);
    }

    private sealed class Profile : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "codealta-startup-config-" + Guid.NewGuid().ToString("N"));

        public Profile(string? config)
        {
            Directory.CreateDirectory(_root);
            if (config is not null) File.WriteAllText(ConfigPath, config);
        }

        public string Root => _root;

        public string ConfigPath => Path.Combine(_root, "config.toml");

        public ConfigRecoveryService Recovery()
        {
            var recovery = new ConfigRecoveryService(_root, new TextFileCodec());
            recovery.Reload();
            return recovery;
        }

        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
