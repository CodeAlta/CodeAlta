using System.Diagnostics;
using System.Globalization;
using System.Text;
using CodeAlta.Hosting;

namespace CodeAlta.Node;

/// <summary>
/// Whether a CodeAlta instance holds a profile, read from its lock (<see cref="CodeAltaInstanceProfile.LockFilePath"/>)
/// without taking it: the lock names the process that holds it, which must still run.
/// </summary>
/// <param name="LockFilePath">The lock of the profile.</param>
/// <param name="IsHeld">Whether a running process holds the profile.</param>
/// <param name="ProcessId">The process the lock names, or null when there is no lock or it cannot be read.</param>
/// <param name="ProcessName">The name of that process when it runs (<c>altanode</c>, <c>alta</c>, <c>altatui</c>), or null.</param>
public sealed record NodeProfileStatus(string LockFilePath, bool IsHeld, int? ProcessId, string? ProcessName)
{
    /// <summary>Reads the status of a profile.</summary>
    /// <param name="profile">The profile.</param>
    /// <returns>The status; a lock whose process no longer runs is a profile nobody holds.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="profile"/> is null.</exception>
    public static NodeProfileStatus Read(CodeAltaInstanceProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var path = profile.LockFilePath;
        if (!File.Exists(path)) return new(path, false, null, null);
        var processId = TryReadProcessId(path);
        if (processId is not { } id) return new(path, true, null, null);
        var name = TryGetRunningProcessName(id);
        return new(path, name is not null, id, name);
    }

    /// <summary>Describes the status for a person, in one line.</summary>
    /// <returns>The description.</returns>
    public string Describe()
    {
        if (!IsHeld) return ProcessId is null ? "No CodeAlta instance holds this profile." : "No CodeAlta instance holds this profile (a stale lock remains).";
        var holder = ProcessName is { Length: > 0 } name ? name : "a CodeAlta process";
        return ProcessId is { } id
            ? $"This profile is held by {holder} (PID {id.ToString(CultureInfo.InvariantCulture)})."
            : "This profile is held by a CodeAlta process whose PID cannot be read yet.";
    }

    // The lock is opened as the guard reads it: shared, so that the holder keeps writing it.
    private static int? TryReadProcessId(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return int.TryParse(reader.ReadToEnd().Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0 ? id : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // The name of a running process; null when it has exited or does not exist. A process that cannot be inspected
    // counts as running, as the guard counts it.
    private static string? TryGetRunningProcessName(int processId)
    {
        Process process;
        try
        {
            process = Process.GetProcessById(processId);
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            return string.Empty;
        }

        using (process)
        {
            try
            {
                return process.HasExited ? null : process.ProcessName;
            }
            catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
            {
                return string.Empty;
            }
        }
    }
}
