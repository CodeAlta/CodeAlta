namespace CodeAlta.Catalog.Skills;

/// <summary>
/// Resolves project-local CodeAlta skill roots.
/// </summary>
public sealed class ProjectCodeAltaSkillRootProvider : ISkillRootProvider
{
    /// <inheritdoc />
    public ValueTask<IReadOnlyList<SkillRootRegistration>> GetRootsAsync(
        SkillDiscoveryContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        return ValueTask.FromResult<IReadOnlyList<SkillRootRegistration>>(
            context.ProjectRoots
                .Where(static path => !string.IsNullOrWhiteSpace(path))
                .Select(static projectRoot => new SkillRootRegistration
                {
                    RootPath = Path.Combine(projectRoot, ".alta", "skills"),
                    SourceKind = SkillSourceKind.ProjectAlta,
                    SourceId = $"project-alta:{Path.GetFullPath(projectRoot)}",
                    Scope = SkillScopeKind.Project,
                    Precedence = 0,
                })
                .ToArray());
    }
}

/// <summary>
/// Resolves project-local common Agent Skills roots.
/// </summary>
public sealed class ProjectCommonSkillRootProvider : ISkillRootProvider
{
    /// <inheritdoc />
    public ValueTask<IReadOnlyList<SkillRootRegistration>> GetRootsAsync(
        SkillDiscoveryContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        return ValueTask.FromResult<IReadOnlyList<SkillRootRegistration>>(
            context.ProjectRoots
                .Where(static path => !string.IsNullOrWhiteSpace(path))
                .Select(static projectRoot => new SkillRootRegistration
                {
                    RootPath = Path.Combine(projectRoot, ".agents", "skills"),
                    SourceKind = SkillSourceKind.ProjectCommon,
                    SourceId = $"project-common:{Path.GetFullPath(projectRoot)}",
                    Scope = SkillScopeKind.Project,
                    Precedence = 1,
                })
                .ToArray());
    }
}

/// <summary>
/// Resolves the project-local skill roots of GitHub Copilot (<c>.github/skills</c>), so that a project written for
/// Copilot brings its skills as they are. A skill of the same name under <c>.alta/skills</c> or
/// <c>.agents/skills</c> comes first.
/// </summary>
public sealed class ProjectCopilotSkillRootProvider : ISkillRootProvider
{
    /// <inheritdoc />
    public ValueTask<IReadOnlyList<SkillRootRegistration>> GetRootsAsync(
        SkillDiscoveryContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        return ValueTask.FromResult<IReadOnlyList<SkillRootRegistration>>(
            context.ProjectRoots
                .Where(static path => !string.IsNullOrWhiteSpace(path))
                .Select(static projectRoot => new SkillRootRegistration
                {
                    RootPath = Path.Combine(projectRoot, ".github", "skills"),
                    SourceKind = SkillSourceKind.ProjectCopilot,
                    SourceId = $"project-copilot:{Path.GetFullPath(projectRoot)}",
                    Scope = SkillScopeKind.Project,
                    // With the common root: of two skills of one name, the path decides, and `.agents` comes before `.github`.
                    Precedence = 1,
                })
                .ToArray());
    }
}

/// <summary>
/// Resolves the user-level skill root of GitHub Copilot (<c>~/.copilot/skills</c>).
/// </summary>
public sealed class UserCopilotSkillRootProvider : ISkillRootProvider
{
    /// <inheritdoc />
    public ValueTask<IReadOnlyList<SkillRootRegistration>> GetRootsAsync(
        SkillDiscoveryContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(context.UserProfileRoot))
        {
            return ValueTask.FromResult<IReadOnlyList<SkillRootRegistration>>([]);
        }

        return ValueTask.FromResult<IReadOnlyList<SkillRootRegistration>>(
        [
            new SkillRootRegistration
            {
                RootPath = Path.Combine(context.UserProfileRoot, ".copilot", "skills"),
                SourceKind = SkillSourceKind.UserCopilot,
                SourceId = $"user-copilot:{Path.GetFullPath(context.UserProfileRoot)}",
                Scope = SkillScopeKind.User,
                // With the common root of the user, after it by its path.
                Precedence = 3,
            },
        ]);
    }
}

/// <summary>
/// Resolves user-level CodeAlta skill roots.
/// </summary>
public sealed class UserCodeAltaSkillRootProvider : ISkillRootProvider
{
    /// <inheritdoc />
    public ValueTask<IReadOnlyList<SkillRootRegistration>> GetRootsAsync(
        SkillDiscoveryContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(context.UserCodeAltaRoot))
        {
            return ValueTask.FromResult<IReadOnlyList<SkillRootRegistration>>([]);
        }

        return ValueTask.FromResult<IReadOnlyList<SkillRootRegistration>>(
        [
            new SkillRootRegistration
            {
                RootPath = Path.Combine(context.UserCodeAltaRoot, "skills"),
                SourceKind = SkillSourceKind.UserAlta,
                SourceId = $"user-alta:{Path.GetFullPath(context.UserCodeAltaRoot)}",
                Scope = SkillScopeKind.User,
                Precedence = 2,
            },
        ]);
    }
}

/// <summary>
/// Resolves user-level common Agent Skills roots.
/// </summary>
public sealed class UserCommonSkillRootProvider : ISkillRootProvider
{
    /// <inheritdoc />
    public ValueTask<IReadOnlyList<SkillRootRegistration>> GetRootsAsync(
        SkillDiscoveryContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(context.UserProfileRoot))
        {
            return ValueTask.FromResult<IReadOnlyList<SkillRootRegistration>>([]);
        }

        return ValueTask.FromResult<IReadOnlyList<SkillRootRegistration>>(
        [
            new SkillRootRegistration
            {
                RootPath = Path.Combine(context.UserProfileRoot, ".agents", "skills"),
                SourceKind = SkillSourceKind.UserCommon,
                SourceId = $"user-common:{Path.GetFullPath(context.UserProfileRoot)}",
                Scope = SkillScopeKind.User,
                Precedence = 3,
            },
        ]);
    }
}

/// <summary>
/// Resolves CodeAlta built-in skill roots bundled with the application.
/// </summary>
public sealed class BuiltInCodeAltaSkillRootProvider : ISkillRootProvider
{
    private readonly string? _rootPath;

    /// <summary>Uses the unchanged application/source builtin discovery route.</summary>
    public BuiltInCodeAltaSkillRootProvider()
    {
    }

    /// <summary>Uses one explicit absolute builtin root without ancestor discovery or existence probes.</summary>
    /// <exception cref="ArgumentException">The root is blank or not fully qualified.</exception>
    public BuiltInCodeAltaSkillRootProvider(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        if (!Path.IsPathFullyQualified(rootPath))
        {
            throw new ArgumentException("The builtin skill root must be fully qualified.", nameof(rootPath));
        }
        _rootPath = Path.GetFullPath(rootPath);
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<SkillRootRegistration>> GetRootsAsync(
        SkillDiscoveryContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        var rootPath = _rootPath ?? ResolveRootPath();
        return ValueTask.FromResult<IReadOnlyList<SkillRootRegistration>>(
        [
            new SkillRootRegistration
            {
                RootPath = rootPath,
                SourceKind = SkillSourceKind.Builtin,
                SourceId = "builtin:codealta",
                Scope = SkillScopeKind.Builtin,
                Precedence = 4,
            },
        ]);
    }

    private static string ResolveRootPath()
    {
        var assemblyDirectory = Path.GetDirectoryName(typeof(BuiltInCodeAltaSkillRootProvider).Assembly.Location)!;
        for (var directory = new DirectoryInfo(assemblyDirectory); directory is not null; directory = directory.Parent)
        {
            var sourceRoot = Path.Combine(directory.FullName, "CodeAlta.Catalog", "BuiltinSkills");
            if (Directory.Exists(sourceRoot))
            {
                return sourceRoot;
            }
        }

        for (var directory = new DirectoryInfo(assemblyDirectory); directory is not null; directory = directory.Parent)
        {
            var copiedRoot = Path.Combine(directory.FullName, "BuiltinSkills");
            if (Directory.Exists(copiedRoot))
            {
                return copiedRoot;
            }
        }

        return Path.Combine(assemblyDirectory, "BuiltinSkills");
    }
}
