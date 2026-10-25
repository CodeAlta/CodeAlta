using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using CodeAlta.Agent;
using CodeAlta.Catalog.ModelTiers;

namespace CodeAlta.Catalog;

/// <summary>
/// What a space sets up besides its projects: what its new sessions start with, the identity its sessions work
/// under, and the tools they do without. Written in the front matter of the file of the space, beside its name.
/// Each part is optional: what a space leaves out is the usual setting of the user.
/// </summary>
/// <remarks>Only references are kept here (paths, names of environment variables), never a secret.</remarks>
/// <param name="NewSessions">What the new sessions of the space start with; <see langword="null"/> for the usual.</param>
/// <param name="Identity">The identity the sessions of the space work under; <see langword="null"/> for the usual.</param>
/// <param name="Tools">The tools the sessions of the space do without; <see langword="null"/> for none.</param>
public sealed record SpaceSettings(SpaceNewSessions? NewSessions = null, SpaceIdentity? Identity = null, SpaceTools? Tools = null)
{
    /// <summary>Gets settings that set nothing.</summary>
    public static SpaceSettings Empty { get; } = new();

    /// <summary>Gets whether nothing is set.</summary>
    public bool IsEmpty => (NewSessions?.IsEmpty ?? true) && (Identity?.IsEmpty ?? true) && (Tools?.IsEmpty ?? true);

    /// <summary>Tells what is wrong with the settings.</summary>
    /// <returns>The first problem found, or <see langword="null"/> when they are valid.</returns>
    public string? Problem()
        => NewSessions?.Problem() ?? Identity?.Problem() ?? Tools?.Problem();

    /// <summary>
    /// Keeps what is valid of settings read from a file: a value that is not valid is left out, so that one wrong
    /// value never hides the rest of the space.
    /// </summary>
    /// <returns>The settings without their invalid values.</returns>
    public SpaceSettings Sanitized()
        => new(NewSessions?.Sanitized(), Identity?.Sanitized(), Tools?.Sanitized());

    // Text on one line, without control characters, of at most the length given.
    internal static bool IsLine([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] string? value, int maximum)
        => value is { Length: > 0 } && value.Length <= maximum && value == value.Trim() && !value.Any(char.IsControl);
}

/// <summary>What the new sessions of a space start with.</summary>
/// <param name="AgentPrompt">The agent prompt of a new session; <see langword="null"/> for the usual one.</param>
/// <param name="Tiers">The model tiers of the space, which replace the global ones; <see langword="null"/> for the global ones.</param>
/// <param name="Fallback">
/// The fallback of the space, which replaces the global one while the space has tiers of its own;
/// <see langword="null"/> for the global one.
/// </param>
/// <param name="Sandbox">
/// The sandbox of the sessions of the space: the name of a sandbox profile, or <c>off</c>; <see langword="null"/> for
/// the setting of every session.
/// </param>
public sealed record SpaceNewSessions(string? AgentPrompt = null, SpaceModelTiers? Tiers = null, ModelFallbackSettings? Fallback = null, string? Sandbox = null)
{
    /// <summary>The longest identifier of an agent prompt.</summary>
    public const int MaximumAgentPromptLength = 128;

    /// <summary>Gets whether nothing is set.</summary>
    public bool IsEmpty => AgentPrompt is null && (Tiers?.IsEmpty ?? true) && Fallback is null && Sandbox is null;

    /// <summary>Tells what is wrong with the settings.</summary>
    /// <returns>The first problem found, or <see langword="null"/>.</returns>
    public string? Problem()
        => AgentPrompt is not null && !IsPromptId(AgentPrompt)
            ? $"'{AgentPrompt}' is not the identifier of an agent prompt."
            : Sandbox is not null && !IsSandboxName(Sandbox)
                ? $"'{Sandbox}' is not the name of a sandbox profile."
                : Tiers?.Problem();

    /// <summary>Keeps what is valid.</summary>
    /// <returns>The settings without their invalid values; <see langword="null"/> when nothing is left.</returns>
    public SpaceNewSessions? Sanitized()
    {
        var kept = new SpaceNewSessions(IsPromptId(AgentPrompt) ? AgentPrompt!.Trim() : null, Tiers?.Sanitized(), Fallback,
            IsSandboxName(Sandbox) ? Sandbox!.Trim() : null);
        return kept.IsEmpty ? null : kept;
    }

    /// <summary>Gets whether a value names a sandbox profile, or <c>off</c>: letters, digits, <c>-</c> and <c>_</c>.</summary>
    /// <param name="value">The value.</param>
    /// <returns>True when it may name a sandbox.</returns>
    public static bool IsSandboxName(string? value)
        => value is { Length: > 0 and <= 64 } && value.All(static ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_');

    private static bool IsPromptId(string? value)
        => value is { Length: > 0 and <= MaximumAgentPromptLength }
            && value.All(static ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.');
}

/// <summary>
/// The model tiers of a space: for complex, standard and quick work, the targets to use in their order, each with
/// its own reasoning efforts in their order.
/// </summary>
/// <param name="Complex">The targets of complex work.</param>
/// <param name="Standard">The targets of standard work.</param>
/// <param name="Quick">The targets of quick work.</param>
public sealed record SpaceModelTiers(IReadOnlyList<ModelTierTarget> Complex, IReadOnlyList<ModelTierTarget> Standard, IReadOnlyList<ModelTierTarget> Quick)
{
    /// <summary>The most targets of one tier.</summary>
    public const int MaximumTargets = 16;

    /// <summary>Gets whether no tier lists a target.</summary>
    public bool IsEmpty => Complex.Count == 0 && Standard.Count == 0 && Quick.Count == 0;

    /// <summary>Tells what is wrong with the tiers.</summary>
    /// <returns>The first problem found, or <see langword="null"/>.</returns>
    public string? Problem()
        => Complex.Count > MaximumTargets || Standard.Count > MaximumTargets || Quick.Count > MaximumTargets
            ? $"A tier lists at most {MaximumTargets} models."
            : null;

    /// <summary>Keeps what is valid.</summary>
    /// <returns>The tiers, each cut to its most targets; <see langword="null"/> when no tier lists one.</returns>
    public SpaceModelTiers? Sanitized()
    {
        var kept = new SpaceModelTiers([.. Complex.Take(MaximumTargets)], [.. Standard.Take(MaximumTargets)], [.. Quick.Take(MaximumTargets)]);
        return kept.IsEmpty ? null : kept;
    }

    /// <summary>Gets the tiers as the session of the space uses them in place of the global ones.</summary>
    /// <returns>The tiers.</returns>
    public ModelTierSettings ToTierSettings() => new(Complex, Standard, Quick);

    /// <summary>Gets the tiers of the space from tiers as the settings edit them; null when none has a target.</summary>
    /// <param name="tiers">The tiers.</param>
    /// <returns>The tiers of the space, or <see langword="null"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="tiers"/> is null.</exception>
    public static SpaceModelTiers? From(ModelTierSettings tiers)
    {
        ArgumentNullException.ThrowIfNull(tiers);
        return tiers.IsEmpty ? null : new SpaceModelTiers(tiers.Complex, tiers.Standard, tiers.Quick);
    }
}

/// <summary>How a space signs its commits.</summary>
public enum SpaceGitSigning
{
    /// <summary>Commits are not signed.</summary>
    Off,

    /// <summary>Commits are signed with an SSH key.</summary>
    Ssh,

    /// <summary>Commits are signed with a GPG key.</summary>
    Gpg,
}

/// <summary>
/// The identity the sessions of a space work under: Git, GitHub, Jira and the profiles of other command line tools. A
/// part left out is the usual setting of the user, so a space without identity changes nothing.
/// </summary>
/// <param name="Git">The Git author, keys and signing; <see langword="null"/> for the usual ones.</param>
/// <param name="GitHub">The GitHub CLI profile; <see langword="null"/> for the usual one.</param>
/// <param name="Jira">The Jira site and account; <see langword="null"/> for the usual ones.</param>
/// <param name="Cli">
/// The profiles of other command line tools, in their order; <see langword="null"/> or empty for none. A tool without
/// a profile keeps its usual configuration.
/// </param>
public sealed record SpaceIdentity(SpaceGitIdentity? Git = null, SpaceGitHubIdentity? GitHub = null, SpaceJiraIdentity? Jira = null,
    IReadOnlyList<SpaceCliProfile>? Cli = null)
{
    /// <summary>Gets whether nothing is set.</summary>
    public bool IsEmpty => (Git?.IsEmpty ?? true) && (GitHub?.IsEmpty ?? true) && (Jira?.IsEmpty ?? true) && (Cli is null || Cli.Count == 0);

    /// <summary>Tells what is wrong with the identity.</summary>
    /// <returns>The first problem found, or <see langword="null"/>.</returns>
    public string? Problem() => Git?.Problem() ?? GitHub?.Problem() ?? Jira?.Problem() ?? SpaceCliProfile.Problem(Cli);

    /// <summary>Keeps what is valid.</summary>
    /// <returns>The identity without its invalid values; <see langword="null"/> when nothing is left.</returns>
    public SpaceIdentity? Sanitized()
    {
        var kept = new SpaceIdentity(Git?.Sanitized(), GitHub?.Sanitized(), Jira?.Sanitized(), SpaceCliProfile.Sanitized(Cli));
        return kept.IsEmpty ? null : kept;
    }

    /// <summary>
    /// Gives this identity with the CLI profiles of a project over the ones of the space: a profile of the project
    /// with the name of one of the space adds its variables to that profile, replacing a variable of the same name,
    /// and replaces its check and login commands when it has them; another profile of the project is added. A variable
    /// the project gives is taken out of every other profile, so that the project's value is the one the processes
    /// get; between two profiles of the project, the later one wins.
    /// </summary>
    /// <param name="project">The profiles of the project; <see langword="null"/> or empty for none.</param>
    /// <returns>The identity the sessions of the project work under; this one when the project has no profile.</returns>
    public SpaceIdentity WithProjectCli(IReadOnlyList<SpaceCliProfile>? project)
    {
        if (project is null || project.Count == 0) return this;
        var merged = new List<SpaceCliProfile>(Cli ?? []);
        foreach (var profile in project)
        {
            var index = merged.FindIndex(own => string.Equals(own.Name, profile.Name, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                merged.Add(profile);
                continue;
            }

            var own = merged[index];
            var variables = new List<KeyValuePair<string, string>>(own.Environment);
            foreach (var variable in profile.Environment)
            {
                var at = variables.FindIndex(known => string.Equals(known.Key, variable.Key, StringComparison.OrdinalIgnoreCase));
                if (at < 0) variables.Add(variable);
                else variables[at] = variable;
            }

            merged[index] = new SpaceCliProfile(own.Name, variables, profile.Check ?? own.Check, profile.Login ?? own.Login);
        }

        // The project comes first: a variable it gives is kept by the last of its profiles that gives it, and taken out
        // of every other profile.
        var owners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var profile in project)
        {
            foreach (var (name, _) in profile.Environment) owners[name] = profile.Name;
        }

        bool Kept(SpaceCliProfile profile, string name)
            => !owners.TryGetValue(name, out var owner) || string.Equals(owner, profile.Name, StringComparison.OrdinalIgnoreCase);
        for (var index = 0; index < merged.Count; index++)
        {
            var other = merged[index];
            if (other.Environment.All(variable => Kept(other, variable.Key))) continue;
            merged[index] = other with { Environment = [.. other.Environment.Where(variable => Kept(other, variable.Key))] };
        }

        return this with { Cli = merged };
    }

    // A path: the user's own folder may be written ~.
    internal static bool IsPath(string? value) => SpaceSettings.IsLine(value, 1024);

    internal static bool IsEmail(string? value)
        => SpaceSettings.IsLine(value, 256) && !value!.Any(char.IsWhiteSpace)
            && value.IndexOf('@', StringComparison.Ordinal) is var at && at > 0 && at == value.LastIndexOf('@') && at < value.Length - 1;
}

/// <summary>The Git identity of a space.</summary>
/// <param name="Name">The name of the author and committer.</param>
/// <param name="Email">The email of the author and committer.</param>
/// <param name="SshKey">The private SSH key used to fetch and push.</param>
/// <param name="Sign">How commits are signed; <see langword="null"/> for the usual setting.</param>
/// <param name="SigningKey">The key commits are signed with: a public SSH key file, or a GPG key id.</param>
/// <param name="SignOff">Whether commits carry a <c>Signed-off-by</c> line; <see langword="null"/> for the usual.</param>
public sealed record SpaceGitIdentity(string? Name = null, string? Email = null, string? SshKey = null, SpaceGitSigning? Sign = null,
    string? SigningKey = null, bool? SignOff = null)
{
    /// <summary>Gets whether nothing is set.</summary>
    public bool IsEmpty => Name is null && Email is null && SshKey is null && Sign is null && SigningKey is null && SignOff is null;

    /// <summary>Tells what is wrong with the identity.</summary>
    /// <returns>The first problem found, or <see langword="null"/>.</returns>
    public string? Problem()
    {
        if (Name is not null && !SpaceSettings.IsLine(Name, 256)) return "A Git name is one line of at most 256 characters.";
        if (Email is not null && !SpaceIdentity.IsEmail(Email)) return $"'{Email}' is not an email.";
        if (SshKey is not null && !SpaceIdentity.IsPath(SshKey)) return "The SSH key is the path of a file, on one line.";
        if (SigningKey is not null && !SpaceIdentity.IsPath(SigningKey)) return "The signing key is a path or a key id, on one line.";
        return Sign is SpaceGitSigning.Ssh or SpaceGitSigning.Gpg && SigningKey is null
            ? "Signing commits needs the key to sign them with."
            : null;
    }

    /// <summary>Keeps what is valid.</summary>
    /// <returns>The identity without its invalid values; <see langword="null"/> when nothing is left.</returns>
    public SpaceGitIdentity? Sanitized()
    {
        var signingKey = SpaceIdentity.IsPath(SigningKey) ? SigningKey : null;
        var kept = new SpaceGitIdentity(
            SpaceSettings.IsLine(Name, 256) ? Name : null,
            SpaceIdentity.IsEmail(Email) ? Email : null,
            SpaceIdentity.IsPath(SshKey) ? SshKey : null,
            Sign is SpaceGitSigning.Ssh or SpaceGitSigning.Gpg && signingKey is null ? null : Sign,
            signingKey,
            SignOff);
        return kept.IsEmpty ? null : kept;
    }

    /// <summary>Reads how commits are signed, as the file writes it: <c>off</c>, <c>ssh</c> or <c>gpg</c>.</summary>
    /// <param name="value">The text.</param>
    /// <returns>The way, or <see langword="null"/> when the text is none of them.</returns>
    public static SpaceGitSigning? ParseSign(string? value)
        => value?.Trim().ToLowerInvariant() switch
        {
            "off" => SpaceGitSigning.Off,
            "ssh" => SpaceGitSigning.Ssh,
            "gpg" => SpaceGitSigning.Gpg,
            _ => null,
        };

    /// <summary>Writes how commits are signed, as the file reads it.</summary>
    /// <param name="sign">The way.</param>
    /// <returns><c>off</c>, <c>ssh</c>, <c>gpg</c>, or <see langword="null"/>.</returns>
    public static string? FormatSign(SpaceGitSigning? sign) => sign?.ToString().ToLowerInvariant();
}

/// <summary>The GitHub identity of a space: the folder of the GitHub CLI profile it uses.</summary>
/// <param name="ConfigDir">The folder given to the GitHub CLI as <c>GH_CONFIG_DIR</c>.</param>
public sealed record SpaceGitHubIdentity(string? ConfigDir = null)
{
    /// <summary>Gets whether nothing is set.</summary>
    public bool IsEmpty => ConfigDir is null;

    /// <summary>Tells what is wrong with the identity.</summary>
    /// <returns>The first problem found, or <see langword="null"/>.</returns>
    public string? Problem()
        => ConfigDir is not null && !SpaceIdentity.IsPath(ConfigDir) ? "The GitHub profile is the path of a folder, on one line." : null;

    /// <summary>Keeps what is valid.</summary>
    /// <returns>The identity, or <see langword="null"/> when nothing valid is set.</returns>
    public SpaceGitHubIdentity? Sanitized() => SpaceIdentity.IsPath(ConfigDir) ? this : null;
}

/// <summary>The Jira identity of a space: its site, its account and where its token is.</summary>
/// <param name="Site">The Jira site, such as <c>contoso.atlassian.net</c>.</param>
/// <param name="Email">The email of the account.</param>
/// <param name="TokenEnv">The name of the environment variable that holds the API token; never the token.</param>
public sealed record SpaceJiraIdentity(string? Site = null, string? Email = null, string? TokenEnv = null)
{
    /// <summary>Gets whether nothing is set.</summary>
    public bool IsEmpty => Site is null && Email is null && TokenEnv is null;

    /// <summary>Tells what is wrong with the identity.</summary>
    /// <returns>The first problem found, or <see langword="null"/>.</returns>
    public string? Problem()
    {
        if (Site is not null && !IsSite(Site)) return $"'{Site}' is not the address of a Jira site.";
        if (Email is not null && !SpaceIdentity.IsEmail(Email)) return $"'{Email}' is not an email.";
        return TokenEnv is not null && !IsVariableName(TokenEnv)
            ? "The Jira token is given by the name of an environment variable, such as CONTOSO_JIRA_TOKEN, never by the token itself."
            : null;
    }

    /// <summary>Keeps what is valid.</summary>
    /// <returns>The identity without its invalid values; <see langword="null"/> when nothing is left.</returns>
    public SpaceJiraIdentity? Sanitized()
    {
        var kept = new SpaceJiraIdentity(IsSite(Site) ? Site : null, SpaceIdentity.IsEmail(Email) ? Email : null, IsVariableName(TokenEnv) ? TokenEnv : null);
        return kept.IsEmpty ? null : kept;
    }

    /// <summary>Tells whether a text can be the name of an environment variable.</summary>
    /// <param name="value">The text.</param>
    /// <returns><see langword="true"/> for letters, digits and <c>_</c>, not starting with a digit, at most 128.</returns>
    public static bool IsVariableName(string? value)
        => value is { Length: > 0 and <= 128 } && !char.IsAsciiDigit(value[0])
            && value.All(static ch => char.IsAsciiLetterOrDigit(ch) || ch == '_');

    // A host name, with an optional https:// before it: contoso.atlassian.net.
    private static bool IsSite(string? value)
    {
        if (!SpaceSettings.IsLine(value, 253)) return false;
        var host = value!.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? value[8..].TrimEnd('/') : value;
        return host.Length > 0 && host.All(static ch => char.IsAsciiLetterOrDigit(ch) || ch is '.' or '-');
    }
}

/// <summary>
/// The profile of one command line tool (the Azure CLI, the AWS CLI, kubectl, …) in a space: the environment
/// variables that select its configuration, login or context, and the commands that check it and sign it in.
/// </summary>
/// <remarks>
/// A value is a path, a name of a profile or a context, never a secret: <c>~</c> at its start is the folder of the
/// user, and <c>${env:NAME}</c> is the value of a variable of the environment of the application. A variable whose
/// name says it holds a secret (a token, a password, a secret key) can only be given such a reference.
/// </remarks>
/// <param name="Name">The name of the profile, such as <c>azure</c> or <c>kubectl</c>.</param>
/// <param name="Environment">The variables it sets, in their order.</param>
/// <param name="Check">The command that tells who or what the tool is signed in to; <see langword="null"/> for none.</param>
/// <param name="Login">The command that signs the tool in with this profile; <see langword="null"/> for none.</param>
public sealed record SpaceCliProfile(string Name, IReadOnlyList<KeyValuePair<string, string>> Environment, string? Check = null, string? Login = null)
{
    /// <summary>The most profiles of one space.</summary>
    public const int MaximumProfiles = 32;

    /// <summary>The most variables of one profile.</summary>
    public const int MaximumVariables = 32;

    /// <summary>The longest name of a profile.</summary>
    public const int MaximumNameLength = 64;

    /// <summary>The longest value of a variable.</summary>
    public const int MaximumValueLength = 2048;

    /// <summary>The longest check or login command.</summary>
    public const int MaximumCommandLength = 2048;

    private const string ReferenceStart = "${env:";

    // The variables the Git and GitHub cards give: a profile does not set them.
    private static readonly FrozenSet<string> Reserved = FrozenSet.Create(StringComparer.OrdinalIgnoreCase,
        "GIT_AUTHOR_NAME", "GIT_AUTHOR_EMAIL", "GIT_COMMITTER_NAME", "GIT_COMMITTER_EMAIL", SpaceIdentityEnvironment.GitSshCommandVariable,
        SpaceIdentityEnvironment.GitConfigCountVariable, SpaceIdentityEnvironment.GitHubConfigDirectoryVariable);

    // The words of a name of a variable that holds a secret: AWS_SECRET_ACCESS_KEY, ARM_CLIENT_SECRET, VAULT_TOKEN.
    private static readonly FrozenSet<string> SecretWords = FrozenSet.Create(StringComparer.OrdinalIgnoreCase,
        "TOKEN", "SECRET", "PASSWORD", "PASSWD", "PASSPHRASE", "APIKEY", "PAT");

    // The last words of a name of a variable that holds where a secret is, not the secret: AZURE_FEDERATED_TOKEN_FILE.
    private static readonly FrozenSet<string> PlaceWords = FrozenSet.Create(StringComparer.OrdinalIgnoreCase, "FILE", "PATH", "DIR", "HOME");

    // Two words that together name a secret: an API key, an access key, a private key.
    private static readonly FrozenSet<string> SecretPairs = FrozenSet.Create(StringComparer.OrdinalIgnoreCase,
        "API_KEY", "ACCESS_KEY", "PRIVATE_KEY", "CLIENT_KEY", "ACCOUNT_KEY");

    /// <summary>Tells what is wrong with the profiles of a space.</summary>
    /// <param name="profiles">The profiles; <see langword="null"/> for none.</param>
    /// <returns>The first problem found, or <see langword="null"/>.</returns>
    public static string? Problem(IReadOnlyList<SpaceCliProfile>? profiles)
    {
        if (profiles is null || profiles.Count == 0) return null;
        if (profiles.Count > MaximumProfiles) return $"A space has at most {MaximumProfiles} CLI profiles.";
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var owners = new Dictionary<string, (string Profile, string Value)>(StringComparer.OrdinalIgnoreCase);
        foreach (var profile in profiles)
        {
            if (profile is null) return "A CLI profile is missing.";
            if (profile.ProblemOfItself(requireVariables: true) is { } problem) return problem;
            if (!names.Add(profile.Name)) return $"Two CLI profiles are named '{profile.Name}'.";
            foreach (var (name, value) in profile.Environment)
            {
                if (owners.TryGetValue(name, out var owner) && !string.Equals(owner.Value, value, StringComparison.Ordinal))
                    return $"{name} is given two values, by the CLI profiles '{owner.Profile}' and '{profile.Name}'.";
                owners[name] = (profile.Name, value);
            }
        }

        return null;
    }

    /// <summary>
    /// Tells what is wrong with one profile on its own: its name, its variables, its commands.
    /// </summary>
    /// <param name="requireVariables">Whether the profile sets at least one variable, as a profile of a space does.</param>
    /// <returns>The first problem found, or <see langword="null"/>.</returns>
    public string? ProblemOfItself(bool requireVariables)
    {
        if (!IsName(Name)) return $"'{Name}' is not the name of a CLI profile: 1 to {MaximumNameLength} letters, digits, -, _ and .";
        if (Environment is null || requireVariables && Environment.Count == 0) return $"The CLI profile '{Name}' sets no variable.";
        if (Environment.Count > MaximumVariables) return $"The CLI profile '{Name}' sets at most {MaximumVariables} variables.";
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in Environment)
        {
            if (VariableProblem(name, value) is { } problem) return $"The CLI profile '{Name}': {problem}";
            if (!seen.Add(name)) return $"The CLI profile '{Name}' sets {name} twice.";
        }

        if (Check is not null && !SpaceSettings.IsLine(Check, MaximumCommandLength)) return $"The check of the CLI profile '{Name}' is one command on one line.";
        return Login is not null && !SpaceSettings.IsLine(Login, MaximumCommandLength) ? $"The login of the CLI profile '{Name}' is one command on one line." : null;
    }

    /// <summary>Keeps what is valid of the profiles of a space, as a file may write them.</summary>
    /// <param name="profiles">The profiles; <see langword="null"/> for none.</param>
    /// <returns>
    /// The profiles without their invalid values: a variable that is not valid, or that another profile before gives
    /// another value, is left out, and so is a profile left without variable or named as one before it;
    /// <see langword="null"/> when none is left.
    /// </returns>
    public static IReadOnlyList<SpaceCliProfile>? Sanitized(IReadOnlyList<SpaceCliProfile>? profiles)
    {
        if (profiles is null || profiles.Count == 0) return null;
        var kept = new List<SpaceCliProfile>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var owners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var profile in profiles)
        {
            if (kept.Count == MaximumProfiles) break;
            if (profile?.Sanitized(owners) is not { Environment.Count: > 0 } valid || !names.Add(valid.Name)) continue;
            foreach (var (name, value) in valid.Environment) owners.TryAdd(name, value);
            kept.Add(valid);
        }

        return kept.Count == 0 ? null : kept;
    }

    /// <summary>Keeps what is valid of one profile on its own, as a project may write it.</summary>
    /// <returns>The profile without its invalid values, or <see langword="null"/> when its name is not valid.</returns>
    public SpaceCliProfile? Sanitized() => Sanitized(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

    /// <summary>Tells whether a text can be the name of a profile.</summary>
    /// <param name="value">The text.</param>
    /// <returns><see langword="true"/> for 1 to 64 letters, digits, <c>-</c>, <c>_</c> and <c>.</c>.</returns>
    public static bool IsName([NotNullWhen(true)] string? value)
        => value is { Length: > 0 and <= MaximumNameLength } && value.All(static ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.');

    /// <summary>Tells whether the name of a variable says that it holds a secret, such as a token or a password.</summary>
    /// <param name="name">The name of the variable.</param>
    /// <returns>
    /// <see langword="true"/> for a name such as <c>VAULT_TOKEN</c>, <c>AWS_SECRET_ACCESS_KEY</c> or <c>OPENAI_API_KEY</c>;
    /// <see langword="false"/> for one that ends in a word of a place, such as <c>AZURE_FEDERATED_TOKEN_FILE</c>, which
    /// holds where a secret is.
    /// </returns>
    public static bool IsSecretName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var words = name.Split('_', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length > 0 && PlaceWords.Contains(words[^1])) return false;
        for (var index = 0; index < words.Length; index++)
        {
            if (SecretWords.Contains(words[index])) return true;
            if (index > 0 && SecretPairs.Contains(words[index - 1] + "_" + words[index])) return true;
        }

        return false;
    }

    /// <summary>Tells what is wrong with one variable of a profile.</summary>
    /// <param name="name">The name of the variable.</param>
    /// <param name="value">Its value, as written.</param>
    /// <returns>What is wrong, or <see langword="null"/>.</returns>
    public static string? VariableProblem(string? name, string? value)
    {
        if (name is null || !SpaceJiraIdentity.IsVariableName(name)) return $"'{name}' is not the name of a variable: letters, digits and _.";
        if (IsReserved(name)) return $"{name} is given by the Git and GitHub cards of the identity.";
        if (!SpaceSettings.IsLine(value, MaximumValueLength)) return $"The value of {name} is one line of at most {MaximumValueLength} characters.";
        if (ReferenceProblem(value) is { } reference) return $"The value of {name}: {reference}";
        return IsSecretName(name) && !IsLoneReference(value)
            ? $"{name} holds a secret: give it as ${{env:NAME}}, the name of a variable of your environment, never the secret itself."
            : null;
    }

    /// <summary>Lists the variables of the environment of the application a value refers to, as <c>${env:NAME}</c>.</summary>
    /// <param name="value">The value, as written.</param>
    /// <returns>The names, in their order.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    public static IReadOnlyList<string> References(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var names = new List<string>();
        for (var start = value.IndexOf(ReferenceStart, StringComparison.Ordinal); start >= 0; start = value.IndexOf(ReferenceStart, start + 1, StringComparison.Ordinal))
        {
            var end = value.IndexOf('}', start);
            if (end > start) names.Add(value[(start + ReferenceStart.Length)..end]);
        }

        return names;
    }

    /// <summary>
    /// Gives the value a process is given for a value as written: <c>~</c> at its start, or after the separator of a
    /// list of paths, is the folder of the user, and each <c>${env:NAME}</c> is the value of that variable.
    /// </summary>
    /// <param name="value">The value, as written.</param>
    /// <param name="inherited">Reads a variable of the environment of the application.</param>
    /// <param name="userProfile">The folder of the user.</param>
    /// <param name="windows">Whether the processes run on Windows, where a list of paths is separated by <c>;</c>, not <c>:</c>.</param>
    /// <param name="missing">The first variable it refers to that is not set; <see langword="null"/> when all are.</param>
    /// <returns>The value, or <see langword="null"/> when a variable it refers to is not set.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public static string? Resolve(string value, Func<string, string?> inherited, string userProfile, bool windows, out string? missing)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(inherited);
        ArgumentNullException.ThrowIfNull(userProfile);
        missing = null;
        var separator = windows ? ';' : ':';
        var text = new StringBuilder(value.Length);
        var index = 0;
        while (index < value.Length)
        {
            if (value.AsSpan(index).StartsWith(ReferenceStart, StringComparison.Ordinal) && value.IndexOf('}', index) is var end and > 0)
            {
                var name = value[(index + ReferenceStart.Length)..end];
                if (inherited(name) is not { } set)
                {
                    missing = name;
                    return null;
                }

                text.Append(set);
                index = end + 1;
                continue;
            }

            // ~ alone, or ~/ and ~\, at the start of the value or of a path of a list.
            if (value[index] == '~' && (index == 0 || value[index - 1] == separator)
                && (index + 1 == value.Length || value[index + 1] is '/' or '\\' || value[index + 1] == separator))
            {
                text.Append(userProfile.TrimEnd('/', '\\'));
                index++;
                continue;
            }

            text.Append(value[index++]);
        }

        return text.ToString();
    }

    private static bool IsReserved(string name)
        => Reserved.Contains(name)
            || name.StartsWith("GIT_CONFIG_KEY_", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("GIT_CONFIG_VALUE_", StringComparison.OrdinalIgnoreCase);

    // Every ${ starts a reference ${env:NAME} to a variable.
    private static string? ReferenceProblem(string value)
    {
        for (var start = value.IndexOf("${", StringComparison.Ordinal); start >= 0; start = value.IndexOf("${", start + 2, StringComparison.Ordinal))
        {
            var end = value.IndexOf('}', start);
            if (!value.AsSpan(start).StartsWith(ReferenceStart, StringComparison.Ordinal) || end < 0
                || !SpaceJiraIdentity.IsVariableName(value[(start + ReferenceStart.Length)..end]))
                return "${...} is only ${env:NAME}, the value of a variable of your environment.";
        }

        return null;
    }

    private static bool IsLoneReference(string value)
        => value.StartsWith(ReferenceStart, StringComparison.Ordinal) && value.EndsWith('}') && value.IndexOf('}') == value.Length - 1;

    // The valid values of this profile, without a variable another profile before it gives another value.
    private SpaceCliProfile? Sanitized(Dictionary<string, string> owners)
    {
        if (!IsName(Name)) return null;
        var variables = new List<KeyValuePair<string, string>>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in Environment ?? [])
        {
            if (variables.Count == MaximumVariables) break;
            if (VariableProblem(name, value) is not null || !seen.Add(name)) continue;
            if (owners.TryGetValue(name, out var other) && !string.Equals(other, value, StringComparison.Ordinal)) continue;
            variables.Add(new(name, value));
        }

        return new SpaceCliProfile(Name, variables,
            SpaceSettings.IsLine(Check, MaximumCommandLength) ? Check : null,
            SpaceSettings.IsLine(Login, MaximumCommandLength) ? Login : null);
    }
}

/// <summary>The tools the sessions of a space do without.</summary>
/// <param name="Disabled">The names of the tools, such as <c>shell_command</c>.</param>
public sealed record SpaceTools(IReadOnlyList<string> Disabled)
{
    /// <summary>The most tools a space turns off.</summary>
    public const int MaximumTools = 64;

    /// <summary>Gets whether no tool is turned off.</summary>
    public bool IsEmpty => Disabled.Count == 0;

    /// <summary>Tells what is wrong with the list.</summary>
    /// <returns>The first problem found, or <see langword="null"/>.</returns>
    public string? Problem()
    {
        if (Disabled.Count > MaximumTools) return $"A space turns off at most {MaximumTools} tools.";
        return Disabled.FirstOrDefault(static name => !IsToolName(name)) is { } wrong ? $"'{wrong}' is not the name of a tool." : null;
    }

    /// <summary>Keeps what is valid: each valid name once.</summary>
    /// <returns>The list, or <see langword="null"/> when no valid name is left.</returns>
    public SpaceTools? Sanitized()
    {
        var kept = Disabled.Where(IsToolName).Distinct(StringComparer.Ordinal).Take(MaximumTools).ToArray();
        return kept.Length == 0 ? null : new SpaceTools(kept);
    }

    /// <summary>Tells whether a text can be the name of a tool.</summary>
    /// <param name="name">The text.</param>
    /// <returns><see langword="true"/> for 1 to 128 letters, digits, <c>_</c>, <c>-</c>, <c>.</c> and <c>:</c>.</returns>
    public static bool IsToolName(string? name)
        => name is { Length: > 0 and <= 128 } && name.All(static ch => char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-' or '.' or ':');
}
