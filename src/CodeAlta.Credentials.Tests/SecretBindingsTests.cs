using System.Text.Json.Nodes;
using CodeAlta.Agent.Runtime;
using CodeAlta.Catalog.Secrets;

namespace CodeAlta.Credentials.Tests;

[TestClass]
public sealed class SecretBindingsTests
{
    [TestMethod]
    public void A_binding_has_a_version_that_grows_and_keeps_what_another_version_wrote()
    {
        using var vault = new FakeVault();
        var store = new LocalSecretStore(vault.Profile);
        var first = store.Bind("ANTHROPIC_API_KEY", "personal", "op://Private/Anthropic/credential", "Private › Anthropic › credential", "alice");
        Assert.AreEqual(1, first.Version);
        var path = store.Bindings.FilePath;
        var document = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        document["sign_ins"] = new JsonObject { ["future"] = true };
        File.WriteAllText(path, document.ToJsonString());

        var second = new LocalSecretStore(vault.Profile).Bind("ANTHROPIC_API_KEY", "personal", "op://Private/Anthropic/other", "other", "alice");
        Assert.AreEqual(2, second.Version);
        StringAssert.Contains(File.ReadAllText(path), "future", "a member this version does not know stays");
        Assert.DoesNotContain("sk-", File.ReadAllText(path), "the file holds no value");
        Assert.IsTrue(store.Bindings.Unbind("anthropic_api_key"), "a name is the same whatever its case");
        Assert.IsEmpty(store.Bindings.List());
    }

    [TestMethod]
    public void A_bound_secret_is_given_only_by_the_source_of_the_host_and_never_from_an_old_local_value()
    {
        using var vault = new FakeVault();
        var store = new LocalSecretStore(vault.Profile, new PlainProtector());
        store.SetSecret("TOKEN", "local-value-1", "alice");
        store.Bind("TOKEN", "personal", "op://Private/Anthropic/credential", "label", "alice");
        Assert.IsFalse(store.TryGetSecret("OTHER", out _));
        Assert.ThrowsExactly<SecretStoreUnavailableException>(() => new LocalSecretStore(vault.Profile, new PlainProtector()).TryGetSecret("TOKEN", out _),
            "a host without a broker reads no vault, and never the value the name had here");

        var source = new Source { Values = { ["TOKEN"] = "vault-value-1" } };
        Assert.IsTrue(new LocalSecretStore(vault.Profile, new PlainProtector()) { BoundSecrets = source, Use = "provider a" }.TryGetSecret("TOKEN", out var value));
        Assert.AreEqual("vault-value-1", value);
        Assert.AreEqual("provider a", source.LastUse);
        var listed = store.ListSecrets().Single();
        Assert.AreEqual(("personal", "label"), (listed.Provider, listed.Label));
        Assert.Contains("local-value-1", store.GetSecretValues(), "the value taken away stays masked");

        store.SetSecret("TOKEN", "local-value-2", "alice");
        Assert.IsNull(store.ListSecrets().Single().Provider, "a value set here takes the name back to this computer");
        Assert.IsTrue(new LocalSecretStore(vault.Profile, new PlainProtector()).TryGetSecret("TOKEN", out var local));
        Assert.AreEqual("local-value-2", local);
    }

    [TestMethod]
    public void A_local_secret_that_asks_before_use_is_given_only_for_a_confirmed_use()
    {
        using var vault = new FakeVault();
        var store = new LocalSecretStore(vault.Profile, new PlainProtector());
        store.SetSecret("TOKEN", "local-value-1", "alice");
        store.Bindings.SetAskBeforeUse("TOKEN", SecretAskDurations.FifteenMinutes);
        Assert.ThrowsExactly<SecretStoreUnavailableException>(() => new LocalSecretStore(vault.Profile, new PlainProtector()).TryGetSecret("TOKEN", out _));
        var source = new Source { Refusal = "TOKEN asks before use" };
        var refused = Assert.ThrowsExactly<SecretStoreUnavailableException>(() => new LocalSecretStore(vault.Profile, new PlainProtector()) { BoundSecrets = source }.TryGetSecret("TOKEN", out _));
        StringAssert.Contains(refused.Message, "asks before use");
        source.Refusal = null;
        Assert.IsTrue(new LocalSecretStore(vault.Profile, new PlainProtector()) { BoundSecrets = source }.TryGetSecret("TOKEN", out var value));
        Assert.AreEqual("local-value-1", value);
        store.DeleteSecret("TOKEN");
        Assert.IsNull(store.Bindings.AskBeforeUse("TOKEN"), "a deleted secret stops asking");
    }

    [TestMethod]
    public void The_resolver_of_the_configuration_names_the_vault_of_a_secret_it_cannot_give()
    {
        using var vault = new FakeVault();
        new LocalSecretStore(vault.Profile, new PlainProtector()).Bind("TOKEN", "personal", "op://Private/Anthropic/credential", "label", "alice");
        var source = new Source { Problem = "TOKEN is kept in 1Password (personal), which is locked." };
        var failure = Assert.ThrowsExactly<SecretReferenceException>(() => SecretReferenceResolver.ForMachine(vault.Profile, null, source, "provider a").Resolve("Bearer ${secret:TOKEN}"));
        StringAssert.Contains(failure.Message, "1Password (personal), which is locked");
        source.Problem = null;
        source.Values["TOKEN"] = "vault-value-1";
        Assert.AreEqual("Bearer vault-value-1", SecretReferenceResolver.ForMachine(vault.Profile, null, source, "provider a").Resolve("Bearer ${secret:TOKEN}"));
    }

    internal sealed class Source : IBoundSecretSource
    {
        public Dictionary<string, string> Values { get; } = new(StringComparer.OrdinalIgnoreCase);

        public string? Problem { get; set; }

        public string? Refusal { get; set; }

        public string? LastUse { get; private set; }

        public BoundSecretRead ReadBound(string name, string? use)
        {
            LastUse = use;
            return Values.TryGetValue(name, out var value) ? BoundSecretRead.Of(value) : BoundSecretRead.Unavailable(Problem ?? $"{name} is not held.");
        }

        public string? AuthorizeUse(string name, string? use) => Refusal;
    }

    // Keeps the values in clear, so that the tests need no protected store of the machine.
    internal sealed class PlainProtector : ISecretProtector
    {
        public string Kind => SecretProtectionKinds.File;

        public byte[] Protect(ReadOnlySpan<byte> value) => value.ToArray();

        public byte[] Unprotect(ReadOnlySpan<byte> protectedValue) => protectedValue.ToArray();
    }
}
