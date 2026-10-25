using System.Globalization;
using CodeAlta.Hosting;

namespace CodeAlta.Cli;

/// <summary>Where the sessions of a run of <c>altacli</c> go.</summary>
internal enum CliStorageMode
{
    /// <summary>The sessions folder of <c>~/.alta</c>: CodeAlta Desktop and TUI list the sessions (the default).</summary>
    Visible,

    /// <summary>The state root <c>~/.alta/cli</c>, with the configuration, credentials and skills of <c>~/.alta</c>.</summary>
    Isolated,

    /// <summary>A whole separate profile (<c>--profile</c>, <c>CODEALTA_HOME</c>): its own configuration and sessions.</summary>
    Profile,
}

/// <summary>
/// The roots of a run. In every mode the run has a private application database and scratchpads of its own, takes no
/// <c>alta.lock</c>, and writes nothing under the global root but the sessions of its state root, its log, and what
/// providers and plugins write themselves (<c>auth/</c>, <c>cache/</c>).
/// </summary>
/// <param name="Mode">The mode.</param>
/// <param name="Ephemeral">Whether the state root is a temporary folder deleted at the end (<c>--ephemeral</c>).</param>
/// <param name="GlobalRoot">The root of the configuration, credentials, prompts and skills.</param>
/// <param name="StateRoot">The root of the sessions of the run.</param>
internal sealed record CliStorage(CliStorageMode Mode, bool Ephemeral, string GlobalRoot, string StateRoot)
{
    /// <summary>The folder of the isolated state root, under the global root.</summary>
    public const string IsolatedFolderName = "cli";

    /// <summary>Gets the name of the mode, as the <c>_codealta/session</c> header gives it.</summary>
    public string Name => Ephemeral ? Mode.ToString().ToLowerInvariant() + "+ephemeral" : Mode.ToString().ToLowerInvariant();

    /// <summary>Gets whether the run drops a marker for each new session, so that CodeAlta Desktop and TUI list it.</summary>
    public bool WritesMarkers => Mode == CliStorageMode.Visible && !Ephemeral;

    /// <summary>
    /// Resolves the roots of the options: <c>--profile</c> (or <see cref="CliEnvironment.HomeVariable"/>) first, then
    /// <c>--isolated</c>, else the visible mode; <c>--dev</c> takes the state root of the developer instance
    /// (<c>~/.alta/dev</c>) instead of the normal one; <c>--ephemeral</c> replaces the state root with a new temporary folder.
    /// </summary>
    /// <returns>False with a message when the profile folder does not exist.</returns>
    public static bool TryResolve(CliCommonOptions options, CliEnvironment environment, out CliStorage? storage, out string? error)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(environment);
        storage = null;
        error = null;
        var profile = options.Profile ?? NonBlank(environment.GetVariable(CliEnvironment.HomeVariable));
        CliStorageMode mode;
        string globalRoot;
        string stateRoot;
        if (profile is not null && options.Developer)
        {
            error = "--dev uses the developer instance of ~/.alta: it does not combine with a separate profile.";
            return false;
        }

        if (profile is not null)
        {
            var folder = Path.GetFullPath(profile, environment.CurrentDirectory);
            if (!Directory.Exists(folder))
            {
                error = options.Profile is null
                    ? $"The profile folder '{folder}' of {CliEnvironment.HomeVariable} does not exist."
                    : $"The profile folder '{folder}' does not exist: create it first.";
                return false;
            }

            (mode, globalRoot, stateRoot) = (CliStorageMode.Profile, folder, folder);
        }
        else
        {
            globalRoot = environment.DefaultProfile;
            // The developer instance (alta --dev) keeps its sessions in a folder of its own: the run shares them.
            var instanceRoot = CodeAltaInstanceProfile.Create(globalRoot, options.Developer).StateRoot;
            (mode, stateRoot) = options.Isolated
                ? (CliStorageMode.Isolated, Path.Combine(instanceRoot, IsolatedFolderName))
                : (CliStorageMode.Visible, instanceRoot);
        }

        if (options.Ephemeral)
        {
            stateRoot = Path.Combine(environment.TempRoot, "codealta-cli-ephemeral",
                string.Create(CultureInfo.InvariantCulture, $"{Environment.ProcessId}-{Guid.NewGuid():N}"));
        }

        storage = new CliStorage(mode, options.Ephemeral, globalRoot, stateRoot);
        return true;
    }

    /// <summary>Deletes the state root of an ephemeral run, best effort.</summary>
    public void DeleteEphemeralState()
    {
        if (!Ephemeral) return;
        try
        {
            if (Directory.Exists(StateRoot)) Directory.Delete(StateRoot, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A file a program the run started still holds: the temporary folder keeps it.
        }
    }

    private static string? NonBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
