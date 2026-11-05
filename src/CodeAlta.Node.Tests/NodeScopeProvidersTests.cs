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
    public async Task AnOlderVersion_OrAProviderTakenAway_IsNotInstalledAgain_AlsoAfterARestart()
    {
        var token = TestContext.CancellationToken;
        using var root = new NodeTestRoot();
        await using var node = await NodeHost.StartAsync(root.Options(), token);
        var catalog = node.Host.CatalogOptions;
        using var identity = DeviceIdentity.LoadOrCreate(Path.Combine(root.Root, "self"));
        var store = new LocalSecretStore(Path.Combine(root.Root, "secrets"), new PlainProtector());
        var config = new CodeAltaConfigStore(catalog);
        var providers = new NodeScopeProviders(catalog, store, identity);
        ScopeProviderDto Provider(long version) => new("infra-gateway", FleetPrincipalKinds.Team, "infra", "Infra gateway", "openai-responses",
            $"https://gateway.example/v{version}", ["gpt-6.1"], version, "p2", DateTimeOffset.UtcNow, ["self"]);
        (string? Endpoint, string? Key) Installed()
        {
            var definition = config.LoadGlobalProviderDefinitions(includeDisabled: true).SingleOrDefault(static definition => definition.ProviderKey == "infra-gateway");
            return (definition?.ApiUrl, store.TryGetSecret(NodeScopeProviders.SecretName("infra-gateway"), out var key) ? key : null);
        }

        providers.Apply("link-1", [(Provider(2), "sk-2")]);
        Assert.AreEqual(("https://gateway.example/v2", "sk-2"), Installed());

        // A genuine copy of version 1 replayed by the server: the machine keeps version 2.
        CollectionAssert.AreEqual(new[] { "infra-gateway" }, providers.Apply("link-1", [(Provider(1), "sk-1")]).ToArray());
        Assert.AreEqual(("https://gateway.example/v2", "sk-2"), Installed(), "a rotation is not rolled back");

        // Taken away, then version 2 given again: it does not come back.
        Assert.AreEqual(0, providers.Apply("link-1", []).Count);
        Assert.AreEqual(0, providers.Apply("link-1", [(Provider(2), "sk-2")]).Count, "a removed provider is not resurrected");
        Assert.AreEqual(((string?)null, (string?)null), Installed());

        // After a restart of the machine, the checkpoints hold; a newer version comes in.
        providers = new NodeScopeProviders(catalog, store, identity);
        Assert.AreEqual(0, providers.Apply("link-1", [(Provider(2), "sk-2")]).Count);
        CollectionAssert.AreEqual(new[] { "infra-gateway" }, providers.Apply("link-1", [(Provider(3), "sk-3")]).ToArray());
        Assert.AreEqual(("https://gateway.example/v3", "sk-3"), Installed());
        providers = new NodeScopeProviders(catalog, store, identity);
        providers.Apply("link-1", [(Provider(1), "sk-1")]);
        Assert.AreEqual(("https://gateway.example/v3", "sk-3"), Installed());
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

    [TestMethod]
    public async Task AControllerDeviceRemovedSince_GivesNoNewVersion_AndTheVersionItGaveBeforeStays()
    {
        var token = TestContext.CancellationToken;
        using var root = new NodeTestRoot();
        await using var node = await NodeHost.StartAsync(root.Options(), token);
        var catalog = node.Host.CatalogOptions;
        using var laptop = DeviceIdentity.LoadOrCreate(Path.Combine(root.Root, "laptop"));
        using var identity = DeviceIdentity.LoadOrCreate(Path.Combine(root.Root, "self"));
        using var bea = DeviceIdentity.LoadOrCreate(Path.Combine(root.Root, "bea"));
        using var beaDesk = DeviceIdentity.LoadOrCreate(Path.Combine(root.Root, "bea-desk"));
        var store = new LocalSecretStore(Path.Combine(root.Root, "secrets"), new PlainProtector());
        var config = new CodeAltaConfigStore(catalog);
        var providers = new NodeScopeProviders(catalog, store, identity);

        // Bea maintains the team of this machine, with two controller devices; her laptop is removed at the fifth entry.
        var entries = new List<FleetSignedDeviceListEntry>();
        void Add(string operation, DeviceIdentity device, string? kind, string? organization, string? owner, DeviceIdentity? by = null)
        {
            var payload = FleetDeviceListEntries.SerializePayload(new FleetDeviceListEntryPayload(1, organization, entries.Count + 1,
                entries.Count == 0 ? null : FleetDeviceListEntries.Hash(entries[^1]), operation, device.DeviceId,
                operation == FleetDeviceListOperations.Add ? Convert.ToBase64String(device.ExportSubjectPublicKeyInfo()) : null, kind, owner, (by ?? laptop).DeviceId,
                new DateTimeOffset(2026, 11, 3, 9, 0, 0, TimeSpan.Zero)));
            entries.Add(new FleetSignedDeviceListEntry(payload, System.Buffers.Text.Base64Url.EncodeToString((by ?? laptop).SignData(FleetDeviceListEntries.GetSignedBytes(payload)))));
        }

        Add(FleetDeviceListOperations.Add, laptop, FleetDeviceKinds.Controller, null, null);
        Add(FleetDeviceListOperations.Add, identity, FleetDeviceKinds.Node, "org", "p1");
        Add(FleetDeviceListOperations.Add, bea, FleetDeviceKinds.Controller, "org", "p2");
        Add(FleetDeviceListOperations.Add, beaDesk, FleetDeviceKinds.Controller, "org", "p2", bea);
        var before = FleetDeviceListVerifier.VerifyOrThrow(entries, "org");
        Add(FleetDeviceListOperations.Remove, bea, null, "org", null);
        var after = FleetDeviceListVerifier.VerifyOrThrow(entries, "org");
        Assert.IsTrue(after.Removed.Contains(bea.DeviceId));
        var access = FleetAccessState.Empty with
        {
            OrganizationId = "org", Teams = FleetAccessState.Empty.Teams.Add("infra", new FleetAccessTeam("infra", ["p2"], [], false, [])),
            Machines = FleetAccessState.Empty.Machines.Add(identity.DeviceId, "infra"),
        };
        Assert.IsTrue(FleetDevicePublicKey.TryParse(Convert.ToBase64String(identity.ExportSubjectPublicKeyInfo()), out var machineKey));
        ScopeProviderKeyDto Given(long version, string endpoint, string apiKey, DeviceIdentity signer)
        {
            var provider = new ScopeProviderDto("infra-gateway", FleetPrincipalKinds.Team, "infra", "Infra gateway", "openai-responses", endpoint, ["gpt-6.1"], version,
                "p2", DateTimeOffset.UtcNow, [identity.DeviceId]);
            var (sealedText, wraps) = Fleet.Server.FleetProviderKeys.Seal("org", provider.Id, version, Fleet.Server.FleetProviderDefinition.Of(provider), apiKey, [machineKey],
                before.Sequence, signer);
            return new ScopeProviderKeyDto(provider, sealedText, wraps.Single().Wrapped);
        }

        string? Endpoint() => config.LoadGlobalProviderDefinitions(includeDisabled: true).SingleOrDefault(static definition => definition.ProviderKey == "infra-gateway")?.ApiUrl;

        // Version 2, from her laptop while it was in the list: installed, and it stays once the laptop is removed.
        var second = Given(2, "https://gateway.example/v1", "sk-2", bea);
        providers.Apply("link-1", providers.Open("link-1", [second], before, access));
        Assert.AreEqual("https://gateway.example/v1", Endpoint());
        Assert.AreEqual(1, providers.Open("link-1", [second], after, access).Count, "the version installed holds as it was given");

        // The removed laptop signs version 3 against the entry it was in the list at, to another endpoint: refused.
        Assert.AreEqual(0, providers.Open("link-1", [Given(3, "https://attacker.example/v1", "sk-3", bea)], after, access).Count,
            "a device removed since gives no new version");

        // Her other device gives it: taken.
        var third = providers.Open("link-1", [Given(3, "https://gateway.example/v3", "sk-3", beaDesk)], after, access);
        providers.Apply("link-1", third);
        Assert.AreEqual("https://gateway.example/v3", Endpoint());
    }

    [TestMethod]
    public async Task TwoServersGivingAProviderOfTheSameId_NeverReplaceEachOther()
    {
        var token = TestContext.CancellationToken;
        using var root = new NodeTestRoot();
        await using var node = await NodeHost.StartAsync(root.Options(), token);
        var catalog = node.Host.CatalogOptions;
        using var identity = DeviceIdentity.LoadOrCreate(Path.Combine(root.Root, "self"));
        var store = new LocalSecretStore(Path.Combine(root.Root, "secrets"), new PlainProtector());
        var config = new CodeAltaConfigStore(catalog);
        var providers = new NodeScopeProviders(catalog, store, identity);
        ScopeProviderDto Provider(string id, string endpoint) => new(id, FleetPrincipalKinds.Organization, "org", "Gateway", "openai-responses", endpoint, ["gpt-6.1"], 1,
            "p2", DateTimeOffset.UtcNow, ["self"]);
        (string? Endpoint, string? Key) Installed()
        {
            var definition = config.LoadGlobalProviderDefinitions(includeDisabled: true).SingleOrDefault(static definition => definition.ProviderKey == "gateway");
            return (definition?.ApiUrl, store.TryGetSecret(NodeScopeProviders.SecretName("gateway"), out var key) ? key : null);
        }

        providers.Apply("link-1", [(Provider("gateway", "https://first.example/v1"), "sk-first")]);
        // The second organization names its provider the same: the first one stays as its server gave it.
        Assert.AreEqual(0, providers.Apply("link-2", [(Provider("gateway", "https://second.example/v1"), "sk-second")]).Count);
        Assert.AreEqual(("https://first.example/v1", "sk-first"), Installed());
        Assert.AreEqual(0, providers.Apply("link-2", [(Provider("Gateway", "https://second.example/v1"), "sk-second")]).Count, "nor in other letters");
        Assert.AreEqual(("https://first.example/v1", "sk-first"), Installed());
        // Taken away by the second server: the provider of the first one is not removed either.
        Assert.AreEqual(0, providers.Apply("link-2", []).Count);
        Assert.AreEqual(("https://first.example/v1", "sk-first"), Installed());

        // Once the first server no longer gives it, the id is free.
        providers.Apply("link-1", []);
        CollectionAssert.AreEqual(new[] { "gateway" }, providers.Apply("link-2", [(Provider("gateway", "https://second.example/v1"), "sk-second")]).ToArray());
        Assert.AreEqual(("https://second.example/v1", "sk-second"), Installed());
    }

    [TestMethod]
    public void TwoProvidersWhoseIdsReadAlike_NeverShareTheSecretOfTheirKey()
    {
        Assert.AreEqual("CODEALTA_PROVIDER_MY_GATEWAY", NodeScopeProviders.SecretName("my-gateway"), "an id of words joined by dashes reads in its name");
        var names = new[] { "my-gateway", "my_gateway", "My-Gateway", "my--gateway", "-my-gateway", "my-gateway-" }.Select(NodeScopeProviders.SecretName).ToArray();
        Assert.AreEqual(names.Length, names.Distinct(StringComparer.OrdinalIgnoreCase).Count(), string.Join(", ", names));
        Assert.IsTrue(names.All(LocalSecretStore.IsName), string.Join(", ", names));
    }

    // Keeps values as they are: the protection of the machine is not what this test is about.
    private sealed class PlainProtector : ISecretProtector
    {
        public string Kind => "test";

        public byte[] Protect(ReadOnlySpan<byte> value) => value.ToArray();

        public byte[] Unprotect(ReadOnlySpan<byte> protectedValue) => protectedValue.ToArray();
    }
}