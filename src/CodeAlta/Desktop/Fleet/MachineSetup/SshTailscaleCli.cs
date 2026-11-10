using System.Text;
using System.Text.Json;
using CodeAlta.Plugins.PrivateNetworks;

namespace CodeAlta.Desktop.Fleet.MachineSetup;

/// <summary>
/// The Tailscale command line of a computer reached over SSH, for the setup of a server behind a name of its tailnet. A
/// change of what Tailscale serves that it keeps for root (on Linux, unless the person is its operator) runs again with
/// sudo: without a password where sudo asks none, or with the one the person gave; otherwise <see cref="NeedsPassword"/>
/// says that one is needed.
/// </summary>
internal sealed class SshTailscaleCli(ISshRunner ssh, SshTarget target, string? trustedHostKey, SshMachineFacts facts, string? sudoPassword) : ITailscaleCli
{
    // What Tailscale answers a person who may not change what it serves.
    private const string AccessDenied = "Access denied";

    // A command that waits (Serve not enabled on the tailnet waits for its admin) is stopped there after this many seconds.
    private const int RemotePatienceSeconds = 15;

    // The exit code of timeout when it stopped the command.
    private const int StoppedByTimeout = 124;

    // Where the computer has no timeout command, the connection is closed here instead.
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(45);

    /// <inheritdoc />
    public bool IsInstalled => facts.Tailscale;

    /// <summary>Gets whether the last change was refused because it needs root, and sudo asks there a password that was not given.</summary>
    public bool NeedsPassword { get; private set; }

    /// <summary>Gets whether sudo refused the last change: a wrong password, or a person who may not use sudo.</summary>
    public bool SudoRefused { get; private set; }

    /// <inheritdoc />
    public async Task<CommandLineResult> RunAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);
        NeedsPassword = SudoRefused = false;
        var elevate = Changes(args) && (facts.Sudo == "nopassword" || facts.Sudo == "password" && !string.IsNullOrEmpty(sudoPassword));
        SshResult result;
        using (var waiting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            waiting.CancelAfter(Patience);
            try { result = await ssh.RunAsync(target, Script(args, elevate), trustedHostKey, null, waiting.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return new CommandLineResult(-1, "", TimedOut: true); }
        }

        // What ssh itself says (a host key it recorded) is on its standard error: it is not part of an answer in JSON.
        var output = result.Succeeded ? result.Output : result.Output + result.Error;
        if (!result.Succeeded && Changes(args))
        {
            if (elevate) SudoRefused = SshNodeInstaller.SudoRefused(output);
            else NeedsPassword = facts.Sudo == "password" && output.Contains(AccessDenied, StringComparison.OrdinalIgnoreCase);
        }

        return new CommandLineResult(result.ExitCode, output, result.ExitCode == StoppedByTimeout);
    }

    /// <summary>
    /// Says whether a change of what Tailscale serves there takes the password of sudo: on Linux, where Tailscale keeps its
    /// changes for root and for its operator, when the person is neither and sudo asks one. The page then asks it with the
    /// offer to publish, as it does for <b>Always on</b>.
    /// </summary>
    /// <param name="cancellationToken">Stops the read.</param>
    /// <returns>Whether the password is needed.</returns>
    public async Task<bool> ChangesAskSudoPasswordAsync(CancellationToken cancellationToken)
    {
        if (facts.Os != "Linux" || facts.Sudo != "password") return false;
        // The preferences name the operator, when one was set (tailscale set --operator); read without it, a change is tried
        // as the person first anyway.
        var prefs = await RunAsync(["debug", "prefs"], cancellationToken).ConfigureAwait(false);
        try
        {
            using var json = JsonDocument.Parse(prefs.Output);
            return !(json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.TryGetProperty("OperatorUser", out var user)
                && user.ValueKind == JsonValueKind.String && string.Equals(user.GetString(), facts.User, StringComparison.Ordinal));
        }
        catch (JsonException)
        {
            return true;
        }
    }

    // What changes Tailscale: everything of serve but its status.
    private static bool Changes(IReadOnlyList<string> args) => args is ["serve", not "status", ..];

    // The command as the person would type it there; a change that is refused for want of root runs once more with sudo.
    // The standard input of sh is this script: no command reads it.
    private string Script(IReadOnlyList<string> args, bool elevate)
    {
        var command = "tailscale " + string.Join(' ', args.Select(SshNodeInstaller.Quote));
        var script = new StringBuilder("T=\"\"\nif command -v timeout >/dev/null 2>&1; then T=\"timeout ").Append(RemotePatienceSeconds).Append("\"; fi\n");
        if (!Changes(args)) return script.Append("$T ").Append(command).Append(" </dev/null\n").ToString();

        script.Append("OUT=$($T ").Append(command).Append(" 2>&1 </dev/null) && CODE=0 || CODE=$?\n");
        if (elevate)
        {
            // sudo -S reads the password on its standard input; sudo -n reads nothing.
            script.Append("case \"$OUT\" in *\"").Append(AccessDenied).Append("\"*)\n  OUT=$(").Append(SshNodeInstaller.Sudo(facts, sudoPassword)).Append("$T ").Append(command)
                .Append(facts.Sudo == "password" ? " 2>&1" : " 2>&1 </dev/null").Append(") && CODE=0 || CODE=$? ;;\nesac\n");
        }

        return script.Append("printf '%s\\n' \"$OUT\"\nexit $CODE\n").ToString();
    }
}
