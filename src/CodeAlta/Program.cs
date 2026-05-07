using System.Diagnostics;
using CodeAlta;
using CodeAlta.App;
using CodeAlta.Catalog;
using CodeAlta.Plugins;
using CodeAlta.Plugins.Abstractions;
using CodeAlta.Views;
using XenoAtom.Ansi;
using XenoAtom.CommandLine;
using XenoAtom.Logging;
using XenoAtom.Terminal;

var mainThreadId = Environment.CurrentManagedThreadId;
try
{
    using var session = Terminal.Open();

    // Plugin runtime startup ordering: register MSBuild before any plugin build service, pipe-logger
    // event payload, or Microsoft.Build type can be touched. Safe-mode raw args/environment are
    // still read by host-owned code before dynamic plugins are built or loaded.
    CodeAltaPluginRuntimeStartup.RegisterMsBuildDefaults();
    _ = PluginRuntimeConfigResolver.IsSafeModeEnabled(args);
    var commandLinePluginRuntime = Program.StartPluginRuntimeForCommandLine(args, CancellationToken.None);
    try
    {
        var pluginCommandLineContributions = Program.GetPluginCommandLineContributions(commandLinePluginRuntime);
        var command = CodeAltaCliOptions.CreateCommandApp(
            options => Program.RunAsync(options, mainThreadId, commandLinePluginRuntime),
            pluginCommandLineContributions);
        return command.RunAsync(args).AsTask().GetAwaiter().GetResult();
    }
    finally
    {
        if (commandLinePluginRuntime is not null)
        {
            commandLinePluginRuntime.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        if (LogManager.IsInitialized)
        {
            LogManager.Shutdown();
        }
    }
}
catch (CodeAltaAlreadyRunningException ex)
{
    Terminal.WriteMarkupLine($"[bright-red]{AnsiMarkup.Escape(ex.Message)}[/]");
    return 1;
}
catch (Exception ex)
{
    CodeAltaCrashReporter.ReportFatalException("Top-level exception", ex);
    Terminal.WriteLine(ex.ToString());
    return 1;
}

internal partial class Program
{
    internal static async ValueTask<int> RunAsync(CodeAltaCliOptions options, int mainThreadId, PluginRuntimeManager? prestartedPluginRuntime = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.PluginsStatus)
        {
            PrintPluginsStatus(options.PluginSafeMode);
            return 0;
        }

        using var singleInstanceGuard = CodeAltaSingleInstanceGuard.Acquire();
        var cancellationTokenSource = new CancellationTokenSource();

        // Defer async app startup until the terminal loop is already running so XenoAtom keeps the UI
        // bound to the process main thread. Awaiting service creation before Terminal.RunAsync can move
        // the actual UI bootstrap onto a worker continuation instead.
        await using var app = new DeferredCodeAltaApp(prestartedPluginRuntime);
        if (options.TestMode)
        {
            var logger = LogManager.GetLogger("CodeAlta.Program");
            var testDurationText = options.TestDuration!.Value.TotalSeconds.ToString("0", System.Globalization.CultureInfo.InvariantCulture);
            if (LogManager.IsInitialized && logger.IsEnabled(LogLevel.Debug))
            {
                logger.Debug($"Starting CodeAlta terminal smoke test for {testDurationText}s.");
            }

            Terminal.WriteLine($"[CodeAlta] Starting terminal smoke test for {testDurationText}s.");
        }

        // Enter the terminal immediately after synchronous setup; DeferredCodeAltaApp finishes async
        // initialization from inside the loop instead of before Terminal.RunAsync starts.
        Program.ThrowIfCurrentThreadIsNotMainThread(mainThreadId);
        await app.RunAsync(cancellationTokenSource.Token);

        if (options.TestMode)
        {
            var logger = LogManager.GetLogger("CodeAlta.Program");
            if (LogManager.IsInitialized && logger.IsEnabled(LogLevel.Debug))
            {
                logger.Debug("CodeAlta terminal smoke test exited cleanly.");
            }

            Terminal.WriteLine("[CodeAlta] Terminal smoke test exited cleanly.");
        }

        return 0;
    }

    internal static PluginRuntimeManager? StartPluginRuntimeForCommandLine(
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
        => StartPluginRuntimeForCommandLineAsync(args, cancellationToken).AsTask().GetAwaiter().GetResult();

    internal static async ValueTask<PluginRuntimeManager?> StartPluginRuntimeForCommandLineAsync(
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);
        var homeRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".alta");
        Directory.CreateDirectory(homeRoot);
        CodeAltaLogging.Initialize(homeRoot);
        var currentDirectory = Environment.CurrentDirectory;
        var pluginBootstrapOptions = CodeAltaCliOptions.GetPluginBootstrapOptions(args);
        var runtime = new PluginRuntimeManager();
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var result = await runtime.StartAsync(
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
                    WaitForEnterAfterBuildLiveOutput = pluginBootstrapOptions.WaitForEnterAfterPluginLiveOutput,
                    RawArguments = args,
                },
                cancellationToken);
            stopwatch.Stop();
            ReportCommandLinePluginStartup(result, stopwatch.Elapsed, pluginBootstrapOptions);
            return runtime;
        }
        catch
        {
            await runtime.DisposeAsync();
            throw;
        }
    }

    internal static void ReportCommandLinePluginStartup(PluginRuntimeManagerStartResult result, TimeSpan elapsed, CodeAltaPluginBootstrapOptions pluginBootstrapOptions)
    {
        ArgumentNullException.ThrowIfNull(result);
        var homeRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".alta");
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
        if (!LogManager.IsInitialized)
        {
            return;
        }

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

    private static void PrintPluginsStatus(bool pluginSafeMode)
    {
        var homeRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".alta");
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
    }
}
