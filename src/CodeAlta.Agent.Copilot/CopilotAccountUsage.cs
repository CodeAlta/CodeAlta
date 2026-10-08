using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace CodeAlta.Agent.Copilot;

/// <summary>
/// Reads what a GitHub Copilot subscription has left: the quotas of its billing period (premium requests or AI
/// credits, and chat and completions for a free plan), from the entitlement GitHub gives the signed-in user. It is
/// the answer the Copilot extension of an editor and the Copilot CLI show.
/// </summary>
public static class CopilotAccountUsage
{
    /// <summary>The quota of the requests or credits a plan includes in a billing period.</summary>
    public const string PremiumInteractions = "premium_interactions";

    private static readonly string[] KnownOrder = [PremiumInteractions, "chat", "completions"];

    /// <summary>Asks GitHub for the quotas of the account a Copilot provider is signed in with.</summary>
    /// <param name="httpClient">The client that sends the request.</param>
    /// <param name="stateRootPath">The global state root that holds the sign-in of the provider.</param>
    /// <param name="providerKey">The key of the provider.</param>
    /// <param name="enterpriseDomain">The GitHub Enterprise host of the provider, or null for github.com.</param>
    /// <param name="gitHubTokenEnvironmentVariable">The environment variable that holds the GitHub token, for a provider that signs in with one.</param>
    /// <param name="cancellationToken">Stops the request.</param>
    /// <returns>The quotas; signed out without a GitHub token or when GitHub refuses it; not available for an account without Copilot.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="httpClient"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="providerKey"/> is blank.</exception>
    /// <exception cref="HttpRequestException">GitHub could not be reached or answered with an error.</exception>
    /// <exception cref="JsonException">The answer is not the expected JSON.</exception>
    /// <exception cref="OperationCanceledException">The request was canceled.</exception>
    public static async Task<AgentSubscriptionUsageReading> ReadAsync(
        HttpClient httpClient,
        string? stateRootPath,
        string providerKey,
        string? enterpriseDomain,
        string? gitHubTokenEnvironmentVariable,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerKey);

        var token = string.IsNullOrWhiteSpace(gitHubTokenEnvironmentVariable) ? null : Environment.GetEnvironmentVariable(gitHubTokenEnvironmentVariable.Trim());
        if (string.IsNullOrWhiteSpace(token))
        {
            var cache = await new CopilotDirectCredentialStore(stateRootPath, providerKey).ReadAsync(cancellationToken).ConfigureAwait(false);
            token = cache?.GitHubToken;
            enterpriseDomain ??= cache?.EnterpriseDomain;
        }

        if (string.IsNullOrWhiteSpace(token))
        {
            return AgentSubscriptionUsageReading.NotSignedIn;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, CreateUri(enterpriseDomain));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Trim());
        CopilotDirectHeaders.ApplyStaticHeaders(request.Headers);
        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.Unauthorized)
        {
            return AgentSubscriptionUsageReading.NotSignedIn;
        }

        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
        {
            return AgentSubscriptionUsageReading.NotAvailable;
        }

        response.EnsureSuccessStatusCode();
        var usage = Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false), DateTimeOffset.UtcNow);
        return usage.Limits.Count == 0 ? AgentSubscriptionUsageReading.NotAvailable : new(AgentSubscriptionUsageReading.Ok, usage);
    }

    internal static Uri CreateUri(string? enterpriseDomain)
    {
        var host = enterpriseDomain?.Trim() ?? string.Empty;
        foreach (var scheme in (string[])["https://", "http://"])
        {
            if (host.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
            {
                host = host[scheme.Length..];
            }
        }

        host = host.Trim('/');
        var api = host.Length == 0 || string.Equals(host, "github.com", StringComparison.OrdinalIgnoreCase) ? "api.github.com" : "api." + host;
        return new Uri($"https://{api}/copilot_internal/user");
    }

    /// <summary>Reads the quotas of an entitlement answer. Fields come and go with the plans: what is missing is left out.</summary>
    internal static AgentSubscriptionUsage Parse(string json, DateTimeOffset now)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("The entitlement is not an object.");
        }

        var plan = Text(root, "copilot_plan");
        var credits = Flag(root, "token_based_billing") == true;
        var reset = Date(root, "quota_reset_date_utc") ?? Date(root, "quota_reset_date") ?? Date(root, "limited_user_reset_date");
        var limits = new List<AgentSubscriptionLimit>();
        if (root.TryGetProperty("quota_snapshots", out var snapshots) && snapshots.ValueKind == JsonValueKind.Object)
        {
            var names = snapshots.EnumerateObject().Select(static property => property.Name).ToList();
            foreach (var name in KnownOrder.Where(names.Contains).Concat(names.Except(KnownOrder).Order(StringComparer.Ordinal)))
            {
                if (Snapshot(name, snapshots.GetProperty(name), reset, credits) is { } limit)
                {
                    limits.Add(limit);
                }
            }
        }
        else if (root.TryGetProperty("monthly_quotas", out var monthly) && monthly.ValueKind == JsonValueKind.Object)
        {
            // A free plan: what a month includes, and what is left of it.
            root.TryGetProperty("limited_user_quotas", out var left);
            foreach (var name in (string[])["chat", "completions"])
            {
                if (Number(monthly, name) is not { } total || total <= 0)
                {
                    continue;
                }

                var remaining = left.ValueKind == JsonValueKind.Object ? Number(left, name) ?? total : total;
                var used = Math.Clamp(total - remaining, 0, total);
                limits.Add(new AgentSubscriptionLimit(name, UsedPercent: used / total * 100, ResetsAt: reset, WindowMinutes: Period(reset), Used: used, Total: total, Unit: "requests"));
            }
        }

        return new AgentSubscriptionUsage(plan, limits, now);
    }

    private static AgentSubscriptionLimit? Snapshot(string name, JsonElement snapshot, DateTimeOffset? reset, bool credits)
    {
        if (snapshot.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var total = Number(snapshot, "entitlement");
        var unlimited = Flag(snapshot, "unlimited") == true || total < 0;
        if (unlimited)
        {
            return new AgentSubscriptionLimit(name, Unlimited: true);
        }

        // A plan that includes none of it has nothing to show.
        if (total is not > 0)
        {
            return null;
        }

        var inCredits = Flag(snapshot, "token_based_billing") ?? credits;
        var remaining = Number(snapshot, "quota_remaining") ?? Number(snapshot, "remaining");
        var used = inCredits && Number(snapshot, "credits_used") is { } spent ? spent : remaining is { } rest ? total.Value - rest : (double?)null;
        var percent = Number(snapshot, "percent_remaining") is { } free ? 100 - free : used is { } amount ? amount / total.Value * 100 : (double?)null;
        var at = Number(snapshot, "quota_reset_at") is > 0 and var seconds ? DateTimeOffset.FromUnixTimeSeconds((long)seconds) : reset;
        return new AgentSubscriptionLimit(
            name,
            UsedPercent: percent is { } value ? Math.Max(0, value) : null,
            ResetsAt: at,
            WindowMinutes: Period(at),
            Used: used is { } count ? Math.Max(0, Math.Round(count)) : null,
            Total: total,
            Unit: inCredits ? "credits" : "requests");
    }

    // The billing period that ends at the reset: one month.
    private static long? Period(DateTimeOffset? reset)
        => reset is { } end ? (long)(end - end.AddMonths(-1)).TotalMinutes : null;

    private static string? Text(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 and <= 128 } text ? text : null;

    private static bool? Flag(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) ? value.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => null } : null;

    // A count is a number, or the same number as text.
    private static double? Number(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetDouble(out var number) && double.IsFinite(number) => number,
            JsonValueKind.String when double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number) => number,
            _ => null,
        };
    }

    private static DateTimeOffset? Date(JsonElement element, string name)
        => Text(element, name) is { } text && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date) ? date : null;
}
