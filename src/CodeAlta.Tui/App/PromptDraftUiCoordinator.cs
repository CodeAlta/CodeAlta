using CodeAlta.Tui.Models;
using CodeAlta.Tui.App.Events;
using CodeAlta.Catalog;
using CodeAlta.Tui.Presentation.Prompting;
using CodeAlta.Tui.ViewModels;
using XenoAtom.Terminal.UI;

namespace CodeAlta.Tui.App;

internal sealed class PromptDraftUiCoordinator : IAsyncDisposable
{
    private const string DraftPromptSessionId = "__draft__";
    private const string GlobalDraftScopeKey = "__draft__:global";
    private const string ProjectDraftScopeKeyPrefix = "__draft__:project:";

    private readonly PromptDraftCoordinator _promptDrafts;
    private readonly SessionPromptDraftPersistenceCoordinator _promptDraftPersistence;
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
        _promptDraftPersistence = new SessionPromptDraftPersistenceCoordinator(catalogOptions);
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

    public Binding<string?> GetPromptTextBinding(string promptSessionId, SessionState? session)
        => GetOrCreatePromptState(promptSessionId, session).ViewModel.Bind.PromptText;

    public bool HasCurrentPromptDraft
    {
        get
        {
            var state = GetActivePromptState();
            if (state.BoundSession is not null)
            {
                return !string.IsNullOrWhiteSpace(state.BoundSession.PromptDraftText) || state.BoundSession.PromptImageAttachments.Count > 0;
            }

            return HasDraftPrompt(ResolveCurrentDraftScopeKey()) || state.DraftPromptImages.Count > 0;
        }
    }

    public void SyncPromptText(SessionState? session)
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
        var draftScopeKey = state.DraftScopeKey;
        _promptDrafts.RememberPrompt(null, string.Empty, draftScopeKey);
        _promptDraftPersistence.ObservePromptDraft(draftScopeKey, string.Empty);
        ClearPromptText(state);
        ClearPromptImages(state);
    }

    public IReadOnlyList<PromptImageAttachment> CurrentPromptImages
        => GetCurrentImageList(GetActivePromptState());

    public bool HasCurrentPromptImages => GetCurrentImageList(GetActivePromptState()).Count > 0;

    public IReadOnlyList<PromptImageAttachment> GetPromptImages(string promptSessionId, SessionState? session)
        => GetCurrentImageList(GetOrCreatePromptState(promptSessionId, session));

    public string GetNextImageTitle()
        => GetNextImageTitle(GetActivePromptState());

    public string GetNextImageTitle(string promptSessionId, SessionState? session)
        => GetNextImageTitle(GetOrCreatePromptState(promptSessionId, session));

    public void AddPromptImage(PromptImageAttachment image)
        => AddPromptImage(GetActivePromptState(), image);

    public void AddPromptImage(string promptSessionId, SessionState? session, PromptImageAttachment image)
        => AddPromptImage(GetOrCreatePromptState(promptSessionId, session), image);

    public bool RenamePromptImage(string imageId, string title)
        => RenamePromptImage(GetActivePromptState(), imageId, title);

    public bool RenamePromptImage(string promptSessionId, SessionState? session, string imageId, string title)
        => RenamePromptImage(GetOrCreatePromptState(promptSessionId, session), imageId, title);

    public bool DeletePromptImage(string imageId)
        => DeletePromptImage(GetActivePromptState(), imageId);

    public bool DeletePromptImage(string promptSessionId, SessionState? session, string imageId)
        => DeletePromptImage(GetOrCreatePromptState(promptSessionId, session), imageId);

    public IReadOnlyList<PromptImageAttachment> SnapshotPromptImages()
        => SnapshotPromptImages(GetActivePromptState());

    public void RestorePromptImages(IReadOnlyList<PromptImageAttachment> images)
        => RestorePromptImages(GetActivePromptState(), images);

    public void ClearPromptImages()
        => ClearPromptImages(GetActivePromptState());

    public string? LoadPromptDraft(string sessionId)
        => _promptDraftPersistence.LoadPromptDraft(sessionId);

    public bool HasPersistedPromptDraft(string sessionId)
        => _promptDraftPersistence.HasPromptDraft(sessionId);

    public bool HasDraftPrompt(string? projectId, bool isGlobal)
        => HasDraftPrompt(ResolveDraftScopeKey(projectId, isGlobal));

    public bool HasDraftPrompt(string draftScopeKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(draftScopeKey);
        return _promptDrafts.HasDraftPrompt(draftScopeKey) || _promptDraftPersistence.HasPromptDraft(draftScopeKey);
    }

    public void DeletePersistedPromptDraft(string sessionId)
        => _promptDraftPersistence.DeletePromptDraft(sessionId);

    public ValueTask DisposeAsync()
        => _promptDraftPersistence.DisposeAsync();

    private PromptDraftSessionState GetActivePromptState()
        => GetOrCreatePromptState(
            _activePromptSessionId,
            string.Equals(_activePromptSessionId, DraftPromptSessionId, StringComparison.OrdinalIgnoreCase)
                ? null
                : ResolveActiveBoundSession());

    private PromptDraftSessionState GetOrCreatePromptState(string promptSessionId, SessionState? session)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(promptSessionId);

        if (!_promptSessions.TryGetValue(promptSessionId, out var state))
        {
            state = new PromptDraftSessionState(promptSessionId);
            state.ViewModel = new PromptDraftViewModel(value => OnPromptTextChanged(state, value));
            _promptSessions.Add(promptSessionId, state);
        }

        if (session is not null && !ReferenceEquals(state.BoundSession, session))
        {
            state.BoundSession = session;
        }

        if (session is null && string.Equals(promptSessionId, DraftPromptSessionId, StringComparison.OrdinalIgnoreCase) &&
            TryResolveSelectedDraftScopeKey(out var draftScopeKey))
        {
            state.DraftScopeKey = draftScopeKey;
        }

        SyncPromptTextFromSession(state);
        return state;
    }

    private SessionState? ResolveActiveBoundSession()
    {
        if (_getSelection().Target is not WorkspaceTarget.Session { SessionId: { Length: > 0 } selectedSessionId })
        {
            return null;
        }

        if (!_promptSessions.TryGetValue(selectedSessionId, out var state))
        {
            return null;
        }

        return state.BoundSession;
    }

    private string ResolveCurrentPromptSessionId()
        => _getSelection().Target is WorkspaceTarget.Session { SessionId: { Length: > 0 } selectedSessionId }
            ? selectedSessionId
            : DraftPromptSessionId;

    private string ResolveCurrentDraftScopeKey()
    {
        return _getSelection().Target is WorkspaceTarget.Draft draft
            ? ResolveDraftScopeKey(draft.ProjectId, draft.IsGlobal)
            : GlobalDraftScopeKey;
    }

    private bool TryResolveSelectedDraftScopeKey(out string draftScopeKey)
    {
        if (_getSelection().Target is WorkspaceTarget.Draft draft)
        {
            draftScopeKey = ResolveDraftScopeKey(draft.ProjectId, draft.IsGlobal);
            return true;
        }

        draftScopeKey = string.Empty;
        return false;
    }

    private static string ResolveDraftScopeKey(string? projectId, bool isGlobal)
        => !isGlobal && !string.IsNullOrWhiteSpace(projectId)
            ? ProjectDraftScopeKeyPrefix + projectId.Trim()
            : GlobalDraftScopeKey;

    private void SyncPromptTextFromSession(PromptDraftSessionState state)
    {
        var promptText = state.BoundSession is null
            ? GetDraftPrompt(state.DraftScopeKey)
            : _promptDrafts.GetPrompt(state.BoundSession);
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
        => state.BoundSession?.PromptImageAttachments ?? state.DraftPromptImages;

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
        if (state.BoundSession is not null && editedStateChanged)
        {
            PublishSessionPromptEditedStateChanged(state.PromptSessionId);
        }
        else if (state.BoundSession is null && editedStateChanged)
        {
            PublishSessionPromptEditedStateChanged(state.DraftScopeKey);
        }
    }

    private void OnPromptTextChanged(PromptDraftSessionState state, string? value)
    {
        if (state.SyncingPromptText)
        {
            return;
        }

        var draftScopeKey = state.BoundSession is null ? state.DraftScopeKey : null;
        var change = _promptDrafts.RememberPrompt(state.BoundSession, value, draftScopeKey);
        if (state.BoundSession is not null)
        {
            _promptDraftPersistence.ObservePromptDraft(state.PromptSessionId, state.BoundSession.PromptDraftText);
            if (change.EditedStateChanged)
            {
                PublishSessionPromptEditedStateChanged(state.PromptSessionId);
            }

            return;
        }

        _promptDraftPersistence.ObservePromptDraft(draftScopeKey!, _promptDrafts.GetPrompt(null, draftScopeKey));
        if (change.EditedStateChanged)
        {
            PublishSessionPromptEditedStateChanged(draftScopeKey!);
        }
    }

    private string GetDraftPrompt(string draftScopeKey)
    {
        if (_promptDrafts.TryGetDraftPrompt(draftScopeKey, out var promptText))
        {
            return promptText;
        }

        promptText = _promptDraftPersistence.LoadPromptDraft(draftScopeKey) ?? string.Empty;
        _promptDrafts.RememberPrompt(null, promptText, draftScopeKey);
        return promptText;
    }

    private void PublishSessionPromptEditedStateChanged(string sessionId)
    {
        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            _frontendEvents.Publish(new PromptDraftChangedEvent(sessionId));
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

        public SessionState? BoundSession { get; set; }

        public List<PromptImageAttachment> DraftPromptImages { get; } = [];

        public string DraftScopeKey { get; set; } = GlobalDraftScopeKey;

        public bool SyncingPromptText { get; set; }
    }
}
