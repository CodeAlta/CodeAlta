namespace CodeAlta.Sandbox.Tests;

// The fleet is never a way out of a sandbox (doc/fleet/security-and-privacy.md, "Remote execution", rule 8): a profile
// says whether its sessions reach other machines, and what it confines is the floor of what they start or drive there.
[TestClass]
public sealed class SandboxConfinementTests
{
    [TestMethod]
    public void TheFleet_IsAllowedByWorkspace_RefusedByStrict_AndByAProfileWithoutBase_AndChangedByAProfile()
    {
        Assert.IsTrue(SandboxProfiles.Resolve(SandboxProfiles.Workspace, []).Fleet);
        Assert.IsFalse(SandboxProfiles.Resolve(SandboxProfiles.Strict, []).Fleet);
        Assert.IsFalse(SandboxProfiles.Resolve("bare", [new SandboxProfile { Name = "bare" }]).Fleet, "a profile without base starts from nothing");
        Assert.IsTrue(SandboxProfiles.Resolve("mine", [new SandboxProfile { Name = "mine", Extends = "workspace" }]).Fleet, "a base gives its value");
        Assert.IsFalse(SandboxProfiles.Resolve("mine", [new SandboxProfile { Name = "mine", Extends = "workspace", Fleet = SandboxFleet.Never }]).Fleet);
        Assert.IsTrue(SandboxProfiles.Resolve("strict", [new SandboxProfile { Name = "strict", Fleet = SandboxFleet.Allow }]).Fleet, "the user may allow it for strict");
    }

    [TestMethod]
    public void AConfinementIsWeaker_WhenItLetsASessionDoAnythingItsFloorDoesNot()
    {
        var workspace = SandboxConfinement.Of(SandboxProfiles.Resolve(SandboxProfiles.Workspace, []));
        var strict = SandboxConfinement.Of(SandboxProfiles.Resolve(SandboxProfiles.Strict, []));

        Assert.IsNull(workspace.WeakerThan(workspace), "the same profile is as strict");
        Assert.IsNull(strict.WeakerThan(workspace), "strict is stricter than workspace");
        StringAssert.Contains(workspace.WeakerThan(strict), "network");
        foreach (var (wider, floor, why) in new (SandboxConfinement, SandboxConfinement, string)[]
        {
            (workspace with { Network = SandboxNetwork.On }, workspace, "network"),
            (workspace with { Hosts = [.. workspace.Hosts, "github.com"] }, workspace, "github.com"),
            (workspace with { LocalPorts = [5432] }, workspace, "5432"),
            (workspace with { Write = ["~/secrets"] }, workspace, "~/secrets"),
            (workspace with { Read = ["~/.ssh"] }, workspace, "~/.ssh"),
            (workspace with { Env = ["GITHUB_TOKEN"] }, workspace, "GITHUB_TOKEN"),
            (workspace with { McpServers = ["github"] }, workspace, "github"),
            (strict with { Outside = SandboxOutside.Ask }, strict, "outside"),
            (strict with { Fleet = true }, strict, "other machines"),
        })
        {
            StringAssert.Contains(wider.WeakerThan(floor), why);
        }

        var floorWithDeny = workspace with { Deny = ["~/.aws"], Read = ["~/code"], Write = [@"C:\work\"] };
        StringAssert.Contains(workspace.WeakerThan(floorWithDeny), "~/.aws", "a folder the floor denies stays denied");
        Assert.IsNull((floorWithDeny with { Read = ["C:/work"] }).WeakerThan(floorWithDeny), "a folder written by the floor can be read, whatever its separators");
        Assert.IsNull((workspace with { Hosts = ["api.nuget.org", "*.cdn.nuget.org"] }).WeakerThan(workspace), "a host under a domain of the floor is as strict");
        StringAssert.Contains((workspace with { Hosts = ["nuget.org.evil.com"] }).WeakerThan(workspace), "nuget.org.evil.com");
        Assert.IsNull((workspace with { Network = SandboxNetwork.Hosts, Hosts = ["github.com"] }).WeakerThan(workspace with { Network = SandboxNetwork.On }),
            "some hosts are less than the internet");
    }

    // A floor crosses machines whose file systems may tell case apart (Linux) or not: folders that differ only by case are
    // taken as different ones, which refuses rather than gives what the floor does not.
    [TestMethod]
    public void FoldersThatDifferOnlyByCase_AreDifferentFolders_InTheStricterDirection()
    {
        var workspace = SandboxConfinement.Of(SandboxProfiles.Resolve(SandboxProfiles.Workspace, []));
        var floor = workspace with { Read = ["/srv/data"], Write = ["/srv/out"], Deny = ["/srv/data/secret"] };

        StringAssert.Contains((floor with { Deny = ["/srv/data/Secret"] }).WeakerThan(floor), "/srv/data/secret", "the folder the floor denies is not denied");
        StringAssert.Contains((floor with { Read = ["/srv/Data"] }).WeakerThan(floor), "/srv/Data", "a folder the floor does not read");
        StringAssert.Contains((floor with { Write = ["/srv/Out"] }).WeakerThan(floor), "/srv/Out", "a folder the floor does not write");
        Assert.IsNull((floor with { Deny = ["/srv/data/secret", "/srv/data/Secret"] }).WeakerThan(floor), "denying both is as strict");
        Assert.IsNull(floor.WeakerThan(floor), "the same folders, as written, are as strict");
    }
}
