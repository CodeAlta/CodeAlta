using XenoAtom.Terminal.UI;
using CodeAlta.Models;
using CodeAlta.Presentation.Prompting;

namespace CodeAlta.ViewModels;

public sealed partial class ThreadWorkspaceViewModel
{
    private Action<int>? _modelProviderSelectionChanged;
    private Action<int>? _modelSelectionChanged;
    private Action<int>? _reasoningSelectionChanged;
    private int _suppressSelectionChangedNotifications;

    public ThreadWorkspaceViewModel()
    {
        ModelProviderStatusMarkup = string.Empty;
        ProviderSummaryMarkup = string.Empty;
        SelectedModelProviderIndex = -1;
        SelectedModelIndex = -1;
        SelectedReasoningIndex = -1;
        ModelProviderOptions = [];
        ModelOptions = [];
        ReasoningOptions = [];
        PromptStripItems = [];
    }

    [Bindable]
    public partial string ModelProviderStatusMarkup { get; set; }

    [Bindable]
    public partial string ProviderSummaryMarkup { get; set; }

    [Bindable]
    public partial bool CanSelectModelProvider { get; set; }

    [Bindable]
    public partial bool CanSelectModel { get; set; }

    [Bindable]
    public partial bool CanSelectReasoning { get; set; }

    [Bindable]
    public partial IReadOnlyList<ChatBackendOption> ModelProviderOptions { get; set; }

    [Bindable]
    public partial int SelectedModelProviderIndex { get; set; }

    [Bindable]
    public partial IReadOnlyList<ChatModelOption> ModelOptions { get; set; }

    [Bindable]
    public partial int SelectedModelIndex { get; set; }

    [Bindable]
    public partial IReadOnlyList<ChatReasoningOption> ReasoningOptions { get; set; }

    [Bindable]
    public partial int SelectedReasoningIndex { get; set; }

    [Bindable]
    public partial bool HasQueuedPrompts { get; set; }

    [Bindable]
    public partial bool CanShowThreadInfo { get; set; }

    [Bindable]
    public partial IReadOnlyList<PromptStripItem> PromptStripItems { get; set; }

    internal void SetPromptStripItems(IReadOnlyList<PromptStripItem> promptStripItems, bool hasQueuedPrompts)
    {
        ArgumentNullException.ThrowIfNull(promptStripItems);

        PromptStripItems = promptStripItems;
        HasQueuedPrompts = hasQueuedPrompts;
    }

    internal void SetModelProviderSelectionChangedHandlers(
        Action<int>? modelProviderSelectionChanged,
        Action<int>? modelSelectionChanged,
        Action<int>? reasoningSelectionChanged)
    {
        _modelProviderSelectionChanged = modelProviderSelectionChanged;
        _modelSelectionChanged = modelSelectionChanged;
        _reasoningSelectionChanged = reasoningSelectionChanged;
    }

    internal IDisposable SuppressSelectionChangedNotifications()
    {
        _suppressSelectionChangedNotifications++;
        return new SelectionChangedNotificationSuppression(this);
    }

    partial void OnSelectedModelProviderIndexChanged(int value)
        => NotifySelectionChanged(_modelProviderSelectionChanged, value);

    partial void OnSelectedModelIndexChanged(int value)
        => NotifySelectionChanged(_modelSelectionChanged, value);

    partial void OnSelectedReasoningIndexChanged(int value)
        => NotifySelectionChanged(_reasoningSelectionChanged, value);

    private void NotifySelectionChanged(Action<int>? handler, int value)
    {
        if (_suppressSelectionChangedNotifications != 0)
        {
            return;
        }

        handler?.Invoke(value);
    }

    private sealed class SelectionChangedNotificationSuppression : IDisposable
    {
        private ThreadWorkspaceViewModel? _owner;

        public SelectionChangedNotificationSuppression(ThreadWorkspaceViewModel owner)
        {
            _owner = owner;
        }

        public void Dispose()
        {
            if (_owner is not { } owner)
            {
                return;
            }

            _owner = null;
            owner._suppressSelectionChangedNotifications--;
        }
    }
}
