using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Geometry;
using CodeAlta.Tui.Models;
using CodeAlta.Tui.Presentation.Prompting;

namespace CodeAlta.Tui.ViewModels;

public sealed partial class SessionWorkspaceViewModel
{
    private Action<int>? _agentPromptSelectionChanged;
    private Action<int>? _modelProviderSelectionChanged;
    private Action<int>? _modelSelectionChanged;
    private Action<int>? _reasoningSelectionChanged;
    private Func<string, Visual, Visual?, bool>? _enterAskMode;
    private Func<string, bool>? _exitAskMode;
    private Func<Rectangle?>? _getAskModeBounds;
    private Action<Visual>? _focusAskModeControl;
    private int _suppressSelectionChangedNotifications;

    public SessionWorkspaceViewModel()
    {
        ModelProviderStatusMarkup = string.Empty;
        ProviderSummaryMarkup = string.Empty;
        SelectedAgentPromptIndex = -1;
        SelectedModelProviderIndex = -1;
        SelectedModelIndex = -1;
        SelectedReasoningIndex = -1;
        AgentPromptOptions = [];
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
    public partial bool CanSelectAgentPrompt { get; set; }

    [Bindable]
    public partial bool CanSelectModelProvider { get; set; }

    [Bindable]
    public partial bool CanSelectModel { get; set; }

    [Bindable]
    public partial bool CanSelectReasoning { get; set; }

    [Bindable]
    public partial IReadOnlyList<AgentPromptOption> AgentPromptOptions { get; set; }

    [Bindable]
    public partial int SelectedAgentPromptIndex { get; set; }

    [Bindable]
    public partial IReadOnlyList<ModelProviderOption> ModelProviderOptions { get; set; }

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
    public partial bool CanShowSessionInfo { get; set; }

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

    internal void SetAgentPromptSelectionChangedHandler(Action<int>? agentPromptSelectionChanged)
    {
        _agentPromptSelectionChanged = agentPromptSelectionChanged;
    }

    internal void SetAskModeHandlers(
        Func<string, Visual, Visual?, bool> enterAskMode,
        Func<string, bool> exitAskMode,
        Func<Rectangle?> getAskModeBounds,
        Action<Visual> focusAskModeControl)
    {
        ArgumentNullException.ThrowIfNull(enterAskMode);
        ArgumentNullException.ThrowIfNull(exitAskMode);
        ArgumentNullException.ThrowIfNull(getAskModeBounds);
        ArgumentNullException.ThrowIfNull(focusAskModeControl);

        _enterAskMode = enterAskMode;
        _exitAskMode = exitAskMode;
        _getAskModeBounds = getAskModeBounds;
        _focusAskModeControl = focusAskModeControl;
    }

    internal bool TryEnterAskMode(string tabId, Visual askForm, Visual? fileReview = null)
        => _enterAskMode?.Invoke(tabId, askForm, fileReview) == true;

    internal bool ExitAskMode(string tabId)
        => _exitAskMode?.Invoke(tabId) == true;

    internal Rectangle? GetAskModeBounds()
        => _getAskModeBounds?.Invoke();

    internal void FocusAskModeControl(Visual control)
        => _focusAskModeControl?.Invoke(control);

    internal IDisposable SuppressSelectionChangedNotifications()
    {
        _suppressSelectionChangedNotifications++;
        return new SelectionChangedNotificationSuppression(this);
    }

    partial void OnSelectedAgentPromptIndexChanged(int value)
        => NotifySelectionChanged(_agentPromptSelectionChanged, value);

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
        private SessionWorkspaceViewModel? _owner;

        public SelectionChangedNotificationSuppression(SessionWorkspaceViewModel owner)
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
