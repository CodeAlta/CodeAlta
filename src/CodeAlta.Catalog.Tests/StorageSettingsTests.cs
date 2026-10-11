using CodeAlta.Catalog.Storage;

namespace CodeAlta.Catalog.Tests;

[TestClass]
public sealed class StorageSettingsTests
{
    [TestMethod]
    public void Defaults_AreOnAndThreeCalendarMonths()
    {
        var settings = StorageSettings.Read(null);
        Assert.IsTrue(settings.ClearScratchpadOnStartup);
        Assert.AreEqual(new SessionRetentionSettings(true, 3, "months"), settings.SessionRetention);
        Assert.AreEqual(settings.SessionRetention, StorageSettings.Read(new CodeAltaStorageSettingsDocument { SessionRetention = new() }).SessionRetention);
        Assert.IsTrue(settings.SessionRetention.TryGetCutoff(new DateTimeOffset(2024, 3, 31, 12, 0, 0, TimeSpan.Zero), out var cutoff, out var error));
        Assert.IsNull(error);
        Assert.AreEqual(new DateTimeOffset(2023, 12, 31, 12, 0, 0, TimeSpan.Zero), cutoff);
    }

    [TestMethod]
    [DataRow(2, "days", "2024-03-29T10:00:00Z")]
    [DataRow(2, "weeks", "2024-03-17T10:00:00Z")]
    [DataRow(2, "months", "2024-01-31T10:00:00Z")]
    [DataRow(2, "years", "2022-03-31T10:00:00Z")]
    public void Cutoff_UsesUtcAndSelectedUnit(int amount, string unit, string expected)
    {
        Assert.IsTrue(new SessionRetentionSettings(true, amount, unit).TryGetCutoff(
            new DateTimeOffset(2024, 3, 31, 12, 0, 0, TimeSpan.FromHours(2)), out var cutoff, out var error));
        Assert.AreEqual(DateTimeOffset.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), cutoff);
        Assert.AreEqual(TimeSpan.Zero, cutoff.Offset);
        Assert.IsNull(error);
    }

    [TestMethod]
    [DataRow(0, "days")]
    [DataRow(-1, "days")]
    [DataRow(1, "fortnights")]
    [DataRow(1, "")]
    [DataRow(int.MaxValue, "weeks")]
    [DataRow(int.MaxValue, "months")]
    public void InvalidDuration_NeverProducesACutoff(int amount, string unit)
    {
        Assert.IsFalse(new SessionRetentionSettings(true, amount, unit).TryGetCutoff(DateTimeOffset.UtcNow, out _, out var error));
        Assert.IsNotNull(error);
    }

    [TestMethod]
    public void Never_HasNoCutoff()
    {
        Assert.IsFalse(new SessionRetentionSettings(false, 45, "days").TryGetCutoff(DateTimeOffset.UtcNow, out _, out var error));
        Assert.IsNull(error);
    }

    [TestMethod]
    public void GlobalStorageRead_DistinguishesMissingFromUnreadable()
    {
        WithStore((store, _) =>
        {
            Assert.AreEqual(StorageSettings.Default, store.LoadGlobalStorageSettings());
            Directory.CreateDirectory(store.ConfigPath);
            Assert.Throws<UnauthorizedAccessException>(() => store.LoadGlobalStorageSettings());
        });
    }

    [TestMethod]
    public void Config_RoundTripsNeverAndCustomDuration_PreservesUnknownKeys_OmitsDefaults()
    {
        WithStore((store, root) =>
        {
            File.WriteAllText(store.ConfigPath, "[storage]\nfuture_option = 42\n[storage.session_retention]\nfuture_unit_option = true\n[custom]\nkeep = 'yes'\n");
            var custom = new StorageSettings(false, new(false, 45, "days"));
            store.SaveGlobalStorageSettings(custom);
            Assert.AreEqual(custom, StorageSettings.Read(store.LoadGlobal().Storage));
            StringAssert.Contains(File.ReadAllText(store.ConfigPath), "clear_scratchpad_on_startup = false");
            store.SaveGlobalStorageSettings(StorageSettings.Default);
            var content = File.ReadAllText(store.ConfigPath);
            StringAssert.Contains(content, "future_option = 42");
            StringAssert.Contains(content, "future_unit_option = true");
            StringAssert.Contains(content, "keep =");
            Assert.DoesNotContain("clear_scratchpad_on_startup", content);
            Assert.DoesNotContain("amount =", content);
            Assert.DoesNotContain("enabled =", content);
            Assert.AreEqual(StorageSettings.Default, StorageSettings.Read(store.LoadGlobal().Storage));

            File.WriteAllText(store.ConfigPath, string.Empty);
            store.SaveGlobalStorageSettings(custom);
            store.SaveGlobalStorageSettings(StorageSettings.Default);
            Assert.DoesNotContain("[storage", File.ReadAllText(store.ConfigPath));

            // One month is a choice, not the default: it is written, so it is not read back as three months.
            var oneMonth = new StorageSettings(true, new(true, 1, "months"));
            store.SaveGlobalStorageSettings(oneMonth);
            StringAssert.Contains(File.ReadAllText(store.ConfigPath), "amount = 1");
            Assert.AreEqual(oneMonth, StorageSettings.Read(store.LoadGlobal().Storage));
            store.SaveGlobalStorageSettings(StorageSettings.Default);

            var project = Path.Combine(root, "project");
            Directory.CreateDirectory(Path.Combine(project, ".alta"));
            File.WriteAllText(Path.Combine(project, ".alta", "config.toml"), "[storage]\nclear_scratchpad_on_startup = false\n");
            Assert.AreEqual(StorageSettings.Default, StorageSettings.Read(store.LoadGlobal().Storage), "project policy cannot change global cleanup");
        });
    }

    [TestMethod]
    [DataRow("amount = 0")]
    [DataRow("amount = 1.5")]
    [DataRow("amount = 999999999999")]
    [DataRow("unit = 'unknown'")]
    [DataRow("enabled = 'yes'")]
    public void InvalidPersistedPolicy_IsReportedInsteadOfDefaulted(string setting)
    {
        var content = "[storage.session_retention]\n" + setting + "\n";
        Assert.IsFalse(CodeAltaConfigStore.ValidateGlobalConfigContent(content).IsValid);
        WithStore((store, _) =>
        {
            File.WriteAllText(store.ConfigPath, content);
            Assert.Throws<InvalidDataException>(() => store.LoadGlobalStorageSettings());
        });
    }

    private static void WithStore(Action<CodeAltaConfigStore, string> action)
    {
        var root = Path.Combine(Path.GetTempPath(), "codealta-storage-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { action(new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = root }), root); }
        finally { Directory.Delete(root, recursive: true); }
    }
}
