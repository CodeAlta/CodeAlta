using System.Collections;
using MxcFilesystem = Microsoft.Mxc.Sdk.V1.Policy.Filesystem;

namespace CodeAlta.Sandbox;

/// <summary>What this computer gives a sandbox, read from the running application.</summary>
public static class SandboxHostDiscovery
{
    /// <summary>Gets the folder where the sandboxes keep their homes, temporary folders and caches.</summary>
    /// <returns><c>%LOCALAPPDATA%\CodeAlta\sandbox</c> on Windows, <c>~/Library/Caches/CodeAlta/sandbox</c> on macOS,
    /// <c>$XDG_CACHE_HOME/codealta/sandbox</c> on Linux: outside <c>~/.alta</c>, whose secrets no sandbox reads.</returns>
    public static string DefaultDataRoot()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsWindows())
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodeAlta", "sandbox");
        if (OperatingSystem.IsMacOS()) return Path.Combine(home, "Library", "Caches", "CodeAlta", "sandbox");
        var cache = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
        return Path.Combine(string.IsNullOrWhiteSpace(cache) ? Path.Combine(home, ".cache") : cache, "codealta", "sandbox");
    }

    /// <summary>Reads the host from the running application.</summary>
    /// <param name="dataRoot">Where the sandboxes keep their data (<see cref="DefaultDataRoot"/>).</param>
    /// <param name="secretFolders">The folders of CodeAlta that hold secrets when they are not <c>~/.alta</c> (its global and state roots).</param>
    /// <returns>The host.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static SandboxHost Current(string dataRoot, IEnumerable<string> secretFolders)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        ArgumentNullException.ThrowIfNull(secretFolders);
        var environment = new Dictionary<string, string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string key && entry.Value is string value) environment[key] = value;
        }

        // The tool folders of PATH and of the variables tools are found with, read-only. mxc also grants the history
        // of PSReadLine read-write; a sandbox has no interactive shell and gets nothing of the user's history.
        var tools = MxcFilesystem.GetAvailableToolsPolicy(environment.ToDictionary(static pair => pair.Key, static pair => (string?)pair.Value, environment.Comparer)).ReadonlyPaths;
        return new SandboxHost
        {
            DataRoot = dataRoot,
            HomeDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment = environment,
            ToolFolders = [.. tools.Where(static path => !string.IsNullOrWhiteSpace(path))],
            ProgramFolders = ProgramFolders(),
            SecretFolders = [.. secretFolders],
        };
    }

    // CodeAlta's own folder, which holds the worker, and the .NET runtime it runs on.
    private static IReadOnlyList<string> ProgramFolders()
    {
        var folders = new List<string> { Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory) };
        // The .NET root: the process when it is `dotnet`, else DOTNET_ROOT, else the places installers use.
        var processFolder = Path.GetDirectoryName(Environment.ProcessPath);
        string?[] candidates =
        [
            processFolder is not null && Path.GetFileNameWithoutExtension(Environment.ProcessPath)!.Equals("dotnet", StringComparison.OrdinalIgnoreCase) ? processFolder : null,
            Environment.GetEnvironmentVariable("DOTNET_ROOT"),
            OperatingSystem.IsWindows() ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet") : null,
            OperatingSystem.IsMacOS() ? "/usr/local/share/dotnet" : null,
            OperatingSystem.IsLinux() ? "/usr/share/dotnet" : null,
            OperatingSystem.IsLinux() ? "/usr/lib/dotnet" : null,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet"),
        ];
        var root = candidates.FirstOrDefault(static folder => !string.IsNullOrWhiteSpace(folder) && Directory.Exists(Path.Combine(folder, "shared")));
        if (root is not null) folders.Add(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)));
        return folders;
    }
}
