using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;

namespace CodeAlta.Automations.Integrations;

/// <summary>Who a schedule of an on-call tool says is on call: their email and name, as the tool knows them.</summary>
/// <param name="Email">Their email; null when the tool gives none.</param>
/// <param name="Name">Their name; null when the tool gives none.</param>
internal sealed record OnCallPerson(string? Email, string? Name);

/// <summary>
/// The on-call resolvers of the automations (<c>doc/fleet/orchestration.md</c>, "Autonomy and approvals"): who a schedule of
/// incident.io, PagerDuty or Opsgenie says is on call now, read with the token the machine holds as a fleet secret. The
/// server then asks that person, when they answer the approvals of the automation.
/// </summary>
internal sealed class OnCallClient
{
    /// <summary>The source of incident.io.</summary>
    internal const string IncidentIo = "incident_io";

    /// <summary>The source of PagerDuty.</summary>
    internal const string PagerDuty = "pagerduty";

    /// <summary>The source of Opsgenie.</summary>
    internal const string Opsgenie = "opsgenie";

    /// <summary>The address of the API of PagerDuty.</summary>
    internal static readonly Uri DefaultPagerDutyApi = new("https://api.pagerduty.com/");

    /// <summary>The address of the API of Opsgenie, in the United States.</summary>
    internal static readonly Uri DefaultOpsgenieApi = new("https://api.opsgenie.com/");

    /// <summary>The address of the API of Opsgenie, in Europe.</summary>
    internal static readonly Uri EuropeOpsgenieApi = new("https://api.eu.opsgenie.com/");

    /// <summary>The most people read as on call.</summary>
    internal const int MaximumPeople = 10;

    private readonly HttpClient _http;
    private readonly Uri _incident;
    private readonly Uri _pagerDuty;
    private readonly Uri? _opsgenie;

    /// <summary>Creates the client.</summary>
    /// <param name="http">The client of the host.</param>
    /// <param name="incident">The address of the API of incident.io; its default but in tests.</param>
    /// <param name="pagerDuty">The address of the API of PagerDuty; its default but in tests.</param>
    /// <param name="opsgenie">The address of the API of Opsgenie, whatever its region; null for the one of the region.</param>
    internal OnCallClient(HttpClient http, Uri? incident = null, Uri? pagerDuty = null, Uri? opsgenie = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        (_http, _incident, _pagerDuty, _opsgenie) = (http, incident ?? IncidentIoClient.DefaultApi, pagerDuty ?? DefaultPagerDutyApi, opsgenie);
    }

    /// <summary>Reads who a schedule says is on call now.</summary>
    /// <param name="source"><see cref="IncidentIo"/>, <see cref="PagerDuty"/> or <see cref="Opsgenie"/>.</param>
    /// <param name="schedule">The id of the schedule in that tool.</param>
    /// <param name="token">The token of that tool.</param>
    /// <param name="region">For Opsgenie, <c>eu</c> for its European API; null for the United States.</param>
    /// <param name="now">The time.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The people on call, or why they could not be read.</returns>
    internal Task<IntegrationResult<IReadOnlyList<OnCallPerson>>> OnCallAsync(string source, string schedule, string token, string? region, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(schedule);
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        var id = Uri.EscapeDataString(schedule);
        switch (source)
        {
            case IncidentIo:
                var start = now.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
                var end = now.ToUniversalTime().AddMinutes(1).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
                return IntegrationHttp.SendAsync(_http, new HttpRequestMessage(HttpMethod.Get,
                        new Uri(_incident, $"v2/schedule_entries?schedule_id={id}&entry_window_start={Uri.EscapeDataString(start)}&entry_window_end={Uri.EscapeDataString(end)}")).Bearer(token),
                    "incident.io", static (root, _) => People(root.Member("schedule_entries").Member("final"), static entry => entry.Member("user"), "email", "name"), cancellationToken);
            case PagerDuty:
                var pagerDuty = new HttpRequestMessage(HttpMethod.Get, new Uri(_pagerDuty, $"oncalls?schedule_ids[]={id}&include[]=users&earliest=true"));
                pagerDuty.Headers.Authorization = new AuthenticationHeaderValue("Token", "token=" + token);
                pagerDuty.Headers.Accept.ParseAdd("application/vnd.pagerduty+json;version=2");
                return IntegrationHttp.SendAsync(_http, pagerDuty, "PagerDuty",
                    static (root, _) => People(root.Member("oncalls"), static entry => entry.Member("user"), "email", "name"), cancellationToken);
            case Opsgenie:
                var api = _opsgenie ?? (string.Equals(region, "eu", StringComparison.OrdinalIgnoreCase) ? EuropeOpsgenieApi : DefaultOpsgenieApi);
                var opsgenie = new HttpRequestMessage(HttpMethod.Get, new Uri(api, $"v2/schedules/{id}/on-calls?scheduleIdentifierType=id&flat=true"));
                opsgenie.Headers.Authorization = new AuthenticationHeaderValue("GenieKey", token);
                return IntegrationHttp.SendAsync(_http, opsgenie, "Opsgenie", static (root, _) =>
                {
                    // A flat answer names the recipients by the usernames of their accounts, which are their emails.
                    var recipients = root.Member("data").Member("onCallRecipients");
                    if (recipients.ValueKind is not JsonValueKind.Array) return IntegrationResult<IReadOnlyList<OnCallPerson>>.Fail("Opsgenie did not answer who is on call.");
                    return IntegrationResult<IReadOnlyList<OnCallPerson>>.Of([.. recipients.EnumerateArray().Where(static item => item.ValueKind is JsonValueKind.String)
                        .Select(static item => item.GetString()!).Where(static text => text.Length > 0).Take(MaximumPeople)
                        .Select(static text => text.Contains('@', StringComparison.Ordinal) ? new OnCallPerson(text, null) : new OnCallPerson(null, text))]);
                }, cancellationToken);
            default:
                return Task.FromResult(IntegrationResult<IReadOnlyList<OnCallPerson>>.Fail($"'{source}' is not a source of who is on call: incident_io, pagerduty or opsgenie."));
        }
    }

    // The people of a list of entries, each with a user that has an email and a name.
    private static IntegrationResult<IReadOnlyList<OnCallPerson>> People(JsonElement entries, Func<JsonElement, JsonElement> user, string email, string name)
    {
        if (entries.ValueKind is not JsonValueKind.Array) return IntegrationResult<IReadOnlyList<OnCallPerson>>.Fail("The tool did not answer who is on call.");
        var people = new List<OnCallPerson>();
        foreach (var entry in entries.EnumerateArray())
        {
            var who = user(entry);
            var person = new OnCallPerson(Blank(who.Text(email)), Blank(who.Text(name)));
            if ((person.Email is not null || person.Name is not null) && !people.Contains(person)) people.Add(person);
            if (people.Count == MaximumPeople) break;
        }

        return IntegrationResult<IReadOnlyList<OnCallPerson>>.Of(people);
    }

    private static string? Blank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
