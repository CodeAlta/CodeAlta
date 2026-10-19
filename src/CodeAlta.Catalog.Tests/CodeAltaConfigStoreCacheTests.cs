using CodeAlta.Catalog.ModelTiers;

namespace CodeAlta.Catalog.Tests;

/// <summary>
/// The store reads and parses a configuration file again only when it changed: what each read gives must stay what a
/// read of the file gives, whoever writes it, and nothing a caller does with a result may reach the next one.
/// </summary>
[TestClass]
public sealed class CodeAltaConfigStoreCacheTests
{
    internal const string RichConfig = """
            # A synthesized user configuration: the bundled providers, a few enabled and tuned, and every table.
            [chat]
            default_provider = "anthropic"

            [providers.alibaba]
            enabled = false
            display_name = "Alibaba"
            type = "openai-chat"
            model = "qwen3.7-max"
            api_key_env = "CODEALTA_ALIBABA_API_KEY"
            api_url = "https://dashscope-intl.aliyuncs.com/compatible-mode/v1"

            [providers.anthropic]
            display_name = "Anthropic"
            type = "anthropic"
            model = "claude-sonnet-4-6"
            reasoning_effort = "high"
            api_key_env = "CODEALTA_ANTHROPIC_API_KEY"
            prompt_cache_ttl = "1h"
            spaces = ["work", "personal"]

            [providers.anthropic.compaction]
            ratio = 0.8
            keep_last_user_message = true

            [providers.anthropic.model_overrides."claude-opus-4-6"]
            display_name = "Opus"
            context_window = 1000000

            [providers.azure-openai]
            enabled = false
            display_name = "Azure OpenAI"
            type = "azure-openai"
            model = "gpt-4.1"
            api_key_env = "CODEALTA_AZURE_OPENAI_API_KEY"
            api_url = "https://your-resource.openai.azure.com"

            [providers.claude-code]
            display_name = "Claude Code"
            type = "claude-code"
            permission_mode = "acceptEdits"
            args = ["--verbose"]
            spaces = ["work"]

            [providers.codex]
            display_name = "Codex"
            type = "codex"
            model = "gpt-5.5"
            reasoning_effort = "high"
            service_tier = "priority"
            web_search = "live"

            [providers.copilot]
            display_name = "Copilot"
            type = "copilot"
            model = "claude-sonnet-4.6"
            reasoning_effort = "high"

            [providers.deepseek]
            enabled = false
            display_name = "DeepSeek"
            type = "openai-chat"
            model = "deepseek-v4-pro"
            api_key_env = "CODEALTA_DEEPSEEK_API_KEY"
            api_url = "https://api.deepseek.com"

            [providers.gemini]
            display_name = "Gemini"
            type = "google-genai"
            api_key_env = "CODEALTA_GEMINI_API_KEY"

            [providers.kimi-for-coding]
            enabled = false
            display_name = "Kimi for Coding"
            type = "anthropic"
            model = "K2P6"
            reasoning_effort = "high"
            api_key_env = "CODEALTA_KIMI_API_KEY"
            api_url = "https://api.kimi.com/coding/"

            [providers.minimax]
            enabled = false
            display_name = "MiniMax"
            type = "anthropic"
            model = "MiniMax-M2.7"
            reasoning_effort = "low"
            api_key_env = "CODEALTA_MINIMAX_API_KEY"
            api_url = "https://api.minimax.io/anthropic"
            single_model_id = "MiniMax-M2.7"

            [providers.minimax.profile]
            supports_developer_role = false

            [providers.mistral]
            enabled = false
            display_name = "Mistral"
            type = "mistral"
            model = "mistral-small-latest"
            api_key_env = "CODEALTA_MISTRAL_API_KEY"

            [providers.openai]
            display_name = "OpenAI"
            type = "openai-responses"
            model = "gpt-5.5"
            reasoning_effort = "high"
            api_key_env = "CODEALTA_OPENAI_API_KEY"
            api_url = "https://api.openai.com/v1"
            extra_body = { store = false, metadata = { origin = "codealta" } }

            [providers.openai.request.headers]
            X-Client = "codealta"

            [providers.openrouter]
            display_name = "OpenRouter"
            type = "openai-chat"
            api_key_env = "CODEALTA_OPENROUTER_API_KEY"
            api_url = "https://openrouter.ai/api/v1/"
            models_include_regex = "^(openai|anthropic|google)/"
            spaces = ["personal"]

            [providers.openrouter.model_request."openai/gpt-5.5".headers]
            X-Title = "CodeAlta"

            [providers.xiaomi]
            enabled = false
            display_name = "Xiaomi"
            type = "openai-chat"
            model = "mimo-v2.5-pro"
            api_key_env = "CODEALTA_XIAOMI_API_KEY"
            api_url = "https://token-plan-ams.xiaomimimo.com/v1"

            [providers.xai]
            enabled = false
            display_name = "xAI Grok"
            type = "xai"
            model = "grok-4.3"
            reasoning_effort = "high"
            auth_source = "xai_browser_oauth"
            model_discovery = "xai_endpoint_with_static_fallback"

            [providers.zai]
            enabled = false
            display_name = "Z.ai"
            type = "openai-chat"
            model = "glm-5.1"
            api_key_env = "CODEALTA_ZAI_API_KEY"
            api_url = "https://api.z.ai/api/coding/paas/v4"

            [skills]
            disabled = ["old-skill", "noisy-skill"]

            [skills.spaces]
            release-notes = ["work"]

            [prompts.spaces]
            reviewer = ["work"]

            [plugins.jira]
            enabled = false
            spaces = ["work"]

            [plugins.mcp]
            enabled = true

            [plugins.mcp.servers.github]
            spaces = ["work"]

            [worktrees]
            location = "project"

            [work_items]
            completed_tasks = "keep"
            start = "session"

            [sessions]
            title = "last_message"

            [storage]
            clear_scratchpad_on_startup = false

            [storage.session_retention]
            amount = 6
            unit = "months"

            [speech]
            enabled = false

            [search]
            semantic = true
            excluded_projects = ["0190a1b2-0000-7000-8000-000000000001"]

            [models.tiers]
            complex = ["anthropic:claude-opus-4-6@high", "codex:gpt-5.5@xhigh"]
            standard = ["anthropic:claude-sonnet-4-6@medium"]
            quick = ["openai:gpt-5.5-mini@low"]

            [models.fallback]
            enabled = true
            on = ["quota", "unavailable"]

            [tools]
            disabled = ["webget"]
            """;

    [TestMethod]
    public void LoadGlobal_GivesEachCallerADocumentOfItsOwn()
    {
        using var fixture = new Fixture(RichConfig);
        var first = fixture.Store.LoadGlobal();
        first.Providers!["anthropic"].Model = "changed";
        first.Providers.Remove("codex");
        first.Providers["openai"].ExtraBody!["store"] = true;
        first.Skills!.Disabled!.Add("added-skill");
        first.Tools!.Disabled!.Clear();
        first.Models!.Tiers!.Complex!.Clear();
        first.Chat!.DefaultProvider = "codex";
        first.Sessions = null;

        var second = fixture.Store.LoadGlobal();
        Assert.AreEqual("claude-sonnet-4-6", second.Providers!["anthropic"].Model);
        Assert.IsTrue(second.Providers.ContainsKey("codex"));
        Assert.AreEqual(false, second.Providers["openai"].ExtraBody!["store"]);
        CollectionAssert.AreEquivalent(new[] { "noisy-skill", "old-skill" }, fixture.Store.LoadGlobalDisabledSkillNames().ToArray());
        CollectionAssert.AreEquivalent(new[] { "webget" }, fixture.Store.LoadGlobalDisabledTools().ToArray());
        Assert.AreEqual(2, fixture.Store.LoadGlobalModelSettings().Tiers.Complex.Count);
        Assert.AreEqual("anthropic", fixture.Store.GetEffectiveDefaultProvider());
        Assert.AreEqual(CodeAlta.Agent.AgentSessionTitleMode.LastMessage, fixture.Store.LoadGlobalSessionTitleMode());
        Assert.AreNotSame(second, fixture.Store.LoadGlobal());
    }

    [TestMethod]
    public void TheReadsOfAStore_GiveWhatTheReadsOfANewStoreGive()
    {
        using var fixture = new Fixture(RichConfig);
        for (var round = 0; round < 3; round++)
        {
            var fresh = new CodeAltaConfigStore(fixture.Options);
            Assert.AreEqual(Tomlyn.TomlSerializer.Serialize(fresh.LoadGlobal()), Tomlyn.TomlSerializer.Serialize(fixture.Store.LoadGlobal()));
            CollectionAssert.AreEquivalent(fresh.LoadGlobalDisabledSkillNames().ToArray(), fixture.Store.LoadGlobalDisabledSkillNames().ToArray());
            CollectionAssert.AreEquivalent(fresh.LoadGlobalDisabledTools().ToArray(), fixture.Store.LoadGlobalDisabledTools().ToArray());
            Assert.AreEqual(Describe(fresh.LoadGlobalModelSettings()), Describe(fixture.Store.LoadGlobalModelSettings()));
            Assert.AreEqual(fresh.GetEffectiveDefaultProvider(), fixture.Store.GetEffectiveDefaultProvider());
            Assert.AreEqual(fresh.GetEffectiveProviderPreference("anthropic"), fixture.Store.GetEffectiveProviderPreference("anthropic"));
            Assert.AreEqual(fresh.LoadGlobalSessionTitleMode(), fixture.Store.LoadGlobalSessionTitleMode());
            var (freshSearch, search) = (fresh.LoadGlobalSearchSettings(), fixture.Store.LoadGlobalSearchSettings());
            Assert.AreEqual(freshSearch with { ExcludedProjects = [], ExcludedSpaces = [] }, search with { ExcludedProjects = [], ExcludedSpaces = [] });
            CollectionAssert.AreEqual(freshSearch.ExcludedProjects.ToArray(), search.ExcludedProjects.ToArray());
            Assert.AreEqual(fresh.LoadGlobalSpeechSettings(), fixture.Store.LoadGlobalSpeechSettings());
            CollectionAssert.AreEqual(fresh.LoadGlobalSpaceAssignments().Of(SpaceResourceKind.Provider, "anthropic").ToArray(),
                fixture.Store.LoadGlobalSpaceAssignments().Of(SpaceResourceKind.Provider, "anthropic").ToArray());
            CollectionAssert.AreEqual(fresh.LoadGlobalProviderDefinitions(includeDisabled: true).Select(static definition => Tomlyn.TomlSerializer.Serialize(definition)).ToArray(),
                fixture.Store.LoadGlobalProviderDefinitions(includeDisabled: true).Select(static definition => Tomlyn.TomlSerializer.Serialize(definition)).ToArray());
        }
    }

    [TestMethod]
    public void AChangeMadeByAnotherWriter_IsSeenAtTheNextRead()
    {
        using var fixture = new Fixture("[sessions]\ntitle = \"last_message\"\n");
        Assert.AreEqual(CodeAlta.Agent.AgentSessionTitleMode.LastMessage, fixture.Store.LoadGlobalSessionTitleMode());

        // Another store of the process.
        new CodeAltaConfigStore(fixture.Options).SaveGlobalSessionTitleMode(CodeAlta.Agent.AgentSessionTitleMode.FirstPrompt);
        Assert.AreEqual(CodeAlta.Agent.AgentSessionTitleMode.FirstPrompt, fixture.Store.LoadGlobalSessionTitleMode());

        // Another program.
        File.WriteAllText(fixture.Store.ConfigPath, "[sessions]\ntitle = \"last_message\"\n\n[tools]\ndisabled = [\"webget\"]\n");
        Assert.AreEqual(CodeAlta.Agent.AgentSessionTitleMode.LastMessage, fixture.Store.LoadGlobalSessionTitleMode());
        CollectionAssert.AreEqual(new[] { "webget" }, fixture.Store.LoadGlobalDisabledTools().ToArray());

        File.Delete(fixture.Store.ConfigPath);
        Assert.AreEqual(CodeAlta.Agent.AgentSessionTitles.DefaultMode, fixture.Store.LoadGlobalSessionTitleMode());
        Assert.IsNull(fixture.Store.LoadGlobal().Sessions);
    }

    [TestMethod]
    public void AChangeOfTheSameLengthAtTheSameTime_IsSeenWhileTheWriteIsRecent()
    {
        // A clock of the file system that counts in steps gives both writes the same time; a time to come is recent.
        var time = DateTime.UtcNow + TimeSpan.FromHours(1);
        using var fixture = new Fixture("[sessions]\ntitle = \"last_message\"\n");
        File.SetLastWriteTimeUtc(fixture.Store.ConfigPath, time);
        Assert.AreEqual(CodeAlta.Agent.AgentSessionTitleMode.LastMessage, fixture.Store.LoadGlobalSessionTitleMode());

        File.WriteAllText(fixture.Store.ConfigPath, "[sessions]\ntitle = \"first_prompt\"\n");
        File.SetLastWriteTimeUtc(fixture.Store.ConfigPath, time);
        Assert.AreEqual(CodeAlta.Agent.AgentSessionTitleMode.FirstPrompt, fixture.Store.LoadGlobalSessionTitleMode());
        Assert.AreEqual("first_prompt", fixture.Store.LoadGlobal().Sessions?.Title);
    }

    [TestMethod]
    public void ASaveOfTheStore_IsSeenAtOnce_EvenWhenItLeavesTheStampAsItWas()
    {
        using var fixture = new Fixture(string.Empty);
        fixture.Store.SaveGlobalDefaultProvider("aaaa");
        var old = DateTime.UtcNow - TimeSpan.FromHours(1);
        File.SetLastWriteTimeUtc(fixture.Store.ConfigPath, old);
        Assert.AreEqual("aaaa", fixture.Store.GetEffectiveDefaultProvider());
        var length = new FileInfo(fixture.Store.ConfigPath).Length;

        fixture.Store.SaveGlobalDefaultProvider("bbbb");
        File.SetLastWriteTimeUtc(fixture.Store.ConfigPath, old);
        Assert.AreEqual(length, new FileInfo(fixture.Store.ConfigPath).Length, "The same length: only the save itself tells.");
        Assert.AreEqual("bbbb", fixture.Store.GetEffectiveDefaultProvider());
        Assert.AreEqual("bbbb", fixture.Store.LoadGlobal().Chat?.DefaultProvider);
    }

    [TestMethod]
    public void EverySaveOfTheStore_IsSeenByItsNextRead()
    {
        using var fixture = new Fixture(RichConfig);
        var store = fixture.Store;
        void Read() => _ = (store.LoadGlobal(), store.LoadGlobalDisabledSkillNames(), store.LoadGlobalSpaceAssignments());

        Read();
        store.SaveGlobalSkillEnabled("old-skill", enabled: true);
        Assert.IsFalse(store.LoadGlobalDisabledSkillNames().Contains("old-skill"));
        Read();
        store.SaveGlobalDisabledTools(["shell_command"]);
        CollectionAssert.AreEqual(new[] { "shell_command" }, store.LoadGlobalDisabledTools().ToArray());
        Read();
        store.SaveGlobalPluginEnabled("statistics", enabled: false);
        Assert.AreEqual(false, store.LoadGlobal().Plugins!["statistics"].Enabled);
        Read();
        Assert.IsTrue(store.RemoveGlobalPluginEnabled("statistics"));
        Assert.IsFalse(store.LoadGlobal().Plugins!.ContainsKey("statistics"));
        Read();
        store.SaveGlobalSpaceAssignment(SpaceResourceKind.Provider, "codex", ["work"]);
        CollectionAssert.AreEqual(new[] { "work" }, store.LoadGlobalSpaceAssignments().Of(SpaceResourceKind.Provider, "codex").ToArray());
        Read();
        store.SaveGlobalProviderPreference("codex", "gpt-6", CodeAlta.Agent.AgentReasoningEffort.Low);
        Assert.AreEqual("gpt-6", store.GetEffectiveProviderPreference("codex").Model);
        Read();
        store.SaveGlobalWorktreeSettings("custom", "/tmp/worktrees");
        Assert.AreEqual("custom", store.LoadGlobal().Worktrees?.Location);
        Read();
        store.SaveGlobalSessionTitleMode(CodeAlta.Agent.AgentSessionTitleMode.FirstPrompt);
        Assert.AreEqual(CodeAlta.Agent.AgentSessionTitleMode.FirstPrompt, store.LoadGlobalSessionTitleMode());
        Read();
        store.SaveGlobalModelSettings(ModelSettings.Default);
        Assert.AreEqual(0, store.LoadGlobalModelSettings().Tiers.Complex.Count);
        Read();
        store.SaveGlobalConfigContent("[chat]\ndefault_provider = \"zzz\"\n");
        Assert.AreEqual("zzz", store.GetEffectiveDefaultProvider());
        Assert.AreEqual(0, store.LoadGlobalDisabledSkillNames().Count);
    }

    [TestMethod]
    public void AnInvalidFile_ThrowsAtEachRead_AndAValidOneIsReadAgain()
    {
        using var fixture = new Fixture(RichConfig);
        Assert.AreEqual(2, fixture.Store.LoadGlobalModelSettings().Tiers.Complex.Count);

        File.WriteAllText(fixture.Store.ConfigPath, "[providers.broken\ntype = ");
        for (var attempt = 0; attempt < 3; attempt++)
        {
            Assert.ThrowsExactly<InvalidDataException>(() => fixture.Store.LoadGlobal());
            Assert.ThrowsExactly<InvalidDataException>(() => fixture.Store.LoadGlobalModelSettings());
            Assert.ThrowsExactly<InvalidDataException>(() => fixture.Store.LoadGlobalSpaceAssignments());
            Assert.AreEqual(CodeAlta.Agent.AgentSessionTitles.DefaultMode, fixture.Store.LoadGlobalSessionTitleMode());
        }

        File.WriteAllText(fixture.Store.ConfigPath, RichConfig);
        Assert.AreEqual(2, fixture.Store.LoadGlobalModelSettings().Tiers.Complex.Count);
    }

    [TestMethod]
    public void TheProjectFile_IsReadAgainWhenItChanges()
    {
        using var fixture = new Fixture(string.Empty);
        var project = Path.Combine(fixture.Root, "project");
        Assert.AreEqual(0, fixture.Store.LoadProjectDisabledSkillNames(project).Count);
        Assert.IsFalse(fixture.Store.IsProjectExcludedFromSearch(project));

        Directory.CreateDirectory(Path.Combine(project, ".alta"));
        var path = Path.Combine(project, ".alta", "config.toml");
        File.WriteAllText(path, "[skills]\ndisabled = [\"one\"]\n\n[search]\nenabled = false\n");
        CollectionAssert.AreEqual(new[] { "one" }, fixture.Store.LoadProjectDisabledSkillNames(project).ToArray());
        Assert.IsTrue(fixture.Store.IsProjectExcludedFromSearch(project));

        fixture.Store.SaveProjectSkillEnabled(project, "two", enabled: false);
        CollectionAssert.AreEquivalent(new[] { "one", "two" }, fixture.Store.LoadProjectDisabledSkillNames(project).ToArray());
        CollectionAssert.AreEquivalent(new[] { "one", "two" }, fixture.Store.LoadEffectiveDisabledSkillNames(project).ToArray());

        var owned = fixture.Store.LoadProject(project);
        owned.Skills!.Disabled!.Clear();
        Assert.AreEqual(2, fixture.Store.LoadProject(project).Skills!.Disabled!.Count);

        File.Delete(path);
        Assert.AreEqual(0, fixture.Store.LoadProjectDisabledSkillNames(project).Count);
        Assert.IsFalse(fixture.Store.IsProjectExcludedFromSearch(project));
    }

    [TestMethod]
    public void ReadsSideBySideWithTheSavesOfAnotherStore_SeeTheLastSave()
    {
        using var fixture = new Fixture(RichConfig);
        var writer = new CodeAltaConfigStore(fixture.Options);
        using var stop = new CancellationTokenSource();
        var errors = 0;
        var readers = Enumerable.Range(0, 4).Select(reader => Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    _ = fixture.Store.LoadGlobalDisabledSkillNames();
                    _ = fixture.Store.LoadGlobal();
                }
                catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
                {
                    // A read can meet a file being written, as it always could.
                }
                catch (Exception)
                {
                    Interlocked.Increment(ref errors);
                }
            }
        })).ToArray();
        // The readers stop before the folder goes, also when a save fails: its own error is the one reported.
        try
        {
            for (var index = 0; index < 20; index++) writer.SaveGlobalSkillEnabled($"skill-{index}", enabled: false);
        }
        finally
        {
            stop.Cancel();
            Task.WaitAll(readers);
        }

        Assert.AreEqual(0, errors);
        Assert.AreEqual(22, fixture.Store.LoadGlobalDisabledSkillNames().Count, "The two of the file and the twenty saved.");
        Assert.AreEqual(22, fixture.Store.LoadGlobal().Skills!.Disabled!.Count);
    }

    private static string Describe(ModelSettings settings)
        => string.Join(";", settings.Tiers.Complex.Concat(settings.Tiers.Standard).Concat(settings.Tiers.Quick).Select(static target => target.Format()))
           + $"|{settings.Fallback}";

    private sealed class Fixture : IDisposable
    {
        public Fixture(string content)
        {
            Root = Path.Combine(Path.GetTempPath(), "codealta-config-cache-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            Options = new CatalogOptions { GlobalRoot = Root };
            if (content.Length > 0) File.WriteAllText(Options.ConfigPath, content);
            Store = new CodeAltaConfigStore(Options);
        }

        public string Root { get; }

        public CatalogOptions Options { get; }

        public CodeAltaConfigStore Store { get; }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
