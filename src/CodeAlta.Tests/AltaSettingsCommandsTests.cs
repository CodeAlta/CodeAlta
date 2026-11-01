using System.Text.Json;
using CodeAlta.Catalog;
using CodeAlta.LiveTool;

namespace CodeAlta.Tests;

/// <summary>Tests of <c>alta settings</c>: the settings found, read, shown to the user, and changed only where agents may change them.</summary>
[TestClass]
public sealed class AltaSettingsCommandsTests
{
    private static readonly AltaCallerIdentity Agent = new() { Kind = "agent", SourceSessionId = "session-1" };

    [TestMethod]
    public async Task Group_ExistsOnlyWithTheService_AndOnlySetAndResetChangeSomething()
    {
        using var none = Fixture.Create(service: false);
        Assert.AreEqual(AltaExitCodes.Usage, (await none.RunAsync("settings", "list")).ExitCode);
        Assert.IsFalse((await none.RunAsync("tool", "list")).Stdout.Contains("settings list", StringComparison.Ordinal));

        using var fixture = Fixture.Create();
        var policies = Records(await fixture.RunAsync("tool", "list", "--detailed"))
            .Where(static line => line.TryGetProperty("path", out var path) && path.GetString()!.StartsWith("settings ", StringComparison.Ordinal))
            .ToDictionary(static line => line.GetProperty("path").GetString()!, static line => line.GetProperty("isMutating").GetBoolean());
        CollectionAssert.AreEquivalent(new[] { "settings list", "settings get", "settings open", "settings set", "settings reset" }, policies.Keys.ToArray());
        Assert.IsTrue(policies["settings set"] && policies["settings reset"]);
        Assert.IsFalse(policies["settings list"] || policies["settings get"] || policies["settings open"]);
        var help = (await fixture.RunAsync("settings", "set", "--help")).Stdout;
        StringAssert.Contains(help, "Only when the user asks for it.");
    }

    [TestMethod]
    public async Task SandboxedSessions_ReadAndOpen_ButNeverChange()
    {
        foreach (var leaf in new[] { "list", "get", "open" }) Assert.IsTrue(AltaSandboxGate.Allows($"alta settings {leaf}"), leaf);
        foreach (var leaf in new[] { "set", "reset" }) Assert.IsFalse(AltaSandboxGate.Allows($"alta settings {leaf}"), leaf);
        Assert.IsFalse(AltaCommandRegistryOptions.Cli.AllowsBuiltInRoot("settings"), "the command line never writes settings");
    }

    [TestMethod]
    public async Task List_FindsByWords_LabelFirst_AndByPage()
    {
        using var fixture = Fixture.Create();
        var all = Records(await fixture.OkAsync("settings", "list")).Where(static line => Type(line) == "alta.settings.ref").ToArray();
        Assert.AreEqual(fixture.Settings.Descriptors.Count, all.Length);

        var titled = Records(await fixture.OkAsync("settings", "list", "title")).Where(static line => Type(line) == "alta.settings.ref").Select(static line => line.GetProperty("id").GetString()).ToArray();
        Assert.AreEqual("sessions.title", titled[0], "a word of the label comes first");
        var page = Records(await fixture.OkAsync("settings", "list", "--page", "appearance")).Where(static line => Type(line) == "alta.settings.ref").ToArray();
        Assert.IsTrue(page.All(static line => line.GetProperty("page").GetString() == "Personalization › Appearance"));
        var list = Records(await fixture.OkAsync("settings", "list", "mcp")).Single(static line => line.TryGetProperty("id", out var id) && id.GetString() == "mcp.servers");
        Assert.AreEqual("alta mcp server", list.GetProperty("managedBy").GetString());
    }

    [TestMethod]
    public async Task ListChangedAndDetailed_ReadTheValues()
    {
        using var fixture = Fixture.Create();
        fixture.Settings.Values["sessions.prompt_suggestions"] = Json("false");
        var changed = Records(await fixture.OkAsync("settings", "list", "--changed")).Where(static line => Type(line) == "alta.settings.ref").Select(static line => line.GetProperty("id").GetString()).ToArray();
        CollectionAssert.AreEqual(new[] { "sessions.prompt_suggestions" }, changed);
        var detailed = Records(await fixture.OkAsync("settings", "list", "theme", "--detailed")).Single(static line => Type(line) == "alta.settings.setting");
        Assert.AreEqual("dark", detailed.GetProperty("value").GetString());
        Assert.AreEqual("default", detailed.GetProperty("source").GetString());
        CollectionAssert.AreEqual(new[] { "dark", "light", "system" }, detailed.GetProperty("choices").EnumerateArray().Select(static choice => choice.GetString()).ToArray());
    }

    [TestMethod]
    public async Task Get_ReadsValues_AndSaysWhereAListOfItemsIsChanged()
    {
        using var fixture = Fixture.Create();
        var values = Records(await fixture.OkAsync("settings", "get", "sessions.title", "appearance.theme")).Where(static line => Type(line) == "alta.settings.value").ToArray();
        CollectionAssert.AreEqual(new[] { "model", "dark" }, values.Select(static value => value.GetProperty("value").GetString()).ToArray());
        await fixture.FailsAsync(AltaExitCodes.NotFound, "settings.notFound", "settings", "get", "nothing.here");
        var managed = await fixture.FailsAsync(AltaExitCodes.Unsupported, "settings.managedByCommand", "settings", "get", "mcp.servers");
        StringAssert.Contains(managed.GetProperty("message").GetString(), "alta mcp server");
        await fixture.FailsAsync(AltaExitCodes.Usage, "usage.missingSetting", "settings", "get");
    }

    [TestMethod]
    public async Task Set_ChangesWhatAgentsMayChange_AndSaysWhoChangedIt()
    {
        using var fixture = Fixture.Create();
        var changed = Records(await fixture.OkAsync(Agent, "settings", "set", "sessions.prompt_suggestions", "off")).Single(static line => Type(line) == "alta.settings.changed");
        Assert.IsFalse(changed.GetProperty("value").GetBoolean());
        Assert.IsTrue(changed.GetProperty("previous").GetBoolean());
        Assert.AreEqual("next prompt", changed.GetProperty("applies").GetString());
        Assert.AreEqual("alta settings set sessions.prompt_suggestions true", changed.GetProperty("undo").GetString());
        var origin = fixture.Settings.Origins.Single();
        Assert.AreEqual((AltaSettingOrigins.Agent, "session-1"), (origin.Kind, origin.SessionId));

        await fixture.OkAsync(Agent, "settings", "set", "sessions.title", "first_prompt");
        Assert.AreEqual("first_prompt", fixture.Settings.Values["sessions.title"].GetString());
        await fixture.FailsAsync(AltaExitCodes.Usage, "usage.missingValue", Agent, "settings", "set", "appearance.code_font", "--stdin");
        var font = await fixture.Dispatcher.InvokeAsync(["settings", "set", "appearance.code_font", "--stdin"], stdin: "[\"Cascadia Code\"]", caller: Agent);
        Assert.AreEqual(AltaExitCodes.Success, font.ExitCode, font.Stdout);
        Assert.AreEqual("Cascadia Code", fixture.Settings.Values["appearance.code_font"][0].GetString());
    }

    [TestMethod]
    public async Task Set_RefusesWhatProtectsTheUser_ListsOfItems_AndBadValues()
    {
        using var fixture = Fixture.Create();
        var refused = await fixture.FailsAsync(AltaExitCodes.PolicyDenied, "settings.readOnly", Agent, "settings", "set", "permissions.default_mode", "default");
        StringAssert.Contains(refused.GetProperty("message").GetString(), "alta settings open permissions.default_mode");
        Assert.IsFalse(fixture.Settings.Values.ContainsKey("permissions.default_mode"), "nothing changed");
        await fixture.FailsAsync(AltaExitCodes.Unsupported, "settings.managedByCommand", Agent, "settings", "set", "mcp.servers", "x");
        await fixture.FailsAsync(AltaExitCodes.Unsupported, "settings.complex", Agent, "settings", "set", "models.tiers", "x");
        await fixture.FailsAsync(AltaExitCodes.Unsupported, "settings.pageOnly", Agent, "settings", "set", "speech.model", "x");
        await fixture.FailsAsync(AltaExitCodes.Usage, "usage.invalidValue", Agent, "settings", "set", "sessions.title", "sometimes");
        await fixture.FailsAsync(AltaExitCodes.Usage, "usage.missingValue", Agent, "settings", "set", "sessions.title");
        await fixture.FailsAsync(AltaExitCodes.Usage, "usage.noProjectScope", Agent, "settings", "set", "sessions.title", "model", "--project", "demo");
        Assert.AreEqual(0, fixture.Settings.Origins.Count, "no change went to the registry");
    }

    [TestMethod]
    public async Task Reset_GivesBackTheDefault_AndShowOpensTheSetting()
    {
        using var fixture = Fixture.Create();
        fixture.Settings.Values["sessions.title"] = Json("\"last_message\"");
        await fixture.OkAsync(Agent, "settings", "reset", "sessions.title", "--show");
        Assert.AreEqual("model", fixture.Settings.Values["sessions.title"].GetString());
        Assert.AreEqual(("sessions", "sessions.title"), fixture.Settings.Opened.Single());
    }

    [TestMethod]
    public async Task Reset_OfAText_GivesBackItsDefault_NotTheWordDefault()
    {
        using var fixture = Fixture.Create();
        fixture.Settings.Values["plugins.builds.branch"] = Json("\"release\"");
        await fixture.OkAsync(Agent, "settings", "reset", "plugins.builds.branch");
        Assert.AreEqual("main", fixture.Settings.Values["plugins.builds.branch"].GetString());
        await fixture.OkAsync(Agent, "settings", "set", "plugins.builds.branch", "default");
        Assert.AreEqual("default", fixture.Settings.Values["plugins.builds.branch"].GetString(), "set takes the text as it is");
    }

    [TestMethod]
    public async Task ASettingOfAPluginOfAProject_IsReadAndChangedForThatProject()
    {
        using var fixture = Fixture.Create();
        await fixture.OkAsync(Agent, "settings", "get", "plugins.builds.branch");
        await fixture.OkAsync(Agent, "settings", "set", "plugins.builds.branch", "release");
        CollectionAssert.AreEqual(new[] { "p1", "p1" }, fixture.Settings.Projects.ToArray(), "the project of the plugin, without --project");
    }

    [TestMethod]
    public async Task Open_ShowsASettingOrAPage_ToTheUser()
    {
        using var fixture = Fixture.Create();
        var opened = Records(await fixture.OkAsync(Agent, "settings", "open", "appearance.theme")).Single(static line => Type(line) == "alta.settings.opened");
        Assert.AreEqual("appearance", opened.GetProperty("page").GetString());
        await fixture.OkAsync(Agent, "settings", "open", "--page", "providers");
        CollectionAssert.AreEqual(new[] { ("appearance", (string?)"appearance.theme"), ("providers", null) }, fixture.Settings.Opened.ToArray());
        await fixture.FailsAsync(AltaExitCodes.NotFound, "settings.notFound", Agent, "settings", "open", "nothing.here");
        fixture.Settings.HasWindow = false;
        await fixture.FailsAsync(AltaExitCodes.ServiceUnavailable, "view.unavailable", Agent, "settings", "open", "appearance.theme");
    }

    private static JsonElement Json(string text)
    {
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    private static string Type(JsonElement line) => line.TryGetProperty("type", out var type) ? type.GetString() ?? "" : "";

    private static List<JsonElement> Records(AltaCommandResult result)
    {
        var values = new List<JsonElement>();
        foreach (var line in (result.Stdout + "\n" + result.Stderr).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!line.StartsWith('{')) continue;
            using var document = JsonDocument.Parse(line);
            values.Add(document.RootElement.Clone());
        }

        return values;
    }

    // The registry as the commands see it: a few settings of each kind, read and written in memory, with the rule
    // that only the window changes what agents may only read.
    private sealed class FakeSettings : IAltaSettings
    {
        private static AltaSettingDescriptor Setting(string id, string label, string page, string pagePath, string kind, string agents, string? defaultJson, params string[] choices)
            => new(id, label, null, page, pagePath, null, kind, [.. choices.Select(static choice => new AltaSettingChoice(choice, null))], null, null,
                defaultJson is null ? null : Json(defaultJson), agents, []);

        public List<AltaSettingDescriptor> Descriptors { get; } =
        [
            Setting("appearance.theme", "Theme", "appearance", "Personalization › Appearance", AltaSettingKinds.Choice, AltaSettingAccess.ReadWrite, "\"dark\"", "dark", "light", "system") with { Owner = "window" },
            Setting("appearance.code_font", "Code font", "appearance", "Personalization › Appearance", AltaSettingKinds.List, AltaSettingAccess.ReadWrite, "[]") with { Owner = "window" },
            Setting("sessions.title", "Title of a session you did not name", "sessions", "Agent & models › Sessions", AltaSettingKinds.Choice, AltaSettingAccess.ReadWrite, "\"model\"", "first_prompt", "last_message", "model") with { Applies = "nextPrompt" },
            Setting("sessions.prompt_suggestions", "Suggest the next prompt", "sessions", "Agent & models › Sessions", AltaSettingKinds.Boolean, AltaSettingAccess.ReadWrite, "true") with { Applies = "nextPrompt" },
            Setting("permissions.default_mode", "Default mode", "permissions", "Agent & models › Permissions", AltaSettingKinds.Choice, AltaSettingAccess.Read, "\"bypassPermissions\"", "bypassPermissions", "default"),
            Setting("speech.model", "Model", "speech", "Personalization › Speech", AltaSettingKinds.Choice, AltaSettingAccess.ReadWrite, "\"parakeet\"") with { Writable = false },
            Setting("models.tiers", "Model tiers", "models", "Agent & models › Models", AltaSettingKinds.Complex, AltaSettingAccess.Read, null),
            Setting("mcp.servers", "MCP Servers", "mcp", "Extensions › MCP Servers", AltaSettingKinds.Collection, AltaSettingAccess.Read, null) with { ManagedBy = "alta mcp server" },
            Setting("plugins.builds.branch", "Release branch", "plugin/source:builds", "Extensions › Builds", AltaSettingKinds.Text, AltaSettingAccess.ReadWrite, "\"main\"")
                with { Owner = "plugin", Plugin = "builds", PluginName = "Builds", Scopes = ["project"], Project = "p1" },
        ];

        public List<string?> Projects { get; } = [];

        public Dictionary<string, JsonElement> Values { get; } = new(StringComparer.Ordinal);

        public List<AltaSettingOrigin> Origins { get; } = [];

        public List<(string Page, string? Id)> Opened { get; } = [];

        public bool HasWindow { get; set; } = true;

        public IReadOnlyList<AltaSettingDescriptor> List() => Descriptors;

        public ValueTask<AltaSettingRead> GetAsync(string id, string? projectId, CancellationToken cancellationToken)
        {
            if (id.StartsWith("plugins.", StringComparison.Ordinal)) Projects.Add(projectId);
            if (Descriptors.FirstOrDefault(setting => setting.Id == id) is not { } setting) return new(new AltaSettingRead(AltaSettingStatus.NotFound, null, null, null));
            if (setting.Kind == AltaSettingKinds.Collection) return new(new AltaSettingRead(AltaSettingStatus.Managed, null, null, null));
            var value = Values.TryGetValue(id, out var known) ? known : setting.Default ?? Json("null");
            return new(new AltaSettingRead(AltaSettingStatus.Ok, value, setting.Default is not { } fallback || JsonElement.DeepEquals(fallback, value) ? "default" : "user", null));
        }

        public ValueTask<AltaSettingWrite> SetAsync(string id, JsonElement? value, string? projectId, AltaSettingOrigin origin, CancellationToken cancellationToken)
        {
            var setting = Descriptors.Single(setting => setting.Id == id);
            if (setting.Plugin is not null) Projects.Add(projectId);
            if (!origin.IsWindow && setting.Agents != AltaSettingAccess.ReadWrite) return new(new AltaSettingWrite(AltaSettingStatus.ReadOnly, null, null, null));
            var wanted = value ?? setting.Default!.Value;
            if (setting.Choices.Count > 0 && (wanted.ValueKind != JsonValueKind.String || setting.Choices.All(choice => choice.Value != wanted.GetString())))
                return new(new AltaSettingWrite(AltaSettingStatus.Invalid, null, null, $"{id} is one of its choices."));
            var previous = Values.TryGetValue(id, out var known) ? known : setting.Default;
            Values[id] = wanted;
            Origins.Add(origin);
            return new(new AltaSettingWrite(AltaSettingStatus.Ok, wanted, previous, null));
        }

        public bool Open(string? page, string? id, string? callerSessionId)
        {
            if (!HasWindow) return false;
            Opened.Add((page ?? Descriptors.First(setting => setting.Id == id).Page, id));
            return true;
        }
    }

    private sealed class Fixture : IDisposable
    {
        private static readonly AltaCallerIdentity Mcp = new() { Kind = "mcp" };

        private Fixture(string root) => Root = root;

        public string Root { get; }

        public FakeSettings Settings { get; } = new();

        public AltaCommandDispatcher Dispatcher { get; private set; } = null!;

        public static Fixture Create(bool service = true)
        {
            var root = Path.Combine(Path.GetTempPath(), "CodeAlta.AltaSettingsCommandsTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var fixture = new Fixture(root);
            var options = new CatalogOptions { GlobalRoot = root };
            var services = new AltaServiceCollection().Add(options).Add(new ProjectCatalog(options));
            if (service) services.Add<IAltaSettings>(fixture.Settings);
            var registry = new AltaCommandRegistry();
            services.Add(registry);
            fixture.Dispatcher = new AltaCommandDispatcher(registry, services);
            return fixture;
        }

        public Task<AltaCommandResult> RunAsync(params string[] args) => Dispatcher.InvokeAsync(args, caller: Mcp).AsTask();

        public Task<AltaCommandResult> OkAsync(params string[] args) => OkAsync(Mcp, args);

        public async Task<AltaCommandResult> OkAsync(AltaCallerIdentity caller, params string[] args)
        {
            var result = await Dispatcher.InvokeAsync(args, caller: caller);
            Assert.AreEqual(AltaExitCodes.Success, result.ExitCode, $"alta {string.Join(' ', args)}: {result.Stdout}{result.Stderr}");
            return result;
        }

        public Task<JsonElement> FailsAsync(int exitCode, string code, params string[] args) => FailsAsync(exitCode, code, Mcp, args);

        public async Task<JsonElement> FailsAsync(int exitCode, string code, AltaCallerIdentity caller, params string[] args)
        {
            var result = await Dispatcher.InvokeAsync(args, caller: caller);
            Assert.AreEqual(exitCode, result.ExitCode, $"alta {string.Join(' ', args)}: {result.Stdout}{result.Stderr}");
            var error = Records(result).First(static line => Type(line) == "alta.error");
            Assert.AreEqual(code, error.GetProperty("code").GetString(), string.Join(' ', args));
            return error;
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A file the test left open is removed with the temporary folder later.
            }
        }
    }
}
