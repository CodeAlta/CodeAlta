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
public sealed record SpaceNewSessions(string? AgentPrompt = null, SpaceModelTiers? Tiers = null)
{
    /// <summary>The longest identifier of an agent prompt.</summary>
    public const int MaximumAgentPromptLength = 128;

    /// <summary>Gets whether nothing is set.</summary>
    public bool IsEmpty => AgentPrompt is null && (Tiers?.IsEmpty ?? true);

    /// <summary>Tells what is wrong with the settings.</summary>
    /// <returns>The first problem found, or <see langword="null"/>.</returns>
    public string? Problem()
        => AgentPrompt is not null && !IsPromptId(AgentPrompt)
            ? $"'{AgentPrompt}' is not the identifier of an agent prompt."
            : Tiers?.Problem();

    /// <summary>Keeps what is valid.</summary>
    /// <returns>The settings without their invalid values; <see langword="null"/> when nothing is left.</returns>
    public SpaceNewSessions? Sanitized()
    {
        var kept = new SpaceNewSessions(IsPromptId(AgentPrompt) ? AgentPrompt!.Trim() : null, Tiers?.Sanitized());
        return kept.IsEmpty ? null : kept;
    }

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
/// The identity the sessions of a space work under: Git, GitHub and Jira. A part left out is the usual setting of
/// the user, so a space without identity changes nothing.
/// </summary>
/// <param name="Git">The Git author, keys and signing; <see langword="null"/> for the usual ones.</param>
/// <param name="GitHub">The GitHub CLI profile; <see langword="null"/> for the usual one.</param>
/// <param name="Jira">The Jira site and account; <see langword="null"/> for the usual ones.</param>
public sealed record SpaceIdentity(SpaceGitIdentity? Git = null, SpaceGitHubIdentity? GitHub = null, SpaceJiraIdentity? Jira = null)
{
    /// <summary>Gets whether nothing is set.</summary>
    public bool IsEmpty => (Git?.IsEmpty ?? true) && (GitHub?.IsEmpty ?? true) && (Jira?.IsEmpty ?? true);

    /// <summary>Tells what is wrong with the identity.</summary>
    /// <returns>The first problem found, or <see langword="null"/>.</returns>
    public string? Problem() => Git?.Problem() ?? GitHub?.Problem() ?? Jira?.Problem();

    /// <summary>Keeps what is valid.</summary>
    /// <returns>The identity without its invalid values; <see langword="null"/> when nothing is left.</returns>
    public SpaceIdentity? Sanitized()
    {
        var kept = new SpaceIdentity(Git?.Sanitized(), GitHub?.Sanitized(), Jira?.Sanitized());
        return kept.IsEmpty ? null : kept;
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
