using CodeAlta.Agent;
using CodeAlta.Orchestration.Runtime;
using CodeAlta.Tui.Threading;
using CodeAlta.Tui.Views;

namespace CodeAlta.Tui.App;

/// <summary>
/// Shows the question of a run whose Claude Code conversation another process holds (VS Code, a terminal) as a dialog,
/// and answers it through <see cref="SessionConversationHeldService.ResolveAsync"/>. The dialog closes when the question
/// ends without it: its run stopped, or it was answered elsewhere. The wait that follows is shown by the status line,
/// from the session updates of the run.
/// </summary>
internal sealed class SessionConversationHeldCoordinator : IDisposable
{
    private readonly IUiDispatcher _uiDispatcher;
    private readonly SessionConversationHeldService _holds;
    private readonly Func<SessionConversationHeldSnapshot, Action<AgentConversationHeldChoice>, IDisposable> _show;
    private readonly IDisposable _observation;

    public SessionConversationHeldCoordinator(IUiDispatcher uiDispatcher, SessionConversationHeldService holds)
        : this(uiDispatcher, holds, static (question, answer) =>
        {
            var dialog = new ConversationHeldDialog(question, answer, getBounds: () => null, getFocusTarget: () => null);
            dialog.Show();
            return dialog;
        })
    {
    }

    /// <summary>Initializes a coordinator that shows its questions with <paramref name="show"/>, on the UI thread.</summary>
    internal SessionConversationHeldCoordinator(
        IUiDispatcher uiDispatcher,
        SessionConversationHeldService holds,
        Func<SessionConversationHeldSnapshot, Action<AgentConversationHeldChoice>, IDisposable> show)
    {
        ArgumentNullException.ThrowIfNull(uiDispatcher);
        ArgumentNullException.ThrowIfNull(holds);
        ArgumentNullException.ThrowIfNull(show);

        _uiDispatcher = uiDispatcher;
        _holds = holds;
        _show = show;
        _observation = holds.Observe(ShowAsync);
    }

    private async Task ShowAsync(SessionConversationHeldSnapshot question, CancellationToken ended)
    {
        var answered = new TaskCompletionSource<AgentConversationHeldChoice>(TaskCreationOptions.RunContinuationsAsynchronously);
        IDisposable? dialog = null;
        try
        {
            // The guard runs on the UI thread: a question that ended while queued is not shown late.
            await _uiDispatcher.InvokeAsync(() =>
            {
                if (!ended.IsCancellationRequested)
                {
                    dialog = _show(question, choice => answered.TrySetResult(choice));
                }
            }).ConfigureAwait(false);
            if (dialog is null)
            {
                return;
            }

            AgentConversationHeldChoice choice;
            try
            {
                choice = await answered.Task.WaitAsync(ended).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ended.IsCancellationRequested)
            {
                return;
            }

            // An answer that is not taken (the question ended meanwhile) changes nothing: the run already went on.
            await _holds.ResolveAsync(question.Handle, choice, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            if (dialog is { } shown)
            {
                await _uiDispatcher.InvokeAsync(shown.Dispose).ConfigureAwait(false);
            }
        }
    }

    public void Dispose() => _observation.Dispose();
}
