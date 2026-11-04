using CodeAlta.Catalog;
using CodeAlta.Catalog.Secrets;
using CodeAlta.Fleet.Identity;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Fleet.Protocol.Api;

namespace CodeAlta.Node.Tests;

/// <summary>
/// The providers of the teams and of the organization a machine runs with (<c>doc/fleet/orchestration.md</c>, credentials of
/// unattended runs): written to its configuration with their key in its protected store, so that they run with the key of an
/// account; removed once the server no longer gives them; a provider of the user's own with the same key never replaced.
/// </summary>
[TestClass]
public sealed class NodeScopeProvidersTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task AProviderOfTheTeamIsWrittenWithItsKeyInTheStore_ThenRemoved_AndOneOfTheUserStays()
    {
        var token = TestContext.CancellationToken;
        using var root = new NodeTestRoot();
        await using var node = await NodeHost.StartAsync(root.Options(), token);
        var catalog = node.Host.CatalogOptions;
        using var identity = DeviceIdentity.LoadOrCreate(Path.Combine(root.Root, "self"));
        var store = new LocalSecretStore(Path.Combine(root.Root, "secrets"), new PlainProtector());
        var config = new CodeAltaConfigStore(catalog);
        config.SaveGlobalProviderDefinitions([.. config.LoadGlobalProviderDefinitions(includeDisabled: true),
            new CodeAltaProviderDocument { ProviderKey = "mine", ProviderType = "anthropic", ApiKeyEnv = "MY_KEY" }]);
        var providers = new NodeScopeProviders(catalog, store, identity);
        ScopeProviderDto Provider(string id) => new(id, FleetPrincipalKinds.Team, "infra", "Infra gateway", "openai-responses", "https://gateway.example/v1", ["gpt-6.1"], 1,
            "p2", DateTimeOffset.UtcNow, ["self"]);

        CollectionAssert.AreEqual(new[] { "infra-gateway" }, providers.Apply("link-1", [(Provider("infra-gateway"), "sk-team"), (Provider("mine"), "sk-other")]).ToArray(),
            "the provider of the user keeps its key");
        var written = config.LoadGlobalProviderDefinitions(includeDisabled: true);
        var team = written.Single(definition => definition.ProviderKey == "infra-gateway");
        Assert.AreEqual(("openai-responses", "https://gateway.example/v1", "gpt-6.1", NodeScopeProviders.SecretName("infra-gateway")),
            (team.ProviderType, team.ApiUrl, team.Model, team.ApiKeySecret));
        Assert.IsTrue(store.TryGetSecret(NodeScopeProviders.SecretName("infra-gateway"), out var key));
        Assert.AreEqual("sk-team", key);
        Assert.AreEqual("MY_KEY", written.Single(definition => definition.ProviderKey == "mine").ApiKeyEnv);
        Assert.AreEqual(CodeAlta.Automations.AutomationCredentialKind.Account, CodeAlta.Automations.AutomationCredentials.Classify(team.ProviderType!, team),
            "it runs with the key of an account");

        // The server no longer gives it: gone from the configuration and from the store; the user's own stays.
        Assert.AreEqual(0, providers.Apply("link-1", []).Count);
        Assert.IsFalse(config.LoadGlobalProviderDefinitions(includeDisabled: true).Any(definition => definition.ProviderKey == "infra-gateway"));
        Assert.IsFalse(store.TryGetSecret(NodeScopeProviders.SecretName("infra-gateway"), out _));
        Assert.IsTrue(config.LoadGlobalProviderDefinitions(includeDisabled: true).Any(definition => definition.ProviderKey == "mine"));
    }

    [TestMethod]
    public async Task AProviderOfTheTeam_RunsAtOnce_TakesARotatedKey_AndStopsOnceTakenAway()
    {
        var token = TestContext.CancellationToken;
        using var root = new NodeTestRoot();
        await using var node = await NodeHost.StartAsync(root.Options(), token);
        var catalog = node.Host.CatalogOptions;
        using var identity = DeviceIdentity.LoadOrCreate(Path.Combine(root.Root, "self"));
        // The store the node reads the keys of its providers from.
        var store = new LocalSecretStore(catalog.GlobalRoot);
        var live = new NodeProviders(node.Host, null);
        var changes = new List<string>();
        var providers = new NodeScopeProviders(catalog, store, identity)
        {
            ProvidersChanged = changed => { changes.Add(string.Join(",", changed)); live.Reconcile(changed); },
        };
        ScopeProviderDto Provider(string id) => new(id, FleetPrincipalKinds.Team, "infra", "Infra gateway", "openai-responses", "https://gateway.example/v1", ["gpt-6.1"], 1,
            "p2", DateTimeOffset.UtcNow, ["self"]);
        bool Registered() => node.Host.ModelProviderRegistry.ListProviders(includeDisabled: true).Any(static provider => provider.ProviderId.Value == "infra-gateway");

        providers.Apply("link-1", [(Provider("infra-gateway"), "sk-team")]);
        Assert.IsTrue(Registered(), "a provider given is run by the node at once, not at its next start");
        providers.Apply("link-1", [(Provider("infra-gateway"), "sk-team")]);
        providers.Apply("link-1", [(Provider("infra-gateway"), "sk-rotated")]);
        CollectionAssert.AreEqual(new[] { "infra-gateway", "infra-gateway" }, changes.ToArray(), "registered again for its new key; an unchanged one is left running");

        // Taken away: the node stops using it, and its key, at once.
        providers.Apply("link-1", []);
        // While the runtime still prepares it (its models listed), it is unregistered as soon as that ends.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        while (Registered() && DateTime.UtcNow < deadline) await Task.Delay(100, token);
        Assert.IsFalse(Registered(), "a provider taken away is no longer usable");
    }

    // Keeps values as they are: the protection of the machine is not what this test is about.
    private sealed class PlainProtector : ISecretProtector
    {
        public string Kind => "test";

        public byte[] Protect(ReadOnlySpan<byte> value) => value.ToArray();

        public byte[] Unprotect(ReadOnlySpan<byte> protectedValue) => protectedValue.ToArray();
    }
}
