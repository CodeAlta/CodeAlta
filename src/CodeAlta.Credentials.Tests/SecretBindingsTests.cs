using System.Text.Json.Nodes;
using CodeAlta.Agent.Runtime;
using CodeAlta.Catalog;
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

    [TestMethod]
    public async Task ProjectRestrictionsCoverDeclaredSubprojectsButNotOtherProjectsOrHostUses()
    {
        using var vault = new FakeVault();
        var catalog = new ProjectCatalog(new CatalogOptions { GlobalRoot = vault.Profile });
        var repository = Directory.CreateDirectory(Path.Combine(vault.Profile, "repo")).FullName;
        var child = Directory.CreateDirectory(Path.Combine(repository, "app", "billing")).FullName;
        Directory.CreateDirectory(Path.Combine(repository, ".alta"));
        await File.WriteAllTextAsync(ProjectManifest.PathOf(repository), """
            [projects.app]
            path = "app"

            [projects.billing]
            path = "app/billing"
            """);
        var rootProject = await catalog.GetOrAddTransientAsync(repository);
        var projects = await catalog.LoadAsync();
        var app = projects.Single(project => project.DeclaredKey == "app");
        var billing = projects.Single(project => project.ProjectPath == child);
        Assert.AreEqual(rootProject.Id, billing.ParentId);

        var store = new LocalSecretStore(vault.Profile,
            new MasterKeySecretProtector(new FileSecretKeyStore(Path.Combine(vault.Profile, "secrets", "key"))));
        store.Bindings.SetProjects("GH_TOKEN", [new(app.Id, "acme-app")]);
        store.SetSecret("GH_TOKEN", "test-token-v1", "alice");
        store.RotateSecret("GH_TOKEN", "test-token-v2", "alice");
        var source = new Source();
        var checkout = SecretReferenceResolver.ForScope(vault.Profile, SecretScope.ForProject("default", billing, null), shell: true);
        Assert.AreEqual("test-token-v2", checkout.Resolve("${secret:gh_token}"));
        Assert.ThrowsExactly<SecretReferenceException>(() =>
            SecretReferenceResolver.ForScope(vault.Profile, SecretScope.ForProject("default", rootProject, null), shell: true).Resolve("${secret:GH_TOKEN}"));
        var denied = Assert.ThrowsExactly<SecretReferenceException>(() => SecretReferenceResolver.ForMachine(vault.Profile, null, source, "provider test")
            .Resolve("${secret:GH_TOKEN}"));
        StringAssert.Contains(denied.Message, "GH_TOKEN is limited to the projects acme-app");
        Assert.AreEqual(("GH_TOKEN", "provider test"), source.ProjectRefusals.Single());

        store.Bind("GH_TOKEN", "vault", "item/field", "item", "alice");
        store.Bindings.SetAskBeforeUse("GH_TOKEN", SecretAskDurations.EachUse);
        source.Values["GH_TOKEN"] = "cached-vault-token";
        Assert.ThrowsExactly<SecretReferenceException>(() => SecretReferenceResolver.ForMachine(vault.Profile, null, source, "MCP server test")
            .Resolve("${secret:GH_TOKEN}"));
        Assert.IsNull(source.LastUse, "The refusal neither reads a cached vault value nor asks for approval.");
        Assert.AreEqual("cached-vault-token", SecretReferenceResolver.ForScope(vault.Profile, SecretScope.ForProject("default", billing, null), shell: true,
            null, source, "allowed shell").Resolve("${secret:GH_TOKEN}"));
        Assert.AreEqual(app.Id, store.ListSecrets().Single().Projects.Single().Id);

        Assert.IsTrue(store.DeleteSecret("GH_TOKEN"));
        Assert.IsEmpty(new LocalSecretStore(vault.Profile).Bindings.ProjectsOf("GH_TOKEN"));
    }

    [TestMethod]
    public void AProjectRestrictionOnTheWinningMachineSecretDoesNotFallThroughToFleetValues()
    {
        using var vault = new FakeVault();
        var store = new LocalSecretStore(vault.Profile,
            new MasterKeySecretProtector(new FileSecretKeyStore(Path.Combine(vault.Profile, "secrets", "key"))));
        store.Bind("TOKEN", "vault", "item/field", "item", "alice");
        var projectId = ProjectId.NewVersion7().ToString();
        store.Bindings.SetProjects("TOKEN", [new(projectId, "acme-app")]);
        store.ReplaceFleetCopies("server", [new("server", "id", FleetSecretValue.SecretKind, "TOKEN", FleetSecretScopes.Person, null, 1, false, "fleet-value")]);
        var source = new Source { Values = { ["TOKEN"] = "cached-vault-token" } };

        Assert.ThrowsExactly<SecretReferenceException>(() => SecretReferenceResolver.ForScope(vault.Profile, SecretScope.None, shell: true, null, source, "shell")
            .Resolve("${secret:TOKEN}"));
        Assert.IsNull(source.LastUse);
        Assert.AreEqual("cached-vault-token", SecretReferenceResolver.ForScope(vault.Profile, new SecretScope(null, projectId, null), shell: true, null, source, "shell")
            .Resolve("${secret:TOKEN}"));

        Assert.IsTrue(store.Bindings.Unbind("TOKEN"));
        Assert.AreEqual("fleet-value", SecretReferenceResolver.ForScope(vault.Profile, SecretScope.None, shell: true)
            .Resolve("${secret:TOKEN}"), "A restriction left by an absent machine secret doesn't apply to the fleet copy.");
    }

    [TestMethod]
    public void AResolverTellsTheSecretsOnlyTheProjectOfTheWorkGets()
    {
        using var vault = new FakeVault();
        var store = new LocalSecretStore(vault.Profile,
            new MasterKeySecretProtector(new FileSecretKeyStore(Path.Combine(vault.Profile, "secrets", "key"))));
        var projectId = ProjectId.NewVersion7().ToString();
        store.SetSecret("ACME_TOKEN", "acme-value", "alice");
        store.Bindings.SetProjects("ACME_TOKEN", [new(projectId, "acme-app")]);
        store.SetSecret("SHARED_TOKEN", "shared-value", "alice");
        store.ReplaceFleetCopies("server",
        [
            new("server", "id-project", FleetSecretValue.SecretKind, "FLEET_PROJECT_TOKEN", FleetSecretScopes.Project, projectId, 1, false, "fleet-project-value"),
            new("server", "id-person", FleetSecretValue.SecretKind, "FLEET_PERSON_TOKEN", FleetSecretScopes.Person, null, 1, false, "fleet-person-value"),
        ]);
        var resolver = SecretReferenceResolver.ForScope(vault.Profile, new SecretScope(null, projectId, null), shell: true);
        bool ProjectOnly(string name)
        {
            Assert.IsTrue(resolver.TryGet(new(SecretReferenceKind.Secret, name), out _, out var only), name);
            return only;
        }

        Assert.IsTrue(ProjectOnly("ACME_TOKEN"), "a secret of the machine limited to projects");
        Assert.IsFalse(ProjectOnly("SHARED_TOKEN"), "a secret every project gets");
        Assert.IsTrue(ProjectOnly("FLEET_PROJECT_TOKEN"), "a secret the fleet gave for the project");
        Assert.IsFalse(ProjectOnly("FLEET_PERSON_TOKEN"), "a secret the fleet gave for every work of the person");
        Assert.AreEqual("acme-value", resolver.Read("secret:ACME_TOKEN", out _, out var read));
        Assert.IsTrue(read);
        Assert.IsNotNull(resolver.Read("PATH", out _, out read));
        Assert.IsFalse(read, "a variable of the environment is no secret of the project");
    }

    internal sealed class Source : IBoundSecretSource
    {
        public Dictionary<string, string> Values { get; } = new(StringComparer.OrdinalIgnoreCase);

        public string? Problem { get; set; }

        public string? Refusal { get; set; }

        public string? LastUse { get; private set; }

        public List<(string Name, string? Use)> ProjectRefusals { get; } = [];

        public BoundSecretRead ReadBound(string name, string? use)
        {
            LastUse = use;
            return Values.TryGetValue(name, out var value) ? BoundSecretRead.Of(value) : BoundSecretRead.Unavailable(Problem ?? $"{name} is not held.");
        }

        public string? AuthorizeUse(string name, string? use) => Refusal;

        public void RecordProjectRefusal(string name, string? use, string problem) => ProjectRefusals.Add((name, use));
    }

    // Keeps the values in clear, so that the tests need no protected store of the machine.
    internal sealed class PlainProtector : ISecretProtector
    {
        public string Kind => SecretProtectionKinds.File;

        public byte[] Protect(ReadOnlySpan<byte> value) => value.ToArray();

        public byte[] Unprotect(ReadOnlySpan<byte> protectedValue) => protectedValue.ToArray();
    }
}
