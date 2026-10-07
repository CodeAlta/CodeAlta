using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CodeAlta.Plugin.Jira;

/// <summary>What a run of the Atlassian CLI gave.</summary>
/// <param name="ExitCode">Its exit code; -1 when it was stopped for taking too long.</param>
/// <param name="Output">What it wrote on its standard output.</param>
/// <param name="Error">What it wrote on its standard error.</param>
internal sealed record JiraCliResult(int ExitCode, string Output, string Error)
{
    public bool Succeeded => ExitCode == 0;

    /// <summary>What the CLI said went wrong, in one line without its mark.</summary>
    public string Message
    {
        get
        {
            var text = string.IsNullOrWhiteSpace(Error) ? Output : Error;
            var line = text.ReplaceLineEndings("\n").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? string.Empty;
            line = line.TrimStart('✗', '✓', ' ').Trim();
            if (line.StartsWith("Error:", StringComparison.OrdinalIgnoreCase)) line = line["Error:".Length..].Trim();
            return line.Length <= 300 ? line : line[..300];
        }
    }
}

/// <summary>Runs the Atlassian CLI (<c>acli</c>).</summary>
internal interface IJiraCli
{
    /// <summary>Gets whether the CLI is there to be run, without downloading it.</summary>
    bool IsInstalled { get; }

    /// <summary>Gets the path of the CLI; null when it is not installed.</summary>
    string? ExecutablePath { get; }

    /// <summary>Makes the CLI available, downloading it when it is not there yet.</summary>
    /// <param name="progress">Told once when a download starts.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>Null when the CLI is ready, or why it is not.</returns>
    ValueTask<string?> EnsureInstalledAsync(Action? progress, CancellationToken cancellationToken);

    /// <summary>Runs the CLI.</summary>
    /// <param name="arguments">Its arguments, each passed as it is.</param>
    /// <param name="input">Text for its standard input; null for none.</param>
    /// <param name="timeout">How long it may take.</param>
    /// <param name="cancellationToken">A token to cancel the run.</param>
    /// <returns>What it gave. A CLI that is not installed gives exit code 127.</returns>
    Task<JiraCliResult> RunAsync(IReadOnlyList<string> arguments, string? input, TimeSpan timeout, CancellationToken cancellationToken);
}

/// <summary>
/// The Atlassian CLI of this machine: the one <c>ACLI_PATH</c> names, the one CodeAlta downloaded in its cache,
/// or the one on the <c>PATH</c>. When there is none it is downloaded from Atlassian into
/// <c>~/.alta/cache/jira/acli/</c>, once, for the system and the processor of the machine.
/// </summary>
internal sealed class JiraCli : IJiraCli, IDisposable
{
    /// <summary>The address the CLI is downloaded from, for a system and an architecture.</summary>
    internal const string DownloadRoot = "https://acli.atlassian.com";

    private const long MinimumSize = 1024 * 1024;
    private const long MaximumSize = 200L * 1024 * 1024;
    private readonly string _cacheDirectory;
    private readonly HttpClient _client;
    private readonly SemaphoreSlim _install = new(1, 1);
    private string? _path;

    /// <summary>Creates the CLI of a cache folder.</summary>
    /// <param name="cacheDirectory">The folder the CLI is downloaded into.</param>
    /// <param name="handler">Answers the download; null for the network.</param>
    internal JiraCli(string cacheDirectory, HttpMessageHandler? handler = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheDirectory);
        _cacheDirectory = cacheDirectory;
        _client = handler is null ? new HttpClient() : new HttpClient(handler);
        _client.Timeout = TimeSpan.FromMinutes(5);
        _client.DefaultRequestHeaders.UserAgent.ParseAdd("CodeAlta-Jira-Plugin");
    }

    /// <inheritdoc />
    public bool IsInstalled => Locate() is not null;

    /// <inheritdoc />
    public string? ExecutablePath => Locate();

    /// <summary>Gets the file name of the CLI on this system.</summary>
    internal static string FileName => OperatingSystem.IsWindows() ? "acli.exe" : "acli";

    /// <summary>Gets the path the CLI is downloaded to.</summary>
    internal string CachedPath => Path.Combine(_cacheDirectory, FileName);

    /// <summary>Gets the address of the CLI for a system and a processor; null when Atlassian has none for them.</summary>
    internal static string? DownloadUrl(OSPlatform? platform = null, Architecture? architecture = null)
    {
        var system = platform is { } given ? given == OSPlatform.Windows ? "windows" : given == OSPlatform.OSX ? "darwin" : given == OSPlatform.Linux ? "linux" : null
            : OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "darwin" : OperatingSystem.IsLinux() ? "linux" : null;
        var processor = (architecture ?? RuntimeInformation.OSArchitecture) switch { Architecture.X64 => "amd64", Architecture.Arm64 => "arm64", _ => null };
        return system is null || processor is null ? null : $"{DownloadRoot}/{system}/latest/acli_{system}_{processor}/acli{(system == "windows" ? ".exe" : string.Empty)}";
    }

    /// <inheritdoc />
    public async ValueTask<string?> EnsureInstalledAsync(Action? progress, CancellationToken cancellationToken)
    {
        if (Locate() is not null) return null;
        await _install.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Locate() is not null) return null;
            if (DownloadUrl() is not { } url) return "Atlassian has no CLI for this system. Install acli and set ACLI_PATH to it.";
            progress?.Invoke();
            Directory.CreateDirectory(_cacheDirectory);
            // Another instance may download at the same time: each writes its own file, and the first one that ends gives the CLI.
            var temporary = Path.Combine(_cacheDirectory, $"{FileName}.{Guid.NewGuid():N}.tmp");
            try
            {
                using (var response = await _client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
                {
                    if (!response.IsSuccessStatusCode) return $"The Atlassian CLI could not be downloaded (HTTP {(int)response.StatusCode}).";
                    if (response.Content.Headers.ContentLength is > MaximumSize) return "The Atlassian CLI could not be downloaded: the file is not what was expected.";
                    await using var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                    await stream.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
                }

                var length = new FileInfo(temporary).Length;
                if (length is < MinimumSize or > MaximumSize) return "The Atlassian CLI could not be downloaded: the file is not what was expected.";
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                        | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
                }

                if (!File.Exists(CachedPath)) File.Move(temporary, CachedPath, overwrite: false);
            }
            catch (IOException) when (File.Exists(CachedPath))
            {
                // The other instance was first.
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException or UnauthorizedAccessException
                || exception is OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                return "The Atlassian CLI could not be downloaded. Check the network, or install acli and set ACLI_PATH to it.";
            }
            finally
            {
                try { File.Delete(temporary); } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
            }

            _path = null;
            return Locate() is null ? "The Atlassian CLI could not be installed." : null;
        }
        finally
        {
            _install.Release();
        }
    }

    /// <inheritdoc />
    public async Task<JiraCliResult> RunAsync(IReadOnlyList<string> arguments, string? input, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (Locate() is not { } path) return new(127, string.Empty, "The Atlassian CLI is not installed.");
        var start = new ProcessStartInfo
        {
            FileName = path,
            WorkingDirectory = _cacheDirectory is { } directory && Directory.Exists(directory) ? directory : Environment.CurrentDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        // The CLI draws colors and marks for a terminal: plain text is what is read here.
        start.Environment["NO_COLOR"] = "1";
        using var process = new Process { StartInfo = start };
        try
        {
            if (!process.Start()) return new(127, string.Empty, "The Atlassian CLI could not be started.");
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return new(127, string.Empty, "The Atlassian CLI could not be started.");
        }

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout);
        try
        {
            var output = process.StandardOutput.ReadToEndAsync(limit.Token);
            var error = process.StandardError.ReadToEndAsync(limit.Token);
            if (input is not null) await process.StandardInput.WriteAsync(input.AsMemory(), limit.Token).ConfigureAwait(false);
            process.StandardInput.Close();
            await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
            return new(process.ExitCode, await output.ConfigureAwait(false), await error.ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            cancellationToken.ThrowIfCancellationRequested();
            return new(-1, string.Empty, "The Atlassian CLI did not answer in time.");
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _client.Dispose();
        _install.Dispose();
    }

    private string? Locate()
    {
        if (_path is { } known && File.Exists(known)) return known;
        var named = Environment.GetEnvironmentVariable("ACLI_PATH");
        if (!string.IsNullOrWhiteSpace(named) && File.Exists(named)) return _path = Path.GetFullPath(named);
        if (File.Exists(CachedPath)) return _path = CachedPath;
        foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var candidate = Path.Combine(folder, FileName);
                if (File.Exists(candidate)) return _path = candidate;
            }
            catch (ArgumentException)
            {
            }
        }

        return _path = null;
    }
}
