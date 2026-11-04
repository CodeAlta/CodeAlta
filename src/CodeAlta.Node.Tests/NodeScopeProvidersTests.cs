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

    // Keeps values as they are: the protection of the machine is not what this test is about.
    private sealed class PlainProtector : ISecretProtector
    {
        public string Kind => "test";

        public byte[] Protect(ReadOnlySpan<byte> value) => value.ToArray();

        public byte[] Unprotect(ReadOnlySpan<byte> protectedValue) => protectedValue.ToArray();
    }
}
