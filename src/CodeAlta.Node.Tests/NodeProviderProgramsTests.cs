using CodeAlta.Agent.Harness;
using CodeAlta.Fleet.NodeApi;
using CodeAlta.Hosting;
using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Node.Tests;

/// <summary>
/// The providers a node offers and adds from the desktop (#414): its own types, Claude Code with the program it runs, the agents of
/// the catalogs of its plugins installed then written; an update of the program of an agent; and a configuration edited by hand,
/// applied without a restart.
/// </summary>
[TestClass]
public sealed class NodeProviderProgramsTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task TheNode_OffersClaudeCode_ItsTypes_AndTheAgentsOfItsPlugins()
    {
        var token = TestContext.CancellationToken;
        using var root = new NodeTestRoot();
        await using var node = await NodeHost.StartAsync(root.Options(), token);
        await using var endpoints = new LoopbackHarnessToolEndpoints();
        var types = new PluginModelProviderTypes(endpoints);
        var catalog = new FakeCatalog();
        types.Attach(() => [Contribution("acp", catalog)]);
        var providers = new NodeProviders(node.Host, null) { PluginProviderTypes = types };

        var offers = (await providers.ListOffersAsync(refresh: false, token)).Offers;

        var claude = offers.Single(static offer => offer.Id == "claude-code");
        Assert.AreEqual(("agent", "cli"), (claude.Group, claude.Credential));
        Assert.IsNotNull(claude.Program, "what the machine runs, or installs");
        Assert.AreEqual(("subscription", true), offers.Single(static offer => offer.Id == "copilot") is var copilot ? (copilot.Group, copilot.SignIn) : default);
        Assert.AreEqual(("subscription", "sign_in"), offers.Single(static offer => offer.Id == "mistral-vibe") is var vibe ? (vibe.Group, vibe.Credential) : default,
            "Mistral Vibe signs in with the browser: it runs no program");
        Assert.AreEqual("key", offers.Single(static offer => offer.Id == "anthropic").Group);
        var agent = offers.Single(static offer => offer.Id == "acp:gemini");
        Assert.AreEqual(("Gemini CLI", "runner", "0.9.0", "ACP registry"), (agent.Name, agent.Program!.State, agent.Program.Version, agent.Catalog));
        Assert.AreEqual("Node.js is not installed on the machine.", offers.Single(static offer => offer.Id == "acp:offline").Unavailable);
    }

    // An agent of the catalog of a plugin: installed by its plugin, with its progress, then written under the key it suggests (the
    // first free one), registered, and listed with the program it runs; its update writes what changed.
    [TestMethod]
    public async Task AnAgentOfACatalog_IsInstalledThenAdded_AndUpdated()
    {
        var token = TestContext.CancellationToken;
        using var root = new NodeTestRoot();
        await using var node = await NodeHost.StartAsync(root.Options(), token);
        File.WriteAllText(node.Host.CatalogOptions.ConfigPath, "[providers.gemini]\ntype = \"openai-chat\"\napi_url = \"http://127.0.0.1:9/v1\"\n");
        await using var endpoints = new LoopbackHarnessToolEndpoints();
        var types = new PluginModelProviderTypes(endpoints);
        var catalog = new FakeCatalog();
        types.Attach(() => [Contribution("acp", catalog)]);
        var providers = new NodeProviders(node.Host, null) { PluginProviderTypes = types };

        var started = await providers.StartAddAsync(new FleetProviderAddParams("acp:gemini"), "owner", token);
        var ended = await providers.WaitOperationAsync(started.Id, TimeSpan.FromSeconds(20), token);

        Assert.AreEqual(("completed", "gemini-2"), (ended.Status, ended.Key), ended.Message);
        CollectionAssert.Contains(catalog.Progress, "Downloading Gemini CLI…");
        var config = File.ReadAllText(node.Host.CatalogOptions.ConfigPath);
        StringAssert.Contains(config, "[providers.gemini-2]");
        StringAssert.Contains(config, "agent = \"gemini\"");
        var listed = (await providers.ListAsync(token)).Providers.Single(static provider => provider.Key == "gemini-2");
        Assert.AreEqual(("acp", "cli", "runner", "0.9.0", "1.0.0"), (listed.Type, listed.Credential, listed.Program!.State, listed.Program.Version, listed.Program.Update));
        Assert.AreEqual("gemini-2", (await providers.ListOffersAsync(false, token)).Offers.Single(static offer => offer.Id == "acp:gemini").ConfiguredKey);

        // A change made while the update downloads (a sign-in that wrote the environment of the agent) stays.
        catalog.DuringUpdate = () => File.WriteAllText(node.Host.CatalogOptions.ConfigPath,
            File.ReadAllText(node.Host.CatalogOptions.ConfigPath).Replace("[providers.gemini-2]", "[providers.gemini-2]\nenv = { OPENAI_API_KEY = \"${secret:KEY}\" }", StringComparison.Ordinal));
        var update = await providers.WaitOperationAsync((await providers.StartUpdateAsync("gemini-2", token)).Id, TimeSpan.FromSeconds(20), token);
        Assert.AreEqual("completed", update.Status, update.Message);
        var updated = File.ReadAllText(node.Host.CatalogOptions.ConfigPath);
        StringAssert.Contains(updated, "agent_version = \"1.0.0\"");
        StringAssert.Contains(updated, "OPENAI_API_KEY", "what was written during the update stays");

        // An entry its catalog does not have fails, with what the catalog said; an offer the node does not have is refused.
        var missing = await providers.WaitOperationAsync((await providers.StartAddAsync(new FleetProviderAddParams("acp:nothing"), "owner", token)).Id, TimeSpan.FromSeconds(20), token);
        Assert.AreEqual(("failed", "No entry nothing."), (missing.Status, missing.Message));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => providers.StartAddAsync(new FleetProviderAddParams("made-up"), "owner", token));
        Assert.AreEqual("failed", (await providers.WaitOperationAsync("unknown", TimeSpan.Zero, token)).Status);
    }

    // A type of the node that runs no program is written at once, under the first free key of its type.
    [TestMethod]
    public async Task ATypeOfTheNode_IsAddedAtOnce()
    {
        var token = TestContext.CancellationToken;
        using var root = new NodeTestRoot();
        await using var node = await NodeHost.StartAsync(root.Options(), token);
        var providers = new NodeProviders(node.Host, null);

        var first = await providers.StartAddAsync(new FleetProviderAddParams("copilot"), "owner", token);
        var second = await providers.StartAddAsync(new FleetProviderAddParams("copilot"), "owner", token);

        Assert.AreEqual(("completed", "copilot"), (first.Status, first.Key));
        Assert.AreEqual(("completed", "copilot-2"), (second.Status, second.Key));
        // Additions at once never take the same key.
        var many = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Task.Run(() => providers.StartAddAsync(new FleetProviderAddParams("xai"), "owner", token), token)));
        Assert.AreEqual(6, many.Select(static state => state.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => providers.StartAddAsync(new FleetProviderAddParams("copilot", "copilot"), "owner", token));
    }

    // A provider written into the configuration by hand is applied when the file changes, without a restart; the same file written
    // again changes nothing; a file that does not read leaves the providers as they run and says where it is wrong.
    [TestMethod]
    public async Task AConfigurationEditedByHand_IsAppliedWithoutARestart()
    {
        var token = TestContext.CancellationToken;
        using var root = new NodeTestRoot();
        await using var node = await NodeHost.StartAsync(root.Options(), token);
        var providers = new NodeProviders(node.Host, null);
        using var watch = providers.WatchConfiguration();
        var path = node.Host.CatalogOptions.ConfigPath;

        File.WriteAllText(path, "[providers.claude]\ntype = \"claude-code\"\n");
        CollectionAssert.AreEqual(new[] { "claude" }, providers.ApplyConfigurationChanges().ToArray());
        Assert.IsTrue(node.Host.ModelProviderRegistry.ListProviders(includeDisabled: true).Any(static provider => provider.ProviderId.Value == "claude"), "registered without a restart");
        Assert.AreNotEqual(FleetProviderReasons.Pending, (await providers.ListAsync(token)).Providers.Single(static provider => provider.Key == "claude").Reason);

        File.WriteAllText(path, "[providers.claude]\ntype = \"claude-code\"\n");
        Assert.AreEqual(0, providers.ApplyConfigurationChanges().Count, "nothing changed");

        // What the node writes itself is applied already.
        await providers.SetAsync(new FleetProviderSetParams("claude", null, "Claude", null, null, null), "owner", token);
        Assert.AreEqual(0, providers.ApplyConfigurationChanges().Count);

        // A key typed in a broken line: the page says where the file is wrong, never what it holds.
        File.WriteAllText(path, "[providers.claude]\ntype = \"claude-code\"\napi_key \"sk-ant-secret-0123456789\"\n");
        Assert.AreEqual(0, providers.ApplyConfigurationChanges().Count);
        var error = (await providers.ListAsync(token)).ConfigurationError;
        Assert.IsNotNull(error, "the page says the file does not read");
        StringAssert.Contains(error, "line 3");
        Assert.IsFalse(error.Contains("sk-ant", StringComparison.Ordinal), error);
        Assert.IsFalse(providers.ConfigurationError!.Contains("sk-ant", StringComparison.Ordinal));

        // A value of the wrong kind reads as TOML but not as a provider: nothing is applied, nothing stops the node.
        File.WriteAllText(path, "[providers.claude]\ntype = \"claude-code\"\nmax_concurrent_requests = \"oops\"\n");
        Assert.AreEqual(0, providers.ApplyConfigurationChanges().Count);
        Assert.IsNotNull(providers.ConfigurationError);
        Assert.IsTrue(node.Host.ModelProviderRegistry.ListProviders(includeDisabled: true).Any(static provider => provider.ProviderId.Value == "claude"), "the providers run as before");

        File.WriteAllText(path, "");
        CollectionAssert.AreEqual(new[] { "claude" }, providers.ApplyConfigurationChanges().ToArray());
        Assert.IsNull((await providers.ListAsync(token)).ConfigurationError);
        // A provider still prepared is taken away once its preparation ended.
        var until = DateTime.UtcNow + TestTime.Seconds(30);
        while (node.Host.ModelProviderRegistry.ListProviders(includeDisabled: true).Any(static provider => provider.ProviderId.Value == "claude") && DateTime.UtcNow < until)
            await Task.Delay(100, token);
        Assert.IsFalse(node.Host.ModelProviderRegistry.ListProviders(includeDisabled: true).Any(static provider => provider.ProviderId.Value == "claude"), "taken away");
    }

    private static PluginModelProviderContribution Contribution(string type, IPluginProviderCatalog catalog) => new()
    {
        ProviderType = type,
        DisplayName = "ACP agent",
        Catalog = catalog,
        CreateRuntime = static _ => throw new NotSupportedException("No runtime in this test."),
    };

    // The ACP registry as its plugin gives it: an agent run with npx, one that cannot run here; an install reports its progress, and
    // an agent installed at 0.9.0 has 1.0.0 to update to.
    private sealed class FakeCatalog : IPluginProviderCatalog
    {
        public List<string> Progress { get; } = [];

        public string Title => "ACP registry";

        public Task<PluginProviderCatalogSnapshot> ListAsync(bool refresh, CancellationToken cancellationToken)
            => Task.FromResult(new PluginProviderCatalogSnapshot([
                new PluginProviderCatalogEntry("gemini", "Gemini CLI", "0.9.0") { InstallKind = PluginProviderCatalogInstallKind.PackageRunner, Distribution = "npx" },
                new PluginProviderCatalogEntry("offline", "Offline agent", "1.0.0") { Unavailable = "Node.js is not installed on the machine." },
            ], DateTimeOffset.UtcNow));

        public Task<PluginProviderInstallResult> InstallAsync(string entryId, IProgress<PluginProviderInstallProgress> progress, CancellationToken cancellationToken)
        {
            if (entryId != "gemini") throw new InvalidOperationException($"No entry {entryId}.");
            progress.Report(new PluginProviderInstallProgress("Downloading Gemini CLI…", 10, 100));
            Progress.Add("Downloading Gemini CLI…");
            return Task.FromResult(new PluginProviderInstallResult("gemini", "Gemini CLI", new Dictionary<string, object?>
            {
                ["agent"] = "gemini", ["agent_version"] = "0.9.0", ["distribution"] = "npx",
            }));
        }

        /// <summary>Gets or sets what runs while an update downloads: a change of the configuration made meanwhile.</summary>
        public Action? DuringUpdate { get; set; }

        public Task<string?> GetUpdateAsync(PluginProviderSettings settings, CancellationToken cancellationToken)
            => Task.FromResult<string?>(settings.GetString("agent_version") == "0.9.0" ? "1.0.0" : null);

        public Task<IReadOnlyDictionary<string, object?>> UpdateAsync(PluginProviderSettings settings, IProgress<PluginProviderInstallProgress> progress, CancellationToken cancellationToken)
        {
            DuringUpdate?.Invoke();
            return Task.FromResult<IReadOnlyDictionary<string, object?>>(new Dictionary<string, object?> { ["agent_version"] = "1.0.0" });
        }
    }
}
