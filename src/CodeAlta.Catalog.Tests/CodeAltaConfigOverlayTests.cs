namespace CodeAlta.Catalog.Tests;

/// <summary>
/// The configuration a process such as the command line reads: the user's file or none, the inline configuration of
/// the environment, then the <c>-c</c> settings; a strict reading; no write.
/// </summary>
[TestClass]
public sealed class CodeAltaConfigOverlayTests
{
    private const string UserConfig = """
        [chat]
        default_provider = "anthropic"

        [providers.anthropic]
        type = "anthropic"
        model = "claude-sonnet-4-6"
        api_key_env = "CODEALTA_ANTHROPIC_API_KEY"

        [sessions]
        title = "last_message"

        [skills]
        disabled = ["a", "b"]
        """;

    [TestMethod]
    public void AnOverlay_MergesOverTheFile_TablesKeyByKey_ValuesAndArraysReplaced()
    {
        using var fixture = new Fixture(UserConfig, Inline("""
            [providers.anthropic]
            model = "claude-opus-5-5"

            [skills]
            disabled = ["c"]
            """));

        var anthropic = fixture.Store.LoadGlobalProviderDefinitions().Single(static provider => provider.ProviderKey == "anthropic");
        Assert.AreEqual("claude-opus-5-5", anthropic.Model, "The overlay wins on a value.");
        Assert.AreEqual("CODEALTA_ANTHROPIC_API_KEY", anthropic.ApiKeyEnv, "The rest of the table of the file stays.");
        Assert.AreEqual("anthropic", fixture.Store.GetEffectiveDefaultProvider());
        Assert.AreEqual(CodeAlta.Agent.AgentSessionTitleMode.LastMessage, fixture.Store.LoadGlobalSessionTitleMode());
        CollectionAssert.AreEquivalent(new[] { "c" }, fixture.Store.LoadGlobalDisabledSkillNames().ToArray(), "An array is replaced, not merged.");
        StringAssert.Contains(fixture.Store.LoadGlobalConfigContent(), "claude-opus-5-5");
        Assert.AreEqual(UserConfig, File.ReadAllText(fixture.Store.ConfigPath), "Nothing is written.");
    }

    [TestMethod]
    public void TheCommandLine_WinsOverTheInlineVariable_WhichWinsOverTheFile()
    {
        using var fixture = new Fixture(UserConfig,
            Inline("[chat]\ndefault_provider = \"codex\"\n[sessions]\ntitle = \"model\"\n"),
            CodeAltaConfigOverlay.FromAssignment("chat.default_provider=anthropic-work"),
            CodeAltaConfigOverlay.FromAssignment("sessions.prompt_suggestions=false"),
            CodeAltaConfigOverlay.FromAssignment("tools.disabled=[\"webget\"]"));

        Assert.AreEqual("anthropic-work", fixture.Store.GetEffectiveDefaultProvider(), "A value that is not TOML is a string.");
        Assert.AreEqual(CodeAlta.Agent.AgentSessionTitleMode.Model, fixture.Store.LoadGlobalSessionTitleMode());
        Assert.IsFalse(fixture.Store.LoadGlobalPromptSuggestions(), "A TOML value keeps its type.");
        CollectionAssert.AreEqual(new[] { "webget" }, fixture.Store.LoadGlobalDisabledTools().ToArray());
    }

    [TestMethod]
    public void AnAssignment_IsADottedKeyAndATomlValue()
    {
        static string Content(string assignment) => CodeAltaConfigOverlay.FromAssignment(assignment).Content.Trim().ReplaceLineEndings("\n");

        var overlay = CodeAltaConfigOverlay.FromAssignment("providers.anthropic.api_key_env=\"MY_KEY\"");
        Assert.AreEqual("-c providers.anthropic.api_key_env", overlay.Source);
        StringAssert.Contains(overlay.Content, "api_key_env = \"MY_KEY\"");
        StringAssert.Contains(Content("default_provider=anthropic"), "default_provider = \"anthropic\"");
        StringAssert.Contains(Content("a=1 2"), "a = \"1 2\"", "Not one value: the text as it is.");
        StringAssert.Contains(Content("a=1\n[evil]\nx = 2"), "a = \"1\\n[evil]\\nx = 2\"", "A value cannot add a table.");
        StringAssert.Contains(Content("a="), "a = \"\"");
        StringAssert.Contains(Content("a = 42"), "a = 42");
        StringAssert.Contains(Content("providers.\"a=b\".model=x"), "model = \"x\"", "A quoted key may hold an equal sign.");
        Assert.ThrowsExactly<ArgumentException>(() => CodeAltaConfigOverlay.FromAssignment("no-value"));
        Assert.ThrowsExactly<ArgumentException>(() => CodeAltaConfigOverlay.FromAssignment("=1"));
        Assert.ThrowsExactly<ArgumentException>(() => CodeAltaConfigOverlay.FromAssignment("[chat]=1"));
        Assert.ThrowsExactly<ArgumentException>(() => CodeAltaConfigOverlay.FromAssignment("a\nb=2"));
    }

    [TestMethod]
    public void TheInlineVariable_IsTomlOrNothing()
    {
        Assert.IsNull(CodeAltaConfigOverlay.FromInlineContent(null));
        Assert.IsNull(CodeAltaConfigOverlay.FromInlineContent("  \n"));
        Assert.AreEqual(CodeAltaConfigOverlay.InlineContentVariable, Inline("[chat]\n").Source);
        var error = Assert.ThrowsExactly<InvalidDataException>(() => CodeAltaConfigOverlay.FromInlineContent("[chat\n"));
        StringAssert.Contains(error.Message, "CODEALTA_CONFIG_CONTENT");
    }

    [TestMethod]
    public void IgnoringTheUserConfig_ReadsTheOverlaysAlone_AndNeverCreatesTheFile()
    {
        using (var fixture = new Fixture(UserConfig, ignoreUserConfig: true))
        {
            Assert.AreEqual(0, fixture.Store.LoadGlobalProviderDefinitions(includeDisabled: true).Count);
            Assert.IsNull(fixture.Store.GetEffectiveDefaultProvider());
            Assert.AreEqual(CodeAlta.Agent.AgentSessionTitles.DefaultMode, fixture.Store.LoadGlobalSessionTitleMode());
            Assert.AreEqual(0, fixture.Store.LoadGlobalDisabledSkillNames().Count);
            Assert.AreEqual(string.Empty, fixture.Store.LoadGlobalConfigContent());
            Assert.AreEqual(Storage.StorageSettings.Default, fixture.Store.LoadGlobalStorageSettings());
            Assert.IsTrue(fixture.Store.ValidateEffectiveGlobalConfig().IsValid);
        }

        using (var fixture = new Fixture(string.Empty, ignoreUserConfig: true, overlays: [CodeAltaConfigOverlay.FromAssignment("chat.default_provider=codex")]))
        {
            Assert.AreEqual("codex", fixture.Store.GetEffectiveDefaultProvider());
            Assert.IsFalse(fixture.Store.EnsureGlobalConfigExists());
            Assert.IsFalse(File.Exists(fixture.Store.ConfigPath), "The first-run file is not created.");
        }
    }

    [TestMethod]
    public void TheStrictReading_RejectsAnUnknownSetting_NamingWhereItComesFrom()
    {
        using (var fixture = new Fixture("[chat]\ndefualt_provider = \"anthropic\"\n", strict: true))
        {
            var error = Assert.ThrowsExactly<InvalidDataException>(() => fixture.Store.LoadGlobal());
            StringAssert.Contains(error.Message, "chat.defualt_provider");
            StringAssert.Contains(error.Message, fixture.Store.ConfigPath);
            Assert.IsFalse(fixture.Store.ValidateEffectiveGlobalConfig().IsValid);
            Assert.IsNotNull(new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = fixture.Root }).LoadGlobal(), "Without it the key is left as it is.");
        }

        using (var fixture = new Fixture(UserConfig, strict: true, overlays: [CodeAltaConfigOverlay.FromAssignment("providers.anthropic.modle=\"x\"")]))
        {
            var result = fixture.Store.ValidateEffectiveGlobalConfig();
            Assert.IsFalse(result.IsValid);
            StringAssert.Contains(result.Message, "providers.anthropic.modle (-c providers.anthropic.modle)");
            Assert.IsNull(result.Line, "A line of the merged text means nothing to the user.");
        }

        using (var fixture = new Fixture(string.Empty, strict: true))
        {
            Directory.CreateDirectory(Path.Combine(fixture.Root, "project", ".alta"));
            File.WriteAllText(Path.Combine(fixture.Root, "project", ".alta", "config.toml"), "[search]\nenabeld = false\n");
            var error = Assert.ThrowsExactly<InvalidDataException>(() => fixture.Store.LoadProject(Path.Combine(fixture.Root, "project")));
            StringAssert.Contains(error.Message, "search.enabeld", "Project files are read strictly too.");
        }
    }

    [TestMethod]
    public void TheStrictReading_AcceptsEveryTableCodeAltaReads_AndWhatOtherOwnersRead()
    {
        const string owned = """
            [plugins.mcp]
            enabled = true
            policy = "strict"

            [plugins.mcp.servers.github]
            spaces = ["work"]

            [providers.custom]
            type = "a-plugin-type"
            anything = 1

            [automations.4f6c3b52-8c1e-4a3a-9b7d-0d1e2f3a4b5c]
            prompt = "Hello"

            [skills.spaces]
            reviewer = ["work"]

            [prompts.spaces]
            planner = ["work"]
            """;
        foreach (var content in new[] { CodeAltaConfigStoreCacheTests.RichConfig, CodeAltaConfigStore.GetDefaultGlobalConfigContent(), owned })
        {
            using var fixture = new Fixture(content, strict: true);
            var result = fixture.Store.ValidateEffectiveGlobalConfig();
            Assert.IsTrue(result.IsValid, result.Message);
            _ = fixture.Store.LoadGlobal();
        }
    }

    [TestMethod]
    public void AnInvalidOverlay_IsNamedInTheError()
    {
        using (var fixture = new Fixture(UserConfig, overlays: [CodeAltaConfigOverlay.FromAssignment("chat=1")]))
        {
            var error = Assert.ThrowsExactly<InvalidDataException>(() => fixture.Store.LoadGlobal());
            StringAssert.Contains(error.Message, "-c chat");
            var result = fixture.Store.ValidateEffectiveGlobalConfig();
            Assert.IsFalse(result.IsValid);
            StringAssert.Contains(result.Message, "-c chat");
            Assert.IsNull(result.Line);
        }

        using (var fixture = new Fixture(UserConfig, Inline("[storage.session_retention]\nunit = \"fortnights\"\n")))
        {
            var result = fixture.Store.ValidateEffectiveGlobalConfig();
            Assert.IsFalse(result.IsValid);
            StringAssert.Contains(result.Message, "CODEALTA_CONFIG_CONTENT");
        }

        // The file alone is invalid; the overlay fixes it, and a later setting breaks it again: the later setting is named.
        using (var fixture = new Fixture("[storage.session_retention]\nunit = \"fortnights\"\n",
            CodeAltaConfigOverlay.FromAssignment("storage.session_retention.unit=weeks"),
            CodeAltaConfigOverlay.FromAssignment("storage.session_retention.amount=-3")))
        {
            var result = fixture.Store.ValidateEffectiveGlobalConfig();
            Assert.IsFalse(result.IsValid);
            StringAssert.Contains(result.Message, "-c storage.session_retention.amount");
        }

        using (var fixture = new Fixture("[storage.session_retention]\nunit = \"fortnights\"\n", CodeAltaConfigOverlay.FromAssignment("storage.session_retention.unit=weeks")))
        {
            Assert.IsTrue(fixture.Store.ValidateEffectiveGlobalConfig().IsValid);
            Assert.AreEqual("weeks", fixture.Store.LoadGlobalStorageSettings().SessionRetention.Unit);
        }
    }

    [TestMethod]
    public void AReadOnlyStore_RefusesEverySave_AndLeavesTheFiles()
    {
        using var fixture = new Fixture(UserConfig, readOnly: true);
        var project = Path.Combine(fixture.Root, "project");
        Directory.CreateDirectory(project);
        var store = fixture.Store;
        Assert.IsTrue(store.IsReadOnly);
        Action[] saves =
        [
            () => store.SaveGlobalConfigContent("[chat]\n"),
            () => store.SaveGlobalDefaultProvider("codex"),
            () => store.SaveGlobalSessionTitleMode(CodeAlta.Agent.AgentSessionTitleMode.Model),
            () => store.SaveGlobalDisabledTools(["webget"]),
            () => store.SaveGlobalSkillEnabled("a", true),
            () => store.SaveGlobalPluginEnabled("mcp", false),
            () => store.RemoveGlobalPluginEnabled("mcp"),
            () => store.SaveGlobalProviderPreference("anthropic", "x", null),
            () => store.SaveGlobalProviderDefinitions([]),
            () => store.SaveGlobalSpaceAssignment(SpaceResourceKind.Provider, "anthropic", ["work"]),
            () => store.SaveGlobalStorageSettings(Storage.StorageSettings.Default),
            () => store.SaveGlobalExternalProvider("custom", "a-plugin-type", new Dictionary<string, object?>()),
            () => store.SaveProjectSkillEnabled(project, "a", false),
            () => store.SaveProjectPluginEnabled(project, "mcp", false),
        ];
        foreach (var save in saves)
        {
            var error = Assert.ThrowsExactly<InvalidOperationException>(save);
            StringAssert.Contains(error.Message, "read-only");
        }

        Assert.AreEqual(UserConfig, File.ReadAllText(store.ConfigPath));
        Assert.IsFalse(Directory.Exists(Path.Combine(project, ".alta")));

        File.Delete(store.ConfigPath);
        Assert.IsFalse(store.EnsureGlobalConfigExists(), "The first-run file is a side effect of a frontend: nothing happens.");
        Assert.IsFalse(File.Exists(store.ConfigPath));
    }

    [TestMethod]
    public void WithAnOverlay_AChangeOfTheFile_IsSeenAtTheNextRead_AndTheOverlayStillWins()
    {
        using var fixture = new Fixture(UserConfig, CodeAltaConfigOverlay.FromAssignment("sessions.title=\"model\""));
        Assert.AreEqual(CodeAlta.Agent.AgentSessionTitleMode.Model, fixture.Store.LoadGlobalSessionTitleMode());
        Assert.AreEqual("anthropic", fixture.Store.GetEffectiveDefaultProvider());

        // Another program.
        File.WriteAllText(fixture.Store.ConfigPath, UserConfig.Replace("default_provider = \"anthropic\"", "default_provider = \"codex\"", StringComparison.Ordinal));
        Assert.AreEqual("codex", fixture.Store.GetEffectiveDefaultProvider());
        Assert.AreEqual(CodeAlta.Agent.AgentSessionTitleMode.Model, fixture.Store.LoadGlobalSessionTitleMode());

        // A save of the store writes the file alone, and its next read merges the overlay again.
        fixture.Store.SaveGlobalDefaultProvider("anthropic");
        Assert.AreEqual("anthropic", fixture.Store.GetEffectiveDefaultProvider());
        Assert.AreEqual(CodeAlta.Agent.AgentSessionTitleMode.Model, fixture.Store.LoadGlobalSessionTitleMode());
        Assert.DoesNotContain("model", File.ReadAllText(fixture.Store.ConfigPath).Split('\n').Where(static line => line.StartsWith("title", StringComparison.Ordinal)).Single());

        File.Delete(fixture.Store.ConfigPath);
        Assert.IsNull(fixture.Store.GetEffectiveDefaultProvider());
        Assert.AreEqual(CodeAlta.Agent.AgentSessionTitleMode.Model, fixture.Store.LoadGlobalSessionTitleMode(), "Without a file, the overlays alone.");
    }

    [TestMethod]
    public void CatalogOptions_CarryTheConfigOptions_ToEveryStoreBuiltFromThem()
    {
        var options = new CatalogOptions { GlobalRoot = Path.GetTempPath() };
        Assert.AreSame(CodeAltaConfigOptions.Default, options.Config);
        Assert.ThrowsExactly<ArgumentNullException>(() => options.Config = null!);
        var overlays = new List<CodeAltaConfigOverlay> { CodeAltaConfigOverlay.FromAssignment("a=1") };
        options.Config = new CodeAltaConfigOptions { ReadOnly = true, Overlays = overlays };
        overlays.Clear();
        Assert.AreEqual(1, options.Config.Overlays.Count, "The options keep a list of their own.");
        Assert.AreSame(options.Config, new CodeAltaConfigStore(options).ConfigOptions);
    }

    private static CodeAltaConfigOverlay Inline(string content) => CodeAltaConfigOverlay.FromInlineContent(content)!;

    private sealed class Fixture : IDisposable
    {
        public Fixture(string content, params CodeAltaConfigOverlay[] overlays)
            : this(content, ignoreUserConfig: false, strict: false, readOnly: false, overlays)
        {
        }

        public Fixture(string content, bool ignoreUserConfig = false, bool strict = false, bool readOnly = false, CodeAltaConfigOverlay[]? overlays = null)
        {
            Root = Path.Combine(Path.GetTempPath(), "codealta-config-overlay-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            var options = new CatalogOptions
            {
                GlobalRoot = Root,
                Config = new CodeAltaConfigOptions { IgnoreUserConfig = ignoreUserConfig, Strict = strict, ReadOnly = readOnly, Overlays = overlays ?? [] },
            };
            if (content.Length > 0) File.WriteAllText(options.ConfigPath, content);
            Store = new CodeAltaConfigStore(options);
        }

        public string Root { get; }

        public CodeAltaConfigStore Store { get; }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
