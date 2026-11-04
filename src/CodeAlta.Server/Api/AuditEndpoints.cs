using System.Globalization;
using System.Text;
using System.Text.Json;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Fleet.Protocol.Api;
using CodeAlta.Server.Services;
using CodeAlta.Server.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CodeAlta.Server.Api;

/// <summary>
/// The audit log on the REST API v1 (<c>doc/fleet/security-and-privacy.md</c>, audit log): pages by filters and in either
/// order for <c>audit.read</c>, the check of its chain, and exports of a filtered range in CSV or JSON Lines for
/// <c>audit.export</c>, each export audited itself. The log is append-only: nothing here changes a record.
/// </summary>
internal static class AuditEndpoints
{
    // An export reads the store a page at a time and writes each page before the next, whatever the size of the log.
    private const int ExportPage = FleetAuditQuery.MaxPage;

    public static void Map(RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);

        api.MapGet("/audit", async (HttpContext context, ICoordinatorStore store, AccessService access) =>
            {
                var actor = DeviceAuthenticationMiddleware.GetDevice(context);
                await access.RequireOrganizationAsync(actor, "audit.read", context.RequestAborted).ConfigureAwait(false);
                var query = Read(context);
                var size = Math.Clamp(query.Limit ?? 100, 1, FleetAuditQuery.MaxPage);
                var records = await store.QueryAuditAsync(query with { Limit = size }, context.RequestAborted).ConfigureAwait(false);
                var names = await AuditNames.ReadAsync(store, actor.OrganizationId, context.RequestAborted).ConfigureAwait(false);
                var next = records.Count == size ? records[^1].Sequence.ToString(CultureInfo.InvariantCulture) : null;
                return new ListResponse<AuditRecordDto>([.. records.Select(names.ToDto)], next);
            })
            .WithSummary("The audit log, filtered (action, person, device, result, target, address, from, to, q) and paged: 'after' is the cursor of the oldest first order, 'before' of the newest first (order=desc).");

        api.MapGet("/audit/verify", async (HttpContext context, ICoordinatorStore store, AccessService access, TimeProvider time) =>
            {
                await access.RequireOrganizationAsync(DeviceAuthenticationMiddleware.GetDevice(context), "audit.read", context.RequestAborted).ConfigureAwait(false);
                var verification = await store.VerifyAuditAsync(context.RequestAborted).ConfigureAwait(false);
                return new AuditVerificationDto(verification.IsValid, verification.Count, verification.BrokenAt, verification.HeadHash, time.GetUtcNow());
            })
            .WithSummary("Checks the hash chain of the whole audit log, and names the hash of its last record that holds.");

        api.MapGet("/audit/export", async (string? format, HttpContext context, ICoordinatorStore store, AccessService access, TimeProvider time) =>
            {
                var actor = DeviceAuthenticationMiddleware.GetDevice(context);
                await access.RequireOrganizationAsync(actor, "audit.export", context.RequestAborted).ConfigureAwait(false);
                var kind = format?.Trim().ToLowerInvariant() ?? FleetAuditExportFormats.JsonLines;
                if (!FleetAuditExportFormats.IsKnown(kind))
                    throw CoordinatorException.BadRequest(FleetErrorCodes.InvalidParams, "'format' is 'csv' or 'jsonl'.");
                // The cursors of a page are not those of an export: it takes the whole filtered range, oldest first.
                var query = Read(context) with { After = null, Before = null, Newest = false, Limit = ExportPage };
                await ExportAsync(context, store, actor, query, kind, time).ConfigureAwait(false);
            })
            .WithSummary("Exports the records of the audit log the filters select, oldest first, as CSV or JSON Lines (format=csv|jsonl); the export is audited.");
    }

    private static FleetAuditQuery Read(HttpContext context)
    {
        var values = context.Request.Query;
        if (!FleetAuditQuery.TryParse(name => values.TryGetValue(name, out var value) ? value.ToString() : null, out var query, out var problem))
            throw CoordinatorException.BadRequest(FleetErrorCodes.InvalidParams, problem!);
        return query!;
    }

    private static async Task ExportAsync(HttpContext context, ICoordinatorStore store, DeviceRecord actor, FleetAuditQuery query, string format, TimeProvider time)
    {
        var names = await AuditNames.ReadAsync(store, actor.OrganizationId, context.RequestAborted).ConfigureAwait(false);
        var csv = format == FleetAuditExportFormats.Csv;
        var response = context.Response;
        response.ContentType = csv ? "text/csv; charset=utf-8" : "application/jsonl; charset=utf-8";
        var file = $"codealta-audit-{time.GetUtcNow():yyyyMMdd-HHmmss}.{(csv ? "csv" : "jsonl")}";
        response.Headers.ContentDisposition = $"attachment; filename=\"{file}\"";
        response.Headers.CacheControl = "no-store";
        long count = 0;
        long? first = null, last = null;
        var result = AuditEntry.Success;
        try
        {
            // Lines end the same on every OS: CRLF in CSV (RFC 4180), LF in JSON Lines.
            await using var writer = new StreamWriter(response.Body, new UTF8Encoding(false), 64 * 1024, leaveOpen: true) { NewLine = csv ? "\r\n" : "\n" };
            if (csv) await writer.WriteLineAsync(AuditExport.CsvHeader).ConfigureAwait(false);
            var page = query;
            while (true)
            {
                var records = await store.QueryAuditAsync(page, context.RequestAborted).ConfigureAwait(false);
                foreach (var record in records)
                {
                    var dto = names.ToDto(record);
                    await writer.WriteLineAsync(csv ? AuditExport.ToCsv(dto) : JsonSerializer.Serialize(dto, ServerApiJsonContext.Default.AuditRecordDto)).ConfigureAwait(false);
                    first ??= record.Sequence;
                    last = record.Sequence;
                    count++;
                }

                await writer.FlushAsync(context.RequestAborted).ConfigureAwait(false);
                if (records.Count < ExportPage) break;
                page = page with { After = records[^1].Sequence };
            }
        }
        catch (OperationCanceledException)
        {
            result = "cancelled";
            throw;
        }
        finally
        {
            // Who exported what is part of the log: the format, the filters and the range, never a record itself.
            var details = JsonSerializer.Serialize(new AuditExportDetails(format, query.IsFiltered ? (query with { Limit = null }).ToQueryString() : null, count, first, last),
                AuditExportJsonContext.Default.AuditExportDetails);
            await store.AppendAuditAsync(ApiEndpoints.Origin(context).Audit("audit.export", actor, null, result, details), CancellationToken.None).ConfigureAwait(false);
        }
    }
}

/// <summary>The names of the people and devices of an organization, as the server knows them now, for the records of a page or an export.</summary>
internal sealed class AuditNames
{
    private readonly Dictionary<string, string> _people;
    private readonly Dictionary<string, string> _devices;

    private AuditNames(Dictionary<string, string> people, Dictionary<string, string> devices)
    {
        _people = people;
        _devices = devices;
    }

    public static async Task<AuditNames> ReadAsync(ICoordinatorStore store, string organizationId, CancellationToken cancellationToken)
    {
        var people = await store.ListPeopleAsync(organizationId, cancellationToken).ConfigureAwait(false);
        var devices = await store.ListDevicesAsync(organizationId, cancellationToken).ConfigureAwait(false);
        return new AuditNames(people.ToDictionary(static person => person.Id, static person => person.DisplayName, StringComparer.Ordinal),
            devices.ToDictionary(static device => device.Id, static device => device.Name, StringComparer.Ordinal));
    }

    public AuditRecordDto ToDto(AuditRecord record)
        => record.ToDto() with
        {
            ActorName = record.Entry.ActorPersonId is { } person && _people.TryGetValue(person, out var name) ? name : null,
            DeviceName = record.Entry.ActorDeviceId is { } device && _devices.TryGetValue(device, out var deviceName) ? deviceName : null,
        };
}

/// <summary>The CSV of an export of the audit log (RFC 4180, a header line, times in UTC ISO 8601).</summary>
internal static class AuditExport
{
    public const string CsvHeader = "sequence,recorded_at,action,result,person_id,person,device_id,device,target,source_address,request_id,details,previous_hash,hash";

    public static string ToCsv(AuditRecordDto record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var builder = new StringBuilder(256);
        builder.Append(record.Sequence.ToString(CultureInfo.InvariantCulture)).Append(',');
        builder.Append(record.RecordedAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture)).Append(',');
        Cell(builder, record.Action, true);
        Cell(builder, record.Result, true);
        Cell(builder, record.ActorPersonId, true);
        Cell(builder, record.ActorName, true);
        Cell(builder, record.ActorDeviceId, true);
        Cell(builder, record.DeviceName, true);
        Cell(builder, record.Target, true);
        Cell(builder, record.SourceAddress, true);
        Cell(builder, record.RequestId, true);
        Cell(builder, record.Details, true);
        // Hashes are base64url, which may start with '-': kept as they are, so that they compare with the chain.
        Cell(builder, record.PreviousHash, false);
        Cell(builder, record.Hash, false, last: true);
        return builder.ToString();
    }

    // A cell a spreadsheet would read as a formula (=, +, -, @, a tab or a return first) starts with an apostrophe.
    private static void Cell(StringBuilder builder, string? value, bool guard, bool last = false)
    {
        if (!string.IsNullOrEmpty(value))
        {
            var text = guard && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r' ? "'" + value : value;
            if (text.AsSpan().IndexOfAny(",\"\r\n") >= 0) builder.Append('"').Append(text.Replace("\"", "\"\"", StringComparison.Ordinal)).Append('"');
            else builder.Append(text);
        }

        if (!last) builder.Append(',');
    }
}

/// <summary>What the audit record of an export says.</summary>
/// <param name="Format">The format.</param>
/// <param name="Filters">The filters, as the query of the request; null when none.</param>
/// <param name="Records">How many records it wrote.</param>
/// <param name="First">The sequence of the first one.</param>
/// <param name="Last">The sequence of the last one.</param>
internal sealed record AuditExportDetails(string Format, string? Filters, long Records, long? First, long? Last);

[System.Text.Json.Serialization.JsonSourceGenerationOptions(PropertyNamingPolicy = System.Text.Json.Serialization.JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
[System.Text.Json.Serialization.JsonSerializable(typeof(AuditExportDetails))]
internal sealed partial class AuditExportJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
