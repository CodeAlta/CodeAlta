using CodeAlta.Agent;
using CodeAlta.Agent.LocalRuntime;
using CodeAlta.Catalog;
using CodeAlta.Orchestration.Runtime;
using XenoAtom.Logging;

namespace CodeAlta.App;

internal sealed class KnownProjectImporter : IKnownProjectImporterWithProgress
{
    private static readonly Logger Logger = LogManager.GetLogger("CodeAlta.App");
    private readonly AgentHub _agentHub;
    private readonly IReadOnlyList<AgentBackendDescriptor> _backendDescriptors;
    private readonly ProjectCatalog _projectCatalog;
    private readonly CatalogOptions? _catalogOptions;
    private readonly SemaphoreSlim _importGate = new(initialCount: 1, maxCount: 1);
    private readonly object _sharedLocalImportTaskGate = new();
    private Task? _sharedLocalImportTask;

    public KnownProjectImporter(
        AgentHub agentHub,
        IReadOnlyList<AgentBackendDescriptor> backendDescriptors,
        ProjectCatalog projectCatalog,
        CatalogOptions? catalogOptions = null)
    {
        ArgumentNullException.ThrowIfNull(agentHub);
        ArgumentNullException.ThrowIfNull(backendDescriptors);
        ArgumentNullException.ThrowIfNull(projectCatalog);

        _agentHub = agentHub;
        _backendDescriptors = backendDescriptors;
        _projectCatalog = projectCatalog;
        _catalogOptions = catalogOptions;
    }

    public Func<AgentBackendId, bool>? ShouldLoadProviderSessions { get; set; }

    public Task ImportAsync(CancellationToken cancellationToken)
        => ImportAsync(static _ => { }, cancellationToken);

    public async Task ImportBackendAsync(AgentBackendDescriptor descriptor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        if (ShouldLoadProviderSessions is { } shouldLoadProviderSessions &&
            !shouldLoadProviderSessions(descriptor.BackendId))
        {
            return;
        }

        try
        {
            if (_catalogOptions is not null &&
                await UsesSharedSessionMetadataStoreAsync(descriptor.BackendId, cancellationToken).ConfigureAwait(false))
            {
                await ImportSharedLocalRuntimeProjectsOnceAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            var sessions = await _agentHub.ListSessionsAsync(descriptor.BackendId, cancellationToken: cancellationToken).ConfigureAwait(false);
            var workingDirectories = new List<string?>();
            foreach (var session in sessions)
            {
                workingDirectories.Add(session.Context?.Cwd ?? session.WorkspacePath);
            }

            await ImportWorkingDirectoriesAsync(workingDirectories, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, $"Failed to import project history from backend '{descriptor.BackendId.Value}'.");
        }
    }

    private Task ImportSharedLocalRuntimeProjectsOnceAsync(CancellationToken cancellationToken)
    {
        lock (_sharedLocalImportTaskGate)
        {
            if (_sharedLocalImportTask is null || _sharedLocalImportTask.IsCompleted)
            {
                _sharedLocalImportTask = ImportSharedLocalRuntimeProjectsAsync(cancellationToken);
            }

            return _sharedLocalImportTask;
        }
    }

    private async Task ImportSharedLocalRuntimeProjectsAsync(CancellationToken cancellationToken)
    {
        var catalogOptions = _catalogOptions ?? throw new InvalidOperationException("Catalog options are required to import shared local runtime sessions.");
        var backendIds = _backendDescriptors
            .Select(static descriptor => descriptor.BackendId.Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var store = new FileSystemLocalAgentSessionStore(new LocalAgentRuntimePathLayout(catalogOptions.GlobalRoot));
        var sessions = await store.ListSessionsAsync(cancellationToken).ConfigureAwait(false);
        var workingDirectories = new List<string?>();
        foreach (var session in sessions)
        {
            if (string.IsNullOrWhiteSpace(session.ProviderKey) ||
                !backendIds.Contains(session.ProviderKey) ||
                ShouldLoadProviderSessions?.Invoke(new AgentBackendId(session.ProviderKey)) == false)
            {
                continue;
            }

            workingDirectories.Add(session.WorkingDirectory);
        }

        await ImportWorkingDirectoriesAsync(workingDirectories, cancellationToken).ConfigureAwait(false);
    }

    private async Task ImportWorkingDirectoriesAsync(
        IReadOnlyList<string?> workingDirectories,
        CancellationToken cancellationToken)
    {
        await _importGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _projectCatalog.ImportWorkingDirectoriesAsync(workingDirectories, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _importGate.Release();
        }
    }

    private async Task<bool> UsesSharedSessionMetadataStoreAsync(AgentBackendId backendId, CancellationToken cancellationToken)
    {
        try
        {
            return await _agentHub.UsesSharedSessionMetadataStoreAsync(backendId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    public async Task ImportAsync(Action<ProviderSessionLoadProgress> reportProgress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reportProgress);

        var descriptors = _backendDescriptors.ToArray();
        var progressGate = new object();
        var loadingProviderNames = descriptors.Select(static descriptor => descriptor.DisplayName).ToList();
        var completedProviderCount = 0;
        ReportProgress(null);

        var importTasks = descriptors.Select(ImportBackendProjectsAsync).ToArray();
        await Task.WhenAll(importTasks).ConfigureAwait(false);
        return;

        async Task ImportBackendProjectsAsync(AgentBackendDescriptor descriptor)
        {
            try
            {
                await ImportBackendAsync(descriptor, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                ReportProgress(descriptor);
            }
        }

        void ReportProgress(AgentBackendDescriptor? descriptor)
        {
            lock (progressGate)
            {
                if (descriptor is not null)
                {
                    completedProviderCount++;
                    loadingProviderNames.RemoveAll(name => string.Equals(name, descriptor.DisplayName, StringComparison.Ordinal));
                }

                reportProgress(new ProviderSessionLoadProgress(
                    descriptor?.BackendId ?? default,
                    descriptor?.DisplayName ?? string.Empty,
                    completedProviderCount,
                    descriptors.Length,
                    loadingProviderNames.ToArray()));
            }
        }
    }
}
