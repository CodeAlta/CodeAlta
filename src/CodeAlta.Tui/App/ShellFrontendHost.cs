using System.Runtime.ExceptionServices;
using CodeAlta.LiveTool;
using CodeAlta.Plugins;
using XenoAtom.Terminal;
using XenoAtom.Terminal.UI;

namespace CodeAlta.Tui.App;

internal interface IShellFrontendHostLifecycle
{
    void PrepareForRun();

    Visual GetRoot();

    TerminalLoopResult Tick(CancellationToken cancellationToken);

    ValueTask DisposeFrontendAsync();

    IAsyncDisposable? OwnedServices { get; }
}

internal sealed class ShellFrontendHost : IAsyncDisposable
{
    private readonly IShellFrontendHostLifecycle _lifecycle;
    private AltaReminderService? _reminders;
    private readonly PluginRuntimeManager? _pluginRuntime;
    private readonly Lazy<Task> _disposeTask;

    public ShellFrontendHost(IShellFrontendHostLifecycle lifecycle)
    {
        ArgumentNullException.ThrowIfNull(lifecycle);
        _lifecycle = lifecycle;
        _pluginRuntime = (lifecycle.OwnedServices as CodeAltaOwnedServices)?.PluginRuntime;
        _disposeTask = PluginEventDependencyBarrier.Wrap(_pluginRuntime,
            new Lazy<Task>(() => DisposeRemindersThenFrontendAsync(
                () => _reminders?.DisposeAsync() ?? ValueTask.CompletedTask,
                DisposeFrontendAndOwnedServicesAsync)));
    }

    internal void OwnReminders(AltaReminderService reminders)
    {
        ArgumentNullException.ThrowIfNull(reminders);
        if (_reminders is not null)
        {
            throw new InvalidOperationException("Reminder ownership already transferred.");
        }

        _reminders = reminders;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        _lifecycle.PrepareForRun();
        var root = _lifecycle.GetRoot();
        await Terminal.RunAsync(
            root,
            () => Tick(cancellationToken),
            cancellationToken);
    }

    public TerminalLoopResult Tick(CancellationToken cancellationToken)
        => _lifecycle.Tick(cancellationToken);

    public ValueTask DisposeAsync() => PluginEventDependencyBarrier.EnterDispose(_pluginRuntime, _disposeTask);

    internal static Task DisposeRemindersThenFrontendAsync(
        Func<ValueTask> disposeReminders,
        Func<ValueTask> disposeExisting)
    {
        ArgumentNullException.ThrowIfNull(disposeReminders);
        ArgumentNullException.ThrowIfNull(disposeExisting);
        return CoreAsync();

        async Task CoreAsync()
        {
            Exception? reminderFailure = null;
            try
            {
                await disposeReminders();
            }
            catch (Exception ex)
            {
                reminderFailure = ex;
            }

            try
            {
                await disposeExisting();
            }
            catch (Exception ex) when (reminderFailure is not null)
            {
                throw new AggregateException(reminderFailure, ex);
            }

            if (reminderFailure is not null)
            {
                ExceptionDispatchInfo.Throw(reminderFailure);
            }
        }
    }

    private async ValueTask DisposeFrontendAndOwnedServicesAsync()
    {
        Exception? frontendFailure = null;
        try
        {
            await _lifecycle.DisposeFrontendAsync();
        }
        catch (Exception ex)
        {
            frontendFailure = ex;
        }

        // A failed draft acknowledgement must not abandon runtime/provider/plugin ownership.
        try
        {
            if (_lifecycle.OwnedServices is { } ownedServices)
            {
                await ownedServices.DisposeAsync();
            }
        }
        catch (Exception ex) when (frontendFailure is not null)
        {
            throw new AggregateException(frontendFailure, ex);
        }

        if (frontendFailure is not null)
        {
            ExceptionDispatchInfo.Throw(frontendFailure);
        }
    }

    /// <summary>
    /// Attempts the seven existing frontend cleanup stages of a successfully returned app in order.
    /// </summary>
    /// <remarks>
    /// Validate every mandatory operation synchronously before starting the local core inline.
    /// Preserve projection, reminder UI, view-state persistence, editors, event pump, controller and
    /// prompt-draft order. The persistence adapter discards its result with status reporting disabled;
    /// this traversal handles escaped failures, not returned persistence errors or conflicts.
    /// Every stage is attempted after earlier terminal failures, retaining direct exception identities.
    /// Plain awaits preserve frontend cleanup context. This operation stores no resources and adds no
    /// caching, retries or repeated/concurrent disposal guarantee; the existing owner routing remains.
    /// There is no overall timeout or guarantee that noncooperative callbacks or lower owners terminate.
    /// A pending stage can prevent later frontend, owned-service and updater cleanup from being reached.
    /// Hidden construction acquisitions, partial surfaces and publication/preparation failures are not
    /// rolled back here; this is not complete startup, UI, plugin, network or lower-owner qualification.
    /// </remarks>
    /// <exception cref="ArgumentNullException">A mandatory operation is null.</exception>
    /// <exception cref="Exception">A lone original failure is rethrown through EDI.</exception>
    /// <exception cref="OperationCanceledException">The sole retained failure is cancellation.</exception>
    /// <exception cref="AggregateException">Multiple direct failures are reported in order without flattening.</exception>
    internal static Task DisposeFrontendResourcesAsync(
        Action disposeProjection,
        Action disposeReminderUi,
        Func<Task> persistViewState,
        Func<ValueTask> disposeFileEditors,
        Func<ValueTask> disposeRuntimeEventPump,
        Func<ValueTask> disposeShellController,
        Func<ValueTask> disposePromptDrafts)
    {
        ArgumentNullException.ThrowIfNull(disposeProjection);
        ArgumentNullException.ThrowIfNull(disposeReminderUi);
        ArgumentNullException.ThrowIfNull(persistViewState);
        ArgumentNullException.ThrowIfNull(disposeFileEditors);
        ArgumentNullException.ThrowIfNull(disposeRuntimeEventPump);
        ArgumentNullException.ThrowIfNull(disposeShellController);
        ArgumentNullException.ThrowIfNull(disposePromptDrafts);
        return CoreAsync();

        async Task CoreAsync()
        {
            List<Exception>? failures = null;
            try
            {
                disposeProjection();
            }
            catch (Exception ex)
            {
                (failures ??= []).Add(ex);
            }

            try
            {
                disposeReminderUi();
            }
            catch (Exception ex)
            {
                (failures ??= []).Add(ex);
            }

            try
            {
                await persistViewState();
            }
            catch (Exception ex)
            {
                (failures ??= []).Add(ex);
            }

            try
            {
                await disposeFileEditors();
            }
            catch (Exception ex)
            {
                (failures ??= []).Add(ex);
            }

            try
            {
                await disposeRuntimeEventPump();
            }
            catch (Exception ex)
            {
                (failures ??= []).Add(ex);
            }

            try
            {
                await disposeShellController();
            }
            catch (Exception ex)
            {
                (failures ??= []).Add(ex);
            }

            try
            {
                await disposePromptDrafts();
            }
            catch (Exception ex)
            {
                (failures ??= []).Add(ex);
            }

            if (failures is { Count: 1 })
            {
                ExceptionDispatchInfo.Throw(failures[0]);
            }

            if (failures is { Count: > 1 })
            {
                throw new AggregateException(failures);
            }
        }
    }
}
