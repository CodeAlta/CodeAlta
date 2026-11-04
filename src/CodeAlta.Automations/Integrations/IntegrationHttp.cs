using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace CodeAlta.Automations.Integrations;

/// <summary>What a call to the API of another tool answered: a value, or what went wrong in words for the user.</summary>
/// <typeparam name="T">The value.</typeparam>
/// <param name="Value">The value; null when the call failed.</param>
/// <param name="Problem">Why it failed, never with a credential in it; null when it did not.</param>
internal readonly record struct IntegrationResult<T>(T? Value, string? Problem)
{
    /// <summary>
    /// Gets whether the failure may pass by itself: the tool did not answer in time, could not be reached, asked to slow down
    /// or failed on its side (5xx). A call refused for what it asks (not found, not allowed) is not.
    /// </summary>
    public bool Transient { get; init; }

    /// <summary>A result that holds a value.</summary>
    internal static IntegrationResult<T> Of(T value) => new(value, null);

    /// <summary>A result that failed.</summary>
    internal static IntegrationResult<T> Fail(string problem) => new(default, problem);

    /// <summary>A result that failed for now: the same call may succeed later.</summary>
    internal static IntegrationResult<T> FailForNow(string problem) => new(default, problem) { Transient = true };
}

/// <summary>The requests the clients of other tools share: JSON in and out, bounded answers, problems without credentials.</summary>
internal static class IntegrationHttp
{
    /// <summary>The largest answer read from another tool.</summary>
    internal const int MaximumAnswerBytes = 4 * 1024 * 1024;

    /// <summary>How long one request waits for its answer.</summary>
    internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>Sends a request and reads its JSON answer.</summary>
    /// <param name="http">The client.</param>
    /// <param name="request">The request, with its credentials.</param>
    /// <param name="service">The name of the tool, for the problems.</param>
    /// <param name="read">Reads the value of the answer.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The value, or the problem.</returns>
    internal static async Task<IntegrationResult<T>> SendAsync<T>(HttpClient http, HttpRequestMessage request, string service, Func<JsonElement, HttpStatusCode, IntegrationResult<T>> read,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            var bytes = await ReadBoundedAsync(response.Content, timeout.Token).ConfigureAwait(false);
            if (bytes is null) return IntegrationResult<T>.Fail($"{service} answered more than can be read.");
            JsonElement root = default;
            if (bytes.Length > 0)
            {
                try
                {
                    using var document = JsonDocument.Parse(bytes);
                    root = document.RootElement.Clone();
                }
                catch (JsonException)
                {
                    if (response.IsSuccessStatusCode) return IntegrationResult<T>.Fail($"{service} answered something that is not JSON.");
                }
            }

            if (!response.IsSuccessStatusCode)
            {
                return response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.RequestTimeout || (int)response.StatusCode >= 500
                    ? IntegrationResult<T>.FailForNow(Refusal(service, response.StatusCode, root))
                    : IntegrationResult<T>.Fail(Refusal(service, response.StatusCode, root));
            }

            return read(root, response.StatusCode);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return IntegrationResult<T>.FailForNow($"{service} did not answer within {Timeout.TotalSeconds:0} seconds.");
        }
        catch (HttpRequestException exception)
        {
            return IntegrationResult<T>.FailForNow($"{service} could not be reached: {Line(exception.Message)}");
        }
    }

    /// <summary>A request with a JSON body.</summary>
    internal static HttpRequestMessage Json(HttpMethod method, Uri uri, string json)
        => new(method, uri) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    /// <summary>Sets a bearer token on a request.</summary>
    internal static HttpRequestMessage Bearer(this HttpRequestMessage request, string token)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    /// <summary>Reads a text member, or null.</summary>
    internal static string? Text(this JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>Reads a member, or an undefined element.</summary>
    internal static JsonElement Member(this JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value : default;

    /// <summary>Reads a true or false member.</summary>
    internal static bool Flag(this JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    /// <summary>One line of a message, at most 300 characters.</summary>
    internal static string Line(string? text) => AutomationText.Line(text, 300);

    private static string Refusal(string service, HttpStatusCode status, JsonElement root)
    {
        var code = (int)status;
        // Jira says it in errorMessages, incident.io in errors, Slack and others in message.
        var said = root.Text("message");
        if (said is null && root.Member("errorMessages") is { ValueKind: JsonValueKind.Array } messages && messages.GetArrayLength() > 0) said = messages[0].ValueKind == JsonValueKind.String ? messages[0].GetString() : null;
        if (said is null && root.Member("errors") is { ValueKind: JsonValueKind.Array } errors && errors.GetArrayLength() > 0) said = errors[0].Text("message");
        var reason = status switch
        {
            HttpStatusCode.Unauthorized => "the credential was refused",
            HttpStatusCode.Forbidden => "the account may not do this",
            HttpStatusCode.NotFound => "it was not found",
            HttpStatusCode.TooManyRequests => "too many requests: it is tried again later",
            _ => "the request failed",
        };
        return $"{service} answered {code}: {reason}" + (said is { Length: > 0 } ? $" ({Line(said)})." : ".");
    }

    private static async Task<byte[]?> ReadBoundedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is > MaximumAnswerBytes) return null;
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaximumAnswerBytes) return null;
            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }
}
