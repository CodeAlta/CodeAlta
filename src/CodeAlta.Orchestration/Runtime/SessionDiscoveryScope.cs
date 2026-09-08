namespace CodeAlta.Orchestration.Runtime;

/// <summary>Supplies an explicit discovery home and one inclusive instruction ancestry boundary.</summary>
/// <remarks>
/// Validation is lexical only. This is not an ownership, filesystem-link or sandbox guarantee.
/// Built-in skill discovery, repository discovery, plugins, providers and authentication are not isolated.
/// </remarks>
public sealed class SessionDiscoveryScope
{
    /// <summary>Initializes an immutable discovery scope without probing the filesystem.</summary>
    /// <param name="userProfileRoot">Explicit absolute home used by prompt and user/common skill discovery.</param>
    /// <param name="instructionAncestorRoot">Explicit absolute inclusive boundary for project instruction ancestry.</param>
    /// <exception cref="ArgumentNullException">A root is null.</exception>
    /// <exception cref="ArgumentException">A root is blank, not fully qualified or lexically invalid.</exception>
    public SessionDiscoveryScope(string userProfileRoot, string instructionAncestorRoot)
    {
        UserProfileRoot = NormalizeAbsolutePath(userProfileRoot, nameof(userProfileRoot));
        InstructionAncestorRoot = NormalizeAbsolutePath(instructionAncestorRoot, nameof(instructionAncestorRoot));
    }

    /// <summary>Gets the normalized explicit discovery home, independent of the catalog root.</summary>
    public string UserProfileRoot { get; }

    /// <summary>Gets the normalized inclusive instruction ancestry boundary.</summary>
    public string InstructionAncestorRoot { get; }

    private static StringComparison PathComparison
        => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    internal static string NormalizeAbsolutePath(string path, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path, parameterName);
        if (!Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException("Path must be explicitly absolute.", parameterName);
        }

        return NormalizePath(path);
    }

    private static string NormalizePath(string path)
    {
        var normalized = Path.GetFullPath(path);
        // GetFullPath has established a fully qualified path, hence a non-null volume root.
        var rootLength = Path.GetPathRoot(normalized)!.Length;
        while (normalized.Length > rootLength && Path.EndsInDirectorySeparator(normalized))
        {
            normalized = Path.TrimEndingDirectorySeparator(normalized);
        }

        return normalized;
    }

    internal string ValidateProjectPath(string path, string parameterName)
    {
        var normalized = NormalizeAbsolutePath(path, parameterName);
        var prefix = Path.EndsInDirectorySeparator(InstructionAncestorRoot)
            ? InstructionAncestorRoot
            : InstructionAncestorRoot + Path.DirectorySeparatorChar;
        if (!string.Equals(normalized, InstructionAncestorRoot, PathComparison) &&
            !normalized.StartsWith(prefix, PathComparison))
        {
            throw new ArgumentException("Path must be within the instruction ancestry boundary.", parameterName);
        }

        return normalized;
    }

    internal void ValidateHostRoots(string? globalRoot, string? currentProjectPath)
    {
        // Preserve the option names in validation errors; the callees reject null before path operations.
        NormalizeAbsolutePath(globalRoot!, nameof(globalRoot));
        ValidateProjectPath(currentProjectPath!, nameof(currentProjectPath));
    }

    internal static IReadOnlyList<string> GetInstructionAncestors(string root, SessionDiscoveryScope? discoveryScope)
    {
        var current = discoveryScope is null
            ? Path.GetFullPath(root)
            : discoveryScope.ValidateProjectPath(root, nameof(root));
        var stack = new Stack<string>();
        while (!string.IsNullOrWhiteSpace(current))
        {
            stack.Push(current);
            if (discoveryScope is not null && string.Equals(current, discoveryScope.InstructionAncestorRoot, PathComparison))
            {
                break;
            }

            current = Path.GetDirectoryName(current);
        }

        return stack.ToArray();
    }
}
