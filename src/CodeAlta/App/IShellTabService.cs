using CodeAlta.Catalog;
using CodeAlta.Models;
using XenoAtom.Terminal.UI;

namespace CodeAlta.App;

internal readonly record struct ShellTabId
{
    public ShellTabId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Value = value;
    }

    public string Value { get; }

    public bool IsEmpty => string.IsNullOrWhiteSpace(Value);

    public override string ToString() => Value;
}

internal enum ShellTabKind
{
    PromptDraft,
    Thread,
    Editor,
    Plugin,
}

internal enum ShellTabCloseReason
{
    User,
    ProjectClosed,
    PluginUnloaded,
    Replaced,
}

internal abstract record ShellTabAssociation
{
    private ShellTabAssociation()
    {
    }

    public sealed record PromptDraft : ShellTabAssociation
    {
        public PromptDraft(PromptSessionBinding prompt)
        {
            ArgumentNullException.ThrowIfNull(prompt);
            Prompt = prompt;
        }

        public PromptSessionBinding Prompt { get; init; }
    }

    public sealed record Thread : ShellTabAssociation
    {
        public Thread(
            string threadId,
            PromptSessionId promptSessionId,
            ProjectId projectId,
            ModelProviderId modelProviderId)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(threadId);
            if (promptSessionId.IsEmpty)
            {
                throw new ArgumentException("Prompt session id cannot be empty.", nameof(promptSessionId));
            }

            if (projectId == default)
            {
                throw new ArgumentException("Project id cannot be empty.", nameof(projectId));
            }

            if (modelProviderId.IsEmpty)
            {
                throw new ArgumentException("Model provider id cannot be empty.", nameof(modelProviderId));
            }

            ThreadId = threadId;
            PromptSessionId = promptSessionId;
            ProjectId = projectId;
            ModelProviderId = modelProviderId;
        }

        public string ThreadId { get; init; }

        public PromptSessionId PromptSessionId { get; init; }

        public ProjectId ProjectId { get; init; }

        public ModelProviderId ModelProviderId { get; init; }
    }

    public sealed record Editor : ShellTabAssociation
    {
        public Editor(ProjectId projectId, string fullPath)
        {
            if (projectId == default)
            {
                throw new ArgumentException("Project id cannot be empty.", nameof(projectId));
            }

            ArgumentException.ThrowIfNullOrWhiteSpace(fullPath);
            ProjectId = projectId;
            FullPath = fullPath;
        }

        public ProjectId ProjectId { get; init; }

        public string FullPath { get; init; }
    }

    public sealed record Plugin : ShellTabAssociation
    {
        public Plugin(string pluginId, string surfaceKey, ProjectId? projectId = null, string? threadId = null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
            ArgumentException.ThrowIfNullOrWhiteSpace(surfaceKey);
            PluginId = pluginId;
            SurfaceKey = surfaceKey;
            ProjectId = projectId;
            ThreadId = string.IsNullOrWhiteSpace(threadId) ? null : threadId;
        }

        public string PluginId { get; init; }

        public string SurfaceKey { get; init; }

        public ProjectId? ProjectId { get; init; }

        public string? ThreadId { get; init; }
    }
}

internal sealed record ShellTabDescriptor
{
    public required ShellTabId TabId { get; init; }

    public required ShellTabKind Kind { get; init; }

    public required ShellTabAssociation Association { get; init; }

    public required Visual Header { get; init; }

    public required Visual Content { get; init; }

    public required object ViewModel { get; init; }

    public bool CanClose { get; init; } = true;

    public Func<ShellTabCloseReason, ValueTask>? OnClosedAsync { get; init; }

    public void Validate()
    {
        if (TabId.IsEmpty)
        {
            throw new ArgumentException("Tab id cannot be empty.", nameof(TabId));
        }

        ArgumentNullException.ThrowIfNull(Association);
        ArgumentNullException.ThrowIfNull(Header);
        ArgumentNullException.ThrowIfNull(Content);
        ArgumentNullException.ThrowIfNull(ViewModel);
    }
}

internal sealed record ShellTabSnapshot(
    ShellTabId TabId,
    ShellTabKind Kind,
    ShellTabAssociation Association,
    Visual Header,
    Visual Content,
    object ViewModel,
    bool IsSelected,
    bool CanClose);

internal sealed class ShellTabChangedEventArgs(IReadOnlyList<ShellTabSnapshot> tabs) : EventArgs
{
    public IReadOnlyList<ShellTabSnapshot> Tabs { get; } = tabs;
}

internal interface IShellTabService
{
    IReadOnlyList<ShellTabSnapshot> GetTabs();

    ShellTabSnapshot OpenOrGetTab(ShellTabDescriptor descriptor);

    Task SelectTabAsync(ShellTabId tabId, CancellationToken cancellationToken = default);

    Task<bool> CloseTabAsync(ShellTabId tabId, ShellTabCloseReason reason, CancellationToken cancellationToken = default);

    bool TryGetTab(ShellTabId tabId, out ShellTabSnapshot tab);

    event EventHandler<ShellTabChangedEventArgs>? TabsChanged;
}

internal sealed class InMemoryShellTabService : IShellTabService
{
    private readonly Dictionary<ShellTabId, ShellTabEntry> _tabs = new();
    private ShellTabId? _selectedTabId;

    public event EventHandler<ShellTabChangedEventArgs>? TabsChanged;

    public IReadOnlyList<ShellTabSnapshot> GetTabs()
        => _tabs.Values.Select(CreateSnapshot).ToArray();

    public ShellTabSnapshot OpenOrGetTab(ShellTabDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        descriptor.Validate();

        if (_tabs.TryGetValue(descriptor.TabId, out var existing))
        {
            return CreateSnapshot(existing);
        }

        var entry = new ShellTabEntry(descriptor);
        _tabs.Add(descriptor.TabId, entry);
        _selectedTabId ??= descriptor.TabId;
        RaiseTabsChanged();
        return CreateSnapshot(entry);
    }

    public Task SelectTabAsync(ShellTabId tabId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureTabId(tabId);
        if (!_tabs.ContainsKey(tabId))
        {
            throw new KeyNotFoundException($"Shell tab '{tabId.Value}' is not open.");
        }

        _selectedTabId = tabId;
        RaiseTabsChanged();
        return Task.CompletedTask;
    }

    public async Task<bool> CloseTabAsync(ShellTabId tabId, ShellTabCloseReason reason, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureTabId(tabId);
        if (!_tabs.TryGetValue(tabId, out var entry))
        {
            return false;
        }

        if (!entry.Descriptor.CanClose)
        {
            return false;
        }

        _tabs.Remove(tabId);
        if (_selectedTabId == tabId)
        {
            _selectedTabId = _tabs.Keys.FirstOrDefault();
        }

        RaiseTabsChanged();
        if (entry.Descriptor.OnClosedAsync is not null)
        {
            await entry.Descriptor.OnClosedAsync(reason);
        }

        return true;
    }

    public bool TryGetTab(ShellTabId tabId, out ShellTabSnapshot tab)
    {
        EnsureTabId(tabId);
        if (_tabs.TryGetValue(tabId, out var entry))
        {
            tab = CreateSnapshot(entry);
            return true;
        }

        tab = default!;
        return false;
    }

    private ShellTabSnapshot CreateSnapshot(ShellTabEntry entry)
        => new(
            entry.Descriptor.TabId,
            entry.Descriptor.Kind,
            entry.Descriptor.Association,
            entry.Descriptor.Header,
            entry.Descriptor.Content,
            entry.Descriptor.ViewModel,
            _selectedTabId == entry.Descriptor.TabId,
            entry.Descriptor.CanClose);

    private void RaiseTabsChanged()
        => TabsChanged?.Invoke(this, new ShellTabChangedEventArgs(GetTabs()));

    private static void EnsureTabId(ShellTabId tabId)
    {
        if (tabId.IsEmpty)
        {
            throw new ArgumentException("Tab id cannot be empty.", nameof(tabId));
        }
    }

    private sealed record ShellTabEntry(ShellTabDescriptor Descriptor);
}
