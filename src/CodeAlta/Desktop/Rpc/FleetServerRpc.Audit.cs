using System.Globalization;
using CodeAlta.Core.IO;
using CodeAlta.Fleet.Protocol.Api;
using CodeAlta.Fleet.Server;
using NeoAstra.Rpc;

namespace CodeAlta.Desktop.Rpc;

// The audit log of a server (doc/fleet/security-and-privacy.md, audit log), for Settings > Fleet > Audit: a filtered, paged
// timeline, the check of its chain, and exports saved where the person chooses. Read only: nothing here changes a record.
internal sealed partial class FleetServerService
{
    private readonly Lock _exportGate = new();
    private string? _lastExport;

    /// <summary>
    /// Gets what asks the person where to save a file: the title of the dialog, the name it proposes, the name of the kind of
    /// file and its extension; the chosen full path, or null when they cancel. Null where the desktop has no dialog.
    /// </summary>
    internal Func<string, string, string, string, CancellationToken, Task<string?>>? ChooseSavePath { get; init; }

    /// <summary>Gets what shows a file in the file manager of the system; false when it could not.</summary>
    internal Func<string, bool> RevealFile { get; init; } = DesktopFileReveal.Show;

    /// <summary>A page of the audit log of a server, newest first, as its filters select it.</summary>
    [NeoRpcMethod("audit")]
    public async Task<FleetAuditPage> AuditAsync(FleetAuditRequest request, CancellationToken cancellationToken)
    {
        if (Refuse(request?.ExpectedEpoch) is { } refused) return FleetAuditPage.Failed(refused, null);
        var query = FleetServerLinkStore.IsValidId(request!.LinkId) ? ToQuery(request.Filters) : null;
        if (query is null) return FleetAuditPage.Failed("invalid_request", null);
        try
        {
            var page = await _servers!.AccessCallAsync(request.LinkId, (client, token) => client.ListAuditAsync(query with
            {
                Newest = true, Before = request.Before is > 0 ? request.Before : null, Limit = Math.Clamp(request.Limit ?? 100, 1, FleetAuditQuery.MaxPage),
            }, token), cancellationToken).ConfigureAwait(false);
            return new FleetAuditPage("ok", null, [.. page.Items.Select(ToItem)], page.Next is { } next && int.TryParse(next, CultureInfo.InvariantCulture, out var before) ? before : null);
        }
        catch (Exception exception) when (Failure(exception) is { } failure)
        {
            return FleetAuditPage.Failed(failure.Status, failure.Message);
        }
    }

    /// <summary>Checks the hash chain of the whole audit log of a server.</summary>
    [NeoRpcMethod("verifyAudit", TimeoutMilliseconds = 600_000)]
    public async Task<FleetAuditCheck> VerifyAuditAsync(FleetServerLinkRequest request, CancellationToken cancellationToken)
    {
        if (Refuse(request?.ExpectedEpoch) is { } refused) return new(refused, null, false, 0, null, null, null);
        if (!FleetServerLinkStore.IsValidId(request!.LinkId)) return new("invalid_request", null, false, 0, null, null, null);
        try
        {
            var check = await _servers!.AccessCallAsync(request.LinkId, static (client, token) => client.VerifyAuditAsync(token), cancellationToken).ConfigureAwait(false);
            return new("ok", null, check.Valid, Small(check.Count), check.BrokenAt is { } broken ? Small(broken) : null, check.HeadHash, Iso(check.CheckedAt ?? DateTimeOffset.UtcNow));
        }
        catch (Exception exception) when (Failure(exception) is { } failure)
        {
            return new(failure.Status, failure.Message, false, 0, null, null, null);
        }
    }

    /// <summary>
    /// Exports the records the filters select, as CSV or JSON Lines, into a file the person chooses: <c>canceled</c> when they
    /// close the dialog. The server records the export in the log.
    /// </summary>
    [NeoRpcMethod("exportAudit", TimeoutMilliseconds = 600_000)]
    public async Task<FleetAuditExport> ExportAuditAsync(FleetAuditExportRequest request, CancellationToken cancellationToken)
    {
        if (Refuse(request?.ExpectedEpoch) is { } refused) return new(refused, null, null, 0);
        var query = FleetServerLinkStore.IsValidId(request!.LinkId) && FleetAuditExportFormats.IsKnown(request.Format) ? ToQuery(request.Filters) : null;
        if (query is null) return new("invalid_request", null, null, 0);
        if (ChooseSavePath is not { } choose) return new("unavailable", null, null, 0);
        var csv = request.Format == FleetAuditExportFormats.Csv;
        var name = $"codealta-audit-{DateTimeOffset.Now:yyyy-MM-dd-HHmm}.{(csv ? "csv" : "jsonl")}";
        var path = await choose("Export the audit log", name, csv ? "CSV" : "JSON Lines", csv ? "csv" : "jsonl", cancellationToken).ConfigureAwait(false);
        if (path is null) return new("canceled", null, null, 0);
        // Written beside the target and moved over it once complete: a failed export never leaves half a file under its name.
        var staging = path + "." + Guid.NewGuid().ToString("N")[..8] + ".part";
        try
        {
            long bytes;
            await using (var file = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                bytes = await _servers!.AccessCallAsync(request.LinkId, (client, token) => client.ExportAuditAsync(query, request.Format, file, token), cancellationToken)
                    .ConfigureAwait(false);
            }

            await StagedFileReplacement.MoveOverAsync(staging, path).ConfigureAwait(false);
            lock (_exportGate) _lastExport = path;
            return new("ok", null, path, Small((bytes + 1023) / 1024));
        }
        catch (Exception exception) when (exception is OperationCanceledException || Failure(exception) is not null)
        {
            TryDelete(staging);
            var (status, message) = Failure(exception) ?? ("canceled", null);
            return new(status, message, null, 0);
        }
    }

    /// <summary>Shows the last export of the audit log in the file manager of the system.</summary>
    [NeoRpcMethod("revealAuditExport")]
    public FleetSecretActionResult RevealAuditExport(FleetServerRequest request)
    {
        if (Refuse(request?.ExpectedEpoch) is { } refused) return new(refused, null);
        string? path;
        lock (_exportGate) path = _lastExport;
        if (path is null || !File.Exists(path)) return new("not_found", null);
        return RevealFile(path) ? new("ok", null) : new("failed", null);
    }

    // The filters of the page, checked as the server checks them; null when one does not hold.
    internal static FleetAuditQuery? ToQuery(FleetAuditFilters? filters)
    {
        if (filters is null) return new FleetAuditQuery();
        var values = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["action"] = filters.Actions is { Count: > 0 } actions ? string.Join(',', actions) : null,
            ["person"] = filters.PersonId, ["device"] = filters.DeviceId, ["result"] = filters.Result, ["target"] = filters.Target, ["address"] = filters.Address,
            ["from"] = filters.From, ["to"] = filters.To, ["q"] = filters.Text,
        };
        return FleetAuditQuery.TryParse(name => values.GetValueOrDefault(name), out var query, out _) ? query : null;
    }

    private static FleetAuditItem ToItem(AuditRecordDto record)
        => new(Small(record.Sequence), Iso(record.RecordedAt), record.Action, record.Result, record.ActorPersonId, record.ActorName, record.ActorDeviceId, record.DeviceName,
            record.Target, record.SourceAddress, record.RequestId, record.Details, record.Hash, record.PreviousHash);

    // The page reads numbers as JavaScript does: a sequence of the log stays far below 2^31.
    private static int Small(long value) => (int)Math.Clamp(value, 0, int.MaxValue);

    private static string Iso(DateTimeOffset time) => time.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }
}

/// <summary>The filters of the audit log the page sets; each one narrows, an absent one says nothing.</summary>
/// <param name="Actions">Actions, exact or by prefix (<c>team.</c>).</param>
/// <param name="PersonId">The person behind the records.</param>
/// <param name="DeviceId">The device that asked.</param>
/// <param name="Result"><c>success</c>, <c>failure</c> or an error code.</param>
/// <param name="Target">What the records were done to.</param>
/// <param name="Address">The address the requests came from.</param>
/// <param name="From">The first time, included, in ISO 8601.</param>
/// <param name="To">The time the records end before, excluded, in ISO 8601.</param>
/// <param name="Text">Words the records hold.</param>
internal sealed record FleetAuditFilters(IReadOnlyList<string>? Actions, string? PersonId, string? DeviceId, string? Result, string? Target, string? Address,
    string? From, string? To, string? Text);

/// <summary>A page of the audit log asked by the page.</summary>
/// <param name="ExpectedEpoch">The host epoch the page knows.</param>
/// <param name="LinkId">The link of the server.</param>
/// <param name="Filters">The filters; null for none.</param>
/// <param name="Before">The cursor of the page: records before this sequence; null for the newest.</param>
/// <param name="Limit">The size of the page; null for 100.</param>
internal sealed record FleetAuditRequest(string ExpectedEpoch, string LinkId, FleetAuditFilters? Filters, int? Before, int? Limit);

/// <summary>An export of the audit log asked by the page.</summary>
/// <param name="ExpectedEpoch">The host epoch the page knows.</param>
/// <param name="LinkId">The link of the server.</param>
/// <param name="Filters">The filters; null for the whole log.</param>
/// <param name="Format"><c>csv</c> or <c>jsonl</c>.</param>
internal sealed record FleetAuditExportRequest(string ExpectedEpoch, string LinkId, FleetAuditFilters? Filters, string Format);

/// <summary>A record of the audit log, for the timeline.</summary>
/// <param name="Sequence">Its position in the chain.</param>
/// <param name="RecordedAt">When, in ISO 8601 UTC.</param>
/// <param name="Action">What was done.</param>
/// <param name="Result"><c>success</c> or the code of a refusal.</param>
/// <param name="PersonId">The person behind it.</param>
/// <param name="PersonName">Their name as the server knows it.</param>
/// <param name="DeviceId">The device that asked.</param>
/// <param name="DeviceName">Its name as the server knows it.</param>
/// <param name="Target">What it was done to.</param>
/// <param name="Address">The address of the request.</param>
/// <param name="RequestId">The id of the request.</param>
/// <param name="Details">More facts, as JSON text, never content.</param>
/// <param name="Hash">Its hash.</param>
/// <param name="PreviousHash">The hash of the record before.</param>
internal sealed record FleetAuditItem(int Sequence, string RecordedAt, string Action, string Result, string? PersonId, string? PersonName, string? DeviceId,
    string? DeviceName, string? Target, string? Address, string? RequestId, string? Details, string Hash, string PreviousHash);

/// <summary>A page of the audit log.</summary>
/// <param name="Status"><c>ok</c>, or why not (<c>forbidden</c> for a person who may not read it).</param>
/// <param name="Message">What was said of a failure.</param>
/// <param name="Items">The records, newest first.</param>
/// <param name="Before">The cursor of the next, older page; null at the end.</param>
internal sealed record FleetAuditPage(string Status, string? Message, IReadOnlyList<FleetAuditItem> Items, int? Before)
{
    /// <summary>A page that could not be read.</summary>
    public static FleetAuditPage Failed(string status, string? message) => new(status, message, [], null);
}

/// <summary>The check of the chain of the audit log.</summary>
/// <param name="Status"><c>ok</c> when the server answered, or why not.</param>
/// <param name="Message">What was said of a failure.</param>
/// <param name="Valid">Whether the chain holds.</param>
/// <param name="Count">How many records were checked.</param>
/// <param name="BrokenAt">The first record that does not hold.</param>
/// <param name="HeadHash">The hash of the last record that holds.</param>
/// <param name="CheckedAt">When it was checked, in ISO 8601 UTC.</param>
internal sealed record FleetAuditCheck(string Status, string? Message, bool Valid, int Count, int? BrokenAt, string? HeadHash, string? CheckedAt);

/// <summary>An export of the audit log.</summary>
/// <param name="Status"><c>ok</c>, <c>canceled</c>, or why not.</param>
/// <param name="Message">What was said of a failure.</param>
/// <param name="Path">The file written.</param>
/// <param name="Kilobytes">Its size, in kilobytes rounded up.</param>
internal sealed record FleetAuditExport(string Status, string? Message, string? Path, int Kilobytes);
