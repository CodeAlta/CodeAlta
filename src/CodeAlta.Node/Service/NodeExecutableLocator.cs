namespace CodeAlta.Node.Service;

/// <summary>How the service starts <c>altanode</c>.</summary>
internal enum NodeExecutableKind
{
    /// <summary>The executable of a self-contained archive or a build: the service runs this file.</summary>
    Executable,

    /// <summary>
    /// The shim of a .NET tool (<c>~/.dotnet/tools/altanode</c>): its path stays when <c>dotnet tool update</c> replaces
    /// the version under <c>.store</c>, so the service follows the updates.
    /// </summary>
    ToolShim,

    /// <summary>The <c>dotnet</c> host with the assembly of <c>altanode</c>: the service runs that assembly where it is.</summary>
    DotnetHost,
}

/// <summary>The command a service starts: the executable and the arguments before <c>run</c>.</summary>
/// <param name="Kind">What the executable is.</param>
/// <param name="Path">The executable.</param>
/// <param name="PrefixArguments">The arguments before <c>run</c>: the assembly for the <c>dotnet</c> host, none otherwise.</param>
internal sealed record NodeExecutable(NodeExecutableKind Kind, string Path, IReadOnlyList<string> PrefixArguments);

/// <summary>Finds the stable path a service starts <c>altanode</c> from.</summary>
internal static class NodeExecutableLocator
{
    private const string ToolStoreFolder = ".store";

    /// <summary>
    /// Finds the executable of this process as a service should start it. A .NET tool runs from its shim
    /// (<c>~/.dotnet/tools/altanode</c>, an app host that loads the assembly of the installed version from
    /// <c>.store</c>): the shim is kept, and an executable found under <c>.store</c> is replaced by the shim of its
    /// tools folder. A process of the <c>dotnet</c> host runs the assembly of <c>altanode</c> with it.
    /// </summary>
    /// <param name="processPath">The executable of the process (<see cref="Environment.ProcessPath"/>).</param>
    /// <param name="assemblyPath">The path of the assembly of <c>altanode</c>, for the <c>dotnet</c> host.</param>
    /// <param name="exists">Tells whether a file or a folder exists.</param>
    /// <returns>The executable, or null when the process has no known executable.</returns>
    public static NodeExecutable? Locate(string? processPath, string? assemblyPath, Func<string, bool> exists)
    {
        ArgumentNullException.ThrowIfNull(exists);
        if (string.IsNullOrWhiteSpace(processPath)) return null;
        // The name after the last separator, '/' or '\', as FindStore reads a path whatever the OS.
        var fileName = System.IO.Path.GetFileNameWithoutExtension(processPath[(processPath.LastIndexOfAny(['/', '\\']) + 1)..]);
        if (string.Equals(fileName, "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            return string.IsNullOrWhiteSpace(assemblyPath) ? null : new NodeExecutable(NodeExecutableKind.DotnetHost, processPath, [assemblyPath]);
        }

        var directory = System.IO.Path.GetDirectoryName(processPath);
        if (directory is not null && FindStore(processPath) is { } toolsRoot)
        {
            var shim = System.IO.Path.Combine(toolsRoot, System.IO.Path.GetFileName(processPath));
            if (exists(shim)) return new NodeExecutable(NodeExecutableKind.ToolShim, shim, []);
        }

        // The shim itself: its folder has the store of the package.
        if (directory is not null && exists(System.IO.Path.Combine(directory, ToolStoreFolder, "codealta.node")))
            return new NodeExecutable(NodeExecutableKind.ToolShim, processPath, []);
        return new NodeExecutable(NodeExecutableKind.Executable, processPath, []);
    }

    /// <summary>Describes how the service starts the node, in one line.</summary>
    /// <param name="executable">The executable.</param>
    /// <returns>The description.</returns>
    public static string Describe(NodeExecutable executable)
    {
        ArgumentNullException.ThrowIfNull(executable);
        return executable.Kind switch
        {
            NodeExecutableKind.ToolShim => $"It starts the .NET tool's shim {executable.Path}: dotnet tool update changes the version it runs.",
            NodeExecutableKind.DotnetHost => $"It starts {executable.Path} {executable.PrefixArguments[0]}: run altanode install again if that assembly moves.",
            _ => $"It starts {executable.Path}: run altanode install again if that file moves.",
        };
    }

    // The tools folder of an executable under <tools>/.store/..., or null.
    private static string? FindStore(string processPath)
    {
        var parts = processPath.Split(['/', '\\']);
        var index = Array.FindLastIndex(parts, static part => string.Equals(part, ToolStoreFolder, StringComparison.OrdinalIgnoreCase));
        if (index <= 0) return null;
        var separator = processPath.Contains('\\', StringComparison.Ordinal) && !processPath.Contains('/', StringComparison.Ordinal) ? "\\" : "/";
        var root = string.Join(separator, parts[..index]);
        return root.Length == 0 ? separator : root;
    }
}
