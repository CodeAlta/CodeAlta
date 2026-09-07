using System.Runtime.CompilerServices;

namespace CodeAlta.Tests;

[TestClass]
public sealed class CodeAltaFrontendCleanupSourceTests
{
    [TestMethod]
    public void FrontendCleanup_SourceWiring_UsesMandatoryOrderedCore()
    {
        var app = ReadSource("CodeAlta.Tui/App/CodeAltaApp.cs");
        RequireOnce(app, "await ShellFrontendHost.DisposeFrontendResourcesAsync(");

        // This is the complete ten-line adapter, not additional async wrappers or result policy.
        RequireOnce(app, """
                async ValueTask IShellFrontendHostLifecycle.DisposeFrontendAsync()
                {
                    await ShellFrontendHost.DisposeFrontendResourcesAsync(
                        _projectionCoordinator.Dispose, _reminderUiCoordinator.Dispose,
                        () => _sessionStateCoordinator.PersistViewStateAsync(reportStatus: false),
                        _fileEditorWorkspaceCoordinator.DisposeAsync,
                        _runtimeEventPump.DisposeAsync,
                        _shellController.DisposeAsync,
                        _promptDraftUiCoordinator.DisposeAsync);
                }
            """);
        var frontend = Scope(app, "    async ValueTask IShellFrontendHostLifecycle.DisposeFrontendAsync()", "\n    }\n");
        RequireOrdered(frontend,
            "await ShellFrontendHost.DisposeFrontendResourcesAsync(",
            "_projectionCoordinator.Dispose, _reminderUiCoordinator.Dispose,",
            "() => _sessionStateCoordinator.PersistViewStateAsync(reportStatus: false),",
            "_fileEditorWorkspaceCoordinator.DisposeAsync,",
            "_runtimeEventPump.DisposeAsync,",
            "_shellController.DisposeAsync,",
            "_promptDraftUiCoordinator.DisposeAsync);");
        Reject(frontend,
            "_projectionCoordinator.Dispose();", "_reminderUiCoordinator.Dispose();",
            "await _sessionStateCoordinator.PersistViewStateAsync(reportStatus: false);",
            "await _fileEditorWorkspaceCoordinator.DisposeAsync();", "await _runtimeEventPump.DisposeAsync();",
            "await _shellController.DisposeAsync();", "await _promptDraftUiCoordinator.DisposeAsync();",
            "async ()", "ThrowIfFailed", "PersistenceResult", "IsConflict", "Succeeded", "reportStatus: true",
            "ConfigureAwait(", "GetAwaiter().GetResult()", ".Result", ".Wait(", ".WaitAsync(",
            "Task.Run(", "Task.Factory", "Task.WhenAny(", "Task.WhenAll(", "Task.Delay(", "ContinueWith(",
            "new ", "_ownedServices", "_updateService", "try", "catch", "throw");
    }

    [TestMethod]
    public void FrontendCleanup_SourceCore_ContainsEveryStageFailure()
    {
        var shell = ReadSource("CodeAlta.Tui/App/ShellFrontendHost.cs");
        RequireOnce(shell, "    internal static Task DisposeFrontendResourcesAsync(");
        var core = Scope(shell, "    internal static Task DisposeFrontendResourcesAsync(", "\n    }\n");
        RequireOnce(core, """
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
            """);
        RequireOrdered(core,
            "ArgumentNullException.ThrowIfNull(disposeProjection);",
            "ArgumentNullException.ThrowIfNull(disposeReminderUi);",
            "ArgumentNullException.ThrowIfNull(persistViewState);",
            "ArgumentNullException.ThrowIfNull(disposeFileEditors);",
            "ArgumentNullException.ThrowIfNull(disposeRuntimeEventPump);",
            "ArgumentNullException.ThrowIfNull(disposeShellController);",
            "ArgumentNullException.ThrowIfNull(disposePromptDrafts);",
            "return CoreAsync();",
            "async Task CoreAsync()",
            "List<Exception>? failures = null;",
            "disposeProjection();",
            "disposeReminderUi();",
            "await persistViewState();",
            "await disposeFileEditors();",
            "await disposeRuntimeEventPump();",
            "await disposeShellController();",
            "await disposePromptDrafts();",
            "ExceptionDispatchInfo.Throw(failures[0]);",
            "throw new AggregateException(failures);");

        // Each operation has its own catch, so one escaped failure cannot skip a later sibling.
        RequireOnce(core, """
                        try
                        {
                            disposeProjection();
                        }
                        catch (Exception ex)
                        {
                            (failures ??= []).Add(ex);
                        }
            """);
        RequireOnce(core, """
                        try
                        {
                            disposeReminderUi();
                        }
                        catch (Exception ex)
                        {
                            (failures ??= []).Add(ex);
                        }
            """);
        RequireOnce(core, """
                        try
                        {
                            await persistViewState();
                        }
                        catch (Exception ex)
                        {
                            (failures ??= []).Add(ex);
                        }
            """);
        RequireOnce(core, """
                        try
                        {
                            await disposeFileEditors();
                        }
                        catch (Exception ex)
                        {
                            (failures ??= []).Add(ex);
                        }
            """);
        RequireOnce(core, """
                        try
                        {
                            await disposeRuntimeEventPump();
                        }
                        catch (Exception ex)
                        {
                            (failures ??= []).Add(ex);
                        }
            """);
        RequireOnce(core, """
                        try
                        {
                            await disposeShellController();
                        }
                        catch (Exception ex)
                        {
                            (failures ??= []).Add(ex);
                        }
            """);
        RequireOnce(core, """
                        try
                        {
                            await disposePromptDrafts();
                        }
                        catch (Exception ex)
                        {
                            (failures ??= []).Add(ex);
                        }
            """);
        RequireOnce(core, """
                        if (failures is { Count: 1 })
                        {
                            ExceptionDispatchInfo.Throw(failures[0]);
                        }

                        if (failures is { Count: > 1 })
                        {
                            throw new AggregateException(failures);
                        }
            """);
        RequireOnce(core, "DisposeFrontendResourcesAsync(");
        Reject(core,
            "ConfigureAwait(", "GetAwaiter().GetResult()", ".Result", ".Wait(", ".WaitAsync(",
            "Task.Run(", "Task.Factory", "Task.WhenAny(", "Task.WhenAll(", "Task.Delay(", "Task.Yield(", "ContinueWith(",
            "when (", "OperationCanceledException", "CancellationToken", "IsCanceled", "IsCompleted", "ReferenceEquals(",
            ".Flatten(", ".InnerExceptions", "GetBaseException(", "failures.Clear(", "failures.Remove", "throw ex;",
            "ThrowIfFailed", "PersistenceResult", "IsConflict", "Succeeded", "return;", "finally", "while (", "for (", "foreach (",
            "lock (", "Lazy<", "Interlocked.", "SemaphoreSlim", "IAsyncDisposable", "new CodeAlta", "Terminal.", "UiLogger", "LogManager");
        // The adapter remains a lifecycle wrapper, not a cached or replacement ownership graph.
        Reject(shell, "Lazy<", "_disposeTask", "_disposed", "_disposeRequested", "SemaphoreSlim", "Interlocked.");
    }

    [TestMethod]
    public void FrontendOwnership_SourceWiring_PreservesShellAndDeferredBoundaries()
    {
        var app = ReadSource("CodeAlta.Tui/App/CodeAltaApp.cs");
        RequireOnce(app, "public async ValueTask DisposeAsync()\n        => await _frontendHost.DisposeAsync();");
        RequireOnce(app, "IAsyncDisposable? IShellFrontendHostLifecycle.OwnedServices => _ownedServices;");
        var constructor = Scope(app, "    private CodeAltaApp(IReadOnlyList<ModelProviderDescriptor> providerDescriptors,", "\n    }\n");
        RequireOrdered(constructor,
            "_ownedServices = ownedServices;",
            "_frontendHost = new ShellFrontendHost(this);",
            "var composition = CodeAltaFrontendComposition.Create(");

        var shell = ReadSource("CodeAlta.Tui/App/ShellFrontendHost.cs");
        // Preserve the entire existing traversal, including late owned-services lookup and errors.
        RequireOnce(shell, """
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
            """);
        RequireOnce(shell, """
                public async ValueTask DisposeAsync()
                    => await DisposeRemindersThenFrontendAsync(
                        () => _reminders?.DisposeAsync() ?? ValueTask.CompletedTask,
                        DisposeFrontendAndOwnedServicesAsync);
            """);

        var shellCleanup = Scope(
            shell,
            "    private async ValueTask DisposeFrontendAndOwnedServicesAsync()",
            "\n    }\n");
        Reject(shellCleanup,
            "ConfigureAwait(", ".Flatten(", "Task.Run(", "Task.WhenAny(",
            ".Wait(", ".WaitAsync(", "GetAwaiter().GetResult()");

        var reminderCleanup = Scope(
            shell,
            "    internal static Task DisposeRemindersThenFrontendAsync(",
            "\n    }\n");
        Reject(reminderCleanup,
            "ConfigureAwait(", ".Flatten(", "Task.Run(", "Task.WhenAny(",
            ".Wait(", ".WaitAsync(", "GetAwaiter().GetResult()");

        var deferred = ReadSource("CodeAlta.Tui/Views/DeferredCodeAltaApp.cs");
        var iteration = Scope(deferred, "    private TerminalLoopResult OnIteration(", "\n    }\n");
        RequireOnce(iteration, """
                        var ownedServices = startupTask.GetAwaiter().GetResult();
                        _app = CodeAltaApp.Create(ownedServices, _updateService);
                        _app.PrepareForRun();
                        _toastHost.Content = _app.GetRoot();
            """);
        RequireOnce(iteration, """
                    if (_app is not null)
                    {
                        SyncUpdateNotifications();
                        return _app.Tick(cancellationToken);
                    }
            """);
        Reject(iteration, "_app = null", "await ", "ConfigureAwait(", "Task.Run(");
        var snapshot = Scope(deferred, "    private Task DisposeCoreAsync()", "\n    }\n");
        RequireOrdered(snapshot,
            "var app = _app;",
            "var startupTask = _ownedServicesTask;",
            "var reportedStartupFailure = _startupFailure;",
            "var startupToken = _startupToken;",
            "var startupCancellation = _startupCancellation;",
            "return DisposeDeferredStartupAsync(");
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
        var cleanup = Scope(deferred, "    internal static Task DisposeDeferredStartupAsync<TServices>(", "\n    }\n");
        RequireOnce(cleanup, """
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
        RequireOrdered(cleanup,
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
            "await returnedServices.DisposeAsync();",
            "await disposeUpdate();",
            "disposePresenter();",
            "disposeStartupCancellation();",
            "ExceptionDispatchInfo.Throw(failures[0]);",
            "throw new AggregateException(failures);");
        RequireOnce(cleanup, """
                        if (app is not null)
                        {
                            // No pending wait precedes frontend disposal in this branch.
                            try
                            {
                                await app.DisposeAsync();
                            }
                            catch (Exception ex)
                            {
                                (failures ??= []).Add(ex);
                            }
                        }
                        else if (startupTask is not null)
                        {
                            TServices? returnedServices = null;
                            try
                            {
                                returnedServices = await startupTask;
                            }
                            catch (Exception ex)
                            {
                                var requestedAtObservation = startupWasCompleted
                                    ? cancellationWasRequested
                                    : startupToken.IsCancellationRequested;
                                if (!ReferenceEquals(ex, reportedStartupFailure) &&
                                    !IsExpectedDeferredStartupCancellation(
                                        startupTask, ex, startupToken, requestedAtObservation))
                                {
                                    (failures ??= []).Add(ex);
                                }
                            }

                            if (returnedServices is not null)
                            {
                                try
                                {
                                    await returnedServices.DisposeAsync();
                                }
                                catch (Exception ex)
                                {
                                    (failures ??= []).Add(ex);
                                }
                            }
                        }
            """);
        RequireOnce(cleanup, """
                        try
                        {
                            await disposeUpdate();
                        }
                        catch (Exception ex)
                        {
                            (failures ??= []).Add(ex);
                        }

                        try
                        {
                            disposePresenter();
                        }
                        catch (Exception ex)
                        {
                            (failures ??= []).Add(ex);
                        }

                        try
                        {
                            disposeStartupCancellation();
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
        Reject(cleanup,
            "ConfigureAwait(", ".Flatten(", "Task.Run(", "Task.WhenAny(", "Task.WhenAll(",
            ".Wait(", ".WaitAsync(", "GetAwaiter().GetResult()", "startupTask =", "app = null", "Task.Delay(");
        // These are named ownership/wiring checks, not construction rollback or UI termination proof.
        // Hidden acquisitions, partial surfaces, publication failures and lower-owner lifetime remain open.
    }

    // Five literal reads across exactly three named checkout files; no discovery or runtime setup.
    // Existing writerless assembly logging and source reads are nonzero I/O, not startup qualification.
    private static string ReadSource(string relativePath)
        => File.ReadAllText(Path.Combine(SourceRoot(), relativePath))
            .Replace("\r\n", "\n", StringComparison.Ordinal);

    private static string SourceRoot([CallerFilePath] string sourceFile = "")
        => Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(sourceFile) ?? throw new AssertFailedException("Missing fixture source directory."), ".."));

    private static string Scope(string source, string startAnchor, string endAnchor)
    {
        var start = source.IndexOf(startAnchor, StringComparison.Ordinal);
        Assert.IsTrue(start >= 0, $"Missing source anchor: {startAnchor}");
        var end = source.IndexOf(endAnchor, start + startAnchor.Length, StringComparison.Ordinal);
        Assert.IsTrue(end > start, $"Missing following source anchor: {endAnchor}");
        return source[start..end];
    }

    private static void RequireOnce(string source, string expected)
    {
        var first = source.IndexOf(expected, StringComparison.Ordinal);
        Assert.IsTrue(first >= 0, $"Missing expected wiring: {expected}");
        Assert.AreEqual(first, source.LastIndexOf(expected, StringComparison.Ordinal), $"Duplicate wiring: {expected}");
    }

    private static void RequireOrdered(string source, params string[] expected)
    {
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
        foreach (var item in forbidden)
        {
            Assert.IsFalse(source.Contains(item, StringComparison.Ordinal), $"Unexpected wiring: {item}");
        }
    }
}
