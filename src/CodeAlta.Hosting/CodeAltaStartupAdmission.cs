namespace CodeAlta.Hosting;

/// <summary>Orders pure built-in early output before synchronous, lease-protected application startup.</summary>
public static class CodeAltaStartupAdmission
{
    /// <summary>Dispatches a sole early flag or runs the admitted startup while retaining its exact lease.</summary>
    /// <param name="arguments">The raw arguments; this operation does not parse plugin arguments.</param>
    /// <param name="runEarlyCommand">Plain built-in output for a sole --help, -h or --version argument.</param>
    /// <param name="acquireLease">Resolves the selected lock path and transfers lease disposal ownership to this operation.</param>
    /// <param name="runAdmittedStartup">The complete synchronous startup, command and cleanup traversal.</param>
    /// <returns>The selected callback's exit code.</returns>
    /// <remarks>
    /// All arguments are validated before any callback. Early output never invokes acquisition or startup;
    /// every other argument sequence acquires first, including empty, plugin, status and mixed-help inputs.
    /// Callbacks and release stay on the calling thread. Startup must include plugin/terminal cleanup and
    /// logging shutdown before returning. A lease is released once even on startup failure; no retry occurs.
    /// Callback exceptions propagate. Normal using semantics apply: a release exception supersedes a
    /// simultaneous startup exception. This operation neither changes the lock algorithm nor owns a runtime.
    /// </remarks>
    /// <exception cref="ArgumentNullException">An argument collection or callback is null.</exception>
    /// <exception cref="InvalidOperationException">Acquisition returns no lease.</exception>
    /// <exception cref="Exception">A selected callback or lease release fails.</exception>
    public static int Run(
        IReadOnlyList<string> arguments,
        Func<string, int> runEarlyCommand,
        Func<IDisposable> acquireLease,
        Func<int> runAdmittedStartup)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(runEarlyCommand);
        ArgumentNullException.ThrowIfNull(acquireLease);
        ArgumentNullException.ThrowIfNull(runAdmittedStartup);

        if (arguments.Count == 1 && arguments[0] is "--help" or "-h" or "--version")
        {
            return runEarlyCommand(arguments[0]);
        }

        using var lease = acquireLease() ?? throw new InvalidOperationException("Startup admission did not return a lease.");
        return runAdmittedStartup();
    }
}
