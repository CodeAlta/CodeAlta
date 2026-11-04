using CodeAlta.Catalog.Secrets;

namespace CodeAlta.Catalog.Tests;

/// <summary>
/// The values the fleet gives a machine, as the machine keeps and resolves them (<c>doc/fleet/secrets.md</c>, how a run
/// gets them): protected as the machine's own values, one set per server, resolved by scope, the narrowest first after the
/// machine's own store, never a host-only one to the agent's shell, masked, and each use recorded once per run and version.
/// </summary>
[TestClass]
public sealed class FleetSecretCopiesTests
{
    [TestMethod]
    public void TheCopiesOfAServer_AreProtected_ReplacedAsAWhole_AndTakenAwayWithTheirLink()
    {
        using var root = new TempRoot();
        var store = FileStore(root.Path);
        Assert.IsTrue(store.ReplaceFleetCopies("link-a", [Value("link-a", "GRAFANA_TOKEN", "glsa_person", FleetSecretScopes.Person, null)]));
        Assert.IsFalse(store.ReplaceFleetCopies("link-a", [Value("link-a", "GRAFANA_TOKEN", "glsa_person", FleetSecretScopes.Person, null)]), "the same versions change nothing");
        store.ReplaceFleetCopies("link-b", [Value("link-b", "OTHER", "other-value", FleetSecretScopes.Person, null)]);
        Assert.IsFalse(File.ReadAllText(Path.Combine(root.Path, "secrets", "store.json")).Contains("glsa_person", StringComparison.Ordinal), "a value is never in the file in clear");

        // A rotation replaces the set of its server; the other server's set stays.
        store.ReplaceFleetCopies("link-a", [Value("link-a", "GRAFANA_TOKEN", "glsa_rotated", FleetSecretScopes.Person, null, version: 2)]);
        CollectionAssert.AreEqual(new[] { ("GRAFANA_TOKEN", 2, "link-a"), ("OTHER", 1, "link-b") },
            store.ListFleetCopies().Select(static copy => (copy.Name, copy.Version, copy.Source)).ToArray());
        Assert.AreEqual("glsa_rotated", SecretReferenceResolver.ForMachine(root.Path).Get(new SecretReference(SecretReferenceKind.Secret, "GRAFANA_TOKEN")));
        Assert.IsTrue(store.GetSecretValues().Contains("glsa_rotated"), "a value of the fleet is masked as the machine's own are");
        Assert.IsFalse(store.GetSecretValues().Contains("glsa_person"), "an old version is no longer masked once it is gone");

        Assert.IsTrue(store.RemoveFleetCopies("link-a"));
        Assert.IsFalse(SecretReferenceResolver.ForMachine(root.Path).TryGet(new SecretReference(SecretReferenceKind.Secret, "GRAFANA_TOKEN"), out _));
        Assert.AreEqual("OTHER", store.ListFleetCopies().Single().Name);
        Assert.AreEqual(0, store.ListSecrets().Count, "the fleet's values are not the machine's own");
    }

    [TestMethod]
    public void AReference_ResolvesTheMachineFirst_ThenTheNarrowestScopeOfTheRun()
    {
        using var root = new TempRoot();
        var store = FileStore(root.Path);
        store.ReplaceFleetCopies("link", [
            Value("link", "TOKEN", "person", FleetSecretScopes.Person, null),
            Value("link", "TOKEN", "space", FleetSecretScopes.Space, "work"),
            Value("link", "TOKEN", "project", FleetSecretScopes.Project, "p1"),
            Value("link", "TOKEN", "environment", FleetSecretScopes.Environment, "production"),
            Value("link", "URL", "https://grafana.example", FleetSecretScopes.Space, "work", kind: FleetSecretValue.VariableKind),
        ]);
        string Resolve(SecretScope scope) => SecretReferenceResolver.ForScope(root.Path, scope, shell: true).Resolve("${secret:TOKEN}");

        Assert.AreEqual("environment", Resolve(new SecretScope("work", "p1", "production")));
        Assert.AreEqual("project", Resolve(new SecretScope("work", "p1", null)));
        Assert.AreEqual("space", Resolve(new SecretScope("work", "p2", null)));
        Assert.AreEqual("person", Resolve(new SecretScope("home", null, null)));
        Assert.AreEqual("person", Resolve(SecretScope.None));
        Assert.AreEqual("https://grafana.example", SecretReferenceResolver.ForScope(root.Path, new SecretScope("work", null, null), shell: true).Resolve("${var:URL}"));
        Assert.IsFalse(store.GetSecretValues().Contains("https://grafana.example"), "a variable is not secret");

        // The machine's own value comes first for the work of its owner, and not for the work of another.
        store.SetSecret("TOKEN", "machine", "alex (tests)");
        Assert.AreEqual("machine", Resolve(new SecretScope("work", "p1", "production")));
        Assert.AreEqual("environment", SecretReferenceResolver.ForScope(root.Path, new SecretScope("work", "p1", "production") { OwnerWork = false }, shell: true)
            .Resolve("${secret:TOKEN}"));
        // The work of another person gets none of the owner's values: not those of the owner's person, spaces or projects.
        Assert.IsFalse(SecretReferenceResolver.ForScope(root.Path, new SecretScope("work", "p1", null) { OwnerWork = false }, shell: true)
            .TryGet(new SecretReference(SecretReferenceKind.Secret, "TOKEN"), out _));
    }

    [TestMethod]
    public void AHostOnlySecret_GoesToHostSideTools_NeverToTheShell()
    {
        using var root = new TempRoot();
        var store = FileStore(root.Path);
        store.ReplaceFleetCopies("link", [Value("link", "DEPLOY_KEY", "host-only-value", FleetSecretScopes.Project, "p1", hostOnly: true),
            Value("link", "DEPLOY_KEY", "wider", FleetSecretScopes.Person, null)]);
        var scope = new SecretScope(null, "p1", null);

        Assert.AreEqual("host-only-value", SecretReferenceResolver.ForScope(root.Path, scope, shell: false).Resolve("${secret:DEPLOY_KEY}"));
        var refused = Assert.ThrowsExactly<SecretReferenceException>(() => SecretReferenceResolver.ForScope(root.Path, scope, shell: true).Resolve("${secret:DEPLOY_KEY}"));
        StringAssert.Contains(refused.Message, "never to the agent's shell");
        Assert.IsNull(SecretReferenceResolver.ForScope(root.Path, scope, shell: true).Read("secret:DEPLOY_KEY"), "not the value of a wider scope either");
        Assert.IsTrue(store.GetSecretValues().Contains("host-only-value"), "masked all the same");
    }

    [TestMethod]
    public void EachUse_IsRecordedOncePerRunAndVersion_AndLeavesTheLogOncePublished()
    {
        using var root = new TempRoot();
        var store = FileStore(root.Path);
        store.ReplaceFleetCopies("link", [Value("link", "TOKEN", "value", FleetSecretScopes.Person, null)]);
        var log = new SecretUseLog(root.Path);
        var resolver = SecretReferenceResolver.ForScope(root.Path, new SecretScope(null, null, null), shell: true, used => log.Record(used, "run-1"));
        resolver.Resolve("${secret:TOKEN}");
        resolver.Resolve("${secret:TOKEN} again");
        SecretReferenceResolver.ForScope(root.Path, SecretScope.None, shell: true, used => log.Record(used, "run-2")).Resolve("${secret:TOKEN}");

        var uses = log.Read();
        CollectionAssert.AreEqual(new[] { ("TOKEN", 1, "run-1", "link"), ("TOKEN", 1, "run-2", "link") }, uses.Select(static use => (use.Name, use.Version, use.RunId, use.Source)).ToArray());
        Assert.IsFalse(File.ReadAllText(log.FilePath).Contains("value\"", StringComparison.Ordinal), "a use never holds the value");

        Assert.AreEqual(0, log.PublishAsync("link", 10, static _ => Task.FromResult(false)).GetAwaiter().GetResult().Count, "a refused publication keeps them");
        Assert.AreEqual(2, log.Read().Count);
        Assert.AreEqual(2, log.PublishAsync("link", 10, static _ => Task.FromResult(true)).GetAwaiter().GetResult().Count);
        Assert.AreEqual(0, log.Read().Count);
    }

    private static FleetSecretValue Value(string source, string name, string value, string scope, string? scopeId, int version = 1, bool hostOnly = false,
        string kind = FleetSecretValue.SecretKind)
        => new(source, "id-" + name + "-" + scope, kind, name, scope, scopeId, version, hostOnly, value);

    private sealed class TempRoot : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("alta-fleet-copies-").FullName;

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static LocalSecretStore FileStore(string root)
        => new(root, new MasterKeySecretProtector(new FileSecretKeyStore(Path.Combine(root, "secrets", "key"))));
}
