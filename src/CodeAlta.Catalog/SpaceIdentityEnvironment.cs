using System.Globalization;
using CodeAlta.Agent;

namespace CodeAlta.Catalog;

/// <summary>
/// Turns the identity of a space into the environment variables the processes of its sessions start with: the Git
/// author and committer, the SSH key Git fetches and pushes with, how commits are signed, and the profile of the
/// GitHub CLI. What the identity leaves out is not set, so the user's usual Git and GitHub settings apply.
/// </summary>
/// <remarks>
/// Git reads its settings from <c>GIT_CONFIG_COUNT</c>, <c>GIT_CONFIG_KEY_n</c> and <c>GIT_CONFIG_VALUE_n</c> after
/// its files: the signing settings are numbered after the ones the environment of the application already gives,
/// which the processes inherit, so none of those is lost. A repository's own <c>.git/config</c> comes before them.
/// </remarks>
public static class SpaceIdentityEnvironment
{
    /// <summary>The variable of the GitHub CLI that names the folder of its profile.</summary>
    public const string GitHubConfigDirectoryVariable = "GH_CONFIG_DIR";

    /// <summary>The variable that gives Git the command it fetches and pushes over SSH with.</summary>
    public const string GitSshCommandVariable = "GIT_SSH_COMMAND";

    /// <summary>The variable that counts the settings Git reads from the environment.</summary>
    public const string GitConfigCountVariable = "GIT_CONFIG_COUNT";

    /// <summary>Creates the environment of the sessions of a space, over the environment of this process.</summary>
    /// <param name="identity">The identity of the space; <see langword="null"/> for none.</param>
    /// <returns>The variables to set; <see cref="AgentProcessEnvironment.Empty"/> for an identity that sets nothing.</returns>
    public static AgentProcessEnvironment Create(SpaceIdentity? identity)
        => Create(identity, Environment.GetEnvironmentVariable, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), OperatingSystem.IsWindows());

    /// <summary>Creates the environment of the sessions of a space.</summary>
    /// <param name="identity">The identity of the space; <see langword="null"/> for none.</param>
    /// <param name="inherited">Reads a variable of the environment the processes inherit.</param>
    /// <param name="userProfile">The folder of the user, which a path written <c>~</c> starts from.</param>
    /// <param name="windows">Whether the processes run on Windows.</param>
    /// <returns>The variables to set; <see cref="AgentProcessEnvironment.Empty"/> for an identity that sets nothing.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="inherited"/> or <paramref name="userProfile"/> is <see langword="null"/>.</exception>
    public static AgentProcessEnvironment Create(SpaceIdentity? identity, Func<string, string?> inherited, string userProfile, bool windows)
    {
        ArgumentNullException.ThrowIfNull(inherited);
        ArgumentNullException.ThrowIfNull(userProfile);
        if (identity is null || identity.IsEmpty) return AgentProcessEnvironment.Empty;
        var variables = new List<KeyValuePair<string, string>>();
        if (identity.Git is { } git)
        {
            if (git.Name is { } name)
            {
                variables.Add(new("GIT_AUTHOR_NAME", name));
                variables.Add(new("GIT_COMMITTER_NAME", name));
            }

            if (git.Email is { } email)
            {
                variables.Add(new("GIT_AUTHOR_EMAIL", email));
                variables.Add(new("GIT_COMMITTER_EMAIL", email));
            }

            if (git.SshKey is { } key)
            {
                variables.Add(new(GitSshCommandVariable, SshCommand(ExpandPath(key, userProfile), windows)));
            }

            var settings = SigningSettings(git, userProfile);
            if (settings.Count > 0)
            {
                // After the settings the application was given, which its processes inherit with this count replaced.
                var first = int.TryParse(inherited(GitConfigCountVariable), NumberStyles.None, CultureInfo.InvariantCulture, out var given) && given is >= 0 and < 10_000 ? given : 0;
                for (var index = 0; index < settings.Count; index++)
                {
                    var number = (first + index).ToString(CultureInfo.InvariantCulture);
                    variables.Add(new("GIT_CONFIG_KEY_" + number, settings[index].Key));
                    variables.Add(new("GIT_CONFIG_VALUE_" + number, settings[index].Value));
                }

                variables.Add(new(GitConfigCountVariable, (first + settings.Count).ToString(CultureInfo.InvariantCulture)));
            }
        }

        if (identity.GitHub?.ConfigDir is { } folder)
        {
            variables.Add(new(GitHubConfigDirectoryVariable, ExpandPath(folder, userProfile)));
        }

        return variables.Count == 0 ? AgentProcessEnvironment.Empty : new AgentProcessEnvironment(variables);
    }

    /// <summary>Gives the full path of a path the user wrote, where <c>~</c> is the folder of the user.</summary>
    /// <param name="path">The path as written.</param>
    /// <param name="userProfile">The folder of the user.</param>
    /// <returns>The path with <c>~</c> replaced; any other path as it is.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> or <paramref name="userProfile"/> is <see langword="null"/>.</exception>
    public static string ExpandPath(string path, string userProfile)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(userProfile);
        if (path == "~") return userProfile;
        return path.Length > 1 && path[0] == '~' && path[1] is '/' or '\\'
            ? Path.Join(userProfile, path[2..])
            : path;
    }

    /// <summary>
    /// Writes the command Git runs to reach a remote over SSH with one key only. Git runs it in a shell (the one it
    /// ships with on Windows), so the path is quoted for a POSIX shell, and written with <c>/</c> on Windows, which
    /// every SSH client there reads.
    /// </summary>
    /// <param name="keyPath">The full path of the private key.</param>
    /// <param name="windows">Whether Git runs on Windows.</param>
    /// <returns>The value of <c>GIT_SSH_COMMAND</c>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="keyPath"/> is <see langword="null"/>.</exception>
    public static string SshCommand(string keyPath, bool windows)
    {
        ArgumentNullException.ThrowIfNull(keyPath);
        var path = windows ? keyPath.Replace('\\', '/') : keyPath;
        return $"ssh -i {QuotePosix(path)} -o IdentitiesOnly=yes";
    }

    /// <summary>Quotes a text as one word of a POSIX shell: in single quotes, a single quote in it written <c>'\''</c>.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The quoted text.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is <see langword="null"/>.</exception>
    public static string QuotePosix(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return "'" + text.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
    }

    // The settings of Git that sign commits as the space says: the key, its kind, and whether commits are signed.
    private static List<KeyValuePair<string, string>> SigningSettings(SpaceGitIdentity git, string userProfile)
    {
        var settings = new List<KeyValuePair<string, string>>(3);
        if (git.SigningKey is { } key)
        {
            // A public key file for SSH signing is a path; a GPG key id is not, and is never written ~.
            settings.Add(new("user.signingkey", key.StartsWith('~') ? ExpandPath(key, userProfile) : key));
        }

        switch (git.Sign)
        {
            case SpaceGitSigning.Ssh:
                settings.Add(new("gpg.format", "ssh"));
                settings.Add(new("commit.gpgsign", "true"));
                break;
            case SpaceGitSigning.Gpg:
                settings.Add(new("gpg.format", "openpgp"));
                settings.Add(new("commit.gpgsign", "true"));
                break;
            case SpaceGitSigning.Off:
                settings.Add(new("commit.gpgsign", "false"));
                break;
        }

        return settings;
    }
}
