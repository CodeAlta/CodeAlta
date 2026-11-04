using System.Globalization;
using System.Text;
using System.Text.Json;

namespace CodeAlta.Automations.Integrations;

/// <summary>An incident of incident.io, as its API reads it.</summary>
/// <param name="Id">Its id.</param>
/// <param name="Reference">Its reference, such as <c>INC-123</c>.</param>
/// <param name="Name">Its name.</param>
/// <param name="Summary">Its summary; null without one.</param>
/// <param name="Status">The name of its status.</param>
/// <param name="Severity">The name of its severity.</param>
/// <param name="Permalink">Its address on the web.</param>
internal sealed record IncidentIoIncident(string Id, string? Reference, string? Name, string? Summary, string? Status, string? Severity, string? Permalink);

/// <summary>
/// The API v2 of incident.io for the triggers: an incident read again by its id (the copy the prompt uses, not the one a
/// webhook carried), the latest one as a sample, and a custom item on an incident's timeline for the reply.
/// </summary>
internal sealed class IncidentIoClient
{
    /// <summary>The address of the API.</summary>
    internal static readonly Uri DefaultApi = new("https://api.incident.io/");

    private const string Service = "incident.io";
    private readonly HttpClient _http;
    private readonly Uri _api;

    /// <summary>Creates the client.</summary>
    /// <param name="http">The client of the host.</param>
    /// <param name="api">The address of the API; <see cref="DefaultApi"/> but in tests.</param>
    internal IncidentIoClient(HttpClient http, Uri? api = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        (_http, _api) = (http, api ?? DefaultApi);
    }

    /// <summary>Reads an incident by its id.</summary>
    internal Task<IntegrationResult<IncidentIoIncident>> IncidentAsync(string apiKey, string id, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return IntegrationHttp.SendAsync(_http, new HttpRequestMessage(HttpMethod.Get, new Uri(_api, "v2/incidents/" + Uri.EscapeDataString(id))).Bearer(apiKey), Service,
            static (root, _) => Read(root.Member("incident")) is { } incident ? IntegrationResult<IncidentIoIncident>.Of(incident)
                : IntegrationResult<IncidentIoIncident>.Fail("incident.io did not answer an incident."), cancellationToken);
    }

    /// <summary>Reads the latest incident: the connection test and the sample of the editor.</summary>
    /// <returns>The incident; a value with an empty id when the organization has none yet.</returns>
    internal Task<IntegrationResult<IncidentIoIncident>> LatestAsync(string apiKey, CancellationToken cancellationToken)
        => IntegrationHttp.SendAsync(_http, new HttpRequestMessage(HttpMethod.Get, new Uri(_api, "v2/incidents?page_size=1")).Bearer(apiKey), Service, static (root, _) =>
            root.Member("incidents") is { ValueKind: JsonValueKind.Array } incidents
                ? IntegrationResult<IncidentIoIncident>.Of(incidents.GetArrayLength() > 0 && Read(incidents[0]) is { } first ? first : new IncidentIoIncident(string.Empty, null, null, null, null, null, null))
                : IntegrationResult<IncidentIoIncident>.Fail("incident.io did not answer a list of incidents."), cancellationToken);

    /// <summary>Adds a custom item to the timeline of an incident; the same key never adds it twice.</summary>
    /// <returns>The id of the item.</returns>
    internal Task<IntegrationResult<string>> AddTimelineItemAsync(string apiKey, string incidentId, string title, string description, string idempotencyKey,
        DateTimeOffset timestamp, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("incident_id", incidentId);
            writer.WriteString("timestamp", timestamp.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture));
            writer.WriteString("title", title);
            writer.WriteString("description", description);
            writer.WriteString("idempotency_key", idempotencyKey);
            writer.WriteEndObject();
        }

        var request = IntegrationHttp.Json(HttpMethod.Post, new Uri(_api, "v2/incident_timeline_items"), Encoding.UTF8.GetString(buffer.ToArray())).Bearer(apiKey);
        return IntegrationHttp.SendAsync(_http, request, Service, static (root, _) => IntegrationResult<string>.Of(root.Member("incident_timeline_item").Text("id") ?? string.Empty),
            cancellationToken);
    }

    /// <summary>The id of the incident an event of a webhook names, and its type: <c>public_incident.incident_created_v2</c> gives <c>created</c>.</summary>
    /// <returns>The id and whether it was declared (rather than updated); null for another event.</returns>
    internal static (string Id, bool Declared)? ReadEvent(JsonElement body)
    {
        var type = body.Text("event_type");
        if (type is null) return null;
        var declared = type.Contains("incident_created", StringComparison.Ordinal);
        if (!declared && !type.Contains("incident_updated", StringComparison.Ordinal) && !type.Contains("incident_status_updated", StringComparison.Ordinal)) return null;
        return body.Member(type).Text("id") is { Length: > 0 } id ? (id, declared) : null;
    }

    private static IncidentIoIncident? Read(JsonElement incident)
        => incident.Text("id") is { Length: > 0 } id
            ? new IncidentIoIncident(id, incident.Text("reference"), incident.Text("name"), incident.Text("summary"), incident.Member("incident_status").Text("name"),
                incident.Member("severity").Text("name"), incident.Text("permalink"))
            : null;
}
