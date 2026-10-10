using CodeAlta.Plugins.Abstractions;
using XenoAtom.Logging;

namespace CodeAlta.Plugins;

/// <summary>
/// Runtime-owned plugin task service that links scheduled work to the plugin activation lifetime.
/// </summary>
public sealed class PluginRuntimeTaskService : IPluginTaskService
{
    private readonly object _gate = new();
    private readonly CancellationToken _lifetimeCancellationToken;
    private readonly List<TrackedPluginTask> _runningTasks = [];
    private bool _closed;
    private PluginOwnedOperation? _close;

    /// <summary>
    /// Initializes a new instance of the <see cref="PluginRuntimeTaskService"/> class.
    /// </summary>
    /// <param name="lifetimeCancellationToken">The token cancelled when the plugin activation is deactivated.</param>
    public PluginRuntimeTaskService(CancellationToken lifetimeCancellationToken)
    {
        _lifetimeCancellationToken = lifetimeCancellationToken;
    }

    /// <inheritdoc />
    public bool HasRunningTasks
    {
        get
        {
            lock (_gate)
            {
                RemoveCompletedTasks();
                return _runningTasks.Count != 0;
            }
        }
    }

    /// <inheritdoc />
    public int RunningTaskCount
    {
        get
        {
            lock (_gate)
            {
                RemoveCompletedTasks();
                return _runningTasks.Count;
            }
        }
    }

    /// <inheritdoc />
    public PluginTaskHandle Run(string name, Func<CancellationToken, ValueTask> work, PluginTaskOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(work);

        options ??= new PluginTaskOptions();
        TrackedPluginTask tracked;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            RemoveCompletedTasks();
            tracked = new TrackedPluginTask(this, name, work, options);
            _runningTasks.Add(tracked);
        }

        tracked.Launch();
        return tracked.Handle;
    }

    /// <inheritdoc />
    public async ValueTask WhenIdleAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            PluginOwnedOperation[] runningTasks;
            lock (_gate)
            {
                RemoveCompletedTasks();
                if (_runningTasks.Count == 0)
                {
                    return;
                }

                runningTasks = [.. _runningTasks.Select(static task => task.Cleanup)];
            }

            // Work faults remain on the handle. Cancellation-cleanup faults prohibit lifetime release.
            await PluginOwnedOperation.JoinAsync(runningTasks).WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Requests cancellation for all currently tracked plugin tasks.
    /// </summary>
    public void CancelAll()
    {
        TrackedPluginTask[] runningTasks;
        lock (_gate)
        {
            RemoveCompletedTasks();
            runningTasks = [.. _runningTasks];
        }

        foreach (var task in runningTasks)
        {
            task.RequestCancellation();
        }
    }

    internal Task CloseForReleaseAsync()
    {
        PluginOwnedOperation close;
        lock (_gate)
        {
            _closed = true;
            close = _close ??= new PluginOwnedOperation(async () =>
            {
                CancelAll();
                await WhenIdleAsync().ConfigureAwait(false);
            });
        }
        close.Launch();
        return close.Work;
    }

    private static Task StartTask(Func<CancellationToken, ValueTask> work, CancellationToken cancellationToken, bool longRunning)
    {
        return longRunning
            ? Task.Factory.StartNew(
                static state => RunWorkAsync((WorkState)state!),
                new WorkState(work, cancellationToken),
                cancellationToken,
                TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach,
                TaskScheduler.Default).Unwrap()
            : Task.Run(() => RunWorkAsync(new WorkState(work, cancellationToken)), cancellationToken);
    }

    private static async Task RunWorkAsync(WorkState state)
    {
        await state.Work(state.CancellationToken).ConfigureAwait(false);
    }

    private void RemoveCompletedTasks()
    {
        // Metadata only: both original cleanup and its observer are terminal before retirement.
        for (var index = _runningTasks.Count - 1; index >= 0; index--)
        {
            var outcome = _runningTasks[index].Cleanup.Outcome;
            if (outcome.IsCompletedSuccessfully && outcome.Result is null) _runningTasks.RemoveAt(index);
        }
    }

    private readonly record struct WorkState(Func<CancellationToken, ValueTask> Work, CancellationToken CancellationToken);

    private sealed class TrackedPluginTask
    {
        private readonly object _gate = new();
        private readonly CancellationTokenSource _cancellation = new();
        private readonly PluginRuntimeTaskService _owner;
        private readonly PluginOwnedOperation _work;
        private PluginOwnedOperation? _cancel;
        private bool _completed;

        internal TrackedPluginTask(PluginRuntimeTaskService owner, string name, Func<CancellationToken, ValueTask> work, PluginTaskOptions options)
        {
            _owner = owner;
            _work = new PluginOwnedOperation(async () =>
            {
                // Do not use a linked CTS whose synchronous propagation can run plugin control callbacks.
                using var registration = owner._lifetimeCancellationToken.UnsafeRegister(static state => ((TrackedPluginTask)state!).RequestCancellation(), this);
                owner._lifetimeCancellationToken.ThrowIfCancellationRequested();
                await StartTask(async token =>
                {
                    // Async control propagation is not the entry gate: recheck lifetime at actual callback entry.
                    owner._lifetimeCancellationToken.ThrowIfCancellationRequested();
                    token.ThrowIfCancellationRequested();
                    await work(token).ConfigureAwait(false);
                }, _cancellation.Token, options.LongRunning).ConfigureAwait(false);
            });
            Handle = new PluginTaskHandle(name, options.Description, options.LongRunning, DateTimeOffset.UtcNow, _work.Work, RequestCancellation);
            Cleanup = new PluginOwnedOperation(CleanupAsync);
        }

        internal PluginTaskHandle Handle { get; }
        internal PluginOwnedOperation Cleanup { get; }
        internal void Launch() { Cleanup.Launch(); _work.Launch(); }

        internal void RequestCancellation()
        {
            PluginOwnedOperation cancel;
            lock (_gate)
            {
                if (_completed) return;
                cancel = _cancel ??= new PluginOwnedOperation(_cancellation.CancelAsync);
            }
            cancel.Launch();
        }

        private async Task CleanupAsync()
        {
            await _work.Outcome.ConfigureAwait(false);
            PluginOwnedOperation? cancel;
            lock (_gate) { _completed = true; cancel = _cancel; }
            try
            {
                if (cancel is not null) await PluginOwnedOperation.JoinAsync([cancel]).ConfigureAwait(false);
                _cancellation.Dispose();
            }
            catch
            {
                // Retain the failed control and CTS and refuse further work. Idle cannot authorize release.
                lock (_owner._gate) _owner._closed = true;
                throw;
            }
        }
    }
}

internal sealed class PluginRuntimeServices : IPluginServices
{
    private const string UnresolvedProjectScopeId = "__codealta_unresolved_project_scope__";

    private readonly IPluginServices _inner;
    private readonly IPluginStateStore? _state;
    private readonly IPluginDatabase? _database;

    public PluginRuntimeServices(
        Logger logger,
        string pluginRuntimeKey,
        PluginScope scope,
        string? scopeProjectId,
        IPluginServices inner,
        IPluginTaskService tasks,
        IPluginStateStore? state = null,
        IPluginDatabase? database = null)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginRuntimeKey);
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(tasks);
        Logger = logger;
        _inner = inner;
        _state = state;
        _database = database;
        Tasks = tasks;
        Alta = new PluginRuntimeAltaService(pluginRuntimeKey, scope, scopeProjectId, inner.Alta);
    }

    public Logger Logger { get; }

    public IPluginUiService Ui => _inner.Ui;

    // The store of the host when it has one; else the files of this plugin.
    public IPluginStateStore State => _state ?? _inner.State;

    // The tables of this plugin when the host has a database; else what the host gave, which has none.
    public IPluginDatabase Database => _database ?? _inner.Database;

    public IPluginWorkspaceService Workspace => _inner.Workspace;

    public IPluginSessionService Sessions => _inner.Sessions;

    public IPluginPromptService Prompts => _inner.Prompts;

    public IPluginAgentService Agents => _inner.Agents;

    public IPluginTaskService Tasks { get; }

    public IPluginAltaService Alta { get; }

    private sealed class PluginRuntimeAltaService(
        string pluginRuntimeKey,
        PluginScope scope,
        string? scopeProjectId,
        IPluginAltaService inner) : IPluginAltaService
    {
        public ValueTask<PluginAltaCommandResult> InvokeAsync(
            IReadOnlyList<string> args,
            string? stdin = null,
            PluginAltaInvocationOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(args);
            var effectiveOptions = scope == PluginScope.Project
                ? (options ?? new PluginAltaInvocationOptions()) with
                {
                    SourceProjectId = string.IsNullOrWhiteSpace(scopeProjectId)
                        ? UnresolvedProjectScopeId
                        : scopeProjectId,
                }
                : options;
            return inner is IPluginAltaRuntimeService runtime
                ? runtime.InvokeAsync(pluginRuntimeKey, args, stdin, effectiveOptions, cancellationToken)
                : inner.InvokeAsync(args, stdin, effectiveOptions, cancellationToken);
        }
    }
}
