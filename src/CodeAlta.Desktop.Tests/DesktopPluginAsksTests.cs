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
        CollectionAssert.AreEqual(new[] { "session-1" }, (await asks.ListAskingSessionsAsync()).ToArray());
        var ask = await asks.GetPendingAsync("session-1");
        Assert.IsNotNull(ask);
        Assert.AreEqual((queued.AskId, "0", "pending"), (ask.AskId, ask.Revision, ask.State));
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
        Assert.IsEmpty(await asks.ListAskingSessionsAsync());

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

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _root = Directory.CreateTempSubdirectory("codealta-plugin-asks-").FullName;
        private readonly OwnedSessionCommandReceipt _receipt = new("answer", OwnedSessionCommandKind.Send, "session-1");
        private OwnedSessionAskExecution? _send;

        internal Fixture()
        {
            Owner = new OwnedSessionAskService(true, (request, context) =>
            {
                Answered.Add(request);
                context.RecordRunReturned(new("run-answer"));
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
            _receipt.Complete(new(OwnedSessionCommandOutcome.Completed, new AgentRunId("run-answer")));
            _send?.Close();
            Owner.CloseAdmission();
            await Owner.DrainAsync().WaitAsync(Patience);
            try { Directory.Delete(_root, recursive: true); }
            catch (IOException) { /* A file the system still holds stays in the temporary folder. */ }
        }
    }
}
