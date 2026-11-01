using System.Diagnostics;
using System.Text;

namespace CodeAlta.Browser.Launch;

/// <summary>A failure to start a browser, with what the browser wrote, for the agent and the log.</summary>
internal sealed class BrowserLaunchException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// A browser CodeAlta started: its process, the folder of a temporary profile to delete when it ends, and the last
/// lines it wrote, which say why a start failed.
/// </summary>
internal sealed class BrowserProcess : IAsyncDisposable
{
    private const int KeptLines = 40;

    private readonly Process _process;
    private readonly Queue<string> _lines = new();
    private readonly Lock _gate = new();
    private readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _disposed;

    private BrowserProcess(Process process, string? temporaryProfile)
    {
        _process = process;
        TemporaryProfile = temporaryProfile;
    }

    /// <summary>Gets the folder of the temporary profile, deleted with the process; or null for a profile that stays.</summary>
    public string? TemporaryProfile { get; }

    /// <summary>Gets the id of the process.</summary>
    public int Id => _process.Id;

    /// <summary>Gets a task that completes when the process exits.</summary>
    public Task Exited => _exited.Task;

    /// <summary>Gets whether the process exited.</summary>
    public bool HasExited => _exited.Task.IsCompleted;

    /// <summary>Raised for each line the browser writes, on a thread of the process reader.</summary>
    public event Action<string>? Line;

    /// <summary>Starts a program with its arguments as a list, never through a shell, with its output read here.</summary>
    /// <exception cref="BrowserLaunchException">The program could not be started.</exception>
    public static BrowserProcess Start(string program, IReadOnlyList<string> arguments, string? temporaryProfile, IReadOnlyDictionary<string, string?>? environment = null)
    {
        var info = new ProcessStartInfo(program)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            StandardErrorEncoding = Encoding.UTF8,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        if (environment is not null)
        {
            foreach (var (name, value) in environment)
            {
                if (value is null) info.Environment.Remove(name);
                else info.Environment[name] = value;
            }
        }

        var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        var browser = new BrowserProcess(process, temporaryProfile);
        process.OutputDataReceived += (_, line) => browser.Record(line.Data);
        process.ErrorDataReceived += (_, line) => browser.Record(line.Data);
        process.Exited += (_, _) => browser._exited.TrySetResult();
        try
        {
            if (!process.Start()) throw new BrowserLaunchException($"{program} did not start.");
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            process.Dispose();
            throw new BrowserLaunchException($"{program} could not be started: {exception.Message}", exception);
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return browser;
    }

    /// <summary>Gets the last lines the browser wrote.</summary>
    public string Output()
    {
        lock (_gate) return string.Join('\n', _lines);
    }

    private void Record(string? line)
    {
        if (line is null) return;
        lock (_gate)
        {
            _lines.Enqueue(line.Length > 500 ? line[..500] : line);
            while (_lines.Count > KeptLines) _lines.Dequeue();
        }

        Line?.Invoke(line);
    }

    /// <summary>Waits for the process to exit by itself for a while, then ends it and what it started.</summary>
    public async Task StopAsync(TimeSpan grace)
    {
        if (!HasExited)
        {
            if (await Task.WhenAny(Exited, Task.Delay(grace)).ConfigureAwait(false) != Exited)
            {
                try { _process.Kill(entireProcessTree: true); }
                catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // It exited meanwhile.
                }

                await Task.WhenAny(Exited, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
            }
        }

        if (TemporaryProfile is not null) await DeleteDirectoryAsync(TemporaryProfile).ConfigureAwait(false);
    }

    /// <summary>Deletes a folder, retrying while the browser's last helpers release its files.</summary>
    internal static async Task DeleteDirectoryAsync(string directory)
    {
        for (var attempt = 0; attempt < 20 && Directory.Exists(directory); attempt++)
        {
            try
            {
                Directory.Delete(directory, recursive: true);
                return;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(250).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Ends the browser at once, and deletes its temporary profile.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await StopAsync(TimeSpan.Zero).ConfigureAwait(false);
        _process.Dispose();
    }
}
