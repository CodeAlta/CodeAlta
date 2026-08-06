using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol.Authentication;

namespace CodeAlta.Plugin.Mcp;

internal sealed record McpOAuthOptions
{
    public bool Enabled { get; init; } = true;

    public string? ClientId { get; init; }

    public string? ClientSecret { get; init; }

    public string? ClientMetadataDocumentUri { get; init; }

    public string? RedirectUri { get; init; }

    public IReadOnlyList<string> Scopes { get; init; } = [];

    public bool DynamicClientRegistration { get; init; } = true;
}

internal sealed record McpOAuthTokenStatus
{
    public required string Server { get; init; }

    public bool HasToken { get; init; }

    public string? TokenType { get; init; }

    public string? Scope { get; init; }

    public DateTimeOffset? ObtainedAt { get; init; }

    public DateTimeOffset? ExpiresAt { get; init; }

    public string? Path { get; init; }
}

internal sealed partial class McpOAuthTokenCache : ITokenCache
{
    private readonly string _path;

    public McpOAuthTokenCache(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
    }

    public string Path => _path;

    public bool Exists => File.Exists(_path);

    public static string GetTokenPath(string? userHomeDirectory, string serverKey, string? serverUrl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverKey);
        var home = string.IsNullOrWhiteSpace(userHomeDirectory)
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : userHomeDirectory;
        if (string.IsNullOrWhiteSpace(home))
        {
            home = Environment.GetEnvironmentVariable("USERPROFILE") ?? Environment.GetEnvironmentVariable("HOME") ?? Environment.CurrentDirectory;
        }

        var suffixSource = string.Concat(serverKey, "\n", serverUrl ?? string.Empty);
        var suffix = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(suffixSource)), 0, 4).ToLowerInvariant();
        return System.IO.Path.Combine(home, ".alta", "auth", "mcp", SanitizeFilePart(serverKey) + "-" + suffix + ".json");
    }

    public async ValueTask StoreTokensAsync(TokenContainer token, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(token);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
        var state = new McpOAuthTokenState
        {
            TokenType = token.TokenType,
            AccessToken = token.AccessToken,
            RefreshToken = token.RefreshToken,
            ExpiresIn = token.ExpiresIn,
            Scope = token.Scope,
            ObtainedAt = token.ObtainedAt,
        };
        var tempPath = _path + ".tmp";
        await using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true))
        {
            await JsonSerializer.SerializeAsync(stream, state, McpOAuthJsonContext.Default.McpOAuthTokenState, cancellationToken).ConfigureAwait(false);
        }

        File.Move(tempPath, _path, overwrite: true);
        TryHardenTokenFile(_path);
    }

    public async ValueTask<TokenContainer?> GetTokensAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
        {
            return null;
        }

        try
        {
            await using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
            var state = await JsonSerializer.DeserializeAsync(stream, McpOAuthJsonContext.Default.McpOAuthTokenState, cancellationToken).ConfigureAwait(false);
            if (state is null || string.IsNullOrWhiteSpace(state.AccessToken))
            {
                return null;
            }

            return new TokenContainer
            {
                TokenType = string.IsNullOrWhiteSpace(state.TokenType) ? "Bearer" : state.TokenType,
                AccessToken = state.AccessToken,
                RefreshToken = state.RefreshToken,
                ExpiresIn = state.ExpiresIn,
                Scope = state.Scope,
                ObtainedAt = state.ObtainedAt,
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public async Task<McpOAuthTokenStatus> GetStatusAsync(string serverKey, CancellationToken cancellationToken)
    {
        var token = await GetTokensAsync(cancellationToken).ConfigureAwait(false);
        return new McpOAuthTokenStatus
        {
            Server = serverKey,
            HasToken = token is not null,
            TokenType = token?.TokenType,
            Scope = token?.Scope,
            ObtainedAt = token?.ObtainedAt,
            ExpiresAt = ComputeExpiresAt(token),
            Path = _path,
        };
    }

    private static DateTimeOffset? ComputeExpiresAt(TokenContainer? token)
    {
        if (token?.ExpiresIn is not { } seconds)
        {
            return null;
        }

        try
        {
            return token.ObtainedAt.AddSeconds(seconds);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    public void Delete()
    {
        if (File.Exists(_path))
        {
            File.Delete(_path);
        }
    }

    private static string SanitizeFilePart(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            builder.Append(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_');
        }

        return builder.Length == 0 ? "server" : builder.ToString();
    }

    private static void TryHardenTokenFile(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // Best-effort: the token remains in the user profile and is never written to MCP config.
        }
    }

    private sealed record McpOAuthTokenState
    {
        [JsonPropertyName("token_type")]
        public string? TokenType { get; init; }

        [JsonPropertyName("access_token")]
        public string? AccessToken { get; init; }

        [JsonPropertyName("refresh_token")]
        public string? RefreshToken { get; init; }

        [JsonPropertyName("expires_in")]
        public int? ExpiresIn { get; init; }

        [JsonPropertyName("scope")]
        public string? Scope { get; init; }

        [JsonPropertyName("obtained_at")]
        public DateTimeOffset ObtainedAt { get; init; }
    }

    [JsonSerializable(typeof(McpOAuthTokenState))]
    private sealed partial class McpOAuthJsonContext : JsonSerializerContext;
}

internal sealed class McpOAuthBrowserAuthorization
{
    private const string SuccessHtml = "<html><body><h1>CodeAlta MCP authorization complete</h1><p>You may close this browser tab and return to CodeAlta.</p></body></html>";
    private const string FailureHtml = "<html><body><h1>CodeAlta MCP authorization failed</h1><p>Return to CodeAlta for details.</p></body></html>";
    private readonly Action<string>? _reportStatus;
    private readonly bool _openBrowser;

    public McpOAuthBrowserAuthorization(Action<string>? reportStatus = null, bool openBrowser = true)
    {
        _reportStatus = reportStatus;
        _openBrowser = openBrowser;
    }

    public async Task<AuthorizationResult?> AuthorizeAsync(AuthorizationCallbackContext authorization, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        var authorizationUri = authorization.AuthorizationUri;
        var redirectUri = authorization.RedirectUri;
        ArgumentNullException.ThrowIfNull(authorizationUri);
        ArgumentNullException.ThrowIfNull(redirectUri);
        if (!redirectUri.IsLoopback || redirectUri.Scheme != Uri.UriSchemeHttp || redirectUri.Port <= 0 || redirectUri.IsDefaultPort)
        {
            _reportStatus?.Invoke("OAuth redirect URI must be an HTTP loopback URI with an explicit non-default port for browser authorization.");
            return null;
        }

        var expectedState = GetQueryParameter(authorizationUri, "state");
        using var listener = new HttpListener();
        var prefix = $"http://{redirectUri.Authority}/";
        listener.Prefixes.Add(prefix);
        listener.Start();
        using var registration = cancellationToken.Register(static state => ((HttpListener)state!).Stop(), listener);
        try
        {
            _reportStatus?.Invoke("Open MCP authorization in your browser: " + authorizationUri);
            if (_openBrowser)
            {
                TryOpenBrowser(authorizationUri);
            }

            while (true)
            {
                var context = await listener.GetContextAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
                if (string.Equals(context.Request.HttpMethod, "OPTIONS", StringComparison.OrdinalIgnoreCase))
                {
                    WritePreflightResponse(context);
                    continue;
                }

                if (!string.Equals(context.Request.Url?.AbsolutePath, redirectUri.AbsolutePath, StringComparison.Ordinal))
                {
                    context.Response.StatusCode = 404;
                    context.Response.Close();
                    continue;
                }

                var error = context.Request.QueryString["error"];
                var code = context.Request.QueryString["code"];
                var state = context.Request.QueryString["state"];
                var issuer = context.Request.QueryString["iss"];
                if (!string.IsNullOrWhiteSpace(error) || string.IsNullOrWhiteSpace(code))
                {
                    await WriteHtmlAsync(context.Response, FailureHtml, cancellationToken).ConfigureAwait(false);
                    _reportStatus?.Invoke("MCP authorization failed: " + McpRedactor.RedactValue(null, error ?? "authorization code was missing"));
                    return null;
                }

                if (!string.IsNullOrEmpty(expectedState) && !FixedTimeEquals(expectedState, state))
                {
                    await WriteHtmlAsync(context.Response, FailureHtml, cancellationToken).ConfigureAwait(false);
                    _reportStatus?.Invoke("MCP authorization failed: callback state did not match the authorization request.");
                    return null;
                }

                await WriteHtmlAsync(context.Response, SuccessHtml, cancellationToken).ConfigureAwait(false);
                return new AuthorizationResult
                {
                    Code = code,
                    State = state,
                    Iss = issuer,
                };
            }
        }
        catch (HttpListenerException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        finally
        {
            listener.Stop();
        }
    }

    private static void WritePreflightResponse(HttpListenerContext context)
    {
        ApplyCorsHeaders(context.Response, context.Request.Headers["Origin"]);
        context.Response.StatusCode = 204;
        context.Response.Close();
    }

    private static void ApplyCorsHeaders(HttpListenerResponse response, string? origin)
    {
        if (!string.IsNullOrWhiteSpace(origin))
        {
            response.Headers["Access-Control-Allow-Origin"] = origin;
            response.Headers["Vary"] = "Origin";
        }

        response.Headers["Access-Control-Allow-Methods"] = "GET, OPTIONS";
        response.Headers["Access-Control-Allow-Headers"] = "Content-Type";
        response.Headers["Access-Control-Allow-Private-Network"] = "true";
    }

    private static async Task WriteHtmlAsync(HttpListenerResponse response, string html, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(html);
        response.ContentType = "text/html; charset=utf-8";
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        response.Close();
    }

    private static string? GetQueryParameter(Uri uri, string parameterName)
    {
        var query = uri.Query;
        if (string.IsNullOrEmpty(query))
        {
            return null;
        }

        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = part.IndexOf('=', StringComparison.Ordinal);
            var key = separator < 0 ? part : part[..separator];
            if (!string.Equals(DecodeQueryComponent(key), parameterName, StringComparison.Ordinal))
            {
                continue;
            }

            return separator < 0 ? string.Empty : DecodeQueryComponent(part[(separator + 1)..]);
        }

        return null;
    }

    private static string DecodeQueryComponent(string value)
        => Uri.UnescapeDataString(value.Replace("+", " ", StringComparison.Ordinal));

    private static bool FixedTimeEquals(string expected, string? actual)
    {
        if (actual is null)
        {
            return false;
        }

        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        var actualBytes = Encoding.UTF8.GetBytes(actual);
        return CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes);
    }

    private static void TryOpenBrowser(Uri uri)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = uri.ToString(),
                UseShellExecute = true,
            });
        }
        catch (Exception)
        {
            // Headless terminals can use the reported authorization URL instead.
        }
    }
}
