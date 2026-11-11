using CodeAlta.Agent.Runtime;
using CodeAlta.Catalog.Secrets;
using CodeAlta.Orchestration.Hosting;

namespace CodeAlta.Orchestration.Tests;

[TestClass]
public sealed class ShellBoundSecretsTests
{
    [TestMethod]
    public void TheValuesOfVaultsGivenToShells_AreFollowed_SoThatNoSandboxGetsThem()
    {
        var shells = new CodeAltaHost.ShellBoundSecrets(new Vault());
        var given = shells.Given;
        Assert.AreEqual(0, given.Count);

        Assert.AreEqual("vault-deploy-value", shells.ReadBound("DEPLOY_KEY", "shell of the session s1").Value);
        Assert.IsNull(shells.ReadBound("LOCKED", "shell of the session s1").Value);

        // The collection the sandbox reads follows what is given after it was taken.
        CollectionAssert.AreEquivalent(new[] { "vault-deploy-value" }, given.ToArray());
    }

    [TestMethod]
    public void TheShellOfEachSession_IsAUseOfItsOwn()
    {
        var first = CodeAltaHost.ShellUseOf(new SecretScope(null, "p1", null) { RunId = "s1" });
        var second = CodeAltaHost.ShellUseOf(new SecretScope(null, "p1", null) { RunId = "s2" });

        Assert.AreEqual("shell of the session s1", first);
        Assert.AreNotEqual(first, second, "a confirmation for the shell of one session is not one for another");
    }

    private sealed class Vault : IBoundSecretSource
    {
        public BoundSecretRead ReadBound(string name, string? use)
            => name == "DEPLOY_KEY" ? BoundSecretRead.Of("vault-deploy-value") : BoundSecretRead.Unavailable($"{name} is locked.");

        public string? AuthorizeUse(string name, string? use) => null;
    }
}
