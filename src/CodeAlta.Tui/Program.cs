using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using CodeAlta.Agent;
using CodeAlta.Hosting;
using CodeAlta.Tui;
using CodeAlta.Tui.App;
using CodeAlta.Catalog;
using CodeAlta.Plugins;
using CodeAlta.Plugins.Abstractions;
using CodeAlta.Tui.Views;
using XenoAtom.Ansi;
using XenoAtom.CommandLine;
using XenoAtom.Logging;
using XenoAtom.Terminal;

var mainThreadId = Environment.CurrentManagedThreadId;
Program.StartupOwner? startupOwner = null;
try
{
    return CodeAltaStartupAdmission.Run(
        args,
        Program.RunEarlyCommand,
        () =>
        {
            startupOwner = Program.StartupOwner.CreateProduction();
            // The developer instance (--dev) takes its own lock, so it runs beside the normal instance.
            var lockFilePath = CodeAltaInstanceProfile.FromArguments(args).LockFilePath;
            return startupOwner.AcquireAdmission(() => CodeAltaSingleInstanceGuard.Acquire(lockFilePath, startupOwner.GuardEvidence));
        },
        () => Program.RunAdmittedStartup(args, mainThreadId, startupOwner!));
}
catch (Exception ex)
{
    startupOwner?.Capture(ex);
    // Admission/early-output failures must not initialize logging, crash reporting or Terminal.
    Console.Error.WriteLine(ex.Message);
    return 1;
}
finally
{
    startupOwner?.FinishAfterAdmissionUnwind();
}

internal partial class Program
{
    // One invocation owns one normal CLR handle. Only FinishAfterAdmissionUnwind may release it.
    // Retained catastrophic failure intentionally keeps the actual graph rooted until CLR shutdown.
    internal sealed class StartupOwner
    {
        private readonly object _gate = new();
        private readonly Action<StartupOwner> _allocateAnchor;
        private readonly Action<StartupOwner> _releaseAnchor;
        private readonly AdmissionWrapper _admission;
        private readonly List<StartupOperation> _operations = [];
        private readonly List<Exception> _failures = [];
        private GCHandle _anchor;
        private bool _anchorAllocated;
        private bool _anchorReleaseAttempted;
        private bool _startupSettled;
        private bool _admissionReleased;
        private bool _admissionReleaseAttempted;
        private bool _retained;
        private StartupOperation? _pluginRelease;
        private StartupOperation? _terminalRelease;
        private StartupOperation? _loggingRelease;
        private StartupOperation? _runSourceRelease;
        private StartupOperation? _appRelease;

        internal StartupOwner(Action<StartupOwner> allocateAnchor, Action<StartupOwner> releaseAnchor)
        {
            ArgumentNullException.ThrowIfNull(allocateAnchor);
            ArgumentNullException.ThrowIfNull(releaseAnchor);
            _allocateAnchor = allocateAnchor;
            _releaseAnchor = releaseAnchor;
            _admission = new AdmissionWrapper(this);
        }

        internal static StartupOwner CreateProduction() => new(
            static owner => owner._anchor = GCHandle.Alloc(owner, GCHandleType.Normal),
            static owner => owner._anchor.Free()) { ShutdownLogging = LogManager.Shutdown };

        internal CodeAltaSingleInstanceGuard.AcquisitionEvidence<FileStream> GuardEvidence { get; } = new();
        internal IDisposable? AdmissionLease { get; private set; }
        internal object? Terminal { get; set; }
        internal Action? ReleaseTerminal { get; set; }
        internal PluginRuntimeManager? Plugins { get; set; }
        internal DeferredCodeAltaApp? App { get; set; }
        internal CancellationTokenSource? RunCancellation { get; set; }
        internal bool LoggingRequired { get; set; }
        internal Action? ShutdownLogging { get; init; }
        internal bool HasRetainedEvidence { get { lock (_gate) return _retained; } }

        internal IDisposable AcquireAdmission(Func<IDisposable> acquire)
        {
            ArgumentNullException.ThrowIfNull(acquire);
            _allocateAnchor(this);
            _anchorAllocated = true;
            try
            {
                AdmissionLease = acquire() ?? throw new InvalidOperationException("Admission returned no lease.");
                return _admission;
            }
            catch (Exception failure)
            {
                Capture(failure);
                _startupSettled = true;
                if (GuardEvidence.RollbackFailure is not null)
                    throw Retain("guard rollback", failure);
                throw;
            }
        }

        internal void Capture(Exception failure)
        {
            ArgumentNullException.ThrowIfNull(failure);
            var retained = ContainsRetention(failure);
            lock (_gate)
            {
                _failures.Add(failure);
                _retained |= retained;
            }
        }

        internal static bool ContainsRetention(Exception failure)
        {
            if (AgentDependencyRetentionException.Contains(failure)) return true;
            var pending = new Stack<Exception>();
            var visited = new HashSet<Exception>(ReferenceEqualityComparer.Instance);
            pending.Push(failure);
            while (pending.TryPop(out var current))
            {
                if (!visited.Add(current)) continue;
                if (current is PluginEventDependencyException) return true;
                if (current is AggregateException aggregate)
                    foreach (var inner in aggregate.InnerExceptions) pending.Push(inner);
                else if (current.InnerException is { } inner) pending.Push(inner);
            }
            return false;
        }

        private AgentDependencyRetentionException Retain(string stage, Exception failure)
        {
            var retained = new AgentDependencyRetentionException("startup", stage, [failure], this);
            Capture(retained);
            return retained;
        }

        internal StartupOperation Start(string stage, Func<Task> invoke)
        {
            var operation = new StartupOperation(this, stage, invoke);
            lock (_gate) _operations.Add(operation);
            operation.Launch();
            return operation;
        }

        internal async ValueTask<int> RunCommandAsync(Func<ValueTask<int>> command)
        {
            var operation = Start("command callback", () => command().AsTask());
            await operation.Completion;
            return ((Task<int>)operation.Original!).GetAwaiter().GetResult();
        }

        internal void ReleasePluginsIfEligible() => ReleasePluginsIfEligibleAsync().AsTask().GetAwaiter().GetResult();

        internal async ValueTask ReleasePluginsIfEligibleAsync()
        {
            if (HasRetainedEvidence || Plugins is null) return;
            _pluginRelease ??= Start("plugin release", () => Plugins.DisposeAsync().AsTask());
            try { await _pluginRelease.Completion; }
            catch (Exception failure) { throw Retain("plugin release", failure); }
        }

        internal void ReleaseStartupIfEligible()
        {
            if (HasRetainedEvidence) return;
            ReleasePluginsIfEligible();
            if (RunCancellation is not null)
                Release(ref _runSourceRelease, "run source release", RunCancellation.Dispose);
            if (ReleaseTerminal is not null)
                Release(ref _terminalRelease, "terminal release", ReleaseTerminal);
            if (LoggingRequired)
                Release(ref _loggingRelease, "logging release", ShutdownLogging
                    ?? throw new InvalidOperationException("Logging shutdown was not supplied."));
        }

        private void Release(ref StartupOperation? record, string stage, Action release)
        {
            if (HasRetainedEvidence) return;
            record ??= Start(stage, () => { release(); return Task.CompletedTask; });
            try { record.Completion.GetAwaiter().GetResult(); }
            catch (Exception failure) { throw Retain(stage, failure); }
        }

        internal void MarkStartupSettled() => _startupSettled = true;

        internal StartupOperation StartAppRelease(DeferredCodeAltaApp app)
        {
            ArgumentNullException.ThrowIfNull(app);
            if (!ReferenceEquals(App, app)) throw new InvalidOperationException("The startup owner does not own this application.");
            return _appRelease ??= Start("deferred disposal", () => app.DisposeAsync().AsTask());
        }

        private bool RequiredDependenciesReleased()
        {
            lock (_gate)
            {
                if (_operations.Any(static operation => !operation.Outcome.IsCompleted)) return false;
            }
            return (Plugins is null || Released(_pluginRelease))
                && (RunCancellation is null || Released(_runSourceRelease))
                && (Terminal is null && ReleaseTerminal is null || Released(_terminalRelease))
                && (!LoggingRequired || Released(_loggingRelease))
                // Deferred disposal may report ordinary errors after its best-effort cleanup. Its
                // explicit retained graphs are captured by StartupOperation before Outcome settles.
                && (App is null || _appRelease is { Original: not null } appRelease && appRelease.Outcome.IsCompleted);

            static bool Released(StartupOperation? release)
                => release is { Original: not null } && release.Outcome.IsCompletedSuccessfully
                    && release.Outcome.GetAwaiter().GetResult() is null;
        }

        private void ReleaseAdmission()
        {
            if (AdmissionLease is null || !TryAuthorizeRelease(false,
                () => _startupSettled && RequiredDependenciesReleased())) return;
            try
            {
                AdmissionLease.Dispose();
                lock (_gate) _admissionReleased = true;
            }
            catch (Exception failure) { throw Retain("admission release", failure); }
        }

        internal void FinishAfterAdmissionUnwind()
        {
            if (!_anchorAllocated || !TryAuthorizeRelease(true,
                () => _startupSettled && RequiredDependenciesReleased()
                    && (AdmissionLease is null || _admissionReleased)
                    && (GuardEvidence.Candidate is null || _admissionReleased || GuardEvidence.RollbackCompleted))) return;
            try
            {
                _releaseAnchor(this);
                lock (_gate) _anchorAllocated = false;
            }
            catch (Exception failure) { throw Retain("anchor release", failure); }
        }

        // Outcome inspection runs outside the gate; final retention/once-only authorization shares
        // Capture's evidence gate. An original captures retention before publishing its Outcome.
        internal bool TryAuthorizeRelease(bool anchor, Func<bool> prerequisites)
        {
            ArgumentNullException.ThrowIfNull(prerequisites);
            lock (_gate)
            {
                if (_retained || (anchor ? _anchorReleaseAttempted : _admissionReleaseAttempted)) return false;
            }
            bool confirmed;
            try { confirmed = prerequisites(); }
            catch (Exception failure) { throw Retain("release eligibility", failure); }
            var missing = confirmed ? null : new AgentDependencyRetentionException("startup", "release eligibility",
                [new InvalidOperationException("Required startup dependency release was not confirmed.")], this);
            lock (_gate)
            {
                // Revalidate after required outcomes settle; never authorize from the earlier latch read.
                if (_retained || (anchor ? _anchorReleaseAttempted : _admissionReleaseAttempted)) return false;
                if (missing is null)
                {
                    if (anchor) _anchorReleaseAttempted = true;
                    else _admissionReleaseAttempted = true;
                    return true;
                }
                _retained = true;
                _failures.Add(missing);
            }
            throw missing;
        }

        private sealed class AdmissionWrapper(StartupOwner owner) : IDisposable
        {
            public void Dispose() => owner.ReleaseAdmission();
        }

        internal sealed class StartupOperation
        {
            private readonly StartupOwner _owner;
            private readonly Func<Task> _invoke;
            private readonly TaskCompletionSource<Task> _launched = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal StartupOperation(StartupOwner owner, string stage, Func<Task> invoke)
            {
                _owner = owner;
                _invoke = invoke;
                Stage = stage;
                Work = RunAsync();
                Outcome = ObserveAsync();
                Completion = ReportAsync();
            }
            internal string Stage { get; }
            internal Task? Original { get; private set; }
            internal Task Work { get; }
            internal Task Completion { get; }
            internal Task<Exception?> Outcome { get; }
            internal AggregateException? OriginalFaults { get; private set; }
            internal void Launch()
            {
                // Invoke on the admitting thread: terminal bootstrap must remain on the main thread.
                try { Original = _invoke(); _launched.TrySetResult(Original); }
                catch (Exception failure) { _launched.TrySetException(failure); }
            }
            private async Task RunAsync()
            {
                try
                {
                    var original = await _launched.Task.ConfigureAwait(false);
                    await original.ConfigureAwait(false);
                }
                catch (Exception failure)
                {
                    OriginalFaults = Original?.Exception;
                    _owner.Capture(failure);
                    if (OriginalFaults is not null && ContainsRetention(OriginalFaults)) _owner.Capture(OriginalFaults);
                    throw;
                }
            }
            private async Task<Exception?> ObserveAsync()
            {
                try { await Work.ConfigureAwait(false); return null; }
                catch (Exception failure) { return failure; }
            }
            private async Task ReportAsync()
            {
                var failure = await Outcome.ConfigureAwait(false);
                if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
            }
        }
    }

    internal static int RunEarlyCommand(string argument)
    {
        var command = CodeAltaCliOptions.CreatePlainCommandApp(
            static _ => throw new InvalidOperationException("Early commands must not enter mutable startup."));
        return command.RunAsync([argument]).AsTask().GetAwaiter().GetResult();
    }

    internal static int RunAdmittedStartup(string[] args, int mainThreadId, StartupOwner owner)
    {
        try
        {
            owner.LoggingRequired = true;
            CodeAltaLogging.Initialize(CodeAltaInstanceProfile.FromArguments(args).StateRoot);

            // Plugin runtime startup ordering: register MSBuild before any plugin build service, pipe-logger
            // event payload, or Microsoft.Build type can be touched. Safe-mode raw args/environment are
            // still read by host-owned code before dynamic plugins are built or loaded.
            // Disabled for now until https://github.com/dotnet/sdk/pull/54172 is merged
            // //CodeAltaPluginRuntimeStartup.RegisterMsBuildDefaults();
            var session = Terminal.Open();
            owner.Terminal = session;
            owner.ReleaseTerminal = session.Dispose;
            // Two CodeAlta consoles are told apart by their title.
            if (CodeAltaInstanceProfile.IsDeveloperRequested(args)) Terminal.Title = "CodeAlta (dev)";

            _ = PluginRuntimeConfigResolver.IsSafeModeEnabled(args);
            var commandLinePluginRuntime = Program.StartPluginRuntimeForCommandLine(args, CancellationToken.None, owner);
            try
            {
                var pluginCommandLineContributions = Program.GetPluginCommandLineContributions(commandLinePluginRuntime);
                var command = CodeAltaCliOptions.CreateCommandApp(
                    options => owner.RunCommandAsync(() => Program.RunAsync(options, mainThreadId, commandLinePluginRuntime, owner)),
                    pluginCommandLineContributions);
                var invocation = owner.Start("command", () => command.RunAsync(args).AsTask());
                invocation.Completion.GetAwaiter().GetResult();
                return ((Task<int>)invocation.Original!).GetAwaiter().GetResult();
            }
            catch (Exception failure) { owner.Capture(failure); throw; }
            finally
            {
                owner.ReleasePluginsIfEligible();
            }
        }
        catch (CodeAltaAlreadyRunningException ex)
        {
            owner.Capture(ex);
            Terminal.WriteMarkupLine($"[bright-red]{AnsiMarkup.Escape(ex.Message)}[/]");
            return 1;
        }
        catch (Exception ex)
        {
            owner.Capture(ex);
            try
            {
                LogManager.GetLogger("CodeAlta.Program").Error(ex, "Top-level exception");
            }
            catch
            {
            }

            CodeAltaCrashReporter.ReportFatalException("Top-level exception", ex);
            Terminal.WriteLine(ex.ToString());
            return 1;
        }
        finally
        {
            try { owner.ReleaseStartupIfEligible(); }
            finally { owner.MarkStartupSettled(); }
        }
    }

    internal static async ValueTask<int> RunAsync(CodeAltaCliOptions options, int mainThreadId, PluginRuntimeManager? prestartedPluginRuntime, StartupOwner owner)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.PluginsStatus)
        {
            return PrintPluginsStatus(options.PluginSafeMode);
        }

        var cancellationTokenSource = new CancellationTokenSource();
        owner.RunCancellation = cancellationTokenSource;

        // Defer async app startup until the terminal loop is already running so XenoAtom keeps the UI
        // bound to the process main thread. Awaiting service creation before Terminal.RunAsync can move
        // the actual UI bootstrap onto a worker continuation instead.
        var app = new DeferredCodeAltaApp(prestartedPluginRuntime);
        owner.App = app;
        Exception? bodyFailure = null;
        try
        {
        if (options.TestMode)
        {
            var logger = LogManager.GetLogger("CodeAlta.Program");
            var testDurationText = options.TestDuration!.Value.TotalSeconds.ToString("0", System.Globalization.CultureInfo.InvariantCulture);
            logger.Debug($"Starting CodeAlta terminal smoke test for {testDurationText}s.");

            Terminal.WriteLine($"[CodeAlta] Starting terminal smoke test for {testDurationText}s.");
        }

        // Enter the terminal immediately after synchronous setup; DeferredCodeAltaApp finishes async
        // initialization from inside the loop instead of before Terminal.RunAsync starts.
        Program.ThrowIfCurrentThreadIsNotMainThread(mainThreadId);
        var terminalRun = owner.Start("terminal run", () => app.RunAsync(cancellationTokenSource.Token).AsTask());
        await terminalRun.Completion;
        PrintUpdateAvailableMessage(app.UpdateCheckSnapshot);

        if (options.TestMode)
        {
            var logger = LogManager.GetLogger("CodeAlta.Program");
            logger.Debug("CodeAlta terminal smoke test exited cleanly.");

            Terminal.WriteLine("[CodeAlta] Terminal smoke test exited cleanly.");
        }

        return 0;
        }
        catch (Exception failure) { bodyFailure = failure; owner.Capture(failure); throw; }
        finally
        {
            // Capture both originals before the command library can turn failure into an exit code.
            var cleanup = owner.StartAppRelease(app);
            try { await cleanup.Completion; }
            catch (Exception failure)
            {
                if (bodyFailure is not null) throw new AggregateException(bodyFailure, failure);
                throw;
            }
        }
    }

    private static void PrintUpdateAvailableMessage(CodeAltaUpdateCheckSnapshot snapshot)
    {
        if (!snapshot.HasNewerVersion)
        {
            return;
        }

        Terminal.WriteLine($"A new version {snapshot.LatestVersionText} of CodeAlta is available!");
        Terminal.WriteLine($"To update: {snapshot.UpdateCommand}");
    }

    internal static PluginRuntimeManager? StartPluginRuntimeForCommandLine(
        IReadOnlyList<string> args,
        CancellationToken cancellationToken, StartupOwner owner)
        => StartPluginRuntimeForCommandLineAsync(args, cancellationToken, owner).AsTask().GetAwaiter().GetResult();

    internal static async ValueTask<PluginRuntimeManager?> StartPluginRuntimeForCommandLineAsync(
        IReadOnlyList<string> args,
        CancellationToken cancellationToken, StartupOwner owner)
    {
        ArgumentNullException.ThrowIfNull(args);
        var homeRoot = GetDefaultHomeRoot();
        Directory.CreateDirectory(homeRoot);
        CodeAltaLogging.Initialize(CodeAltaInstanceProfile.FromArguments(args).StateRoot);
        var currentDirectory = Environment.CurrentDirectory;
        var pluginBootstrapOptions = CodeAltaCliOptions.GetPluginBootstrapOptions(args);
        if (!CanStartPluginRuntimeBeforeConfigRecovery(homeRoot))
        {
            return null;
        }

        var runtime = new PluginRuntimeManager();
        owner.Plugins = runtime;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var startup = owner.Start("plugin startup", () => runtime.StartAsync(
                new PluginRuntimeManagerOptions
                {
                    GlobalRoot = homeRoot,
                    ProjectContext = new PluginProjectContext
                    {
                        ProjectId = "current",
                        ProjectPath = currentDirectory,
                    },
                    SafeMode = pluginBootstrapOptions.PluginSafeMode,
                    IsHeadless = false,
                    StartupFeedback = new CodeAlta.Tui.Plugins.TerminalPluginStartupFeedback(),
                    AuthoringProfile = PluginAuthoringProfile.Terminal,
                    WaitForEnterAfterBuildLiveOutput = pluginBootstrapOptions.WaitForEnterAfterPluginLiveOutput,
                    RawArguments = args,
                    BuiltIns = CodeAltaBuiltInPlugins.All,
                },
                cancellationToken).AsTask());
            await startup.Completion;
            var result = ((Task<PluginRuntimeManagerStartResult>)startup.Original!).GetAwaiter().GetResult();
            stopwatch.Stop();
            ReportCommandLinePluginStartup(result, stopwatch.Elapsed, pluginBootstrapOptions);
            return runtime;
        }
        catch (Exception failure)
        {
            owner.Capture(failure);
            try { await owner.ReleasePluginsIfEligibleAsync(); }
            catch (Exception cleanup) { throw new AggregateException(failure, cleanup); }
            throw;
        }
    }

    internal static void ReportCommandLinePluginStartup(PluginRuntimeManagerStartResult result, TimeSpan elapsed, CodeAltaPluginBootstrapOptions pluginBootstrapOptions)
    {
        ArgumentNullException.ThrowIfNull(result);
        var homeRoot = GetDefaultHomeRoot();
        var checkedPackageCount = result.BuildResults.Count;
        var builtPackageCount = result.BuildResults.Count(static build => build.Succeeded && !build.IsUpToDate);
        var upToDatePackageCount = result.BuildResults.Count(static build => build.Succeeded && build.IsUpToDate);
        var failedPackageCount = result.BuildResults.Count(static build => !build.Succeeded);
        var activatedSourcePluginCount = result.ActivePlugins.Count(static plugin => plugin.SourcePackage is not null);
        LogPluginStartup(result, elapsed, homeRoot);
        if (checkedPackageCount == 0 && activatedSourcePluginCount == 0 && failedPackageCount == 0)
        {
            return;
        }

        var startupSummaryShownInLiveOutput = pluginBootstrapOptions.WaitForEnterAfterPluginLiveOutput
            && checkedPackageCount > 0
            && Terminal.Instance.IsInitialized
            && !Terminal.Instance.Capabilities.IsOutputRedirected;
        if (!startupSummaryShownInLiveOutput && (checkedPackageCount > 0 || failedPackageCount > 0 || pluginBootstrapOptions.PluginsStatus || pluginBootstrapOptions.WaitForEnterAfterPluginLiveOutput))
        {
            var buildSummary = checkedPackageCount == 0
                ? "no source plugins checked"
                : $"{checkedPackageCount} source plugin {Pluralize(checkedPackageCount, "package")} checked ({builtPackageCount} built, {upToDatePackageCount} up-to-date{(failedPackageCount == 0 ? string.Empty : $", {failedPackageCount} failed")})";
            Terminal.WriteLine($"CodeAlta plugins: {buildSummary}; {activatedSourcePluginCount} source {Pluralize(activatedSourcePluginCount, "plugin")} activated in {FormatElapsed(elapsed)}.");
        }

        ReportPluginFailuresToTerminal(result.BuildResults, homeRoot);
    }

    private static void ReportPluginFailuresToTerminal(IReadOnlyList<PluginBuildResult> buildResults, string homeRoot)
    {
        var failedBuilds = buildResults.Where(static build => !build.Succeeded).ToArray();
        if (failedBuilds.Length == 0)
        {
            return;
        }

        var displayCount = Math.Min(failedBuilds.Length, 8);
        Terminal.WriteLine($"Plugin build failures (showing {displayCount} of {failedBuilds.Length}):");
        foreach (var build in failedBuilds.Take(displayCount))
        {
            Terminal.WriteLine($"- {build.Package.PackageId}: {FormatPluginFailureReason(build)}");
            if (!string.IsNullOrWhiteSpace(build.Package.EntryFilePath))
            {
                Terminal.WriteLine($"  source: {build.Package.EntryFilePath}");
            }
        }

        Terminal.WriteLine($"Plugin diagnostics were written to {CodeAltaLogging.GetLogFilePath(homeRoot)}.");
        Terminal.WriteLine("Run `alta --plugins-status` or open `/plugins` after startup for plugin status details.");
    }

    private static void LogPluginStartup(PluginRuntimeManagerStartResult result, TimeSpan elapsed, string homeRoot)
    {
        var logger = LogManager.GetLogger("CodeAlta.Plugins");
        var checkedPackageCount = result.BuildResults.Count;
        var builtPackageCount = result.BuildResults.Count(static build => build.Succeeded && !build.IsUpToDate);
        var upToDatePackageCount = result.BuildResults.Count(static build => build.Succeeded && build.IsUpToDate);
        var failedBuilds = result.BuildResults.Where(static build => !build.Succeeded).ToArray();
        var activatedSourcePluginCount = result.ActivePlugins.Count(static plugin => plugin.SourcePackage is not null);
        logger.Info($"Plugin startup completed in {FormatElapsed(elapsed)}: {checkedPackageCount} source package(s) checked, {builtPackageCount} built, {upToDatePackageCount} up-to-date, {failedBuilds.Length} failed, {activatedSourcePluginCount} source plugin(s) activated. Log file: {CodeAltaLogging.GetLogFilePath(homeRoot)}");

        foreach (var diagnostic in result.Diagnostics.Where(static diagnostic => diagnostic.Severity >= PluginDiagnosticSeverity.Warning && diagnostic.Source != PluginRuntimeDiagnosticSource.Build))
        {
            LogRuntimeDiagnostic(logger, diagnostic);
        }

        foreach (var build in failedBuilds)
        {
            logger.Error(FormatPluginBuildFailureForLog(build));
        }
    }

    private static string GetDefaultHomeRoot()
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".alta");

    internal static bool CanStartPluginRuntimeBeforeConfigRecovery(string homeRoot)
    {
        var validation = ValidateExistingGlobalConfigForStartup(homeRoot, out _);
        return validation.IsValid;
    }

    private static CodeAltaConfigValidationResult ValidateExistingGlobalConfigForStartup(string homeRoot, out string configPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(homeRoot);
        configPath = Path.Combine(homeRoot, "config.toml");
        if (!File.Exists(configPath))
        {
            return CodeAltaConfigValidationResult.Valid;
        }

        string content;
        try
        {
            content = File.ReadAllText(configPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new CodeAltaConfigValidationResult(false, $"Unable to read config file: {ex.Message}", null, null);
        }

        return CodeAltaConfigStore.ValidateGlobalConfigContent(content, configPath);
    }

    private static void LogRuntimeDiagnostic(Logger logger, PluginRuntimeDiagnostic diagnostic)
    {
        var message = $"Plugin runtime diagnostic [{diagnostic.Severity}/{diagnostic.Source}]" +
            (string.IsNullOrWhiteSpace(diagnostic.PackageId) ? string.Empty : $" {diagnostic.PackageId}") +
            (string.IsNullOrWhiteSpace(diagnostic.Path) ? string.Empty : $" ({diagnostic.Path})") +
            $": {diagnostic.Message}";
        if (diagnostic.Severity >= PluginDiagnosticSeverity.Error)
        {
            logger.Error(message);
        }
        else
        {
            logger.Warn(message);
        }
    }

    private static string FormatPluginFailureReason(PluginBuildResult build)
    {
        foreach (var diagnostic in build.RuntimeDiagnostics.Where(static diagnostic => diagnostic.Severity >= PluginDiagnosticSeverity.Error && !IsGenericBuildStatusMessage(diagnostic.Message)))
        {
            return diagnostic.Message;
        }

        foreach (var diagnostic in build.Diagnostics.Where(static diagnostic => diagnostic.Severity >= PluginDiagnosticSeverity.Error))
        {
            return FormatBuildDiagnostic(diagnostic);
        }

        foreach (var line in EnumerateNonEmptyLines(build.StandardError).Concat(EnumerateNonEmptyLines(build.StandardOutput)))
        {
            return line;
        }

        return build.ExitCode is null
            ? "Build failed before the dotnet process returned an exit code."
            : $"Build failed with exit code {build.ExitCode.Value}.";
    }

    private static bool IsGenericBuildStatusMessage(string message)
        => string.Equals(message, "Build FAILED.", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(message, "Build failed.", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(message, "Build started.", StringComparison.OrdinalIgnoreCase);

    private static string FormatPluginBuildFailureForLog(PluginBuildResult build)
    {
        var builder = new System.Text.StringBuilder();
        builder.AppendLine($"Plugin '{build.Package.PackageId}' build failed.");
        builder.AppendLine($"Source: {build.Package.EntryFilePath}");
        builder.AppendLine($"Package directory: {build.Package.PackageDirectory}");
        builder.AppendLine($"Plugin root: {build.Package.Root.RootPath}");
        builder.AppendLine($"Exit code: {(build.ExitCode is null ? "<none>" : build.ExitCode.Value.ToString(System.Globalization.CultureInfo.InvariantCulture))}");
        AppendRuntimeDiagnostics(builder, build.RuntimeDiagnostics);
        AppendBuildDiagnostics(builder, build.Diagnostics);
        AppendTextTail(builder, "stdout", build.StandardOutput);
        AppendTextTail(builder, "stderr", build.StandardError);
        return builder.ToString().TrimEnd();
    }

    private static void AppendRuntimeDiagnostics(System.Text.StringBuilder builder, IReadOnlyList<PluginRuntimeDiagnostic> diagnostics)
    {
        if (diagnostics.Count == 0)
        {
            return;
        }

        builder.AppendLine("Runtime diagnostics:");
        foreach (var diagnostic in diagnostics)
        {
            builder.AppendLine($"  [{diagnostic.Severity}/{diagnostic.Source}] {diagnostic.Message}");
            if (!string.IsNullOrWhiteSpace(diagnostic.Path))
            {
                builder.AppendLine($"    Path: {diagnostic.Path}");
            }

            if (diagnostic.Exception is not null)
            {
                builder.AppendLine($"    Exception: {diagnostic.Exception.TypeName}: {diagnostic.Exception.Message}");
            }
        }
    }

    private static void AppendBuildDiagnostics(System.Text.StringBuilder builder, IReadOnlyList<PluginBuildDiagnostic> diagnostics)
    {
        if (diagnostics.Count == 0)
        {
            return;
        }

        builder.AppendLine("Build diagnostics:");
        foreach (var diagnostic in diagnostics)
        {
            builder.AppendLine($"  [{diagnostic.Severity}] {FormatBuildDiagnostic(diagnostic)}");
        }
    }

    private static string FormatBuildDiagnostic(PluginBuildDiagnostic diagnostic)
    {
        var location = string.IsNullOrWhiteSpace(diagnostic.File)
            ? string.Empty
            : diagnostic.LineNumber > 0
                ? $"{diagnostic.File}({diagnostic.LineNumber},{Math.Max(1, diagnostic.ColumnNumber)}) "
                : diagnostic.File + " ";
        var code = string.IsNullOrWhiteSpace(diagnostic.Code) ? string.Empty : diagnostic.Code + ": ";
        return location + code + diagnostic.Message;
    }

    private static void AppendTextTail(System.Text.StringBuilder builder, string label, string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        builder.AppendLine($"Captured {label} tail:");
        foreach (var line in EnumerateNonEmptyLines(GetTail(text, 4096)))
        {
            builder.AppendLine("  " + line);
        }
    }

    private static IEnumerable<string> EnumerateNonEmptyLines(string text)
    {
        using var reader = new StringReader(text);
        while (reader.ReadLine() is { } line)
        {
            if (!string.IsNullOrWhiteSpace(line))
            {
                yield return line.Trim();
            }
        }
    }

    private static string GetTail(string text, int maximumLength)
        => text.Length <= maximumLength ? text : text[^maximumLength..];

    private static string Pluralize(int count, string singular)
        => count == 1 ? singular : singular + "s";

    private static string FormatElapsed(TimeSpan elapsed)
        => elapsed.TotalSeconds >= 1
            ? elapsed.TotalSeconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "s"
            : Math.Max(0, (int)Math.Round(elapsed.TotalMilliseconds, MidpointRounding.AwayFromZero)).ToString(System.Globalization.CultureInfo.InvariantCulture) + "ms";

    internal static IReadOnlyList<CommandNode> GetPluginCommandLineContributions(PluginRuntimeManager? runtime)
    {
        if (runtime is null)
        {
            return [];
        }

        return runtime.Adapter.GetContributions<CommandNode>(
                PluginPoint.CommandLine,
                new PluginAdapterOperationOptions
                {
                    ProjectPath = Environment.CurrentDirectory,
                    HasInteractiveUi = true,
                })
            .Select(static registration => (CommandNode)registration.Contribution)
            .ToArray();
    }

    internal static void ThrowIfCurrentThreadIsNotMainThread(int mainThreadId)
    {
        var currentThreadId = Environment.CurrentManagedThreadId;
        if (currentThreadId == mainThreadId)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Program.RunAsync must start on the process main thread. Expected thread {mainThreadId}, but the current thread is {currentThreadId}.");
    }

    private static int PrintPluginsStatus(bool pluginSafeMode)
    {
        var homeRoot = GetDefaultHomeRoot();
        var validation = ValidateExistingGlobalConfigForStartup(homeRoot, out var configPath);
        if (!validation.IsValid)
        {
            Terminal.WriteLine($"CodeAlta config is invalid: {configPath}");
            if (validation.Line is { } line)
            {
                Terminal.WriteLine($"Line {line}, column {validation.Column.GetValueOrDefault(1)}: {validation.Message ?? "Configuration is invalid."}");
            }
            else
            {
                Terminal.WriteLine(validation.Message ?? "Configuration is invalid.");
            }

            Terminal.WriteLine("Start CodeAlta without --plugins-status to repair ~/.alta/config.toml in recovery mode.");
            return 1;
        }

        var currentProject = new ProjectDescriptor
        {
            Id = "current",
            Name = Path.GetFileName(Environment.CurrentDirectory),
            Slug = Path.GetFileName(Environment.CurrentDirectory),
            DisplayName = Path.GetFileName(Environment.CurrentDirectory),
            ProjectPath = Environment.CurrentDirectory,
        };
        var snapshot = new PluginManagementService(new CatalogOptions { GlobalRoot = homeRoot }, () => currentProject).LoadSnapshot();
        Terminal.WriteLine($"Plugin safe mode: {(pluginSafeMode || snapshot.SafeMode ? "enabled" : "disabled")}");
        Terminal.WriteLine($"Project plugin root: {Path.Combine(currentProject.ProjectPath, ".alta", "plugins")}");
        Terminal.WriteLine($"Discovered plugin entries: {snapshot.Entries.Count}");
        foreach (var entry in snapshot.Entries)
        {
            Terminal.WriteLine($"- {entry.DisplayName} [{entry.LoadUnitKind}/{entry.Scope}/{entry.State}] enabled={entry.Enabled} key={entry.Key}");
            if (!string.IsNullOrWhiteSpace(entry.SourcePath))
            {
                Terminal.WriteLine($"  source: {entry.SourcePath}");
            }

            foreach (var diagnostic in entry.Diagnostics.Take(5))
            {
                Terminal.WriteLine($"  {diagnostic.Severity}: {diagnostic.Message}");
            }
        }

        return 0;
    }
}
