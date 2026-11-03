using System.Diagnostics;
using System.Text;

namespace CodeAlta.Desktop.Fleet.MachineSetup;

/// <summary>A computer reached over SSH: <c>user@host</c> and its port.</summary>
/// <param name="User">The account on the computer; null for the one of the SSH configuration.</param>
/// <param name="Host">Its name or address.</param>
/// <param name="Port">Its SSH port; null for the one of the SSH configuration (22).</param>
internal sealed record SshTarget(string? User, string Host, int? Port)
{
    /// <summary>Gets the destination as ssh takes it: <c>user@host</c>, or the host alone.</summary>
    public string Destination => User is { Length: > 0 } user ? $"{user}@{Host}" : Host;

    /// <summary>Reads what a person types: <c>host</c>, <c>user@host</c>, <c>user@host:port</c>, or a host of the SSH configuration.</summary>
    /// <param name="text">The text.</param>
    /// <param name="target">The computer, when the text names one.</param>
    /// <returns>Whether it does.</returns>
    public static bool TryParse(string? text, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out SshTarget? target)
    {
        target = null;
        var value = text?.Trim() ?? "";
        if (value.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase)) value = value[6..].TrimEnd('/');
        if (value.Length is 0 or > 255 || value.Any(static c => char.IsWhiteSpace(c) || char.IsControl(c) || c is '\'' or '"' or '`' or ';' or '&' or '|' or '$' or '\\'))
            return false;
        string? user = null;
        var at = value.LastIndexOf('@');
        if (at >= 0)
        {
            user = value[..at];
            value = value[(at + 1)..];
            if (user.Length == 0 || user.StartsWith('-')) return false;
        }

        int? port = null;
        var colon = value.LastIndexOf(':');
        // An IPv6 address keeps its colons: [::1]:2222, or a bare address without a port.
        if (value.StartsWith('[') && value.IndexOf(']') is var close and > 0)
        {
            if (close + 1 < value.Length)
            {
                if (value[close + 1] != ':' || !int.TryParse(value[(close + 2)..], out var bracketPort) || bracketPort is < 1 or > 65535) return false;
                port = bracketPort;
            }

            value = value[1..close];
        }
        else if (colon > 0 && value.IndexOf(':') == colon)
        {
            if (!int.TryParse(value[(colon + 1)..], out var parsed) || parsed is < 1 or > 65535) return false;
            port = parsed;
            value = value[..colon];
        }

        if (value.Length == 0 || value.StartsWith('-')) return false;
        target = new SshTarget(user, value, port);
        return true;
    }
}

/// <summary>What a command run over SSH ended with.</summary>
/// <param name="ExitCode">The exit code of ssh: that of the remote command, or 255 when ssh itself failed.</param>
/// <param name="Output">What it wrote on its standard output.</param>
/// <param name="Error">What it wrote on its standard error.</param>
internal sealed record SshResult(int ExitCode, string Output, string Error)
{
    /// <summary>The exit code of ssh when it could not connect or sign in.</summary>
    public const int ConnectionFailed = 255;

    /// <summary>Gets whether the command succeeded.</summary>
    public bool Succeeded => ExitCode == 0;
}

/// <summary>How the desktop runs commands on a computer over SSH, and copies files there; a test gives its own.</summary>
internal interface ISshRunner
{
    /// <summary>Runs a POSIX shell script on the computer (<c>sh -s</c>, the script on its standard input).</summary>
    /// <param name="target">The computer.</param>
    /// <param name="script">The script.</param>
    /// <param name="acceptNewHostKey">Whether a host key ssh does not know yet is accepted and recorded (a person said so).</param>
    /// <param name="onOutput">Called with each line of its standard output, as it comes; null for none.</param>
    /// <param name="cancellationToken">Stops the command.</param>
    /// <returns>How it ended.</returns>
    Task<SshResult> RunAsync(SshTarget target, string script, bool acceptNewHostKey, Action<string>? onOutput, CancellationToken cancellationToken);

    /// <summary>Copies files of this computer into a folder of the computer.</summary>
    /// <param name="target">The computer.</param>
    /// <param name="files">The files.</param>
    /// <param name="folder">The folder there, which exists.</param>
    /// <param name="acceptNewHostKey">Whether a host key ssh does not know yet is accepted.</param>
    /// <param name="cancellationToken">Stops the copy.</param>
    /// <returns>How it ended.</returns>
    Task<SshResult> CopyAsync(SshTarget target, IReadOnlyList<string> files, string folder, bool acceptNewHostKey, CancellationToken cancellationToken);
}

/// <summary>
/// The OpenSSH client of the system (<c>ssh</c>; on Windows the one of <c>C:\Windows\System32\OpenSSH</c>), with the
/// person's keys and agent, never asking anything: <c>BatchMode</c>, so that a password or an unknown host key fails at
/// once with what to do instead of waiting for an answer nobody can give. Files go over the same ssh, on its standard
/// input (<c>cat &gt; file</c>): the scp of Windows drops its connection when it has no console input, as in a desktop.
/// </summary>
internal sealed class OpenSshRunner : ISshRunner
{
    private readonly string _ssh;

    /// <summary>Creates the runner over the client found on this computer.</summary>
    public OpenSshRunner()
        : this(Locate("ssh"))
    {
    }

    /// <summary>Creates the runner over a given client.</summary>
    /// <param name="ssh">The ssh executable.</param>
    internal OpenSshRunner(string ssh) => _ssh = ssh;

    /// <inheritdoc />
    public async Task<SshResult> RunAsync(SshTarget target, string script, bool acceptNewHostKey, Action<string>? onOutput, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(script);
        // -T: no terminal; the script comes on the standard input of sh, so that nothing of it is quoted for a remote shell.
        var bytes = Encoding.UTF8.GetBytes(script.Replace("\r\n", "\n", StringComparison.Ordinal));
        return await RunProcessAsync(Arguments(target, acceptNewHostKey, "sh -s"), new MemoryStream(bytes), onOutput, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<SshResult> CopyAsync(SshTarget target, IReadOnlyList<string> files, string folder, bool acceptNewHostKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        foreach (var file in files)
        {
            var name = Path.GetFileName(file);
            var destination = SshNodeInstaller.Quote(folder.TrimEnd('/') + "/" + name);
            await using var content = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
            var copied = await RunProcessAsync(Arguments(target, acceptNewHostKey, $"cat > {destination}"), content, null, cancellationToken).ConfigureAwait(false);
            if (!copied.Succeeded) return copied;
        }

        return new SshResult(0, "", "");
    }

    private List<string> Arguments(SshTarget target, bool acceptNewHostKey, string command)
    {
        // No ConnectTimeout: the ssh of Windows then fails to connect to a host of several addresses ("Connection to
        // UNKNOWN port -1"); the installer bounds its first connection itself.
        List<string> arguments =
        [
            "-o", "BatchMode=yes", "-o", "ServerAliveInterval=15", "-o", "ServerAliveCountMax=4",
            "-o", acceptNewHostKey ? "StrictHostKeyChecking=accept-new" : "StrictHostKeyChecking=yes",
        ];
        if (target.Port is { } port) arguments.AddRange(["-p", port.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
        arguments.AddRange(["-T", "--", target.Destination, command]);
        return arguments;
    }

    private async Task<SshResult> RunProcessAsync(IReadOnlyList<string> arguments, Stream input, Action<string>? onOutput, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(_ssh)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        var output = new StringBuilder();
        var error = new StringBuilder();
        process.OutputDataReceived += (_, line) =>
        {
            if (line.Data is null) return;
            lock (output) output.Append(line.Data).Append('\n');
            onOutput?.Invoke(line.Data);
        };
        process.ErrorDataReceived += (_, line) =>
        {
            if (line.Data is null) return;
            lock (error) error.Append(line.Data).Append('\n');
        };
        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            return new SshResult(SshResult.ConnectionFailed, "", $"{_ssh} could not start: {exception.Message}");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try
        {
            try
            {
                await input.CopyToAsync(process.StandardInput.BaseStream, cancellationToken).ConfigureAwait(false);
                await process.StandardInput.BaseStream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (IOException)
            {
                // ssh ended before it read everything (it could not connect): its exit code and its error say why.
            }
            finally
            {
                try { process.StandardInput.Close(); }
                catch (IOException) { }
            }

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            throw;
        }

        // The last lines come after the exit.
        process.WaitForExit();
        lock (output) lock (error) return new SshResult(process.ExitCode, output.ToString(), error.ToString());
    }

    // The clients of Windows are in System32\OpenSSH, which a desktop started from Explorer may not have on its PATH.
    private static string Locate(string name)
    {
        if (OperatingSystem.IsWindows())
        {
            var system = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "OpenSSH", name + ".exe");
            if (File.Exists(system)) return system;
            return name + ".exe";
        }

        foreach (var folder in new[] { "/usr/bin", "/usr/local/bin", "/opt/homebrew/bin" })
        {
            var path = Path.Combine(folder, name);
            if (File.Exists(path)) return path;
        }

        return name;
    }
}