using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodeAlta.ModelServers.Servers;

/// <summary>What the test of a server gave.</summary>
/// <param name="Passed">Whether it passed.</param>
/// <param name="Text">What the model wrote.</param>
/// <param name="TokensPerSecond">Its generation speed.</param>
/// <param name="Message">What it means, for people.</param>
public sealed record ServerTestResult(bool Passed, string? Text, double? TokensPerSecond, string Message);

/// <summary>
/// The client of a running llama-server, on loopback with its key: its health, the state of its models (the router's
/// <c>/models</c>), their requests (<c>/slots</c>), context and build (<c>/props</c>) and speed (<c>/metrics</c>), and a short
/// generation to test it. Only the model host reads <c>/metrics</c>: its gauges are averaged between two reads.
/// </summary>
public sealed partial class RouterClient
{
    private readonly HttpClient _http;
    private readonly string _base;
    private readonly string _key;

    /// <summary>Creates the client of a server.</summary>
    /// <param name="http">The HTTP client.</param>
    /// <param name="port">Its port on loopback.</param>
    /// <param name="key">Its key.</param>
    public RouterClient(HttpClient http, int port, string key)
    {
        ArgumentNullException.ThrowIfNull(http);
        _http = http;
        _base = "http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture);
        _key = key;
    }

    /// <summary>Gets the address of the server.</summary>
    public string Address => _base;

    /// <summary>Asks whether the server is up.</summary>
    /// <param name="cancellationToken">Ends the call.</param>
    /// <returns>200 when ready, 503 while it loads its model, 0 when it does not answer.</returns>
    public async Task<int> HealthAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await SendAsync(HttpMethod.Get, "/health", null, TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
            return (int)response.StatusCode;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return 0;
        }
    }

    /// <summary>Whether the key of the server is the one CodeAlta holds: a server it started.</summary>
    /// <param name="cancellationToken">Ends the call.</param>
    /// <returns>Whether it answers with the key.</returns>
    public async Task<bool> AcceptsKeyAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await SendAsync(HttpMethod.Get, "/v1/models", null, TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            return response.StatusCode != HttpStatusCode.Unauthorized && (int)response.StatusCode < 500;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    /// <summary>Reads the models of a router and where each stands.</summary>
    /// <param name="cancellationToken">Ends the call.</param>
    /// <returns>Each model with its state, progress and trained context.</returns>
    public async Task<IReadOnlyList<(string Model, string State, double? Progress, int? TrainedContext, bool Vision, bool Failed, string? Path)>> ModelsAsync(CancellationToken cancellationToken)
    {
        using var document = await GetJsonAsync("/models", cancellationToken).ConfigureAwait(false);
        var result = new List<(string, string, double?, int?, bool, bool, string?)>();
        if (document is null || !document.RootElement.TryGetProperty("data", out var data)) return result;
        foreach (var model in data.EnumerateArray())
        {
            var id = model.GetProperty("id").GetString() ?? "";
            var state = "unloaded";
            double? progress = null;
            var failed = false;
            string? path = null;
            if (model.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.Object)
            {
                // The file the router runs the model from: what names it when the router renamed its section.
                if (status.TryGetProperty("args", out var args) && args.ValueKind == JsonValueKind.Array)
                {
                    var words = args.EnumerateArray().Select(static word => word.GetString()).ToList();
                    var at = words.IndexOf("--model");
                    if (at >= 0 && at + 1 < words.Count) path = words[at + 1];
                }

                state = status.TryGetProperty("value", out var value) ? value.GetString() ?? state : state;
                failed = status.TryGetProperty("failed", out var f) && f.ValueKind == JsonValueKind.True;
                if (status.TryGetProperty("progress", out var p) && p.ValueKind == JsonValueKind.Object && p.TryGetProperty("value", out var v) && v.TryGetDouble(out var number))
                    progress = number;
            }

            int? context = model.TryGetProperty("context_length", out var length) && length.TryGetInt32(out var tokens) ? tokens : null;
            var vision = model.TryGetProperty("architecture", out var architecture) && architecture.TryGetProperty("input_modalities", out var inputs)
                && inputs.EnumerateArray().Any(static input => input.GetString() == "image");
            result.Add((id, failed ? "failed" : state, progress, context, vision, failed, path));
        }

        return result;
    }

    /// <summary>Reads the requests of a model: how many work and how many it serves at once.</summary>
    /// <param name="model">The model, or null for a one-model server.</param>
    /// <param name="cancellationToken">Ends the call.</param>
    /// <returns>The busy and total slots, and the context of a slot.</returns>
    public async Task<(int Busy, int Total, int? Context)> SlotsAsync(string? model, CancellationToken cancellationToken)
    {
        using var document = await GetJsonAsync(WithModel("/slots", model), cancellationToken).ConfigureAwait(false);
        if (document is null || document.RootElement.ValueKind != JsonValueKind.Array) return (0, 0, null);
        var slots = document.RootElement.EnumerateArray().ToList();
        var busy = slots.Count(static slot => slot.TryGetProperty("is_processing", out var p) && p.ValueKind == JsonValueKind.True);
        int? context = slots.Count > 0 && slots[0].TryGetProperty("n_ctx", out var n) && n.TryGetInt32(out var tokens) ? tokens : null;
        return (busy, slots.Count, context);
    }

    /// <summary>Reads the speed of a model from its metrics.</summary>
    /// <param name="model">The model, or null for a one-model server.</param>
    /// <param name="cancellationToken">Ends the call.</param>
    /// <returns>Its tokens per second, or null.</returns>
    public async Task<double?> TokensPerSecondAsync(string? model, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await SendAsync(HttpMethod.Get, WithModel("/metrics", model), null, TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var match = MetricPattern().Match(text);
            return match.Success && double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && value > 0 ? value : null;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>Loads a model of a router.</summary>
    /// <param name="model">The model.</param>
    /// <param name="cancellationToken">Ends the call.</param>
    /// <returns>Whether the router took it.</returns>
    public Task<bool> LoadAsync(string model, CancellationToken cancellationToken) => PostModelAsync("/models/load", model, cancellationToken);

    /// <summary>Unloads a model of a router.</summary>
    /// <param name="model">The model.</param>
    /// <param name="cancellationToken">Ends the call.</param>
    /// <returns>Whether the router took it.</returns>
    public Task<bool> UnloadAsync(string model, CancellationToken cancellationToken) => PostModelAsync("/models/unload", model, cancellationToken);

    /// <summary>Tests a model: 8 tokens at temperature 0.</summary>
    /// <param name="model">The model.</param>
    /// <param name="timeout">How long the load and the generation may take.</param>
    /// <param name="cancellationToken">Ends the test.</param>
    /// <returns>What it gave.</returns>
    public async Task<ServerTestResult> TestAsync(string model, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var body = "{\"model\":" + JsonSerializer.Serialize(model, ModelServersWireJson.Default.String)
            + ",\"messages\":[{\"role\":\"user\",\"content\":\"Reply with the single word: ready\"}],\"max_tokens\":8,\"temperature\":0,\"stream\":false}";
        try
        {
            using var response = await SendAsync(HttpMethod.Post, "/v1/chat/completions", body, timeout, cancellationToken).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return new ServerTestResult(false, null, null, $"The model answered {(int)response.StatusCode}: {text[..Math.Min(200, text.Length)]}");
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            string? content = null;
            if (root.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0 && choices[0].TryGetProperty("message", out var message))
                content = (message.TryGetProperty("content", out var c) ? c.GetString() : null) ?? (message.TryGetProperty("reasoning_content", out var r) ? r.GetString() : null);
            double? speed = root.TryGetProperty("timings", out var timings) && timings.TryGetProperty("predicted_per_second", out var s) && s.TryGetDouble(out var value) ? value : null;
            var predicted = root.TryGetProperty("usage", out var usage) && usage.TryGetProperty("completion_tokens", out var t) && t.TryGetInt32(out var tokens) ? tokens : 0;
            return predicted > 0
                ? new ServerTestResult(true, content, speed, string.Create(CultureInfo.InvariantCulture, $"{predicted} tokens at {speed ?? 0:0} tokens/s."))
                : new ServerTestResult(false, content, speed, "The model generated no token.");
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException && !cancellationToken.IsCancellationRequested)
        {
            return new ServerTestResult(false, null, null, $"The server did not answer the test: {exception.Message}");
        }
    }

    private async Task<bool> PostModelAsync(string path, string model, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await SendAsync(HttpMethod.Post, path, "{\"model\":" + JsonSerializer.Serialize(model, ModelServersWireJson.Default.String) + "}",
                TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private async Task<JsonDocument?> GetJsonAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await SendAsync(HttpMethod.Get, path, null, TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            return JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException && !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? body, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, _base + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _key);
        if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout);
        return await _http.SendAsync(request, limit.Token).ConfigureAwait(false);
    }

    private static string WithModel(string path, string? model) => model is null ? path : path + "?model=" + Uri.EscapeDataString(model);

    [GeneratedRegex(@"^llamacpp:predicted_tokens_seconds\s+([0-9.eE+\-]+)", RegexOptions.Multiline)]
    private static partial Regex MetricPattern();
}

/// <summary>The JSON of plain values on the wire.</summary>
[System.Text.Json.Serialization.JsonSerializable(typeof(string))]
internal sealed partial class ModelServersWireJson : System.Text.Json.Serialization.JsonSerializerContext;
