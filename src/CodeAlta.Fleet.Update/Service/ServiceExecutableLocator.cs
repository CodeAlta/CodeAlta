namespace CodeAlta.Fleet.Update.Service;

/// <summary>How the service starts <c>altanode</c> or <c>altaserver</c>.</summary>
public enum ServiceExecutableKind
{
    /// <summary>The executable of a self-contained archive or a build: the service runs this file.</summary>
    Executable,

    /// <summary>
    /// The shim of a .NET tool (<c>~/.dotnet/tools/altanode</c>, <c>altaserver.cmd</c> on Windows): its path stays when
    /// <c>dotnet tool update</c> replaces the version under <c>.store</c>, so the service follows the updates.
    /// </summary>
    ToolShim,

    /// <summary>The <c>dotnet</c> host with the assembly of the product: the service runs that assembly where it is.</summary>
    DotnetHost,
}

/// <summary>The command a service starts: the executable and the arguments before <c>run</c>.</summary>
/// <param name="Kind">What the executable is.</param>
/// <param name="Path">The executable.</param>
/// <param name="PrefixArguments">The arguments before <c>run</c>: the assembly for the <c>dotnet</c> host, none otherwise.</param>
public sealed record ServiceExecutable(ServiceExecutableKind Kind, string Path, IReadOnlyList<string> PrefixArguments);

/// <summary>Finds the stable path a service starts <c>altanode</c> or <c>altaserver</c> from.</summary>
public static class ServiceExecutableLocator
{
    private const string ToolStoreFolder = ".store";

    /// <summary>
    /// Finds the executable of this process as a service should start it. A .NET tool runs from its shim in its tools
    /// folder (<c>~/.dotnet/tools</c>), which <c>dotnet tool update</c> points at the new version under <c>.store</c>: an
    /// app host of the same name, the symbolic link <c>altanode</c> .NET 10 writes on macOS and Linux, or the batch file
    /// <c>altanode.cmd</c> it writes on Windows. The shim is kept, and an executable found under <c>.store</c> is replaced
    /// by the shim of its tools folder. A process of the <c>dotnet</c> host runs the assembly of the product with it.
    /// </summary>
    /// <param name="product">The product of the process, whose package names its folder under <c>.store</c>.</param>
    /// <param name="processPath">The executable of the process (<see cref="Environment.ProcessPath"/>).</param>
    /// <param name="assemblyPath">The path of the assembly of the product, for the <c>dotnet</c> host.</param>
    /// <param name="exists">Tells whether a file or a folder exists.</param>
    /// <returns>The executable, or null when the process has no known executable.</returns>
    public static ServiceExecutable? Locate(UpdateProduct product, string? processPath, string? assemblyPath, Func<string, bool> exists)
    {
        ArgumentNullException.ThrowIfNull(product);
        ArgumentNullException.ThrowIfNull(exists);
        if (string.IsNullOrWhiteSpace(processPath)) return null;
        // The name after the last separator, '/' or '\', as FindStore reads a path whatever the OS.
        var leaf = processPath[(processPath.LastIndexOfAny(['/', '\\']) + 1)..];
        var fileName = System.IO.Path.GetFileNameWithoutExtension(leaf);
        if (string.Equals(fileName, "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            return string.IsNullOrWhiteSpace(assemblyPath) ? null : new ServiceExecutable(ServiceExecutableKind.DotnetHost, processPath, [assemblyPath]);
        }

        var directory = System.IO.Path.GetDirectoryName(processPath);
        if (directory is not null && FindStore(processPath) is { } toolsRoot)
        {
            var shim = System.IO.Path.Combine(toolsRoot, leaf);
            if (exists(shim)) return new ServiceExecutable(ServiceExecutableKind.ToolShim, shim, []);
            // .NET 10 writes the shim of a RID-specific tool on Windows as a batch file, with no app host beside it.
            if (processPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                var batch = System.IO.Path.Combine(toolsRoot, fileName + ".cmd");
                if (exists(batch)) return new ServiceExecutable(ServiceExecutableKind.ToolShim, batch, []);
            }
        }

        // The shim itself: its folder has the store of the package.
        if (directory is not null && exists(System.IO.Path.Combine(directory, ToolStoreFolder, product.PackageId.ToLowerInvariant())))
            return new ServiceExecutable(ServiceExecutableKind.ToolShim, processPath, []);
        return new ServiceExecutable(ServiceExecutableKind.Executable, processPath, []);
    }

    /// <summary>Describes how the service starts the product, in one line.</summary>
    /// <param name="product">The product.</param>
    /// <param name="executable">The executable.</param>
    /// <returns>The description.</returns>
    public static string Describe(UpdateProduct product, ServiceExecutable executable)
    {
        ArgumentNullException.ThrowIfNull(product);
        ArgumentNullException.ThrowIfNull(executable);
        return executable.Kind switch
        {
            ServiceExecutableKind.ToolShim => $"It starts the .NET tool's shim {executable.Path}: dotnet tool update changes the version it runs.",
            ServiceExecutableKind.DotnetHost => $"It starts {executable.Path} {executable.PrefixArguments[0]}: run {product.AssemblyName} install again if that assembly moves.",
            _ => $"It starts {executable.Path}: run {product.AssemblyName} install again if that file moves.",
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
