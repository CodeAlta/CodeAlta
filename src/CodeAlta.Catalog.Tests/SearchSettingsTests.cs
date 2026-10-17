using CodeAlta.Catalog.Search;

namespace CodeAlta.Catalog.Tests;

[TestClass]
public sealed class SearchSettingsTests
{
    [TestMethod]
    public void Defaults_IndexOn_SessionsOn_MeaningOff()
    {
        var settings = SearchSettings.Read(null);
        Assert.IsTrue(settings.Enabled);
        Assert.IsTrue(settings.Sessions);
        Assert.IsFalse(settings.Semantic);
        Assert.AreEqual(SearchSettings.DefaultModel, settings.Model);
        Assert.AreEqual(SearchSettings.PromptCatalogAuto, settings.PromptCatalog);
        Assert.IsEmpty(settings.ExcludedProjects);
    }

    [TestMethod]
    public void MeaningIsOffered_UntilItIsRefusedOrTurnedOff_AndAgainAMonthAfterNotNow()
    {
        var today = new DateOnly(2026, 10, 17);
        Assert.IsTrue(SearchSettings.Default.MayOfferMeaning(today));
        Assert.IsFalse((SearchSettings.Default with { Semantic = true }).MayOfferMeaning(today), "not while it is on");
        Assert.IsFalse((SearchSettings.Default with { Enabled = false }).MayOfferMeaning(today), "not without an index");
        Assert.IsFalse((SearchSettings.Default with { MeaningOffer = SearchSettings.MeaningOfferNever }).MayOfferMeaning(today));
        Assert.IsFalse((SearchSettings.Default with { MeaningOffer = SearchSettings.MeaningOfferTurnedOff }).MayOfferMeaning(today));
        Assert.IsFalse((SearchSettings.Default with { MeaningOffer = "later:2026-10-01" }).MayOfferMeaning(today));
        Assert.IsTrue((SearchSettings.Default with { MeaningOffer = "later:2026-09-17" }).MayOfferMeaning(today));
        Assert.AreEqual("later:2026-10-01", SearchSettings.Read(new CodeAltaSearchSettingsDocument { MeaningOffer = " later:2026-10-01 " }).MeaningOffer);
    }

    [TestMethod]
    public void Read_RefusesAnUnknownModeOrModel()
    {
        Assert.Throws<InvalidDataException>(() => SearchSettings.Read(new CodeAltaSearchSettingsDocument { PromptCatalog = "some" }));
        Assert.Throws<InvalidDataException>(() => SearchSettings.Read(new CodeAltaSearchSettingsDocument { Model = "Not A Model" }));
    }

    [TestMethod]
    public void Save_WritesOnlyWhatDiffersFromTheDefaults_AndRoundTrips()
    {
        var root = Path.Combine(Path.GetTempPath(), "codealta-search-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = root });
            store.SaveGlobalSearchSettings(SearchSettings.Default);
            var config = Path.Combine(root, "config.toml");
            Assert.IsFalse(File.Exists(config) && File.ReadAllText(config).Contains("[search]"), "the defaults are not written");

            var changed = SearchSettings.Default with { Semantic = true, Model = "granite-97m-multilingual-r2", ExcludedProjects = ["p1", "P1", " p2 "], ExcludedSpaces = ["work"] };
            store.SaveGlobalSearchSettings(changed);
            var text = File.ReadAllText(config);
            StringAssert.Contains(text, "[search]");
            StringAssert.Contains(text, "semantic = true");
            Assert.DoesNotContain("enabled", text, "only what differs is written");

            var read = store.LoadGlobalSearchSettings();
            Assert.IsTrue(read.Semantic);
            Assert.AreEqual("granite-97m-multilingual-r2", read.Model);
            CollectionAssert.AreEqual(new[] { "p1", "p2" }, read.ExcludedProjects.ToArray());
            Assert.IsTrue(read.IsExcluded("P2", null));
            Assert.IsTrue(read.IsExcluded(null, "WORK"));
            Assert.IsFalse(read.IsExcluded("p3", "default"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void AProjectFile_CanKeepItsProjectOutOfSearch()
    {
        var root = Path.Combine(Path.GetTempPath(), "codealta-search-project-" + Guid.NewGuid().ToString("N"));
        var project = Path.Combine(root, "project");
        Directory.CreateDirectory(Path.Combine(project, ".alta"));
        try
        {
            var store = new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = Path.Combine(root, "global") });
            Assert.IsFalse(store.IsProjectExcludedFromSearch(project));
            File.WriteAllText(Path.Combine(project, ".alta", "config.toml"), "[search]\nenabled = false\n");
            Assert.IsTrue(store.IsProjectExcludedFromSearch(project));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void TheIndexIsInTheStateRoot_AndTheModelsInTheSharedCache()
    {
        var options = new CatalogOptions { GlobalRoot = Path.Combine(Path.GetTempPath(), "alta-root") };
        Assert.AreEqual(Path.Combine(options.StateRoot, "data", "search.sqlite3"), options.SearchDatabasePath);
        Assert.AreEqual(Path.Combine(options.GlobalRoot, "cache", "search"), options.SearchCacheRoot);
        Assert.AreEqual(Path.Combine(options.GlobalRoot, "cache", "runtime"), options.RuntimeRoot);
    }
}
