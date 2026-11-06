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

    [TestMethod]
    public async Task TheWorktreesOfIdleSessions_Go_ButNotWhatIsUncommitted_NorACommitOnlyTheirBranchHas()
    {
        var token = TestContext.CancellationToken;
        await using var fixture = await MoveFixture.StartAsync(token);
        await using var client = await fixture.ConnectAsync(DevicePermissions.View | DevicePermissions.Drive | DevicePermissions.Start, token);
        var runtime = fixture.Node.Host.RuntimeService;
        async Task<(string Id, string Folder, string Branch)> StartAsync(string title)
        {
            var created = await client.CreateSessionAsync(new FleetCreateSessionParams(fixture.NodeProjectId, Title: title), cancellationToken: token);
            var folder = (await runtime.TryGetRecoverableSessionAsync(created.SessionId, token))!.WorktreeDirectory!;
            return (created.SessionId, folder, await GitAsync(folder, "rev-parse", "--abbrev-ref", "HEAD"));
        }

        var clean = await StartAsync("Clean");
        var dirty = await StartAsync("Dirty");
        var committed = await StartAsync("Committed");
        File.WriteAllText(Path.Combine(dirty.Folder, "draft.md"), "not committed\n");
        File.WriteAllText(Path.Combine(committed.Folder, "work.md"), "done\n");
        await GitAsync(committed.Folder, "add", "-A");
        await GitAsync(committed.Folder, "-c", "user.name=Test", "-c", "user.email=test@example.invalid", "commit", "-q", "-m", "Work");

        // A week later, nobody touched them.
        await using (var cleanup = new FleetSessionWorktreeCleanup(fixture.Node.Host, new Later(FleetSessionWorktreeCleanup.IdleTime + TimeSpan.FromHours(1))))
        {
            var usage = (await cleanup.RunOnceAsync(token))!;
            Assert.AreEqual((1, 2), (usage.Count, usage.Removed), "the clean and the committed ones go; the one with a draft stays");
            Assert.IsGreaterThan(0L, usage.Bytes);
            Assert.AreEqual(FleetSessionWorktreeCleanup.IdleTime.Days, usage.IdleDays);
        }

        Assert.IsFalse(Directory.Exists(clean.Folder));
        Assert.AreEqual("", await GitAsync(fixture.NodeProject, "branch", "--list", clean.Branch), "a branch with nothing of its own goes with its worktree");
        Assert.IsTrue(File.Exists(Path.Combine(dirty.Folder, "draft.md")), "what is not committed is never removed");
        Assert.IsFalse(Directory.Exists(committed.Folder));
        StringAssert.Contains(await GitAsync(fixture.NodeProject, "branch", "--list", committed.Branch), committed.Branch, "a commit only its branch has keeps the branch");

        // Given a prompt again, a session works in a new worktree, from the branch that kept its commits.
        Assert.AreEqual("accepted", (await client.SendAsync(committed.Id, "Go on.", cancellationToken: token)).Status);
        await fixture.WaitIdleOnNodeAsync(client, committed.Id, token);
        var revived = (await runtime.TryGetRecoverableSessionAsync(committed.Id, token))!.WorktreeDirectory!;
        Assert.AreNotEqual(committed.Folder, revived);
        Assert.AreEqual("done\n", File.ReadAllText(Path.Combine(revived, "work.md")).ReplaceLineEndings("\n"), "its work is there");
        Assert.AreEqual(revived, fixture.NodeProvider.LastWorkingDirectory);
        Assert.IsNull(fixture.Fleet.Service.WorktreeCleanup.KeptBranch(committed.Id), "the session is not taken for one whose worktree is gone");
    }

    [TestMethod]
    public async Task TheWorktreeOfADeletedSession_Goes_AtTheNextPass()
    {
        var token = TestContext.CancellationToken;
        await using var fixture = await MoveFixture.StartAsync(token);
        await using var client = await fixture.ConnectAsync(DevicePermissions.View | DevicePermissions.Drive | DevicePermissions.Start, token);
        var runtime = fixture.Node.Host.RuntimeService;
        var created = await client.CreateSessionAsync(new FleetCreateSessionParams(fixture.NodeProjectId, Title: "Short"), cancellationToken: token);
        var session = (await runtime.TryGetRecoverableSessionAsync(created.SessionId, token))!;
        var cleanup = fixture.Fleet.Service.WorktreeCleanup;

        Assert.AreEqual((1, 0), (await cleanup.RunOnceAsync(token)) is { } seen ? (seen.Count, seen.Removed) : default, "a session in use keeps its worktree");
        Assert.IsTrue(await runtime.DeleteSessionAsync(session, token));
        Assert.IsTrue(Directory.Exists(session.WorktreeDirectory), "the deletion leaves it");

        Assert.AreEqual(0, (await cleanup.RunOnceAsync(token))!.Count);
        Assert.IsFalse(Directory.Exists(session.WorktreeDirectory));
    }

    // The clock of a later day.
    private sealed class Later(TimeSpan by) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => System.GetUtcNow() + by;
    }

    private async Task<string> GitAsync(string folder, params string[] arguments)
    {
        var run = await GitWorktreeService.RunGitAsync(folder, arguments, GitWorktreeService.ReadTimeout, TestContext.CancellationToken);
        Assert.IsTrue(run.Succeeded, run.Error);
        return run.Output.Trim();
    }
}