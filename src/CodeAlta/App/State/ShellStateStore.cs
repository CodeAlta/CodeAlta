using CodeAlta.Catalog;
using CodeAlta.Models;
using CodeAlta.Threading;

namespace CodeAlta.App.State;

/// <summary>
/// UI-thread-owned immutable state store for shell frontend projections.
/// </summary>
/// <remarks>
/// Frontend state ownership remains split by domain owner; this store is authoritative only
/// for immutable projection snapshots consumed across coordinator boundaries.
///
/// | State slice | Authoritative live owner | ShellStateStore role |
/// | --- | --- | --- |
/// | Catalog and selection restore | <c>ShellThreadStateCoordinator</c> and catalog/view-state persistence | Snapshot selected target, catalog lists, open thread ids, and navigator settings. |
/// | Live logical tabs | <c>IShellTabService</c> | Snapshot projected tab identity/order/selection only. |
/// | Prompt draft text and images | <c>PromptDraftUiCoordinator</c> plus prompt composer view models and prompt-draft persistence | No live ownership; consumers read the dedicated prompt services/view models. |
/// | Model-provider selection and runtime state | <c>ModelProviderSelectorCoordinator</c>, <c>ModelProviderSelectorStateStore</c>, and backend runtime state | No live ownership; projections read the dedicated provider state. |
/// | Shell and thread status | <c>ShellStatusProjectionController</c> and selected <c>OpenThreadState</c> status fields | Snapshot shell-level status text when needed across boundaries. |
/// | File editor tabs | <c>FileEditorWorkspaceCoordinator</c> and <c>IShellTabService</c> | Snapshot logical tab projection only. |
/// | Plugin projections | <c>PluginHostBridge</c> and plugin-owned surfaces/events | No live ownership; plugins enter through explicit bridge/events. |
/// </remarks>
internal class ShellStateStore
{
    private readonly int _ownerThreadId = Environment.CurrentManagedThreadId;
    private readonly IUiDispatcher? _uiDispatcher;
    private ShellFrontendStateSnapshot _snapshot = ShellFrontendStateSnapshot.Empty;

    public ShellStateStore(IUiDispatcher? uiDispatcher = null)
        => _uiDispatcher = uiDispatcher;

    /// <summary>Gets the current immutable snapshot.</summary>
    public ShellFrontendStateSnapshot Snapshot
    {
        get
        {
            VerifyOwnerThread();
            return _snapshot;
        }
    }

    /// <summary>
    /// Applies a mutation and publishes the resulting immutable snapshot.
    /// </summary>
    /// <param name="mutation">Mutation function.</param>
    /// <returns>The updated snapshot.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="mutation"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the store is accessed from a non-owner thread.</exception>
    public ShellFrontendStateSnapshot Mutate(Func<ShellFrontendStateSnapshot, ShellFrontendStateSnapshot> mutation)
    {
        VerifyOwnerThread();
        ArgumentNullException.ThrowIfNull(mutation);

        _snapshot = mutation(_snapshot) ?? throw new InvalidOperationException("Frontend state mutation returned null.");
        return _snapshot;
    }

    private void VerifyOwnerThread()
    {
        if (_uiDispatcher is not null)
        {
            _uiDispatcher.VerifyAccess();
            return;
        }

        if (Environment.CurrentManagedThreadId != _ownerThreadId)
        {
            throw new InvalidOperationException("Shell frontend state must be accessed from its owning UI thread.");
        }
    }
}

/// <summary>
/// Immutable shell frontend state snapshot.
/// </summary>
/// <param name="Tabs">The projected shell tabs.</param>
/// <param name="ActiveTabId">The active tab identifier, when one is selected.</param>
/// <param name="StatusText">The shell status text, when available.</param>
/// <param name="Projects">The catalog project snapshot.</param>
/// <param name="Threads">The catalog thread snapshot.</param>
/// <param name="Selection">The selected shell target snapshot.</param>
/// <param name="OpenThreadIds">The ordered open thread identifiers.</param>
/// <param name="NavigatorSettings">The navigator settings snapshot.</param>
internal sealed record ShellFrontendStateSnapshot(
    IReadOnlyList<ShellFrontendTabSnapshot> Tabs,
    string? ActiveTabId,
    string? StatusText,
    IReadOnlyList<ProjectDescriptor> Projects,
    IReadOnlyList<WorkThreadDescriptor> Threads,
    ShellSelection Selection,
    IReadOnlyList<string> OpenThreadIds,
    NavigatorSettings NavigatorSettings)
{
    /// <summary>Gets an empty shell frontend snapshot.</summary>
    public static ShellFrontendStateSnapshot Empty { get; } = new(
        [],
        ActiveTabId: null,
        StatusText: null,
        Projects: [],
        Threads: [],
        ShellSelection.GlobalDraft(),
        OpenThreadIds: [],
        new NavigatorSettings());

    /// <summary>Returns a snapshot with the supplied tab inserted or replaced.</summary>
    public ShellFrontendStateSnapshot UpsertTab(ShellFrontendTabSnapshot tab)
    {
        ArgumentNullException.ThrowIfNull(tab);

        var tabs = Tabs.ToList();
        var index = tabs.FindIndex(candidate => string.Equals(candidate.TabId, tab.TabId, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            tabs[index] = tab;
        }
        else
        {
            tabs.Add(tab);
        }

        return this with { Tabs = tabs, ActiveTabId = ActiveTabId ?? tab.TabId };
    }

    /// <summary>Returns a snapshot with the supplied tab removed.</summary>
    public ShellFrontendStateSnapshot RemoveTab(string tabId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tabId);

        var tabs = Tabs.Where(tab => !string.Equals(tab.TabId, tabId, StringComparison.OrdinalIgnoreCase)).ToList();
        var activeTabId = string.Equals(ActiveTabId, tabId, StringComparison.OrdinalIgnoreCase)
            ? tabs.FirstOrDefault()?.TabId
            : ActiveTabId;
        return this with { Tabs = tabs, ActiveTabId = activeTabId };
    }

    /// <summary>Returns a snapshot with the supplied tab selected.</summary>
    public ShellFrontendStateSnapshot SelectTab(string tabId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tabId);
        if (!Tabs.Any(tab => string.Equals(tab.TabId, tabId, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"Tab '{tabId}' is not present in the shell frontend state.");
        }

        return this with { ActiveTabId = tabId };
    }

    /// <summary>Returns a snapshot with updated status text.</summary>
    public ShellFrontendStateSnapshot SetStatus(string? statusText)
        => this with { StatusText = statusText };

    /// <summary>Returns a snapshot with updated catalog projects and threads.</summary>
    public ShellFrontendStateSnapshot SetCatalog(
        IReadOnlyList<ProjectDescriptor> projects,
        IReadOnlyList<WorkThreadDescriptor> threads)
    {
        ArgumentNullException.ThrowIfNull(projects);
        ArgumentNullException.ThrowIfNull(threads);

        return this with
        {
            Projects = projects.ToArray(),
            Threads = threads.ToArray(),
        };
    }

    /// <summary>Returns a snapshot with updated selected target, open thread ids, and navigator settings.</summary>
    public ShellFrontendStateSnapshot SetSelection(
        ShellSelection selection,
        IReadOnlyList<string> openThreadIds,
        NavigatorSettings navigatorSettings)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(openThreadIds);
        ArgumentNullException.ThrowIfNull(navigatorSettings);

        return this with
        {
            Selection = selection,
            OpenThreadIds = openThreadIds
                .Where(static id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            NavigatorSettings = CloneNavigatorSettings(navigatorSettings),
        };
    }

    private static NavigatorSettings CloneNavigatorSettings(NavigatorSettings settings)
        => new()
        {
            SortMode = settings.SortMode,
            RecentThreadsPerProject = settings.RecentThreadsPerProject,
            ThemeSchemeName = settings.ThemeSchemeName,
        };
}

/// <summary>
/// Immutable shell tab projection stored by <see cref="ShellStateStore"/>.
/// </summary>
/// <param name="TabId">The stable tab identifier.</param>
/// <param name="Title">The tab title.</param>
/// <param name="Kind">The tab kind.</param>
/// <param name="Data">Optional stable view-model/projection data associated with the tab.</param>
internal sealed record ShellFrontendTabSnapshot(
    string TabId,
    string Title,
    string Kind,
    object? Data = null);
