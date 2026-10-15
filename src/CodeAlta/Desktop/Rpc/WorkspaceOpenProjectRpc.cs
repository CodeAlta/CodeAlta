using CodeAlta.Catalog;
using CodeAlta.Orchestration.Runtime;
using NeoAstra.Rpc;

namespace CodeAlta.Desktop.Rpc;

internal sealed partial class WorkspaceService
{
    private readonly ProjectCatalog? _importCatalog;
    private readonly string? _importEpoch;
    private readonly Func<string, Task<ProjectDescriptor>>? _import;
    private readonly object _importGate = new();
    private Task<WorkspaceOpenProjectResponse>? _importWork;
    private bool _importsClosed;
    // Access only under _importGate (also used by session creation).
    private bool CatalogAdmissionClosed => _importsClosed || _sessionsClosed;
    private bool CatalogAdmissionBusy => _sessionWork is not null || _archiveWork is not null || _importWork is not null
        || _projectReadWork is not null || _projectRenameWork is not null || _projectDeleteWork is not null;
    // Tests answer the caller inline (None): its next call then runs before the worker that answered it has ended.
    internal TaskCreationOptions CatalogReplyOptions { get; init; } = TaskCreationOptions.RunContinuationsAsynchronously;

    internal WorkspaceService(OwnedSessionWorkspace reads, ProjectCatalog catalog, string epoch) : this(reads)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        if (!ValidEpoch(epoch)) throw new ArgumentException("A canonical host epoch is required.", nameof(epoch));
        _importCatalog = catalog;
        _importEpoch = epoch;
        _directoryCompletionReader = (directory, prefix) => Task.Run(() => DirectoryCompletionReader.Read(directory, prefix));
        _import = async path => await catalog.GetByPathAsync(path, CancellationToken.None).ConfigureAwait(false)
            ?? await catalog.UpsertFromPathAsync(path, CancellationToken.None).ConfigureAwait(false);
        _projectNameWrite = (id, path, source, revision, name) =>
            catalog.RenameDisplayNameAsync(id, path, source, revision, name, CancellationToken.None);
    }

    internal WorkspaceService(OwnedSessionWorkspace reads, ProjectCatalog catalog, string epoch, Func<string, Task<ProjectDescriptor>> import)
        : this(reads, catalog, epoch)
    {
        ArgumentNullException.ThrowIfNull(import);
        _import = import;
    }

    /// <summary>Previews an existing absolute directory or imports it into the owned host's catalog after confirmation.</summary>
    /// <remarks>An admitted import runs to completion even if the caller stops waiting; close drains it before host disposal.</remarks>
    /// <param name="request">Host epoch, exact folder path and explicit confirmation.</param>
    /// <param name="cancellationToken">Cancels the wait, never an admitted catalog write.</param>
    /// <returns>A bounded outcome; failures after admission are reported as uncertain.</returns>
    /// <exception cref="OperationCanceledException">The caller cancels its wait.</exception>
    [NeoRpcMethod("openProject")]
    public async Task<WorkspaceOpenProjectResponse> OpenProjectAsync(WorkspaceOpenProjectRequest request, CancellationToken cancellationToken)
    {
        WorkspaceOpenProjectResponse Reply(string status, string? requestedPath = null, string? path = null, string? id = null)
            => new(status, _importEpoch, requestedPath, path, id);
        if (_importCatalog is null || _importEpoch is null) return Reply("unconfigured");
        if (request is null || !ValidEpoch(request.ExpectedHostEpoch) || !ValidPath(request.DirectoryPath)) return Reply("invalid_request");
        if (request.ExpectedHostEpoch != _importEpoch) return Reply("stale_epoch");
        cancellationToken.ThrowIfCancellationRequested();

        string path;
        try
        {
            if (!Path.IsPathFullyQualified(request.DirectoryPath)) return Reply("invalid_request");
            path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(request.DirectoryPath));
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Reply("invalid_request");
        }
        if (!Directory.Exists(path)) return Reply("missing_directory", request.DirectoryPath);
        if (!request.Confirmed) return Reply("confirmation_required", request.DirectoryPath, path);

        Task<WorkspaceOpenProjectResponse> work;
        lock (_importGate)
        {
            if (CatalogAdmissionClosed) return Reply("closed", request.DirectoryPath);
            if (CatalogAdmissionBusy) return Reply("busy", request.DirectoryPath);
            var completion = new TaskCompletionSource<WorkspaceOpenProjectResponse>(CatalogReplyOptions);
            _importWork = work = completion.Task;
            _ = ImportAsync(path, request.DirectoryPath, completion);
        }
        // Bridge cancellation only stops waiting. The admitted write and gate stay owned through settlement.
        return await work.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    internal async Task CloseImportsAsync()
    {
        Task? work;
        Task? read;
        Task? rename;
        Task? completion;
        Task? archive;
        Task? delete;
        lock (_importGate) { _importsClosed = true; work = _importWork; read = _projectReadWork; rename = _projectRenameWork; completion = _directoryCompletionWork; archive = _archiveWork; delete = _projectDeleteWork; }
        await Task.WhenAll(work ?? Task.CompletedTask, read ?? Task.CompletedTask, rename ?? Task.CompletedTask,
            completion ?? Task.CompletedTask, archive ?? Task.CompletedTask, delete ?? Task.CompletedTask).ConfigureAwait(false);
    }

    private async Task ImportAsync(string path, string requestedPath, TaskCompletionSource<WorkspaceOpenProjectResponse> completion)
    {
        WorkspaceOpenProjectResponse? response = null;
        try
        {
            // Directory.Exists is rechecked after admission; a removed folder never becomes a new catalog entry.
            if (!Directory.Exists(path)) response = new("missing_directory", _importEpoch, requestedPath, null, null);
            else
            {
                // General catalog Upsert/EnsurePersisted may restore archives. Desktop
                // must require the separate revision-checked unarchive confirmation.
                var existing = await _importCatalog!.GetByPathAsync(path, CancellationToken.None).ConfigureAwait(false);
                if (existing?.Archived == true)
                {
                    response = new("archived", _importEpoch, requestedPath, existing.ProjectPath, existing.Id);
                    return;
                }
                var project = await _import!(path).ConfigureAwait(false);
                response = new("ok", _importEpoch, requestedPath, project.ProjectPath, project.Id);
            }
        }
        catch (Exception)
        {
            // The exception can follow a persisted write. Never claim it was refused or retry automatically.
            response = new("import_unconfirmed", _importEpoch, requestedPath, null, null);
        }
        finally { SettleCatalogWork(ref _importWork, completion, response); }
    }

    // Admission is free again before the answer is given: whoever reads the answer can make the next catalog call.
    private void SettleCatalogWork<T>(ref Task<T>? work, TaskCompletionSource<T> completion, T? response) where T : class
    {
        lock (_importGate) { if (ReferenceEquals(work, completion.Task)) work = null; }
        if (response is not null) completion.TrySetResult(response);
    }

    private static bool ValidEpoch(string? value) => value is { Length: 36 } && Guid.TryParseExact(value, "D", out var parsed)
        && parsed != Guid.Empty && parsed.ToString("D") == value;

    private static bool ValidPath(string? value)
    {
        if (value is null || value.Length is < 1 or > 4096 || value != value.Trim() || string.IsNullOrWhiteSpace(value)) return false;
        for (var i = 0; i < value.Length; i++)
        {
            if (char.IsControl(value[i])) return false;
            if (!char.IsSurrogate(value[i])) continue;
            if (!char.IsHighSurrogate(value[i]) || ++i == value.Length || !char.IsLowSurrogate(value[i])) return false;
        }
        return true;
    }
}

internal sealed record WorkspaceOpenProjectRequest(string ExpectedHostEpoch, string DirectoryPath, bool Confirmed);
internal sealed record WorkspaceOpenProjectResponse(string Status, string? HostEpoch, string? RequestedPath, string? ProjectPath, string? ProjectId);
