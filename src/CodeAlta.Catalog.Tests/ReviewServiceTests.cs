using System.ComponentModel;
using System.Diagnostics;
using CodeAlta.Catalog.Reviews;

namespace CodeAlta.Catalog.Tests;

/// <summary>The reviews of a checkout: comments that follow their lines, rounds that carry a verdict, and the file they are kept in.</summary>
[TestClass]
public sealed class ReviewServiceTests
{
    private static readonly ProjectDescriptor Project = new() { Id = "project-1", Slug = "p", Name = "P", DisplayName = "P", ProjectPath = "/nowhere/project" };

    [TestMethod]
    public async Task AComment_IsADraft_WithTheTextOfItsLines_AndIdsAreNeverReused()
    {
        using var temp = new Temp();
        var text = new FakeText();
        text.Set("new", "src/A.cs", "one\ntwo\nthree\nfour\n");
        var service = new ReviewService(temp.State, text);
        var scope = new ReviewScope(Project, null);
        var changes = new List<string>();
        service.Changed += changes.Add;

        var first = await service.AddThreadAsync(scope, new(ReviewTargets.Changes, "src/A.cs", ReviewSides.New, 2, 3, "Why?"), default);
        Assert.AreEqual(("c1", ReviewThreadStates.Draft, "two\nthree"), (first.Id, first.State, first.Anchor));
        Assert.IsTrue(service.DeleteThread(scope, "c1"));
        var second = await service.AddThreadAsync(scope, new(ReviewTargets.Changes, "src/A.cs", ReviewSides.New, 1, 1, "Rename"), default);
        Assert.AreEqual("c2", second.Id);
        CollectionAssert.AreEqual(new[] { "project-1", "project-1", "project-1" }, changes);

        // Another instance reads the same file: the window and `alta` share one state.
        var other = new ReviewService(temp.State, text);
        var state = await other.ListAsync(scope, ReviewTargets.Changes, null, includeResolved: false, default);
        Assert.AreEqual("c2", state.Threads.Single().Id);
        Assert.IsFalse(File.Exists(Path.Combine(Project.ProjectPath, ".alta")), "Nothing is written in the checkout.");
        Assert.IsTrue(File.Exists(Path.Combine(temp.State, "reviews", "project-1", "project.json")));
    }

    [TestMethod]
    public async Task InvalidComments_AreRefused()
    {
        using var temp = new Temp();
        var text = new FakeText();
        text.Set("new", "a.md", "one\n");
        var service = new ReviewService(temp.State, text);
        var scope = new ReviewScope(Project, null);

        async Task<string> Code(ReviewThreadDraft draft)
            => (await Assert.ThrowsExactlyAsync<ReviewException>(() => service.AddThreadAsync(scope, draft, default))).Code;

        Assert.AreEqual("invalidPath", await Code(new(ReviewTargets.File, "../a.md", ReviewSides.New, 1, 1, "x")));
        Assert.AreEqual("invalidPath", await Code(new(ReviewTargets.File, "/etc/passwd", ReviewSides.New, 1, 1, "x")));
        Assert.AreEqual("invalid", await Code(new(ReviewTargets.File, "a.md", ReviewSides.Old, 1, 1, "x")));
        Assert.AreEqual("invalidLines", await Code(new(ReviewTargets.File, "a.md", ReviewSides.New, 2, 2, "x")));
        Assert.AreEqual("invalidLines", await Code(new(ReviewTargets.File, "a.md", ReviewSides.New, 1, 0, "x")));
        Assert.AreEqual("invalid", await Code(new(ReviewTargets.File, "a.md", ReviewSides.New, 1, 1, "  ")));
        Assert.AreEqual("limit", await Code(new(ReviewTargets.File, "a.md", ReviewSides.New, 1, 1, new string('x', ReviewService.MaximumText + 1))));

        // A suggestion alone is a comment: deleting the lines.
        var deletion = await service.AddThreadAsync(scope, new(ReviewTargets.File, "a.md", ReviewSides.New, 1, 1, "", ""), default);
        Assert.AreEqual("", deletion.Suggestion);
        // The whole file needs no lines.
        Assert.AreEqual(0, (await service.AddThreadAsync(scope, new(ReviewTargets.File, "a.md", ReviewSides.New, 0, 0, "Too long"), default)).StartLine);
    }

    [TestMethod]
    public async Task AThread_FollowsItsText_AndIsOutdatedWhenItIsGone()
    {
        using var temp = new Temp();
        var text = new FakeText();
        text.Set("new", "a.cs", "a\nb\nc\nd\n");
        var service = new ReviewService(temp.State, text);
        var scope = new ReviewScope(Project, null);
        await service.AddThreadAsync(scope, new(ReviewTargets.Changes, "a.cs", ReviewSides.New, 2, 3, "here"), default);

        // Two lines are inserted above: the thread moves with its text.
        text.Set("new", "a.cs", "x\ny\na\nb\nc\nd\n");
        var moved = (await service.ListAsync(scope, null, null, false, default)).Threads.Single();
        Assert.AreEqual((4, 5, false), (moved.StartLine, moved.EndLine, moved.Outdated));

        // The text is gone: the thread stays where it was, on the line above it, and says it is outdated.
        text.Set("new", "a.cs", "x\ny\na\nd\n");
        var gone = (await service.ListAsync(scope, null, null, false, default)).Threads.Single();
        Assert.AreEqual((3, 3, true), (gone.StartLine, gone.EndLine, gone.Outdated));

        // It comes back twice: the match nearest its last place wins, the earlier one on a tie.
        text.Set("new", "a.cs", "b\nc\nq\nq\nq\nq\nb\nc\n");
        var back = (await service.ListAsync(scope, null, null, false, default)).Threads.Single();
        Assert.AreEqual((1, false), (back.StartLine, back.Outdated));

        // White space at the end of a line does not matter; a file that cannot be read makes it outdated.
        text.Set("new", "a.cs", "b  \r\nc\t\r\n");
        Assert.IsFalse((await service.ListAsync(scope, null, null, false, default)).Threads.Single().Outdated);
        text.Set("new", "a.cs", null);
        Assert.IsTrue((await service.ListAsync(scope, null, null, false, default)).Threads.Single().Outdated);
    }

    [TestMethod]
    public async Task AThreadWhoseLinesTheAgentRevised_StaysWhereTheUserCommented()
    {
        using var temp = new Temp();
        var text = new FakeText();
        text.Set("new", "plan.md", "# Plan\n\n## Steps\n\n- [ ] 1. Add the parser.\n- [ ] 2. Write the tests of the parser.\n- [ ] 3. Update the documentation.\n\n## Risks\n\nSlow reading.\n");
        var service = new ReviewService(temp.State, text);
        var scope = new ReviewScope(Project, null);
        await service.AddThreadAsync(scope, new(ReviewTargets.File, "plan.md", ReviewSides.New, 6, 6, "Split the tests."), default);

        // The agent split the step and front matter came above: the thread is on the step most like its text.
        text.Set("new", "plan.md", "---\nstatus: draft\n---\n# Plan\n\n## Steps\n\n- [ ] 1. Add the parser.\n- [ ] 2. Write the tests of the keyword.\n- [ ] 3. Write the tests of the errors.\n- [ ] 4. Update the documentation.\n\n## Risks\n\nSlow reading.\n");
        var split = (await service.ListAsync(scope, null, null, false, default)).Threads.Single();
        Assert.AreEqual((9, 9, true), (split.StartLine, split.EndLine, split.Outdated));
        Assert.AreEqual("- [ ] 2. Write the tests of the parser.", split.Anchor, "The thread keeps the text it was about.");

        // The agent rewrote it with other words: the thread is between the lines that were around it.
        text.Set("new", "plan.md", "---\nstatus: draft\n---\n# Plan\n\n## Steps\n\n- [ ] 1. Add the parser.\n- [ ] Cover keywords and errors in two suites.\n- [ ] 3. Update the documentation.\n\n## Risks\n\nSlow reading.\n");
        var rewritten = (await service.ListAsync(scope, null, null, false, default)).Threads.Single();
        Assert.AreEqual((9, 9, true), (rewritten.StartLine, rewritten.EndLine, rewritten.Outdated));

        // Its text is back: it is found again, and no longer outdated.
        text.Set("new", "plan.md", "x\n# Plan\n\n## Steps\n\n- [ ] 1. Add the parser.\n- [ ] 2. Write the tests of the parser.\n");
        var back = (await service.ListAsync(scope, null, null, false, default)).Threads.Single();
        Assert.AreEqual((7, 7, false), (back.StartLine, back.EndLine, back.Outdated));

        // Nothing like it nor around it: it keeps its lines, within the file.
        text.Set("new", "plan.md", "nothing\nelse\n");
        var lost = (await service.ListAsync(scope, null, null, false, default)).Threads.Single();
        Assert.AreEqual((2, 2, true), (lost.StartLine, lost.EndLine, lost.Outdated));
    }

    [TestMethod]
    public async Task AReview_IsDeletedWhole_ItsRequestsReturned_AndOtherTargetsKept()
    {
        using var temp = new Temp();
        var text = new FakeText();
        text.Set("new", "plan.md", "# P\nstep\n");
        text.Set("new", "other.md", "# O\n");
        text.Set("new", "a.cs", "a\n");
        var service = new ReviewService(temp.State, text);
        var scope = new ReviewScope(Project, null);
        await service.AddThreadAsync(scope, new(ReviewTargets.File, "plan.md", ReviewSides.New, 2, 2, "Why?"), default);
        service.CommitRound(scope, await service.PrepareRoundAsync(scope, new(ReviewTargets.File, "plan.md", ReviewVerdicts.ChangesRequested, null, "s1"), default));
        service.ReplyAsAgent(scope, "s1", [new("c1", "Done.")]);
        await service.AddThreadAsync(scope, new(ReviewTargets.File, "other.md", ReviewSides.New, 1, 1, "Keep"), default);
        await service.AddThreadAsync(scope, new(ReviewTargets.Changes, "a.cs", ReviewSides.New, 1, 1, "Keep too"), default);
        service.AddRequest(scope, new ReviewRequest { Id = "ask-1", SessionId = "s2", Target = ReviewTargets.File, Path = "plan.md" });
        service.SetViewed(scope, ReviewTargets.Changes, "a.cs", "rev");

        var deleted = service.DeleteReview(scope, ReviewTargets.File, "plan.md");
        Assert.AreEqual((1, 1, "ask-1", true), (deleted.Threads, deleted.Rounds, deleted.Requests.Single().Id, deleted.Deleted));
        var state = await service.ListAsync(scope, null, null, includeResolved: true, default);
        CollectionAssert.AreEquivalent(new[] { "other.md", "a.cs" }, state.Threads.Select(static thread => thread.Path).ToArray());
        Assert.IsNull(state.Request);
        Assert.HasCount(1, state.Viewed);
        Assert.IsFalse(service.DeleteReview(scope, ReviewTargets.File, "plan.md").Deleted, "Nothing is left to delete.");

        // A diff review takes its viewed marks with it; a plan the work items deleted takes its review.
        Assert.AreEqual(1, service.DeleteReview(scope, ReviewTargets.Changes, null).Threads);
        Assert.IsEmpty((await service.ListAsync(scope, ReviewTargets.Changes, null, true, default)).Viewed);
        Assert.AreEqual(1, service.ForgetFile(Project, Project.ProjectPath, "other.md").Threads);
        Assert.IsEmpty((await service.ListAsync(scope, null, null, true, default)).Threads);
        Assert.AreEqual("invalidPath", Assert.ThrowsExactly<ReviewException>(() => service.DeleteReview(scope, ReviewTargets.File, "../x.md")).Code);
    }

    [TestMethod]
    public async Task StaleRequests_AreForgotten_AndAReviewWhoseFileIsGone_SaysSo()
    {
        using var temp = new Temp();
        var folder = Directory.CreateDirectory(Path.Combine(temp.State, "project")).FullName;
        var project = new ProjectDescriptor { Id = "project-1", Slug = "p", Name = "P", DisplayName = "P", ProjectPath = folder };
        File.WriteAllText(Path.Combine(folder, "plan.md"), "# P\n");
        var text = new FakeText();
        text.Set("new", "plan.md", "# P\n");
        var service = new ReviewService(Path.Combine(temp.State, "state"), text);
        var scope = new ReviewScope(project, null);
        await service.AddThreadAsync(scope, new(ReviewTargets.File, "plan.md", ReviewSides.New, 1, 1, "Title?"), default);
        service.AddRequest(scope, new ReviewRequest { Id = "ask-1", SessionId = "s1", Target = ReviewTargets.File, Path = "plan.md" });
        service.AddRequest(scope, new ReviewRequest { Id = "ask-2", SessionId = "s2", Target = ReviewTargets.Changes });
        Assert.IsFalse(service.Overview(project).Single(entry => entry.Target == ReviewTargets.File).FileMissing);

        Assert.AreEqual(1, service.RemoveRequests(scope, request => request.Id == "ask-1"));
        Assert.AreEqual(0, service.RemoveRequests(scope, request => request.Id == "ask-1"));
        Assert.IsNull((await service.ListAsync(scope, ReviewTargets.File, "plan.md", false, default)).Request);
        Assert.AreEqual("ask-2", (await service.ListAsync(scope, ReviewTargets.Changes, null, false, default)).Request?.Id);

        File.Delete(Path.Combine(folder, "plan.md"));
        var gone = service.Overview(project).Single(entry => entry.Target == ReviewTargets.File);
        Assert.AreEqual((true, true), (gone.FileMissing, gone.InFlight));
    }

    [TestMethod]
    public async Task OnlyTheUserResolves_TheAgentAddresses()
    {
        using var temp = new Temp();
        var text = new FakeText();
        text.Set("new", "a.cs", "a\nb\n");
        var service = new ReviewService(temp.State, text);
        var scope = new ReviewScope(Project, null);
        var thread = await service.AddThreadAsync(scope, new(ReviewTargets.Changes, "a.cs", ReviewSides.New, 1, 1, "fix"), default);

        // A draft the agent answers stays a draft: it was not sent.
        Assert.AreEqual(ReviewThreadStates.Draft, service.ReplyAsAgent(scope, "s1", [new("c1", "noted")]).Single().State);

        var round = await service.PrepareRoundAsync(scope, new(ReviewTargets.Changes, null, ReviewVerdicts.ChangesRequested, null, "s1"), default);
        service.CommitRound(scope, round);
        var answered = service.ReplyAsAgent(scope, "s1", [new("c1", "Fixed it.")]).Single();
        Assert.AreEqual(ReviewThreadStates.Addressed, answered.State);
        Assert.AreEqual((ReviewAuthors.Agent, "s1"), (answered.Replies[^1].Author, answered.Replies[^1].SessionId));

        // The user replies: the thread asks again.
        Assert.AreEqual(ReviewThreadStates.Open, service.ReplyAsUser(scope, "c1", "Not quite").State);
        Assert.AreEqual(ReviewThreadStates.Open, service.ReplyAsAgent(scope, "s1", [new("c1", "Is this a question?", Addressed: false)]).Single().State);
        service.ReplyAsAgent(scope, "s1", [new("c1", "Now it is.")]);
        Assert.AreEqual(1, service.ResolveAddressed(scope, ReviewTargets.Changes, null));
        Assert.AreEqual(ReviewThreadStates.Resolved, service.Resolve(scope, "c1", true).State);
        Assert.AreEqual(ReviewThreadStates.Open, service.Resolve(scope, "c1", false).State);

        // A reply to a thread that does not exist changes nothing at all.
        var refused = Assert.ThrowsExactly<ReviewException>(() => service.ReplyAsAgent(scope, "s1", [new("c1", "ok"), new("c9", "?")]));
        Assert.AreEqual("notFound", refused.Code);
        Assert.HasCount(5, (await service.ListAsync(scope, null, null, true, default)).Threads.Single().Replies);
        Assert.AreEqual("c1", thread.Id);
    }

    [TestMethod]
    public async Task ARound_SendsDraftsAndOpenThreads_WithTheirLines_AndApprovalResolvesWhatWasAddressed()
    {
        using var temp = new Temp();
        var text = new FakeText();
        text.Set("old", "src/P.cs", "class P\n{\n    int x;\n}\n");
        text.Set("new", "src/P.cs", "class P\n{\n    int x;\n    catch (Exception)\n    {\n        return 0m;\n    }\n}\n");
        text.Snapshot = "4f2a9c1aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var service = new ReviewService(temp.State, text);
        var scope = new ReviewScope(Project, null);
        await service.AddThreadAsync(scope, new(ReviewTargets.Changes, "src/P.cs", ReviewSides.New, 3, 4, "Don't swallow it"), default);
        await service.AddThreadAsync(scope, new(ReviewTargets.Changes, "src/P.cs", ReviewSides.Old, 3, 3, "Keep x", "    int y;"), default);

        var draft = await service.PrepareRoundAsync(scope, new(ReviewTargets.Changes, null, ReviewVerdicts.ChangesRequested, "Good direction.", "s1", "ask-1"), default);
        StringAssert.StartsWith(draft.Text, "# Review of your changes: changes requested");
        StringAssert.Contains(draft.Text, "Round 1 · 2 comments · reviewed snapshot `4f2a9c1`");
        StringAssert.Contains(draft.Text, "Good direction.");
        StringAssert.Contains(draft.Text, "### c1 · lines 3–4 (new)\n```diff\n     int x;\n+    catch (Exception)\n```\nDon't swallow it");
        StringAssert.Contains(draft.Text, "### c2 · line 3 (old) · suggestion\n```diff\n-    int x;\n```");
        StringAssert.Contains(draft.Text, "```suggestion\n    int y;\n```");
        StringAssert.Contains(draft.Text, "`alta review reply --stdin`");
        StringAssert.Contains(draft.Text, "Do not commit before a review approves the changes.");

        // Nothing is recorded before the round is committed.
        Assert.IsTrue((await service.ListAsync(scope, null, null, false, default)).Threads.All(static thread => thread.State == ReviewThreadStates.Draft));
        var round = service.CommitRound(scope, draft);
        Assert.AreEqual(("r1", "4f2a9c1aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"), (round.Id, round.Snapshot));
        var state = await service.ListAsync(scope, ReviewTargets.Changes, null, false, default);
        Assert.IsTrue(state.Threads.All(static thread => thread.State == ReviewThreadStates.Open && thread.Round == "r1"));

        // Round 2: the agent addressed c1; c2 is still open and sent again, with the agent's earlier reply.
        service.ReplyAsAgent(scope, "s1", [new("c1", "Rethrows now.")]);
        service.ReplyAsAgent(scope, "s1", [new("c2", "Why y?", Addressed: false)]);
        var second = await service.PrepareRoundAsync(scope, new(ReviewTargets.Changes, null, ReviewVerdicts.Approved, null, "s1"), default);
        StringAssert.Contains(second.Text, "# Review of your changes: approved");
        StringAssert.Contains(second.Text, "Round 2 · 0 comments, 1 still open from earlier rounds");
        StringAssert.Contains(second.Text, "## Still open from earlier rounds");
        StringAssert.Contains(second.Text, "> Your earlier reply: Why y?");
        StringAssert.Contains(second.Text, "Commit the approved changes now, and only them");
        service.CommitRound(scope, second);
        var after = await service.ListAsync(scope, ReviewTargets.Changes, null, true, default);
        Assert.AreEqual(ReviewThreadStates.Resolved, after.Threads.Single(static thread => thread.Id == "c1").State);
        Assert.AreEqual(ReviewThreadStates.Open, after.Threads.Single(static thread => thread.Id == "c2").State);
        Assert.HasCount(2, after.Rounds);

        // Nothing to say: a comment-only round with nothing in it is refused.
        service.Resolve(scope, "c2", true);
        Assert.AreEqual("nothingToSubmit", (await Assert.ThrowsExactlyAsync<ReviewException>(
            () => service.PrepareRoundAsync(scope, new(ReviewTargets.Changes, null, ReviewVerdicts.Commented, null, "s1"), default))).Code);
    }

    [TestMethod]
    public async Task ARoundTheSessionDidNotGet_IsUndone_AndARoundCanNameItsSessionLater()
    {
        using var temp = new Temp();
        var text = new FakeText();
        text.Set("new", "plan.md", "- one\n- two\n");
        var service = new ReviewService(temp.State, text);
        var scope = new ReviewScope(Project, null);
        await service.AddThreadAsync(scope, new(ReviewTargets.File, "plan.md", ReviewSides.New, 1, 1, "First"), default);
        service.CommitRound(scope, await service.PrepareRoundAsync(scope, new(ReviewTargets.File, "plan.md", ReviewVerdicts.ChangesRequested, null, "s1"), default));
        service.ReplyAsAgent(scope, "s1", [new("c1", "Done.")]);
        await service.AddThreadAsync(scope, new(ReviewTargets.File, "plan.md", ReviewSides.New, 2, 2, "Second"), default);
        service.AddRequest(scope, new ReviewRequest { Id = "ask-1", SessionId = "s1", Target = ReviewTargets.File, Path = "plan.md" });

        // An approval opens the draft, resolves what was addressed and answers the request: recorded before the send.
        var record = service.RecordRound(scope, await service.PrepareRoundAsync(scope, new(ReviewTargets.File, "plan.md", ReviewVerdicts.Approved, null, "new", "ask-1"), default));
        var sent = await service.ListAsync(scope, ReviewTargets.File, "plan.md", true, default);
        Assert.AreEqual((ReviewThreadStates.Resolved, ReviewThreadStates.Open, null), (sent.Threads[0].State, sent.Threads[1].State, sent.Request));
        service.SetRoundSession(scope, record.Round.Id, "s2");
        Assert.AreEqual("s2", (await service.ListAsync(scope, ReviewTargets.File, "plan.md", true, default)).Rounds[^1].SessionId);

        // The session did not get it: everything is as before, and the user's edit of a comment meanwhile is kept.
        service.UpdateThread(scope, "c2", "Second, edited", null, false);
        service.RevertRound(scope, record);
        var undone = await service.ListAsync(scope, ReviewTargets.File, "plan.md", true, default);
        Assert.AreEqual((ReviewThreadStates.Addressed, ReviewThreadStates.Draft, "Second, edited"), (undone.Threads[0].State, undone.Threads[1].State, undone.Threads[1].Body));
        Assert.IsNull(undone.Threads[0].ResolvedAt);
        Assert.HasCount(1, undone.Rounds);
        Assert.AreEqual("ask-1", undone.Request!.Id);
    }

    [TestMethod]
    public async Task AFileRound_QuotesItsLines_AndALongRoundIsCut()
    {
        using var temp = new Temp();
        var text = new FakeText();
        var lines = string.Join('\n', Enumerable.Range(1, 400).Select(static index => $"- step {index}")) + "\n";
        text.Set("new", ".alta/plans/p.md", lines);
        var service = new ReviewService(temp.State, text);
        var scope = new ReviewScope(Project, null);
        await service.AddThreadAsync(scope, new(ReviewTargets.File, ".alta/plans/p.md", ReviewSides.New, 2, 2, "Split this step"), default);

        var draft = await service.PrepareRoundAsync(scope, new(ReviewTargets.File, ".alta/plans/p.md", ReviewVerdicts.ChangesRequested, null, "s1"), default);
        StringAssert.StartsWith(draft.Text, "# Review of `.alta/plans/p.md`: changes requested");
        StringAssert.Contains(draft.Text, "### c1 · line 2\n> - step 2\n\nSplit this step");
        StringAssert.Contains(draft.Text, "request the next review with `alta review request file .alta/plans/p.md`");
        Assert.IsNull(draft.Round.Snapshot);

        for (var index = 0; index < 60; index++)
        {
            await service.AddThreadAsync(scope, new(ReviewTargets.File, ".alta/plans/p.md", ReviewSides.New, 3 + index * 5, 7 + index * 5, new string('w', 900)), default);
        }

        var large = await service.PrepareRoundAsync(scope, new(ReviewTargets.File, ".alta/plans/p.md", ReviewVerdicts.Commented, null, "s1"), default);
        Assert.IsLessThanOrEqualTo(32 * 1024, large.Text.Length);
        StringAssert.Contains(large.Text, "Read them with `alta review list`.");
    }

    [TestMethod]
    public async Task ASave_ReplacesTheFileWhileAReaderHoldsItOpen_AndLeavesNoStagedFile()
    {
        using var temp = new Temp();
        var text = new FakeText();
        text.Set("new", "a", "one\ntwo\n");
        var service = new ReviewService(temp.State, text);
        var scope = new ReviewScope(Project, null);
        await service.AddThreadAsync(scope, new(ReviewTargets.Changes, "a", ReviewSides.New, 1, 1, "First"), default);
        var path = Path.Combine(temp.State, "reviews", "project-1", "project.json");

        // A reader of the file, such as an indexer or the list of the checkouts, has it open as the window saves:
        // on Windows the plain replace was refused ("Access to the path is denied") and left its staged file behind.
        using (var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            await service.AddThreadAsync(scope, new(ReviewTargets.Changes, "a", ReviewSides.New, 2, 2, "Second"), default);
        }

        Assert.HasCount(2, (await service.ListAsync(scope, null, null, false, default)).Threads);
        Assert.IsEmpty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp"), "No staged file is left beside the reviews.");
        Assert.HasCount(2, (await new ReviewService(temp.State, text).ListAsync(scope, null, null, false, default)).Threads, "The second comment is in the file.");
    }

    [TestMethod]
    public async Task ASnapshot_WhoseIndexGitCannotReplace_IsWrittenThroughAnIndexGitCreates()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("A file held open stops a rename over it on Windows only.");
        }

        using var repository = GitRepository.Create();
        File.WriteAllText(Path.Combine(repository.Root, "readme.md"), "one\nchanged\n");
        File.WriteAllText(Path.Combine(repository.Root, "new.txt"), "new\n");
        var expected = await GitSnapshot.WriteTreeAsync(repository.Root, default);
        Assert.IsNotNull(expected);

        // The copy of the index is held as a file system filter holds what the process wrote (an antivirus scanning
        // it): git cannot put the index it writes in its place. The tree is written through an index git creates.
        var held = Path.Combine(repository.Home, "held.index");
        File.WriteAllText(held, string.Empty);
        var paths = new Queue<string>([held, Path.Combine(repository.Home, "created.index")]);
        using (new FileStream(held, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            Assert.AreEqual(expected, await GitSnapshot.WriteTreeAsync(repository.Root, paths.Dequeue, default));
        }

        Assert.IsEmpty(paths, "The first index could not be replaced: the second one was used.");
        Assert.IsFalse(File.Exists(Path.Combine(repository.Home, "created.index")), "The temporary index is removed.");
    }

    [TestMethod]
    public async Task AFileThatCannotBeRead_IsNeverOverwritten()
    {
        using var temp = new Temp();
        var service = new ReviewService(temp.State, new FakeText());
        var scope = new ReviewScope(Project, null);
        var path = Path.Combine(temp.State, "reviews", "project-1", "project.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        File.WriteAllText(path, "{ not json");
        var broken = await service.ListAsync(scope, null, null, false, default);
        Assert.IsNotNull(broken.ReadOnly);
        Assert.AreEqual("readOnly", Assert.ThrowsExactly<ReviewException>(() => service.SetViewed(scope, ReviewTargets.Changes, "a", "1")).Code);
        Assert.AreEqual("{ not json", File.ReadAllText(path));

        File.WriteAllText(path, """{ "version": 99, "threads": [] }""");
        Assert.IsNotNull((await service.ListAsync(scope, null, null, false, default)).ReadOnly);
        Assert.AreEqual("readOnly", Assert.ThrowsExactly<ReviewException>(() => service.SetViewed(scope, ReviewTargets.Changes, "a", "1")).Code);

        // Fixed by hand, or deleted: it works again, with ids after the ones in use.
        File.WriteAllText(path, """{ "version": 1, "threads": [ { "id": "c7", "target": "changes", "path": "a", "side": "new", "anchor": "", "body": "x", "state": "draft" } ] }""");
        service.SetViewed(scope, ReviewTargets.Changes, "a", "rev1");
        var text = new FakeText();
        text.Set("new", "a", "one\n");
        var next = await new ReviewService(temp.State, text).AddThreadAsync(scope, new(ReviewTargets.Changes, "a", ReviewSides.New, 1, 1, "y"), default);
        Assert.AreEqual("c8", next.Id);
    }

    [TestMethod]
    public async Task TheStore_KeepsTheResolvedOnesLast_AndViewedMarksFollowRevisions()
    {
        using var temp = new Temp();
        var text = new FakeText();
        text.Set("new", "a", "one\n");
        var service = new ReviewService(temp.State, text);
        var scope = new ReviewScope(Project, null);
        for (var index = 0; index < ReviewService.MaximumThreads; index++)
        {
            await service.AddThreadAsync(scope, new(ReviewTargets.Changes, "a", ReviewSides.New, 1, 1, "x"), default);
        }

        Assert.AreEqual("limit", (await Assert.ThrowsExactlyAsync<ReviewException>(() => service.AddThreadAsync(scope, new(ReviewTargets.Changes, "a", ReviewSides.New, 1, 1, "x"), default))).Code);
        service.Resolve(scope, "c3", true);
        await service.AddThreadAsync(scope, new(ReviewTargets.Changes, "a", ReviewSides.New, 1, 1, "x"), default);
        Assert.IsFalse((await service.ListAsync(scope, null, null, true, default)).Threads.Any(static thread => thread.Id == "c3"));

        service.SetViewed(scope, ReviewTargets.Changes, "a", "rev1");
        Assert.AreEqual("rev1", (await service.ListAsync(scope, ReviewTargets.Changes, null, false, default)).Viewed["changes:a"]);
        service.SetViewed(scope, ReviewTargets.Changes, "a", null);
        Assert.IsEmpty((await service.ListAsync(scope, ReviewTargets.Changes, null, false, default)).Viewed);
    }

    [TestMethod]
    public async Task TheOverview_ListsEachTargetOfEachCheckout_InFlightFirst()
    {
        using var temp = new Temp();
        var text = new FakeText();
        text.Set("new", "a.cs", "a\nb\n");
        text.Set("new", "plan.md", "# P\n");
        var service = new ReviewService(temp.State, text);
        var scope = new ReviewScope(Project, null);
        Assert.IsEmpty(service.Overview(Project));

        await service.AddThreadAsync(scope, new(ReviewTargets.File, "plan.md", ReviewSides.New, 1, 1, "Title?"), default);
        service.CommitRound(scope, await service.PrepareRoundAsync(scope, new(ReviewTargets.File, "plan.md", ReviewVerdicts.Approved, null, "s1"), default));
        service.Resolve(scope, "c1", true);
        await service.AddThreadAsync(scope, new(ReviewTargets.Changes, "a.cs", ReviewSides.New, 1, 1, "x"), default);
        service.AddRequest(scope, new ReviewRequest { Id = "ask-1", SessionId = "s1", Target = ReviewTargets.Changes, CreatedAt = DateTimeOffset.UtcNow });

        var entries = service.Overview(Project);
        Assert.HasCount(2, entries);
        var changes = entries[0];
        Assert.AreEqual((ReviewTargets.Changes, null, 1, "ask-1", true), (changes.Target, changes.Path, changes.Drafts, changes.Request?.Id, changes.InFlight));
        var plan = entries[1];
        Assert.AreEqual((ReviewTargets.File, "plan.md", 1, ReviewVerdicts.Approved, false), (plan.Target, plan.Path, plan.Resolved, plan.LastRound?.Verdict, plan.InFlight));
        Assert.IsNull(changes.Checkout);
    }

    [TestMethod]
    public async Task Checkouts_KeepTheirOwnReviews_AndAMissingOneIsListedNotDeleted()
    {
        using var repository = GitRepository.Create();
        var project = new ProjectDescriptor { Id = "project-1", Slug = "p", Name = "P", DisplayName = "P", ProjectPath = repository.Root };
        var worktree = Path.Combine(repository.Home, "wt");
        repository.Git("worktree", "add", "-q", "-b", "feature", worktree);
        File.WriteAllText(Path.Combine(repository.Root, "readme.md"), "one\nchanged\n");
        File.WriteAllText(Path.Combine(worktree, "readme.md"), "one\nother\n");
        var service = new ReviewService(Path.Combine(repository.Home, "state"));

        var main = new ReviewScope(project, null);
        var linked = new ReviewScope(project, worktree);
        Assert.AreEqual("changed", (await service.AddThreadAsync(main, new(ReviewTargets.Changes, "readme.md", ReviewSides.New, 2, 2, "a"), default)).Anchor);
        Assert.AreEqual("other", (await service.AddThreadAsync(linked, new(ReviewTargets.Changes, "readme.md", ReviewSides.New, 2, 2, "b"), default)).Anchor);
        Assert.AreEqual("c1", (await service.ListAsync(linked, null, null, false, default)).Threads.Single().Id);
        Assert.AreEqual("a", (await service.ListAsync(main, null, null, false, default)).Threads.Single().Body);
        // The old side is the last commit.
        Assert.AreEqual("one", (await service.AddThreadAsync(main, new(ReviewTargets.Changes, "readme.md", ReviewSides.Old, 1, 1, "c"), default)).Anchor);

        // A folder outside the repository is not a checkout of the project.
        var stranger = Directory.CreateDirectory(Path.Combine(repository.Home, "stranger")).FullName;
        Assert.AreEqual("invalidCheckout", (await Assert.ThrowsExactlyAsync<ReviewException>(
            () => service.ListAsync(new ReviewScope(project, stranger), null, null, false, default))).Code);

        repository.Git("worktree", "remove", "--force", worktree);
        var listed = service.ListCheckouts(project.Id);
        Assert.IsTrue(listed.Any(entry => entry.Checkout == worktree && entry.Missing));
        Assert.IsTrue(listed.Any(entry => entry.Checkout == repository.Root && !entry.Missing));
        Assert.IsTrue(service.DeleteCheckout(project.Id, worktree));
        Assert.HasCount(1, service.ListCheckouts(project.Id));
        Assert.IsTrue(service.DeleteProject(project.Id));
        Assert.IsEmpty(service.ListCheckouts(project.Id));
    }

    [TestMethod]
    public async Task ASnapshot_LeavesTheIndexAsItWas_AndApprovalHoldsUntilTheWorkTreeChanges()
    {
        using var repository = GitRepository.Create();
        var project = new ProjectDescriptor { Id = "project-1", Slug = "p", Name = "P", DisplayName = "P", ProjectPath = repository.Root };
        File.WriteAllText(Path.Combine(repository.Root, "readme.md"), "one\ntwo\n");
        File.WriteAllText(Path.Combine(repository.Root, "new.txt"), "new\n");
        var index = repository.Git("diff", "--cached", "--name-only");
        var service = new ReviewService(Path.Combine(repository.Home, "state"));
        var scope = new ReviewScope(project, null);

        var first = await GitSnapshot.WriteTreeAsync(repository.Root, default);
        Assert.IsNotNull(first);
        Assert.AreEqual(first, await GitSnapshot.WriteTreeAsync(repository.Root, default), "The same work tree is the same tree.");
        Assert.AreEqual(index, repository.Git("diff", "--cached", "--name-only"), "The index of the checkout is untouched.");
        Assert.Contains("new.txt", repository.Git("ls-tree", "--name-only", first!));

        Assert.IsFalse(await service.IsApprovedAsync(scope, default));
        await service.AddThreadAsync(scope, new(ReviewTargets.Changes, "readme.md", ReviewSides.New, 2, 2, "ok"), default);
        var approved = await service.PrepareRoundAsync(scope, new(ReviewTargets.Changes, null, ReviewVerdicts.Approved, null, "s1"), default);
        service.CommitRound(scope, approved);
        Assert.IsTrue(await service.IsApprovedAsync(scope, default));

        // A commit does not change the work tree: the approval still holds for the next commit.
        repository.Git("add", "readme.md");
        repository.Git("commit", "-q", "-m", "first part");
        Assert.IsTrue(await service.IsApprovedAsync(scope, default));

        // An edit after the approval needs a new review.
        File.WriteAllText(Path.Combine(repository.Root, "new.txt"), "edited\n");
        Assert.IsFalse(await service.IsApprovedAsync(scope, default));
    }

    private sealed class FakeText : IReviewTextSource
    {
        private readonly Dictionary<(string Side, string Path), string?> _texts = [];

        public string? Snapshot { get; set; }

        public void Set(string side, string path, string? text) => _texts[(side, path)] = text;

        public Task<string?> ReadAsync(ReviewScope scope, string target, string side, string path, CancellationToken cancellationToken)
            => Task.FromResult(_texts.GetValueOrDefault((side, path)));

        public Task<string?> SnapshotAsync(ReviewScope scope, CancellationToken cancellationToken) => Task.FromResult(Snapshot);
    }

    private sealed class Temp : IDisposable
    {
        public string State { get; } = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "CodeAlta-reviews-" + Guid.NewGuid().ToString("N"))).FullName;

        public void Dispose()
        {
            try
            {
                Directory.Delete(State, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private sealed class GitRepository : IDisposable
    {
        private GitRepository(string home)
        {
            Home = home;
            Root = Directory.CreateDirectory(Path.Combine(home, "repository")).FullName;
        }

        public string Home { get; }

        public string Root { get; }

        public static GitRepository Create()
        {
            var home = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "CodeAlta-review-git-" + Guid.NewGuid().ToString("N"))).FullName;
            if (Directory.ResolveLinkTarget(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar), returnFinalTarget: true) is { } target)
            {
                home = Path.Combine(target.FullName, Path.GetFileName(home));
            }

            var repository = new GitRepository(home);
            try
            {
                repository.Git("init", "-q", "-b", "main");
            }
            catch (Win32Exception)
            {
                repository.Dispose();
                Assert.Inconclusive("git is not installed.");
            }

            foreach (var (name, value) in new[] { ("user.name", "Test"), ("user.email", "test@example.invalid"), ("core.autocrlf", "false"), ("commit.gpgsign", "false") })
            {
                repository.Git("config", name, value);
            }

            File.WriteAllText(Path.Combine(repository.Root, "readme.md"), "one\n");
            repository.Git("add", "-A");
            repository.Git("commit", "-q", "-m", "one");
            return repository;
        }

        public string Git(params string[] arguments)
        {
            var start = new ProcessStartInfo("git") { WorkingDirectory = Root, RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            start.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
            start.Environment["GIT_CONFIG_GLOBAL"] = Path.Combine(Home, "no-global-config");
            using var process = Process.Start(start)!;
            var error = process.StandardError.ReadToEndAsync();
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            Assert.AreEqual(0, process.ExitCode, $"git {string.Join(' ', arguments)}: {error.Result}");
            return output;
        }

        public void Dispose()
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(Home, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(Home, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
