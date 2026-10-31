using CodeAlta.Agent;
using CodeAlta.Catalog;
using CodeAlta.Desktop.Rpc;
using CodeAlta.Orchestration.Runtime;

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
    public async Task Validate_KnowsTheProviderTypesOfTheRunningPlugins()
    {
        const string content = """
[providers.cursor]
type = "fake-agent"
command = "cursor-agent"

[providers.future]
type = "future-provider"
""";
        await using var endpoints = new CodeAlta.Agent.Harness.LoopbackHarnessToolEndpoints();
        var types = new CodeAlta.Hosting.PluginModelProviderTypes(endpoints);
        types.Attach(static () => [new CodeAlta.Plugins.Abstractions.PluginModelProviderContribution
        {
            ProviderType = "fake-agent", DisplayName = "Fake agent", CreateRuntime = static _ => throw new NotSupportedException("Not created in this test."),
        }]);

        // The type a plugin adds is not one of a newer version: only the type nothing knows is warned about.
        var result = new GlobalConfigService { PluginProviderTypes = types }.Validate(new(content));
        Assert.IsTrue(result.Valid);
        StringAssert.Contains(result.Warning, "providers.future");
        Assert.IsFalse(result.Warning!.Contains("cursor", StringComparison.Ordinal), result.Warning);
        // Without the plugins, the type is unknown too.
        StringAssert.Contains(new GlobalConfigService().Validate(new(content)).Warning, "providers.cursor");
    }

    [TestMethod]
    public async Task PluginProviderSignIn_StreamsThePromptOpensTheBrowserAndEnds()
    {
        const string content = """
[providers.codex-work]
type = "fake-agent"
""";
        await using var fixture = new Fixture(content);
        await using var endpoints = new CodeAlta.Agent.Harness.LoopbackHarnessToolEndpoints();
        var types = new CodeAlta.Hosting.PluginModelProviderTypes(endpoints);
        var account = new PromptingAccount();
        types.Attach(() => [new CodeAlta.Plugins.Abstractions.PluginModelProviderContribution
        {
            ProviderType = "fake-agent", DisplayName = "Fake agent", CreateRuntime = static _ => throw new NotSupportedException("Not created in this test."), Account = account,
        }]);
        var opened = new List<Uri>();
        var service = new GlobalConfigService(fixture.Store, fixture.Registry, Path.GetDirectoryName(fixture.ConfigPath)!, Epoch)
        {
            PluginProviderTypes = types,
            OpenSignInBrowser = uri => { opened.Add(uri); return true; },
        };

        var events = new List<GlobalConfigPluginSignInEvent>();
        await foreach (var item in service.SignInAsync(new(Epoch, "codex-work", "device"), CancellationToken.None)) events.Add(item);

        CollectionAssert.AreEqual(new[] { "prompt", "completed" }, events.Select(static item => item.Kind).ToArray());
        Assert.AreEqual("https://auth.example.com/device", events[0].Url);
        Assert.AreEqual("ABCD-1234", events[0].UserCode);
        Assert.IsTrue(events[0].BrowserOpened);
        Assert.AreEqual("ok", events[1].Status);
        Assert.AreEqual(new Uri("https://auth.example.com/device"), opened.Single());

        account.Failure = "The sign-in to ChatGPT did not complete: denied";
        events.Clear();
        await foreach (var item in service.SignInAsync(new(Epoch, "codex-work", "device"), CancellationToken.None)) events.Add(item);
        Assert.AreEqual("failed", events[^1].Kind);
        Assert.AreEqual("failed", events[^1].Status);
        Assert.AreEqual(account.Failure, events[^1].Message);

        events.Clear();
        await foreach (var item in service.SignInAsync(new("another", "codex-work", "device"), CancellationToken.None)) events.Add(item);
        Assert.AreEqual("stale_epoch", events.Single().Status);
    }

    [TestMethod]
    public async Task SavePluginProvider_SavesTheContextSize_AndKeepsTheOtherValues()
    {
        const string content = """
[providers.codex-work]
type = "fake-agent"
command = "codex"
""";
        await using var fixture = new Fixture(content);
        await using var endpoints = new CodeAlta.Agent.Harness.LoopbackHarnessToolEndpoints();
        var types = new CodeAlta.Hosting.PluginModelProviderTypes(endpoints);
        types.Attach(static () => [new CodeAlta.Plugins.Abstractions.PluginModelProviderContribution
        {
            ProviderType = "fake-agent", DisplayName = "Fake agent", CreateRuntime = static _ => throw new NotSupportedException("Not created in this test."),
        }]);
        var service = new GlobalConfigService(fixture.Store, fixture.Registry, Path.GetDirectoryName(fixture.ConfigPath)!, Epoch) { PluginProviderTypes = types };
        GlobalConfigProvider Listed() => service.Providers(new(Epoch)).Providers.Single(static provider => provider.Key == "codex-work");
        Assert.IsNull(Listed().ContextSize);

        GlobalConfigSaveResponse Save(int? size) => service.SavePluginProvider(new(Epoch, service.Providers(new(Epoch)).Revision, "codex-work", "codex-work", "fake-agent", true,
            null, null, null, null, false) { ContextSize = size });

        Assert.IsTrue(Save(872_000).Status is "ok" or "apply_failed");
        Assert.AreEqual(872_000, Listed().ContextSize);
        var written = File.ReadAllText(fixture.ConfigPath);
        StringAssert.Contains(written, "context_size = 872000");
        StringAssert.Contains(written, "command = \"codex\"", "the other values of the section are kept");
        Assert.AreEqual(872_000L, fixture.Registry.ListProviders(includeDisabled: true).Single(static provider => provider.ProviderId.Value == "codex-work").DefaultContextSize);

        Assert.IsTrue(Save(null).Status is "ok" or "apply_failed");
        Assert.IsNull(Listed().ContextSize, "Default removes the size");
        Assert.IsFalse(File.ReadAllText(fixture.ConfigPath).Contains("context_size", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task PluginProviderSignIn_PreparesTheProviderAgain_BeforeItSaysCompleted()
    {
        // The state of the provider (its account, its models) is read again once signed in: sessions and the list of
        // models see it at once, without a restart. A failed sign-in changes nothing.
        const string content = """
[providers.codex-work]
type = "fake-agent"
""";
        await using var fixture = new Fixture(content);
        await using var endpoints = new CodeAlta.Agent.Harness.LoopbackHarnessToolEndpoints();
        var types = new CodeAlta.Hosting.PluginModelProviderTypes(endpoints);
        var account = new PromptingAccount();
        types.Attach(() => [new CodeAlta.Plugins.Abstractions.PluginModelProviderContribution
        {
            ProviderType = "fake-agent", DisplayName = "Fake agent", CreateRuntime = static _ => throw new NotSupportedException("Not created in this test."), Account = account,
        }]);
        var refreshed = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var events = new List<GlobalConfigPluginSignInEvent>();
        var service = new GlobalConfigService(fixture.Store, fixture.Registry, Path.GetDirectoryName(fixture.ConfigPath)!, Epoch)
        {
            PluginProviderTypes = types,
            OpenSignInBrowser = static _ => true,
            RefreshProviderAfterSignIn = async (key, _) =>
            {
                // Slower than the page: "completed" must still wait for it.
                await Task.Delay(50);
                refreshed.Enqueue(key);
            },
        };

        await foreach (var item in service.SignInAsync(new(Epoch, "codex-work", "device"), CancellationToken.None))
        {
            if (item.Kind == "completed") Assert.HasCount(1, refreshed, "the provider is prepared before the sign-in says it completed");
            events.Add(item);
        }

        Assert.AreEqual("completed", events[^1].Kind);
        CollectionAssert.AreEqual(new[] { "codex-work" }, refreshed.ToArray());

        account.Failure = "denied";
        events.Clear();
        await foreach (var item in service.SignInAsync(new(Epoch, "codex-work", "device"), CancellationToken.None)) events.Add(item);
        Assert.AreEqual("failed", events[^1].Kind);
        Assert.HasCount(1, refreshed, "a failed sign-in does not prepare the provider again");

        // A preparation that fails does not turn a sign-in that completed into a failure.
        account.Failure = null;
        events.Clear();
        var failing = new GlobalConfigService(fixture.Store, fixture.Registry, Path.GetDirectoryName(fixture.ConfigPath)!, Epoch)
        {
            PluginProviderTypes = types,
            OpenSignInBrowser = static _ => true,
            RefreshProviderAfterSignIn = static (_, _) => Task.FromException(new InvalidOperationException("probe failed")),
        };
        await foreach (var item in failing.SignInAsync(new(Epoch, "codex-work", "device"), CancellationToken.None)) events.Add(item);
        Assert.AreEqual("completed", events[^1].Kind);
    }

    [TestMethod]
    [DoNotParallelize]
    public async Task InstallPluginProvider_ShowsTheMessage_AndLogsTheWholeFailure()
    {
        // The dialog shows the message of the catalog; the log keeps what it does not show: the causes and the stack.
        await using var fixture = new Fixture();
        await using var endpoints = new CodeAlta.Agent.Harness.LoopbackHarnessToolEndpoints();
        var types = new CodeAlta.Hosting.PluginModelProviderTypes(endpoints);
        types.Attach(static () => [new CodeAlta.Plugins.Abstractions.PluginModelProviderContribution
        {
            ProviderType = "fake-agent", DisplayName = "Fake agent", CreateRuntime = static _ => throw new NotSupportedException("Not created in this test."), Catalog = new FailingCatalog(),
        }]);
        var service = new GlobalConfigService(fixture.Store, fixture.Registry, Path.GetDirectoryName(fixture.ConfigPath)!, Epoch) { PluginProviderTypes = types };
        var events = new List<GlobalConfigPluginInstallEvent>();

        var capture = new DesktopLogCapture();
        var config = new XenoAtom.Logging.LogManagerConfig();
        config.RootLogger.MinimumLevel = XenoAtom.Logging.LogLevel.Debug;
        config.RootLogger.Writers.Add(new DesktopCaptureLogWriter(capture));
        XenoAtom.Logging.LogManager.Initialize(config);
        try
        {
            await foreach (var item in service.InstallAsync(new(Epoch, "fake-agent", "fake"), CancellationToken.None)) events.Add(item);
        }
        finally
        {
            // The loggers keep the levels of the last configuration once the log ends: the tests that run after this
            // one, without a log, must find them off.
            config.RootLogger.MinimumLevel = XenoAtom.Logging.LogLevel.None;
            config.ApplyChanges();
            XenoAtom.Logging.LogManager.Shutdown();
        }

        Assert.AreEqual("install_failed", events.Single().Status);
        Assert.AreEqual(FailingCatalog.Message, events.Single().Message);
        var row = capture.Snapshot().Rows.Single(static row => row.Text.Contains("Fake agent could not be added", StringComparison.Ordinal));
        Assert.AreEqual("Error", row.Level);
        StringAssert.Contains(row.Text, "the entry 'fake' of Fake catalog");
        StringAssert.Contains(row.Text, "System.IO.IOException: locked by another process", "the cause the dialog does not show");
        StringAssert.Contains(row.Text, nameof(FailingCatalog), "where it was thrown");
        Assert.IsFalse(File.Exists(fixture.ConfigPath), "a failed install writes no provider");
    }

    // A catalog whose installation fails on the disk.
    private sealed class FailingCatalog : CodeAlta.Plugins.Abstractions.IPluginProviderCatalog
    {
        public const string Message = "Fake could not be unpacked (IOException 0x80070020: locked by another process)";

        public string Title => "Fake catalog";

        public Task<CodeAlta.Plugins.Abstractions.PluginProviderCatalogSnapshot> ListAsync(bool refresh, CancellationToken cancellationToken)
            => throw new NotSupportedException("Not listed in this test.");

        public async Task<CodeAlta.Plugins.Abstractions.PluginProviderInstallResult> InstallAsync(string entryId,
            IProgress<CodeAlta.Plugins.Abstractions.PluginProviderInstallProgress> progress, CancellationToken cancellationToken)
        {
            await Task.Yield();
            throw new InvalidOperationException(Message, new IOException("locked by another process"));
        }

        public Task<string?> GetUpdateAsync(CodeAlta.Plugins.Abstractions.PluginProviderSettings settings, CancellationToken cancellationToken)
            => Task.FromResult<string?>(null);

        public Task<IReadOnlyDictionary<string, object?>> UpdateAsync(CodeAlta.Plugins.Abstractions.PluginProviderSettings settings,
            IProgress<CodeAlta.Plugins.Abstractions.PluginProviderInstallProgress> progress, CancellationToken cancellationToken)
            => throw new NotSupportedException("Not updated in this test.");
    }

    // A sign-in that shows a device code, then ends, or fails.
    private sealed class PromptingAccount : CodeAlta.Plugins.Abstractions.IPluginProviderAccount
    {
        public string? Failure { get; set; }

        public Task<IReadOnlyList<CodeAlta.Plugins.Abstractions.PluginProviderSignInMethod>> GetSignInMethodsAsync(CodeAlta.Plugins.Abstractions.PluginProviderSettings settings, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<CodeAlta.Plugins.Abstractions.PluginProviderSignInMethod>>([new("device", "Sign in with a device code", null)]);

        public Task SignInAsync(CodeAlta.Plugins.Abstractions.PluginProviderSettings settings, string methodId, CancellationToken cancellationToken)
            => throw new NotSupportedException("The window calls the sign-in that shows a prompt.");

        public async Task SignInAsync(CodeAlta.Plugins.Abstractions.PluginProviderSettings settings, string methodId,
            Func<CodeAlta.Plugins.Abstractions.PluginProviderSignInPrompt, CancellationToken, ValueTask> onPrompt, CancellationToken cancellationToken)
        {
            await onPrompt(new(new Uri("https://auth.example.com/device"), "ABCD-1234", DateTimeOffset.UtcNow.AddMinutes(15)), cancellationToken);
            if (Failure is { } failure) throw new InvalidOperationException(failure);
        }
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
    public async Task FreeDisplayName_TellsAnAddedAgentApartFromAProviderOfTheSameName()
    {
        await using var fixture = new Fixture();
        fixture.Registry.RegisterOrReplace(new ModelProviderDescriptor(new ModelProviderId("copilot"), "Copilot", "copilot"),
            () => throw new AssertFailedException("Naming a provider must not create a runtime."));

        Assert.AreEqual("Copilot (ACP)", fixture.Service.FreeDisplayName("Copilot", "ACP"));
        Assert.AreEqual("copilot (ACP)", fixture.Service.FreeDisplayName("copilot", "ACP"), "Names are compared without case.");
        Assert.AreEqual("Cursor", fixture.Service.FreeDisplayName("Cursor", "ACP"));
        Assert.AreEqual("Copilot", fixture.Service.FreeDisplayName("Copilot", null), "A type without a tag keeps the name.");
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
        Assert.AreEqual("local", listed.StartingProvider, "The configured default is enabled: a new session starts with it.");
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
    public async Task SessionTitles_AreReadFromAndSavedToTheConfigurationFile()
    {
        await using var fixture = new Fixture("[providers.openai]\nenabled = true\ntype = \"openai-responses\"\napi_key = \"sk-kept\"\n");
        Assert.AreEqual(new GlobalConfigSessionTitlesResponse("ok", "model"), fixture.Service.SessionTitles(new(Epoch)));

        Assert.AreEqual(new GlobalConfigSessionTitlesResponse("ok", "last_message"), fixture.Service.SaveSessionTitles(new(Epoch, "last_message")));
        Assert.AreEqual(new GlobalConfigSessionTitlesResponse("ok", "last_message"), fixture.Service.SessionTitles(new(Epoch)));
        var text = File.ReadAllText(fixture.ConfigPath);
        StringAssert.Contains(text, "[sessions]");
        StringAssert.Contains(text, "title = \"last_message\"");
        StringAssert.Contains(text, "sk-kept", "The other settings stay.");

        // The default is the absence of the setting.
        Assert.AreEqual("ok", fixture.Service.SaveSessionTitles(new(Epoch, "model")).Status);
        Assert.IsFalse(File.ReadAllText(fixture.ConfigPath).Contains("[sessions]", StringComparison.Ordinal));

        foreach (var mode in new[] { "summary", "", null })
            Assert.AreEqual(new GlobalConfigSessionTitlesResponse("invalid", null), fixture.Service.SaveSessionTitles(new(Epoch, mode)));
        Assert.AreEqual("stale_epoch", fixture.Service.SessionTitles(new("another")).Status);
        Assert.AreEqual("stale_epoch", fixture.Service.SaveSessionTitles(new("another", "first_prompt")).Status);
        Assert.AreEqual("unavailable", new GlobalConfigService().SessionTitles(new(Epoch)).Status);
        Assert.AreEqual("unavailable", new GlobalConfigService().SaveSessionTitles(new(Epoch, "first_prompt")).Status);
        Assert.AreEqual("model", fixture.Service.SessionTitles(new(Epoch)).Mode, "A refused save changes nothing.");
    }

    [TestMethod]
    public async Task ModelTiers_AreReadAndSavedWithTheModelEachGives()
    {
        await using var fixture = new Fixture("[providers.openai]\nenabled = true\ntype = \"openai-responses\"\napi_key = \"sk-kept\"\n");
        var empty = ReadTiers(fixture.Service, new(Epoch));
        Assert.AreEqual("ok", empty.Status);
        CollectionAssert.AreEqual(new[] { "complex", "standard", "quick" }, empty.Tiers.Select(static list => list.Tier).ToArray());
        Assert.IsTrue(empty.Tiers.All(static list => list.Targets.Count == 0 && list.Choice is null));
        Assert.AreEqual(new ModelFallbackSetting(false, true, true, false, 5), empty.Fallback);

        var service = new GlobalConfigService(fixture.Store, fixture.Registry, Path.GetDirectoryName(fixture.ConfigPath)!, Epoch)
        {
            ProviderStates = () =>
            [
                new ModelProviderStateSnapshot
                {
                    Descriptor = new ModelProviderDescriptor(new ModelProviderId("codex"), "Codex"),
                    Availability = ModelProviderAvailability.Ready,
                    Models = [new AgentModelInfo("gpt-6.1", "GPT-6.1", SupportedReasoningEfforts: [AgentReasoningEffort.High, AgentReasoningEffort.XHigh])],
                },
            ],
        };
        var saved = SaveTiers(service, new(Epoch,
        [
            new("complex", [new("claude-code", "opus", ["High", "XHigh"]), new("codex", "gpt-6.1", ["XHigh"])]),
            new("quick", [new("claude-code", "haiku", [])]),
        ], new(true, true, false, true, 3)));
        Assert.AreEqual("ok", saved.Status, saved.Message);
        Assert.AreEqual(new ModelFallbackSetting(true, true, false, true, 3), saved.Fallback, "The resume after a limit is saved with the fallback.");
        // Claude Code is not ready: the complex tier gives the next target.
        Assert.AreEqual(new ModelTierChoiceItem("codex", "gpt-6.1", "XHigh", "Codex", "GPT-6.1"), saved.Tiers[0].Choice);
        Assert.IsNull(saved.Tiers[2].Choice);
        var text = File.ReadAllText(fixture.ConfigPath);
        StringAssert.Contains(text, "\"claude-code:opus@high,xhigh\"");
        StringAssert.Contains(text, "on = [\"quota\"]");
        StringAssert.Contains(text, "resume = true");
        StringAssert.Contains(text, "max_resumes = 3");
        StringAssert.Contains(text, "sk-kept", "The other settings stay.");
        CollectionAssert.AreEqual(new[] { "High", "XHigh" }, ReadTiers(service, new(Epoch)).Tiers[0].Targets[0].Reasoning.ToArray());

        // What is not a target is refused with what is wrong, and nothing is written.
        var refused = SaveTiers(service, new(Epoch, [new("quick", [new("claude-code", "haiku", ["Huge"])])], new(false, true, true, false, 5)));
        Assert.AreEqual(("invalid", "'Huge' is not a reasoning effort."), (refused.Status, refused.Message));
        Assert.AreEqual("invalid", SaveTiers(service, new(Epoch, [new("quick", [new("", "haiku", [])])], new(false, true, true, false, 5))).Status);
        Assert.AreEqual("invalid", SaveTiers(service, new(Epoch, [new("fast", [])], new(false, true, true, false, 5))).Status);
        StringAssert.Contains(SaveTiers(service, new(Epoch, [new("quick", [new("a", "m", []), new("A", "M", [])])], new(false, true, true, false, 5))).Message, "listed twice");
        Assert.AreEqual(text, File.ReadAllText(fixture.ConfigPath));

        // Tiers the file writes wrong are said, and shown as none.
        File.WriteAllText(fixture.ConfigPath, "[models.tiers]\nquick = [\"haiku\"]\n");
        var invalid = ReadTiers(service, new(Epoch));
        Assert.AreEqual("invalid", invalid.Status);
        StringAssert.Contains(invalid.Message, "models.tiers.quick[1]");
        Assert.IsTrue(invalid.Tiers.All(static list => list.Targets.Count == 0));

        Assert.AreEqual("stale_epoch", ReadTiers(service, new("another")).Status);
        Assert.AreEqual("unavailable", ReadTiers(new GlobalConfigService(), new(Epoch)).Status);
    }

    [TestMethod]
    public async Task Adaptive_IsSetUpAndTakenOut_ReadWithTheTiers_AndKeptWhenTheTiersAreSaved()
    {
        await using var fixture = new Fixture("[providers.openai]\nenabled = true\ntype = \"openai-responses\"\napi_key = \"sk-kept\"\n");
        var service = fixture.Service;
        var none = ReadTiers(service, new(Epoch));
        Assert.IsNull(none.Adaptive);
        Assert.IsFalse(none.AdaptiveReady);

        // Set up before its tiers have models: it is saved, and cannot be chosen yet.
        var set = await service.SaveAdaptiveAsync(new(Epoch, new AdaptiveSetting("quick", "complex", true)), CancellationToken.None);
        Assert.AreEqual("ok", set.Status, set.Message);
        Assert.AreEqual(new AdaptiveSetting("quick", "complex", true), set.Adaptive);
        Assert.IsFalse(set.AdaptiveReady);
        StringAssert.Contains(File.ReadAllText(fixture.ConfigPath), "[models.adaptive]");

        var tiers = SaveTiers(service, new(Epoch,
            [new("complex", [new("claude-code", "opus", ["XHigh"])]), new("quick", [new("claude-code", "haiku", ["High"])])],
            new(false, true, true, false, 5)));
        Assert.AreEqual(new AdaptiveSetting("quick", "complex", true), tiers.Adaptive, "Saving the tiers keeps Adaptive.");
        Assert.IsTrue(tiers.AdaptiveReady);

        var changed = await service.SaveAdaptiveAsync(new(Epoch, new AdaptiveSetting("standard", "complex", false)), CancellationToken.None);
        Assert.AreEqual(new AdaptiveSetting("standard", "complex", false), changed.Adaptive);
        Assert.IsFalse(changed.AdaptiveReady, "The standard tier has no model.");

        Assert.AreEqual("invalid", (await service.SaveAdaptiveAsync(new(Epoch, new AdaptiveSetting("cheap", "complex", true)), CancellationToken.None)).Status);
        var removed = await service.SaveAdaptiveAsync(new(Epoch, null), CancellationToken.None);
        Assert.IsNull(removed.Adaptive);
        Assert.DoesNotContain("adaptive", File.ReadAllText(fixture.ConfigPath));
        StringAssert.Contains(File.ReadAllText(fixture.ConfigPath), "claude-code:opus@xhigh", "Taking Adaptive out keeps the tiers.");
        Assert.AreEqual("stale_epoch", (await service.SaveAdaptiveAsync(new("another", null), CancellationToken.None)).Status);
    }

    [TestMethod]
    public async Task ModelTiersOfASpace_ReplaceTheGlobalOnes_AndTakeOnlyTheProvidersOfTheSpace()
    {
        await using var fixture = new Fixture("""
            [providers.codex]
            type = "codex"
            spaces = ["personal"]

            [models.tiers]
            standard = ["codex:gpt-6.1@high", "claude-code:opus@medium"]
            """);
        var spaces = new SpaceCatalog(new ProjectCatalog(new CatalogOptions { GlobalRoot = Path.GetDirectoryName(fixture.ConfigPath)! }));
        Assert.IsTrue((await spaces.CreateAsync("Business")).Succeeded);
        Assert.IsTrue((await spaces.CreateAsync("Personal")).Succeeded);
        ModelProviderStateSnapshot Ready(string key, params string[] models) => new()
        {
            Descriptor = new ModelProviderDescriptor(new ModelProviderId(key), key),
            Availability = ModelProviderAvailability.Ready,
            Models = [.. models.Select(static model => new AgentModelInfo(model))],
        };
        var service = new GlobalConfigService(fixture.Store, fixture.Registry, Path.GetDirectoryName(fixture.ConfigPath)!, Epoch)
        {
            ProviderStates = () => [Ready("codex", "gpt-6.1"), Ready("claude-code", "opus", "haiku")],
            Spaces = spaces,
            SessionSpace = (sessionId, _) => Task.FromResult<SessionSpaceRecord?>(sessionId == "s1" ? new SessionSpaceRecord("business", null) : null),
        };

        // Codex is a provider of Personal only: Business takes the next target of the global tier.
        Assert.AreEqual("codex", ReadTiers(service, new(Epoch)).Tiers[1].Choice?.ProviderKey);
        var business = ReadTiers(service, new(Epoch) { SpaceId = "business" });
        Assert.AreEqual(("ok", "global", "business"), (business.Status, business.Source, business.SpaceId));
        Assert.AreEqual("claude-code", business.Tiers[1].Choice?.ProviderKey);
        Assert.AreEqual("claude-code", ReadTiers(service, new(Epoch) { SessionId = "s1" }).Tiers[1].Choice?.ProviderKey, "A session is read as its space.");
        Assert.AreEqual("codex", ReadTiers(service, new(Epoch) { SpaceId = "personal" }).Tiers[1].Choice?.ProviderKey);

        // Tiers of its own, in the file of the space; the settings keep theirs.
        var global = File.ReadAllText(fixture.ConfigPath);
        var own = SaveTiers(service, new(Epoch, [new("quick", [new("claude-code", "haiku", ["Low"])])], new(false, true, true, false, 5)) { SpaceId = "business" });
        Assert.AreEqual(("ok", "space"), (own.Status, own.Source), own.Message);
        Assert.AreEqual(0, own.Tiers[1].Targets.Count, "The tiers of a space replace the global ones, also where it sets none.");
        Assert.AreEqual("haiku", own.Tiers[2].Choice?.ModelId);
        Assert.AreEqual(global, File.ReadAllText(fixture.ConfigPath));
        StringAssert.Contains(File.ReadAllText(Path.Combine(Path.GetDirectoryName(fixture.ConfigPath)!, "spaces", "business.md")), "claude-code:haiku@low");
        Assert.AreEqual("space", ReadTiers(service, new(Epoch) { SessionId = "s1" }).Source);

        // Back to the global tiers.
        var back = SaveTiers(service, new(Epoch, [], new(false, true, true, false, 5)) { SpaceId = "business", UseGlobal = true });
        Assert.AreEqual(("ok", "global"), (back.Status, back.Source));
        Assert.IsFalse(File.ReadAllText(Path.Combine(Path.GetDirectoryName(fixture.ConfigPath)!, "spaces", "business.md")).Contains("haiku", StringComparison.Ordinal));

        Assert.AreEqual("not_found", ReadTiers(service, new(Epoch) { SpaceId = "nowhere" }).Status);
        Assert.AreEqual("not_found", ReadTiers(service, new(Epoch) { SessionId = "unknown" }).Status);
        Assert.AreEqual("not_found", SaveTiers(service, new(Epoch, [new("quick", [])], new(false, true, true, false, 5)) { SpaceId = "nowhere" }).Status);
    }

    [TestMethod]
    public async Task FallbackOfASpace_IsSavedWithItsTiers_AndTheGlobalOneStaysUntilItIsChanged()
    {
        await using var fixture = new Fixture("""
            [providers.work-claude]
            type = "claude-code"
            spaces = ["business"]

            [models.tiers]
            standard = ["claude-code:opus@medium"]

            [models.fallback]
            enabled = true
            """);
        var root = Path.GetDirectoryName(fixture.ConfigPath)!;
        var spaces = new SpaceCatalog(new ProjectCatalog(new CatalogOptions { GlobalRoot = root }));
        Assert.IsTrue((await spaces.CreateAsync("Business")).Succeeded);
        var file = Path.Combine(root, "spaces", "business.md");
        var service = new GlobalConfigService(fixture.Store, fixture.Registry, root, Epoch) { Spaces = spaces };
        var global = File.ReadAllText(fixture.ConfigPath);
        var usual = new ModelFallbackSetting(true, true, true, false, 5);
        Assert.AreEqual(usual, ReadTiers(service, new(Epoch) { SpaceId = "business" }).Fallback, "A space with the usual settings has the global fallback.");

        // Customizing with the global fallback writes tiers only: the space follows the global fallback.
        ModelTierList[] tiers = [new("standard", [new("work-claude", "opus", ["High"]), new("claude-code", "opus", ["High"])])];
        var own = SaveTiers(service, new(Epoch, tiers, usual) { SpaceId = "business" });
        Assert.AreEqual(("ok", "space"), (own.Status, own.Source), own.Message);
        Assert.AreEqual(usual, own.Fallback);
        Assert.IsFalse(File.ReadAllText(file).Contains("fallback", StringComparison.Ordinal));
        StringAssert.Contains(File.ReadAllText(file), "work-claude:opus@high");

        // Changed there, the space has its own fallback, kept once it is the same as the global one again.
        var quotaOnly = new ModelFallbackSetting(true, true, false, false, 5);
        Assert.AreEqual(quotaOnly, SaveTiers(service, new(Epoch, tiers, quotaOnly) { SpaceId = "business" }).Fallback);
        StringAssert.Contains(File.ReadAllText(file), "fallback:");
        Assert.AreEqual(quotaOnly, ReadTiers(service, new(Epoch) { SpaceId = "business" }).Fallback);
        Assert.AreEqual(usual, ReadTiers(service, new(Epoch)).Fallback, "The global fallback is the same.");
        Assert.AreEqual(global, File.ReadAllText(fixture.ConfigPath));
        Assert.AreEqual(usual, SaveTiers(service, new(Epoch, tiers, usual) { SpaceId = "business" }).Fallback);
        StringAssert.Contains(File.ReadAllText(file), "fallback:");
        Assert.AreEqual(usual with { Enabled = false }, SaveTiers(service, new(Epoch, tiers, usual with { Enabled = false }) { SpaceId = "business" }).Fallback);
        Assert.AreEqual(usual, ReadTiers(service, new(Epoch)).Fallback);

        // The usual settings again: neither tiers nor fallback stay in the file.
        var back = SaveTiers(service, new(Epoch, [], usual) { SpaceId = "business", UseGlobal = true });
        Assert.AreEqual(("ok", "global"), (back.Status, back.Source));
        Assert.AreEqual(usual, back.Fallback);
        Assert.IsFalse(File.ReadAllText(file).Contains("new_sessions", StringComparison.Ordinal));
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
    public async Task BuiltInProviders_ThatTheConfigurationLacks_AreOfferedAndAddedAsCodeAltaShipsThem()
    {
        await using var fixture = new Fixture("[providers.openai]\nenabled = true\ntype = \"openai-responses\"\napi_key = \"sk-kept\"\n\n[providers.Anthropic]\nenabled = false\ntype = \"anthropic\"\n");
        var listed = fixture.Service.Providers(new(Epoch));

        var offered = listed.BuiltIn.ToDictionary(static entry => entry.Key);
        Assert.IsFalse(offered.ContainsKey("openai") || offered.ContainsKey("anthropic"), "What the configuration has, whatever the case of its key, is not offered again.");
        Assert.AreEqual(new GlobalConfigBuiltInProvider("claude-code", "claude-code", "Claude Code"), offered["claude-code"]);
        Assert.AreEqual(("openai-chat", "DeepSeek"), (offered["deepseek"].Type, offered["deepseek"].Name));
        CollectionAssert.AreEqual(listed.BuiltIn.Select(static entry => entry.Name).Order(StringComparer.OrdinalIgnoreCase).ToArray(), listed.BuiltIn.Select(static entry => entry.Name).ToArray());
        Assert.AreEqual(0, fixture.Service.Providers(new("another")).BuiltIn.Count);

        Assert.AreEqual("conflict", fixture.Service.AddBuiltInProvider(new(Epoch, "stale", "deepseek")).Status);
        Assert.AreEqual("invalid", fixture.Service.AddBuiltInProvider(new(Epoch, listed.Revision, "no-such-provider")).Status);
        Assert.AreEqual("invalid", fixture.Service.AddBuiltInProvider(new(Epoch, listed.Revision, "OpenAI")).Status, "A provider that is there is not replaced.");
        Assert.AreEqual("invalid", fixture.Service.AddBuiltInProvider(new(Epoch, listed.Revision, " ")).Status);

        var added = fixture.Service.AddBuiltInProvider(new(Epoch, listed.Revision, "deepseek"));
        Assert.AreEqual("ok", added.Status, added.Message);
        var text = File.ReadAllText(fixture.ConfigPath);
        StringAssert.Contains(text, "[providers.deepseek]");
        StringAssert.Contains(text, "api_key = \"sk-kept\"", "The other providers stay as they were.");
        var after = fixture.Service.Providers(new(Epoch));
        var deepseek = after.Providers.Single(static provider => provider.Key == "deepseek");
        var shipped = CodeAltaConfigStore.LoadDefaultProviderDefinitions().Single(static provider => provider.ProviderKey == "deepseek");
        Assert.AreEqual(("openai-chat", false, "DeepSeek", shipped.ApiUrl, shipped.ApiKeyEnv), (deepseek.Type, deepseek.Enabled, deepseek.EffectiveName, deepseek.EffectiveApiUrl, deepseek.ApiKeyEnv),
            "It is added with its endpoint and the variable of its key, disabled until the user gives it a credential.");
        Assert.IsFalse(after.BuiltIn.Any(static entry => entry.Key == "deepseek"));
    }

    [TestMethod]
    public async Task ProviderOfANewerVersion_IsReportedLeftOut_AndKeptWhenProvidersAreSaved()
    {
        // 1.2.0 with the file of 1.3.0: the type it did not know made it refuse the whole configuration.
        const string newer = ProvidersConfig + """

            [providers.future]
            type = "future-provider"
            launch_command = "future --serve"
            """;
        await using var fixture = new Fixture(newer);

        var validation = fixture.Service.Validate(new(newer));
        Assert.IsTrue(validation.Valid);
        StringAssert.Contains(validation.Warning, "providers.future");
        Assert.IsNull(fixture.Service.Validate(new(ProvidersConfig)).Warning);

        var listed = fixture.Service.Providers(new(Epoch));
        Assert.AreEqual("ok", listed.Status);
        CollectionAssert.AreEqual(new[] { new GlobalConfigUnsupportedProvider("future", "future-provider") }, listed.Unsupported.ToArray());
        CollectionAssert.AreEquivalent(new[] { "local", "spare" }, listed.Providers.Select(static provider => provider.Key).ToArray());

        // The providers this version knows are registered; the other is not, and nothing fails for it.
        var edit = new GlobalConfigProviderEdit("local", "openai-chat", true, "Local renamed", "model-b", "high", "http://127.0.0.1:9999/v1", null, null, false);
        var saved = fixture.Service.SaveProvider(new(Epoch, listed.Revision, "local", edit, false, true));
        Assert.AreEqual("ok", saved.Status, saved.Message);
        Assert.IsFalse(fixture.Registry.ListProviders(includeDisabled: true).Any(static provider => provider.ProviderId.Value == "future"));
        var text = File.ReadAllText(fixture.ConfigPath);
        StringAssert.Contains(text, "[providers.future]");
        StringAssert.Contains(text, "type = \"future-provider\"");
        StringAssert.Contains(text, "launch_command = \"future --serve\"");
        StringAssert.Contains(text, "Local renamed");

        // Its key is taken: a provider added under it would be written into its section.
        var after = fixture.Service.Providers(new(Epoch));
        var taken = fixture.Service.SaveProvider(new(Epoch, after.Revision, null, edit with { Key = "future" }, false, false));
        Assert.AreEqual("invalid", taken.Status);
        StringAssert.Contains(taken.Message, "providers.future");
        Assert.AreEqual(text, File.ReadAllText(fixture.ConfigPath));
    }

    [TestMethod]
    public async Task SaveProvider_WritesTheIconAndItsColor_AndLeavesThemOutWhenTheyFollowTheProvider()
    {
        await using var fixture = new Fixture(ProvidersConfig);
        var listed = fixture.Service.Providers(new(Epoch));
        Assert.IsTrue(listed.Providers.All(static provider => provider.Icon is null && provider.Color is null));
        var edit = new GlobalConfigProviderEdit("local", "openai-chat", true, "Local", "model-a", null, "http://127.0.0.1:9999/v1", null, null, false);

        // What is not the name of an icon or a color is refused, and nothing is written.
        foreach (var refused in new[] { edit with { Icon = "two words" }, edit with { Icon = new string('a', 65) }, edit with { Color = "red" }, edit with { Color = "#12345" }, edit with { Color = "#gggggg" } })
            Assert.AreEqual("invalid", fixture.Service.SaveProvider(new(Epoch, listed.Revision, "local", refused, false, false)).Status, refused.Icon + refused.Color);
        Assert.AreEqual(ProvidersConfig, File.ReadAllText(fixture.ConfigPath), "Refused edits must not write.");

        var saved = fixture.Service.SaveProvider(new(Epoch, listed.Revision, "local", edit with { Icon = " Flask-Conical ", Color = " #22B8CD " }, false, false));
        Assert.AreEqual("ok", saved.Status, saved.Message);
        var text = File.ReadAllText(fixture.ConfigPath);
        StringAssert.Contains(text, "icon = \"flask-conical\"");
        StringAssert.Contains(text, "color = \"#22B8CD\"");
        StringAssert.Contains(text, "secret-value", "The other settings of the provider are kept.");
        var after = fixture.Service.Providers(new(Epoch));
        var local = after.Providers.Single(static provider => provider.Key == "local");
        Assert.AreEqual(("flask-conical", "#22B8CD"), (local.Icon, local.Color));
        Assert.IsNull(after.Providers.Single(static provider => provider.Key == "spare").Icon);

        // An edit of something else keeps them only when it sends them: the form always does. Blank takes them out.
        var cleared = fixture.Service.SaveProvider(new(Epoch, after.Revision, "local", edit with { Icon = " ", Color = "" }, false, false));
        Assert.AreEqual("ok", cleared.Status, cleared.Message);
        text = File.ReadAllText(fixture.ConfigPath);
        Assert.IsFalse(text.Contains("icon =", StringComparison.Ordinal) || text.Contains("color =", StringComparison.Ordinal), text);
        // The short form of a color is one too.
        Assert.AreEqual("ok", fixture.Service.SaveProvider(new(Epoch, cleared.Revision, "local", edit with { Color = "#0af" }, false, false)).Status);
    }

    [TestMethod]
    public async Task CodeMode_IsOnByDefault_OffIsWritten_AndOnRemovesItsTable()
    {
        await using var fixture = new Fixture("[providers.openai]\nenabled = true\ntype = \"openai-responses\"\napi_key = \"sk-kept\"\n");
        var read = fixture.Service.CodeModeSettings(new(Epoch));
        Assert.AreEqual("ok", read.Status);
        Assert.AreEqual("on", read.Mode, "no setting is on");
        CollectionAssert.AreEqual(new[] { "off", "on", "only" }, read.Modes.ToArray());

        Assert.AreEqual("off", fixture.Service.SaveCodeModeSettings(new(Epoch, "off")).Mode);
        Assert.AreEqual("off", fixture.Service.CodeModeSettings(new(Epoch)).Mode);
        var text = File.ReadAllText(fixture.ConfigPath);
        StringAssert.Contains(text, "mode = \"off\"");
        StringAssert.Contains(text, "sk-kept", "The other settings stay.");
        Assert.AreEqual("only", fixture.Service.SaveCodeModeSettings(new(Epoch, "only")).Mode);

        Assert.AreEqual("on", fixture.Service.SaveCodeModeSettings(new(Epoch, "on")).Mode);
        Assert.IsFalse(File.ReadAllText(fixture.ConfigPath).Contains("[code_mode]", StringComparison.Ordinal), "on is the absence of the setting");
        Assert.AreEqual("invalid", fixture.Service.SaveCodeModeSettings(new(Epoch, null)).Status);
        Assert.AreEqual("invalid", fixture.Service.SaveCodeModeSettings(new(Epoch, "sometimes")).Status);
        Assert.AreEqual("stale_epoch", fixture.Service.CodeModeSettings(new("another")).Status);
    }

    [TestMethod]
    public async Task CodeMode_OfAProviderAndItsModels_IsSaved_AndListed()
    {
        await using var fixture = new Fixture("[providers.copilot]\nenabled = true\ntype = \"openai-responses\"\ndisplay_name = \"Copilot\"\napi_key = \"sk-kept\"\n");
        var first = fixture.Service.CodeModeProvider(new(Epoch, "copilot"));
        Assert.AreEqual(("ok", null, "on", 0), (first.Status, first.Provider, first.AllSessions, first.Models.Count), "a provider without a mode follows every session: on");

        Assert.AreEqual("ok", fixture.Service.SaveCodeModeScope(new(Epoch, "copilot", null, "only")).Status);
        Assert.AreEqual("ok", fixture.Service.SaveCodeModeScope(new(Epoch, "copilot", "claude-haiku-4.5", "off")).Status);
        Assert.AreEqual("off", fixture.Service.SaveCodeModeSettings(new(Epoch, "off")).Mode);
        var read = fixture.Service.CodeModeProvider(new(Epoch, "copilot"));
        Assert.AreEqual(("only", "off"), (read.Provider, read.AllSessions));
        CollectionAssert.AreEqual(new[] { "claude-haiku-4.5:off" }, read.Models.Select(static model => $"{model.ModelId}:{model.Mode}").ToArray());
        CollectionAssert.AreEqual(new[] { "copilot:Copilot:-:only", "copilot:Copilot:claude-haiku-4.5:off" },
            fixture.Service.CodeModeSettings(new(Epoch)).Scopes.Select(static scope => $"{scope.ProviderKey}:{scope.ProviderName}:{scope.ModelId ?? "-"}:{scope.Mode}").ToArray());
        StringAssert.Contains(File.ReadAllText(fixture.ConfigPath), "sk-kept", "nothing else of the provider changes");

        Assert.AreEqual("ok", fixture.Service.SaveCodeModeScope(new(Epoch, "copilot", "claude-haiku-4.5", null)).Status);
        Assert.AreEqual("ok", fixture.Service.SaveCodeModeScope(new(Epoch, "copilot", null, null)).Status);
        Assert.AreEqual(0, fixture.Service.CodeModeSettings(new(Epoch)).Scopes.Count);
        Assert.AreEqual("not_found", fixture.Service.SaveCodeModeScope(new(Epoch, "missing", null, "on")).Status);
        Assert.AreEqual("invalid", fixture.Service.SaveCodeModeScope(new(Epoch, "copilot", null, "sometimes")).Status);
        Assert.AreEqual("not_found", fixture.Service.CodeModeProvider(new(Epoch, "missing")).Status);
    }

    [TestMethod]
    public async Task PromptSuggestions_AreOnByDefault_AndSavedBesideTheTitleOfTheSessions()
    {
        await using var fixture = new Fixture("[providers.openai]\nenabled = true\ntype = \"openai-responses\"\napi_key = \"sk-kept\"\n");
        Assert.AreEqual(new GlobalConfigPromptSuggestionSettingsResponse("ok", true), fixture.Service.PromptSuggestionSettings(new(Epoch)));

        Assert.AreEqual("ok", fixture.Service.SaveSessionTitles(new(Epoch, "last_message")).Status);
        Assert.AreEqual(new GlobalConfigPromptSuggestionSettingsResponse("ok", false), fixture.Service.SavePromptSuggestionSettings(new(Epoch, false)));
        Assert.AreEqual(new GlobalConfigPromptSuggestionSettingsResponse("ok", false), fixture.Service.PromptSuggestionSettings(new(Epoch)));
        var text = File.ReadAllText(fixture.ConfigPath);
        StringAssert.Contains(text, "prompt_suggestions = false");
        StringAssert.Contains(text, "title = \"last_message\"", "The two settings of the sessions share their table.");
        StringAssert.Contains(text, "sk-kept", "The other settings stay.");

        // Each keeps the other: the title back to its default leaves the suggestions off, and the reverse.
        Assert.AreEqual("ok", fixture.Service.SaveSessionTitles(new(Epoch, "model")).Status);
        Assert.IsFalse(fixture.Service.PromptSuggestionSettings(new(Epoch)).Enabled);
        Assert.AreEqual("ok", fixture.Service.SaveSessionTitles(new(Epoch, "first_prompt")).Status);
        Assert.AreEqual(new GlobalConfigPromptSuggestionSettingsResponse("ok", true), fixture.Service.SavePromptSuggestionSettings(new(Epoch, true)));
        Assert.AreEqual("first_prompt", fixture.Service.SessionTitles(new(Epoch)).Mode);

        // The default is the absence of the setting.
        Assert.AreEqual("ok", fixture.Service.SaveSessionTitles(new(Epoch, "model")).Status);
        Assert.IsFalse(File.ReadAllText(fixture.ConfigPath).Contains("[sessions]", StringComparison.Ordinal));

        Assert.AreEqual("stale_epoch", fixture.Service.PromptSuggestionSettings(new("another")).Status);
        Assert.AreEqual("stale_epoch", fixture.Service.SavePromptSuggestionSettings(new("another", false)).Status);
        Assert.AreEqual("unavailable", new GlobalConfigService().PromptSuggestionSettings(new(Epoch)).Status);
        Assert.AreEqual("unavailable", new GlobalConfigService().SavePromptSuggestionSettings(new(Epoch, false)).Status);
        Assert.IsTrue(fixture.Service.PromptSuggestionSettings(new(Epoch)).Enabled, "A refused save changes nothing.");
    }

    [TestMethod]
    public async Task SaveProvider_WritesTheChoiceForAnthropicApiKeyOfClaudeCode_AndOfNoOtherType()
    {
        await using var fixture = new Fixture(ProvidersConfig);
        var listed = fixture.Service.Providers(new(Epoch));
        var edit = new GlobalConfigProviderEdit("claude", "claude-code", true, null, null, null, null, null, null, true);

        Assert.AreEqual("invalid", fixture.Service.SaveProvider(new(Epoch, listed.Revision, null, edit with { AnthropicApiKey = "ask" }, false, false)).Status);
        Assert.AreEqual(ProvidersConfig, File.ReadAllText(fixture.ConfigPath), "A refused edit must not write.");

        var saved = fixture.Service.SaveProvider(new(Epoch, listed.Revision, null, edit with { AnthropicApiKey = " ignore " }, false, false));
        Assert.AreEqual("ok", saved.Status, saved.Message);
        StringAssert.Contains(File.ReadAllText(fixture.ConfigPath), "anthropic_api_key = \"ignore\"");
        var after = fixture.Service.Providers(new(Epoch));
        Assert.AreEqual("ignore", after.Providers.Single(static provider => provider.Key == "claude").AnthropicApiKey);
        Assert.IsNull(after.Providers.Single(static provider => provider.Key == "local").AnthropicApiKey);

        // Blank follows the answer Claude Code saved: the setting is taken out.
        var blank = fixture.Service.SaveProvider(new(Epoch, after.Revision, "claude", edit with { AnthropicApiKey = " " }, false, false));
        Assert.AreEqual("ok", blank.Status, blank.Message);
        Assert.IsFalse(File.ReadAllText(fixture.ConfigPath).Contains("anthropic_api_key", StringComparison.Ordinal));

        // A type that does not run Claude Code does not keep it.
        var use = fixture.Service.SaveProvider(new(Epoch, blank.Revision, "claude", edit with { AnthropicApiKey = "use" }, false, false));
        Assert.AreEqual("ok", use.Status, use.Message);
        var retyped = fixture.Service.SaveProvider(new(Epoch, use.Revision, "claude",
            edit with { Type = "anthropic", ApiKeyEnv = "CODEALTA_ANTHROPIC_API_KEY", ClearApiKey = false, AnthropicApiKey = "use" }, false, false));
        Assert.AreEqual("ok", retyped.Status, retyped.Message);
        Assert.IsFalse(File.ReadAllText(fixture.ConfigPath).Contains("anthropic_api_key", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task SaveProvider_WritesTheConfigFolderOfClaudeCode_AndOfNoOtherType()
    {
        await using var fixture = new Fixture(ProvidersConfig);
        var listed = fixture.Service.Providers(new(Epoch));
        var edit = new GlobalConfigProviderEdit("claude", "claude-code", true, null, null, null, null, null, null, true);

        var relative = fixture.Service.SaveProvider(new(Epoch, listed.Revision, null, edit with { ConfigDir = "claude-business" }, false, false));
        Assert.AreEqual("invalid", relative.Status, "A relative folder would depend on the folder CodeAlta was started in.");
        StringAssert.Contains(relative.Message, "config_dir");
        Assert.AreEqual(ProvidersConfig, File.ReadAllText(fixture.ConfigPath), "A refused edit must not write.");

        var saved = fixture.Service.SaveProvider(new(Epoch, listed.Revision, null, edit with { ConfigDir = " ~/.claude-business " }, false, false));
        Assert.AreEqual("ok", saved.Status, saved.Message);
        StringAssert.Contains(File.ReadAllText(fixture.ConfigPath), "config_dir = \"~/.claude-business\"");
        var after = fixture.Service.Providers(new(Epoch));
        Assert.AreEqual("~/.claude-business", after.Providers.Single(static provider => provider.Key == "claude").ConfigDir);
        Assert.IsNull(after.Providers.Single(static provider => provider.Key == "local").ConfigDir);

        // A type that does not run Claude Code does not keep it.
        var retyped = fixture.Service.SaveProvider(new(Epoch, after.Revision, "claude",
            edit with { Type = "anthropic", ApiKeyEnv = "CODEALTA_ANTHROPIC_API_KEY", ClearApiKey = false, ConfigDir = "~/.claude-business" }, false, false));
        Assert.AreEqual("ok", retyped.Status, retyped.Message);
        Assert.IsFalse(File.ReadAllText(fixture.ConfigPath).Contains("config_dir", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task SaveProvider_WritesTheEnvironmentOfClaudeCode_AndADuplicateLeavesItBehind()
    {
        await using var fixture = new Fixture(ProvidersConfig);
        var listed = fixture.Service.Providers(new(Epoch));
        var edit = new GlobalConfigProviderEdit("claude", "claude-code", true, null, null, null, null, null, null, true);
        GlobalConfigPluginMapEntry[] token = [new(" CLAUDE_CODE_OAUTH_TOKEN ", "sk-ant-oat01-test"), new("", "a line without a name is left out")];

        foreach (var invalid in new[] { new GlobalConfigPluginMapEntry[] { new("NOT A NAME", "x") }, [new("1ST", "x")], [new("A", "1"), new("A", "2")] })
        {
            var refused = fixture.Service.SaveProvider(new(Epoch, listed.Revision, null, edit with { Environment = invalid }, false, false));
            Assert.AreEqual("invalid", refused.Status, invalid[0].Name);
        }

        Assert.AreEqual(ProvidersConfig, File.ReadAllText(fixture.ConfigPath), "A refused edit must not write.");
        var saved = fixture.Service.SaveProvider(new(Epoch, listed.Revision, null, edit with { Environment = token }, false, false));
        Assert.AreEqual("ok", saved.Status, saved.Message);
        var after = fixture.Service.Providers(new(Epoch));
        var variable = after.Providers.Single(static provider => provider.Key == "claude").Environment!.Single();
        Assert.AreEqual(("CLAUDE_CODE_OAUTH_TOKEN", "sk-ant-oat01-test"), (variable.Name, variable.Value));
        Assert.IsNull(after.Providers.Single(static provider => provider.Key == "local").Environment);

        // A copy is another account: the token of this one is not copied, as its folder is not.
        var copied = fixture.Service.DuplicateProvider(new(Epoch, after.Revision, "claude"));
        Assert.AreEqual("ok", copied.Status, copied.Message);
        var withCopy = fixture.Service.Providers(new(Epoch));
        Assert.IsNull(withCopy.Providers.Single(static provider => provider.Key != "claude" && provider.Type == "claude-code").Environment);

        // An empty list removes the variables; another type keeps none.
        var emptied = fixture.Service.SaveProvider(new(Epoch, withCopy.Revision, "claude", edit with { Environment = [] }, false, false));
        Assert.AreEqual("ok", emptied.Status, emptied.Message);
        Assert.IsNull(fixture.Service.Providers(new(Epoch)).Providers.Single(static provider => provider.Key == "claude").Environment);
        var retyped = fixture.Service.SaveProvider(new(Epoch, emptied.Revision, "claude",
            edit with { Type = "anthropic", ApiKeyEnv = "CODEALTA_ANTHROPIC_API_KEY", ClearApiKey = false, Environment = token }, false, false));
        Assert.AreEqual("ok", retyped.Status, retyped.Message);
        Assert.IsFalse(File.ReadAllText(fixture.ConfigPath).Contains("CLAUDE_CODE_OAUTH_TOKEN", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task SaveProvider_KeepsTheSpacesOfTheProvider_AlsoUnderANewKey()
    {
        await using var fixture = new Fixture("""
            [providers.claude-work]
            type = "claude-code"
            spaces = ["business"]
            """);
        var listed = fixture.Service.Providers(new(Epoch));
        var edit = new GlobalConfigProviderEdit("claude-work", "claude-code", true, "Work", null, null, null, null, null, true);

        var saved = fixture.Service.SaveProvider(new(Epoch, listed.Revision, "claude-work", edit, false, false));
        Assert.AreEqual("ok", saved.Status, saved.Message);
        CollectionAssert.AreEqual(new[] { "business" }, fixture.Store.LoadGlobalSpaceAssignments()
            .Of(SpaceResourceKind.Provider, "claude-work").ToArray(), "A save of the form leaves the spaces as they are.");

        var renamed = fixture.Service.SaveProvider(new(Epoch, saved.Revision, "claude-work", edit with { Key = "claude-business" }, false, false));
        Assert.AreEqual("ok", renamed.Status, renamed.Message);
        var assignments = fixture.Store.LoadGlobalSpaceAssignments();
        CollectionAssert.AreEqual(new[] { "business" }, assignments.Of(SpaceResourceKind.Provider, "claude-business").ToArray());
        Assert.AreEqual(0, assignments.Of(SpaceResourceKind.Provider, "claude-work").Count);
    }

    [TestMethod]
    public async Task DuplicateProvider_AddsANumberedCopyWithoutTheFolderOfTheAccount()
    {
        await using var fixture = new Fixture("""
            [providers.claude-code]
            type = "claude-code"
            config_dir = "~/.claude"
            args = ["--add-dir", "/data"]
            permission_mode = "plan"

            [providers.claude-code-2]
            type = "claude-code"
            display_name = "Claude Code 2"
            """);
        var listed = fixture.Service.Providers(new(Epoch));

        var first = fixture.Service.DuplicateProvider(new(Epoch, listed.Revision, "claude-code"));
        Assert.AreEqual("ok", first.Status, first.Message);
        Assert.AreEqual("claude-code-3", first.Key, "The first free number.");
        var second = fixture.Service.DuplicateProvider(new(Epoch, first.Revision, "claude-code-2"));
        Assert.AreEqual("claude-code-4", second.Key, "The copy of a numbered provider is the next number, not a number of it.");

        var providers = fixture.Service.Providers(new(Epoch)).Providers;
        var copy = providers.Single(static provider => provider.Key == "claude-code-3");
        Assert.AreEqual(("claude-code", "Claude Code 3", "plan", null), (copy.Type, copy.DisplayName, copy.PermissionMode, copy.ConfigDir));
        Assert.AreEqual("Claude Code 4", providers.Single(static provider => provider.Key == "claude-code-4").DisplayName);
        StringAssert.Contains(File.ReadAllText(fixture.ConfigPath), "[providers.claude-code-3]");
        Assert.AreEqual(2, File.ReadAllText(fixture.ConfigPath).Split("--add-dir").Length - 1, "The settings the form does not show are copied too.");

        var stale = fixture.Service.DuplicateProvider(new(Epoch, listed.Revision, "claude-code"));
        Assert.AreEqual("conflict", stale.Status);
        Assert.IsNull(stale.Key);
        Assert.AreEqual("invalid", fixture.Service.DuplicateProvider(new(Epoch, second.Revision, "missing")).Status);
    }

    [TestMethod]
    public async Task SaveProvider_WritesThePermissionModeOfACliProvider_AndTakesItForNoOtherType()
    {
        await using var fixture = new Fixture(ProvidersConfig);
        var listed = fixture.Service.Providers(new(Epoch));
        CollectionAssert.AreEqual(new[] { "default", "acceptEdits", "plan", "auto", "dontAsk", "bypassPermissions" }, listed.PermissionModes.ToArray());
        Assert.IsTrue(listed.Providers.All(static provider => provider.PermissionMode is null));
        var cli = new GlobalConfigProviderEdit("claude", "claude-code", true, "Claude", null, null, null, null, null, true);

        // A mode the CLI does not have is refused, and nothing is written.
        foreach (var refused in new[] { cli with { PermissionMode = "whatever" }, cli with { PermissionMode = "Auto" } })
            Assert.AreEqual("invalid", fixture.Service.SaveProvider(new(Epoch, listed.Revision, null, refused, false, false)).Status, refused.PermissionMode);
        Assert.AreEqual(ProvidersConfig, File.ReadAllText(fixture.ConfigPath), "Refused edits must not write.");

        var saved = fixture.Service.SaveProvider(new(Epoch, listed.Revision, null, cli with { PermissionMode = " auto " }, false, false));
        Assert.AreEqual("ok", saved.Status, saved.Message);
        StringAssert.Contains(File.ReadAllText(fixture.ConfigPath), "permission_mode = \"auto\"");
        var after = fixture.Service.Providers(new(Epoch));
        Assert.AreEqual("auto", after.Providers.Single(static provider => provider.Key == "claude").PermissionMode);
        Assert.IsNull(after.Providers.Single(static provider => provider.Key == "local").PermissionMode);

        // Blank leaves the mode to the settings of the CLI.
        var cleared = fixture.Service.SaveProvider(new(Epoch, after.Revision, "claude", cli with { PermissionMode = " " }, false, false));
        Assert.AreEqual("ok", cleared.Status, cleared.Message);
        Assert.IsFalse(File.ReadAllText(fixture.ConfigPath).Contains("permission_mode", StringComparison.Ordinal));

        // The configuration file takes the field for no other adapter type: a type changed away from the CLI
        // drops the mode instead of writing something the store would refuse to read back.
        var restored = fixture.Service.SaveProvider(new(Epoch, cleared.Revision, "claude", cli with { PermissionMode = "plan" }, false, false));
        Assert.AreEqual("ok", restored.Status, restored.Message);
        var moved = fixture.Service.SaveProvider(new(Epoch, restored.Revision, "claude",
            cli with { Type = "anthropic", ApiKeyEnv = "CLAUDE_KEY", PermissionMode = "plan" }, false, false));
        Assert.AreEqual("ok", moved.Status, moved.Message);
        Assert.IsFalse(File.ReadAllText(fixture.ConfigPath).Contains("permission_mode", StringComparison.Ordinal));
        Assert.IsNull(fixture.Service.Providers(new(Epoch)).Providers.Single(static provider => provider.Key == "claude").PermissionMode);
    }

    [TestMethod]
    public async Task SaveProvider_WritesTheModelsOrder_InPlaceOfTheOlderSortModels()
    {
        await using var fixture = new Fixture(ProvidersConfig.Replace("network_timeout_seconds = 77", "network_timeout_seconds = 77\nsort_models = true", StringComparison.Ordinal));
        var listed = fixture.Service.Providers(new(Epoch));
        Assert.AreEqual("name", listed.Providers.Single(static provider => provider.Key == "local").ModelsOrder, "sort_models = true is the order by name.");
        Assert.IsNull(listed.Providers.Single(static provider => provider.Key == "spare").ModelsOrder);
        var edit = new GlobalConfigProviderEdit("local", "openai-chat", true, "Local", "model-a", null, "http://127.0.0.1:9999/v1", null, null, false);

        Assert.AreEqual("invalid", fixture.Service.SaveProvider(new(Epoch, listed.Revision, "local", edit with { ModelsOrder = "newest" }, false, false)).Status);
        var saved = fixture.Service.SaveProvider(new(Epoch, listed.Revision, "local", edit with { ModelsOrder = "Name_Version" }, false, false));
        Assert.AreEqual("ok", saved.Status, saved.Message);
        var written = File.ReadAllText(fixture.ConfigPath);
        StringAssert.Contains(written, "models_order = \"name_version\"");
        Assert.IsFalse(written.Contains("sort_models", StringComparison.Ordinal));
        Assert.AreEqual("name_version", fixture.Service.Providers(new(Epoch)).Providers.Single(static provider => provider.Key == "local").ModelsOrder);

        // Blank is the order of the type.
        var cleared = fixture.Service.SaveProvider(new(Epoch, saved.Revision, "local", edit, false, false));
        Assert.AreEqual("ok", cleared.Status, cleared.Message);
        Assert.IsFalse(File.ReadAllText(fixture.ConfigPath).Contains("models_order", StringComparison.Ordinal));
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

    [TestMethod]
    public async Task AProviderRemovedLeavesTheTiers_AndARenamedOneTakesItsTargetsAlong()
    {
        await using var fixture = new Fixture("""
            [providers.alpha]
            type = "openai-chat"
            api_url = "http://127.0.0.1:9999/v1"
            api_key_env = "ALPHA_KEY"

            [providers.bravo]
            type = "openai-chat"
            api_url = "http://127.0.0.1:9998/v1"
            api_key_env = "BRAVO_KEY"

            [models.tiers]
            standard = ["alpha:model-a@high", "bravo:model-b"]
            quick = ["alpha:model-q"]

            [models.fallback]
            enabled = true
            """);
        var root = Path.GetDirectoryName(fixture.ConfigPath)!;
        var spaces = new SpaceCatalog(new ProjectCatalog(new CatalogOptions { GlobalRoot = root }));
        Assert.IsTrue((await spaces.CreateAsync("Business")).Succeeded);
        Assert.IsTrue((await spaces.CreateAsync("Personal")).Succeeded);
        var service = new GlobalConfigService(fixture.Store, fixture.Registry, root, Epoch) { Spaces = spaces };
        // Business names only the provider that goes; Personal names both.
        Assert.AreEqual("ok", SaveTiers(service, new(Epoch, [new("standard", [new("alpha", "model-a", [])])], new(true, true, false, false, 5)) { SpaceId = "business" }).Status);
        Assert.AreEqual("ok", SaveTiers(service, new(Epoch, [new("quick", [new("bravo", "model-b", []), new("alpha", "model-q", [])])], new(true, true, true, false, 5)) { SpaceId = "personal" }).Status);

        var deleted = await service.DeleteProviderAsync(new(Epoch, service.Providers(new(Epoch)).Revision, "alpha", false), CancellationToken.None);
        Assert.AreEqual("ok", deleted.Status, deleted.Message);
        var models = fixture.Store.LoadGlobalModelSettings();
        CollectionAssert.AreEqual(new[] { "bravo:model-b" }, models.Tiers.Standard.Select(static target => target.Format()).ToArray());
        Assert.AreEqual(0, models.Tiers.Quick.Count);
        Assert.IsTrue(models.Fallback.Enabled, "The fallback stays as it was.");
        Assert.IsNull((await spaces.GetAsync("business"))!.Settings.NewSessions, "A space left without tiers takes the global ones, and their fallback.");
        CollectionAssert.AreEqual(new[] { "bravo:model-b" },
            (await spaces.GetAsync("personal"))!.Settings.NewSessions!.Tiers!.Quick.Select(static target => target.Format()).ToArray());

        var bravo = new GlobalConfigProviderEdit("charlie", "openai-chat", true, null, null, null, "http://127.0.0.1:9998/v1", "BRAVO_KEY", null, false);
        var renamed = await service.SaveProviderAsync(new(Epoch, service.Providers(new(Epoch)).Revision, "bravo", bravo, false, false), CancellationToken.None);
        Assert.AreEqual("ok", renamed.Status, renamed.Message);
        CollectionAssert.AreEqual(new[] { "charlie:model-b" }, fixture.Store.LoadGlobalModelSettings().Tiers.Standard.Select(static target => target.Format()).ToArray());
        CollectionAssert.AreEqual(new[] { "charlie:model-b" },
            (await spaces.GetAsync("personal"))!.Settings.NewSessions!.Tiers!.Quick.Select(static target => target.Format()).ToArray());
    }

    [TestMethod]
    public async Task TheProviderANewSessionStartsWith_IsNamed_AndADefaultThatIsUncheckedIsCleared()
    {
        const string Config = """
            [chat]
            default_provider = "zulu"

            [providers.zulu]
            type = "openai-chat"
            display_name = "Zulu"
            api_url = "http://127.0.0.1:9999/v1"
            api_key_env = "ZULU_KEY"

            [providers.bravo]
            type = "openai-chat"
            display_name = "Bravo"
            api_url = "http://127.0.0.1:9998/v1"
            api_key_env = "BRAVO_KEY"

            [providers.alpha]
            enabled = false
            type = "openai-chat"
            display_name = "Alpha"
            api_url = "http://127.0.0.1:9997/v1"
            api_key_env = "ALPHA_KEY"
            """;
        await using var fixture = new Fixture(Config);
        var listed = fixture.Service.Providers(new(Epoch));
        Assert.AreEqual(("zulu", "zulu"), (listed.DefaultProvider, listed.StartingProvider));
        var zulu = new GlobalConfigProviderEdit("zulu", "openai-chat", true, "Zulu", null, null, "http://127.0.0.1:9999/v1", "ZULU_KEY", null, false);

        // The box is unchecked: the provider is no longer named as the default, and the first enabled one by name is.
        var cleared = fixture.Service.SaveProvider(new(Epoch, listed.Revision, "zulu", zulu, MakeDefault: false, ApplyProviders: false));
        Assert.AreEqual("ok", cleared.Status, cleared.Message);
        listed = fixture.Service.Providers(new(Epoch));
        Assert.AreEqual((null, "bravo"), (listed.DefaultProvider, listed.StartingProvider), "A disabled provider is never the one a session starts with.");
        Assert.IsNull(fixture.Store.GetEffectiveDefaultProvider());

        // Saving another provider leaves the default of the configuration alone.
        Assert.AreEqual("ok", fixture.Service.SaveProvider(new(Epoch, listed.Revision, "zulu", zulu, MakeDefault: true, ApplyProviders: false)).Status);
        listed = fixture.Service.Providers(new(Epoch));
        var bravo = new GlobalConfigProviderEdit("bravo", "openai-chat", true, "Bravo", "model-b", null, "http://127.0.0.1:9998/v1", "BRAVO_KEY", null, false);
        Assert.AreEqual("ok", fixture.Service.SaveProvider(new(Epoch, listed.Revision, "bravo", bravo, MakeDefault: false, ApplyProviders: false)).Status);
        listed = fixture.Service.Providers(new(Epoch));
        Assert.AreEqual(("zulu", "zulu"), (listed.DefaultProvider, listed.StartingProvider));

        // A default that is disabled is no longer the default: the store gives the place to the first enabled provider.
        Assert.AreEqual("ok", fixture.Service.SaveProvider(new(Epoch, listed.Revision, "zulu", zulu with { Enabled = false }, MakeDefault: false, ApplyProviders: false)).Status);
        listed = fixture.Service.Providers(new(Epoch));
        Assert.AreEqual(("bravo", "bravo"), (listed.DefaultProvider, listed.StartingProvider));
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

    private static GlobalConfigModelTiersResponse ReadTiers(GlobalConfigService service, GlobalConfigModelTiersRequest request)
        => service.ModelTiersAsync(request, CancellationToken.None).GetAwaiter().GetResult();

    private static GlobalConfigModelTiersResponse SaveTiers(GlobalConfigService service, GlobalConfigSaveModelTiersRequest request)
        => service.SaveModelTiersAsync(request, CancellationToken.None).GetAwaiter().GetResult();

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
