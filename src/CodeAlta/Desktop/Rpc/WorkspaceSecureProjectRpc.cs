using CodeAlta.Catalog;
using NeoAstra.Rpc;

namespace CodeAlta.Desktop.Rpc;

internal sealed partial class WorkspaceService
{
    // Where a project says that its sessions are confidential ([privacy] pseudonymize in its .alta/config.toml).
    private readonly CodeAltaConfigStore? _privacy;

    /// <summary>
    /// Makes a project secure, or normal again: the sessions of a secure project are confidential, what they send to their
    /// provider is pseudonymized. A session that was confidential stays so once its project is normal again.
    /// </summary>
    /// <param name="request">The host epoch, the project and the choice.</param>
    /// <param name="cancellationToken">Stops the read of the project.</param>
    /// <returns><c>ok</c> with the choice the project now has, or why nothing was written.</returns>
    [NeoRpcMethod("setProjectSecure")]
    public async Task<WorkspaceSecureProjectResponse> SetProjectSecureAsync(WorkspaceSecureProjectRequest request, CancellationToken cancellationToken)
    {
        if (_importCatalog is null || _importEpoch is null || _privacy is null) return new("unconfigured", false);
        if (request is null || !ValidEpoch(request.ExpectedHostEpoch) || !ValidScopeValue(request.ProjectId, 256)) return new("invalid_scope", false);
        if (request.ExpectedHostEpoch != _importEpoch) return new("stale_epoch", false);
        var project = await _importCatalog.GetByIdAsync(request.ProjectId!, cancellationToken).ConfigureAwait(false);
        if (project is null) return new("not_found", false);
        if (project.Archived) return new("archived", _privacy.IsProjectPseudonymized(project.ProjectPath));
        try
        {
            _privacy.SaveProjectPseudonymize(project.ProjectPath, request.Secure);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or FormatException)
        {
            return new("write_failed", _privacy.IsProjectPseudonymized(project.ProjectPath));
        }

        return new("ok", _privacy.IsProjectPseudonymized(project.ProjectPath));
    }
}

/// <param name="ExpectedHostEpoch">The epoch of the host the window reads.</param>
/// <param name="ProjectId">The project.</param>
/// <param name="Secure">Whether its sessions are confidential.</param>
internal sealed record WorkspaceSecureProjectRequest(string? ExpectedHostEpoch, string? ProjectId, bool Secure);

/// <param name="Status"><c>ok</c>, or why nothing was written.</param>
/// <param name="Secure">Whether the project is secure now.</param>
internal sealed record WorkspaceSecureProjectResponse(string Status, bool Secure);
