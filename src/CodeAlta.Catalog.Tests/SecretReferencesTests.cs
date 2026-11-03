using System.Diagnostics.CodeAnalysis;
using CodeAlta.Catalog.Secrets;

namespace CodeAlta.Catalog.Tests;

[TestClass]
public sealed class SecretReferencesTests
{
    [TestMethod]
    public void References_AreEnvSecretAndVar_OfValidNames()
    {
        CollectionAssert.AreEqual(
            new[]
            {
                new SecretReference(SecretReferenceKind.Environment, "HOME"),
                new SecretReference(SecretReferenceKind.Secret, "GRAFANA_TOKEN"),
                new SecretReference(SecretReferenceKind.Variable, "GRAFANA_URL"),
            },
            SecretReferences.Find("${env:HOME}/x ${secret:GRAFANA_TOKEN} ${var:GRAFANA_URL} ${input:x} ${secret:bad-name} ${HOME}").ToArray());
        Assert.IsTrue(SecretReferences.TryParseLone("${secret:OPENAI_API_KEY}", out var lone));
        Assert.AreEqual("${secret:OPENAI_API_KEY}", lone.ToString());
        Assert.IsFalse(SecretReferences.TryParseLone("Bearer ${secret:TOKEN}", out _));
        Assert.IsFalse(SecretReferences.TryParseLone("${secret:A}${secret:B}", out _));
    }

    [TestMethod]
    public void Resolve_GivesEachValue_FromItsSource()
    {
        var resolver = new SecretReferenceResolver(Environment(("HOME", "/home/alex")), [new Lookup("machine", secrets: [("TOKEN", "s3cr3t")], variables: [("URL", "https://grafana.example")])]);

        Assert.AreEqual("Bearer s3cr3t at https://grafana.example for /home/alex", resolver.Resolve("Bearer ${secret:TOKEN} at ${var:URL} for ${env:HOME}"));
        Assert.AreEqual("no reference ${HOME} ${unknown:X}", resolver.Resolve("no reference ${HOME} ${unknown:X}"), "What is no reference stays as it is written.");
    }

    [TestMethod]
    public void AMissingValue_IsAnErrorThatNamesIt_NeverAnEmptyValue()
    {
        var resolver = new SecretReferenceResolver(Environment(), [new Lookup("machine")]);

        var error = Assert.ThrowsExactly<SecretReferenceException>(() => resolver.Resolve("Bearer ${secret:GRAFANA_TOKEN}"));
        Assert.AreEqual("The secret GRAFANA_TOKEN is not set on this machine.", error.Message);
        Assert.AreEqual(new SecretReference(SecretReferenceKind.Secret, "GRAFANA_TOKEN"), error.Reference);
        Assert.IsFalse(resolver.TryResolve("${var:GRAFANA_URL}", out _, out var problem));
        Assert.AreEqual("The variable GRAFANA_URL is not set on this machine.", problem);
        Assert.IsFalse(resolver.TryResolve("${env:NOPE}", out _, out problem));
        Assert.AreEqual("The environment variable NOPE is not set.", problem);
    }

    [TestMethod]
    public void TheNarrowestSourceWins_AndASecretNeverComesFromTheEnvironment()
    {
        var resolver = new SecretReferenceResolver(Environment(("TOKEN", "from-env")),
            [new Lookup("project", secrets: [("TOKEN", "from-project")]), new Lookup("machine", secrets: [("TOKEN", "from-machine"), ("OTHER", "machine-only")])]);

        Assert.AreEqual("from-project", resolver.Resolve("${secret:TOKEN}"));
        Assert.AreEqual("machine-only", resolver.Resolve("${secret:OTHER}"));
        Assert.AreEqual("from-env", resolver.Resolve("${env:TOKEN}"));
        Assert.ThrowsExactly<SecretReferenceException>(() => new SecretReferenceResolver(Environment(("TOKEN", "from-env")), []).Resolve("${secret:TOKEN}"));
    }

    [TestMethod]
    public void ASecretThatCannotBeReadBack_IsAnErrorThatNamesIt()
    {
        var resolver = new SecretReferenceResolver(Environment(), [new Lookup("machine", failing: true)]);

        var error = Assert.ThrowsExactly<SecretReferenceException>(() => resolver.Resolve("${secret:TOKEN}"));
        StringAssert.StartsWith(error.Message, "${secret:TOKEN} cannot be read");
        Assert.IsNull(resolver.Read("secret:TOKEN"), "A reader of variables leaves it out.");
    }

    [TestMethod]
    public void Read_TakesTheKeysOfAReaderOfVariables()
    {
        var resolver = new SecretReferenceResolver(Environment(("HOME", "/home/alex")), [new Lookup("machine", secrets: [("TOKEN", "s")], variables: [("URL", "u")])]);

        Assert.AreEqual("/home/alex", resolver.Read("HOME"));
        Assert.AreEqual("/home/alex", resolver.Read("env:HOME"));
        Assert.AreEqual("s", resolver.Read("secret:TOKEN"));
        Assert.AreEqual("u", resolver.Read("var:URL"));
        Assert.IsNull(resolver.Read("input:URL"));
        Assert.IsNull(resolver.Read("secret:NOPE"));
    }

    [TestMethod]
    public void ACliProfile_TakesSecretsOfTheMachine_ForAVariableThatHoldsASecret()
    {
        Assert.IsNull(SpaceCliProfile.VariableProblem("VAULT_TOKEN", "${secret:CONTOSO_VAULT}"));
        Assert.IsNull(SpaceCliProfile.VariableProblem("VAULT_TOKEN", "${env:CONTOSO_VAULT}"));
        StringAssert.Contains(SpaceCliProfile.VariableProblem("VAULT_TOKEN", "${var:CONTOSO_VAULT}"), "holds a secret", "A variable of the store is not secret.");
        StringAssert.Contains(SpaceCliProfile.VariableProblem("VAULT_TOKEN", "hunter2"), "${secret:NAME}");
        Assert.IsNull(SpaceCliProfile.VariableProblem("KUBECONFIG", "~/.kube/${var:CLUSTER}"));
        StringAssert.Contains(SpaceCliProfile.VariableProblem("KUBECONFIG", "${secret:bad-name}"), "${secret:NAME}");

        var resolver = new SecretReferenceResolver(Environment(("USER", "alex")), [new Lookup("machine", secrets: [("VAULT", "v4ult")], variables: [("CLUSTER", "dev")])]);
        Assert.AreEqual("v4ult", SpaceCliProfile.Resolve("${secret:VAULT}", resolver.Read, "/home/alex", windows: false, out var missing));
        Assert.IsNull(missing);
        Assert.AreEqual("/home/alex/.kube/dev-alex", SpaceCliProfile.Resolve("~/.kube/${var:CLUSTER}-${env:USER}", resolver.Read, "/home/alex", windows: false, out _));
        Assert.IsNull(SpaceCliProfile.Resolve("${secret:MISSING}", resolver.Read, "/home/alex", windows: false, out missing), "Never half a value.");
        Assert.AreEqual("${secret:MISSING}", missing);
        Assert.IsNull(SpaceCliProfile.Resolve("${env:UNSET}", resolver.Read, "/home/alex", windows: false, out missing));
        Assert.AreEqual("UNSET", missing);
    }

    [TestMethod]
    public void AProvider_TakesItsKeyFromASecret()
    {
        var root = Path.Combine(Path.GetTempPath(), "codealta-secrets-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "config.toml"), """
                [providers.work]
                enabled = true
                type = "openai-responses"
                api_key_secret = "WORK_OPENAI_KEY"
                api_key_env = "WORK_OPENAI_KEY"
                """);
            var definition = new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = root }).LoadGlobalProviderDefinitions().Single(static provider => provider.ProviderKey == "work");

            Assert.AreEqual("WORK_OPENAI_KEY", definition.ApiKeySecret, "api_key_secret alone gives a provider its key: it needs no api_key nor api_key_env.");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static Func<string, string?> Environment(params (string Name, string Value)[] variables)
        => name => variables.FirstOrDefault(variable => variable.Name == name).Value;

    private sealed class Lookup(string scope, (string, string)[]? secrets = null, (string, string)[]? variables = null, bool failing = false) : ISecretLookup
    {
        public string Scope => scope;

        public bool TryGetSecret(string name, [NotNullWhen(true)] out string? value)
        {
            if (failing) throw new SecretStoreUnavailableException("the keyring is locked.");
            value = secrets?.FirstOrDefault(secret => secret.Item1 == name).Item2;
            return value is not null;
        }

        public bool TryGetVariable(string name, [NotNullWhen(true)] out string? value)
        {
            value = variables?.FirstOrDefault(variable => variable.Item1 == name).Item2;
            return value is not null;
        }

        public IReadOnlyCollection<string> GetSecretValues() => [.. (secrets ?? []).Select(static secret => secret.Item2)];
    }
}
