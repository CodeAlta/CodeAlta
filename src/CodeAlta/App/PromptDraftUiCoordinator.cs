using CodeAlta.Models;
using CodeAlta.App.Events;
using CodeAlta.Catalog;
using CodeAlta.Presentation.Prompting;
using CodeAlta.ViewModels;
using XenoAtom.Terminal.UI;

namespace CodeAlta.App;

internal sealed class PromptDraftUiCoordinator : IAsyncDisposable
{
    private const string DraftPromptSessionId = "__draft__";

    private readonly PromptDraftCoordinator _promptDrafts;
    private readonly ThreadPromptDraftPersistenceCoordinator _promptDraftPersistence;
    private readonly Func<ShellSelection> _getSelection;
    private readonly FrontendEventPublisher _frontendEvents;
    private readonly Action _onPromptImageAttachmentsChanged;
    private readonly Dictionary<string, PromptDraftSessionState> _promptSessions = new(StringComparer.OrdinalIgnoreCase);
    private string _activePromptSessionId = DraftPromptSessionId;

    public PromptDraftUiCoordinator(
        PromptDraftCoordinator promptDrafts,
        CatalogOptions catalogOptions,
        Func<ShellSelection> getSelection,
        FrontendEventPublisher frontendEvents,
        Action? onPromptImageAttachmentsChanged = null)
    {
        ArgumentNullException.ThrowIfNull(promptDrafts);
        ArgumentNullException.ThrowIfNull(catalogOptions);
        ArgumentNullException.ThrowIfNull(getSelection);
        ArgumentNullException.ThrowIfNull(frontendEvents);

        _promptDrafts = promptDrafts;
        _promptDraftPersistence = new ThreadPromptDraftPersistenceCoordinator(catalogOptions);
        _getSelection = getSelection;
        _frontendEvents = frontendEvents;
        _onPromptImageAttachmentsChanged = onPromptImageAttachmentsChanged ?? (static () => { });
    }

    public Binding<string?> PromptTextBinding => GetActivePromptState().ViewModel.Bind.PromptText;

    public string? PromptText
    {
        get => GetActivePromptState().ViewModel.PromptText;
        set => GetActivePromptState().ViewModel.PromptText = value ?? string.Empty;
    }

    public Binding<string?> GetPromptTextBinding(string promptSessionId, ThreadSessionState? session)
        => GetOrCreatePromptState(promptSessionId, session).ViewModel.Bind.PromptText;

    public void SyncPromptText(ThreadSessionState? session)
    {
        var previousImageList = GetCurrentImageList(GetActivePromptState());
        _activePromptSessionId = session is null ? DraftPromptSessionId : ResolveCurrentPromptSessionId();
        var activeState = GetOrCreatePromptState(_activePromptSessionId, session);
        SyncPromptTextFromSession(activeState);

        if (!ReferenceEquals(previousImageList, GetCurrentImageList(activeState)))
        {
            _onPromptImageAttachmentsChanged();
        }
    }

    public void ClearPromptText()
        => PromptText = string.Empty;

    public void ClearPrompt()
    {
        var state = GetActivePromptState();
        ClearPromptText(state);
        ClearPromptImages(state);
    }

    public void ClearDraftPromptText()
    {
        var state = GetOrCreatePromptState(DraftPromptSessionId, session: null);
        _promptDrafts.RememberPrompt(null, string.Empty);
        ClearPromptText(state);
        ClearPromptImages(state);
    }

    public IReadOnlyList<PromptImageAttachment> CurrentPromptImages
        => GetCurrentImageList(GetActivePromptState());

    public bool HasCurrentPromptImages => GetCurrentImageList(GetActivePromptState()).Count > 0;

    public IReadOnlyList<PromptImageAttachment> GetPromptImages(string promptSessionId, ThreadSessionState? session)
        => GetCurrentImageList(GetOrCreatePromptState(promptSessionId, session));

    public string GetNextImageTitle()
        => GetNextImageTitle(GetActivePromptState());

    public string GetNextImageTitle(string promptSessionId, ThreadSessionState? session)
        => GetNextImageTitle(GetOrCreatePromptState(promptSessionId, session));

    public void AddPromptImage(PromptImageAttachment image)
        => AddPromptImage(GetActivePromptState(), image);

    public void AddPromptImage(string promptSessionId, ThreadSessionState? session, PromptImageAttachment image)
        => AddPromptImage(GetOrCreatePromptState(promptSessionId, session), image);

    public bool RenamePromptImage(string imageId, string title)
        => RenamePromptImage(GetActivePromptState(), imageId, title);

    public bool RenamePromptImage(string promptSessionId, ThreadSessionState? session, string imageId, string title)
        => RenamePromptImage(GetOrCreatePromptState(promptSessionId, session), imageId, title);

    public bool DeletePromptImage(string imageId)
        => DeletePromptImage(GetActivePromptState(), imageId);

    public bool DeletePromptImage(string promptSessionId, ThreadSessionState? session, string imageId)
        => DeletePromptImage(GetOrCreatePromptState(promptSessionId, session), imageId);

    public IReadOnlyList<PromptImageAttachment> SnapshotPromptImages()
        => SnapshotPromptImages(GetActivePromptState());

    public void RestorePromptImages(IReadOnlyList<PromptImageAttachment> images)
        => RestorePromptImages(GetActivePromptState(), images);

    public void ClearPromptImages()
        => ClearPromptImages(GetActivePromptState());

    public string? LoadPromptDraft(string threadId)
        => _promptDraftPersistence.LoadPromptDraft(threadId);

    public bool HasPersistedPromptDraft(string threadId)
        => _promptDraftPersistence.HasPromptDraft(threadId);

    public void DeletePersistedPromptDraft(string threadId)
        => _promptDraftPersistence.DeletePromptDraft(threadId);

    public ValueTask DisposeAsync()
        => _promptDraftPersistence.DisposeAsync();

    private PromptDraftSessionState GetActivePromptState()
        => GetOrCreatePromptState(
            _activePromptSessionId,
            string.Equals(_activePromptSessionId, DraftPromptSessionId, StringComparison.OrdinalIgnoreCase)
                ? null
                : ResolveActiveThreadSession());

    private PromptDraftSessionState GetOrCreatePromptState(string promptSessionId, ThreadSessionState? session)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(promptSessionId);

        if (!_promptSessions.TryGetValue(promptSessionId, out var state))
        {
            state = new PromptDraftSessionState(promptSessionId);
            state.ViewModel = new PromptDraftViewModel(value => OnPromptTextChanged(state, value));
            _promptSessions.Add(promptSessionId, state);
        }

        if (session is not null && !ReferenceEquals(state.ThreadSession, session))
        {
            state.ThreadSession = session;
        }

        SyncPromptTextFromSession(state);
        return state;
    }

    private ThreadSessionState? ResolveActiveThreadSession()
    {
        if (_getSelection().Target is not WorkspaceTarget.Thread { ThreadId: { Length: > 0 } selectedThreadId })
        {
            return null;
        }

        if (!_promptSessions.TryGetValue(selectedThreadId, out var state))
        {
            return null;
        }

        return state.ThreadSession;
    }

    private string ResolveCurrentPromptSessionId()
        => _getSelection().Target is WorkspaceTarget.Thread { ThreadId: { Length: > 0 } selectedThreadId }
            ? selectedThreadId
            : DraftPromptSessionId;

    private void SyncPromptTextFromSession(PromptDraftSessionState state)
    {
        var promptText = _promptDrafts.GetPrompt(state.ThreadSession);
        if (string.Equals(state.ViewModel.PromptText, promptText, StringComparison.Ordinal))
        {
            return;
        }

        state.SyncingPromptText = true;
        try
        {
            state.ViewModel.PromptText = promptText;
        }
        finally
        {
            state.SyncingPromptText = false;
        }
    }

    private List<PromptImageAttachment> GetCurrentImageList(PromptDraftSessionState state)
        => state.ThreadSession?.PromptImageAttachments ?? state.DraftPromptImages;

    private string GetNextImageTitle(PromptDraftSessionState state)
    {
        var existingTitles = new HashSet<string>(GetCurrentImageList(state).Select(static image => image.Title), StringComparer.OrdinalIgnoreCase);
        for (var index = 1; ; index++)
        {
            var candidate = $"Image-{index}";
            if (!existingTitles.Contains(candidate))
            {
                return candidate;
            }
        }
    }

    private void AddPromptImage(PromptDraftSessionState state, PromptImageAttachment image)
    {
        ArgumentNullException.ThrowIfNull(image);

        var list = GetCurrentImageList(state);
        var wasEmpty = list.Count == 0;
        list.Add(image.Copy());
        NotifyPromptImagesChanged(state, editedStateChanged: wasEmpty);
    }

    private bool RenamePromptImage(PromptDraftSessionState state, string imageId, string title)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imageId);

        var normalizedTitle = PromptImageAttachment.NormalizeTitle(title);
        var list = GetCurrentImageList(state);
        for (var index = 0; index < list.Count; index++)
        {
            if (!string.Equals(list[index].Id, imageId, StringComparison.Ordinal))
            {
                continue;
            }

            if (string.Equals(list[index].Title, normalizedTitle, StringComparison.Ordinal))
            {
                return false;
            }

            list[index] = list[index].WithTitle(normalizedTitle);
            NotifyPromptImagesChanged(state, editedStateChanged: false);
            return true;
        }

        return false;
    }

    private bool DeletePromptImage(PromptDraftSessionState state, string imageId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imageId);

        var list = GetCurrentImageList(state);
        var wasEmpty = list.Count == 0;
        var removed = list.RemoveAll(image => string.Equals(image.Id, imageId, StringComparison.Ordinal)) > 0;
        if (removed)
        {
            NotifyPromptImagesChanged(state, editedStateChanged: wasEmpty != (list.Count == 0));
        }

        return removed;
    }

    private IReadOnlyList<PromptImageAttachment> SnapshotPromptImages(PromptDraftSessionState state)
        => GetCurrentImageList(state).Select(static image => image.Copy()).ToArray();

    private void RestorePromptImages(PromptDraftSessionState state, IReadOnlyList<PromptImageAttachment> images)
    {
        ArgumentNullException.ThrowIfNull(images);

        var list = GetCurrentImageList(state);
        var wasEmpty = list.Count == 0;
        list.Clear();
        foreach (var image in images)
        {
            list.Add(image.Copy());
        }

        NotifyPromptImagesChanged(state, editedStateChanged: wasEmpty != (list.Count == 0));
    }

    private void ClearPromptText(PromptDraftSessionState state)
        => state.ViewModel.PromptText = string.Empty;

    private void ClearPromptImages(PromptDraftSessionState state)
    {
        var list = GetCurrentImageList(state);
        if (list.Count == 0)
        {
            return;
        }

        list.Clear();
        NotifyPromptImagesChanged(state, editedStateChanged: true);
    }

    private void NotifyPromptImagesChanged(PromptDraftSessionState state, bool editedStateChanged)
    {
        _onPromptImageAttachmentsChanged();
        if (state.ThreadSession is not null && editedStateChanged)
        {
            PublishThreadPromptEditedStateChanged(state.PromptSessionId);
        }
    }

    private void OnPromptTextChanged(PromptDraftSessionState state, string? value)
    {
        if (state.SyncingPromptText)
        {
            return;
        }

        var change = _promptDrafts.RememberPrompt(state.ThreadSession, value);
        if (state.ThreadSession is not null)
        {
            _promptDraftPersistence.ObservePromptDraft(state.PromptSessionId, state.ThreadSession.PromptDraftText);
            if (change.EditedStateChanged)
            {
                PublishThreadPromptEditedStateChanged(state.PromptSessionId);
            }
        }
    }

    private void PublishThreadPromptEditedStateChanged(string threadId)
    {
        if (!string.IsNullOrWhiteSpace(threadId))
        {
            _frontendEvents.Publish(new PromptDraftChangedEvent(threadId));
        }
    }

    private sealed class PromptDraftSessionState
    {
        public PromptDraftSessionState(string promptSessionId)
        {
            PromptSessionId = promptSessionId;
        }

        public string PromptSessionId { get; }

        public PromptDraftViewModel ViewModel { get; set; } = null!;

        public ThreadSessionState? ThreadSession { get; set; }

        public List<PromptImageAttachment> DraftPromptImages { get; } = [];

        public bool SyncingPromptText { get; set; }
    }
}
