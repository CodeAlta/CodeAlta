namespace CodeAlta.Server.Identity.SignIn;

/// <summary>
/// The public sign-in clients the CodeAlta project registers with Microsoft, Google and GitHub (<c>doc/fleet/readme.md</c>,
/// decision 26), so that any self-hosted server signs people in without a registration of its own. A method whose client id
/// is a placeholder (<see cref="PlaceholderPrefix"/>) is not offered. A server may take other values from
/// <c>CODEALTA_SIGNIN_&lt;PROVIDER&gt;_CLIENT_ID</c> (and <c>_CLIENT_SECRET</c> for Microsoft and Google). GitHub has no
/// secret: its sign-in is the device flow of a GitHub App, registered from
/// <c>src/CodeAlta.Server/Identity/SignIn/github-app-manifest.json</c> (<c>doc/fleet/identity.md</c>).
/// </summary>
public static class PublicSignInClients
{
    /// <summary>The prefix of a placeholder.</summary>
    public const string PlaceholderPrefix = "codealta-register-";

    /// <summary>
    /// The application (client) id of the CodeAlta application in Microsoft Entra: multitenant and personal accounts,
    /// platform "Mobile and desktop applications" with the redirect <c>http://127.0.0.1/callback</c> (Entra ignores the port of
    /// a loopback redirect, and the desktop listens on a port of its choice: <c>FleetLoopbackRedirect</c>), public client flows on.
    /// </summary>
    public const string MicrosoftClientId = "8d3b8e84-8200-4fff-9272-cc4e01f075b1";

    /// <summary>
    /// The OAuth client id of the CodeAlta "Desktop app" client in Google Cloud, with its published consent screen. It is not
    /// in the source: the build takes it from <c>CODEALTA_SIGNIN_GOOGLE_CLIENT_ID</c> (a secret of the repository's CI, so that
    /// released packages carry it), and a build without it has a placeholder, which does not offer Google.
    /// </summary>
    public const string GoogleClientId = PublicSignInBuild.GoogleClientId;

    /// <summary>
    /// The client "secret" Google gives a desktop client: Google requires it at the token endpoint, and documents that it
    /// is not secret for an installed application (the code is still bound to its PKCE verifier). Like the client id, the
    /// build takes it from <c>CODEALTA_SIGNIN_GOOGLE_CLIENT_SECRET</c>, so that it is not in the public repository.
    /// </summary>
    public const string GoogleClientSecret = PublicSignInBuild.GoogleClientSecret;

    /// <summary>
    /// The client id of the CodeAlta GitHub App, with "Enable Device Flow" on: the device flow needs no client secret and
    /// no redirect.
    /// </summary>
    public const string GitHubClientId = "Iv23lipXnKCrRFkcoegA";

    /// <summary>Gets whether a client id is a placeholder (or missing).</summary>
    /// <param name="clientId">The client id.</param>
    /// <returns>Whether the method it is for cannot be used.</returns>
    public static bool IsPlaceholder(string? clientId) => string.IsNullOrWhiteSpace(clientId) || clientId.StartsWith(PlaceholderPrefix, StringComparison.Ordinal);
}

/// <summary>The client of one identity provider at the server.</summary>
/// <param name="ClientId">The client id.</param>
/// <param name="ClientSecret">The client secret, or null for a public client with PKCE or a device flow.</param>
public sealed record SignInClient(string ClientId, string? ClientSecret = null);

/// <summary>The sign-in of a server: the public clients of the linked methods and where their providers are.</summary>
public sealed class SignInOptions
{
    /// <summary>Gets the Microsoft client.</summary>
    public SignInClient Microsoft { get; init; } = new(PublicSignInClients.MicrosoftClientId);

    /// <summary>Gets the Google client.</summary>
    public SignInClient Google { get; init; } = new(PublicSignInClients.GoogleClientId, PublicSignInClients.GoogleClientSecret);

    /// <summary>Gets the GitHub client: the client id of a GitHub App with the device flow on, never a secret.</summary>
    public SignInClient GitHub { get; init; } = new(PublicSignInClients.GitHubClientId);

    /// <summary>Gets the authority of Microsoft accounts: the <c>common</c> endpoint, personal and work accounts.</summary>
    public Uri MicrosoftAuthority { get; init; } = new("https://login.microsoftonline.com/common/v2.0");

    /// <summary>Gets the issuer of Google accounts.</summary>
    public Uri GoogleAuthority { get; init; } = new("https://accounts.google.com");

    /// <summary>Gets the address of GitHub's sign-in pages: <c>https://github.com</c>, or a GitHub Enterprise Server.</summary>
    public Uri GitHubAddress { get; init; } = new("https://github.com");

    /// <summary>Gets the address of GitHub's API: <c>https://api.github.com</c>, or <c>/api/v3</c> of a GitHub Enterprise Server.</summary>
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

    /// <summary>
    /// Reads the clients from the environment, over the built-in ones: <c>CODEALTA_SIGNIN_MICROSOFT_CLIENT_ID</c> and
    /// <c>_CLIENT_SECRET</c>, the same for <c>GOOGLE</c>, <c>CODEALTA_SIGNIN_GITHUB_CLIENT_ID</c> (no secret), and
    /// <c>CODEALTA_SIGNIN_GITHUB_URL</c>, the address of a GitHub Enterprise Server whose own GitHub App the company registered.
    /// </summary>
    /// <param name="environment">What gives a variable of the environment.</param>
    /// <returns>The options.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="environment"/> is null.</exception>
    /// <exception cref="ArgumentException"><c>CODEALTA_SIGNIN_GITHUB_URL</c> is not an <c>https://</c> address.</exception>
    public static SignInOptions FromEnvironment(Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        var defaults = new SignInOptions();
        var gitHubId = environment("CODEALTA_SIGNIN_GITHUB_CLIENT_ID");
        var (gitHub, gitHubApi) = GitHubAddresses(environment("CODEALTA_SIGNIN_GITHUB_URL"), defaults);
        return new SignInOptions
        {
            Microsoft = Read(environment, "MICROSOFT", defaults.Microsoft),
            Google = Read(environment, "GOOGLE", defaults.Google),
            GitHub = string.IsNullOrWhiteSpace(gitHubId) ? defaults.GitHub : new SignInClient(gitHubId.Trim()),
            GitHubAddress = gitHub,
            GitHubApiAddress = gitHubApi,
        };
    }

    // github.com, or a GitHub Enterprise Server, whose API is under /api/v3 of its address.
    private static (Uri Pages, Uri Api) GitHubAddresses(string? baseUrl, SignInOptions defaults)
    {
        if (string.IsNullOrWhiteSpace(baseUrl)) return (defaults.GitHubAddress, defaults.GitHubApiAddress);
        if (!Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out var address) || address.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(address.Query)
            || !string.IsNullOrEmpty(address.Fragment) || !string.IsNullOrEmpty(address.UserInfo))
            throw new ArgumentException("CODEALTA_SIGNIN_GITHUB_URL must be the https:// address of a GitHub Enterprise Server.", nameof(baseUrl));
        var pages = new Uri(address.GetLeftPart(UriPartial.Path).TrimEnd('/'));
        if (string.Equals(pages.Host, "github.com", StringComparison.OrdinalIgnoreCase)) return (defaults.GitHubAddress, defaults.GitHubApiAddress);
        return (pages, new Uri(pages.AbsoluteUri.TrimEnd('/') + "/api/v3"));
    }

    private static SignInClient Read(Func<string, string?> environment, string provider, SignInClient fallback)
    {
        var id = environment($"CODEALTA_SIGNIN_{provider}_CLIENT_ID");
        if (string.IsNullOrWhiteSpace(id)) return fallback;
        var secret = environment($"CODEALTA_SIGNIN_{provider}_CLIENT_SECRET");
        return new SignInClient(id.Trim(), string.IsNullOrWhiteSpace(secret) ? null : secret.Trim());
    }
}
