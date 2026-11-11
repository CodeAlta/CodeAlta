using System.Runtime.InteropServices;

namespace CodeAlta.Fleet.Update;

/// <summary>
/// Whether the files a running process loads its code from are still there: the folder of its .NET runtime, which the
/// package manager of the system removes when it installs a newer patch (the unattended upgrades of Ubuntu, Homebrew), and
/// the folder of its application, which <c>dotnet tool update</c> removes with the previous version. .NET loads an assembly
/// when it is first used: a process whose files are gone runs on until it needs one it did not load yet, then fails (a node
/// that joins a server cannot open its first WebSocket). Such a process restarts on the new files instead.
/// </summary>
public sealed class UpdateRuntimeFiles
{
    // A file of every shared runtime and of every self-contained application: its folder without it is not a runtime.
    private const string CoreLibrary = "System.Private.CoreLib.dll";

    private readonly string _runtimeDirectory;
    private readonly string _applicationDirectory;

    /// <summary>Initializes a new instance of the <see cref="UpdateRuntimeFiles"/> class.</summary>
    /// <param name="runtimeDirectory">The folder of the .NET runtime of the process.</param>
    /// <param name="applicationDirectory">The folder of its application.</param>
    /// <exception cref="ArgumentException">A folder is null or empty.</exception>
    public UpdateRuntimeFiles(string runtimeDirectory, string applicationDirectory)
    {
        ArgumentException.ThrowIfNullOrEmpty(runtimeDirectory);
        ArgumentException.ThrowIfNullOrEmpty(applicationDirectory);
        _runtimeDirectory = Path.TrimEndingDirectorySeparator(runtimeDirectory);
        _applicationDirectory = Path.TrimEndingDirectorySeparator(applicationDirectory);
    }

    /// <summary>Gets the files of this process: its runtime (<see cref="RuntimeEnvironment.GetRuntimeDirectory"/>) and <see cref="AppContext.BaseDirectory"/>.</summary>
    /// <returns>The files.</returns>
    public static UpdateRuntimeFiles OfThisProcess() => new(RuntimeEnvironment.GetRuntimeDirectory(), AppContext.BaseDirectory);

    /// <summary>Gets the folder that is gone, the runtime first; null while both are there.</summary>
    /// <returns>The folder, or null.</returns>
    public string? Missing()
    {
        if (!File.Exists(Path.Combine(_runtimeDirectory, CoreLibrary))) return _runtimeDirectory;
        return Directory.Exists(_applicationDirectory) ? null : _applicationDirectory;
    }
}
