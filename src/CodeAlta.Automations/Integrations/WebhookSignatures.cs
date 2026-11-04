using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodeAlta.Automations.Integrations;

/// <summary>An event a hook of the server received, as it was pushed: what its sender signed.</summary>
/// <param name="Headers">Its headers, names in lower case.</param>
/// <param name="Body">Its body, byte for byte.</param>
internal sealed record WebhookRequest(IReadOnlyDictionary<string, string> Headers, byte[] Body)
{
    /// <summary>The value of a header, or null.</summary>
    internal string? Header(string name) => Headers.TryGetValue(name.ToLowerInvariant(), out var value) ? value : null;
}

/// <summary>
/// Checks the signature of a webhook with the secret of its sender, on the machine that runs the automation: the server
/// that received it does not hold the secret, so it cannot make an event up (<c>doc/fleet/orchestration.md</c>, "Triggers").
/// </summary>
internal static class WebhookSignatures
{
    /// <summary>How far the time a Standard Webhook was signed may be from the clock.</summary>
    internal static readonly TimeSpan Tolerance = TimeSpan.FromMinutes(5);

    /// <summary>Checks a request.</summary>
    /// <param name="scheme">The scheme (<see cref="AutomationSignatures"/>).</param>
    /// <param name="secret">The signing secret.</param>
    /// <param name="request">The request.</param>
    /// <param name="header">The header of <see cref="AutomationSignatures.HmacSha256"/>.</param>
    /// <param name="now">The clock.</param>
    /// <returns>The id of the event when it verifies; otherwise why not.</returns>
    internal static (string? EventId, string? Problem) Verify(string scheme, string secret, WebhookRequest request, string? header, DateTimeOffset now)
        => Verify(scheme, secret, request, header, now, Tolerance);

    /// <summary>
    /// Checks a request that may have waited: a Standard Webhook signed up to <paramref name="maximumAge"/> ago (its machine was
    /// offline, the server kept it), and no more than <see cref="Tolerance"/> in the future. What its machine remembers it took
    /// refuses a replay within that age (<see cref="AutomationReplayMemory"/>).
    /// </summary>
    /// <param name="scheme">The scheme (<see cref="AutomationSignatures"/>).</param>
    /// <param name="secret">The signing secret.</param>
    /// <param name="request">The request.</param>
    /// <param name="header">The header of <see cref="AutomationSignatures.HmacSha256"/>.</param>
    /// <param name="now">The clock.</param>
    /// <param name="maximumAge">How long ago a Standard Webhook may have been signed.</param>
    /// <returns>The id of the event when it verifies; otherwise why not.</returns>
    internal static (string? EventId, string? Problem) Verify(string scheme, string secret, WebhookRequest request, string? header, DateTimeOffset now, TimeSpan maximumAge)
    {
        ArgumentException.ThrowIfNullOrEmpty(secret);
        ArgumentNullException.ThrowIfNull(request);
        switch (scheme)
        {
            case AutomationSignatures.Standard:
            {
                if (request.Header("webhook-id") is not { Length: > 0 and <= 256 } id || request.Header("webhook-timestamp") is not { } stamp
                    || request.Header("webhook-signature") is not { Length: > 0 } signatures)
                    return (null, "The event has no webhook-id, webhook-timestamp and webhook-signature headers.");
                if (SignedAt(request) is not { } signedAt || now - signedAt > maximumAge || signedAt - now > Tolerance)
                    return (null, "The event was signed too long ago, or in the future.");
                var key = secret.StartsWith("whsec_", StringComparison.Ordinal) ? FromBase64(secret[6..]) : Encoding.UTF8.GetBytes(secret);
                if (key is null) return (null, "The signing secret is not the whsec_ secret of the sender.");
                byte[] signed = [.. Encoding.UTF8.GetBytes(id + "." + stamp + "."), .. request.Body];
                var expected = HMACSHA256.HashData(key, signed);
                foreach (var part in signatures.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (part.StartsWith("v1,", StringComparison.Ordinal) && FromBase64(part[3..]) is { } given && CryptographicOperations.FixedTimeEquals(given, expected))
                        return (id, null);
                }

                return (null, "The signature of the event does not verify with its signing secret.");
            }

            case AutomationSignatures.GitHub:
            {
                var given = request.Header("x-hub-signature-256");
                if (given is null || !given.StartsWith("sha256=", StringComparison.Ordinal)) return (null, "The event has no X-Hub-Signature-256 header.");
                // The signature covers the body only, not X-GitHub-Delivery: the event is what was signed, so that the same body under
                // another delivery header is the same event, not one more run (a redelivery keeps its body).
                return Matches(given[7..], HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), request.Body))
                    ? (BodyId(request.Body), null)
                    : (null, "The signature of the event does not verify with its signing secret.");
            }

            case AutomationSignatures.HmacSha256:
            {
                if (string.IsNullOrEmpty(header) || request.Header(header) is not { Length: > 0 } value) return (null, $"The event has no {header} header.");
                // Some senders write sha256=<hex> or v1=<hex>: what follows the mark is the signature.
                var equals = value.IndexOf('=');
                var text = equals is > 0 and < 8 && value[..equals].All(char.IsAsciiLetterOrDigit) ? value[(equals + 1)..] : value;
                return Matches(text.Trim(), HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), request.Body))
                    ? (BodyId(request.Body), null) : (null, "The signature of the event does not verify with its signing secret.");
            }

            default:
                return (null, $"'{scheme}' is not a signature this machine checks.");
        }
    }

    /// <summary>When a Standard Webhook was signed (its <c>webhook-timestamp</c>, which its signature covers); null when it says none.</summary>
    internal static DateTimeOffset? SignedAt(WebhookRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request.Header("webhook-timestamp") is { } stamp && long.TryParse(stamp, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            && seconds <= DateTimeOffset.MaxValue.ToUnixTimeSeconds()
            ? DateTimeOffset.FromUnixTimeSeconds(seconds) : null;
    }

    /// <summary>
    /// Reads the fields a trigger binds from the JSON body of an event, by their JSON pointers, each one line: nothing
    /// else of the body reaches the prompt.
    /// </summary>
    /// <returns>Each pointer that names a value, with that value.</returns>
    internal static IReadOnlyList<(string Pointer, string Value)> Fields(byte[] body, IReadOnlyList<string> pointers, int maximum = 300)
    {
        if (pointers.Count == 0) return [];
        JsonDocument document;
        try { document = JsonDocument.Parse(body); }
        catch (JsonException) { return []; }
        using (document)
        {
            var found = new List<(string, string)>();
            foreach (var pointer in pointers)
            {
                if (Resolve(document.RootElement, pointer) is not { } element) continue;
                var text = element.ValueKind switch
                {
                    JsonValueKind.String => element.GetString(),
                    JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => element.GetRawText(),
                    _ => null,
                };
                if (AutomationText.Line(text, maximum) is { Length: > 0 } line) found.Add((pointer, line));
            }

            return found;
        }
    }

    private static JsonElement? Resolve(JsonElement root, string pointer)
    {
        var current = root;
        foreach (var raw in pointer.Split('/').Skip(1))
        {
            var part = raw.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
            if (current.ValueKind == JsonValueKind.Object && current.TryGetProperty(part, out var next)) current = next;
            else if (current.ValueKind == JsonValueKind.Array && int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var index) && index < current.GetArrayLength()) current = current[index];
            else return null;
        }

        return current;
    }

    private static bool Matches(string text, byte[] expected)
    {
        var given = text.Length == expected.Length * 2 && text.All(char.IsAsciiHexDigit) ? Convert.FromHexString(text) : FromBase64(text);
        return given is not null && CryptographicOperations.FixedTimeEquals(given, expected);
    }

    // The same body pushed twice is the same event, for a sender that names no delivery.
    private static string BodyId(byte[] body) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(body));

    private static byte[]? FromBase64(string text)
    {
        try { return Convert.FromBase64String(text); }
        catch (FormatException) { return null; }
    }
}
