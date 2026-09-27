using System.Globalization;
using System.Text.Json;
using CodeAlta.Catalog;
using CodeAlta.Catalog.Skills;
using NeoAstra.Rpc;

namespace CodeAlta.Desktop.Rpc;

// One explicit root, no effective discovery, configuration, provider or runtime activation.
[NeoRpcService("skillsInspection", Version = 1)]
internal sealed class SkillsInspectionService
{
    internal const int MaximumResponseBytes = 64 * 1024;
    internal const int MaximumMetadataReads = 4;
    private readonly string _epoch;
    private readonly Func<SkillsScanRequest, CancellationToken, Task<(string Status, string? Root)>> _resolve;
    private int _reading;

    internal SkillsInspectionService(ProjectCatalog projects, SessionViewJournalStore journals, string epoch)
        : this(epoch, async (request, token) =>
        {
            var created = DateTimeOffset.Parse(request.CreatedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            var result = await journals.ReadBoundedHeaderAsync(request.SessionId, created, token).ConfigureAwait(false);
            if (result.Status != BoundedSessionHeaderStatus.Found) return ("metadata_unavailable", null);
            var header = result.Header!;
            if (header.SchemaVersion != 1 || header.SessionId != request.SessionId || header.CreatedAt != created ||
                (request.Scope == "global" ? header.Kind != SessionViewKind.GlobalSession || header.ProjectRef is not null
                    || header.WorkingDirectory != projects.Options.GlobalRoot
                    : header.Kind != SessionViewKind.ProjectSession || header.ProjectRef != request.ProjectId || header.WorkingDirectory != request.ProjectPath))
                return ("scope_mismatch", null);
            if (request.Scope == "project")
            {
                var ownership = await projects.ReadBoundedOwnershipAsync(header.ProjectRef, header.WorkingDirectory, token).ConfigureAwait(false);
                if (ownership.Status != ProjectOwnershipStatus.Match) return ("project_unverified", null);
                if (ownership.Archived != false) return ("archived_project", null);
            }
            // The persisted header, verified against the complete bounded project catalog, supplies the root.
            return ("ok", request.RootKind == "project_alta" ? Path.Combine(header.WorkingDirectory!, ".alta", "skills")
                : Path.Combine(projects.Options.GlobalRoot, "skills"));
        }) { }

    // Literal isolated-root test seam; production always uses the bounded catalog/header resolver above.
    internal SkillsInspectionService(string epoch, Func<SkillsScanRequest, CancellationToken, Task<(string Status, string? Root)>> resolve)
    {
        if (!Guid.TryParseExact(epoch, "D", out var id) || id == Guid.Empty || id.ToString("D") != epoch)
            throw new ArgumentException("A canonical host epoch is required.", nameof(epoch));
        _epoch = epoch;
        _resolve = resolve;
    }

    [NeoRpcMethod("scan")]
    public async Task<SkillsScanResponse> ScanAsync(SkillsScanRequest request, CancellationToken cancellationToken)
    {
        SkillsScanResponse Reply(string status) => new(status, _epoch, request, null, "none", 0, 0, 0, 0, []);
        if (request is null || !Guid.TryParseExact(request.ExpectedHostEpoch, "D", out var epoch) || epoch == Guid.Empty
            || !Text(request.RequestId, 64) || !Text(request.SessionId, 256) || request.SessionId.Any(ch => "/\\:*?\"<>|".Contains(ch))
            || request.CreatedAt is not { Length: > 0 and <= 64 } || !DateTimeOffset.TryParse(request.CreatedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var created) || created.Year <= 1
            || request.Scope is not ("global" or "project") || request.RootKind is not ("project_alta" or "user_alta")
            || request.Scope == "global" && (request.ProjectId is not null || request.ProjectPath is not null || request.RootKind != "user_alta")
            || request.Scope == "project" && (!Guid.TryParseExact(request.ProjectId, "D", out var project) || project == Guid.Empty
                || !Text(request.ProjectPath, 4096) || !Path.IsPathFullyQualified(request.ProjectPath!)))
            return new("invalid_request", _epoch, null, null, "none", 0, 0, 0, 0, []);
        if (request.ExpectedHostEpoch != _epoch) return Reply("stale_epoch");
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.CompareExchange(ref _reading, 1, 0) != 0) return Reply("busy");
        // Do not release this gate on caller wait cancellation: the actual original must finish.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        var token = deadline.Token;
        try
        {
            return await Task.Run(async () =>
            {
                var resolved = await _resolve(request, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (resolved.Status != "ok" || resolved.Root is null) return Reply(resolved.Status);
                var raw = RawSkillCandidateReader.Scan(resolved.Root, token);
                var rows = new List<SkillsCandidate>();
                var bytes = 0;
                var reads = 0;
                foreach (var path in raw.CandidatePaths)
                {
                    token.ThrowIfCancellationRequested();
                    var relative = Path.GetRelativePath(resolved.Root, path).Replace('\\', '/');
                    if (!Text(relative, 1024) || relative.StartsWith("../", StringComparison.Ordinal) || Path.IsPathFullyQualified(relative))
                    {
                        rows.Add(new(rows.Count.ToString(CultureInfo.InvariantCulture), null, "omitted", "unsafe_path", null, null));
                        continue;
                    }
                    if (reads == MaximumMetadataReads)
                    {
                        rows.Add(new(rows.Count.ToString(CultureInfo.InvariantCulture), relative, "omitted", "read_budget", null, null));
                        continue;
                    }
                    reads++;
                    var metadata = await SkillCandidateMetadataReader.ReadAsync(resolved.Root, path,
                        Path.GetFileName(Path.GetDirectoryName(path)), token).ConfigureAwait(false);
                    bytes += metadata.BytesRead;
                    var name = metadata.Frontmatter?.Name;
                    var description = metadata.Frontmatter?.Description;
                    var parsed = metadata.Status == SkillCandidateMetadataStatus.Parsed;
                    var omitted = parsed && (!Text(name, 64) || !Text(description, 1024, multiline: true));
                    rows.Add(new(rows.Count.ToString(CultureInfo.InvariantCulture), relative,
                        omitted ? "omitted" : Code(metadata.Status.ToString()), omitted ? "display_budget" : Code(metadata.ParserDiagnostic.ToString()),
                        parsed && !omitted ? name : null, parsed && !omitted ? description : null));
                }
                token.ThrowIfCancellationRequested();
                var response = Reply("ok") with { TraversalStatus = Code(raw.Status.ToString()), Diagnostics = raw.Diagnostics.ToString(),
                    EntriesVisited = raw.EntriesVisited, DirectoriesOpened = raw.DirectoriesOpened, MetadataBytesRead = bytes, Candidates = rows.ToArray() };
                while (JsonSerializer.SerializeToUtf8Bytes(response, DesktopJsonContext.Default.SkillsScanResponse).Length > MaximumResponseBytes && rows.Count > 0)
                {
                    rows.RemoveAt(rows.Count - 1);
                    response = response with { Candidates = rows.ToArray(), ResponseOmitted = response.ResponseOmitted + 1 };
                }
                return response;
            }, CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested) { return Reply(cancellationToken.IsCancellationRequested ? "canceled" : "deadline"); }
        catch (Exception) { return Reply("read_failed"); }
        finally { Volatile.Write(ref _reading, 0); }
    }

    private static string Code(string value) => string.Concat(value.Select((ch, i) => char.IsUpper(ch) && i > 0 ? "_" + char.ToLowerInvariant(ch) : char.ToLowerInvariant(ch).ToString()));
    private static bool Text(string? value, int maximum, bool multiline = false)
    {
        if (value is not { Length: > 0 } || value.Length > maximum || value != value.Trim()) return false;
        for (var i = 0; i < value.Length; i++)
        {
            if (char.IsControl(value[i]) && !(multiline && value[i] is '\n' or '\r' or '\t')) return false;
            if (value[i] is >= '\u202a' and <= '\u202e' or >= '\u2066' and <= '\u2069') return false;
            if (char.IsSurrogate(value[i]) && (!char.IsHighSurrogate(value[i]) || ++i == value.Length || !char.IsLowSurrogate(value[i]))) return false;
        }
        return true;
    }
}

internal sealed record SkillsScanRequest(string ExpectedHostEpoch, string RequestId, string SessionId, string CreatedAt,
    string Scope, string? ProjectId, string? ProjectPath, string RootKind);
internal sealed record SkillsScanResponse(string Status, string HostEpoch, SkillsScanRequest? Request, string? TraversalStatus,
    string Diagnostics, int EntriesVisited, int DirectoriesOpened, int MetadataBytesRead, int ResponseOmitted, IReadOnlyList<SkillsCandidate> Candidates);
internal sealed record SkillsCandidate(string Id, string? RelativePath, string Status, string Diagnostic, string? Name, string? Description);
