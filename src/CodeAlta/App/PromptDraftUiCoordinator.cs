using CodeAlta.Models;
using CodeAlta.Catalog;
using CodeAlta.Presentation.Prompting;
using CodeAlta.ViewModels;
using XenoAtom.Terminal.UI;

namespace CodeAlta.App;

internal sealed class PromptDraftUiCoordinator : IAsyncDisposable
{
    private readonly PromptDraftCoordinator _promptDrafts;
    private readonly ThreadPromptDraftPersistenceCoordinator _promptDraftPersistence;
    private readonly Func<ShellSelection> _getSelection;
    private readonly Action _onThreadPromptEditedStateChanged;
    private readonly Action _onPromptImageAttachmentsChanged;
    private readonly PromptDraftViewModel _viewModel;
    private readonly List<PromptImageAttachment> _draftPromptImages = [];
    private ThreadSessionState? _selectedSession;
    private bool _syncingPromptText;

    public PromptDraftUiCoordinator(
        PromptDraftCoordinator promptDrafts,
        CatalogOptions catalogOptions,
        Func<ShellSelection> getSelection,
        Action onThreadPromptEditedStateChanged,
        Action? onPromptImageAttachmentsChanged = null)
    {
        ArgumentNullException.ThrowIfNull(promptDrafts);
        ArgumentNullException.ThrowIfNull(catalogOptions);
        ArgumentNullException.ThrowIfNull(getSelection);
        ArgumentNullException.ThrowIfNull(onThreadPromptEditedStateChanged);

        _promptDrafts = promptDrafts;
        _promptDraftPersistence = new ThreadPromptDraftPersistenceCoordinator(catalogOptions);
        _getSelection = getSelection;
        _onThreadPromptEditedStateChanged = onThreadPromptEditedStateChanged;
        _onPromptImageAttachmentsChanged = onPromptImageAttachmentsChanged ?? (static () => { });
        _viewModel = new PromptDraftViewModel(OnPromptTextChanged);
    }

    public Binding<string?> PromptTextBinding => _viewModel.Bind.PromptText;

    public string? PromptText
    {
        get => _viewModel.PromptText;
        set => _viewModel.PromptText = value ?? string.Empty;
    }

    public void SyncPromptText(ThreadSessionState? session)
    {
        var previousImageList = GetCurrentImageList();
        _selectedSession = session;

        var promptText = _promptDrafts.GetPrompt(session);
        if (!string.Equals(PromptText, promptText, StringComparison.Ordinal))
        {
            _syncingPromptText = true;
            try
            {
                PromptText = promptText;
            }
            finally
            {
                _syncingPromptText = false;
            }
        }

        if (!ReferenceEquals(previousImageList, GetCurrentImageList()))
        {
            _onPromptImageAttachmentsChanged();
        }
    }

    public void ClearPromptText()
        => PromptText = string.Empty;

    public void ClearPrompt()
    {
        ClearPromptText();
        ClearPromptImages();
    }

    public void ClearDraftPromptText()
    {
        _promptDrafts.RememberPrompt(null, string.Empty);
        _draftPromptImages.Clear();
        if (_selectedSession is null)
        {
            PromptText = string.Empty;
            _onPromptImageAttachmentsChanged();
        }
    }

    public IReadOnlyList<PromptImageAttachment> CurrentPromptImages
        => GetCurrentImageList();

    public bool HasCurrentPromptImages => GetCurrentImageList().Count > 0;

    public string GetNextImageTitle()
    {
        var existingTitles = new HashSet<string>(GetCurrentImageList().Select(static image => image.Title), StringComparer.OrdinalIgnoreCase);
        for (var index = 1; ; index++)
        {
            var candidate = $"Image-{index}";
            if (!existingTitles.Contains(candidate))
            {
                return candidate;
            }
        }
    }

    public void AddPromptImage(PromptImageAttachment image)
    {
        ArgumentNullException.ThrowIfNull(image);

        var list = GetCurrentImageList();
        var wasEmpty = list.Count == 0;
        list.Add(image.Copy());
        NotifyPromptImagesChanged(editedStateChanged: wasEmpty);
    }

    public bool RenamePromptImage(string imageId, string title)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imageId);

        var normalizedTitle = PromptImageAttachment.NormalizeTitle(title);
        var list = GetCurrentImageList();
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
            NotifyPromptImagesChanged(editedStateChanged: false);
            return true;
        }

        return false;
    }

    public bool DeletePromptImage(string imageId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imageId);

        var list = GetCurrentImageList();
        var wasEmpty = list.Count == 0;
        var removed = list.RemoveAll(image => string.Equals(image.Id, imageId, StringComparison.Ordinal)) > 0;
        if (removed)
        {
            NotifyPromptImagesChanged(editedStateChanged: wasEmpty != (list.Count == 0));
        }

        return removed;
    }

    public IReadOnlyList<PromptImageAttachment> SnapshotPromptImages()
        => GetCurrentImageList().Select(static image => image.Copy()).ToArray();

    public void RestorePromptImages(IReadOnlyList<PromptImageAttachment> images)
    {
        ArgumentNullException.ThrowIfNull(images);

        var list = GetCurrentImageList();
        var wasEmpty = list.Count == 0;
        list.Clear();
        foreach (var image in images)
        {
            list.Add(image.Copy());
        }

        NotifyPromptImagesChanged(editedStateChanged: wasEmpty != (list.Count == 0));
    }

    public void ClearPromptImages()
    {
        var list = GetCurrentImageList();
        if (list.Count == 0)
        {
            return;
        }

        list.Clear();
        NotifyPromptImagesChanged(editedStateChanged: true);
    }

    public string? LoadPromptDraft(string threadId)
        => _promptDraftPersistence.LoadPromptDraft(threadId);

    public bool HasPersistedPromptDraft(string threadId)
        => _promptDraftPersistence.HasPromptDraft(threadId);

    public void DeletePersistedPromptDraft(string threadId)
        => _promptDraftPersistence.DeletePromptDraft(threadId);

    public ValueTask DisposeAsync()
        => _promptDraftPersistence.DisposeAsync();

    private List<PromptImageAttachment> GetCurrentImageList()
        => _selectedSession?.PromptImageAttachments ?? _draftPromptImages;

    private void NotifyPromptImagesChanged(bool editedStateChanged)
    {
        _onPromptImageAttachmentsChanged();
        if (_selectedSession is not null && editedStateChanged)
        {
            _onThreadPromptEditedStateChanged();
        }
    }

    private void OnPromptTextChanged(string? value)
    {
        if (_syncingPromptText)
        {
            return;
        }

        var change = _promptDrafts.RememberPrompt(_selectedSession, value);
        if (_selectedSession is not null &&
            _getSelection().Target is WorkspaceTarget.Thread { ThreadId: { Length: > 0 } selectedThreadId })
        {
            _promptDraftPersistence.ObservePromptDraft(selectedThreadId, _selectedSession.PromptDraftText);
            if (change.EditedStateChanged)
            {
                _onThreadPromptEditedStateChanged();
            }
        }
    }
}
