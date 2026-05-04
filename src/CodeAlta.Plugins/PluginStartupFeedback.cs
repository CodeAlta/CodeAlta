using System.Text;
using XenoAtom.Terminal;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Input;
using XenoAtom.Terminal.UI.Styling;

namespace CodeAlta.Plugins;

/// <summary>
/// Describes startup feedback mode for plugin build/load operations.
/// </summary>
public enum PluginStartupFeedbackMode
{
    /// <summary>Interactive terminal feedback is available.</summary>
    Interactive,
    /// <summary>Headless/non-interactive feedback is available.</summary>
    Headless,
}

/// <summary>
/// Reports concise startup feedback for stale plugin builds while keeping fast-path loads quiet.
/// </summary>
public sealed class PluginStartupFeedbackReporter
{
    private readonly PluginStartupFeedbackMode _mode;
    private readonly Action<string> _interactiveSink;
    private readonly Action<string> _headlessSink;

    /// <summary>
    /// Initializes a new instance of the <see cref="PluginStartupFeedbackReporter"/> class.
    /// </summary>
    /// <param name="mode">The feedback mode.</param>
    /// <param name="interactiveSink">The interactive sink, typically <c>Terminal.WriteMarkupLine</c>.</param>
    /// <param name="headlessSink">The headless sink, typically the normal logger/output path.</param>
    /// <exception cref="ArgumentNullException">Thrown when a sink is <see langword="null"/>.</exception>
    public PluginStartupFeedbackReporter(PluginStartupFeedbackMode mode, Action<string> interactiveSink, Action<string> headlessSink)
    {
        ArgumentNullException.ThrowIfNull(interactiveSink);
        ArgumentNullException.ThrowIfNull(headlessSink);
        _mode = mode;
        _interactiveSink = interactiveSink;
        _headlessSink = headlessSink;
    }

    /// <summary>
    /// Reports stale plugin builds before scheduling begins.
    /// </summary>
    /// <param name="stalePackageCount">The number of stale packages.</param>
    public void ReportStaleBuilds(int stalePackageCount)
    {
        if (stalePackageCount <= 0)
        {
            return;
        }

        Write($"Building {stalePackageCount} stale plugin{(stalePackageCount == 1 ? string.Empty : "s")}...");
    }

    /// <summary>
    /// Reports a build progress transition.
    /// </summary>
    /// <param name="progress">The progress event.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="progress"/> is <see langword="null"/>.</exception>
    public void ReportProgress(PluginBuildProgress progress)
    {
        ArgumentNullException.ThrowIfNull(progress);
        if (progress.State == PluginBuildProgressState.Queued || progress.State == PluginBuildProgressState.UpToDate)
        {
            return;
        }

        Write($"Plugin {progress.Index + 1}/{progress.Total} {progress.Package.PackageId}: {progress.State}");
    }

    /// <summary>
    /// Reports a completed build result, keeping up-to-date fast-path loads quiet.
    /// </summary>
    /// <param name="result">The build result.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="result"/> is <see langword="null"/>.</exception>
    public void ReportResult(PluginBuildResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.IsUpToDate)
        {
            return;
        }

        if (!result.Succeeded)
        {
            Write($"Plugin {result.Package.PackageId} build failed.");
        }
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

        var status = new PluginBuildLiveStatus(requests);
        var liveRegion = status.CreateVisual(waitForEnterAfterCompletion);
        void OnProgress(object? _, PluginBuildProgress progress)
        {
            status.Report(progress);
        }

        scheduler.ProgressChanged += OnProgress;
        try
        {
            var buildTask = scheduler.BuildAsync(requests, cancellationToken).AsTask();
            Terminal.Live(
                liveRegion,
                _ =>
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        return TerminalLoopResult.Stop;
                    }

                    if (!buildTask.IsCompleted)
                    {
                        return TerminalLoopResult.Continue;
                    }

                    status.MarkCompleted();
                    return waitForEnterAfterCompletion && !status.ContinueRequested
                        ? TerminalLoopResult.Continue
                        : TerminalLoopResult.Stop;
                });
            return await buildTask.ConfigureAwait(false);
        }
        finally
        {
            scheduler.ProgressChanged -= OnProgress;
        }
    }

    private sealed class PluginBuildLiveStatus
    {
        private readonly Lock _lock = new();
        private readonly PluginBuildLiveItem[] _items;
        private bool _completed;
        private bool _continueRequested;

        public PluginBuildLiveStatus(IReadOnlyList<PluginBuildRequest> requests)
        {
            _items = requests.Select(static request => new PluginBuildLiveItem(request.Package)).ToArray();
        }

        public bool ContinueRequested
        {
            get
            {
                lock (_lock)
                {
                    return _continueRequested;
                }
            }
        }

        public Visual CreateVisual(bool waitForEnterAfterCompletion)
            => new PluginBuildLiveVisual(
                this,
                new VStack(
                    new HStack(
                            new Spinner().Style(SpinnerStyles.Dots),
                            new Markup(BuildHeaderMarkup))
                        .Spacing(1),
                    new VStack(_items.Select((_, index) => (Visual)new Markup(() => BuildItemMarkup(index))).ToArray()),
                    new Markup(() => BuildFooterMarkup(waitForEnterAfterCompletion)))
                .Spacing(1));

        public void Report(PluginBuildProgress progress)
        {
            if ((uint)progress.Index >= (uint)_items.Length)
            {
                return;
            }

            lock (_lock)
            {
                var item = _items[progress.Index];
                item.State = progress.State;
            }
        }

        public void MarkCompleted()
        {
            lock (_lock)
            {
                _completed = true;
            }
        }

        public void RequestContinueIfCompleted()
        {
            lock (_lock)
            {
                if (_completed)
                {
                    _continueRequested = true;
                }
            }
        }

        private string BuildHeaderMarkup()
        {
            lock (_lock)
            {
                var completed = _items.Count(static item => item.State is PluginBuildProgressState.Succeeded or PluginBuildProgressState.Failed or PluginBuildProgressState.UpToDate);
                var failed = _items.Count(static item => item.State == PluginBuildProgressState.Failed);
                var running = _items.Count(static item => item.State == PluginBuildProgressState.Running);
                var builder = new StringBuilder();
                builder.Append(_completed ? "✓ Plugin builds finished" : "Building source plugins")
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
                return EscapeMarkup(builder.ToString());
            }
        }

        private string BuildItemMarkup(int index)
        {
            if ((uint)index >= (uint)_items.Length)
            {
                return string.Empty;
            }

            SourcePluginPackage package;
            PluginBuildProgressState state;
            lock (_lock)
            {
                package = _items[index].Package;
                state = _items[index].State;
            }

            var ordinal = (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture).PadLeft(2);
            var packageId = EscapeMarkup(package.PackageId);
            return state switch
            {
                PluginBuildProgressState.Queued => $"○ {ordinal}. Queued {packageId}",
                PluginBuildProgressState.Running => $"◌ {ordinal}. Building {packageId}",
                PluginBuildProgressState.Succeeded => $"✓ {ordinal}. Plugin {packageId} built successfully",
                PluginBuildProgressState.Failed => $"✗ {ordinal}. Plugin {packageId} build failed",
                PluginBuildProgressState.UpToDate => $"◇ {ordinal}. Plugin {packageId} is up-to-date",
                _ => $"○ {ordinal}. {packageId}",
            };
        }

        private string BuildFooterMarkup(bool waitForEnterAfterCompletion)
        {
            if (!waitForEnterAfterCompletion)
            {
                return string.Empty;
            }

            lock (_lock)
            {
                return _completed
                    ? "✓ Plugin build live output paused. Press Enter to continue."
                    : "Press Enter after plugin builds finish to continue.";
            }
        }

        private static string EscapeMarkup(string text)
            => text.Replace("[", "\\[", StringComparison.Ordinal).Replace("]", "\\]", StringComparison.Ordinal);
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

        public PluginBuildProgressState State { get; set; } = PluginBuildProgressState.Queued;
    }

    private void Write(string message)
    {
        if (_mode == PluginStartupFeedbackMode.Interactive)
        {
            _interactiveSink(message);
        }
        else
        {
            _headlessSink(message);
        }
    }
}
