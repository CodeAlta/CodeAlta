using System.Threading.Channels;
using CodeAlta.Agent;
using CodeAlta.Orchestration.Runtime;
using CodeAlta.Tui.App;
using CodeAlta.Tui.Threading;
using XenoAtom.Terminal;
using XenoAtom.Terminal.UI.Controls;

namespace CodeAlta.Tests;

/// <summary>
/// The terminal dialog of a run whose Claude Code conversation another process holds: shown on the UI thread for each
/// question, answered through the held service, closed when the question ends without it.
/// </summary>
[TestClass]
public sealed class SessionConversationHeldCoordinatorTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private static readonly ModelProviderId Provider = new("claude-code");

    [TestMethod]
    public async Task Question_IsShown_AndTheChoiceAnswersIt()
    {
        await using var fixture = new Fixture();
        var shown = new List<(SessionConversationHeldSnapshot Question, Action<AgentConversationHeldChoice> Answer, Closing Dialog)>();
        using var coordinator = new SessionConversationHeldCoordinator(fixture.Dispatcher, fixture.Service, (question, answer) =>
        {
            var dialog = new Closing();
            shown.Add((question, answer, dialog));
            return dialog;
        });

        var ask = fixture.Service.Handler(Request(), CancellationToken.None);
        fixture.Dispatcher.Take().Run();
        var (question, answer, dialog) = shown.Single();
        Assert.AreEqual("conversation-1", question.ConversationId);
        Assert.IsFalse(ask.IsCompleted, "The run waits for the answer.");

        answer(AgentConversationHeldChoice.ContinueAsCopy);
        Assert.AreEqual(AgentConversationHeldChoice.ContinueAsCopy, await ask.WaitAsync(Timeout));
        fixture.Dispatcher.Take().Run(); // The dialog is closed on the UI thread.
        Assert.IsTrue(dialog.Closed);
        Assert.AreEqual(0, (await fixture.Service.ListAsync("session", default)).Count);
    }

    [TestMethod]
    public async Task Question_ThatEndsWithItsRun_ClosesTheDialogWithoutAnswering()
    {
        await using var fixture = new Fixture();
        var dialogs = new List<Closing>();
        using var coordinator = new SessionConversationHeldCoordinator(fixture.Dispatcher, fixture.Service, (_, _) =>
        {
            var dialog = new Closing();
            dialogs.Add(dialog);
            return dialog;
        });
        using var run = new CancellationTokenSource();
        var ask = fixture.Service.Handler(Request(), run.Token);
        fixture.Dispatcher.Take().Run();

        await run.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => ask.WaitAsync(Timeout));
        fixture.Dispatcher.Take().Run();
        Assert.IsTrue(dialogs.Single().Closed);
    }

    [TestMethod]
    public async Task Question_ThatEndedWhileQueued_IsNotShown()
    {
        await using var fixture = new Fixture();
        var shown = 0;
        using var coordinator = new SessionConversationHeldCoordinator(fixture.Dispatcher, fixture.Service, (_, _) => { shown++; return new Closing(); });
        using var run = new CancellationTokenSource();
        var ask = fixture.Service.Handler(Request(), run.Token);
        var show = fixture.Dispatcher.Take();
        await run.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => ask.WaitAsync(Timeout));
        show.Run();
        Assert.AreEqual(0, shown);
    }

    [TestMethod]
    public void Dialog_OffersOnlyTheChoicesOfTheQuestion_AndEscapeCancelsTheRun()
    {
        // Synchronous: the terminal is used on this thread only.
        var fixture = new Fixture();
        using var terminal = new PermissionTerminalFixture();
        using var coordinator = new SessionConversationHeldCoordinator(fixture.Dispatcher, fixture.Service);

        var ask = fixture.Service.Handler(Request(new AgentConversationHolder(AgentConversationHolderOrigin.BackgroundSession, "a background session of Claude Code", 4100),
            [AgentConversationHeldChoice.Wait, AgentConversationHeldChoice.ContinueAsCopy, AgentConversationHeldChoice.Cancel]), CancellationToken.None);
        fixture.Dispatcher.Take().Run();
        terminal.Tick();
        Assert.AreEqual(1, terminal.DialogCount);
        var dialog = terminal.App.Root.EnumerateVisualsDepthFirst().OfType<Dialog>().Single();
        var buttons = dialog.EnumerateVisualsDepthFirst().OfType<Button>().ToArray();
        Assert.AreEqual(2, buttons.Length, "Wait and Continue as a copy: a background session cannot be opened anyway; Cancel is Escape.");

        terminal.Key(TerminalKey.Escape);
        Assert.AreEqual(AgentConversationHeldChoice.Cancel, ask.WaitAsync(Timeout).GetAwaiter().GetResult());
        fixture.Dispatcher.Take().Run();
        terminal.Tick();
        Assert.AreEqual(0, terminal.DialogCount);
        fixture.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    private static AgentConversationHeldRequest Request(AgentConversationHolder? holder = null, IReadOnlyList<AgentConversationHeldChoice>? choices = null)
        => new(Provider, "session", new AgentRunId("run-1"), "request-1", DateTimeOffset.UnixEpoch, "conversation-1",
            holder ?? new AgentConversationHolder(AgentConversationHolderOrigin.VsCode, "VS Code", 4100, "claude-vscode"),
            choices ?? [AgentConversationHeldChoice.Wait, AgentConversationHeldChoice.ContinueAsCopy, AgentConversationHeldChoice.OpenAnyway, AgentConversationHeldChoice.Cancel]);

    private sealed class Closing : IDisposable
    {
        public bool Closed { get; private set; }
        public void Dispose() => Closed = true;
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public Fixture()
        {
            var attachment = Forwarding.RegisterAttachment("session", "handle", static () => Task.CompletedTask, static () => Task.CompletedTask);
            attachment.CompleteSetup();
            var runtime = Guid.NewGuid();
            Service.ResolveTarget = sessionId => sessionId == "session" ? new(runtime, attachment, Provider) : null;
        }

        public SessionConversationHeldService Service { get; } = new();
        public OwnedProviderEventForwarding Forwarding { get; } = new();
        public ControlledDispatcher Dispatcher { get; } = new();
        public async ValueTask DisposeAsync() => await Service.DisposeAsync();
    }

    // The UI work the coordinator queues, run by the test on its own thread when it chooses.
    private sealed class ControlledDispatcher : IUiDispatcher
    {
        private readonly int _owner = Environment.CurrentManagedThreadId;
        private readonly Channel<Work> _work = Channel.CreateUnbounded<Work>();
        public bool CheckAccess() => Environment.CurrentManagedThreadId == _owner;
        public void Post(Action action) => throw new AssertFailedException("No unjoined UI posts are allowed.");
        public Task InvokeAsync(Action action)
        {
            var work = new Work(action);
            Assert.IsTrue(_work.Writer.TryWrite(work));
            return work.Completion.Task;
        }

        public Task<T> InvokeAsync<T>(Func<T> action) => throw new AssertFailedException("Unexpected background UI read.");
        public Work Take() => _work.Reader.ReadAsync().AsTask().WaitAsync(Timeout).GetAwaiter().GetResult();
    }

    private sealed class Work(Action action)
    {
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Run()
        {
            try { action(); Completion.SetResult(); }
            catch (Exception exception) { Completion.SetException(exception); }
        }
    }
}
