using CodeAlta.Fleet.Protocol.Api;

namespace CodeAlta.Fleet.Server;

// The audit log (doc/fleet/security-and-privacy.md, audit log): read by owners, admins and auditors, exported by those with
// audit.export. Nothing here changes a record.
public sealed partial class FleetServerClient
{
    /// <summary>Reads a page of the audit log.</summary>
    /// <param name="query">The filters, the cursor and the order.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The records and the cursor of the next page.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="query"/> is null.</exception>
    /// <exception cref="FleetServerException">The server refused the call (the person may not read the log).</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public Task<ListResponse<AuditRecordDto>> ListAuditAsync(FleetAuditQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        return SendAsync(HttpMethod.Get, "api/v1/audit" + query.ToQueryString(), null, ServerApiJsonContext.Default.ListResponseAuditRecordDto, cancellationToken);
    }

    /// <summary>Checks the hash chain of the whole audit log at the server.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>Whether it holds, where it breaks, and its head.</returns>
    /// <exception cref="FleetServerException">The server refused the call.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public Task<AuditVerificationDto> VerifyAuditAsync(CancellationToken cancellationToken = default)
        => SendAsync(HttpMethod.Get, "api/v1/audit/verify", null, ServerApiJsonContext.Default.AuditVerificationDto, cancellationToken);

    /// <summary>Exports the records the filters select, oldest first, into a stream as the server writes them.</summary>
    /// <param name="query">The filters; cursors, order and limit are not those of an export.</param>
    /// <param name="format"><see cref="FleetAuditExportFormats.Csv"/> or <see cref="FleetAuditExportFormats.JsonLines"/>.</param>
    /// <param name="destination">Where the export is written.</param>
    /// <param name="cancellationToken">Cancels the export.</param>
    /// <returns>How many bytes were written.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="query"/> or <paramref name="destination"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="format"/> is not known.</exception>
    /// <exception cref="FleetServerException">The server refused the call (the person may not export the log).</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public async Task<long> ExportAuditAsync(FleetAuditQuery query, string format, Stream destination, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(destination);
        if (!FleetAuditExportFormats.IsKnown(format)) throw new ArgumentException($"'{format}' is not a format of the audit export.", nameof(format));
        var filters = (query with { After = null, Before = null, Newest = false, Limit = null }).ToQueryString();
        var path = "api/v1/audit/export?format=" + format + (filters.Length == 0 ? string.Empty : "&" + filters[1..]);
        await RenewTunnelAccessAsync(cancellationToken).ConfigureAwait(false);
        using var request = CreateSigned(HttpMethod.Get, path, null);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw Refusal(response.StatusCode, await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false));
        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var start = destination.CanSeek ? destination.Position : 0;
        long copied = 0;
        var buffer = new byte[81920];
        int read;
        while ((read = await body.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            copied += read;
        }

        return destination.CanSeek ? destination.Position - start : copied;
    }
}
