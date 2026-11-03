using System.Diagnostics;

namespace CodeAlta.Fleet.Update;

/// <summary>How a version is started: an executable and the arguments before those of the command.</summary>
/// <param name="FileName">The executable: the app host of the version, or <c>dotnet</c>.</param>
/// <param name="PrefixArguments">The arguments before the command's: <c>exec &lt;assembly&gt;</c> for <c>dotnet</c>, none for an app host.</param>
public sealed record UpdateCommand(string FileName, IReadOnlyList<string> PrefixArguments);

/// <summary>A process of a version that a launcher started.</summary>
public interface IUpdateProcess : IDisposable
{
    /// <summary>Gets the id of the process.</summary>
    int Id { get; }

    /// <summary>Waits until the process exits.</summary>
    /// <param name="cancellationToken">Stops waiting.</param>
    /// <returns>Its exit code.</returns>
    Task<int> WaitForExitAsync(CancellationToken cancellationToken);

    /// <summary>Asks the process to stop gracefully: SIGTERM on Unix. Windows has no such request: the caller kills it after a grace.</summary>
    /// <returns>Whether the request was sent.</returns>
    bool RequestStop();

    /// <summary>Ends the process at once.</summary>
    void Kill();
}

/// <summary>Starts the processes of versions.</summary>
public interface IUpdateProcessRunner
{
    /// <summary>Starts a version, with the standard streams of this process.</summary>
    /// <param name="command">How the version starts.</param>
    /// <param name="arguments">The arguments of the command (<c>run</c> and its options).</param>
    /// <param name="environment">Variables to set, or to remove when null.</param>
    /// <returns>The process.</returns>
    IUpdateProcess Start(UpdateCommand command, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string?> environment);
}

/// <summary>Starts versions as child processes that share the console and the standard streams of this one.</summary>
public sealed class UpdateProcessRunner : IUpdateProcessRunner
{
    /// <inheritdoc />
    public IUpdateProcess Start(UpdateCommand command, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(environment);
        var info = new ProcessStartInfo(command.FileName) { UseShellExecute = false };
        foreach (var argument in command.PrefixArguments) info.ArgumentList.Add(argument);
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        foreach (var (name, value) in environment)
        {
            if (value is null) info.Environment.Remove(name);
            else info.Environment[name] = value;
        }

        var process = Process.Start(info) ?? throw new InvalidOperationException($"{command.FileName} did not start.");
        return new ChildProcess(process);
    }

    private sealed class ChildProcess(Process process) : IUpdateProcess
    {
        public int Id => process.Id;

        public async Task<int> WaitForExitAsync(CancellationToken cancellationToken)
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return process.ExitCode;
        }

        public bool RequestStop()
        {
            if (OperatingSystem.IsWindows()) return false;
            try
            {
                using var kill = Process.Start(new ProcessStartInfo("kill") { ArgumentList = { "-TERM", process.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) }, UseShellExecute = false });
                kill?.WaitForExit(5000);
                return kill is not null;
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                return false;
            }
        }

        public void Kill()
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // It exited meanwhile.
            }
        }

        public void Dispose() => process.Dispose();
    }
}
