namespace CodeAlta.Hosting;

/// <summary>
/// Where one running CodeAlta instance keeps its data: the profile every instance shares and the state only
/// this instance writes.
/// </summary>
/// <remarks>
/// <para>
/// The normal instance keeps both in the user's <c>.alta</c> directory and is the only one allowed on it.
/// </para>
/// <para>
/// A developer instance (<see cref="DeveloperOption"/>) shares that directory for configuration, providers,
/// credentials, prompts, skills and the project catalog, but keeps what two processes must not write
/// together — sessions, the session cache, view state, prompt drafts, logs and the single-instance lock —
/// in its <c>dev</c> subdirectory. It can therefore run beside the normal instance, which is how CodeAlta is
/// developed with CodeAlta. Only one developer instance runs at a time.
/// </para>
/// </remarks>
public sealed record CodeAltaInstanceProfile
{
    /// <summary>The command-line option that selects the developer instance.</summary>
    public const string DeveloperOption = "--dev";

    /// <summary>The directory of the developer instance's state, under the global root.</summary>
    public const string DeveloperStateDirectoryName = "dev";

    private const string LockFileName = "alta.lock";

    private CodeAltaInstanceProfile(string globalRoot, string stateRoot, bool isDeveloper)
    {
        GlobalRoot = globalRoot;
        StateRoot = stateRoot;
        IsDeveloper = isDeveloper;
    }

    /// <summary>Gets the profile directory shared by every instance, normally <c>~/.alta</c>.</summary>
    public string GlobalRoot { get; }

    /// <summary>Gets the directory of the state this instance alone writes.</summary>
    public string StateRoot { get; }

    /// <summary>Gets whether this is the developer instance.</summary>
    public bool IsDeveloper { get; }

    /// <summary>Gets the path of the lock that admits one instance on <see cref="StateRoot"/>.</summary>
    public string LockFilePath => Path.Combine(StateRoot, LockFileName);

    /// <summary>Resolves the profile a command line asks for, in the user's <c>.alta</c> directory.</summary>
    /// <param name="arguments">The command-line arguments of the process.</param>
    /// <returns>The developer profile when <see cref="DeveloperOption"/> is among the arguments, otherwise the normal one.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="arguments"/> is null.</exception>
    /// <exception cref="InvalidOperationException">The user profile directory cannot be determined.</exception>
    public static CodeAltaInstanceProfile FromArguments(IEnumerable<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        return Create(GetDefaultGlobalRoot(), IsDeveloperRequested(arguments));
    }

    /// <summary>Creates the profile of an instance on a global root.</summary>
    /// <param name="globalRoot">The shared profile directory.</param>
    /// <param name="developer">Whether to create the developer instance's profile.</param>
    /// <returns>The profile.</returns>
    /// <exception cref="ArgumentException"><paramref name="globalRoot"/> is empty.</exception>
    public static CodeAltaInstanceProfile Create(string globalRoot, bool developer)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(globalRoot);
        var root = Path.GetFullPath(globalRoot);
        return new(root, developer ? Path.Combine(root, DeveloperStateDirectoryName) : root, developer);
    }

    /// <summary>Tells whether a command line asks for the developer instance.</summary>
    /// <param name="arguments">The command-line arguments of the process.</param>
    /// <returns>True when <see cref="DeveloperOption"/> is among them.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="arguments"/> is null.</exception>
    public static bool IsDeveloperRequested(IEnumerable<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        return arguments.Contains(DeveloperOption, StringComparer.Ordinal);
    }

    /// <summary>Gets the user's <c>.alta</c> directory.</summary>
    /// <returns>The default global root.</returns>
    /// <exception cref="InvalidOperationException">The user profile directory cannot be determined.</exception>
    public static string GetDefaultGlobalRoot()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(userProfile))
        {
            throw new InvalidOperationException("Unable to determine the user profile directory for CodeAlta.");
        }

        return Path.Combine(userProfile, ".alta");
    }
}
