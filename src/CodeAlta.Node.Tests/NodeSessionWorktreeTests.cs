using CodeAlta.Catalog.Worktrees;
using CodeAlta.Fleet.Identity;
using CodeAlta.Fleet.NodeApi;

namespace CodeAlta.Node.Tests;

/// <summary>
/// The folder a session started on a node works in (<c>doc/fleet/node.md</c>, "Workspaces"): a worktree of its own in a git
/// repository, so that two sessions of the machine never share a checkout; the folder of the project otherwise.
/// </summary>
[TestClass]
public sealed class NodeSessionWorktreeTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task TwoSessionsStartedOnTheSameProject_WorkInWorktreesOfTheirOwn_AndTheCheckoutStaysAsItIs()
    {
        var token = TestContext.CancellationToken;
        await using var fixture = await MoveFixture.StartAsync(token);
        await using var client = await fixture.ConnectAsync(DevicePermissions.View | DevicePermissions.Drive | DevicePermissions.Start, token);
        var runtime = fixture.Node.Host.RuntimeService;

        var first = await client.CreateSessionAsync(new FleetCreateSessionParams(fixture.NodeProjectId, Title: "First"), cancellationToken: token);
        var second = await client.CreateSessionAsync(new FleetCreateSessionParams(fixture.NodeProjectId, Title: "Second"), cancellationToken: token);
        var chat = await client.CreateSessionAsync(new FleetCreateSessionParams(Title: "A chat"), cancellationToken: token);

        var firstFolder = (await runtime.TryGetRecoverableSessionAsync(first.SessionId, token))!.WorktreeDirectory;
        var secondFolder = (await runtime.TryGetRecoverableSessionAsync(second.SessionId, token))!.WorktreeDirectory;
        Assert.IsNotNull(firstFolder, "a session of a git project works in a worktree");
        Assert.IsNotNull(secondFolder);
        Assert.AreNotEqual(firstFolder, secondFolder, "two sessions never share their folder");
        foreach (var folder in new[] { firstFolder, secondFolder })
        {
            Assert.IsTrue(Directory.Exists(folder), folder);
            Assert.IsFalse(GitWorktreeService.IsWithin(folder, fixture.NodeProject), "the worktree is not inside the checkout");
            Assert.IsTrue(GitWorktreeService.SameRepository(fixture.NodeProject, folder), "it is a checkout of the same repository");
            StringAssert.StartsWith(await GitAsync(folder, "rev-parse", "--abbrev-ref", "HEAD"), GitWorktreeService.BranchPrefix, "on a branch of its own");
        }

        Assert.AreEqual("main", await GitAsync(fixture.NodeProject, "rev-parse", "--abbrev-ref", "HEAD"), "the checkout of the project keeps its branch");
        Assert.IsNull((await runtime.TryGetRecoverableSessionAsync(chat.SessionId, token))!.WorktreeDirectory, "a chat has no project to check out");
    }

    [TestMethod]
    public async Task AProjectInNoRepository_IsWorkedInWhereItIs()
    {
        using var root = new NodeTestRoot();
        await using var node = await NodeHost.StartAsync(root.Options(), TestContext.CancellationToken);
        var worktrees = new FleetSessionWorktrees(node.Host.CatalogOptions);

        Assert.AreEqual(FleetSessionWorktree.None, await worktrees.PrepareAsync(node.Host.CurrentProject, TestContext.CancellationToken));
        Assert.AreEqual(FleetSessionWorktree.None, await worktrees.PrepareAsync(null, TestContext.CancellationToken));
    }

    private async Task<string> GitAsync(string folder, params string[] arguments)
    {
        var run = await GitWorktreeService.RunGitAsync(folder, arguments, GitWorktreeService.ReadTimeout, TestContext.CancellationToken);
        Assert.IsTrue(run.Succeeded, run.Error);
        return run.Output.Trim();
    }
}
