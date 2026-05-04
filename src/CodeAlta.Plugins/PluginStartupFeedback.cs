using System.Text;
using XenoAtom.Terminal;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;

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
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The build results.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="scheduler" /> or <paramref name="requests" /> is <see langword="null" />.</exception>
    public static async ValueTask<IReadOnlyList<PluginBuildResult>> BuildWithInteractiveLiveAsync(
        PluginBuildScheduler scheduler,
        IReadOnlyList<PluginBuildRequest> requests,
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
        var liveRegion = new Markup(status.BuildMarkup) { Wrap = true };
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
                _ => buildTask.IsCompleted || cancellationToken.IsCancellationRequested
                    ? TerminalLoopResult.Stop
                    : TerminalLoopResult.Continue);
            return await buildTask.ConfigureAwait(false);
        }
        finally
        {
            scheduler.ProgressChanged -= OnProgress;
        }
    }

    private sealed class PluginBuildLiveStatus
    {
        private static readonly string[] SpinnerFrames = ["⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏"];
        private readonly Lock _lock = new();
        private readonly PluginBuildLiveItem[] _items;
        private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;

        public PluginBuildLiveStatus(IReadOnlyList<PluginBuildRequest> requests)
        {
            _items = requests.Select(static request => new PluginBuildLiveItem(request.Package)).ToArray();
        }

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
                item.Timestamp = progress.Timestamp;
            }
        }

        public string BuildMarkup()
        {
            lock (_lock)
            {
                var completed = _items.Count(static item => item.State is PluginBuildProgressState.Succeeded or PluginBuildProgressState.Failed or PluginBuildProgressState.UpToDate);
                var failed = _items.Count(static item => item.State == PluginBuildProgressState.Failed);
                var running = _items.Count(static item => item.State == PluginBuildProgressState.Running);
                var elapsed = DateTimeOffset.UtcNow - _startedAt;
                var spinner = SpinnerFrames[(int)(elapsed.TotalMilliseconds / 90) % SpinnerFrames.Length];
                var builder = new StringBuilder();
                builder.Append("[primary]").Append(spinner).Append("[/] [bold]Building source plugins[/] ")
                    .Append("[dim](").Append(completed.ToString(System.Globalization.CultureInfo.InvariantCulture))
                    .Append('/').Append(_items.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(" complete");
                if (running > 0)
                {
                    builder.Append(", ").Append(running.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(" running");
                }

                if (failed > 0)
                {
                    builder.Append(", ").Append(failed.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(" failed");
                }

                builder.AppendLine(")[/]");

                for (var index = 0; index < _items.Length; index++)
                {
                    AppendItem(builder, _items[index], index, spinner);
                }

                return builder.ToString().TrimEnd();
            }
        }

        private static void AppendItem(StringBuilder builder, PluginBuildLiveItem item, int index, string spinner)
        {
            var ordinal = (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture).PadLeft(2);
            var packageId = EscapeMarkup(item.Package.PackageId);
            switch (item.State)
            {
                case PluginBuildProgressState.Queued:
                    builder.Append("[dim]○[/] [dim]").Append(ordinal).Append(". Queued ").Append(packageId).AppendLine("[/]");
                    break;
                case PluginBuildProgressState.Running:
                    builder.Append("[primary]").Append(spinner).Append("[/] [primary]").Append(ordinal).Append(". Building ").Append(packageId).AppendLine("[/]");
                    break;
                case PluginBuildProgressState.Succeeded:
                    builder.Append("[success]✓[/] [success]").Append(ordinal).Append(". Plugin ").Append(packageId).AppendLine(" built successfully[/]");
                    break;
                case PluginBuildProgressState.Failed:
                    builder.Append("[error]✗[/] [error]").Append(ordinal).Append(". Plugin ").Append(packageId).AppendLine(" build failed[/]");
                    break;
                case PluginBuildProgressState.UpToDate:
                    builder.Append("[dim]◇[/] [dim]").Append(ordinal).Append(". Plugin ").Append(packageId).AppendLine(" is up-to-date[/]");
                    break;
            }
        }

        private static string EscapeMarkup(string text)
            => text.Replace("[", "\\[", StringComparison.Ordinal).Replace("]", "\\]", StringComparison.Ordinal);
    }

    private sealed class PluginBuildLiveItem(SourcePluginPackage package)
    {
        public SourcePluginPackage Package { get; } = package;

        public PluginBuildProgressState State { get; set; } = PluginBuildProgressState.Queued;

        public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
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
