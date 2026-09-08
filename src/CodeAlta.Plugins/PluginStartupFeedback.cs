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

    internal static string BuildStartupSummary(IReadOnlyList<PluginBuildResult> buildResults, int activatedSourcePluginCount, TimeSpan elapsed)
    {
        ArgumentNullException.ThrowIfNull(buildResults);
        var checkedPackageCount = buildResults.Count;
        var builtPackageCount = buildResults.Count(static build => build.Succeeded && !build.IsUpToDate);
        var upToDatePackageCount = buildResults.Count(static build => build.Succeeded && build.IsUpToDate);
        var failedPackageCount = buildResults.Count(static build => !build.Succeeded);
        var buildSummary = checkedPackageCount == 0
            ? "no source plugins checked"
            : $"{checkedPackageCount} source plugin {Pluralize(checkedPackageCount, "package")} checked ({builtPackageCount} built, {upToDatePackageCount} up-to-date{(failedPackageCount == 0 ? string.Empty : $", {failedPackageCount} failed")})";
        return $"CodeAlta plugins: {buildSummary}; {activatedSourcePluginCount} source {Pluralize(activatedSourcePluginCount, "plugin")} activated in {FormatElapsed(elapsed)}.";
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

    private static string Pluralize(int count, string singular)
        => count == 1 ? singular : singular + "s";

    private static string FormatElapsed(TimeSpan elapsed)
        => elapsed.TotalSeconds >= 1
            ? elapsed.TotalSeconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "s"
            : Math.Max(0, (int)Math.Round(elapsed.TotalMilliseconds, MidpointRounding.AwayFromZero)).ToString(System.Globalization.CultureInfo.InvariantCulture) + "ms";
}

/// <summary>Receives startup phases and build progress without prescribing a frontend toolkit.</summary>
/// <remarks>Progress can arrive on build workers. Implementations own any required presentation
/// dispatch; callbacks are borrowed, not disposed, and their failures retain the caller's policy.</remarks>
public interface IPluginStartupProgress
{
    /// <summary>Reports preparation of source build inputs.</summary>
    void MarkPreparing();
    /// <summary>Reports the start of source builds.</summary>
    void MarkBuilding();
    /// <summary>Reports a scheduler progress transition.</summary>
    /// <param name="progress">The original progress event.</param>
    void Report(PluginBuildProgress progress);
    /// <summary>Reports completed builds before activation.</summary>
    void MarkBuildsCompleted();
    /// <summary>Reports source activation or startup-hook execution.</summary>
    void MarkActivating();
}

/// <summary>Runs a borrowed plugin startup operation with host-selected feedback.</summary>
/// <remarks>No runtime ownership, scheduling, cancellation source or disposal is transferred.
/// Implementations invoke the operation once after successful presentation setup. Normal completion
/// joins it; presentation failure may escape without a join, matching the existing terminal contract.
/// This port does not promise termination, UI affinity or observer-error isolation.</remarks>
public interface IPluginStartupFeedback
{
    /// <summary>Runs the original operation, optionally presenting progress and acknowledgement.</summary>
    /// <typeparam name="T">The unchanged operation result type.</typeparam>
    /// <param name="requests">The original planned source-build requests.</param>
    /// <param name="waitForAcknowledgement">Whether presentation should await completion acknowledgement.</param>
    /// <param name="operation">The borrowed operation; null progress requests silent execution.</param>
    /// <param name="summaryFactory">The synchronous completion-summary factory.</param>
    /// <param name="cancellationToken">The original caller token, not a cancellation-abandonment instruction.</param>
    /// <returns>The operation result after the selected presentation path completes.</returns>
    /// <exception cref="ArgumentNullException">A required reference is null.</exception>
    /// <exception cref="OperationCanceledException">The operation or presentation reports cancellation.</exception>
    /// <exception cref="Exception">Operation, presentation or summary generation failed.</exception>
    ValueTask<T> RunAsync<T>(IReadOnlyList<PluginBuildRequest> requests, bool waitForAcknowledgement,
        Func<IPluginStartupProgress?, CancellationToken, ValueTask<T>> operation,
        Func<T, TimeSpan, string> summaryFactory, CancellationToken cancellationToken);
}

/// <summary>Default stateless feedback: invokes the operation with null progress, without output.</summary>
/// <remarks>Ignores acknowledgement and does not invoke the summary factory. It never detects or
/// initializes a terminal. Each options instance owns its default; supplied instances remain borrowed.</remarks>
public sealed class SilentPluginStartupFeedback : IPluginStartupFeedback
{
    /// <inheritdoc />
    public ValueTask<T> RunAsync<T>(IReadOnlyList<PluginBuildRequest> requests, bool waitForAcknowledgement,
        Func<IPluginStartupProgress?, CancellationToken, ValueTask<T>> operation,
        Func<T, TimeSpan, string> summaryFactory, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requests);
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(summaryFactory);
        return operation(null, cancellationToken);
    }
}

// Mandatory production routing seam. No runtime construction or presentation fallback occurs here.
internal static class PluginStartupFeedbackRouting
{
    internal static ValueTask<T> RunAsync<T>(IReadOnlyList<PluginBuildRequest> requests,
        IPluginStartupFeedback feedback, Func<IPluginStartupProgress?, CancellationToken, ValueTask<T>> operation,
        Func<T, TimeSpan, string> summaryFactory, bool isHeadless, bool waitForAcknowledgement, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requests);
        ArgumentNullException.ThrowIfNull(feedback);
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(summaryFactory);
        return isHeadless || requests.Count == 0
            ? operation(null, cancellationToken)
            : feedback.RunAsync(requests, waitForAcknowledgement, operation, summaryFactory, cancellationToken);
    }
}
