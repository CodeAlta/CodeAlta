using CodeAlta.Agent;

namespace CodeAlta.Plugin.Git;

/// <summary>
/// The issue lookups of the profiles the sessions work under: one for the usual profile, and one for each set of
/// variables that can choose the credentials of a provider (the <c>GH_CONFIG_DIR</c> of a space, the
/// <c>AZURE_CONFIG_DIR</c> or <c>GLAB_CONFIG_DIR</c> of its CLI profiles…), so that what one profile may read is never
/// cached for another.
/// </summary>
/// <remarks>One instance serves concurrent callers. The <see cref="HttpClient"/> is borrowed, as by <see cref="GitIssueLookup"/>.</remarks>
public sealed class GitIssueLookups
{
    /// <summary>The most profiles kept besides the usual one; the one used least recently is forgotten first.</summary>
    public const int MaximumProfiles = 16;

    // The variables of the Git card of a space (author, committer, SSH key, signing): they choose no credential of a
    // provider, so a space that only sets them reads with the usual profile.
    private const string GitVariablePrefix = "GIT_";

    private readonly HttpClient _client;
    private readonly GitIssueLookupOptions _options;
    private readonly Func<GitRepositoryReference, AgentProcessEnvironment?, CancellationToken, ValueTask<GitRemoteCredential?>>? _credentials;
    private readonly Lock _gate = new();
    private readonly Dictionary<AgentProcessEnvironment, (GitIssueLookup Lookup, long Used)> _profiles = [];
    private long _uses;

    /// <summary>Initializes the lookups.</summary>
    /// <param name="client">The borrowed client of the provider REST requests.</param>
    /// <param name="options">The options of every lookup; its credentials are those of the usual profile.</param>
    /// <param name="credentials">
    /// Finds the credentials of a repository in the environment of a profile, such as
    /// <see cref="GitIssueLookup.ResolveCredentialAsync(GitRepositoryReference, AgentProcessEnvironment?, CancellationToken)"/>;
    /// null to use the credentials of <paramref name="options"/> for every profile.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="client"/> or <paramref name="options"/> is null.</exception>
    public GitIssueLookups(HttpClient client, GitIssueLookupOptions options,
        Func<GitRepositoryReference, AgentProcessEnvironment?, CancellationToken, ValueTask<GitRemoteCredential?>>? credentials = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);
        _client = client;
        _options = options;
        _credentials = credentials;
        Usual = new GitIssueLookup(client, options);
    }

    /// <summary>Gets the lookup of the usual profile of the user.</summary>
    public GitIssueLookup Usual { get; }

    /// <summary>Gets the lookup of the profile an environment names.</summary>
    /// <param name="environment">
    /// The variables of the session; null, or with the variables of its Git card alone, for the usual profile.
    /// </param>
    /// <returns>The lookup.</returns>
    public GitIssueLookup For(AgentProcessEnvironment? environment)
    {
        if (_credentials is null || environment is null || environment.IsEmpty) return Usual;
        var profile = environment.Variables.Any(static variable => variable.Key.StartsWith(GitVariablePrefix, StringComparison.OrdinalIgnoreCase))
            ? new AgentProcessEnvironment(environment.Variables.Where(static variable => !variable.Key.StartsWith(GitVariablePrefix, StringComparison.OrdinalIgnoreCase)))
            : environment;
        if (profile.IsEmpty) return Usual;
        lock (_gate)
        {
            var use = ++_uses;
            if (_profiles.TryGetValue(profile, out var known))
            {
                _profiles[profile] = (known.Lookup, use);
                return known.Lookup;
            }

            if (_profiles.Count >= MaximumProfiles)
            {
                _profiles.Remove(_profiles.MinBy(static entry => entry.Value.Used).Key);
            }

            var credentials = _credentials;
            var lookup = new GitIssueLookup(_client, new GitIssueLookupOptions
            {
                CacheDuration = _options.CacheDuration,
                TimeProvider = _options.TimeProvider,
                RemoteUrlReader = _options.RemoteUrlReader,
                GitLabHosts = _options.GitLabHosts,
                CredentialProvider = (repository, token) => credentials(repository, profile, token),
            });
            _profiles[profile] = (lookup, use);
            return lookup;
        }
    }
}
