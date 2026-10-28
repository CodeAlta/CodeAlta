using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Catalog;
using CodeAlta.Catalog.Reviews;
using CodeAlta.Desktop.Rpc;
using CodeAlta.Desktop.WorkItems;
using CodeAlta.LiveTool;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Desktop.Tests;

/// <summary>
/// The reviews as the application has them: the service the page comments, resolves and submits through, the
/// `alta review` commands of a session over the same store, and where a round goes.
/// </summary>
[TestClass]
public sealed class ReviewHostTests
{
    private const string Epoch = "6f1c8f0e-2a4b-4c55-9d1e-7a2b3c4d5e6f";
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    [TestMethod]
    public async Task ThePage_CommentsAPlan_AndAnAgentReplyIsSeenThroughTheWatch()
    {
        await using var fixture = await Fixture.CreateAsync();
        using var watch = new CancellationTokenSource();
        await using var events = fixture.Rpc.WatchAsync(new(Epoch), watch.Token).GetAsyncEnumerator(watch.Token);
        Assert.IsTrue(await events.MoveNextAsync());
        Assert.AreEqual(0, events.Current.Revision);

        var added = await fixture.Rpc.AddAsync(new(Epoch, fixture.Project.Id, null, "file", Fixture.Plan, "new", 3, 4, "Split these steps.", null), default);
        Assert.AreEqual(("ok", "c1", "draft", "- one\n- two"), (added.Status, added.Thread!.Id, added.Thread.State, added.Thread.Anchor));
        Assert.IsTrue(await events.MoveNextAsync().AsTask().WaitAsync(Patience));

        // The Reviews page lists it, in the default space that holds every project.
        var overview = await fixture.Rpc.OverviewAsync(new(Epoch, "default", false), default);
        var entry = overview.Projects.Single().Entries.Single();
        Assert.AreEqual(("ok", "file", Fixture.Plan, 1), (overview.Status, entry.Target, entry.Path, entry.Drafts));
        Assert.IsEmpty((await fixture.Rpc.OverviewAsync(new(Epoch, "another-space", false), default)).Projects);

        var updated = await fixture.Rpc.UpdateAsync(new(Epoch, fixture.Project.Id, null, "c1", null, "- one, then two", false), default);
        Assert.AreEqual("- one, then two", updated.Thread!.Suggestion);
        var listed = await fixture.Rpc.ListAsync(new(Epoch, fixture.Project.Id, null, "file", Fixture.Plan, false), default);
        Assert.AreEqual(("ok", 1), (listed.Status, listed.Threads.Count));

        // The round goes to the session as a prompt; then the agent answers through `alta review reply`.
        var submitted = await fixture.Rpc.SubmitAsync(new(Epoch, fixture.Project.Id, null, "file", Fixture.Plan, "changes_requested", "Close.", "session-1", false), default);
        Assert.AreEqual(("ok", "session-1"), (submitted.Status, submitted.SessionId));
        var (sessionId, text) = fixture.Sent.Single();
        Assert.AreEqual("session-1", sessionId);
        StringAssert.StartsWith(text, "# Review of `.alta/plans/plan.md`: changes requested");
        StringAssert.Contains(text, "Split these steps.");

        var replied = await fixture.Alta.InvokeAsync(["review", "reply", "--stdin"], "[{\"id\":\"c1\",\"reply\":\"Split.\"}]",
            new AltaCallerIdentity { Kind = "agent", SourceSessionId = "session-1", SourceProjectId = fixture.Project.Id }).AsTask().WaitAsync(Patience);
        Assert.AreEqual(AltaExitCodes.Success, replied.ExitCode, replied.Stderr + replied.Stdout);
        Assert.IsTrue(await events.MoveNextAsync().AsTask().WaitAsync(Patience), "The page is told of the agent's reply.");
        var after = (await fixture.Rpc.ListAsync(new(Epoch, fixture.Project.Id, null, null, null, false), default)).Threads.Single();
        Assert.AreEqual(("addressed", "agent", "Split."), (after.State, after.Replies.Single().Author, after.Replies.Single().Body));

        // The user resolves what the agent addressed, and can open it again.
        Assert.AreEqual("ok", (await fixture.Rpc.ResolveAddressedAsync(new(Epoch, fixture.Project.Id, null, "file", Fixture.Plan), default)).Status);
        Assert.AreEqual("resolved", (await fixture.Rpc.ListAsync(new(Epoch, fixture.Project.Id, null, null, null, true), default)).Threads.Single().State);
        Assert.AreEqual("open", (await fixture.Rpc.ResolveAsync(new(Epoch, fixture.Project.Id, null, "c1", false), default)).Thread!.State);
        Assert.AreEqual("ok", (await fixture.Rpc.DeleteAsync(new(Epoch, fixture.Project.Id, null, "c1"), default)).Status);
        Assert.AreEqual("not_found", (await fixture.Rpc.DeleteAsync(new(Epoch, fixture.Project.Id, null, "c1"), default)).Status);
        watch.Cancel();
    }

    [TestMethod]
    public async Task ARound_ThatCannotBeSent_LeavesTheDrafts_AndRequestsAreChecked()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Rpc.AddAsync(new(Epoch, fixture.Project.Id, null, "file", Fixture.Plan, "new", 1, 1, "Title?", null), default);
        fixture.SendProblem = "The session is gone.";

        var failed = await fixture.Rpc.SubmitAsync(new(Epoch, fixture.Project.Id, null, "file", Fixture.Plan, "commented", null, "session-1", false), default);
        Assert.AreEqual(("send_failed", "The session is gone."), (failed.Status, failed.Message));
        Assert.AreEqual("draft", (await fixture.Rpc.ListAsync(new(Epoch, fixture.Project.Id, null, null, null, false), default)).Threads.Single().State);

        // Invalid requests, a stale page, a host without reviews.
        Assert.AreEqual("invalid_request", (await fixture.Rpc.AddAsync(new(Epoch, fixture.Project.Id, null, "file", "../x.md", "new", 1, 1, "x", null), default)).Status);
        Assert.AreEqual("invalid_request", (await fixture.Rpc.ListAsync(new(Epoch, fixture.Project.Id, "relative/folder", null, null, false), default)).Status);
        Assert.AreEqual("invalid_request", (await fixture.Rpc.SubmitAsync(new(Epoch, fixture.Project.Id, null, "file", Fixture.Plan, "maybe", null, "session-1", false), default)).Status);
        Assert.AreEqual("not_found", (await fixture.Rpc.ListAsync(new(Epoch, "unknown", null, null, null, false), default)).Status);
        Assert.AreEqual("stale_epoch", (await fixture.Rpc.ListAsync(new(Guid.NewGuid().ToString("D"), fixture.Project.Id, null, null, null, false), default)).Status);
        Assert.AreEqual("unavailable", (await new ReviewsService().ListAsync(new(Epoch, fixture.Project.Id, null, null, null, false), default)).Status);
    }

    [TestMethod]
    public async Task AnAgentThatRepliesBeforeTheSendReturns_FindsItsCommentsOpen()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Rpc.AddAsync(new(Epoch, fixture.Project.Id, null, "file", Fixture.Plan, "new", 3, 4, "Split these steps.", null), default);
        AltaCommandResult? replied = null;
        fixture.DuringSend = async () => replied = await fixture.Alta.InvokeAsync(["review", "reply", "--stdin"], "[{\"id\":\"c1\",\"reply\":\"Split.\"}]",
            new AltaCallerIdentity { Kind = "agent", SourceSessionId = "session-1", SourceProjectId = fixture.Project.Id }).AsTask().WaitAsync(Patience);

        var submitted = await fixture.Rpc.SubmitAsync(new(Epoch, fixture.Project.Id, null, "file", Fixture.Plan, "changes_requested", null, "session-1", false), default);

        Assert.AreEqual("ok", submitted.Status, submitted.Message);
        Assert.AreEqual(AltaExitCodes.Success, replied!.ExitCode, replied.Stderr + replied.Stdout);
        var thread = (await fixture.Rpc.ListAsync(new(Epoch, fixture.Project.Id, null, null, null, false), default)).Threads.Single();
        Assert.AreEqual(("addressed", "Split."), (thread.State, thread.Replies.Single().Body));
    }

    [TestMethod]
    public async Task ARound_StartsANewSessionInPlanMode_ForAPlan()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Rpc.AddAsync(new(Epoch, fixture.Project.Id, null, "file", Fixture.Plan, "new", 1, 1, "Rename it", null), default);

        var started = await fixture.Rpc.SubmitAsync(new(Epoch, fixture.Project.Id, null, "file", Fixture.Plan, "changes_requested", null, null, true, "session-1"), default);

        Assert.AreEqual(("ok", "started-session"), (started.Status, started.SessionId));
        var start = fixture.Starter.Starts.Single();
        Assert.AreEqual(("plan", null, "session-1"), (start.Model!.AgentPromptId, start.Model.Worktree, start.Like));
        StringAssert.StartsWith(start.Prompt, "# Review of `.alta/plans/plan.md`: changes requested");
        var round = (await fixture.Rpc.ListAsync(new(Epoch, fixture.Project.Id, null, null, null, false), default)).Rounds.Single();
        Assert.AreEqual("started-session", round.SessionId);
    }

    [TestMethod]
    public async Task ARound_ForASessionThatWaitsForThisReview_AnswersItsRequest()
    {
        await using var fixture = await Fixture.CreateAsync();
        var receipt = new OwnedSessionCommandReceipt("answer", OwnedSessionCommandKind.Send, "session-1");
        OwnedTextSendRequest? answered = null;
        fixture.Owner = new OwnedSessionAskService(true, (request, context) =>
        {
            answered = request;
            context.RecordRunReturned(new("run-answer"));
            return new(OwnedSessionCommandAdmissionKind.Accepted, receipt);
        });
        var send = fixture.Owner.CreateExecution(Guid.NewGuid(), "session-1", default);
        send.Bind(Guid.NewGuid(), 1, new("fixture"));
        await send.StartedAsync(new("run"), default);
        try
        {
            var queued = fixture.Owner.QueueFromSession("session-1", new LiveTool.AltaAskRequest
            {
                Review = new() { Kind = "file", Path = Fixture.Plan, ProjectId = fixture.Project.Id },
            })!;
            fixture.Reviews.AddRequest(new ReviewScope(fixture.Project, null), new ReviewRequest { Id = queued.AskId, SessionId = "session-1", Target = "file", Path = Fixture.Plan });
            Assert.AreEqual(queued.AskId, (await fixture.Rpc.ListAsync(new(Epoch, fixture.Project.Id, null, "file", Fixture.Plan, false), default)).Request!.Id);

            var submitted = await fixture.Rpc.SubmitAsync(new(Epoch, fixture.Project.Id, null, "file", Fixture.Plan, "approved", "Go.", "session-1", false), default);

            Assert.AreEqual("ok", submitted.Status, submitted.Message);
            Assert.IsEmpty(fixture.Sent, "The round is the answer of the request, not another prompt.");
            StringAssert.StartsWith(answered!.Text, "# Review of `.alta/plans/plan.md`: approved");
            Assert.IsNull(fixture.Owner.List("session-1").Head);
            Assert.IsNull((await fixture.Rpc.ListAsync(new(Epoch, fixture.Project.Id, null, "file", Fixture.Plan, false), default)).Request);
        }
        finally
        {
            receipt.Complete(new(OwnedSessionCommandOutcome.Completed, new AgentRunId("run-answer")));
            send.Close();
            fixture.Owner.CloseAdmission();
            await fixture.Owner.DrainAsync().WaitAsync(Patience);
        }
    }

    [TestMethod]
    public async Task TheApproval_OfTheChanges_HoldsUntilAFileChanges_AndAFolderBelowTheCheckoutIsThatCheckout()
    {
        // #238: the composer and the Changes tab no longer say that approved changes wait for a review.
        await using var fixture = await Fixture.CreateAsync();
        var root = fixture.Project.ProjectPath;
        if (!Git(root, "init", "-q", "-b", "main"))
        {
            Assert.Inconclusive("git is not installed.");
        }

        foreach (var (name, value) in new[] { ("user.name", "Test"), ("user.email", "test@example.invalid"), ("commit.gpgsign", "false"), ("core.autocrlf", "false") }) Git(root, "config", name, value);
        Assert.IsTrue(Git(root, "add", "-A") && Git(root, "commit", "-q", "-m", "one"));
        File.WriteAllText(Path.Combine(root, "a.txt"), "one\n");
        File.WriteAllText(Path.Combine(root, "b.txt"), "two\n");
        var below = Directory.CreateDirectory(Path.Combine(root, "src")).FullName;

        var none = await fixture.Rpc.ApprovalAsync(new(Epoch, fixture.Project.Id, null), default);
        Assert.AreEqual(("ok", null, false), (none.Status, none.Round, none.Current));
        var submitted = await fixture.Rpc.SubmitAsync(new(Epoch, fixture.Project.Id, null, "changes", null, "approved", null, "session-1", false), default);
        Assert.AreEqual("ok", submitted.Status, submitted.Message);

        var approval = await fixture.Rpc.ApprovalAsync(new(Epoch, fixture.Project.Id, null), default);
        Assert.AreEqual(("ok", submitted.RoundId, "approved", true), (approval.Status, approval.Round?.Id, approval.Round?.Verdict, approval.Current));
        Assert.IsTrue((await fixture.Rpc.ApprovalAsync(new(Epoch, fixture.Project.Id, below), default)).Current, "A folder of the project's checkout is the project folder.");

        // The agent commits a part: the rest is still what the user approved.
        Assert.IsTrue(Git(root, "add", "a.txt") && Git(root, "commit", "-q", "-m", "a"));
        Assert.IsTrue((await fixture.Rpc.ApprovalAsync(new(Epoch, fixture.Project.Id, null), default)).Current);

        File.WriteAllText(Path.Combine(root, "b.txt"), "two, edited\n");
        var changed = await fixture.Rpc.ApprovalAsync(new(Epoch, fixture.Project.Id, null), default);
        Assert.AreEqual(("ok", submitted.RoundId, false), (changed.Status, changed.Round?.Id, changed.Current));
        Assert.AreEqual("stale_epoch", (await fixture.Rpc.ApprovalAsync(new("6f1c8f0e-2a4b-4c55-9d1e-000000000000", fixture.Project.Id, null), default)).Status);
    }

    private static bool Git(string folder, params string[] arguments)
    {
        try
        {
            var start = new System.Diagnostics.ProcessStartInfo("git") { WorkingDirectory = folder, RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            start.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
            start.Environment["GIT_CONFIG_GLOBAL"] = Path.Combine(folder, "..", "no-global-config");
            using var process = System.Diagnostics.Process.Start(start)!;
            var error = process.StandardError.ReadToEndAsync();
            process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            _ = error.Result;
            return process.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public const string Plan = ".alta/plans/plan.md";
        private readonly string _root;
        private OwnedSessionAskService _owner = new(true, static (_, _) => throw new AssertFailedException("No answer expected."));

        private Fixture(string root, ProjectCatalog projects, ProjectDescriptor project)
        {
            (_root, Project) = (root, project);
            Reviews = new ReviewService(Path.Combine(root, "state"));
            Projects = projects;
            Rpc = Create();
            var services = new AltaServiceCollection().Add(projects).Add(Reviews);
            var registry = new AltaCommandRegistry();
            Alta = new AltaCommandDispatcher(registry, services);
            services.Add(registry).Add(Alta);
        }

        public ProjectDescriptor Project { get; }
        public ProjectCatalog Projects { get; }
        public ReviewService Reviews { get; }
        public ReviewsService Rpc { get; private set; }
        public AltaCommandDispatcher Alta { get; }
        public RecordingStarter Starter { get; } = new();
        public List<(string SessionId, string Text)> Sent { get; } = [];
        public string? SendProblem { get; set; }
        public Func<Task>? DuringSend { get; set; }

        public OwnedSessionAskService Owner
        {
            get => _owner;
            set
            {
                _owner = value;
                Rpc = Create();
            }
        }

        public static async Task<Fixture> CreateAsync()
        {
            var root = Directory.CreateTempSubdirectory("codealta-review-host-").FullName;
            var options = new CatalogOptions { GlobalRoot = Directory.CreateDirectory(Path.Combine(root, "global")).FullName };
            var projects = new ProjectCatalog(options);
            var folder = Directory.CreateDirectory(Path.Combine(root, "app")).FullName;
            Directory.CreateDirectory(Path.Combine(folder, ".alta", "plans"));
            File.WriteAllText(Path.Combine(folder, ".alta", "plans", "plan.md"), "# Plan\n\n- one\n- two\n");
            var project = await projects.UpsertFromPathAsync(folder);
            return new Fixture(root, projects, project);
        }

        private ReviewsService Create() => new(Reviews, Projects, _owner, async (sessionId, text, _) =>
        {
            if (SendProblem is { } problem) return problem;
            Sent.Add((sessionId, text));
            if (DuringSend is { } during) await during();
            return null;
        }, Starter, Epoch);

        public ValueTask DisposeAsync()
        {
            try
            {
                // Git marks its objects read-only.
                foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(_root, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Best-effort cleanup of a temporary directory.
            }

            return ValueTask.CompletedTask;
        }
    }

    internal sealed class RecordingStarter : ISessionStarter
    {
        public List<(string Title, string Prompt, string? Like, SessionStartModel? Model)> Starts { get; } = [];

        public Task<SessionStartResult> StartAsync(ProjectDescriptor project, string title, Func<string, string> prompt, bool worktree, string? likeSessionId, string origin, SessionStartModel? model)
        {
            Starts.Add((title, prompt(project.ProjectPath), likeSessionId, model));
            return Task.FromResult(new SessionStartResult("started-session", null));
        }
    }
}
