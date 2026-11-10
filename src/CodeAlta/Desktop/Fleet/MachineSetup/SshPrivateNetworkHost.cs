using System.Text;
using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Desktop.Fleet.MachineSetup;

/// <summary>
/// The command lines of private networks on a computer reached over SSH. An elevation retry uses the password given
/// for this attempt only, on the standard input of sudo, never in an argument, a log or a result.
/// </summary>
internal sealed class SshPrivateNetworkHost(ISshRunner ssh, SshTarget target, string? trustedHostKey, SshMachineFacts facts, string? sudoPassword) : IPrivateNetworkRemoteHost
{
    // A command that waits (Serve not enabled on the tailnet waits for its admin) is stopped there after this many seconds.
    private const int RemotePatienceSeconds = 15;

    // The exit code of timeout when it stopped the command.
    private const int StoppedByTimeout = 124;

    // Where the computer has no timeout command, the connection is closed here instead.
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(45);

    /// <inheritdoc />
    public string OperatingSystem => facts.Os;

    /// <inheritdoc />
    public string UserName => facts.User;

    /// <inheritdoc />
    public bool IsRoot => facts.Sudo == "root";

    /// <inheritdoc />
    public bool ElevationAsksPassword => facts.Sudo == "password";

    /// <inheritdoc />
    public async Task<bool> IsInstalledAsync(string command, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        if (command == "tailscale") return facts.Tailscale;
        if (command == "devtunnel") return facts.DevTunnel;
        return (await RunAsync("command", ["-v", command], null, cancellationToken).ConfigureAwait(false)).ExitCode == 0;
    }

    /// <inheritdoc />
    public async Task<PrivateNetworkRemoteCommandResult> RunAsync(string command, IReadOnlyList<string> arguments, string? elevateOnFailure, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        ArgumentNullException.ThrowIfNull(arguments);
        var elevate = elevateOnFailure is not null && (facts.Sudo == "nopassword" || facts.Sudo == "password" && !string.IsNullOrEmpty(sudoPassword));
        SshResult result;
        using (var waiting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            waiting.CancelAfter(Patience);
            try { result = await ssh.RunAsync(target, Script(command, arguments, elevateOnFailure, elevate), trustedHostKey, null, waiting.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return new(-1, "", TimedOut: true); }
        }

        // What ssh itself says (a host key it recorded) is on its standard error: it is not part of an answer in JSON.
        var output = result.Succeeded ? result.Output : result.Output + result.Error;
        return new(result.ExitCode, output, result.ExitCode == StoppedByTimeout,
            NeedsPassword: !result.Succeeded && !elevate && elevateOnFailure is not null && facts.Sudo == "password" && output.Contains(elevateOnFailure, StringComparison.OrdinalIgnoreCase),
            ElevationRefused: !result.Succeeded && elevate && SshNodeInstaller.SudoRefused(output));
    }

    // The command as the person would type it there; a change that is refused for want of root runs once more with sudo.
    // The standard input of sh is this script: no command reads it.
    private string Script(string executable, IReadOnlyList<string> args, string? elevateOnFailure, bool elevate)
    {
        // Executable names supplied by plugins are quoted too; known names keep the established, readable scripts.
        var command = (executable is "tailscale" or "devtunnel" or "command" ? executable : SshNodeInstaller.Quote(executable))
            + " " + string.Join(' ', args.Select(SshNodeInstaller.Quote));
        var script = new StringBuilder("T=\"\"\nif command -v timeout >/dev/null 2>&1; then T=\"timeout ").Append(RemotePatienceSeconds).Append("\"; fi\n");
        if (elevateOnFailure is null) return script.Append("$T ").Append(command).Append(" </dev/null\n").ToString();

        script.Append("OUT=$($T ").Append(command).Append(" 2>&1 </dev/null) && CODE=0 || CODE=$?\n");
        if (elevate)
        {
            // sudo -S reads the password on its standard input; sudo -n reads nothing.
            script.Append("case \"$OUT\" in *").Append(elevateOnFailure == "Access denied" ? "\"Access denied\"" : SshNodeInstaller.Quote(elevateOnFailure!))
                .Append("*)\n  OUT=$(").Append(SshNodeInstaller.Sudo(facts, sudoPassword)).Append("$T ").Append(command)
                .Append(facts.Sudo == "password" ? " 2>&1" : " 2>&1 </dev/null").Append(") && CODE=0 || CODE=$? ;;\nesac\n");
        }

        return script.Append("printf '%s\\n' \"$OUT\"\nexit $CODE\n").ToString();
    }
}
