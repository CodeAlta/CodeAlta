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

    internal WorkspaceService(OwnedSessionWorkspace reads, ProjectCatalog catalog, string epoch) : this(reads)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        if (!ValidEpoch(epoch)) throw new ArgumentException("A canonical host epoch is required.", nameof(epoch));
        _importCatalog = catalog;
        _importEpoch = epoch;
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
            if (_importsClosed) return Reply("closed", request.DirectoryPath);
            if (_importWork is not null || _projectReadWork is not null || _projectRenameWork is not null) return Reply("busy", request.DirectoryPath);
            var completion = new TaskCompletionSource<WorkspaceOpenProjectResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
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
        lock (_importGate) { _importsClosed = true; work = _importWork; read = _projectReadWork; rename = _projectRenameWork; }
        await Task.WhenAll(work ?? Task.CompletedTask, read ?? Task.CompletedTask, rename ?? Task.CompletedTask).ConfigureAwait(false);
    }

    private async Task ImportAsync(string path, string requestedPath, TaskCompletionSource<WorkspaceOpenProjectResponse> completion)
    {
        try
        {
            // Directory.Exists is rechecked after admission; a removed folder never becomes a new catalog entry.
            if (!Directory.Exists(path)) completion.TrySetResult(new("missing_directory", _importEpoch, requestedPath, null, null));
            else
            {
                var project = await _import!(path).ConfigureAwait(false);
                completion.TrySetResult(new("ok", _importEpoch, requestedPath, project.ProjectPath, project.Id));
            }
        }
        catch (Exception)
        {
            // The exception can follow a persisted write. Never claim it was refused or retry automatically.
            completion.TrySetResult(new("import_unconfirmed", _importEpoch, requestedPath, null, null));
        }
        finally { lock (_importGate) { if (ReferenceEquals(_importWork, completion.Task)) _importWork = null; } }
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
