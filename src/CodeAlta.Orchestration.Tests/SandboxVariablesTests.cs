using System.Diagnostics.CodeAnalysis;
using CodeAlta.Catalog.Secrets;
using CodeAlta.Orchestration.Runtime;
using CodeAlta.Sandbox;

namespace CodeAlta.Orchestration.Tests;

// The variables a sandbox profile sets (`env = ["NAME=value"]`), whose values name secrets and variables as the rest of
// the configuration does.
[TestClass]
public sealed class SandboxVariablesTests
{
    [TestMethod]
    public void TheVariablesAProfileSets_GetTheValuesOfTheirReferences_AndTheOthersStayAsWritten()
    {
        var profile = Profile("GH_TOKEN", "TOOL_MODE=ci", "NPM_TOKEN=${secret:NPM_TOKEN}", "FEED=https://${var:FEED_HOST}/v3?key=${secret:NPM_TOKEN}", "FROM_HOST=${env:HOST_VALUE}",
            "NOT_A_REFERENCE=${HOME}/bin");
        var issued = new List<string>();

        var (resolved, problem) = SessionRuntimeService.ResolveSandboxVariables(profile, Resolver(issued));

        Assert.IsNull(problem);
        CollectionAssert.AreEqual(new[]
        {
            "GH_TOKEN", "TOOL_MODE=ci", "NPM_TOKEN=npm-value", "FEED=https://feed.example.com/v3?key=npm-value", "FROM_HOST=host-value", "NOT_A_REFERENCE=${HOME}/bin",
        }, resolved.Env.ToArray());
        CollectionAssert.Contains(issued, "npm-value", "a secret is masked before the sandbox gets it");
        CollectionAssert.DoesNotContain(issued, "feed.example.com", "a variable of the store is no secret");
        CollectionAssert.AreEqual(profile.Env.ToArray(), Profile("GH_TOKEN", "TOOL_MODE=ci", "NPM_TOKEN=${secret:NPM_TOKEN}", "FEED=https://${var:FEED_HOST}/v3?key=${secret:NPM_TOKEN}",
            "FROM_HOST=${env:HOST_VALUE}", "NOT_A_REFERENCE=${HOME}/bin").Env.ToArray(), "the profile keeps what is written");
    }

    [TestMethod]
    public void AVariableWhoseSecretIsNotSet_IsLeftOut_AndNamedAsTheProblem()
    {
        var profile = Profile("TOOL_MODE=ci", "DEPLOY_KEY=${secret:MISSING}", "NPM_TOKEN=${secret:NPM_TOKEN}");

        var (resolved, problem) = SessionRuntimeService.ResolveSandboxVariables(profile, Resolver([]));

        CollectionAssert.AreEqual(new[] { "TOOL_MODE=ci", "NPM_TOKEN=npm-value" }, resolved.Env.ToArray(), "never the reference as a value, nor an empty one");
        Assert.AreEqual("The sandbox profile 'env' sets the variable DEPLOY_KEY, which has no value: The secret MISSING is not set on this machine.", problem);
    }

    [TestMethod]
    public void AVariableSetAgainByAProfileThatExtendsAnother_DoesNotReadTheSecretOfItsBase()
    {
        var profile = Profile("NPM_TOKEN=${secret:MISSING}", "OTHER=${secret:NPM_TOKEN}", "NPM_TOKEN=${secret:NPM_TOKEN}");

        var (resolved, problem) = SessionRuntimeService.ResolveSandboxVariables(profile, Resolver([]));

        Assert.IsNull(problem, "the secret of the entry that was replaced may be missing");
        CollectionAssert.AreEqual(new[] { "OTHER=npm-value", "NPM_TOKEN=npm-value" }, resolved.Env.ToArray());
    }

    [TestMethod]
    public void AProfileThatSetsNoReference_IsTheSameProfile()
    {
        var profile = Profile("GH_TOKEN", "TOOL_MODE=ci");

        var (resolved, problem) = SessionRuntimeService.ResolveSandboxVariables(profile, Resolver([]));

        Assert.AreSame(profile, resolved);
        Assert.IsNull(problem);
    }

    private static ResolvedSandboxProfile Profile(params string[] env)
        => SandboxProfiles.Resolve("env", [new SandboxProfile { Name = "env", Extends = "workspace", Env = env }]);

    private static SecretReferenceResolver Resolver(List<string> issued)
        => new(name => name == "HOST_VALUE" ? "host-value" : null, [new Store()]) { Issued = issued.Add };

    private sealed class Store : ISecretLookup
    {
        public string Scope => "machine";

        public bool TryGetSecret(string name, [NotNullWhen(true)] out string? value)
        {
            value = name == "NPM_TOKEN" ? "npm-value" : null;
            return value is not null;
        }

        public bool TryGetVariable(string name, [NotNullWhen(true)] out string? value)
        {
            value = name == "FEED_HOST" ? "feed.example.com" : null;
            return value is not null;
        }

        public IReadOnlyCollection<string> GetSecretValues() => ["npm-value"];
    }
}
