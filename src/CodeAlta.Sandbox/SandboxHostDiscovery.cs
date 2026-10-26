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
        var tools = MxcFilesystem.GetAvailableToolsPolicy(environment.ToDictionary(static pair => pair.Key, static pair => (string?)pair.Value, environment.Comparer)).ReadonlyPaths
            .Where(static path => !string.IsNullOrWhiteSpace(path)).ToList();
        tools.AddRange(LinkedToolFolders(tools));
        if (OperatingSystem.IsMacOS() && DeveloperFolder() is { } developer) tools.Add(DeveloperGrant(developer));
        return new SandboxHost
        {
            DataRoot = dataRoot,
            HomeDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment = environment,
            ToolFolders = [.. tools.Distinct(SandboxPolicyBuilder.PathComparer)],
            ProgramFolders = ProgramFolders(),
            SecretFolders = [.. secretFolders],
        };
    }

    // The folders of a Homebrew prefix its commands need: `bin/node` is a link into `Cellar`, which loads its libraries
    // through `opt` and `lib`, and its certificates from `etc`. `var`, the data of its services (databases, logs), is not.
    private static readonly string[] HomebrewFolders = ["Cellar", "Caskroom", "opt", "lib", "libexec", "share", "etc", "include", "Frameworks"];

    /// <summary>
    /// Gets the folders the commands of the tool folders need beside them: those of the Homebrew prefix (macOS, Linux)
    /// whose <c>bin</c> or <c>sbin</c> is a tool folder.
    /// </summary>
    /// <param name="toolFolders">The tool folders, those of <c>PATH</c>.</param>
    /// <returns>The folders to grant read-only, besides the tool folders.</returns>
    internal static IReadOnlyList<string> LinkedToolFolders(IEnumerable<string> toolFolders)
    {
        var folders = new List<string>();
        if (OperatingSystem.IsWindows()) return folders;
        foreach (var folder in toolFolders)
        {
            var trimmed = Path.TrimEndingDirectorySeparator(folder);
            if (Path.GetFileName(trimmed) is not ("bin" or "sbin") || Path.GetDirectoryName(trimmed) is not { } prefix) continue;
            if (!Directory.Exists(Path.Combine(prefix, "Cellar"))) continue;
            folders.AddRange(HomebrewFolders.Select(name => Path.Combine(prefix, name)).Where(Directory.Exists));
        }

        return [.. folders.Distinct(SandboxPolicyBuilder.PathComparer)];
    }

    /// <summary>
    /// Gets the active developer folder of macOS, as <c>xcrun</c> finds it: the link <c>xcode-select</c> writes, else
    /// Xcode in <c>/Applications</c>, else the Command Line Tools. The commands of <c>/usr/bin</c> (<c>git</c>,
    /// <c>python3</c>) are shims that load <c>libxcrun</c> from there; mxc grants it only through the link.
    /// </summary>
    /// <returns>The folder, or null.</returns>
    internal static string? DeveloperFolder()
    {
        foreach (var link in (string[])["/private/var/db/xcode_select_link", "/private/var/select/developer_dir"])
        {
            try
            {
                if (new FileInfo(link).LinkTarget is { } target && Path.GetFullPath(target, Path.GetDirectoryName(link)!) is var resolved && Directory.Exists(resolved))
                    return resolved;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }

        return ((string[])["/Applications/Xcode.app/Contents/Developer", "/Library/Developer/CommandLineTools"]).FirstOrDefault(Directory.Exists);
    }

    /// <summary>
    /// Gets what is granted for a developer folder: the whole bundle of an Xcode, whose tools load its
    /// <c>SharedFrameworks</c>, else the folder.
    /// </summary>
    /// <param name="developerFolder">The developer folder.</param>
    /// <returns>The folder to grant read-only.</returns>
    internal static string DeveloperGrant(string developerFolder)
    {
        var folder = Path.TrimEndingDirectorySeparator(developerFolder);
        return Path.GetFileName(folder) == "Developer" && Path.GetDirectoryName(folder) is { } contents && Path.GetFileName(contents) == "Contents"
               && Path.GetDirectoryName(contents) is { } bundle && bundle.EndsWith(".app", StringComparison.OrdinalIgnoreCase)
            ? bundle
            : folder;
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
