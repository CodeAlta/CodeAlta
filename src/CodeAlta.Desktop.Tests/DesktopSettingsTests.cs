using System.Text.Json;
using CodeAlta.Catalog;
using CodeAlta.Catalog.WorkItems;
using CodeAlta.Desktop.Rpc;
using CodeAlta.Desktop.Settings;
using CodeAlta.LiveTool;

namespace CodeAlta.Desktop.Tests;

/// <summary>Tests of the registry of the settings: the catalog, the bindings of the host, the settings the page keeps, and who may change what.</summary>
[TestClass]
public sealed class DesktopSettingsTests
{
    private static readonly AltaSettingOrigin Agent = new(AltaSettingOrigins.Agent, "session-1", null);

    [TestMethod]
    public void Catalog_IsEmbedded_WithUniqueIds_OnKnownPages()
    {
        var catalog = DesktopSettingsCatalog.Load();
        Assert.IsTrue(catalog.Settings.Count > 50);
        Assert.AreEqual(catalog.Settings.Count, catalog.Settings.Select(static setting => setting.Id).Distinct(StringComparer.Ordinal).Count());
        foreach (var setting in catalog.Settings)
        {
            Assert.IsTrue(catalog.Pages.ContainsKey(setting.Page), $"{setting.Id} is on {setting.Page}");
            Assert.IsTrue(setting.Agents is AltaSettingAccess.ReadWrite or AltaSettingAccess.Read, setting.Id);
        }

        Assert.AreEqual("Agent & models › Sessions", catalog.Find("sessions.title")!.PagePath);
    }

    [TestMethod]
    public void EverySettingTheHostKeeps_HasABinding_AndEveryBindingASetting()
    {
        using var fixture = Fixture.Create();
        var hostKept = fixture.Catalog.Settings.Where(static setting => setting.Owner == "host" && setting.Kind != AltaSettingKinds.Collection).ToArray();
        foreach (var setting in hostKept.Where(static setting => setting.Kind != AltaSettingKinds.Complex))
            Assert.IsTrue(fixture.Bindings.ContainsKey(setting.Id), $"{setting.Id} has no binding");
        foreach (var id in fixture.Bindings.Keys)
            Assert.IsTrue(hostKept.Any(setting => setting.Id == id), $"the binding {id} has no setting the host keeps");
        // A binding that writes is for a setting that is written outside its page.
        foreach (var setting in hostKept.Where(setting => fixture.Bindings.TryGetValue(setting.Id, out var binding) && binding.Write is not null))
            Assert.IsTrue(setting.Writable, $"{setting.Id} is written by its binding, yet changed on its page alone");
    }

    [TestMethod]
    public void OnAnEmptyProfile_EachBindingReadsTheDefaultOfTheCatalog()
    {
        using var fixture = Fixture.Create();
        var checkedCount = 0;
        foreach (var setting in fixture.Catalog.Settings)
        {
            if (setting.Default is not { } expected || !fixture.Bindings.TryGetValue(setting.Id, out var binding)) continue;
            if (binding.Available is { } available && !available()) continue;
            var value = binding.Read();
            Assert.IsTrue(JsonElement.DeepEquals(expected, value), $"{setting.Id}: the catalog says {expected.GetRawText()}, the store reads {value.GetRawText()}");
            checkedCount++;
        }

        Assert.IsTrue(checkedCount > 25, $"only {checkedCount} defaults were checked");
    }

    [TestMethod]
    public async Task Set_WritesTheConfiguration_KeepsWhatItDoesNotModel_AndReadsBack()
    {
        // The save of the page: a typed save keeps the keys it does not model and the comments at the end of a line.
        using var fixture = Fixture.Create("[sessions]\nprompt_suggestions = false # mine\n\n[custom]\nkeep = 1\n");
        var write = await fixture.Settings.SetAsync("sessions.title", Value("\"first_prompt\""), null, AltaSettingOrigin.Window, CancellationToken.None);
        Assert.AreEqual(AltaSettingStatus.Ok, write.Status, write.Message);
        Assert.AreEqual("model", write.Previous!.Value.GetString());
        var text = File.ReadAllText(fixture.ConfigPath);
        StringAssert.Contains(text, "# mine");
        StringAssert.Contains(text, "keep = 1");
        StringAssert.Contains(text, "title = \"first_prompt\"");
        var read = await fixture.Settings.GetAsync("sessions.title", null, CancellationToken.None);
        Assert.AreEqual(("first_prompt", "user"), (read.Value!.Value.GetString(), read.Source));

        var reset = await fixture.Settings.SetAsync("sessions.title", null, null, AltaSettingOrigin.Window, CancellationToken.None);
        Assert.AreEqual(AltaSettingStatus.Ok, reset.Status, reset.Message);
        Assert.AreEqual("default", (await fixture.Settings.GetAsync("sessions.title", null, CancellationToken.None)).Source);
    }

    [TestMethod]
    public async Task Values_AreCheckedAgainstTheirSetting()
    {
        using var fixture = Fixture.Create();
        Assert.AreEqual(AltaSettingStatus.Invalid, (await fixture.Settings.SetAsync("sessions.title", Value("\"sometimes\""), null, AltaSettingOrigin.Window, CancellationToken.None)).Status);
        Assert.AreEqual(AltaSettingStatus.Invalid, (await fixture.Settings.SetAsync("models.fallback.max_resumes", Value("500"), null, AltaSettingOrigin.Window, CancellationToken.None)).Status);
        Assert.AreEqual(AltaSettingStatus.Invalid, (await fixture.Settings.SetAsync("sessions.prompt_suggestions", Value("\"yes\""), null, AltaSettingOrigin.Window, CancellationToken.None)).Status);
        Assert.AreEqual(AltaSettingStatus.Invalid, (await fixture.Settings.SetAsync("worktrees.location", Value("\"custom\""), null, AltaSettingOrigin.Window, CancellationToken.None)).Status,
            "a folder of the user's own needs a folder");
        Assert.AreEqual(AltaSettingStatus.NotFound, (await fixture.Settings.SetAsync("nothing.here", Value("1"), null, AltaSettingOrigin.Window, CancellationToken.None)).Status);
        Assert.AreEqual(AltaSettingStatus.Managed, (await fixture.Settings.GetAsync("mcp.servers", null, CancellationToken.None)).Status);
        Assert.AreEqual(AltaSettingStatus.Complex, (await fixture.Settings.SetAsync("models.tiers", Value("{}"), null, AltaSettingOrigin.Window, CancellationToken.None)).Status);
    }

    [TestMethod]
    public async Task Agents_ChangePreferences_ButNeverWhatProtectsTheUser_AndEachChangeIsTold()
    {
        using var fixture = Fixture.Create();
        var events = new List<SettingsEvent>();
        using var watch = fixture.Settings.Watch(null, events.Add);
        var refused = await fixture.Settings.SetAsync("review.before_commit", Value("false"), null, Agent, CancellationToken.None);
        Assert.AreEqual(AltaSettingStatus.ReadOnly, refused.Status);
        Assert.AreEqual(AltaSettingStatus.ReadOnly, (await fixture.Settings.SetAsync("storage.session_retention.amount", Value("1"), null, Agent, CancellationToken.None)).Status);
        Assert.AreEqual(AltaSettingStatus.PageOnly, (await fixture.Settings.SetAsync("speech.model", Value("\"other\""), null, AltaSettingOrigin.Window, CancellationToken.None)).Status);
        Assert.AreEqual(0, events.Count);

        var changed = await fixture.Settings.SetAsync("commits.co_author", Value("false"), null, Agent, CancellationToken.None);
        Assert.AreEqual(AltaSettingStatus.Ok, changed.Status, changed.Message);
        var told = events.Single();
        Assert.AreEqual(("changed", "commits.co_author", "false", "true", "agent", "session-1"), (told.Kind, told.Id, told.Value, told.Previous, told.Origin, told.SessionId));

        // The window changes what protects the user, and is not told of its own change.
        Assert.AreEqual(AltaSettingStatus.Ok, (await fixture.Settings.SetAsync("review.before_commit", Value("true"), null, AltaSettingOrigin.Window, CancellationToken.None)).Status);
        Assert.AreEqual(1, events.Count);
    }

    [TestMethod]
    public async Task ASettingThePageKeeps_IsReadFromWhatThePageReported_AndChangedByThePage()
    {
        using var fixture = Fixture.Create();
        Assert.AreEqual(AltaSettingStatus.Unavailable, (await fixture.Settings.GetAsync("appearance.theme", null, CancellationToken.None)).Status, "until the page reports it");
        Assert.AreEqual(AltaSettingStatus.Unavailable, (await fixture.Settings.SetAsync("appearance.theme", Value("\"light\""), null, Agent, CancellationToken.None)).Status, "no page to ask");

        fixture.Settings.ReportWindow([("appearance.theme", Value("\"dark\""), null)], null, null);
        var read = await fixture.Settings.GetAsync("appearance.theme", null, CancellationToken.None);
        Assert.AreEqual(("dark", "default"), (read.Value!.Value.GetString(), read.Source));

        // The page applies the change it is asked for, and answers with the value it took.
        using var page = fixture.Settings.Watch(null, value =>
        {
            if (value.Kind == "write") _ = Task.Run(() => fixture.Settings.ReportWindow([(value.Id!, Value(value.Value!), null)], value.RequestId, null));
        });
        var write = await fixture.Settings.SetAsync("appearance.theme", Value("\"light\""), null, Agent, CancellationToken.None);
        Assert.AreEqual(AltaSettingStatus.Ok, write.Status, write.Message);
        Assert.AreEqual(("light", "dark"), (write.Value!.Value.GetString(), write.Previous!.Value.GetString()));
        Assert.AreEqual("window", (await fixture.Settings.GetAsync("appearance.theme", null, CancellationToken.None)).Source);
        Assert.AreEqual(AltaSettingStatus.Invalid, (await fixture.Settings.SetAsync("appearance.theme", Value("\"pink\""), null, Agent, CancellationToken.None)).Status);
    }

    [TestMethod]
    public void Open_TellsThePageOfTheMainWindow()
    {
        using var fixture = Fixture.Create();
        Assert.IsFalse(fixture.Settings.Open(null, "sessions.title", null), "no page watches");
        var events = new List<SettingsEvent>();
        using var watch = fixture.Settings.Watch(null, events.Add);
        Assert.IsTrue(fixture.Settings.Open(null, "sessions.title", null));
        Assert.IsTrue(fixture.Settings.Open("providers", null, null));
        CollectionAssert.AreEqual(new[] { ("show", "sessions", (string?)"sessions.title"), ("show", "providers", null) }, events.Select(static e => (e.Kind, e.Page!, e.Id)).ToArray());
    }

    private static JsonElement Value(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private sealed class Fixture : IDisposable
    {
        private Fixture(string root, DesktopSettingsCatalog catalog, System.Collections.Frozen.FrozenDictionary<string, DesktopSettingBinding> bindings, string configPath)
        {
            Root = root;
            Catalog = catalog;
            Bindings = bindings;
            ConfigPath = configPath;
            Settings = new DesktopSettings(catalog, bindings);
        }

        public string Root { get; }

        public string ConfigPath { get; }

        public DesktopSettingsCatalog Catalog { get; }

        public System.Collections.Frozen.FrozenDictionary<string, DesktopSettingBinding> Bindings { get; }

        public DesktopSettings Settings { get; }

        public static Fixture Create(string? config = null)
        {
            var root = Path.Combine(Path.GetTempPath(), "CodeAlta.DesktopSettingsTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var options = new CatalogOptions { GlobalRoot = root };
            if (config is not null) File.WriteAllText(options.ConfigPath, config);
            var store = new CodeAltaConfigStore(options);
            var bindings = DesktopSettingBindings.Create(new DesktopSettingSources
            {
                Config = store, WorkItems = new WorkItemService(new CodeAltaConfigStore(options), Path.Combine(root, "state")),
            });
            return new Fixture(root, DesktopSettingsCatalog.Load(), bindings, options.ConfigPath);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // The temporary folder is removed later.
            }
        }
    }
}
