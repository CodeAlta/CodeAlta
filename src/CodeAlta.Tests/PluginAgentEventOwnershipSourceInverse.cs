namespace CodeAlta.Tests;

/// <summary>Literal-only restoration of the bounded event prerequisite to complete fb18b27e inputs; no source reads.</summary>
internal static class PluginAgentEventOwnershipSourceInverse
{
    private const string Lifecycle = "CodeAlta.Plugins/PluginRuntimeLifecycle.cs";
    private const string Adapter = "CodeAlta.Plugins/PluginContributionAdapters.cs";
    private const string Manager = "CodeAlta.Plugins/PluginRuntimeManager.cs";
    private const string Tasks = "CodeAlta.Plugins/PluginTaskTracking.cs";
    private const string Host = "CodeAlta.Orchestration/Hosting/CodeAltaHost.cs";
    private const string Owned = "CodeAlta.Tui/App/CodeAltaOwnedServices.cs";
    private const string Shell = "CodeAlta.Tui/App/ShellFrontendHost.cs";
    private const string Project = "CodeAlta.Plugins/CodeAlta.Plugins.csproj";
    private const string CacheInverse = "CodeAlta.Tests/RuntimeFileSearchInvalidationSourceInverse.cs";
    private const string OwnerInverse = "CodeAlta.Tests/OwnedSessionCommandSourceInverse.cs";
    private const string ProfileInverse = "CodeAlta.Tests/PluginAuthoringProfileSourceInverse.cs";
    private const string GitHubInverse = "CodeAlta.Tests/PluginGitHubBackendSeparationSourceInverse.cs";
    private const string McpInverse = "CodeAlta.Tests/PluginMcpBackendSeparationSourceInverse.cs";
    private const string FrontendGuard = "CodeAlta.Tests/CodeAltaFrontendCleanupSourceTests.cs";
    private const string WorkspaceGuard = "CodeAlta.Tests/FileEditorWorkspaceCleanupSourceTests.cs";
    private const string DeferredGuard = "CodeAlta.Tests/DeferredCodeAltaAppSourceTests.cs";
    private const string PromptGuard = "CodeAlta.Tests/PromptDraftPrerequisiteSourceTests.cs";
    private const string DesktopProject = "CodeAlta.Desktop.Tests/CodeAlta.Desktop.Tests.csproj";
    internal static IReadOnlyList<string> Paths => [Lifecycle, Adapter, Manager, Tasks, Host, Owned, Shell, Project,
        CacheInverse, OwnerInverse, ProfileInverse, GitHubInverse, McpInverse,
        FrontendGuard, WorkspaceGuard, DeferredGuard, PromptGuard, DesktopProject];
    internal static string RestoreInput(string path, string source)
        => Paths.Contains(path, StringComparer.Ordinal) ? Restore(path, source) : source;

    // These maps are reader-specific: a shared production path can enter independent historical chains,
    // but must never be restored twice along one chain. Unmapped inputs retain their existing routing.
    internal static string RestoreCacheInput(string path, string source)
        => path is Host or OwnerInverse or DesktopProject ? Restore(path, source) : source;
    internal static string RestoreArchitectureInput(string path, string source)
        => path is Shell or FrontendGuard or WorkspaceGuard or DeferredGuard or PromptGuard ? Restore(path, source)
            : path == RuntimePluginLiveEventSourceInverse.Deferred ? RuntimePluginLiveEventSourceInverse.Restore(path, source) : source;
    internal static string RestoreLifetimeInput(string path, string source)
        => path is Project or Lifecycle or Adapter or Tasks or FrontendGuard or WorkspaceGuard or DeferredGuard or PromptGuard ? Restore(path, source)
            : RuntimePluginLiveEventSourceInverse.RestoreLifetimeInput(path, source);
    internal static string RestoreOwnerInput(string path, string source)
        => path is ProfileInverse ? Restore(path, source) : source;
    internal static string RestoreDiscoveryInput(string path, string source)
        => path is McpInverse ? Restore(path, source) : source;
    internal static string RestoreProfileInput(string path, string source)
        => path is Owned ? Restore(path, source)
            : path == RuntimePluginLiveEventSourceInverse.Program ? RuntimePluginLiveEventSourceInverse.Restore(path, source) : source;
    internal static string RestoreGitHubInput(string path, string source)
        => path is Lifecycle or Manager ? Restore(path, source) : source;
    internal static string RestoreMcpInput(string path, string source)
        => path is GitHubInverse ? Restore(path, source) : source;
    internal static string RestoreShellInput(string path, string source)
        => path is Shell ? Restore(path, source)
            : path == RuntimePluginLiveEventSourceInverse.Deferred ? RuntimePluginLiveEventSourceInverse.Restore(path, source) : source;
    internal static string RestoreDeferredInput(string path, string source)
        => path is Shell or Owned or Host ? Restore(path, source)
            : path == RuntimePluginLiveEventSourceInverse.Deferred ? RuntimePluginLiveEventSourceInverse.Restore(path, source) : source;
    internal static string RestorePromptInput(string path, string source)
        => path is Shell or FrontendGuard ? Restore(path, source) : source;

    internal static string Restore(string path, string source)
    {
        source = RuntimePluginLiveEventSourceInverse.RestoreInput(path, source);
        source = SourceTestText.Canonicalize(source);
        foreach (var (before, after, count) in Edits(path))
        {
            Assert.IsTrue(after.Length > 0, path);
            Assert.AreEqual(count, source.Split(after, StringSplitOptions.None).Length - 1, path + ": " + after);
            source = source.Replace(after, before, StringComparison.Ordinal);
        }
        Assert.AreEqual(Original(path), OwnedSessionAskSourceInverse.GitObjectId(source), path);
        return source;
    }

    // Parent-verified complete Git blob anchors; these are not locally executed verification results.
    internal static string Original(string path) => path switch
    {
        Lifecycle => "da3160a1374e19b44e379d82a2ad2731724d90d4",
        Adapter => "e88e16b06100321687fa72842da7ca96ca526a21",
        Manager => "e9d99794586d1b802b02e28d28ef558cf253abad",
        Tasks => "246283b79e9ac94e17550a5be0654f237771745c",
        Host => "f7643627aca2378108719174e0a1bfefed9a405e",
        Owned => "f3842c0934ed5d1b2eb1420f959c5ffb37346891",
        Shell => "7859cc477e393f4c02fb0ca609b6de1cb0e2150b",
        Project => "0406663524c91c4a88c759f776464ef3c3a3ac8f",
        CacheInverse => "2c819b90bd170f6d62ca0922c3ee44c3f8400e6f",
        OwnerInverse => "32cf3a7d27b2d010ab23cfd92bb9f8b22555cede",
        ProfileInverse => "c8612b9b998418ea4c29834c1d7bd101f04b32da",
        GitHubInverse => "6e86397d6dd5370bf6c35af466f7faf8810dc25f",
        McpInverse => "4583ae16782f59641b09d80cff4021ec2c6b43fa",
        FrontendGuard => "4cff559b31f752d80790ed5a43462515a4a2aea6",
        WorkspaceGuard => "43a600ee6455f0aa0fcfaae6f6ec717cb77feb7d",
        DeferredGuard => "f637716c07b1956e339ba3fcab240ea985ca1b78",
        PromptGuard => "b075bb26738797a707cdc7e881a1b5039eb3ddca",
        DesktopProject => "858dfb176ff589b65220d504344a8a3d1d315925",
        _ => throw new ArgumentException("Unlisted source.", nameof(path)),
    };

    private static IEnumerable<(string Before, string After, int Count)> Edits(string path)
    {
        switch (path)
        {
            case CacheInverse:
                yield return ("", "        source = PluginAgentEventOwnershipSourceInverse.RestoreCacheInput(path, source);\n", 1);
                yield return (
                    "internal static string RestoreArchitectureInput(string path, string source) => path == Architecture ? Restore(path, source) : source;",
                    "internal static string RestoreArchitectureInput(string path, string source) => path == Architecture ? Restore(path, source) : PluginAgentEventOwnershipSourceInverse.RestoreArchitectureInput(path, source);", 1);
                yield return (
                    "internal static string RestoreLifetimeInput(string path, string source) => path is Architecture or Reminder ? Restore(path, source) : source;",
                    "internal static string RestoreLifetimeInput(string path, string source) => path is Architecture or Reminder ? Restore(path, source) : PluginAgentEventOwnershipSourceInverse.RestoreLifetimeInput(path, source);", 1);
                break;
            case OwnerInverse:
                yield return ("", "        source = PluginAgentEventOwnershipSourceInverse.RestoreOwnerInput(path, source);\n", 1);
                yield return (
                    "=> path is Host or Options or Runtime or Desktop or Profile ? RestoreCurrentInput(path, source) : source;",
                    "=> path is Host or Options or Runtime or Desktop or Profile ? RestoreCurrentInput(path, source) : PluginAgentEventOwnershipSourceInverse.RestoreDiscoveryInput(path, source);", 1);
                break;
            case ProfileInverse:
                yield return ("", "        source = PluginAgentEventOwnershipSourceInverse.RestoreProfileInput(path, source);\n", 1);
                break;
            case GitHubInverse:
                yield return ("", "        source = PluginAgentEventOwnershipSourceInverse.RestoreGitHubInput(path, source);\n", 1);
                break;
            case McpInverse:
                yield return ("", "        source = PluginAgentEventOwnershipSourceInverse.RestoreMcpInput(path, source);\n", 1);
                break;
            case FrontendGuard:
            case WorkspaceGuard:
            case PromptGuard:
                var readerMap = path == PromptGuard ? "RestorePromptInput" : "RestoreShellInput";
                yield return (
                    "        => File.ReadAllText(Path.Combine(SourceRoot(), relativePath))\n            .Replace(\"\\r\\n\", \"\\n\", StringComparison.Ordinal);",
                    $"        => PluginAgentEventOwnershipSourceInverse.{readerMap}(relativePath,\n            SourceTestText.DecodeSource(File.ReadAllBytes(Path.Combine(SourceRoot(), relativePath))));", 1);
                break;
            case DeferredGuard:
                yield return (
                    "        => SourceTestText.DecodeSource(File.ReadAllBytes(Path.Combine(SourceRoot(), relativePath)));",
                    "        => PluginAgentEventOwnershipSourceInverse.RestoreDeferredInput(relativePath,\n            SourceTestText.DecodeSource(File.ReadAllBytes(Path.Combine(SourceRoot(), relativePath))));", 1);
                break;
            case DesktopProject:
                yield return ("", "    <Compile Include=\"../CodeAlta.Tests/PluginAgentEventOwnershipSourceInverse.cs\" Link=\"PluginAgentEventOwnershipSourceInverse.cs\" />\n", 1);
                break;
            case Project:
                yield return ("", "    <InternalsVisibleTo Include=\"CodeAlta.Tests\" />\n", 1);
                break;
            case Host:
            case Owned:
                var factory = path == Host ? "CreateHostDisposal" : "CreateOwnedServicesDisposal";
                yield return ($"_disposeTask = {factory}(", $"_disposeTask = PluginEventDependencyBarrier.Wrap(PluginRuntime, {factory}(", 1);
                var tail = path == Host
                    ? "            LogManager.Shutdown,\n            ownsPluginRuntime,\n            ownsLogging)"
                    : "            LogManager.Shutdown,\n            ownsLogging)";
                yield return (tail + ";", tail + ");", 1);
                yield return ("public ValueTask DisposeAsync() => new(_disposeTask.Value);",
                    "public ValueTask DisposeAsync() => PluginEventDependencyBarrier.EnterDispose(PluginRuntime, _disposeTask);", 1);
                yield return ("", path == Host ? """
            await PluginEventDependencyBarrier.BeforeRollbackAsync(
                pluginRuntime ?? options.PrestartedPluginRuntime, creationFailure,
                new object?[] { pluginRuntime, runtimeService, agentHub, modelProviderRegistry, options }).ConfigureAwait(false);
""" + "\n" : """
            await PluginEventDependencyBarrier.BeforeRollbackAsync(
                sharedHost?.PluginRuntime ?? prestartedPluginRuntime, creationFailure,
                new object?[] { sharedHost, modelsDevCatalogService, prestartedPluginRuntime }).ConfigureAwait(false);
""" + "\n", 1);
                break;
            case Shell:
                yield return ("", "using CodeAlta.Plugins;\n", 1);
                yield return ("", "    private readonly PluginRuntimeManager? _pluginRuntime;\n    private readonly Lazy<Task> _disposeTask;\n", 1);
                yield return ("", """
        _pluginRuntime = (lifecycle.OwnedServices as CodeAltaOwnedServices)?.PluginRuntime;
        _disposeTask = PluginEventDependencyBarrier.Wrap(_pluginRuntime,
            new Lazy<Task>(() => DisposeRemindersThenFrontendAsync(
                () => _reminders?.DisposeAsync() ?? ValueTask.CompletedTask,
                DisposeFrontendAndOwnedServicesAsync)));
""" + "\n", 1);
                yield return ("""
    public async ValueTask DisposeAsync()
        => await DisposeRemindersThenFrontendAsync(
            () => _reminders?.DisposeAsync() ?? ValueTask.CompletedTask,
            DisposeFrontendAndOwnedServicesAsync);
""", "    public ValueTask DisposeAsync() => PluginEventDependencyBarrier.EnterDispose(_pluginRuntime, _disposeTask);", 1);
                break;
            case Lifecycle:
                yield return ("private readonly CancellationTokenSource _lifetime;", "private readonly PluginActivationLifetime _lifetime;", 1);
                yield return ("        CancellationTokenSource lifetime)", "        PluginActivationLifetime lifetime)", 1);
                yield return ("var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);", "var lifetime = new PluginActivationLifetime(cancellationToken);", 1);
                yield return ("public sealed class ActivePluginInstance", "public sealed partial class ActivePluginInstance", 1);
                yield return ("""
    /// <param name="timeout">The bounded deactivation timeout.</param>
    /// <param name="cancellationToken">A token to cancel deactivation.</param>
    /// <returns>Runtime diagnostics raised during deactivation.</returns>
    public async ValueTask<IReadOnlyList<PluginRuntimeDiagnostic>> DeactivateAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (State is PluginRuntimeState.Deactivated or PluginRuntimeState.Unloaded)
        {
            return [];
        }

        var diagnostics = await DeactivateManagedAsync(timeout, cancellationToken).ConfigureAwait(false);
        VerifyUnload(diagnostics);
        return diagnostics;
    }

    private async ValueTask<List<PluginRuntimeDiagnostic>> DeactivateManagedAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        State = PluginRuntimeState.Deactivating;
        var diagnostics = new List<PluginRuntimeDiagnostic>();
        _contributionRegistry.RemoveByPlugin(Descriptor.RuntimeKey);
        Contributions = [];
        _lifetime.Cancel();
        _taskService.CancelAll();

        using var timeoutSource = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeoutSource.Token, cancellationToken);
        try
        {
            await _taskService.WhenIdleAsync(linked.Token).ConfigureAwait(false);
            await DeactivatePluginInstanceAsync(_instance, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested)
        {
            diagnostics.Add(PluginRuntimeDiagnostic.Warning(
                PluginRuntimeDiagnosticSource.Unload,
                "Plugin deactivation timed out.",
                SourcePackage?.PackageId,
                SourcePackage?.PackageDirectory));
        }
        catch (Exception ex)
        {
            diagnostics.Add(PluginRuntimeDiagnostic.Error(
                PluginRuntimeDiagnosticSource.Unload,
                $"Plugin deactivation failed: {ex.Message}",
                SourcePackage?.PackageId,
                SourcePackage?.PackageDirectory,
                ex));
        }
        finally
        {
            RuntimeContext.Invalidate();
            State = PluginRuntimeState.Deactivated;
            _instance = null;
        }

        return diagnostics;
    }
""", """
    /// <param name="timeout">Bounds only this caller's wait, never the retained original or dependency lifetime.</param>
    /// <param name="cancellationToken">Cancels only this caller's wait.</param>
    /// <returns>Runtime diagnostics raised during deactivation.</returns>
    /// <exception cref="InvalidOperationException">The caller would join its own outstanding event attempt.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The timeout is invalid.</exception>
    /// <exception cref="OperationCanceledException">The caller's wait was cancelled.</exception>
    /// <exception cref="Exception">The retained prerequisite or deactivation failed; dependencies are retained.</exception>
    public ValueTask<IReadOnlyList<PluginRuntimeDiagnostic>> DeactivateAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
        => WaitDeactivationAsync(timeout, cancellationToken);
""", 1);
                yield return ("""
    public async ValueTask DisposeAsync()
    {
        await DeactivateAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        _lifetime.Dispose();
    }
""", "    public ValueTask DisposeAsync() => new(DeactivateOriginalAsync());", 1);
                break;
            case Adapter:
                yield return ("""
        foreach (var active in GetApplicableActivePlugins(activePlugins, options))
        {
            var context = CreateAgentEventContext(active, template, options, cancellationToken);
            try
            {
                if (active.Instance is not null)
                {
                    await active.Instance.OnAgentEventAsync(context, cancellationToken).ConfigureAwait(false);
                }

                context.Invalidate();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogCallbackFailure(active, "Agent-event callback failed.", ex);
                diagnostics.Add(AddDiagnostic(CreateCallbackDiagnostic(active, PluginRuntimeDiagnosticSource.Callback, "Agent-event callback failed.", ex)));
            }
        }
""", """
        // Eligibility is not liveness: stale supplied candidates must receive explicit Closing admission.
        foreach (var active in activePlugins.Where(plugin => plugin.RuntimeContext.AppliesToProject(options?.ProjectId, options?.ProjectPath)))
        {
            var admission = await active.ObserveOwnedAgentEventAsync(async () =>
            {
                var context = CreateAgentEventContext(active, template, options, cancellationToken);
                try
                {
                    if (active.Instance is not null)
                    {
                        await active.Instance.OnAgentEventAsync(context, cancellationToken).ConfigureAwait(false);
                    }

                    context.Invalidate();
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LogCallbackFailure(active, "Agent-event callback failed.", ex);
                    diagnostics.Add(AddDiagnostic(CreateCallbackDiagnostic(active, PluginRuntimeDiagnosticSource.Callback, "Agent-event callback failed.", ex)));
                }
            }).ConfigureAwait(false);
            if (admission != PluginAgentEventAdmission.Admitted)
            {
                // Return the outcome, but do not grow the manager's diagnostic store for rejected events.
                diagnostics.Add(PluginRuntimeDiagnostic.Warning(PluginRuntimeDiagnosticSource.Callback,
                    $"Agent-event admission: {admission}.", active.SourcePackage?.PackageId, active.SourcePackage?.PackageDirectory));
            }
        }
""", 1);
                break;
            case Manager:
                yield return ("public sealed class PluginRuntimeManager :", "public sealed partial class PluginRuntimeManager :", 1);
                yield return ("", "    /// <exception cref=\"InvalidOperationException\">Startup was already admitted, or manager-wide event admission is closed.</exception>\n", 1);
                yield return ("", """
        PluginRuntimeManagerStartResult? result = null;
        await RunOwnedStartAsync(async () => result = await StartOwnedCoreAsync(options, cancellationToken).ConfigureAwait(false)).ConfigureAwait(false);
        return result!;
    }

    private async Task<PluginRuntimeManagerStartResult> StartOwnedCoreAsync(PluginRuntimeManagerOptions options, CancellationToken cancellationToken)
    {
""" + "\n", 1);
                yield return ("""
            diagnostics.AddRange(activation.Diagnostics);
            if (activation.ActivePlugin is not null)
            {
                activePlugins.Add(activation.ActivePlugin);
            }
""", """
            if (activation.ActivePlugin is not null)
            {
                OwnActivation(activation.ActivePlugin);
                activePlugins.Add(activation.ActivePlugin);
            }
            diagnostics.AddRange(activation.Diagnostics);
""", 1);
                yield return ("""
                    diagnostics.AddRange(activation.Diagnostics);
                    if (activation.ActivePlugin is not null)
                    {
                        activePlugins.Add(activation.ActivePlugin);
                    }
""", """
                    if (activation.ActivePlugin is not null)
                    {
                        OwnActivation(activation.ActivePlugin);
                        activePlugins.Add(activation.ActivePlugin);
                    }
                    diagnostics.AddRange(activation.Diagnostics);
""", 1);
                yield return ("""
            await BuildAndActivateSourcePluginsAsync(liveStatus, token).ConfigureAwait(false);
            lock (_lock)
            {
                _activePlugins.AddRange(activePlugins);
            }
""", "            await BuildAndActivateSourcePluginsAsync(liveStatus, token).ConfigureAwait(false);", 1);
                yield return ("""
    /// <summary>Deactivates all active plugins and releases runtime-owned handles.</summary>
    public async ValueTask DeactivateAllAsync(CancellationToken cancellationToken = default)
    {
        ActivePluginInstance[] active;
        lock (_lock)
        {
            active = _activePlugins.ToArray();
            _activePlugins.Clear();
        }

        foreach (var plugin in active.Reverse())
        {
            var diagnostics = await plugin.DeactivateAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            _diagnostics.AddRange(diagnostics);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await DeactivateAllAsync().ConfigureAwait(false);
    }
""", """
    /// <summary>Deactivates all active plugins and releases runtime-owned handles.</summary>
    /// <remarks>Permanently closes event/start admission. Cancellation bounds the caller's wait only.</remarks>
    /// <exception cref="InvalidOperationException">The caller would await its own startup/event attempt.</exception>
    public async ValueTask DeactivateAllAsync(CancellationToken cancellationToken = default)
    {
        await DeactivateManagerOriginalAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        ThrowIfAgentEventSelfJoin();
        lock (_lock) _disposed = true;
        return new(DeactivateManagerOriginalAsync());
    }
""", 1);
                break;
            case Tasks:
                foreach (var edit in TaskEdits()) yield return edit;
                break;
            default: throw new ArgumentException("Unlisted source.", nameof(path));
        }
    }

    private static IEnumerable<(string Before, string After, int Count)> TaskEdits()
    {
        yield return ("", "    private bool _closed;\n    private PluginOwnedOperation? _close;\n", 1);
        yield return ("""
        var cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCancellationToken);
        var task = StartTask(work, cancellationTokenSource.Token, options.LongRunning);
        var handle = new PluginTaskHandle(
            name,
            options.Description,
            options.LongRunning,
            DateTimeOffset.UtcNow,
            task,
            () => RequestCancellation(cancellationTokenSource));
        var tracked = new TrackedPluginTask(handle, cancellationTokenSource);

        lock (_gate)
        {
            _runningTasks.Add(tracked);
        }

        _ = task.ContinueWith(
            _ => Complete(tracked),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        return handle;
""", """
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
""", 1);
        yield return ("""
            Task[] runningTasks;
            lock (_gate)
            {
                RemoveCompletedTasks();
                if (_runningTasks.Count == 0)
""", """
            PluginOwnedOperation[] runningTasks;
            lock (_gate)
            {
                RemoveCompletedTasks();
                if (_runningTasks.Count == 0)
""", 1);
        yield return ("runningTasks = [.. _runningTasks.Select(static task => task.Handle.Completion)];", "runningTasks = [.. _runningTasks.Select(static task => task.Cleanup)];", 1);
        yield return ("""
            try
            {
                await Task.WhenAll(runningTasks).WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // Completion failures are surfaced through PluginTaskHandle.Completion. Idle waits only gate unload.
            }
""", """
            // Work faults remain on the handle. Cancellation-cleanup faults prohibit lifetime release.
            await PluginOwnedOperation.JoinAsync(runningTasks).WaitAsync(cancellationToken).ConfigureAwait(false);
""", 1);
        yield return ("RequestCancellation(task.CancellationTokenSource);", "task.RequestCancellation();", 1);
        yield return ("", """
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

""" + "\n", 1);
        yield return ("", """
    private void RemoveCompletedTasks()
    {
        // Metadata only: both original cleanup and its observer are terminal before retirement.
        for (var index = _runningTasks.Count - 1; index >= 0; index--)
        {
            var outcome = _runningTasks[index].Cleanup.Outcome;
            if (outcome.IsCompletedSuccessfully && outcome.Result is null) _runningTasks.RemoveAt(index);
        }
    }

""" + "\n", 1);
        yield return ("""
    private static void RequestCancellation(CancellationTokenSource cancellationTokenSource)
    {
        try
        {
            cancellationTokenSource.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The task has already completed and the tracker has released its cancellation source.
        }
    }

    private void Complete(TrackedPluginTask task)
    {
        lock (_gate)
        {
            _runningTasks.Remove(task);
        }

        task.CancellationTokenSource.Dispose();
    }

    private void RemoveCompletedTasks()
    {
        for (var index = _runningTasks.Count - 1; index >= 0; index--)
        {
            if (!_runningTasks[index].Handle.IsCompleted)
            {
                continue;
            }

            var task = _runningTasks[index];
            _runningTasks.RemoveAt(index);
            task.CancellationTokenSource.Dispose();
        }
    }

    private readonly record struct WorkState
""", "    private readonly record struct WorkState", 1);
        yield return ("    private sealed record TrackedPluginTask(PluginTaskHandle Handle, CancellationTokenSource CancellationTokenSource);", """
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
""", 1);
    }
}
