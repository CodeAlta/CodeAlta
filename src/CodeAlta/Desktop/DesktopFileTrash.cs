using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace CodeAlta.Desktop;

/// <summary>Moves a file or a folder to where the system keeps what was deleted, so that it can be restored.</summary>
internal interface IDesktopFileTrash
{
    /// <summary>Whether this system has such a place that the application can move things to.</summary>
    bool Available { get; }

    /// <summary>Moves a file or a folder there.</summary>
    /// <param name="fullPath">The full path of an existing file or folder.</param>
    /// <param name="cancellationToken">Cancels the wait; what was started is not undone.</param>
    /// <returns>True when the entry is gone from its folder; false when it could not be moved.</returns>
    ValueTask<bool> MoveAsync(string fullPath, CancellationToken cancellationToken);
}

/// <summary>
/// The Recycle Bin on Windows, the Trash on macOS 14 and later (<c>/usr/bin/trash</c>) and the trash of the
/// desktop on Linux (<c>gio trash</c>). Nothing is ever deleted here: where none of these exists, moving fails.
/// </summary>
internal sealed class DesktopFileTrash : IDesktopFileTrash
{
    private const string MacTrash = "/usr/bin/trash";
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(30);
    private readonly Lazy<string?> _command = new(FindCommand);

    /// <inheritdoc />
    public bool Available => OperatingSystem.IsWindows() ? Environment.Is64BitProcess : _command.Value is not null;

    /// <inheritdoc />
    public async ValueTask<bool> MoveAsync(string fullPath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fullPath);
        if (!Path.IsPathFullyQualified(fullPath) || !Available) return false;
        try
        {
            if (OperatingSystem.IsWindows())
            {
                if (!await RecycleAsync(fullPath).WaitAsync(cancellationToken).ConfigureAwait(false)) return false;
            }
            else if (!await RunAsync(_command.Value!, fullPath, cancellationToken).ConfigureAwait(false))
            {
                return false;
            }

            return !File.Exists(fullPath) && !Directory.Exists(fullPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException or NotSupportedException)
        {
            return false;
        }
    }

    private static string? FindCommand()
    {
        if (OperatingSystem.IsMacOS()) return File.Exists(MacTrash) ? MacTrash : null;
        if (!OperatingSystem.IsLinux()) return null;
        foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(folder, "gio");
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException)
            {
                // Not a folder name.
            }
        }

        return null;
    }

    // Without a shell: the path is one argument, whatever it holds.
    private static async Task<bool> RunAsync(string command, string fullPath, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(command) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        if (OperatingSystem.IsLinux())
        {
            start.ArgumentList.Add("trash");
            start.ArgumentList.Add("--");
        }

        start.ArgumentList.Add(fullPath);
        using var process = Process.Start(start);
        if (process is null) return false;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(CommandTimeout);
        try
        {
            // What the command prints is read so that it never waits on a full pipe.
            await Task.WhenAll(process.StandardOutput.ReadToEndAsync(timeout.Token), process.StandardError.ReadToEndAsync(timeout.Token)).ConfigureAwait(false);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            return process.ExitCode == 0;
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (Exception exception) when (exception is InvalidOperationException or Win32Exception) { /* Already gone. */ }
            cancellationToken.ThrowIfCancellationRequested();
            return false;
        }
    }

    // The shell may show a window (a file too large for the bin): it runs on a thread of its own, as the shell expects.
    [SupportedOSPlatform("windows")]
    private static Task<bool> RecycleAsync(string fullPath)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { completion.TrySetResult(Recycle(fullPath)); }
            catch (Exception exception) { completion.TrySetException(exception); }
        }) { IsBackground = true, Name = "CodeAlta recycle" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    [SupportedOSPlatform("windows")]
    private static bool Recycle(string fullPath)
    {
        // The list of names ends with an empty name: two terminators after the one path.
        var from = Marshal.StringToHGlobalUni(fullPath + '\0');
        try
        {
            var operation = new FileOperation
            {
                Function = Delete,
                From = from,
                // No progress and no question, except the one asked before something too large for the bin is deleted for good.
                Flags = AllowUndo | NoConfirmation | Silent | NoErrorUi | WantNukeWarning,
            };
            return SHFileOperationW(ref operation) == 0 && operation.AnyOperationsAborted == 0;
        }
        finally
        {
            Marshal.FreeHGlobal(from);
        }
    }

    private const uint Delete = 3;                 // FO_DELETE
    private const ushort Silent = 0x0004;          // FOF_SILENT
    private const ushort NoConfirmation = 0x0010;  // FOF_NOCONFIRMATION
    private const ushort AllowUndo = 0x0040;       // FOF_ALLOWUNDO
    private const ushort NoErrorUi = 0x0400;       // FOF_NOERRORUI
    private const ushort WantNukeWarning = 0x4000; // FOF_WANTNUKEWARNING

    // SHFILEOPSTRUCTW as 64-bit Windows lays it out; the 32-bit one is packed and is not used.
    [StructLayout(LayoutKind.Sequential)]
    private struct FileOperation
    {
        public nint Window;
        public uint Function;
        public nint From;
        public nint To;
        public ushort Flags;
        public int AnyOperationsAborted;
        public nint NameMappings;
        public nint ProgressTitle;
    }

    [DllImport("shell32.dll", ExactSpelling = true)]
    [SupportedOSPlatform("windows")]
    private static extern int SHFileOperationW(ref FileOperation operation);
}

/// <summary>Shows a file or a folder in the file manager of the system.</summary>
internal static class DesktopFileReveal
{
    /// <summary>Whether this system has a file manager the application can open.</summary>
    internal static bool Available => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() || OperatingSystem.IsLinux();

    /// <summary>Opens the folder of an entry with the entry selected; on Linux the folder that holds it.</summary>
    /// <param name="fullPath">The full path of an existing file or folder.</param>
    /// <returns>False when the file manager could not be started.</returns>
    internal static bool Show(string fullPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fullPath);
        if (!Path.IsPathFullyQualified(fullPath)) return false;
        try
        {
            ProcessStartInfo start;
            if (OperatingSystem.IsWindows())
            {
                // Explorer reads its own command line: the path is quoted, and a Windows path holds no quote.
                if (fullPath.Contains('"')) return false;
                start = new ProcessStartInfo("explorer.exe", $"/select,\"{fullPath}\"");
            }
            else if (OperatingSystem.IsMacOS())
            {
                start = new ProcessStartInfo("/usr/bin/open");
                start.ArgumentList.Add("-R");
                start.ArgumentList.Add(fullPath);
            }
            else if (OperatingSystem.IsLinux())
            {
                start = new ProcessStartInfo("xdg-open");
                start.ArgumentList.Add(Directory.Exists(fullPath) ? fullPath : Path.GetDirectoryName(fullPath) ?? fullPath);
            }
            else
            {
                return false;
            }

            start.UseShellExecute = false;
            start.CreateNoWindow = true;
            using var process = Process.Start(start);
            return process is not null;
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or IOException or NotSupportedException)
        {
            return false;
        }
    }
}
