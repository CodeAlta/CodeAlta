namespace CodeAlta.Sandbox.Tests;

[TestClass]
public sealed class SandboxProfileTests
{
    [TestMethod]
    public void BuiltIns_WorkspaceReachesRegistriesAndAsks_StrictHasNoNetworkAndNeverAsks()
    {
        var workspace = SandboxProfiles.Resolve(SandboxProfiles.Workspace, []);
        var strict = SandboxProfiles.Resolve(SandboxProfiles.Strict, []);

        Assert.AreEqual(SandboxNetwork.Hosts, workspace.Network);
        CollectionAssert.Contains(workspace.Hosts.ToList(), "*.nuget.org");
        CollectionAssert.Contains(workspace.Hosts.ToList(), "registry.npmjs.org");
        Assert.IsFalse(workspace.Hosts.Any(static host => host.Contains("github", StringComparison.OrdinalIgnoreCase)), "git hosts are added by the user");
        Assert.AreEqual(SandboxOutside.Ask, workspace.Outside);
        Assert.AreEqual(SandboxNetwork.Off, strict.Network);
        Assert.AreEqual(SandboxOutside.Never, strict.Outside);
    }

    [TestMethod]
    public void ATableNamedLikeABuiltIn_ReplacesTheKeysItSets()
    {
        var resolved = SandboxProfiles.Resolve("workspace", [new SandboxProfile { Name = "workspace", Hosts = ["nuget.org"], Outside = SandboxOutside.Never }]);

        CollectionAssert.AreEqual(new[] { "nuget.org" }, resolved.Hosts.ToArray());
        Assert.AreEqual(SandboxOutside.Never, resolved.Outside);
        Assert.AreEqual(SandboxNetwork.Hosts, resolved.Network, "a key it does not set keeps the built-in value");
    }

    [TestMethod]
    public void Extends_AddsListsAndReplacesTheOtherKeys()
    {
        var resolved = SandboxProfiles.Resolve("lib-feed",
        [
            new SandboxProfile { Name = "lib-feed", Extends = "workspace", Read = ["~/code/XenoAtom"], Hosts = ["pkgs.dev.azure.com"], LocalPorts = [5432] },
        ]);

        Assert.AreEqual("lib-feed", resolved.Name);
        CollectionAssert.Contains(resolved.Hosts.ToList(), "*.nuget.org");
        CollectionAssert.Contains(resolved.Hosts.ToList(), "pkgs.dev.azure.com");
        CollectionAssert.AreEqual(new[] { "~/code/XenoAtom" }, resolved.Read.ToArray());
        CollectionAssert.AreEqual(new[] { 5432 }, resolved.LocalPorts.ToArray());
        Assert.AreEqual(SandboxOutside.Ask, resolved.Outside);
    }

    [TestMethod]
    public void AProfileWithoutBase_StartsWithNothing()
    {
        var resolved = SandboxProfiles.Resolve("bare", [new SandboxProfile { Name = "bare" }]);

        Assert.AreEqual(SandboxNetwork.Off, resolved.Network);
        Assert.IsEmpty(resolved.Hosts);
        Assert.AreEqual(SandboxOutside.Ask, resolved.Outside);
    }

    [TestMethod]
    public void UnknownLoopingOrInvalidProfiles_AreRefusedWithTheirNames()
    {
        Assert.Contains("'nope'", Assert.ThrowsExactly<SandboxProfileException>(() => SandboxProfiles.Resolve("nope", [])).Message);
        Assert.Contains("extends itself", Assert.ThrowsExactly<SandboxProfileException>(() => SandboxProfiles.Resolve("a",
            [new SandboxProfile { Name = "a", Extends = "b" }, new SandboxProfile { Name = "b", Extends = "a" }])).Message);
        Assert.Contains("does not exist", Assert.ThrowsExactly<SandboxProfileException>(() => SandboxProfiles.Resolve("a", [new SandboxProfile { Name = "a", Extends = "missing" }])).Message);
        Assert.Contains("'*'", Assert.ThrowsExactly<SandboxProfileException>(() => SandboxProfiles.Resolve("a", [new SandboxProfile { Name = "a", Hosts = ["*"] }])).Message);
        Assert.Contains("port", Assert.ThrowsExactly<SandboxProfileException>(() => SandboxProfiles.Resolve("a", [new SandboxProfile { Name = "a", LocalPorts = [70000] }])).Message);
        Assert.ThrowsExactly<SandboxProfileException>(() => SandboxProfiles.Resolve("off", [new SandboxProfile { Name = "off" }]));
    }

    [TestMethod]
    public void HostPatterns_MatchNamesAndSubdomainsOnly()
    {
        string[] patterns = ["nuget.org", "*.npmjs.org"];

        Assert.IsTrue(SandboxHostPattern.Matches("NuGet.org.", patterns));
        Assert.IsTrue(SandboxHostPattern.Matches("registry.npmjs.org", patterns));
        Assert.IsFalse(SandboxHostPattern.Matches("npmjs.org", patterns), "*. is for subdomains");
        Assert.IsFalse(SandboxHostPattern.Matches("api.nuget.org", patterns));
        Assert.IsFalse(SandboxHostPattern.Matches("evilnpmjs.org", patterns));
        Assert.IsFalse(SandboxHostPattern.IsValid("*"));
        Assert.IsFalse(SandboxHostPattern.IsValid("*.*.org"));
        Assert.IsFalse(SandboxHostPattern.IsValid("http://nuget.org"));
    }

    [TestMethod]
    public void Scopes_TheMostSpecificThatSaysSomethingDecides()
    {
        Assert.AreEqual(SandboxChoice.None, SandboxScopes.Resolve(new SandboxScopeValues()));
        Assert.AreEqual(new SandboxChoice("workspace", SandboxScope.AllSessions), SandboxScopes.Resolve(new SandboxScopeValues { AllSessions = "workspace" }));
        Assert.AreEqual(new SandboxChoice("strict", SandboxScope.Provider),
            SandboxScopes.Resolve(new SandboxScopeValues { Provider = "strict", Project = "lib-feed", Space = "workspace", AllSessions = "workspace" }));
        Assert.AreEqual(new SandboxChoice(null, SandboxScope.Project), SandboxScopes.Resolve(new SandboxScopeValues { Project = "off", AllSessions = "workspace" }));
        Assert.AreEqual(new SandboxChoice("workspace", SandboxScope.Model), SandboxScopes.Resolve(new SandboxScopeValues { Model = "workspace", Provider = "OFF" }));
        Assert.AreEqual(new SandboxChoice(null, SandboxScope.Session), SandboxScopes.Resolve(new SandboxScopeValues { Session = "off", Model = "strict" }));
    }
}
