using CodeAlta.App.Events;
using CodeAlta.App.State;
using CodeAlta.Catalog;
using CodeAlta.LiveTool;
using CodeAlta.Models;
using CodeAlta.ViewModels;
using CodeAlta.Views;
using XenoAtom.Logging;
using XenoAtom.Terminal;
using XenoAtom.Terminal.UI;
using CodeAlta.Presentation.Styling;
using XenoAtom.Terminal.UI.Commands;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Input;
using XenoAtom.Terminal.UI.Styling;

namespace CodeAlta.App;

internal sealed class AskModeCoordinator : IDisposable
{
    private readonly IAltaAskService _askService;
    private readonly ShellSessionStateCoordinator _sessionState;
    private readonly SessionCommandCoordinator _sessionCommands;
    private readonly FrontendEventPublisher _frontendEvents;
    private readonly SessionWorkspaceViewModel _workspaceViewModel;
    private readonly Action<string, bool, StatusTone> _setStatus;
    private readonly IDisposable _subscription;
    private string? _activeAskId;
    private string? _activeSessionId;

    public AskModeCoordinator(
        IAltaAskService askService,
        ShellSessionStateCoordinator sessionState,
        SessionCommandCoordinator sessionCommands,
        FrontendEventPublisher frontendEvents,
        SessionWorkspaceViewModel workspaceViewModel,
        Action<string, bool, StatusTone> setStatus)
    {
        ArgumentNullException.ThrowIfNull(askService);
        ArgumentNullException.ThrowIfNull(sessionState);
        ArgumentNullException.ThrowIfNull(sessionCommands);
        ArgumentNullException.ThrowIfNull(frontendEvents);
        ArgumentNullException.ThrowIfNull(workspaceViewModel);
        ArgumentNullException.ThrowIfNull(setStatus);

        _askService = askService;
        _sessionState = sessionState;
        _sessionCommands = sessionCommands;
        _frontendEvents = frontendEvents;
        _workspaceViewModel = workspaceViewModel;
        _setStatus = setStatus;
        _subscription = _frontendEvents.Subscribe(OnFrontendEvent);
    }

    public void Dispose() => _subscription.Dispose();

    internal bool TryPresentPendingAsk(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        if (_activeAskId is not null)
        {
            return false;
        }

        var ask = _askService.Peek(sessionId);
        if (ask is null || !TryGetIdleSession(sessionId, out var session, out var tab))
        {
            return false;
        }

        _activeAskId = ask.AskId;
        _activeSessionId = sessionId;
        try
        {
            _sessionState.OpenSession(sessionId);
            var form = new AskQuestionFormView(ask);
            var fileReview = AskFileReviewView.Create(ask.Request.File, GetAskFileRootCandidates(session));
            if (fileReview is not null)
            {
                form.AddFileReviewCommands(fileReview);
                fileReview.AddQuestionFocusCommand(form);
            }

            form.Submitted += (_, answers) => HandleSubmitRequest(ask, session, tab, form, fileReview, answers);
            form.CancelRequested += (_, _) => HandleCancelRequest(ask, form, fileReview);
            if (!_workspaceViewModel.TryEnterAskMode(sessionId, form.Root, fileReview?.Root))
            {
                ClearActive();
                return false;
            }

            _setStatus(SR.T("Answer the queued ask, then submit to continue the session."), false, StatusTone.Info);
            _workspaceViewModel.FocusAskModeControl(form.InitialFocusTarget);
            return true;
        }
        catch
        {
            ClearActive();
            throw;
        }
    }

    private void OnFrontendEvent(ShellFrontendEvent frontendEvent)
    {
        switch (frontendEvent)
        {
            case AskQueueChangedEvent ask:
                _ = TryPresentPendingAsk(ask.SessionId);
                break;
            case SessionStatusChangedEvent status:
                _ = TryPresentPendingAsk(status.SessionId);
                break;
            case SelectionChangedEvent:
                if (_sessionState.GetSelectedSession() is { } session)
                {
                    _ = TryPresentPendingAsk(session.SessionId);
                }

                break;
        }
    }

    private void HandleSubmitRequest(
        AltaQueuedAsk ask,
        SessionViewDescriptor session,
        OpenSessionState tab,
        AskQuestionFormView form,
        AskFileReviewView? fileReview,
        IReadOnlyList<AltaAskAnswer> answers)
    {
        if (!IsActive(ask))
        {
            return;
        }

        if (fileReview?.HasUnsavedChanges == true)
        {
            ShowUnsavedFileDialog(
                SR.T("Submit Ask"),
                SR.T("The attached file has unsaved edits. Save them before submitting the ask response?"),
                SR.T("Save and submit"),
                ControlTone.Primary,
                SR.T("Submit without saving"),
                ControlTone.Warning,
                form.Tabs,
                () =>
                {
                    if (!fileReview.TrySave(out var error))
                    {
                        _setStatus(SR.T("Failed to save attached ask file: {0}", error), false, StatusTone.Error);
                        return;
                    }

                    ObserveSubmit(ask, session, tab, answers, fileReview);
                },
                () => ObserveSubmit(ask, session, tab, answers, fileReview));
            return;
        }

        ObserveSubmit(ask, session, tab, answers, fileReview);
    }

    private void ObserveSubmit(AltaQueuedAsk ask, SessionViewDescriptor session, OpenSessionState tab, IReadOnlyList<AltaAskAnswer> answers, AskFileReviewView? fileReview)
        => _ = UiTaskDiagnostics.ObserveAsync(
            () => SubmitAsync(ask, session, tab, answers, fileReview?.CreateReviewSnapshot()),
            SR.T("submit ask response"),
            _setStatus);

    private async Task SubmitAsync(AltaQueuedAsk ask, SessionViewDescriptor session, OpenSessionState tab, IReadOnlyList<AltaAskAnswer> answers, AltaAskFileReview? fileReview)
    {
        if (!IsActive(ask))
        {
            return;
        }

        var markdown = AltaAskAnswerMarkdownFormatter.Format(ask.Request, answers, fileReview);
        try
        {
            RestoreNormalProjection(ask.SessionId);
            await _sessionCommands.SendAskResponseAsync(session, tab, markdown, ask.AskId);
            _ = _askService.Dequeue(ask.SessionId);
            ClearActive();
            _ = TryPresentPendingAsk(ask.SessionId);
        }
        catch (Exception ex)
        {
            CodeAltaApp.UiLogger.Error(ex, $"Failed to submit ask response for session {ask.SessionId}");
            _activeAskId = null;
            _activeSessionId = null;
            _setStatus(SR.T("Failed to submit ask response: {0}", ex.Message), false, StatusTone.Error);
            _ = TryPresentPendingAsk(ask.SessionId);
        }
    }

    private void HandleCancelRequest(AltaQueuedAsk ask, AskQuestionFormView form, AskFileReviewView? fileReview)
    {
        if (!IsActive(ask))
        {
            return;
        }

        if (fileReview?.HasUnsavedChanges == true)
        {
            ShowUnsavedFileDialog(
                SR.T("Cancel Ask"),
                SR.T("The attached file has unsaved edits. Save them before exiting ask mode?"),
                SR.T("Save and exit"),
                ControlTone.Primary,
                SR.T("Exit without saving"),
                ControlTone.Error,
                form.Tabs,
                () =>
                {
                    if (!fileReview.TrySave(out var error))
                    {
                        _setStatus(SR.T("Failed to save attached ask file: {0}", error), false, StatusTone.Error);
                        return;
                    }

                    Cancel(ask);
                },
                () => Cancel(ask));
            return;
        }

        ShowCancelConfirmation(ask, form);
    }

    private void Cancel(AltaQueuedAsk ask)
    {
        if (!IsActive(ask))
        {
            return;
        }

        _ = _askService.Dequeue(ask.SessionId);
        RestoreNormalProjection(ask.SessionId);
        ClearActive();
        _setStatus(SR.T("Ask canceled; no response was sent."), false, StatusTone.Warning);
        _ = TryPresentPendingAsk(ask.SessionId);
    }

    private void ShowCancelConfirmation(AltaQueuedAsk ask, AskQuestionFormView form)
    {
        if (!IsActive(ask))
        {
            return;
        }

        new ConfirmationDialog(
            SR.T("Cancel Ask"),
            [
                SR.T("Exit ask mode without sending a response?"),
                SR.T("The queued ask will be canceled locally and the session will return to the normal prompt editor."),
            ],
            SR.T("Exit without responding"),
            ControlTone.Warning,
            () =>
            {
                Cancel(ask);
                return Task.CompletedTask;
            },
            _workspaceViewModel.GetAskModeBounds,
            () => form.Tabs)
            .Show();
    }

    private void ShowUnsavedFileDialog(
        string title,
        string message,
        string saveText,
        ControlTone saveTone,
        string discardText,
        ControlTone discardTone,
        Visual focusTarget,
        Action saveAndContinue,
        Action discardAndContinue)
    {
        Dialog? dialog = null;
        var closeButton = new Button(new TextBlock($"{TerminalIcons.MdClose} {SR.T("Close")}"));
        closeButton.Click(Close);

        var keepButton = new Button(SR.T("Keep answering"));
        keepButton.Click(Close);

        var discardButton = new Button(discardText) { Tone = discardTone };
        discardButton.Click(() =>
        {
            Close();
            discardAndContinue();
        });

        var saveButton = new Button(saveText) { Tone = saveTone };
        saveButton.Click(() =>
        {
            Close();
            saveAndContinue();
        });

        var buttons = new HStack(keepButton, discardButton, saveButton)
        {
            HorizontalAlignment = Align.End,
            Spacing = 2,
        };

        dialog = new Dialog()
            .Title(title)
            .TopRightText(closeButton)
            .IsModal(true)
            .Padding(1)
            .Content(new DockLayout()
                .Content(new ScrollViewer(new TextBlock(message).Wrap(true), focusable: false).Stretch())
                .Bottom(buttons)
                .HorizontalAlignment(Align.Stretch)
                .VerticalAlignment(Align.Stretch));
        ResponsiveDialogSize.Apply(dialog, _workspaceViewModel.GetAskModeBounds(), minWidth: 56, minHeight: 9, widthFactor: 0.34, heightFactor: 0.28);
        dialog.AddCommand(new Command
        {
            Id = "CodeAlta.Ask.UnsavedFile.Close",
            LabelMarkup = SR.T("Keep answering"),
            DescriptionMarkup = SR.T("Close the save prompt and return to ask mode."),
            Gesture = new KeyGesture(TerminalKey.Escape),
            Importance = CommandImportance.Primary,
            Execute = _ => Close(),
        });
        dialog.Show();

        void Close()
        {
            var app = dialog?.App;
            dialog?.Close();
            app?.Focus(focusTarget);
        }
    }

    private bool TryGetIdleSession(string sessionId, out SessionViewDescriptor session, out OpenSessionState tab)
    {
        session = null!;
        tab = null!;
        var candidate = _sessionState.FindSession(sessionId);
        if (candidate is null)
        {
            return false;
        }

        var openTab = _sessionState.EnsureSessionTab(candidate);
        if (openTab.StatusBusy || openTab.ActiveRunId is not null || openTab.ActiveRunStartedAt is not null)
        {
            return false;
        }

        session = candidate;
        tab = openTab;
        return true;
    }

    private IReadOnlyList<string> GetAskFileRootCandidates(SessionViewDescriptor session)
    {
        var roots = new List<string>();
        AddRoot(session.WorkingDirectory);
        if (_sessionState.GetProjectById(session.ProjectRef)?.ProjectPath is { } projectPath)
        {
            AddRoot(projectPath);
        }

        if (roots.Count == 0)
        {
            roots.Add(Environment.CurrentDirectory);
        }

        return roots;

        void AddRoot(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            var fullPath = Path.GetFullPath(path);
            if (!roots.Contains(fullPath, StringComparer.OrdinalIgnoreCase))
            {
                roots.Add(fullPath);
            }
        }
    }

    private bool IsActive(AltaQueuedAsk ask)
        => string.Equals(_activeAskId, ask.AskId, StringComparison.Ordinal) && string.Equals(_activeSessionId, ask.SessionId, StringComparison.Ordinal);

    private void RestoreNormalProjection(string sessionId)
        => _workspaceViewModel.ExitAskMode(sessionId);

    private void ClearActive()
    {
        _activeAskId = null;
        _activeSessionId = null;
    }

}
