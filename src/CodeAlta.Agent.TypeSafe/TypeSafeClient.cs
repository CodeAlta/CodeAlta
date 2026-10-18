using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodeAlta.Agent.TypeSafe;

/// <summary>
/// The answers of one System One evaluation.
/// </summary>
/// <param name="Model">The versioned model that answered, such as <c>jev-1.13.0</c>.</param>
/// <param name="Answers">One typed answer by question id, as the API wrote it.</param>
/// <param name="InputTokens">The input tokens charged, when the API said.</param>
/// <param name="OutputTokens">The output tokens, when the API said (they are free).</param>
public sealed record TypeSafeEvaluation(string? Model, JsonObject Answers, long? InputTokens, long? OutputTokens);

/// <summary>
/// One model name an account can ask for.
/// </summary>
/// <param name="Name">The name to give as <c>model</c>, such as <c>jev-latest</c>.</param>
/// <param name="Description">What the API says of it.</param>
/// <param name="ReleaseDate">Its release date, as the API wrote it.</param>
public sealed record TypeSafeModel(string Name, string? Description, string? ReleaseDate);

/// <summary>
/// A client of the TypeSafe AI System One API: typed questions (noul, choice, score) about a state, answered with
/// probabilities. It generates no text and runs no conversation.
/// </summary>
/// <remarks>
/// The <see cref="HttpClient"/> is borrowed. A request the service refuses for its rate (429) or its load (529) is
/// sent again a few times, after the delay the service asks for or a growing one.
/// </remarks>
public sealed class TypeSafeClient
{
    private const int MaximumAttempts = 3;
    private const int MaximumErrorCharacters = 600;
    private static readonly TimeSpan MaximumRetryDelay = TimeSpan.FromSeconds(10);

    private readonly HttpClient _httpClient;
    private readonly string _apiKey;
    private readonly Uri _baseUri;
    private readonly TimeSpan _timeout;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    /// <summary>
    /// Creates a client of one account.
    /// </summary>
    /// <param name="httpClient">The borrowed HTTP client.</param>
    /// <param name="apiKey">The API key of the account.</param>
    /// <param name="baseUri">The address of the API; <see langword="null"/> for <see cref="TypeSafeDefaults.BaseUrl"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="httpClient"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="apiKey"/> is blank, or <paramref name="baseUri"/> is not absolute.</exception>
    public TypeSafeClient(HttpClient httpClient, string apiKey, Uri? baseUri)
        : this(httpClient, apiKey, baseUri, TimeSpan.FromSeconds(60), static (delay, token) => Task.Delay(delay, token))
    {
    }

    internal TypeSafeClient(HttpClient httpClient, string apiKey, Uri? baseUri, TimeSpan timeout, Func<TimeSpan, CancellationToken, Task> delay)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        ArgumentNullException.ThrowIfNull(delay);
        if (baseUri is { IsAbsoluteUri: false })
        {
            throw new ArgumentException("The address of the TypeSafe API must be absolute.", nameof(baseUri));
        }

        _httpClient = httpClient;
        _apiKey = apiKey.Trim();
        _baseUri = baseUri ?? new Uri(TypeSafeDefaults.BaseUrl);
        _timeout = timeout;
        _delay = delay;
    }

    /// <summary>
    /// Asks typed questions about a state.
    /// </summary>
    /// <param name="model">The model to ask, such as <c>jev-latest</c>.</param>
    /// <param name="state">What the questions are about: a text, an object or an array. It is copied.</param>
    /// <param name="questions">The questions by id, checked with <see cref="TypeSafeQuestions.TryValidate"/>. They are copied.</param>
    /// <param name="cancellationToken">Cancels the request and its retries.</param>
    /// <returns>The typed answers.</returns>
    /// <exception cref="ArgumentException">The model is blank, or the state or the questions break a rule of the API.</exception>
    /// <exception cref="TypeSafeApiException">The service refused the request, failed, could not be reached or timed out.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    public async Task<TypeSafeEvaluation> EvaluateAsync(string model, JsonNode state, JsonObject questions, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        if (!TypeSafeQuestions.TryValidate(state, questions, out var error))
        {
            throw new ArgumentException(error);
        }

        var body = new JsonObject
        {
            ["model"] = model.Trim(),
            ["state"] = state.DeepClone(),
            ["questions"] = questions.DeepClone(),
        }.ToJsonString();
        var document = await SendAsync(
            () => new HttpRequestMessage(HttpMethod.Post, Combine(TypeSafeDefaults.SystemOnePath)) { Content = new StringContent(body, Encoding.UTF8, "application/json") },
            cancellationToken).ConfigureAwait(false);
        if (document is not JsonObject result || result["answers"] is not JsonObject answers)
        {
            throw new TypeSafeApiException(HttpStatusCode.OK, "The TypeSafe API answered without `answers`.");
        }

        var usage = result["usage"] as JsonObject;
        return new TypeSafeEvaluation(Text(result["model"]), (JsonObject)answers.DeepClone(), Number(usage?["input_tokens"]), Number(usage?["output_tokens"]));
    }

    /// <summary>
    /// Lists the model names the account can ask for. The request is not charged: it tests the key and the endpoint.
    /// </summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The names, in the order of the API.</returns>
    /// <exception cref="TypeSafeApiException">The service refused the request, failed, could not be reached or timed out.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    public async Task<IReadOnlyList<TypeSafeModel>> ListModelsAsync(CancellationToken cancellationToken)
    {
        var document = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, Combine(TypeSafeDefaults.ModelsPath)), cancellationToken).ConfigureAwait(false);
        // The list is the document itself or its `data`/`models`, as list endpoints write it.
        var list = document as JsonObject;
        var entries = document as JsonArray ?? list?["data"] as JsonArray ?? list?["models"] as JsonArray ?? [];
        return [.. entries.OfType<JsonObject>()
            .Select(static entry => new TypeSafeModel(Text(entry["name"]) ?? Text(entry["id"]) ?? string.Empty, Text(entry["description"]), Text(entry["release_date"])))
            .Where(static entry => entry.Name.Length > 0)];
    }

    // The base address keeps its own path: `https://host/proxy` gives `https://host/proxy/v1/systemone`.
    internal Uri Combine(string path)
        => new(_baseUri.GetLeftPart(UriPartial.Path).TrimEnd('/') + path + _baseUri.Query);

    private async Task<JsonNode?> SendAsync(Func<HttpRequestMessage> createRequest, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_timeout);
            using var request = createRequest();
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            HttpResponseMessage response;
            string text;
            try
            {
                response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token).ConfigureAwait(false);
                text = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TypeSafeApiException(null, $"The TypeSafe API did not answer within {_timeout.TotalSeconds.ToString(CultureInfo.InvariantCulture)} seconds.", exception);
            }
            catch (HttpRequestException exception)
            {
                throw new TypeSafeApiException(null, $"The TypeSafe API could not be reached: {exception.Message}", exception);
            }

            using (response)
            {
                if (response.IsSuccessStatusCode)
                {
                    try
                    {
                        return JsonNode.Parse(text);
                    }
                    catch (JsonException exception)
                    {
                        throw new TypeSafeApiException(response.StatusCode, "The TypeSafe API answered with a body that is not JSON.", exception);
                    }
                }

                var busy = response.StatusCode is HttpStatusCode.TooManyRequests || (int)response.StatusCode == 529;
                if (!busy || attempt >= MaximumAttempts)
                {
                    throw new TypeSafeApiException(response.StatusCode, Describe(response.StatusCode, text));
                }

                await _delay(RetryDelay(response, attempt), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static TimeSpan RetryDelay(HttpResponseMessage response, int attempt)
    {
        var asked = response.Headers.RetryAfter is { } retryAfter
            ? retryAfter.Delta ?? (retryAfter.Date is { } date ? date - DateTimeOffset.UtcNow : null)
            : null;
        var delay = asked is { } value && value > TimeSpan.Zero ? value : TimeSpan.FromMilliseconds(500 * Math.Pow(2, attempt - 1));
        return delay > MaximumRetryDelay ? MaximumRetryDelay : delay;
    }

    private static string Describe(HttpStatusCode status, string body)
    {
        var detail = Detail(body);
        var meaning = (int)status switch
        {
            400 => "the request breaks a rule of the API",
            401 => "the API key is missing or invalid",
            403 => "the API key may not do this",
            413 => "the request is larger than 32 MiB",
            415 => "the encoding of the request is not supported",
            422 => "a question or a field is not valid",
            429 => "the rate limit is exceeded",
            529 => "the service is overloaded",
            >= 500 => "the service failed",
            _ => "the request was refused",
        };
        return $"TypeSafe API {((int)status).ToString(CultureInfo.InvariantCulture)}: {meaning}{(detail is null ? "." : $": {detail}")}";
    }

    // The detail the API gives: the message of its JSON error, else the start of its body.
    private static string? Detail(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        string? detail = null;
        try
        {
            if (JsonNode.Parse(body) is JsonObject node)
            {
                // The API writes `{"detail": {"error_type": ..., "message": ...}}`; other services write `error.message`.
                detail = Text((node["detail"] as JsonObject)?["message"]) ?? Text((node["error"] as JsonObject)?["message"])
                    ?? Text(node["message"]) ?? Text(node["detail"]) ?? Text(node["error"]) ?? node["detail"]?.ToJsonString();
            }
        }
        catch (JsonException)
        {
            // Not JSON: the body itself is the detail.
        }

        detail = (detail ?? body).Trim();
        return detail.Length <= MaximumErrorCharacters ? detail : detail[..MaximumErrorCharacters] + "…";
    }

    private static string? Text(JsonNode? node)
        => node is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;

    private static long? Number(JsonNode? node)
        => node is JsonValue value && value.GetValueKind() == JsonValueKind.Number && value.TryGetValue<long>(out var number) ? number : null;
}
