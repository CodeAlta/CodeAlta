using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodeAlta.Agent.Runtime;

namespace CodeAlta.Agent.Mistral;

/// <summary>
/// Where a Mistral Vibe sign-in keeps its key, and where it signs in.
/// </summary>
/// <param name="ProviderKey">The key of the configured provider, which names its credential file.</param>
/// <param name="StateRootPath">The state root that holds the credentials of the providers; null for the default one.</param>
/// <param name="ConsoleBaseUri">The console that signs in; null for <c>https://console.mistral.ai</c>.</param>
public sealed record MistralVibeLoginOptions(string ProviderKey, string? StateRootPath, Uri? ConsoleBaseUri = null);

/// <summary>What the user opens to sign in to Mistral Vibe.</summary>
/// <param name="SignInUri">The page of the sign-in, in the browser.</param>
/// <param name="ExpiresAt">When the sign-in expires.</param>
public sealed record MistralVibeBrowserAuthorization(Uri SignInUri, DateTimeOffset ExpiresAt);

/// <summary>The non-secret state of a Mistral Vibe sign-in.</summary>
/// <param name="SignedInAt">When the key was stored.</param>
public sealed record MistralVibeLoginResult(DateTimeOffset SignedInAt);

/// <summary>
/// Signs in to Mistral Vibe as its CLI does: the browser signs in on the console of Mistral, and the sign-in gives an
/// API key of the workspace chosen there, which the requests use as any key of Mistral. The key does not expire.
/// </summary>
public sealed class MistralVibeLoginManager
{
    /// <summary>The console that signs in by default.</summary>
    public static readonly Uri DefaultConsoleBaseUri = new("https://console.mistral.ai");

    // The error of a workspace whose keys the user may not create (Mistral Vibe 2.26).
    private const int WorkspaceNotEligibleErrorCode = 9004;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);

    private readonly HttpClient _httpClient;

    /// <summary>Initializes a new instance of the <see cref="MistralVibeLoginManager"/> class.</summary>
    /// <param name="httpClient">The client of the requests of the sign-in.</param>
    /// <exception cref="ArgumentNullException"><paramref name="httpClient"/> is null.</exception>
    public MistralVibeLoginManager(HttpClient httpClient)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        _httpClient = httpClient;
    }

    internal Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = Task.Delay;

    internal Func<DateTimeOffset> Now { get; init; } = static () => DateTimeOffset.UtcNow;

    /// <summary>
    /// Signs in in the browser and stores the key it gives.
    /// </summary>
    /// <param name="options">Where the key goes and where to sign in.</param>
    /// <param name="onAuthorize">Receives the page to open, and is awaited before the sign-in waits for the user.</param>
    /// <param name="cancellationToken">Cancels the sign-in.</param>
    /// <returns>The state of the sign-in once the key is stored.</returns>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="InvalidOperationException">The console refuses the sign-in, denies it, or answers what is not a sign-in.</exception>
    /// <exception cref="TimeoutException">The sign-in expires before the user completes it.</exception>
    /// <exception cref="HttpRequestException">The console cannot be reached.</exception>
    /// <exception cref="IOException">The key cannot be stored.</exception>
    /// <exception cref="OperationCanceledException">The sign-in is canceled.</exception>
    public async ValueTask<MistralVibeLoginResult> LoginWithBrowserAsync(
        MistralVibeLoginOptions options,
        Func<MistralVibeBrowserAuthorization, CancellationToken, ValueTask> onAuthorize,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(onAuthorize);

        var console = options.ConsoleBaseUri ?? DefaultConsoleBaseUri;
        var api = new Uri($"{console.AbsoluteUri.TrimEnd('/')}/api");
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(64));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

        var process = await StartAsync(api, console, challenge, cancellationToken).ConfigureAwait(false);
        await onAuthorize(new MistralVibeBrowserAuthorization(process.SignInUri, process.ExpiresAt), cancellationToken).ConfigureAwait(false);
        var exchangeToken = await WaitForCompletionAsync(api, process, cancellationToken).ConfigureAwait(false);
        var apiKey = await ExchangeAsync(api, process.Id, exchangeToken, verifier, cancellationToken).ConfigureAwait(false);

        var signedInAt = Now();
        await new MistralVibeCredentialStore(options).WriteAsync(new MistralVibeCredential { ApiKey = apiKey, SignedInAt = signedInAt }, cancellationToken).ConfigureAwait(false);
        return new MistralVibeLoginResult(signedInAt);
    }

    /// <summary>Reads the stored sign-in, without a request.</summary>
    /// <param name="options">Where the key is.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The state of the sign-in; null when no key is stored.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    public static async ValueTask<MistralVibeLoginResult?> GetCredentialStatusAsync(MistralVibeLoginOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        return await new MistralVibeCredentialStore(options).ReadAsync(cancellationToken).ConfigureAwait(false) is { ApiKey.Length: > 0 } credential
            ? new MistralVibeLoginResult(credential.SignedInAt)
            : null;
    }

    /// <summary>Signs out: deletes the stored key. The key stays in the workspace, where the console of Mistral revokes it.</summary>
    /// <param name="options">Where the key is.</param>
    /// <param name="cancellationToken">Cancels the deletion.</param>
    /// <returns>A task that completes once the key is deleted.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    /// <exception cref="IOException">The file of the key cannot be deleted.</exception>
    public static ValueTask DeleteCredentialAsync(MistralVibeLoginOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new MistralVibeCredentialStore(options).DeleteAsync(cancellationToken);
    }

    /// <summary>Returns the stored key, for the requests of the provider.</summary>
    /// <param name="options">Where the key is.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The API key the sign-in gave.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    /// <exception cref="InvalidOperationException">No key is stored: the provider is not signed in.</exception>
    public static async ValueTask<string> GetApiKeyAsync(MistralVibeLoginOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        return await new MistralVibeCredentialStore(options).ReadAsync(cancellationToken).ConfigureAwait(false) is { ApiKey: { Length: > 0 } apiKey }
            ? apiKey
            : throw new InvalidOperationException("Mistral Vibe is not signed in. Sign in from the form of the provider, then test it again.");
    }

    private async Task<SignInProcess> StartAsync(Uri api, Uri console, string challenge, CancellationToken cancellationToken)
    {
        const string Failure = "The sign-in to Mistral Vibe could not start";
        using var response = await _httpClient.PostAsJsonAsync(
            $"{api.AbsoluteUri}/vibe/sign-in",
            new StartRequest { CodeChallenge = challenge, CodeChallengeMethod = "S256" },
            MistralVibeJsonContext.Default.StartRequest,
            cancellationToken).ConfigureAwait(false);
        var payload = await ReadAsync(response, MistralVibeJsonContext.Default.StartResponse, Failure, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(payload.ProcessId) ||
            !DateTimeOffset.TryParse(payload.ExpiresAt, out var expiresAt) ||
            Under(payload.SignInUrl, console) is not { } signInUri ||
            Under(payload.PollUrl, api) is not { } pollUri)
        {
            throw new InvalidOperationException($"{Failure}: the console answered what is not a sign-in.");
        }

        return new SignInProcess(payload.ProcessId, signInUri, pollUri, expiresAt);
    }

    private async Task<string> WaitForCompletionAsync(Uri api, SignInProcess process, CancellationToken cancellationToken)
    {
        while (Now() < process.ExpiresAt)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var response = await _httpClient.GetAsync(process.PollUri, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Gone)
            {
                break;
            }

            var poll = await ReadAsync(response, MistralVibeJsonContext.Default.PollResponse, "The state of the sign-in to Mistral Vibe could not be read", cancellationToken).ConfigureAwait(false);
            switch (poll.Status)
            {
                case "pending":
                    var left = process.ExpiresAt - Now();
                    await Delay(left < PollInterval ? (left < TimeSpan.Zero ? TimeSpan.Zero : left) : PollInterval, cancellationToken).ConfigureAwait(false);
                    break;
                case "completed" when !string.IsNullOrWhiteSpace(poll.ExchangeToken):
                    return poll.ExchangeToken;
                case "completed":
                    throw new InvalidOperationException("The sign-in to Mistral Vibe completed without its token.");
                case "expired":
                    throw new TimeoutException("The sign-in to Mistral Vibe expired. Sign in again.");
                case "denied":
                    throw new InvalidOperationException("The sign-in to Mistral Vibe was denied.");
                case "error":
                    throw new InvalidOperationException(string.IsNullOrWhiteSpace(poll.Message) ? "The sign-in to Mistral Vibe failed." : $"The sign-in to Mistral Vibe failed: {poll.Message}");
                default:
                    throw new InvalidOperationException("The sign-in to Mistral Vibe answered an unknown state.");
            }
        }

        throw new TimeoutException("The sign-in to Mistral Vibe expired. Sign in again.");
    }

    private async Task<string> ExchangeAsync(Uri api, string processId, string exchangeToken, string verifier, CancellationToken cancellationToken)
    {
        const string Failure = "The sign-in to Mistral Vibe did not give a key";
        using var response = await _httpClient.PostAsJsonAsync(
            $"{api.AbsoluteUri}/vibe/sign-in/{Uri.EscapeDataString(processId)}/exchange",
            new ExchangeRequest { ExchangeToken = exchangeToken, CodeVerifier = verifier },
            MistralVibeJsonContext.Default.ExchangeRequest,
            cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Forbidden &&
            await TryReadAsync(response, MistralVibeJsonContext.Default.ErrorResponse, cancellationToken).ConfigureAwait(false) is { ErrorCode: WorkspaceNotEligibleErrorCode })
        {
            throw new InvalidOperationException(
                "The workspace you chose cannot be used for Mistral Vibe. Sign in again and choose another workspace, or ask its administrator.");
        }

        var payload = await ReadAsync(response, MistralVibeJsonContext.Default.ExchangeResponse, Failure, cancellationToken).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(payload.ApiKey)
            ? throw new InvalidOperationException($"{Failure}.")
            : payload.ApiKey;
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type, string failure, CancellationToken cancellationToken)
        where T : class
    {
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"{failure} (HTTP {(int)response.StatusCode}).");
        }

        return await TryReadAsync(response, type, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"{failure}: the console answered what is not a sign-in.");
    }

    private static async Task<T?> TryReadAsync<T>(HttpResponseMessage response, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type, CancellationToken cancellationToken)
        where T : class
    {
        try
        {
            return await response.Content.ReadFromJsonAsync(type, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // An address the console gives must be one of its own, under the path it was asked on: the page the user opens and
    // the address the sign-in waits on cannot lead elsewhere.
    internal static Uri? Under(string? value, Uri baseUri)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, baseUri.Scheme, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(uri.Host, baseUri.Host, StringComparison.OrdinalIgnoreCase) ||
            uri.Port != baseUri.Port)
        {
            return null;
        }

        var basePath = baseUri.AbsolutePath.TrimEnd('/');
        var path = uri.AbsolutePath;
        return basePath.Length == 0 || path == basePath || path.StartsWith(basePath + "/", StringComparison.Ordinal) ? uri : null;
    }

    private static string Base64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed record SignInProcess(string Id, Uri SignInUri, Uri PollUri, DateTimeOffset ExpiresAt);

    internal sealed class StartRequest
    {
        [JsonPropertyName("code_challenge")]
        public string? CodeChallenge { get; set; }

        [JsonPropertyName("code_challenge_method")]
        public string? CodeChallengeMethod { get; set; }
    }

    internal sealed class StartResponse
    {
        [JsonPropertyName("process_id")]
        public string? ProcessId { get; set; }

        [JsonPropertyName("sign_in_url")]
        public string? SignInUrl { get; set; }

        [JsonPropertyName("poll_url")]
        public string? PollUrl { get; set; }

        [JsonPropertyName("expires_at")]
        public string? ExpiresAt { get; set; }
    }

    internal sealed class PollResponse
    {
        [JsonPropertyName("status")]
        public string? Status { get; set; }

        [JsonPropertyName("exchange_token")]
        public string? ExchangeToken { get; set; }

        [JsonPropertyName("message")]
        public string? Message { get; set; }
    }

    internal sealed class ExchangeRequest
    {
        [JsonPropertyName("exchange_token")]
        public string? ExchangeToken { get; set; }

        [JsonPropertyName("code_verifier")]
        public string? CodeVerifier { get; set; }
    }

    internal sealed class ExchangeResponse
    {
        [JsonPropertyName("api_key")]
        public string? ApiKey { get; set; }
    }

    internal sealed class ErrorResponse
    {
        [JsonPropertyName("error_code")]
        public int? ErrorCode { get; set; }
    }
}

/// <summary>The key a Mistral Vibe sign-in stored.</summary>
internal sealed class MistralVibeCredential
{
    [JsonPropertyName("api_key")]
    public string? ApiKey { get; set; }

    [JsonPropertyName("signed_in_at")]
    public DateTimeOffset SignedInAt { get; set; }
}

/// <summary>
/// The file of the key of a Mistral Vibe provider, <c>auth/mistral-vibe/&lt;provider key&gt;.json</c> under the state
/// root: written whole in one step, readable by its user alone, hidden on Windows.
/// </summary>
internal sealed class MistralVibeCredentialStore
{
    /// <summary>The folder of the keys under <c>auth</c>, and the type of the provider.</summary>
    public const string ProviderType = "mistral-vibe";

    private readonly string _path;

    public MistralVibeCredentialStore(MistralVibeLoginOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ProviderKey);
        var root = string.IsNullOrWhiteSpace(options.StateRootPath)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodeAlta")
            : options.StateRootPath.Trim();
        var invalid = Path.GetInvalidFileNameChars();
        var name = string.Concat(options.ProviderKey.Trim().Select(ch => invalid.Contains(ch) ? '_' : ch));
        _path = Path.Combine(root, "auth", ProviderType, name + ".json");
    }

    public async ValueTask<MistralVibeCredential?> ReadAsync(CancellationToken cancellationToken)
    {
        try
        {
            // A writer may replace the file while it is read: the reader keeps the content it opened.
            await using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 4096, useAsync: true);
            return await JsonSerializer.DeserializeAsync(stream, MistralVibeJsonContext.Default.MistralVibeCredential, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or JsonException)
        {
            return null;
        }
    }

    public async ValueTask WriteAsync(MistralVibeCredential credential, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporaryPath = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }

            await using (var stream = new FileStream(temporaryPath, options))
            {
                await JsonSerializer.SerializeAsync(stream, credential, MistralVibeJsonContext.Default.MistralVibeCredential, cancellationToken).ConfigureAwait(false);
            }

            // A hidden file may refuse to be replaced: it is shown for the move, and hidden again after it.
            if (File.Exists(_path))
            {
                File.SetAttributes(_path, File.GetAttributes(_path) & ~(FileAttributes.Hidden | FileAttributes.ReadOnly));
            }

            await StagedFileReplacement.MoveOverAsync(temporaryPath, _path).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        if (OperatingSystem.IsWindows())
        {
            try
            {
                File.SetAttributes(_path, File.GetAttributes(_path) | FileAttributes.Hidden);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    public ValueTask DeleteAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            File.Delete(_path);
        }
        catch (DirectoryNotFoundException)
        {
        }

        return ValueTask.CompletedTask;
    }
}

[JsonSerializable(typeof(MistralVibeCredential))]
[JsonSerializable(typeof(MistralVibeLoginManager.StartRequest))]
[JsonSerializable(typeof(MistralVibeLoginManager.StartResponse))]
[JsonSerializable(typeof(MistralVibeLoginManager.PollResponse))]
[JsonSerializable(typeof(MistralVibeLoginManager.ExchangeRequest))]
[JsonSerializable(typeof(MistralVibeLoginManager.ExchangeResponse))]
[JsonSerializable(typeof(MistralVibeLoginManager.ErrorResponse))]
internal sealed partial class MistralVibeJsonContext : JsonSerializerContext;
