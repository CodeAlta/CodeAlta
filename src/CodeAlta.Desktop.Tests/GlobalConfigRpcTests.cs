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

    private const string ProvidersConfig = """
        [chat]
        default_provider = "local"

        [providers.local]
        display_name = "Local"
        type = "openai-chat"
        model = "model-a"
        api_key = "secret-value"
        api_url = "http://127.0.0.1:9999/v1"
        network_timeout_seconds = 77

        [providers.spare]
        enabled = false
        type = "anthropic"
        api_key_env = "SPARE_KEY"
        """;

    [TestMethod]
    public async Task Providers_ListsDefinitionsWithoutSecretsAndWithTheConfiguredDefault()
    {
        await using var fixture = new Fixture(ProvidersConfig);
        var listed = fixture.Service.Providers(new(Epoch));

        Assert.AreEqual("ok", listed.Status);
        Assert.AreEqual("local", listed.DefaultProvider);
        Assert.AreEqual(GlobalConfigService.Revision(File.ReadAllText(fixture.ConfigPath)), listed.Revision);
        var local = listed.Providers.Single(provider => provider.Key == "local");
        Assert.IsTrue(local.Enabled);
        Assert.IsTrue(local.HasApiKey);
        Assert.AreEqual("Local", local.DisplayName);
        Assert.AreEqual("model-a", local.Model);
        var spare = listed.Providers.Single(provider => provider.Key == "spare");
        Assert.IsFalse(spare.Enabled);
        Assert.IsFalse(spare.HasApiKey);
        Assert.IsNull(spare.DisplayName, "A value the file does not set stays unset.");
        Assert.AreEqual("anthropic", spare.Type);
        Assert.IsFalse(listed.ToString()!.Contains("secret-value", StringComparison.Ordinal));
        CollectionAssert.Contains(listed.ProviderTypes.ToArray(), "openai-chat");
        CollectionAssert.Contains(listed.ReasoningEfforts.ToArray(), "xhigh");
        Assert.AreEqual("stale_epoch", fixture.Service.Providers(new("another")).Status);
        Assert.AreEqual("unavailable", new GlobalConfigService().Providers(new(Epoch)).Status);
    }

    [TestMethod]
    public async Task Providers_ReportWhatBlankFieldsFallBackTo()
    {
        await using var fixture = new Fixture("""
            [providers.anthropic]
            enabled = false
            type = "anthropic"

            [providers.codex]
            enabled = false
            type = "codex"
            model = "gpt-custom"

            [providers.openai]
            enabled = false
            type = "anthropic"

            [providers.local]
            enabled = false
            type = "openai-chat"
            """);
        var listed = fixture.Service.Providers(new(Epoch));
        Assert.AreEqual("ok", listed.Status);
        GlobalConfigProviderDefaults Defaults(string key) => listed.Providers.Single(provider => provider.Key == key).Defaults;

        // The built-in template entry with the same key and adapter type. Its model and reasoning effort are not
        // defaults: a provider left without a model starts with the first one it lists.
        Assert.AreEqual(new GlobalConfigProviderDefaults("Anthropic", null, null, null, "CODEALTA_ANTHROPIC_API_KEY"), Defaults("anthropic"));
        // The template, completed by what the type fills in; a written value does not change its default.
        Assert.AreEqual(new GlobalConfigProviderDefaults("Codex", null, null, "https://api.openai.com/v1", null), Defaults("codex"));
        Assert.AreEqual("gpt-custom", listed.Providers.Single(static provider => provider.Key == "codex").Model);
        // A template entry of another adapter type describes a different provider: only the key remains as the name.
        Assert.AreEqual(new GlobalConfigProviderDefaults("openai", null, null, null, null), Defaults("openai"));
        Assert.AreEqual(new GlobalConfigProviderDefaults("local", null, null, null, null), Defaults("local"));

        CollectionAssert.AreEqual(listed.ProviderTypes.ToArray(), listed.TypeDefaults.Select(static entry => entry.Type).ToArray());
        GlobalConfigProviderDefaults Type(string type) => listed.TypeDefaults.Single(entry => entry.Type == type).Defaults;
        Assert.AreEqual(new GlobalConfigProviderDefaults("Codex", null, null, "https://api.openai.com/v1", null), Type("codex"));
        Assert.AreEqual(new GlobalConfigProviderDefaults("Copilot", null, null, null, null), Type("copilot"));
        Assert.AreEqual(new GlobalConfigProviderDefaults("xAI Grok", null, null, null, null), Type("xai"));
        foreach (var type in new[] { "openai-chat", "openai-responses", "azure-openai", "anthropic", "google-genai", "vertex-ai", "mistral" })
            Assert.AreEqual(new GlobalConfigProviderDefaults(null, null, null, null, null), Type(type), type);

        Assert.AreEqual(0, fixture.Service.Providers(new("another")).TypeDefaults.Count);
        Assert.AreEqual(0, new GlobalConfigService().Providers(new(Epoch)).TypeDefaults.Count);
    }

    [TestMethod]
    public async Task SaveProvider_UpdatesOnlyTheEditedFieldsAndKeepsTheStoredSecret()
    {
        await using var fixture = new Fixture(ProvidersConfig);
        var listed = fixture.Service.Providers(new(Epoch));
        var edit = new GlobalConfigProviderEdit("local", "openai-chat", true, "Local renamed", "model-b", "high", "http://127.0.0.1:9999/v1", null, null, false);

        Assert.AreEqual("conflict", fixture.Service.SaveProvider(new(Epoch, "stale", "local", edit, false, false)).Status);
        Assert.AreEqual("stale_epoch", fixture.Service.SaveProvider(new("another", listed.Revision, "local", edit, false, false)).Status);
        Assert.AreEqual("invalid", fixture.Service.SaveProvider(new(Epoch, listed.Revision, "local", edit with { Key = "bad key" }, false, false)).Status);
        Assert.AreEqual("invalid", fixture.Service.SaveProvider(new(Epoch, listed.Revision, "local", edit with { Key = "spare" }, false, false)).Status);
        Assert.AreEqual("invalid", fixture.Service.SaveProvider(new(Epoch, listed.Revision, "missing", edit, false, false)).Status);
        Assert.AreEqual(ProvidersConfig, File.ReadAllText(fixture.ConfigPath), "Refused edits must not write.");

        var saved = fixture.Service.SaveProvider(new(Epoch, listed.Revision, "local", edit, false, false));
        Assert.AreEqual("ok", saved.Status, saved.Message);
        var text = File.ReadAllText(fixture.ConfigPath);
        StringAssert.Contains(text, "secret-value", "A null key keeps the stored secret.");
        StringAssert.Contains(text, "network_timeout_seconds = 77", "Settings the form does not show are preserved.");
        var after = fixture.Service.Providers(new(Epoch));
        Assert.AreEqual(saved.Revision, after.Revision);
        var local = after.Providers.Single(provider => provider.Key == "local");
        Assert.AreEqual("Local renamed", local.DisplayName);
        Assert.AreEqual("model-b", local.Model);
        Assert.AreEqual("high", local.ReasoningEffort);
        Assert.IsTrue(local.HasApiKey);

        var cleared = fixture.Service.SaveProvider(new(Epoch, after.Revision, "local", edit with { ApiKeyEnv = "LOCAL_KEY", ClearApiKey = true }, false, false));
        Assert.AreEqual("ok", cleared.Status, cleared.Message);
        Assert.IsFalse(File.ReadAllText(fixture.ConfigPath).Contains("secret-value", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task SaveProvider_AddsEnablesAndMakesDefault_AndDeleteRemovesAndApplies()
    {
        await using var fixture = new Fixture(ProvidersConfig);
        var listed = fixture.Service.Providers(new(Epoch));
        var added = fixture.Service.SaveProvider(new(Epoch, listed.Revision, null,
            new("extra", "openai-chat", true, "Extra", "model-x", null, "http://127.0.0.1:9998/v1", null, "extra-secret", false), true, true));

        Assert.AreEqual("ok", added.Status, added.Message);
        var after = fixture.Service.Providers(new(Epoch));
        Assert.AreEqual("extra", after.DefaultProvider);
        Assert.IsTrue(after.Providers.Single(provider => provider.Key == "extra").HasApiKey);
        var registered = fixture.Registry.ListProviders(includeDisabled: true).Select(provider => provider.ProviderId.Value).ToArray();
        CollectionAssert.Contains(registered, "extra");
        CollectionAssert.Contains(registered, "local");
        CollectionAssert.DoesNotContain(registered, "spare", "A disabled provider is not registered.");

        Assert.AreEqual("invalid", fixture.Service.DeleteProvider(new(Epoch, after.Revision, "missing", true)).Status);
        var deleted = fixture.Service.DeleteProvider(new(Epoch, after.Revision, "extra", true));
        Assert.AreEqual("ok", deleted.Status, deleted.Message);
        var final = fixture.Service.Providers(new(Epoch));
        Assert.IsFalse(final.Providers.Any(provider => provider.Key == "extra"));
        Assert.AreNotEqual("extra", final.DefaultProvider);
        CollectionAssert.DoesNotContain(fixture.Registry.ListProviders(includeDisabled: true).Select(provider => provider.ProviderId.Value).ToArray(), "extra");
    }

    // Settings the provider form never shows and the typed configuration document does not model.
    private const string UnmodeledSettings = """


        [plugins.mcp]
        enabled = false
        tool_timeout_ms = 1234

        [plugins.mcp.servers.files]
        enabled = false

        [future_section]
        answer = 42
        """;

    [TestMethod]
    public async Task SaveProviderAndDeleteProvider_KeepSettingsOutsideTheProviderForm()
    {
        await using var fixture = new Fixture(ProvidersConfig + UnmodeledSettings);
        void AssertKept(string step)
        {
            var text = File.ReadAllText(fixture.ConfigPath);
            StringAssert.Contains(text, "tool_timeout_ms = 1234", step);
            StringAssert.Contains(text, "[plugins.mcp.servers.files]", step);
            StringAssert.Contains(text, "[future_section]", step);
            StringAssert.Contains(text, "answer = 42", step);
            StringAssert.Contains(text, "network_timeout_seconds = 77", step);
            Assert.AreEqual(false, fixture.Store.LoadGlobal().Plugins!["mcp"].Enabled, step);
        }

        var listed = fixture.Service.Providers(new(Epoch));
        var edited = fixture.Service.SaveProvider(new(Epoch, listed.Revision, "local",
            new("local", "openai-chat", true, "Local renamed", "model-b", null, "http://127.0.0.1:9999/v1", null, null, false), false, false));
        Assert.AreEqual("ok", edited.Status, edited.Message);
        AssertKept("after editing a provider");

        var added = fixture.Service.SaveProvider(new(Epoch, edited.Revision, null,
            new("extra", "openai-chat", true, "Extra", "model-x", null, "http://127.0.0.1:9998/v1", null, "extra-secret", false), true, false));
        Assert.AreEqual("ok", added.Status, added.Message);
        Assert.AreEqual("extra", fixture.Service.Providers(new(Epoch)).DefaultProvider);
        AssertKept("after adding a default provider");

        var deleted = fixture.Service.DeleteProvider(new(Epoch, added.Revision, "extra", false));
        Assert.AreEqual("ok", deleted.Status, deleted.Message);
        Assert.IsFalse(fixture.Service.Providers(new(Epoch)).Providers.Any(provider => provider.Key == "extra"));
        AssertKept("after deleting a provider");
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "CodeAlta-global-config-" + Guid.NewGuid().ToString("N"));

        public Fixture(string? config = null)
        {
            Directory.CreateDirectory(_root);
            var options = new CatalogOptions { GlobalRoot = _root };
            ConfigPath = options.ConfigPath;
            if (config is not null) File.WriteAllText(ConfigPath, config);
            Store = new CodeAltaConfigStore(options);
            Service = new GlobalConfigService(Store, Registry, _root, Epoch);
        }

        public CodeAltaConfigStore Store { get; }
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
