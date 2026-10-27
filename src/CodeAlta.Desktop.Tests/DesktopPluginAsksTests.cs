using CodeAlta.Agent;
using CodeAlta.Catalog;
using CodeAlta.Catalog.Reviews;
using CodeAlta.Desktop.Rpc;
using CodeAlta.LiveTool;
using CodeAlta.Orchestration.Runtime;
using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Desktop.Tests;

/// <summary>The asks the plugins of the window read and answer for the user, as the window does: the page of the phones.</summary>
[TestClass]
public sealed class DesktopPluginAsksTests
{
    private const string Plan = ".alta/plans/plan.md";
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    [TestMethod]
    public async Task AQuestion_IsReadAndAnsweredOnce_InTheRevisionTheUserSaw()
    {
        await using var fixture = new Fixture();
        var asks = new DesktopPluginAsks();
        Assert.IsFalse(asks.HasAsks, "No host yet.");
        Assert.IsNull(await asks.GetPendingAsync("session-1"));
        Assert.AreEqual("unavailable", (await asks.AnswerAsync(new(Guid.NewGuid(), "session-1", "ask", "0"), [], null)).Status);

        await fixture.StartRunAsync();
        var queued = fixture.Owner.QueueFromSession("session-1", new AltaAskRequest
        {
            Questions = [new() { Title = "Database", Question = "Which database?", Description = "For the cache.",
                Choices = [new() { Title = "SQLite" }, new() { Title = "Postgres", Description = "A server." }], Freeform = new() { Title = "Why" } }],
        })!;
        asks.Attach(fixture.Owner, fixture.Window, fixture.Epoch);

        Assert.IsTrue(asks.HasAsks);
        CollectionAssert.AreEqual(new[] { new PluginAskSummary("session-1", queued.AskId, "0", "pending", false) }, (await asks.ListAskingAsync()).ToArray());
        var ask = await asks.GetPendingAsync("session-1");
        Assert.IsNotNull(ask);
        Assert.AreEqual((queued.AskId, "0", "pending", false), (ask.AskId, ask.Revision, ask.State, ask.Immediate));
        var question = ask.Questions.Single();
        Assert.AreEqual(("Database", "Which database?", "For the cache.", "Why"), (question.Title, question.Question, question.Description, question.Freeform!.Title));
        Assert.AreEqual(("Postgres", "A server."), (question.Choices[1].Title, question.Choices[1].Description));
        Assert.IsNull(ask.Review);

        PluginAskAnswer[] answers = [new() { QuestionIndex = 0, SelectedChoiceIndexes = [1], FreeformText = "It is small." }];
        Assert.AreEqual("rejected", (await asks.AnswerAsync(new(Guid.NewGuid(), "session-1", ask.AskId, "1"), answers, null)).Status, "Another revision of the ask.");
        Assert.AreEqual("rejected", (await asks.AnswerAsync(new(Guid.NewGuid(), "session-1", "another", ask.Revision), answers, null)).Status, "Another ask.");
        Assert.AreEqual("invalid", (await asks.AnswerAsync(new(Guid.NewGuid(), "session-1", ask.AskId, ask.Revision), answers, new("approved", null))).Status, "A verdict for a question.");
        Assert.AreEqual("invalid", (await asks.AnswerAsync(new(Guid.Empty, "session-1", ask.AskId, ask.Revision), answers, null)).Status);
        Assert.IsEmpty(fixture.Answered, "Nothing refused reaches the session.");

        var reference = new PluginAskReference(Guid.NewGuid(), "session-1", ask.AskId, ask.Revision);
        Assert.AreEqual("admitted", (await asks.AnswerAsync(reference, answers, null)).Status);
        StringAssert.Contains(fixture.Answered.Single().Text, "Postgres");
        StringAssert.Contains(fixture.Answered.Single().Text, "It is small.");
        Assert.IsNull(await asks.GetPendingAsync("session-1"));
        Assert.IsEmpty(await asks.ListAskingAsync());

        Assert.AreEqual("admitted", (await asks.AnswerAsync(reference, answers, null)).Status, "The same answer sent again gets the outcome of the first.");
        Assert.HasCount(1, fixture.Answered, "and is not sent twice.");
    }

    [TestMethod]
    public async Task ACancel_LeavesTheSessionWithoutAPrompt()
    {
        await using var fixture = new Fixture();
        await fixture.StartRunAsync();
        fixture.Owner.QueueFromSession("session-1", new AltaAskRequest { Questions = [new() { Title = "Go", Question = "Go on?", Choices = [new() { Title = "Yes" }] }] });
        var asks = new DesktopPluginAsks();
        asks.Attach(fixture.Owner, fixture.Window, fixture.Epoch);
        var ask = (await asks.GetPendingAsync("session-1"))!;

        Assert.AreEqual("cancelled", (await asks.CancelAsync(new(Guid.NewGuid(), "session-1", ask.AskId, ask.Revision))).Status);
        Assert.IsEmpty(fixture.Answered);
        Assert.IsNull(await asks.GetPendingAsync("session-1"));

        fixture.Window.CloseAdmission();
        Assert.AreEqual("unavailable", (await asks.CancelAsync(new(Guid.NewGuid(), "session-1", ask.AskId, ask.Revision))).Status, "A window that closes takes no answer.");
    }

    [TestMethod]
    public async Task AReviewRequest_SaysTheCommentsOfTheUser_AndItsAnswerSendsThemWithTheVerdict()
    {
        await using var fixture = new Fixture();
        var project = await fixture.CreateProjectAsync();
        var scope = new ReviewScope(project, null);
        await fixture.Reviews.AddThreadAsync(scope, new ReviewThreadDraft("file", Plan, "new", 1, 1, "Rename it"), default);
        await fixture.StartRunAsync();
        fixture.Owner.QueueFromSession("session-1", new AltaAskRequest
        {
            Review = new() { Kind = "file", Path = Plan, Title = "The plan", Message = "Is it **right**?", ProjectId = project.Id },
        });
        var asks = new DesktopPluginAsks();
        asks.Attach(fixture.Owner, fixture.Window, fixture.Epoch);

        var ask = (await asks.GetPendingAsync("session-1"))!;
        Assert.IsEmpty(ask.Questions);
        Assert.AreEqual(new PluginAskReview { Kind = "file", Path = Plan, Title = "The plan", Message = "Is it **right**?", Comments = 1 }, ask.Review);

        var outcome = await asks.AnswerAsync(new(Guid.NewGuid(), "session-1", ask.AskId, ask.Revision), [], new("changes_requested", "Shorter."));
        Assert.AreEqual("admitted", outcome.Status);
        var round = fixture.Answered.Single().Text;
        StringAssert.StartsWith(round, "# Review of `.alta/plans/plan.md`: changes requested");
        StringAssert.Contains(round, "Rename it");
        StringAssert.Contains(round, "Shorter.");
    }

    [TestMethod]
    public async Task AnAnswerSentAgainWhileTheFirstGoes_WaitsForTheOutcomeOfTheFirst_AndIsSentOnce()
    {
        await using var fixture = new Fixture { HoldDeliveries = true };
        await fixture.StartRunAsync();
        fixture.Owner.QueueFromSession("session-1", new AltaAskRequest { Questions = [new() { Title = "Go", Question = "Go on?", Choices = [new() { Title = "Yes" }] }] });
        var asks = new DesktopPluginAsks();
        asks.Attach(fixture.Owner, fixture.Window, fixture.Epoch);
        var ask = (await asks.GetPendingAsync("session-1"))!;
        var reference = new PluginAskReference(Guid.NewGuid(), "session-1", ask.AskId, ask.Revision);
        PluginAskAnswer[] answers = [new() { QuestionIndex = 0, SelectedChoiceIndexes = [0] }];

        var first = asks.AnswerAsync(reference, answers, null).AsTask();
        await fixture.WaitAsync(() => fixture.Answered.Count == 1, "the answer goes to the session");
        Assert.AreEqual("submitting", (await asks.ListAskingAsync()).Single().State, "the phones are told the answer goes");
        var retry = asks.AnswerAsync(reference, answers, null).AsTask();
        Assert.AreEqual("conflict", (await asks.CancelAsync(reference)).Status, "a cancel with the id of the answer");
        Assert.IsFalse(retry.IsCompleted, "the retry waits for the first: it is never said submitting, nor sent again");

        fixture.Deliver();
        Assert.AreEqual(("admitted", "admitted"), ((await first).Status, (await retry).Status));
        Assert.HasCount(1, fixture.Answered);
    }

    [TestMethod]
    public async Task AnAnswerTheSessionDidNotTake_LeavesTheAskWaitingInANewRevision_WhichIsAnswered()
    {
        await using var fixture = new Fixture { Refuse = true };
        await fixture.StartRunAsync();
        fixture.Owner.QueueFromSession("session-1", new AltaAskRequest { Questions = [new() { Title = "Go", Question = "Go on?", Freeform = new() }] });
        var asks = new DesktopPluginAsks();
        asks.Attach(fixture.Owner, fixture.Window, fixture.Epoch);
        var ask = (await asks.GetPendingAsync("session-1"))!;
        PluginAskAnswer[] answers = [new() { QuestionIndex = 0, FreeformText = "Yes." }];

        Assert.AreEqual("not_admitted", (await asks.AnswerAsync(new(Guid.NewGuid(), "session-1", ask.AskId, ask.Revision), answers, null)).Status);
        var again = (await asks.GetPendingAsync("session-1"))!;
        Assert.AreEqual((ask.AskId, "pending"), (again.AskId, again.State), "the ask waits again");
        Assert.AreNotEqual(ask.Revision, again.Revision, "in a new revision");
        Assert.AreEqual(again.Revision, (await asks.ListAskingAsync()).Single().Revision, "which the phones are told");
        Assert.AreEqual("rejected", (await asks.AnswerAsync(new(Guid.NewGuid(), "session-1", ask.AskId, ask.Revision), answers, null)).Status, "the revision of before");

        fixture.Refuse = false;
        Assert.AreEqual("admitted", (await asks.AnswerAsync(new(Guid.NewGuid(), "session-1", again.AskId, again.Revision), answers, null)).Status);
        Assert.IsNull(await asks.GetPendingAsync("session-1"));
    }

    [TestMethod]
    public async Task AQuestionOfAScript_IsImmediate_AndItsAnswerGoesToTheScript()
    {
        await using var fixture = new Fixture();
        var asks = new DesktopPluginAsks();
        asks.Attach(fixture.Owner, fixture.Window, fixture.Epoch);
        var origin = new OwnedAskHandle(Guid.Empty, Guid.NewGuid(), 1, "fixture", "session-1", "run-script", "", 0);
        var script = fixture.Owner.AskForScriptAsync(origin, new AltaAskRequest { Questions = [new() { Title = "Go", Question = "Go on?", Choices = [new() { Title = "Yes" }] }] }, default);

        var ask = (await asks.GetPendingAsync("session-1"))!;
        Assert.IsTrue(ask.Immediate, "shown while the turn of its session runs");
        Assert.IsTrue((await asks.ListAskingAsync()).Single().Immediate);
        Assert.AreEqual("admitted", (await asks.AnswerAsync(new(Guid.NewGuid(), "session-1", ask.AskId, ask.Revision), [new() { QuestionIndex = 0, SelectedChoiceIndexes = [0] }], null)).Status);
        Assert.AreEqual(0, (await script.WaitAsync(Patience))!.Single().SelectedChoiceIndexes.Single());
        Assert.IsEmpty(fixture.Answered, "no prompt is sent");
    }

    [TestMethod]
    public void OnlyTheBuiltInMobilePlugin_GetsTheAsks()
    {
        var asks = new DesktopPluginAsks();
        Assert.AreSame(asks, asks.ForPlugin("builtin:mobile", builtIn: true));
        Assert.AreSame(NoopPluginAskService.Instance, asks.ForPlugin("builtin:mobile", builtIn: false), "a plugin of a folder whose key looks like it");
        Assert.AreSame(NoopPluginAskService.Instance, asks.ForPlugin("builtin:git", builtIn: true));
        Assert.AreSame(NoopPluginAskService.Instance, asks.ForPlugin("MyPlugin:MyPlugin.Answers", builtIn: false));
    }

    [TestMethod]
    public void TheLimitsOfAnAnswer_AreThoseTheHostChecks()
    {
        CollectionAssert.AreEqual(new[] { ReviewService.MaximumText }, new[] { PluginAskLimits.OverallComment }, "the overall comment has the limit of the review store");
        var request = new AltaAskRequest { Questions = [new() { Title = "A", Question = "A?", Freeform = new() }, new() { Title = "B", Question = "B?", Freeform = new() }] };
        var half = new string('a', PluginAskLimits.AnswerText / 2);
        OwnedSessionAskTool.CaptureAnswers(request, [new() { QuestionIndex = 0, FreeformText = half }, new() { QuestionIndex = 1, FreeformText = half }]);
        Assert.ThrowsExactly<ArgumentException>(() => OwnedSessionAskTool.CaptureAnswers(request, [new() { QuestionIndex = 0, FreeformText = half }, new() { QuestionIndex = 1, FreeformText = half + "a" }]));
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _root = Directory.CreateTempSubdirectory("codealta-plugin-asks-").FullName;
        private readonly OwnedSessionCommandReceipt _receipt = new("answer", OwnedSessionCommandKind.Send, "session-1");
        private OwnedSessionAskExecution? _send;

        internal Fixture()
        {
            Owner = new OwnedSessionAskService(true, (request, context) =>
            {
                if (Refuse) return new(OwnedSessionCommandAdmissionKind.Conflict, null);
                lock (Answered) Answered.Add(request);
                if (HoldDeliveries) lock (_held) _held.Add(context);
                else context.RecordRunReturned(new("run-answer"));
                return new(OwnedSessionCommandAdmissionKind.Accepted, _receipt);
            });
            Reviews = new ReviewService(Path.Combine(_root, "state"));
            Projects = new ProjectCatalog(new CatalogOptions { GlobalRoot = Directory.CreateDirectory(Path.Combine(_root, "global")).FullName });
            Window = new SessionAsksService(Owner, Epoch, Reviews, Projects);
        }

        internal string Epoch { get; } = Guid.NewGuid().ToString("D");

        internal OwnedSessionAskService Owner { get; }

        internal SessionAsksService Window { get; }

        internal ReviewService Reviews { get; }

        internal ProjectCatalog Projects { get; }

        internal List<OwnedTextSendRequest> Answered { get; } = [];

        /// <summary>Gets or sets whether the session takes no answer: the ask waits again.</summary>
        internal bool Refuse { get; set; }

        /// <summary>Gets or sets whether the runs that carry the answers start only when <see cref="Deliver"/> says.</summary>
        internal bool HoldDeliveries { get; set; }

        private readonly List<OwnedAskSubmission> _held = [];

        // The runs held start: the session has the answers.
        internal void Deliver()
        {
            lock (_held)
            {
                foreach (var context in _held) context.RecordRunReturned(new("run-answer"));
                _held.Clear();
            }
        }

        internal async Task WaitAsync(Func<bool> condition, string what)
        {
            using var deadline = new CancellationTokenSource(Patience);
            while (!condition())
            {
                Assert.IsFalse(deadline.IsCancellationRequested, "Waited for " + what);
                await Task.Delay(10);
            }
        }

        // The run of session-1 that asks: an ask is queued from a run of its session.
        internal async Task StartRunAsync()
        {
            _send = Owner.CreateExecution(Guid.NewGuid(), "session-1", default);
            _send.Bind(Guid.NewGuid(), 1, new("fixture"));
            await _send.StartedAsync(new("run"), default);
        }

        internal async Task<ProjectDescriptor> CreateProjectAsync()
        {
            var folder = Directory.CreateDirectory(Path.Combine(_root, "app")).FullName;
            Directory.CreateDirectory(Path.Combine(folder, ".alta", "plans"));
            await File.WriteAllTextAsync(Path.Combine(folder, ".alta", "plans", "plan.md"), "# Plan\n\n- one\n- two\n");
            return await Projects.UpsertFromPathAsync(folder);
        }

        public async ValueTask DisposeAsync()
        {
            Deliver();
            _receipt.Complete(new(OwnedSessionCommandOutcome.Completed, new AgentRunId("run-answer")));
            _send?.Close();
            Owner.CloseAdmission();
            await Owner.DrainAsync().WaitAsync(Patience);
            try { Directory.Delete(_root, recursive: true); }
            catch (IOException) { /* A file the system still holds stays in the temporary folder. */ }
        }
    }
}
