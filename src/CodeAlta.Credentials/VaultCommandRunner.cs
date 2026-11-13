using System.Collections.Immutable;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace CodeAlta.Credentials;

/// <summary>What a program of a vault answered.</summary>
/// <param name="ExitCode">Its exit code; -1 when it did not end in time.</param>
/// <param name="Output">What it wrote on its standard output, as bytes: a value is never made a text before it is needed.</param>
/// <param name="Error">
/// What it wrote on its error output, bounded. It can hold a value (a wrapper that prints the secret it fetched before it fails), which
/// the masking does not know yet: it is read to classify the failure, and shown to the user only (<see cref="ErrorDetail"/>), never
/// put in a message an agent or a log gets.
/// </param>
/// <param name="TimedOut">Whether it was stopped because it did not end in time.</param>
public sealed record VaultCommandResult(int ExitCode, byte[] Output, string Error, bool TimedOut)
{
    /// <summary>Gets the standard output as text.</summary>
    /// <returns>The text.</returns>
    public string OutputText() => Encoding.UTF8.GetString(Output);

    /// <summary>Gets how it ended, for a message anybody may read: its exit code, or that it did not answer in time; never what it wrote.</summary>
    /// <returns>The text, such as <c>it ended with code 1</c>.</returns>
    public string Summary() => TimedOut ? "it did not answer in time" : $"it ended with code {ExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture)}";

    /// <summary>
    /// Gets the first line of the error output that says something, at most 300 characters, for the user only (the detail of a
    /// status or a failure, which the window shows and no agent nor log gets).
    /// </summary>
    /// <returns>The line; null when it wrote nothing.</returns>
    public string? ErrorDetail()
    {
        foreach (var line in Error.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // The causes of bws are numbered: "0: the cause"; a program that writes "Error:" alone on its first line says why on the next.
            var digits = 0;
            while (digits < line.Length && char.IsAsciiDigit(line[digits])) digits++;
            var text = digits > 0 && digits < line.Length && line[digits] == ':' ? line[(digits + 1)..].Trim() : line;
            if (text.Length == 0 || text.Equals("Error:", StringComparison.OrdinalIgnoreCase) || text.Equals("Error", StringComparison.OrdinalIgnoreCase)) continue;
            return text.Length > 300 ? text[..300] : text;
        }

        return null;
    }
}

/// <summary>How a program of a vault is run.</summary>
public sealed record VaultCommandOptions
{
    /// <summary>Gets what is written on its standard input (a password); null closes it at once.</summary>
    public string? Input { get; init; }

    /// <summary>Gets the variables added to its environment (a session key, a token).</summary>
    public IReadOnlyDictionary<string, string>? Environment { get; init; }

    /// <summary>Gets how long it may run.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>Gets the most bytes of its output that are read.</summary>
    public int MaximumOutput { get; init; } = 4 * 1024 * 1024;
}

/// <summary>
/// Runs the programs of vaults (<c>op</c>, <c>bw</c>, <c>keepassxc-cli</c>, a command of the user) for the credential providers,
/// in one place, by the rules of all of them: a program is started by the host only, by its full path, with an array of
/// arguments and never a shell; in a neutral folder, never the folder of a project; with the environment of CodeAlta without
/// the variables of vault sessions, plus what the provider gives; a token or a password goes on its standard input or in its own
/// environment, never in an argument; its output is bounded in size and time.
/// </summary>
public sealed class VaultCommandRunner
{
    // The variables of vault sessions a program never inherits from CodeAlta's environment: a provider gives its own.
    private static readonly ImmutableArray<string> SessionVariables = ["OP_SERVICE_ACCOUNT_TOKEN", "OP_CONNECT_TOKEN", "OP_CONNECT_HOST", "BW_SESSION", "BWS_ACCESS_TOKEN", "BW_PASSWORD"];

    private readonly string _workingDirectory;

    /// <summary>Initializes a new instance of the <see cref="VaultCommandRunner"/> class.</summary>
    /// <param name="workingDirectory">The neutral folder the programs run in; null for the temporary folder of the system.</param>
    public VaultCommandRunner(string? workingDirectory = null)
    {
        _workingDirectory = string.IsNullOrWhiteSpace(workingDirectory) ? Path.GetTempPath() : workingDirectory;
    }

    /// <summary>
    /// Finds a program by its name: a full path given by the user, then the folders of <c>PATH</c> as the system has them now (a
    /// program installed after CodeAlta started is found), then the folders where the package managers put programs.
    /// </summary>
    /// <param name="name">The name of the program, such as <c>op</c>.</param>
    /// <param name="configuredPath">A path the user gave in the settings of the provider; null for none.</param>
    /// <returns>The full path, or null when it is not found.</returns>
    public static string? FindProgram(string name, string? configuredPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            var expanded = Environment.ExpandEnvironmentVariables(configuredPath.Trim());
            if (expanded.StartsWith('~')) expanded = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), expanded[1..].TrimStart('/', '\\'));
            return Path.IsPathFullyQualified(expanded) && File.Exists(expanded) ? Path.GetFullPath(expanded) : null;
        }

        if (Path.IsPathFullyQualified(name)) return File.Exists(name) ? name : null;
        var extensions = OperatingSystem.IsWindows() ? new[] { ".exe", ".cmd", ".bat" } : [string.Empty];
        foreach (var folder in SearchFolders())
        {
            foreach (var extension in extensions)
            {
                try
                {
                    var candidate = Path.Combine(folder, name + extension);
                    if (File.Exists(candidate)) return candidate;
                }
                catch (ArgumentException)
                {
                    // A folder of PATH that is not a valid path.
                }
            }
        }

        return null;
    }

    /// <summary>Runs a program and waits for it.</summary>
    /// <param name="program">Its full path (<see cref="FindProgram"/>).</param>
    /// <param name="arguments">Its arguments, each passed as it is.</param>
    /// <param name="options">How it runs; null for the defaults.</param>
    /// <param name="cancellationToken">Stops it.</param>
    /// <returns>What it answered.</returns>
    /// <exception cref="ArgumentException">The program is not a full path, or an argument of a batch file holds a character its interpreter reads.</exception>
    /// <exception cref="Win32Exception">The program cannot be started.</exception>
    public async Task<VaultCommandResult> RunAsync(string program, IReadOnlyList<string> arguments, VaultCommandOptions? options, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(program);
        ArgumentNullException.ThrowIfNull(arguments);
        options ??= new VaultCommandOptions();
        if (!Path.IsPathFullyQualified(program)) throw new ArgumentException($"'{program}' is not the full path of a program.", nameof(program));
        // A batch file is read again by cmd.exe, which would take & | < > as its own: only plain arguments go to one.
        if (OperatingSystem.IsWindows() && Path.GetExtension(program) is { } extension
            && (extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase) || extension.Equals(".bat", StringComparison.OrdinalIgnoreCase))
            && arguments.Any(static argument => argument.Any(static ch => !(char.IsAsciiLetterOrDigit(ch) || "-_./:=@,+ ".Contains(ch)))))
            throw new ArgumentException($"{Path.GetFileName(program)} is a batch file, and an argument holds a character cmd.exe would read: use the program itself.", nameof(arguments));

        var start = new ProcessStartInfo(program)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = _workingDirectory,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        foreach (var variable in SessionVariables) start.Environment.Remove(variable);
        foreach (var key in start.Environment.Keys.Where(static key => key.StartsWith("OP_SESSION_", StringComparison.OrdinalIgnoreCase)).ToArray()) start.Environment.Remove(key);
        if (options.Environment is { } environment)
        {
            foreach (var (name, value) in environment) start.Environment[name] = value;
        }

        using var process = Process.Start(start) ?? throw new Win32Exception($"{Path.GetFileName(program)} did not start.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.Timeout);
        // Whatever stops it (its time, the caller) ends the program at once, in every step after its start: a write to an input it
        // does not read blocks on a full pipe, and only the end of the program unblocks it.
        using var stop = timeout.Token.UnsafeRegister(static state => Kill((Process)state!), process);
        var output = ReadBoundedAsync(process.StandardOutput.BaseStream, options.MaximumOutput, timeout.Token);
        var error = ReadBoundedAsync(process.StandardError.BaseStream, 64 * 1024, timeout.Token);
        try
        {
            try
            {
                if (options.Input is not null) await process.StandardInput.WriteAsync(options.Input.AsMemory(), timeout.Token).ConfigureAwait(false);
                process.StandardInput.Close();
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException)
            {
                // It ended without reading (or was ended): its exit code, or the token, says why.
            }

            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            var bytes = await output.ConfigureAwait(false);
            var errorBytes = await error.ConfigureAwait(false);
            return new VaultCommandResult(process.ExitCode, bytes, Encoding.UTF8.GetString(errorBytes), TimedOut: false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Kill(process);
            await DrainAsync(output, error).ConfigureAwait(false);
            return new VaultCommandResult(-1, [], $"{Path.GetFileName(program)} did not answer within {options.Timeout.TotalSeconds:0} seconds.", TimedOut: true);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            await DrainAsync(output, error).ConfigureAwait(false);
            throw;
        }
    }

    // The readers of a program that was stopped end with it: what they read is dropped, and nothing they throw is left unobserved.
    private static async Task DrainAsync(Task<byte[]> output, Task<byte[]> error)
    {
        foreach (var reader in new[] { output, error })
        {
            try
            {
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(await reader.ConfigureAwait(false));
            }
            catch (Exception exception) when (exception is OperationCanceledException or IOException or ObjectDisposedException)
            {
                // Stopped with the program.
            }
        }
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
            // It ended meanwhile.
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream stream, int maximum, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            // What goes beyond the bound is read and dropped, so that the program is not blocked on a full pipe.
            var room = maximum - (int)buffer.Length;
            if (room > 0) buffer.Write(chunk, 0, Math.Min(room, read));
        }

        System.Security.Cryptography.CryptographicOperations.ZeroMemory(chunk);
        var all = buffer.ToArray();
        // The caller zeroes what it is given: the copy the stream holds is not left behind it.
        if (buffer.TryGetBuffer(out var held)) System.Security.Cryptography.CryptographicOperations.ZeroMemory(held.Array);
        return all;
    }

    private static IEnumerable<string> SearchFolders()
    {
        var seen = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var paths = new List<string?> { Environment.GetEnvironmentVariable("PATH") };
        if (OperatingSystem.IsWindows())
        {
            // The PATH of the system as it is now: a program installed after CodeAlta started is on it.
            paths.Add(Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User));
            paths.Add(Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine));
        }

        foreach (var path in paths)
        {
            foreach (var folder in (path ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var expanded = Environment.ExpandEnvironmentVariables(folder.Trim('"'));
                if (Path.IsPathFullyQualified(expanded) && seen.Add(expanded)) yield return expanded;
            }
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        IEnumerable<string> known = OperatingSystem.IsWindows()
            ?
            [
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Links"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "1Password CLI"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "KeePassXC"),
                Path.Combine(home, "scoop", "shims"),
            ]
            : ["/opt/homebrew/bin", "/usr/local/bin", "/usr/bin", "/snap/bin", Path.Combine(home, ".local", "bin")];
        foreach (var folder in known)
        {
            if (seen.Add(folder)) yield return folder;
        }
    }
}
