using System.Diagnostics;
using System.Text;
using CodeAlta.Plugins;
using XenoAtom.Terminal;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Input;
using XenoAtom.Terminal.UI.Styling;

namespace CodeAlta.Tui.Plugins;

/// <summary>
/// Presents plugin startup in the terminal; the runtime and operation remain caller-owned.
/// </summary>
/// <remarks>Preserves the existing live-driver/summary failure precedence: such failures may escape
/// without joining the original operation. This adapter is not a lifetime or termination guarantee.</remarks>
public sealed class TerminalPluginStartupFeedback : IPluginStartupFeedback
{
    /// <inheritdoc />
    public ValueTask<T> RunAsync<T>(IReadOnlyList<PluginBuildRequest> requests, bool waitForAcknowledgement,
        Func<IPluginStartupProgress?, CancellationToken, ValueTask<T>> operation,
        Func<T, TimeSpan, string> summaryFactory, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requests);
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(summaryFactory);
        return RunWithInteractiveLiveAsync(requests, waitForAcknowledgement, operation, summaryFactory, cancellationToken);
    }

    /// <summary>
    /// Builds stale plugin requests while rendering interactive terminal progress with <c>Terminal.Live</c>.
    /// </summary>
    /// <param name="scheduler">The build scheduler.</param>
    /// <param name="requests">The stale build requests.</param>
    /// <param name="waitForEnterAfterCompletion">A value indicating whether the live region should wait for Enter after builds complete.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The build results.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="scheduler" /> or <paramref name="requests" /> is <see langword="null" />.</exception>
    public static async ValueTask<IReadOnlyList<PluginBuildResult>> BuildWithInteractiveLiveAsync(
        PluginBuildScheduler scheduler,
        IReadOnlyList<PluginBuildRequest> requests,
        bool waitForEnterAfterCompletion = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scheduler);
        ArgumentNullException.ThrowIfNull(requests);
        if (requests.Count == 0)
        {
            return [];
        }

        if (!Terminal.Instance.IsInitialized || Terminal.Instance.Capabilities.IsOutputRedirected)
        {
            return await scheduler.BuildAsync(requests, cancellationToken).ConfigureAwait(false);
        }

        return await RunWithInteractiveLiveAsync(
                requests,
                waitForEnterAfterCompletion,
                async (status, token) =>
                {
                    // Both branches of this adapter supply a non-null status (unlike silent feedback).
                    void OnProgress(object? _, PluginBuildProgress progress)
                    {
                        status!.Report(progress);
                    }

                    scheduler.ProgressChanged += OnProgress;
                    try
                    {
                        var results = await scheduler.BuildAsync(requests, token).ConfigureAwait(false);
                        status!.MarkBuildsCompleted();
                        return results;
                    }
                    finally
                    {
                        scheduler.ProgressChanged -= OnProgress;
                    }
                },
                static (results, elapsed) => BuildBuildOnlySummary(results, elapsed),
                cancellationToken)
            .ConfigureAwait(false);
    }

    internal static async ValueTask<T> RunWithInteractiveLiveAsync<T>(
        IReadOnlyList<PluginBuildRequest> requests,
        bool waitForEnterAfterCompletion,
        Func<IPluginStartupProgress?, CancellationToken, ValueTask<T>> operation,
        Func<T, TimeSpan, string> summaryFactory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requests);
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(summaryFactory);

        if (!Terminal.Instance.IsInitialized || Terminal.Instance.Capabilities.IsOutputRedirected)
        {
            var status = new PluginBuildLiveStatus(requests);
            var started = Stopwatch.StartNew();
            var result = await operation(status, cancellationToken).ConfigureAwait(false);
            started.Stop();
            status.MarkCompleted(summaryFactory(result, started.Elapsed));
            return result;
        }

        var liveStatus = new PluginBuildLiveStatus(requests);
        var liveRegion = liveStatus.CreateVisual(waitForEnterAfterCompletion);
        var stopwatch = Stopwatch.StartNew();
        return await RunPresentedOperationAsync(liveStatus, operation, RunLive, cancellationToken).ConfigureAwait(false);

        void RunLive(Task<T> operationTask)
        {
            var completionApplied = false;
            Terminal.Live(
                liveRegion,
                _ =>
                {
                    liveStatus.ApplyPendingUpdates();

                    if (cancellationToken.IsCancellationRequested || operationTask.IsCanceled || operationTask.IsFaulted)
                    {
                        return TerminalLoopResult.Stop;
                    }

                    if (!operationTask.IsCompleted)
                    {
                        return TerminalLoopResult.Continue;
                    }

                    if (!completionApplied)
                    {
                        stopwatch.Stop();
                        liveStatus.ApplyPendingUpdates();
                        var result = operationTask.GetAwaiter().GetResult();
                        liveStatus.MarkCompleted(summaryFactory(result, stopwatch.Elapsed));
                        completionApplied = true;
                    }

                    return waitForEnterAfterCompletion && !liveStatus.ContinueRequested
                        ? TerminalLoopResult.Continue
                        : TerminalLoopResult.Stop;
                });
        }
    }

    internal sealed class PluginBuildLiveStatus : IPluginStartupProgress
    {
        private readonly Lock _pendingUpdatesLock = new();
        private readonly Queue<Action> _pendingUpdates = new();
        private readonly PluginBuildLiveItem[] _items;
        private readonly State<PluginStartupLivePhase> _phase = new(PluginStartupLivePhase.Building);
        private readonly State<bool> _completed = new(false);
        private readonly State<bool> _spinnerActive = new(true);
        private readonly State<string?> _summaryMarkup = new(null);
        private bool _continueRequested;

        public PluginBuildLiveStatus(IReadOnlyList<PluginBuildRequest> requests)
        {
            _items = requests.Select(static request => new PluginBuildLiveItem(request.Package)).ToArray();
        }

        public bool ContinueRequested
        {
            get
            {
                return _continueRequested;
            }
        }

        public Visual CreateVisual(bool waitForEnterAfterCompletion)
        {
            var spinner = new Spinner().Style(SpinnerStyles.Dots);
            spinner.IsActive(_spinnerActive);
            spinner.IsVisible(_spinnerActive);
            return new PluginBuildLiveVisual(
                this,
                new VStack(
                        new HStack(
                                spinner,
                                new Markup(BuildHeaderMarkup).Wrap(true))
                            .Spacing(1),
                        new VStack(_items.Select((_, index) => (Visual)new Markup(() => BuildItemMarkup(index)).Wrap(true)).ToArray()),
                        new Markup(() => _summaryMarkup.Value ?? string.Empty).Wrap(true),
                        new Markup(() => BuildFooterMarkup(waitForEnterAfterCompletion)).Wrap(true))
                    .Spacing(1));
        }

        public void MarkPreparing()
            => ApplyOrQueueStateUpdate(() =>
            {
            _phase.Value = PluginStartupLivePhase.Preparing;
            _spinnerActive.Value = true;
            });

        public void MarkBuilding()
            => ApplyOrQueueStateUpdate(() =>
            {
                _phase.Value = PluginStartupLivePhase.Building;
                _spinnerActive.Value = true;
            });

        public void MarkBuildsCompleted()
            => ApplyOrQueueStateUpdate(() =>
            {
            if (_phase.Value == PluginStartupLivePhase.Building)
            {
                _phase.Value = PluginStartupLivePhase.Activating;
            }
            });

        public void MarkActivating()
            => ApplyOrQueueStateUpdate(() =>
            {
            _phase.Value = PluginStartupLivePhase.Activating;
            _spinnerActive.Value = true;
            });

        public void Report(PluginBuildProgress progress)
        {
            if ((uint)progress.Index >= (uint)_items.Length)
            {
                return;
            }

            // Scheduler progress is raised from build worker tasks; defer bindable State updates
            // to the live UI update callback so XenoAtom.Terminal.UI can track and render them.
            EnqueueStateUpdate(() => _items[progress.Index].State.Value = progress.State);
        }

        public void ApplyPendingUpdates()
        {
            while (TryDequeueUpdate(out var update))
            {
                update();
            }
        }

        public void MarkCompleted(string summary)
        {
            ArgumentNullException.ThrowIfNull(summary);
            ApplyOrQueueStateUpdate(() =>
            {
                if (_completed.Value)
                {
                    return;
                }

                _phase.Value = PluginStartupLivePhase.Completed;
                _summaryMarkup.Value = EscapeMarkup(summary);
                _spinnerActive.Value = false;
                _completed.Value = true;
            });
        }

        public void RequestContinueIfCompleted()
        {
            if (_completed.Value)
            {
                _continueRequested = true;
            }
        }

        private string BuildHeaderMarkup()
        {
            var completed = _items.Count(static item => item.State.Value is PluginBuildProgressState.Succeeded or PluginBuildProgressState.Failed or PluginBuildProgressState.UpToDate);
            var failed = _items.Count(static item => item.State.Value == PluginBuildProgressState.Failed);
            var running = _items.Count(static item => item.State.Value == PluginBuildProgressState.Running);
            var builder = new StringBuilder();
            builder.Append(_phase.Value switch
                {
                    PluginStartupLivePhase.Preparing => "Preparing source plugins",
                    PluginStartupLivePhase.Activating => "Activating source plugins",
                    PluginStartupLivePhase.Completed when failed > 0 => "[error]✗[/] Plugin startup finished with build failures",
                    PluginStartupLivePhase.Completed => "[success]✓[/] Plugin startup complete",
                    _ => "Building source plugins",
                })
                .Append(" (").Append(completed.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .Append('/').Append(_items.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(" complete");
            if (running > 0)
            {
                builder.Append(", ").Append(running.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(" running");
            }

            if (failed > 0)
            {
                builder.Append(", ").Append(failed.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(" failed");
            }

            builder.Append(')');
            return builder.ToString();
        }

        private string BuildItemMarkup(int index)
        {
            if ((uint)index >= (uint)_items.Length)
            {
                return string.Empty;
            }

            var package = _items[index].Package;
            var state = _items[index].State.Value;

            var ordinal = (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture).PadLeft(2);
            var packageId = EscapeMarkup(package.PackageId);
            return state switch
            {
                PluginBuildProgressState.Queued => $"[dim]○[/] {ordinal}. [dim]Queued[/] {packageId}",
                PluginBuildProgressState.Running => $"[warning]◌[/] {ordinal}. [warning]Building[/] {packageId}",
                PluginBuildProgressState.Succeeded => $"[success]✓[/] {ordinal}. Built {packageId}",
                PluginBuildProgressState.Failed => $"[error]✗[/] {ordinal}. Build failed {packageId}",
                PluginBuildProgressState.UpToDate => $"[success]✓[/] {ordinal}. Checked {packageId} [dim](up-to-date)[/]",
                _ => $"[dim]○[/] {ordinal}. {packageId}",
            };
        }

        private string BuildFooterMarkup(bool waitForEnterAfterCompletion)
        {
            if (!waitForEnterAfterCompletion)
            {
                return string.Empty;
            }

            return _completed.Value
                ? "[dim]Press Enter to continue.[/]"
                : "Plugin startup is still running. Press Enter after it finishes to continue.";
        }

        private void ApplyOrQueueStateUpdate(Action update)
        {
            if (XenoAtom.Terminal.UI.Threading.Dispatcher.Current.CheckAccess())
            {
                update();
                return;
            }

            EnqueueStateUpdate(update);
        }

        private void EnqueueStateUpdate(Action update)
        {
            lock (_pendingUpdatesLock)
            {
                _pendingUpdates.Enqueue(update);
            }
        }

        private bool TryDequeueUpdate(out Action update)
        {
            lock (_pendingUpdatesLock)
            {
                if (_pendingUpdates.Count == 0)
                {
                    update = null!;
                    return false;
                }

                update = _pendingUpdates.Dequeue();
                return true;
            }
        }

        private static string EscapeMarkup(string text)
            => text.Replace("[", "\\[", StringComparison.Ordinal).Replace("]", "\\]", StringComparison.Ordinal);
    }

    private enum PluginStartupLivePhase
    {
        Preparing,
        Building,
        Activating,
        Completed,
    }

    private sealed class PluginBuildLiveVisual : Padder
    {
        private readonly PluginBuildLiveStatus _status;

        public PluginBuildLiveVisual(PluginBuildLiveStatus status, Visual content) : base(content)
        {
            ArgumentNullException.ThrowIfNull(status);
            _status = status;
            Focusable = true;
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.Key == TerminalKey.Enter)
            {
                _status.RequestContinueIfCompleted();
                e.Handled = true;
            }
        }
    }

    private sealed class PluginBuildLiveItem(SourcePluginPackage package)
    {
        public SourcePluginPackage Package { get; } = package;

        public State<PluginBuildProgressState> State { get; } = new(PluginBuildProgressState.Queued);
    }

    private static string Pluralize(int count, string singular)
        => count == 1 ? singular : singular + "s";

    private static string BuildBuildOnlySummary(IReadOnlyList<PluginBuildResult> buildResults, TimeSpan elapsed)
    {
        var failedPackageCount = buildResults.Count(static build => !build.Succeeded);
        return failedPackageCount == 0
            ? $"Plugin builds finished in {FormatElapsed(elapsed)}."
            : $"Plugin builds finished with {failedPackageCount} {Pluralize(failedPackageCount, "failure")} in {FormatElapsed(elapsed)}.";
    }

    private static string FormatElapsed(TimeSpan elapsed)
        => elapsed.TotalSeconds >= 1
            ? elapsed.TotalSeconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "s"
            : Math.Max(0, (int)Math.Round(elapsed.TotalMilliseconds, MidpointRounding.AwayFromZero)).ToString(System.Globalization.CultureInfo.InvariantCulture) + "ms";

    // Mandatory production-used seam, not a replacement lifetime policy. Driver/summary failure
    // deliberately skips the join as before. Infrastructure awaits retain the original suppression;
    // bindable-state changes above still use their existing dispatcher/queue boundary.
    internal static ValueTask<T> RunPresentedOperationAsync<T>(IPluginStartupProgress status,
        Func<IPluginStartupProgress?, CancellationToken, ValueTask<T>> operation,
        Action<Task<T>> runLive, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(runLive);
        return RunCoreAsync();

        async ValueTask<T> RunCoreAsync()
        {
            var operationTask = operation(status, cancellationToken).AsTask();
            runLive(operationTask);
            return await operationTask.ConfigureAwait(false);
        }
    }
}
