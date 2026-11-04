namespace CodeAlta.Server.Identity.SignIn;

/// <summary>
/// The public sign-in clients the CodeAlta project registers with Microsoft, Google and GitHub (<c>doc/fleet/readme.md</c>,
/// decision 26), so that any self-hosted server signs people in without a registration of its own. They do not exist
/// yet: these values are placeholders, and a method whose client id is still a placeholder is not offered. A server
/// takes the real values from <c>CODEALTA_SIGNIN_&lt;PROVIDER&gt;_CLIENT_ID</c> and <c>_CLIENT_SECRET</c> until a version
/// of CodeAlta Server carries them.
/// </summary>
public static class PublicSignInClients
{
    /// <summary>The prefix of a placeholder.</summary>
    public const string PlaceholderPrefix = "codealta-register-";

    /// <summary>
    /// The application (client) id of the CodeAlta application in Microsoft Entra: multitenant and personal accounts,
    /// platform "Mobile and desktop applications" with the redirect <c>http://localhost</c>, public client flows on.
    /// </summary>
    public const string MicrosoftClientId = PlaceholderPrefix + "microsoft-client-id";

    /// <summary>The OAuth client id of the CodeAlta "Desktop app" client in Google Cloud, with its published consent screen.</summary>
    public const string GoogleClientId = PlaceholderPrefix + "google-client-id";

    /// <summary>
    /// The client "secret" Google gives a desktop client: Google requires it at the token endpoint, and documents that it
    /// is not secret for an installed application (the code is still bound to its PKCE verifier).
    /// </summary>
    public const string GoogleClientSecret = PlaceholderPrefix + "google-desktop-client-secret";

    /// <summary>The client id of the CodeAlta OAuth App on GitHub, with the callback <c>http://127.0.0.1/callback</c>.</summary>
    public const string GitHubClientId = PlaceholderPrefix + "github-client-id";

    /// <summary>The client secret of the CodeAlta OAuth App, which GitHub requires at its token endpoint even with PKCE.</summary>
    public const string GitHubClientSecret = PlaceholderPrefix + "github-client-secret";

    /// <summary>Gets whether a client id is a placeholder (or missing).</summary>
    /// <param name="clientId">The client id.</param>
    /// <returns>Whether the method it is for cannot be used.</returns>
    public static bool IsPlaceholder(string? clientId) => string.IsNullOrWhiteSpace(clientId) || clientId.StartsWith(PlaceholderPrefix, StringComparison.Ordinal);
}

/// <summary>The client of one identity provider at the server.</summary>
/// <param name="ClientId">The client id.</param>
/// <param name="ClientSecret">The client secret, or null for a public client with PKCE.</param>
public sealed record SignInClient(string ClientId, string? ClientSecret = null);

/// <summary>The sign-in of a server: the public clients of the linked methods and where their providers are.</summary>
public sealed class SignInOptions
{
    /// <summary>Gets the Microsoft client.</summary>
    public SignInClient Microsoft { get; init; } = new(PublicSignInClients.MicrosoftClientId);

    /// <summary>Gets the Google client.</summary>
    public SignInClient Google { get; init; } = new(PublicSignInClients.GoogleClientId, PublicSignInClients.GoogleClientSecret);

    /// <summary>Gets the GitHub client.</summary>
    public SignInClient GitHub { get; init; } = new(PublicSignInClients.GitHubClientId, PublicSignInClients.GitHubClientSecret);

    /// <summary>Gets the authority of Microsoft accounts: the <c>common</c> endpoint, personal and work accounts.</summary>
    public Uri MicrosoftAuthority { get; init; } = new("https://login.microsoftonline.com/common/v2.0");

    /// <summary>Gets the issuer of Google accounts.</summary>
    public Uri GoogleAuthority { get; init; } = new("https://accounts.google.com");

    /// <summary>Gets the address of GitHub's OAuth pages.</summary>
    public Uri GitHubAddress { get; init; } = new("https://github.com");

    /// <summary>Gets the address of GitHub's API.</summary>
    public Uri GitHubApiAddress { get; init; } = new("https://api.github.com");

    /// <summary>
    /// Gets whether a provider may be reached over plain <c>http://</c> on the loopback (a test provider); never for a
    /// provider elsewhere.
    /// </summary>
    public bool AllowLoopbackHttpProviders { get; init; }

    /// <summary>Gets how long a sign-in attempt waits for the browser.</summary>
    public TimeSpan AttemptLifetime { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>Gets how long the grant of a sign-in lets its device enroll.</summary>
    public TimeSpan GrantLifetime { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>Gets how many attempts wait at once, beyond which new ones are refused.</summary>
    public int MaxPendingAttempts { get; init; } = 1024;

    /// <summary>Gets the HTTP handler of the calls to the providers, for tests; null for the usual one.</summary>
    public HttpMessageHandler? ProviderHandler { get; init; }

    /// <summary>Reads the clients from the environment (<c>CODEALTA_SIGNIN_MICROSOFT_CLIENT_ID</c>, ...), over the built-in ones.</summary>
    /// <param name="environment">What gives a variable of the environment.</param>
    /// <returns>The options.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="environment"/> is null.</exception>
    public static SignInOptions FromEnvironment(Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        var defaults = new SignInOptions();
        return new SignInOptions
        {
            Microsoft = Read(environment, "MICROSOFT", defaults.Microsoft),
            Google = Read(environment, "GOOGLE", defaults.Google),
            GitHub = Read(environment, "GITHUB", defaults.GitHub),
        };
    }

    private static SignInClient Read(Func<string, string?> environment, string provider, SignInClient fallback)
    {
        var id = environment($"CODEALTA_SIGNIN_{provider}_CLIENT_ID");
        if (string.IsNullOrWhiteSpace(id)) return fallback;
        var secret = environment($"CODEALTA_SIGNIN_{provider}_CLIENT_SECRET");
        return new SignInClient(id.Trim(), string.IsNullOrWhiteSpace(secret) ? null : secret.Trim());
    }
}
