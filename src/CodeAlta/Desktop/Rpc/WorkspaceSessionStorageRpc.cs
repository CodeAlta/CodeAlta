using System.Globalization;
using CodeAlta.Agent.Runtime;
using NeoAstra.Rpc;

namespace CodeAlta.Desktop.Rpc;

internal sealed partial class WorkspaceService
{
    /// <summary>Shows an entry in the file manager, its folder open and the entry selected; the one of the system by default.</summary>
    internal Func<string, bool> RevealSessionFile { get; init; } = DesktopFileReveal.Show;

    /// <summary>Tells where a session is stored on this computer: its journal, and the folder that holds it.</summary>
    [NeoRpcMethod("sessionStorage")]
    public WorkspaceSessionStorageResponse SessionStorage(WorkspaceSessionStorageRequest request) => SessionStorage(request, reveal: false);

    /// <summary>Opens the folder of a session in the file manager of the system, with the journal of the session selected.</summary>
    [NeoRpcMethod("revealSessionStorage")]
    public WorkspaceSessionStorageResponse RevealSessionStorage(WorkspaceSessionStorageRequest request) => SessionStorage(request, reveal: true);

    private WorkspaceSessionStorageResponse SessionStorage(WorkspaceSessionStorageRequest request, bool reveal)
    {
        WorkspaceSessionStorageResponse Reply(string status, string? folder = null, string? file = null)
            => new(status, _importEpoch, request?.SessionId, folder, file, DesktopFileReveal.Available);
        if (_importCatalog is null || _importEpoch is null) return Reply("unconfigured");
        if (request is null || !ValidEpoch(request.ExpectedHostEpoch) || !ValidScopeValue(request.SessionId, 128)
            || request.SessionId.AsSpan().IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || !DateTimeOffset.TryParse(request.CreatedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var createdAt))
            return Reply("invalid_scope");
        if (request.ExpectedHostEpoch != _importEpoch) return Reply("stale_epoch");

        // A journal is filed under the day its session was created: the page names the session and that day, and only a
        // journal that is there, under the sessions of this instance, is told or shown.
        var sessions = Path.GetFullPath(_importCatalog.Options.SessionsRoot);
        var file = Path.GetFullPath(new AgentRuntimePathLayout(_importCatalog.Options.StateRoot).GetSessionFilePath(request.SessionId, createdAt));
        if (!file.StartsWith(sessions + Path.DirectorySeparatorChar, StringComparison.Ordinal) || !File.Exists(file)) return Reply("session_missing");
        var folder = Path.GetDirectoryName(file)!;
        return Reply(reveal && !RevealSessionFile(file) ? "failed" : "ok", folder, file);
    }
}

/// <param name="CreatedAt">When the session was created, as its row says it.</param>
internal sealed record WorkspaceSessionStorageRequest(string ExpectedHostEpoch, string SessionId, string CreatedAt);

/// <param name="Status"><c>ok</c>, <c>unconfigured</c>, <c>invalid_scope</c>, <c>stale_epoch</c>, <c>session_missing</c>, or
/// <c>failed</c> when the file manager could not be opened.</param>
/// <param name="Folder">The folder that holds the journal of the session.</param>
/// <param name="File">The journal of the session.</param>
/// <param name="CanReveal">Whether this system has a file manager the application can open.</param>
internal sealed record WorkspaceSessionStorageResponse(string Status, string? HostEpoch, string? SessionId, string? Folder, string? File, bool CanReveal);
