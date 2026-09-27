using System.Globalization;
using System.Text;
using CodeAlta.Catalog;
using CodeAlta.Orchestration.Runtime.SystemPrompts;
using NeoAstra.Rpc;

namespace CodeAlta.Desktop.Rpc;

// Desktop host lifetime owns the original task, independent of RPC waiter cancellation.
[NeoRpcService("promptCreation", Version = 1)]
internal sealed class PromptCreationService
{
    private readonly object _gate = new();
    private readonly string _epoch;
    private readonly Func<PromptCreateRequest, CancellationToken, Task<(string Status, PromptResourceStore? Store)>> _resolve;
    private Task<PromptCreateResponse>? _original;
    private bool _closed;
    private readonly WorkspaceService? _workspace;

    internal PromptCreationService(ProjectCatalog projects, SessionViewJournalStore journals, string epoch, WorkspaceService workspace)
        : this(epoch, Resolver(projects, journals)) { _workspace = workspace; }

    internal PromptCreationService(ProjectCatalog projects, SessionViewJournalStore journals, string epoch)
        : this(epoch, Resolver(projects, journals)) { }

    private static Func<PromptCreateRequest, CancellationToken, Task<(string, PromptResourceStore?)>> Resolver(ProjectCatalog projects, SessionViewJournalStore journals)
    {
        var codec = new TextFileCodec(); // No other Desktop prompt writer; all creates share this owner.
        return async (request, token) =>
        {
            var created = DateTimeOffset.Parse(request.CreatedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            var found = await journals.ReadBoundedHeaderAsync(request.SessionId, created, token).ConfigureAwait(false);
            if (found.Status != BoundedSessionHeaderStatus.Found) return ("refused", null);
            var header = found.Header!;
            if (header.SchemaVersion != 1 || header.SessionId != request.SessionId || header.CreatedAt != created ||
                (request.Scope == "global" ? header.Kind != SessionViewKind.GlobalSession || header.ProjectRef is not null
                    || header.WorkingDirectory != projects.Options.GlobalRoot
                    : header.Kind != SessionViewKind.ProjectSession || header.ProjectRef != request.ProjectId || header.WorkingDirectory != request.ProjectPath))
                return ("refused", null);
            if (request.Scope == "project")
            {
                var ownership = await projects.ReadBoundedOwnershipAsync(header.ProjectRef, header.WorkingDirectory, token).ConfigureAwait(false);
                if (ownership.Status != ProjectOwnershipStatus.Match || ownership.Archived != false) return ("refused", null);
            }
            var roots = new AgentPromptCatalog().ResolveRoots(new AgentPromptCatalogQuery
            {
                UserCodeAltaRoot = projects.Options.GlobalRoot,
                UserProfileRoot = projects.Options.GlobalRoot, // Explicit unused fallback: do not query the user's profile.
                ProjectRoot = request.Scope == "project" ? header.WorkingDirectory : null,
            });
            return ("ok", new PromptResourceStore(roots.ShippedPromptRoot, roots.GlobalPromptRoot, roots.ProjectPromptRoot, codec));
        };
    }

    internal PromptCreationService(string epoch, Func<PromptCreateRequest, CancellationToken, Task<(string Status, PromptResourceStore? Store)>> resolve)
    {
        if (!CanonicalEpoch(epoch)) throw new ArgumentException("A canonical host epoch is required.", nameof(epoch));
        _epoch = epoch;
        _resolve = resolve;
    }

    [NeoRpcMethod("create")]
    public Task<PromptCreateResponse> CreateAsync(PromptCreateRequest request, CancellationToken cancellationToken)
    {
        if (!Valid(request)) return Task.FromResult(new PromptCreateResponse("refused", _epoch, null));
        if (request.ExpectedHostEpoch != _epoch) return Task.FromResult(new PromptCreateResponse("stale_epoch", _epoch, request));
        cancellationToken.ThrowIfCancellationRequested();
        Task<PromptCreateResponse> original;
        lock (_gate)
        {
            if (_closed) return Task.FromResult(new PromptCreateResponse("closed", _epoch, request));
            if (_original is { IsCompleted: false }) return Task.FromResult(new PromptCreateResponse("busy", _epoch, request));
            original = _original = Task.Run(() => _workspace is null ? RunAsync(request)
                : _workspace.RunPromptCreationAsync(() => RunAsync(request), status => new(status, _epoch, request)), CancellationToken.None);
        }
        return original.WaitAsync(cancellationToken);
    }

    private async Task<PromptCreateResponse> RunAsync(PromptCreateRequest request)
    {
        var publishing = false;
        try
        {
            var resolved = await _resolve(request, CancellationToken.None).ConfigureAwait(false);
            if (resolved.Status != "ok" || resolved.Store is null) return new("refused", _epoch, request);
            var identity = new PromptResourceIdentity(request.RootKind == "project_alta" ? PromptResourceScope.Project : PromptResourceScope.Global,
                PromptResourceKind.Agent, request.PromptId);
            _ = resolved.Store.GetEditablePath(identity); // Reject observed linked ancestors/final entry before publication.
            var content = new PromptFileContent(request.Name, request.Description.Length == 0 ? null : request.Description, null, request.Body, request.Mode == "append");
            publishing = true;
            var created = await resolved.Store.TryCreateAsync(identity, content, CancellationToken.None).ConfigureAwait(false);
            return new(created ? "created" : "conflict", _epoch, request);
        }
        catch (Exception) { return new(publishing ? "uncertain" : "refused", _epoch, request); }
    }

    internal void CloseAdmission() { lock (_gate) _closed = true; }
    internal Task DrainAsync()
    {
        lock (_gate) { _closed = true; return _original ?? Task.CompletedTask; }
    }

    private static bool CanonicalEpoch(string? value) => Guid.TryParseExact(value, "D", out var id) && id != Guid.Empty && id.ToString("D") == value;
    private static bool Text(string? value, int maximum, bool multiline = false, bool empty = false)
    {
        if (value is null || value.Length > maximum || !empty && string.IsNullOrWhiteSpace(value)) return false;
        if (value.Any(ch => char.IsControl(ch) && !(multiline && ch is '\n' or '\r' or '\t') || ch is >= '\u202a' and <= '\u202e' or >= '\u2066' and <= '\u2069')) return false;
        try { _ = new UTF8Encoding(false, true).GetByteCount(value); return true; }
        catch (EncoderFallbackException) { return false; }
    }
    private static bool Valid(PromptCreateRequest? request)
    {
        if (request is null || !CanonicalEpoch(request.ExpectedHostEpoch) || !Text(request.RequestId, 64)
            || !Text(request.SessionId, 256) || request.SessionId.Trim() != request.SessionId || request.SessionId.Any(ch => "/\\:*?\"<>|".Contains(ch))
            || !Text(request.CreatedAt, 64) || !DateTimeOffset.TryParse(request.CreatedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var created) || created.Year <= 1
            || request.Scope is not ("global" or "project") || request.RootKind is not ("user_alta" or "project_alta")
            || request.Scope == "global" && (request.ProjectId is not null || request.ProjectPath is not null || request.RootKind != "user_alta")
            || request.Scope == "project" && (!CanonicalEpoch(request.ProjectId) || !Text(request.ProjectPath, 4096) || !Path.IsPathFullyQualified(request.ProjectPath!))
            || request.PromptId is not { Length: > 0 and <= 64 } || request.PromptId[0] is < 'a' or > 'z'
            || request.PromptId.Any(ch => ch is not (>= 'a' and <= 'z') and not (>= '0' and <= '9') and not '-')
            || !Text(request.Name, 128) || !Text(request.Description, 512, empty: true) || !Text(request.Body, 16384, multiline: true)
            || request.Mode is not ("append" or "replace") || !request.UnderstoodShadowing) return false;
        try { PromptResourceStore.ValidateId(request.PromptId); return true; }
        catch (ArgumentException) { return false; }
    }
}

internal sealed record PromptCreateRequest(string ExpectedHostEpoch, string RequestId, string SessionId, string CreatedAt,
    string Scope, string? ProjectId, string? ProjectPath, string RootKind, string PromptId, string Name, string Description,
    string Body, string Mode, bool UnderstoodShadowing);
internal sealed record PromptCreateResponse(string Status, string HostEpoch, PromptCreateRequest? Request);
