namespace CodeAlta.Sandbox;

/// <summary>Whether the sandbox can run on this computer, and what it can do here.</summary>
/// <param name="IsAvailable">Whether a sandbox can start.</param>
/// <param name="Backend">The name of the backend, such as <c>Windows process container</c>.</param>
/// <param name="Reason">Why it cannot start, or what it cannot do here; null when nothing is missing.</param>
/// <param name="Setup">What the user can do about it, or null.</param>
/// <param name="Networks">The networks a profile can use here.</param>
public sealed record SandboxAvailability(bool IsAvailable, string Backend, string? Reason, string? Setup, IReadOnlySet<SandboxNetwork> Networks)
{
    /// <summary>Creates the availability of a computer where no sandbox can start.</summary>
    /// <param name="backend">The backend that was looked for.</param>
    /// <param name="reason">Why it cannot start.</param>
    /// <param name="setup">What the user can do, or null.</param>
    /// <returns>The availability.</returns>
    public static SandboxAvailability Unavailable(string backend, string reason, string? setup)
        => new(false, backend, reason, setup, new HashSet<SandboxNetwork>());

    /// <summary>Says why a policy cannot run here, or null when it can.</summary>
    /// <param name="network">The network of the policy.</param>
    /// <returns>The reason, for the user and the agent.</returns>
    public string? Refusal(SandboxNetwork network)
        => !IsAvailable ? $"The sandbox is not available on this computer: {Reason}{(Setup is null ? "" : " " + Setup)}"
            : Networks.Contains(network) ? null
            : $"The sandbox cannot give the network '{network.ToString().ToLowerInvariant()}' on this computer.{(Reason is null ? "" : " " + Reason)}{(Setup is null ? "" : " " + Setup)}";
}

/// <summary>A program started in a sandbox.</summary>
public interface ISandboxProcess : IAsyncDisposable
{
    /// <summary>Gets the process id, as the system knows it.</summary>
    int Id { get; }

    /// <summary>Gets what is written to the program.</summary>
    Stream StandardInput { get; }

    /// <summary>Gets what the program writes.</summary>
    Stream StandardOutput { get; }

    /// <summary>Gets what the program writes as errors.</summary>
    Stream StandardError { get; }

    /// <summary>Waits until the program ends.</summary>
    /// <param name="cancellationToken">Stops the wait, not the program.</param>
    /// <returns>Its exit code.</returns>
    Task<int> WaitForExitAsync(CancellationToken cancellationToken);

    /// <summary>Ends the program and every process it started.</summary>
    void Kill();
}

/// <summary>Starts programs in a sandbox.</summary>
public interface ISandboxBackend
{
    /// <summary>Looks at what this computer can do; cheap after the first call.</summary>
    /// <returns>The availability.</returns>
    SandboxAvailability GetAvailability();

    /// <summary>Starts a program under a policy.</summary>
    /// <param name="policy">The policy.</param>
    /// <param name="commandLine">The command line of the program, its executable quoted.</param>
    /// <param name="proxyEndpoint">The loopback address of the proxy of <see cref="SandboxNetwork.Hosts"/>, such as <c>http://127.0.0.1:50123</c>; null otherwise.</param>
    /// <param name="cancellationToken">Cancels the start.</param>
    /// <returns>The program.</returns>
    /// <exception cref="SandboxUnavailableException">The sandbox cannot run the policy here.</exception>
    Task<ISandboxProcess> StartAsync(SandboxPolicy policy, string commandLine, string? proxyEndpoint, CancellationToken cancellationToken);
}

/// <summary>A sandbox that cannot start: the backend is missing, or refuses the policy.</summary>
public sealed class SandboxUnavailableException : Exception
{
    /// <summary>Initializes the exception.</summary>
    /// <param name="message">Why, for the user and the agent.</param>
    /// <param name="innerException">The failure of the backend, or null.</param>
    public SandboxUnavailableException(string message, Exception? innerException = null) : base(message, innerException)
    {
    }
}
