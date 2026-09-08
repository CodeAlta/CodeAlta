using System.Runtime.CompilerServices;

namespace CodeAlta.Tests;

[TestClass]
public sealed class CodeAltaUpdateServiceSourceTests
{
    [TestMethod]
    public void UpdateStart_SourceWiring_CapturesTokenBeforeScheduling()
    {
        // Named-checkout evidence only. No updater, scheduler, checker or UI operation runs.
        var source = ReadSource("CodeAlta.Tui/Views/CodeAltaUpdateService.cs");
        RequireOnce(source, "private bool _startRequested;");

        RequireOnce(source, "private bool _stopRequested;");
        RequireOnce(source, "private CancellationTokenSource? _cancellationTokenSource;");
        RequireOnce(source, "private Task? _checkTask;");
        var constructor = Scope(source, "    public CodeAltaUpdateService()", "\n    }\n");
        Reject(constructor, "new CancellationTokenSource(", "Start();", "CheckAsync(", "Task.Run(");

        var start = Scope(source, "    public void Start()", "\n    }\n");
        RequireOnce(start, """
                    StartUpdateCheck(
                        ref _startRequested,
                        _stopRequested,
                        ref _cancellationTokenSource,
                        ref _checkTask,
                        () => SetSnapshot(Snapshot with
                        {
                            Status = CodeAltaUpdateCheckStatus.Checking
                        }),
                        token => Task.Run(() => CheckAsync(token)));
            """);
        // This exact adapter has no scheduler cancellation argument and captures only the token value.
        RequireOnce(source, "Task.Run(");
        RequireOnce(source, "        StartUpdateCheck(\n");
        RequireOnce(source, "CodeAltaUpdateChecker.CheckCurrentAssemblyAsync(");
        Reject(start, "_cancellationTokenSource.Token", "new CancellationTokenSource(", "if (", "??");

        var admission = Scope(source, "    internal static void StartUpdateCheck(", "\n    }\n");
        RequireOnce(admission, """
                internal static void StartUpdateCheck(
                    ref bool startRequested,
                    bool stopRequested,
                    ref CancellationTokenSource? cancellationTokenSource,
                    ref Task? checkTask,
                    Action publishChecking,
                    Func<CancellationToken, Task> startCheck)
            """);
        RequireOrdered(admission,
            "ArgumentNullException.ThrowIfNull(publishChecking);",
            "ArgumentNullException.ThrowIfNull(startCheck);",
            "if (stopRequested)",
            "throw new ObjectDisposedException(nameof(CodeAltaUpdateService));",
            "if (startRequested)",
            "return;",
            "startRequested = true;",
            "cancellationTokenSource = new CancellationTokenSource();",
            "var token = cancellationTokenSource.Token;",
            "publishChecking();",
            "checkTask = startCheck(token);");
        RequireOnce(admission, """
                    if (stopRequested)
                    {
                        throw new ObjectDisposedException(nameof(CodeAltaUpdateService));
                    }

                    if (startRequested)
                    {
                        return;
                    }
            """);
        RequireOnce(source, "new CancellationTokenSource(");
        Reject(admission, "Task.Run(", "CheckAsync(", "await ", "catch (", "??", "CreateLinkedTokenSource", "IsCancellationRequested");
        Reject(source,
            "_cancellationTokenSource.Token", "_cancellationTokenSource = null;", "cancellationTokenSource = null;",
            "_checkTask = null;", "checkTask = null;", "_startRequested = false;", "startRequested = false;",
            "_stopRequested = false;", "stopRequested = false;", "CreateLinkedTokenSource", "ThrowIfCancellationRequested(");

        // Preserve requested-token OCE handling, including late success and failure publication.
        // This is deliberately not Deferred's stricter startup-cancellation classification.
        RequireOnce(source, "private async Task CheckAsync(CancellationToken cancellationToken)");
        RequireOnce(source, """
                private async Task CheckAsync(CancellationToken cancellationToken)
                {
                    try
                    {
                        var result = await CodeAltaUpdateChecker.CheckCurrentAssemblyAsync(cancellationToken: cancellationToken);
                        SetSnapshot(new CodeAltaUpdateCheckSnapshot(
                            result.HasNewerVersion
                                ? CodeAltaUpdateCheckStatus.UpdateAvailable
                                : result.PackageFound
                                    ? CodeAltaUpdateCheckStatus.Latest
                                    : CodeAltaUpdateCheckStatus.PackageNotFound,
                            result.PackageId,
                            result.CurrentVersionText,
                            result.LatestVersionText,
                            result.LatestVersion?.IsPrerelease ?? false,
                            result.IncludePrerelease,
                            ErrorMessage: null));
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                    }
                    catch (Exception ex)
                    {
                        SetSnapshot(Snapshot with
                        {
                            Status = CodeAltaUpdateCheckStatus.Failed,
                            ErrorMessage = ex.Message,
                        });
                    }
                }
            """);
        RequireOnce(source, "private readonly object _gate = new();");
        RequireOnce(source, "private readonly State<int> _uiRefreshVersion = new(0);");
        RequireOnce(source, "private CodeAltaUpdateCheckSnapshot _snapshot = CodeAltaUpdateCheckSnapshot.CreateNotStarted();");
        RequireOnce(source, "private int _generation;");
        RequireOnce(source, "private int _observedGeneration;");
        RequireOnce(source, "public State<int> UiRefreshVersion => _uiRefreshVersion;");
        RequireOnce(source, """
                public CodeAltaUpdateCheckSnapshot Snapshot
                {
                    get
                    {
                        lock (_gate)
                        {
                            return _snapshot;
                        }
                    }
                }
            """);
        RequireOnce(source, """
                public void SynchronizeUiState()
                {
                    var generation = Volatile.Read(ref _generation);
                    if (generation == _observedGeneration)
                    {
                        return;
                    }

                    _observedGeneration = generation;
                    _uiRefreshVersion.Value++;
                }
            """);
        RequireOnce(source, """
                private void SetSnapshot(CodeAltaUpdateCheckSnapshot snapshot)
                {
                    lock (_gate)
                    {
                        _snapshot = snapshot;
                        _generation++;
                    }
                }
            """);
        RequireOnce(source, "_uiRefreshVersion.Value++;");
        RequireOnce(source, "_generation++;");
    }

    [TestMethod]
    public void UpdateDisposal_SourceWiring_JoinsBeforeSourceRelease()
    {
        var source = ReadSource("CodeAlta.Tui/Views/CodeAltaUpdateService.cs");
        RequireOnce(source, "internal sealed class CodeAltaUpdateService : IAsyncDisposable");

        RequireOnce(source, "private readonly Lazy<Task> _disposeTask;");
        RequireOnce(source, "public ValueTask DisposeAsync() => new(_disposeTask.Value);");
        var constructor = Scope(source, "    public CodeAltaUpdateService()", "\n    }\n");
        RequireOnce(constructor, """
                    _disposeTask = CreateUpdateDisposal(
                        stopCheck: () => _stopRequested = true,
                        disposeCore: DisposeCoreAsync);
            """);
        RequireOnce(source, "_stopRequested = true");
        var factory = Scope(source, "    internal static Lazy<Task> CreateUpdateDisposal(", "\n    }\n");
        RequireOnce(factory, """
                internal static Lazy<Task> CreateUpdateDisposal(
                    Action stopCheck,
                    Func<Task> disposeCore)
            """);
        RequireOrdered(factory,
            "ArgumentNullException.ThrowIfNull(stopCheck);",
            "ArgumentNullException.ThrowIfNull(disposeCore);",
            "return new Lazy<Task>(async () =>",
            "stopCheck();",
            "await disposeCore();");
        RequireOnce(factory, """
                    return new Lazy<Task>(async () =>
                    {
                        stopCheck();
                        await disposeCore();
                    });
            """);

        var snapshot = Scope(source, "    private Task DisposeCoreAsync()", "\n    }\n");
        RequireOrdered(snapshot,
            "var checkTask = _checkTask;",
            "var cancellation = _cancellationTokenSource;",
            "return DisposeUpdateCheckAsync(");
        RequireOnce(snapshot, """
                    return DisposeUpdateCheckAsync(
                        checkTask,
                        cancelCheck: () => cancellation?.Cancel(),
                        disposeCancellation: () => cancellation?.Dispose());
            """);
        Reject(snapshot, "await ", ".Token", "_cancellationTokenSource?.Cancel()", "_cancellationTokenSource?.Dispose()");
        RequireOnce(source, "cancellation?.Cancel()");
        RequireOnce(source, "cancellation?.Dispose()");

        var core = Scope(source, "    internal static Task DisposeUpdateCheckAsync(", "\n    }\n");
        RequireOnce(core, """
                internal static Task DisposeUpdateCheckAsync(
                    Task? checkTask,
                    Action cancelCheck,
                    Action disposeCancellation)
            """);
        RequireOrdered(core,
            "ArgumentNullException.ThrowIfNull(cancelCheck);",
            "ArgumentNullException.ThrowIfNull(disposeCancellation);",
            "return CoreAsync();",
            "async Task CoreAsync()",
            "List<Exception>? failures = null;",
            "cancelCheck();",
            "if (checkTask is not null)",
            "await checkTask;",
            "disposeCancellation();",
            "ExceptionDispatchInfo.Throw(failures[0]);",
            "throw new AggregateException(failures);");
        RequireOnce(core, """
                        try
                        {
                            cancelCheck();
                        }
                        catch (Exception ex)
                        {
                            (failures ??= []).Add(ex);
                        }
            """);
        RequireOnce(core, """
                        if (checkTask is not null)
                        {
                            try
                            {
                                await checkTask;
                            }
                            catch (Exception ex)
                            {
                                (failures ??= []).Add(ex);
                            }
                        }
            """);
        RequireOnce(core, """
                        try
                        {
                            disposeCancellation();
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
            """);
        // All escaped check failures are cleanup failures; normal requested cancellation is
        // already handled inside CheckAsync. No reported-failure or cancellation suppression here.
        Reject(core, "when (", "OperationCanceledException", "IsCanceled", "IsCompleted", "ReferenceEquals(", "CodeAltaUpdateChecker.", "Task.Run(");
        Reject(source,
            ": IDisposable", "public void Dispose()", "public async ValueTask DisposeAsync()",
            "IsValueCreated", "ConfigureAwait(false)", ".WaitAsync(", ".Wait(", ".Result",
            "GetAwaiter().GetResult()", "Task.WhenAny(", "Task.Delay(", "Task.Factory",
            ".Flatten(", "IsExpectedDeferredStartupCancellation(");
    }

    [TestMethod]
    public void UpdateCaller_SourceWiring_AwaitsOwnedStageAndPreservesPresentation()
    {
        var deferred = ReadSource("CodeAlta.Tui/Views/DeferredCodeAltaApp.cs");
        var snapshot = Scope(deferred, "    private Task DisposeCoreAsync()", "\n    }\n");
        RequireOnce(snapshot, "disposeUpdate: _updateService.DisposeAsync,");

        RequireOnce(deferred, "private readonly CodeAltaUpdateService _updateService = new();");
        RequireOnce(deferred, "public CodeAltaUpdateCheckSnapshot UpdateCheckSnapshot => _updateService.Snapshot;");
        RequireOnce(deferred, "_updateService.Start();");
        RequireOnce(deferred, "_updateService.DisposeAsync");
        var constructor = Scope(deferred, "    public DeferredCodeAltaApp(", "\n    }\n");
        RequireOrdered(constructor, "_disposeTask = CreateDeferredDisposal(", "disposeCore: DisposeCoreAsync);", "_updateService.Start();");
        RequireOnce(deferred, "public ValueTask DisposeAsync() => new(_disposeTask.Value);");
        RequireOnce(snapshot, """
                    return DisposeDeferredStartupAsync(
                        app,
                        startupTask,
                        reportedStartupFailure,
                        startupToken,
                        cancelStartup: () => startupCancellation?.Cancel(),
                        disposeUpdate: _updateService.DisposeAsync,
                        disposePresenter: _graphicsPresenter.Dispose,
                        disposeStartupCancellation: () => startupCancellation?.Dispose());
            """);
        var core = Scope(deferred, "    internal static Task DisposeDeferredStartupAsync<TServices>(", "\n    }\n");
        RequireOnce(core, """
                internal static Task DisposeDeferredStartupAsync<TServices>(
                    IAsyncDisposable? app,
                    Task<TServices>? startupTask,
                    Exception? reportedStartupFailure,
                    CancellationToken startupToken,
                    Action cancelStartup,
                    Func<ValueTask> disposeUpdate,
                    Action disposePresenter,
                    Action disposeStartupCancellation)
                    where TServices : class, IAsyncDisposable
            """);
        RequireOrdered(core,
            "ArgumentNullException.ThrowIfNull(cancelStartup);",
            "ArgumentNullException.ThrowIfNull(disposeUpdate);",
            "ArgumentNullException.ThrowIfNull(disposePresenter);",
            "ArgumentNullException.ThrowIfNull(disposeStartupCancellation);",
            "return CoreAsync();",
            "cancelStartup();",
            "if (app is not null)",
            "await app.DisposeAsync();",
            "else if (startupTask is not null)",
            "returnedServices = await startupTask;",
            "ReferenceEquals(ex, reportedStartupFailure)",
            "IsExpectedDeferredStartupCancellation(",
            "await returnedServices.DisposeAsync();",
            "await disposeUpdate();",
            "disposePresenter();",
            "disposeStartupCancellation();",
            "ExceptionDispatchInfo.Throw(failures[0]);",
            "throw new AggregateException(failures);");
        RequireOnce(core, """
                        try
                        {
                            await disposeUpdate();
                        }
                        catch (Exception ex)
                        {
                            (failures ??= []).Add(ex);
                        }
            """);
        Reject(deferred, "_updateService.Dispose(", "Action disposeUpdate", "ConfigureAwait(false)", ".WaitAsync(", "Task.Run(");
        Reject(core, "GetAwaiter().GetResult()", ".Wait(", ".Result", ".Flatten(");
        // Same stage, not early cancellation: an earlier noncompleting owner can prevent reaching it.
        var iteration = Scope(deferred, "    private TerminalLoopResult OnIteration(CancellationToken cancellationToken)", "\n    }\n");
        RequireOnce(iteration, """
                        var ownedServices = startupTask.GetAwaiter().GetResult();
                        _app = CodeAltaApp.Create(ownedServices, _updateService);
                        _app.PrepareForRun();
                        _toastHost.Content = _app.GetRoot();
            """);
        RequireOnce(iteration, "        SyncUpdateNotifications();\n        return _app.Tick(cancellationToken);");
        Reject(iteration, "await ", "DisposeAsync(");
        var notifications = Scope(deferred, "    private void SyncUpdateNotifications()", "\n    }\n");
        RequireOrdered(notifications,
            "_updateService.SynchronizeUiState();",
            "var snapshot = _updateService.Snapshot;",
            "if (_updateToastShown || !snapshot.HasNewerVersion)",
            "return;",
            "_updateToastShown = true;",
            "ToastService.Show(() => new Toast",
            "Content = CodeAltaUpdateVisualFactory.CreateToastContent(snapshot, CopyUpdateCommand),");

        var app = ReadSource("CodeAlta.Tui/App/CodeAltaApp.cs");
        var createApp = Scope(app, "    internal static CodeAltaApp Create(", "\n    }\n");
        RequireOnce(createApp, "CodeAltaUpdateService? updateService = null)");
        RequireOnce(createApp, "            ownedServices,\n            updateService);");
        var appConstructor = Scope(app, "    private CodeAltaApp(IReadOnlyList<ModelProviderDescriptor> providerDescriptors,", "\n    }\n");
        RequireOnce(appConstructor, "CodeAltaUpdateService? updateService = null)");
        RequireOnce(appConstructor, "() => new AboutDialog(() => DialogBoundsResolver.ResolveAppBounds(GetDialogAnchor()), GetDialogAnchor, _shellAnimationRuntime.WelcomePhase01, updateService).Show()");
        RequireOnce(app, "public async ValueTask DisposeAsync()\n        => await _frontendHost.DisposeAsync();");
        Reject(app, "new CodeAltaUpdateService(", "updateService.Dispose", "updateService?.Dispose", "updateService.Start(", "updateService?.Start(");

        var about = ReadSource("CodeAlta.Tui/Views/AboutDialog.cs");
        var aboutConstructor = Scope(about, "    public AboutDialog(", "\n    }\n");
        RequireOnce(aboutConstructor, "CodeAltaUpdateService? updateService = null)");
        RequireOrdered(aboutConstructor,
            "_getUpdateSnapshot = () => updateService?.Snapshot ?? CodeAltaUpdateCheckSnapshot.CreateNotStarted();",
            "_updateStatusVersion = updateService?.UiRefreshVersion;",
            "_dialog = BuildDialog();");
        RequireOnce(about, """
                private Visual BuildUpdateStatusVisual()
                {
                    _ = _updateStatusVersion?.Value;
                    var snapshot = _getUpdateSnapshot();
                    if (_lastUpdateStatusVisual is not null && snapshot == _lastUpdateStatusSnapshot)
                    {
                        return _lastUpdateStatusVisual;
                    }

                    _lastUpdateStatusSnapshot = snapshot;
                    _lastUpdateStatusVisual = CodeAltaUpdateVisualFactory.CreateAboutUpdateStatus(snapshot, CopyUpdateCommand);
                    return _lastUpdateStatusVisual;
                }
            """);
        Reject(about, "new CodeAltaUpdateService(", "updateService.Dispose", "updateService?.Dispose", "updateService.Start(", "updateService?.Start(");

        var program = ReadSource("CodeAlta.Tui/Program.cs");
        CodeAltaStartupAdmissionSourceTests.RequireProgramAdmission(program);
        var run = Scope(program, "    internal static async ValueTask<int> RunAsync(", "\n    }\n");
        RequireOrdered(run,
            "var cancellationTokenSource = new CancellationTokenSource();",
            "await using var app = new DeferredCodeAltaApp(prestartedPluginRuntime);",
            "Program.ThrowIfCurrentThreadIsNotMainThread(mainThreadId);",
            "await app.RunAsync(cancellationTokenSource.Token);",
            "PrintUpdateAvailableMessage(app.UpdateCheckSnapshot);",
            "return 0;");
        Reject(run, "app.DisposeAsync(", "cancellationTokenSource.Cancel(", "cancellationTokenSource.Dispose(", "new CodeAltaUpdateService(");
        RequireOnce(program, """
                private static void PrintUpdateAvailableMessage(CodeAltaUpdateCheckSnapshot snapshot)
                {
                    if (!snapshot.HasNewerVersion)
                    {
                        return;
                    }

                    Terminal.WriteLine($"A new version {snapshot.LatestVersionText} of CodeAlta is available!");
                    Terminal.WriteLine($"To update: {snapshot.UpdateCommand}");
                }
            """);

        // Lexical forwarding/ownership only: HTTP timeout is not a whole-check or shutdown bound.
        // No network, arbitrary-callback, transport or complete lifetime qualification is implied.
        var checker = ReadSource("CodeAlta.Tui/Views/CodeAltaUpdateChecker.cs");
        var currentAssembly = Scope(checker, "    public static async Task<CodeAltaNuGetUpdateCheckResult> CheckCurrentAssemblyAsync(", "\n    }\n");
        RequireOrdered(currentAssembly,
            "CancellationToken cancellationToken = default)",
            "var currentVersion = GetCurrentAssemblyNuGetVersion(assembly);",
            "var effectiveIncludePrerelease = includePrerelease ?? currentVersion.IsPrerelease;",
            "return await CheckNuGetOrgAsync(PackageId, currentVersion, effectiveIncludePrerelease, cancellationToken);");
        var ownHttp = Scope(checker, "    public static async Task<CodeAltaNuGetUpdateCheckResult> CheckNuGetOrgAsync(", "\n    }\n");
        RequireOrdered(ownHttp,
            "ArgumentException.ThrowIfNullOrWhiteSpace(packageId);",
            "ArgumentNullException.ThrowIfNull(currentVersion);",
            "using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };",
            "return await CheckNuGetOrgAsync(packageId, currentVersion, includePrerelease, httpClient, cancellationToken);");
        var check = Scope(checker, "    internal static async Task<CodeAltaNuGetUpdateCheckResult> CheckNuGetOrgAsync(", "\n    }\n");
        RequireOrdered(check,
            "ArgumentException.ThrowIfNullOrWhiteSpace(packageId);",
            "ArgumentNullException.ThrowIfNull(currentVersion);",
            "ArgumentNullException.ThrowIfNull(httpClient);",
            "var listedVersions = await GetListedVersionsAsync(httpClient, packageId, cancellationToken);",
            "if (listedVersions.Length == 0)",
            "return new CodeAltaNuGetUpdateCheckResult(packageId, currentVersion, null, PackageFound: false, HasNewerVersion: false, includePrerelease);",
            "var latestVersion = listedVersions",
            ".Where(version => includePrerelease || !version.IsPrerelease)",
            ".OrderByDescending(static version => version, VersionComparer.VersionRelease)",
            ".FirstOrDefault();",
            "if (latestVersion is null)",
            "return new CodeAltaNuGetUpdateCheckResult(packageId, currentVersion, null, PackageFound: true, HasNewerVersion: false, includePrerelease);",
            "var hasNewerVersion = VersionComparer.VersionRelease.Compare(latestVersion, currentVersion) > 0;",
            "return new CodeAltaNuGetUpdateCheckResult(packageId, currentVersion, latestVersion, PackageFound: true, hasNewerVersion, includePrerelease);");
        Reject(check, "httpClient.Dispose(", "using var httpClient");
        var index = Scope(checker, "    private static async Task<NuGetVersion[]> GetListedVersionsAsync(", "\n    }\n");
        RequireOrdered(index,
            "using var response = await httpClient.GetAsync(registrationUri, cancellationToken);",
            "if (response.StatusCode == System.Net.HttpStatusCode.NotFound)",
            "return [];",
            "response.EnsureSuccessStatusCode();",
            "using var document = await ReadJsonDocumentAsync(response, cancellationToken);",
            "await AddListedVersionsAsync(httpClient, document.RootElement, versions, cancellationToken);",
            "return versions.ToArray();");
        var pages = Scope(checker, "    private static async Task AddListedVersionsAsync(", "\n    }\n");
        RequireOrdered(pages,
            "foreach (var item in items.EnumerateArray())",
            "AddListedVersions(inlineItems, versions);",
            "using var response = await httpClient.GetAsync(pageUri, cancellationToken);",
            "response.EnsureSuccessStatusCode();",
            "using var pageDocument = await ReadJsonDocumentAsync(response, cancellationToken);",
            "AddListedVersions(pageItems, versions);");
        RequireOnce(checker, """
                private static async Task<JsonDocument> ReadJsonDocumentAsync(HttpResponseMessage response, CancellationToken cancellationToken)
                {
                    await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                    using var decodedStream = CreateDecodedStream(stream, response.Content.Headers.ContentEncoding);
                    return await JsonDocument.ParseAsync(decodedStream, cancellationToken: cancellationToken);
                }
            """);
    }

    // These six literal call-site paths are the complete checkout-content read inventory.
    // No upward discovery, profile access, localization or logging setup is performed.
    // Named source reads and existing writerless assembly logging are not zero I/O.
    private static string ReadSource(string relativePath)
        => SourceTestText.DecodeSource(File.ReadAllBytes(Path.Combine(SourceRoot(), relativePath)));

    private static string SourceRoot([CallerFilePath] string sourceFile = "")
        => Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(sourceFile) ?? throw new AssertFailedException("Missing fixture source directory."), ".."));

    private static string Scope(string source, string startAnchor, string endAnchor)
    {
        source = SourceTestText.Canonicalize(source);
        startAnchor = SourceTestText.Canonicalize(startAnchor);
        endAnchor = SourceTestText.Canonicalize(endAnchor);
        var start = source.IndexOf(startAnchor, StringComparison.Ordinal);
        Assert.IsTrue(start >= 0, $"Missing source anchor: {startAnchor}");
        var end = source.IndexOf(endAnchor, start + startAnchor.Length, StringComparison.Ordinal);
        Assert.IsTrue(end > start, $"Missing following source anchor: {endAnchor}");
        return source[start..end];
    }

    private static void RequireOnce(string source, string expected)
    {
        source = SourceTestText.Canonicalize(source);
        expected = SourceTestText.Canonicalize(expected);
        var first = source.IndexOf(expected, StringComparison.Ordinal);
        Assert.IsTrue(first >= 0, $"Missing expected wiring: {expected}");
        Assert.AreEqual(first, source.LastIndexOf(expected, StringComparison.Ordinal), $"Duplicate wiring: {expected}");
    }

    private static void RequireOrdered(string source, params string[] expected)
    {
        source = SourceTestText.Canonicalize(source);
        expected = Array.ConvertAll(expected, SourceTestText.Canonicalize);
        var previous = -1;
        foreach (var item in expected)
        {
            RequireOnce(source, item);
            var current = source.IndexOf(item, StringComparison.Ordinal);
            Assert.IsTrue(current > previous, $"Out-of-order wiring: {item}");
            previous = current;
        }
    }

    private static void Reject(string source, params string[] forbidden)
    {
        source = SourceTestText.Canonicalize(source);
        forbidden = Array.ConvertAll(forbidden, SourceTestText.Canonicalize);
        foreach (var item in forbidden)
        {
            Assert.IsFalse(source.Contains(item, StringComparison.Ordinal), $"Unexpected wiring: {item}");
        }
    }
}
