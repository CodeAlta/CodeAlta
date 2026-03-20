using CodeAlta.Models;
using CodeAlta.Orchestration.Runtime;
using CodeAlta.Views;
using XenoAtom.Logging;

namespace CodeAlta.App;

internal sealed class CodeAltaShellController : IAsyncDisposable
{
    private readonly ICodeAltaShell _shell;
    private readonly IKnownProjectImporter _knownProjectImporter;
    private readonly IProjectCatalogLoader _projectCatalog;
    private readonly IRecoverableThreadSource _recoverableThreadSource;
    private readonly CancellationTokenSource _disposeCts = new();
    private IUiDispatcher? _uiDispatcher;
    private CancellationTokenSource? _initializationCts;
    private Task? _initializationTask;

    public CodeAltaShellController(
        ICodeAltaShell shell,
        IKnownProjectImporter knownProjectImporter,
        IProjectCatalogLoader projectCatalog,
        IRecoverableThreadSource recoverableThreadSource)
    {
        ArgumentNullException.ThrowIfNull(shell);
        ArgumentNullException.ThrowIfNull(knownProjectImporter);
        ArgumentNullException.ThrowIfNull(projectCatalog);
        ArgumentNullException.ThrowIfNull(recoverableThreadSource);

        _shell = shell;
        _knownProjectImporter = knownProjectImporter;
        _projectCatalog = projectCatalog;
        _recoverableThreadSource = recoverableThreadSource;
    }

    public void AttachUiDispatcher(IUiDispatcher uiDispatcher)
    {
        ArgumentNullException.ThrowIfNull(uiDispatcher);
        _uiDispatcher = uiDispatcher;
    }

    public void StartInitialization(CancellationToken cancellationToken)
    {
        if (_initializationTask is not null)
        {
            return;
        }

        _initializationCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposeCts.Token);
        _initializationTask = Task.Run(
            () => RunInitializationAsync(_initializationCts.Token),
            CancellationToken.None);
    }

    public async Task ReloadCatalogAsync(CancellationToken cancellationToken)
    {
        try
        {
            await UiDispatcher.InvokeAsync(
                    () => _shell.SetStatus("Refreshing project and thread catalog...", showSpinner: true))
                .ConfigureAwait(false);

            await _knownProjectImporter.ImportAsync(cancellationToken).ConfigureAwait(false);
            var projects = await _projectCatalog.LoadAsync(cancellationToken).ConfigureAwait(false);
            var threads = await _recoverableThreadSource.ListRecoverableThreadsAsync(cancellationToken).ConfigureAwait(false);

            await UiDispatcher.InvokeAsync(
                    () =>
                    {
                        _shell.ApplyRecoveredCatalogState(projects, threads);
                        _shell.SetReadyStatusForCurrentSelection();
                    })
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            await UiDispatcher.InvokeAsync(
                    () => _shell.SetStatus($"Failed to refresh catalog: {ex.Message}", tone: StatusTone.Error))
                .ConfigureAwait(false);
        }
    }

    public Task ApplyRuntimeEventAsync(WorkThreadRuntimeEvent runtimeEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(runtimeEvent);
        cancellationToken.ThrowIfCancellationRequested();
        return UiDispatcher.InvokeAsync(() => _shell.HandleRuntimeEvent(runtimeEvent));
    }

    public Task SelectGlobalScopeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return UiDispatcher.InvokeAsync(_shell.SelectGlobalScope);
    }

    public Task SelectProjectScopeAsync(string projectId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        cancellationToken.ThrowIfCancellationRequested();
        return UiDispatcher.InvokeAsync(() => _shell.SelectProjectScope(projectId));
    }

    public Task OpenThreadAsync(string threadId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);
        cancellationToken.ThrowIfCancellationRequested();
        return UiDispatcher.InvokeAsync(() => _shell.OpenThread(threadId));
    }

    public async ValueTask DisposeAsync()
    {
        _disposeCts.Cancel();
        _initializationCts?.Cancel();

        if (_initializationTask is not null)
        {
            try
            {
                await _initializationTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _initializationCts?.Dispose();
        _disposeCts.Dispose();
    }

    private IUiDispatcher UiDispatcher
        => _uiDispatcher ?? throw new InvalidOperationException("The UI dispatcher must be attached before shell operations begin.");

    internal Task InitializeAsync(CancellationToken cancellationToken)
        => RunInitializationAsync(cancellationToken);

    private async Task RunInitializationAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _shell.InitializeChatBackendsAsync(cancellationToken).ConfigureAwait(false);
            await RefreshCatalogFromBackendsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            if (!_disposeCts.IsCancellationRequested)
            {
                await UiDispatcher.InvokeAsync(
                        () =>
                        {
                            _shell.RefreshCatalogAndThreadWorkspace();
                            _shell.SetReadyStatusForCurrentSelection();
                            _shell.SetInitialized(true);
                            _shell.TrySchedulePendingStartupThreadRestore(CancellationToken.None);
                        })
                    .ConfigureAwait(false);
            }
        }
    }

    private async Task RefreshCatalogFromBackendsAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _knownProjectImporter.ImportAsync(cancellationToken).ConfigureAwait(false);
            var projects = await _projectCatalog.LoadAsync(cancellationToken).ConfigureAwait(false);
            var threads = await _recoverableThreadSource.ListRecoverableThreadsAsync(cancellationToken).ConfigureAwait(false);

            await UiDispatcher.InvokeAsync(
                    () => _shell.ApplyRecoveredCatalogState(projects, threads))
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (LogManager.IsInitialized && CodeAltaApp.UiLogger.IsEnabled(LogLevel.Error))
            {
                CodeAltaApp.UiLogger.Error(ex, "Failed to refresh backend startup state.");
            }
        }
    }
}