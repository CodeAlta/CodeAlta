using System.IO.Enumeration;
using CodeAlta.Catalog;
using XenoAtom.CommandLine;

namespace CodeAlta.LiveTool;

// The `alta project manifest` commands: the project map of a repository (`.alta/projects.toml`), which declares the folders
// of a monorepo that are projects of their own. Agents cannot write under `.alta/` with their file tools: these commands are
// how a session edits the map, a file of the repository that the user reviews and commits.
internal sealed partial class BuiltInAltaCommandContributor
{
    private static readonly AltaCommandPolicy[] ManifestPolicies =
    [
        Read("project manifest show", requiresRuntime: false, supportsCatalogOnlyContext: true),
        Read("project manifest scan", requiresRuntime: false, supportsCatalogOnlyContext: true),
        Mutating("project manifest add", requiresRuntime: false, supportsCatalogOnlyContext: true),
        Mutating("project manifest set", requiresRuntime: false, supportsCatalogOnlyContext: true),
        Mutating("project manifest remove", requiresRuntime: false, supportsCatalogOnlyContext: true),
        Mutating("project manifest label", requiresRuntime: false, supportsCatalogOnlyContext: true),
    ];

    // Folders a scan never goes into: what a build, a package manager or a tool writes, and the folders of git and CodeAlta.
    private static readonly HashSet<string> ScanSkipped = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".alta", ".claude", ".vs", ".vscode", ".idea", "node_modules", "bin", "obj", "dist", "build", "out", "target", ".next", ".nuxt",
        "coverage", "__pycache__", ".venv", "venv", ".gradle", ".terraform", "artifacts", "TestResults",
    };

    // What marks a folder as a project of its own when a scan is given no marker.
    private static readonly string[] DefaultScanMarkers =
    [
        "package.json", "*.csproj", "*.fsproj", "*.sln", "*.slnx", "Cargo.toml", "go.mod", "pyproject.toml", "setup.py", "pom.xml", "build.gradle",
        "build.gradle.kts", "Gemfile", "composer.json", "mix.exs", "Package.swift", "CMakeLists.txt", "BUILD.bazel", "project.json", "deno.json",
        "AGENTS.md",
    ];

    // The files at the root of a repository that tell how it is organized, for an agent to read before it proposes projects.
    private static readonly (string File, string Kind)[] WorkspaceFiles =
    [
        ("pnpm-workspace.yaml", "pnpm"), ("package.json", "npm"), ("lerna.json", "lerna"), ("nx.json", "nx"), ("turbo.json", "turborepo"),
        ("rush.json", "rush"), ("Cargo.toml", "cargo"), ("go.work", "go"), ("WORKSPACE", "bazel"), ("WORKSPACE.bazel", "bazel"), ("MODULE.bazel", "bazel"),
        ("pants.toml", "pants"), ("settings.gradle", "gradle"), ("settings.gradle.kts", "gradle"), ("pom.xml", "maven"), (".gitmodules", "git-submodules"),
        ("CODEOWNERS", "codeowners"), (".github/CODEOWNERS", "codeowners"), ("docs/CODEOWNERS", "codeowners"),
    ];

    private const int MaximumScanFolders = 20_000;
    private const int MaximumScanCandidates = 2_000;

    private static Command CreateProjectManifestCommand(AltaCommandContext context)
    {
        var group = Group("manifest", "Read and edit the project map of a repository: `.alta/projects.toml`, the folders of a monorepo that are projects of their own.");
        group.Add(CreateManifestShowCommand(context));
        group.Add(CreateManifestScanCommand(context));
        group.Add(CreateManifestAddCommand(context));
        group.Add(CreateManifestSetCommand(context));
        group.Add(CreateManifestRemoveCommand(context));
        group.Add(CreateManifestLabelCommand(context));
        AddHelpText(
            group,
            "A repository (a project of CodeAlta) can declare sub-projects: folders of it, any folder, a git submodule included, each a project with its own sessions, plans, tasks, skills and agents, on top of the setup of the repository (its skills, prompts, MCP servers, `config.toml`, `AGENTS.md`).",
            "The map is a file of the repository, committed with it: whoever adds the repository gets its sub-projects, grouped under it in the CodeAlta window with the labels of the map. These commands change the file; the user reviews the change in Changes and commits it.",
            "Without --project, the repository is the project of the calling session (its repository when it is a sub-project), else the project of the current folder.",
            "To set a monorepo up, activate the skill `codealta-monorepo`: `alta skill activate codealta-monorepo`.",
            "Examples: `alta project manifest show`; `alta project manifest scan --glob \"services/*\"`; `alta project manifest add services/billing --name Billing --label backend`; `alta project manifest label backend --name Backend --color #2d72d2`.");
        return group;
    }

    private static Command CreateManifestShowCommand(AltaCommandContext context)
    {
        string? projectRef = null;
        var command = Leaf("show", "Show the project map of a repository as CodeAlta reads it: its labels, its projects with their folders and the project each is, and its problems.");
        command.Add("<project>?", "The repository project: id, slug or path.", value => projectRef = value);
        command.Add("project=", "The repository project: id, slug or path.", value => projectRef ??= value);
        command.Add(async (_, _) => await HandleManifestShowAsync(context, projectRef).ConfigureAwait(false));
        AddHelpText(command,
            "Each project names `folder`: `ok`, `missing` (nothing there) or `outside` (a link that leads out of the repository); `projectId` once it is listed; `hidden` when the user hid it; `optional` when it is listed only for those who add it.",
            "Example: `alta project manifest show contoso`.");
        return command;
    }

    private static Command CreateManifestScanCommand(AltaCommandContext context)
    {
        string? projectRef = null;
        var globs = new List<string>();
        var markers = new List<string>();
        string? depth = null;
        var command = Leaf("scan", "List the folders of a repository that could be projects of its map, and the files that tell how it is organized. Writes nothing.");
        command.Add("project=", "The repository project: id, slug or path.", value => projectRef = value);
        command.Add("glob=", "A pattern of folders, relative to the root: `*` for one name, `**` for any folders between (`services/*`, `packages/**`). Repeat it for several.", value => { if (value is not null) globs.Add(value); });
        command.Add("marker=", "A file a folder must hold to be listed, a name or a pattern (`package.json`, `*.csproj`). Repeat it for several. Without it, the usual files of a project of each language.", value => { if (value is not null) markers.Add(value); });
        command.Add("depth=", "How deep the scan goes without --glob, 1 to 8; 3 by default.", value => depth = value);
        command.Add(async (_, _) => await HandleManifestScanAsync(context, projectRef, globs, markers, depth).ConfigureAwait(false));
        AddHelpText(command,
            "The scan skips what tools write (`node_modules`, `bin`, `obj`, `dist`, `target`, ...), and the folders of git and CodeAlta. A folder that is a git submodule says `submodule: true`; the submodules of `.gitmodules` are listed too.",
            "A package is not always a project: read the workspace files the scan names (`alta.project.manifest.workspace`), then ask the user which folders are projects before you add them.",
            "Examples: `alta project manifest scan`; `alta project manifest scan --glob \"services/*\" --glob \"apps/*\"`; `alta project manifest scan --glob \"packages/*\" --marker package.json`.");
        return command;
    }

    private static Command CreateManifestAddCommand(AltaCommandContext context)
    {
        string? path = null;
        var options = new ManifestProjectOptions();
        var command = Leaf("add", "Declare a folder of the repository as a project of its own.");
        command.Add("<path>", "The folder, relative to the root of the repository, or a full path inside it.", value => path = value);
        command.Add("key=", "Its key in the map: lower-case letters, digits, `-` and `_`; its identity, kept when the folder moves. Worked out from the folder by default.", value => options.Key = value);
        AddManifestProjectOptions(command, options);
        command.Add(async (_, _) => await HandleManifestAddAsync(context, path, options).ConfigureAwait(false));
        AddHelpText(command,
            "A label the map does not declare is created, named from its key; name it with `alta project manifest label`.",
            "Examples: `alta project manifest add services/billing --name Billing --label backend`; `alta project manifest add tools/reports --optional`.");
        return command;
    }

    private static Command CreateManifestSetCommand(AltaCommandContext context)
    {
        string? key = null;
        var options = new ManifestProjectOptions();
        var command = Leaf("set", "Change a project of the map: its folder, its name, its description, its labels, whether it is optional, what its sessions write.");
        command.Add("<key>", "The key of the project in the map.", value => key = value);
        command.Add("path=", "Its new folder, relative to the root: a folder that moved keeps its project and its sessions.", value => options.Path = value);
        AddManifestProjectOptions(command, options);
        command.Add("no-labels", "Take every label of the map from it.", value => options.NoLabels = value is not null);
        command.Add(async (_, _) => await HandleManifestSetAsync(context, key, options).ConfigureAwait(false));
        AddHelpText(command,
            "What is not given stays. An empty --name or --description removes it; --label replaces its labels.",
            "Examples: `alta project manifest set billing --name \"Billing service\"`; `alta project manifest set web --label frontend --label web`; `alta project manifest set web --write folder`.");
        return command;
    }

    private static Command CreateManifestRemoveCommand(AltaCommandContext context)
    {
        string? key = null;
        string? projectRef = null;
        var command = Leaf("remove", "Remove a project from the map. Its folder stays; a sub-project that has sessions stays a project of its own.");
        command.Add("<key>", "The key of the project in the map.", value => key = value);
        command.Add("project=", "The repository project: id, slug or path.", value => projectRef = value);
        command.Add(async (_, _) => await HandleManifestChangeAsync(context, projectRef, "alta project manifest remove", key,
            (root, value) => ProjectManifestEditor.RemoveProject(root, value)).ConfigureAwait(false));
        AddHelpText(command, "Example: `alta project manifest remove legacy-reports`.");
        return command;
    }

    private static Command CreateManifestLabelCommand(AltaCommandContext context)
    {
        string? key = null;
        string? projectRef = null;
        string? name = null, parent = null, icon = null, color = null, description = null;
        var remove = false;
        var command = Leaf("label", "Create or change a label of the map, or remove it: the labels a repository gives its projects, shown under it in the CodeAlta window.");
        command.Add("<key>", "The key of the label: lower-case letters, digits, `-` and `_`.", value => key = value);
        command.Add("project=", "The repository project: id, slug or path.", value => projectRef = value);
        command.Add("name=", "Its name, 1 to 64 characters.", value => name = value);
        command.Add("parent=", "The key of the label it goes under; empty for the top level.", value => parent = value);
        command.Add("icon=", "The name of its icon, as the CodeAlta window lists them; empty removes it.", value => icon = value);
        command.Add("color=", "The color of its badge, `#rgb` or `#rrggbb`; empty removes it.", value => color = value);
        command.Add("description=", "What it stands for; empty removes it.", value => description = value);
        command.Add("remove", "Remove the label: its projects lose it, the labels under it take its place.", value => remove = value is not null);
        command.Add(async (_, _) => await HandleManifestChangeAsync(context, projectRef, "alta project manifest label", key,
            (root, value) => remove ? ProjectManifestEditor.RemoveLabel(root, value) : ProjectManifestEditor.SetLabel(root, value,
                new ProjectManifestLabelEdit(name, parent, icon, color, description))).ConfigureAwait(false));
        AddHelpText(command,
            "The labels of a map are the team's: the user sees them in the window and cannot change them there, besides their own labels.",
            "Examples: `alta project manifest label backend --name Backend --icon server --color #2d72d2`; `alta project manifest label payments --name Payments --parent backend`; `alta project manifest label old --remove`.");
        return command;
    }

    private static void AddManifestProjectOptions(Command command, ManifestProjectOptions options)
    {
        command.Add("project=", "The repository project: id, slug or path.", value => options.Project = value);
        command.Add("name=", "The name shown; the name of its folder by default.", value => options.Name = value);
        command.Add("description=", "What the project is.", value => options.Description = value);
        command.Add("label=", "The key of a label of the map. Repeat it for several.", value => { if (value is not null) options.Labels.Add(value); });
        command.Add("optional:", "Listed only for those who add it; `--optional=false` lists it for everyone.", value => options.Optional = value is null || !string.Equals(value.Trim(), "false", StringComparison.OrdinalIgnoreCase));
        command.Add("write=", "What its sessions write: `repository` (the whole repository folder, the default) or `folder` (only its folder).", value => options.Write = value);
    }

    // The repository a command works on: the project named, else the repository of the calling session's project, else
    // that project, else the project of the current folder.
    private static async Task<(ProjectCatalog? Catalog, ProjectDescriptor? Repository, int ExitCode)> ResolveManifestRepositoryAsync(AltaCommandContext context, string? projectRef,
        string commandPath)
    {
        if (!context.TryGetRequired<ProjectCatalog>(nameof(ProjectCatalog), out var catalog))
        {
            return (null, null, AltaExitCodes.ServiceUnavailable);
        }

        var reference = NormalizeOptionalText(projectRef);
        var project = reference is not null
            ? await ResolveProjectAsync(catalog, reference, context, includeArchived: true).ConfigureAwait(false)
            : NormalizeOptionalText(context.Caller.SourceProjectId) is { } callerProject
                ? await ResolveProjectAsync(catalog, callerProject, context, includeArchived: true).ConfigureAwait(false)
                : await catalog.GetByPathAsync(ResolvePath(context, context.Cwd ?? Environment.CurrentDirectory), context.CancellationToken).ConfigureAwait(false);
        if (project is null)
        {
            return (catalog, null, NotFound(context, "project.notFound", reference is null
                ? $"No catalog project matches the current directory. Name the repository: `{commandPath} --project <project>`."
                : $"Project '{reference}' was not found."));
        }

        // A sub-project's map is the one of its repository, unless the sub-project is named.
        if (reference is null && project.ParentId is { } parentId && await catalog.GetByIdAsync(parentId, context.CancellationToken).ConfigureAwait(false) is { } repository)
        {
            project = repository;
        }

        return (catalog, project, AltaExitCodes.Success);
    }

    private static async ValueTask<int> HandleManifestShowAsync(AltaCommandContext context, string? projectRef)
    {
        var (catalog, repository, exitCode) = await ResolveManifestRepositoryAsync(context, projectRef, "alta project manifest show").ConfigureAwait(false);
        if (repository is null) return exitCode;
        var map = await catalog!.ReadRepositoryMapAsync(repository.Id, context.CancellationToken).ConfigureAwait(false);
        if (map is null) return NotFound(context, "project.notFound", $"Project '{repository.Slug}' was not found.");
        var labels = map.Manifest.Labels;
        AltaJsonlWriter.WriteRecord(context.Stdout, new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["type"] = "alta.project.manifest",
            ["version"] = 1,
            ["correlationId"] = context.CorrelationId,
            ["projectId"] = repository.Id,
            ["slug"] = repository.Slug,
            ["projectPath"] = repository.ProjectPath,
            ["manifestPath"] = map.ManifestPath,
            ["exists"] = map.Exists,
            ["claimedBy"] = map.ClaimedBy,
            ["labels"] = labels.Select(label => new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["key"] = label.Key,
                ["id"] = ProjectCatalog.DeclaredLabelId(repository.Slug, label.Key),
                ["name"] = label.Name,
                ["parent"] = label.Parent,
                ["icon"] = label.Icon,
                ["color"] = label.Color,
                ["description"] = label.Description,
            }).ToArray(),
            ["projects"] = map.Entries.Select(static entry => new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["key"] = entry.Entry.Key,
                ["path"] = entry.Entry.Path,
                ["name"] = entry.Entry.Name,
                ["description"] = entry.Entry.Description,
                ["labels"] = entry.Entry.Labels,
                ["optional"] = entry.Entry.Optional,
                ["write"] = entry.Entry.Write == ProjectWriteScope.Folder ? "folder" : "repository",
                ["folder"] = entry.Folder switch { ProjectManifestFolderState.Ok => "ok", ProjectManifestFolderState.Outside => "outside", _ => "missing" },
                ["submodule"] = entry.Folder == ProjectManifestFolderState.Ok && File.Exists(Path.Combine(entry.FullPath, ".git")) ? true : null,
                ["projectId"] = entry.Project?.Id,
                ["hidden"] = entry.Project?.Archived == true ? true : null,
                ["claimedBy"] = entry.ClaimedBy,
            }).ToArray(),
            ["problems"] = map.Problems.Select(static problem => new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["key"] = problem.Key,
                ["code"] = problem.Code,
                ["message"] = problem.Message,
            }).ToArray(),
        });
        return AltaExitCodes.Success;
    }

    private static async ValueTask<int> HandleManifestScanAsync(AltaCommandContext context, string? projectRef, IReadOnlyList<string> globs, IReadOnlyList<string> markers,
        string? depthText)
    {
        const string CommandPath = "alta project manifest scan";
        var (catalog, repository, exitCode) = await ResolveManifestRepositoryAsync(context, projectRef, CommandPath).ConfigureAwait(false);
        if (repository is null) return exitCode;
        var depth = 3;
        if (NormalizeOptionalText(depthText) is { } text && (!int.TryParse(text, out depth) || depth is < 1 or > 8))
        {
            return UsageError(context, "usage.invalidDepth", "--depth is a number from 1 to 8.", CommandPath);
        }

        var patterns = new List<string[]>();
        foreach (var glob in globs)
        {
            var normalized = glob.Trim().Replace('\\', '/').Trim('/');
            if (normalized.Length == 0 || normalized.Split('/').Any(static part => part is "" or "." or ".."))
            {
                return UsageError(context, "usage.invalidGlob", $"`{glob}` is not a pattern of folders inside the repository.", CommandPath);
            }

            patterns.Add(normalized.Split('/'));
        }

        var root = repository.ProjectPath;
        var map = await catalog!.ReadManifestAsync(root, context.CancellationToken).ConfigureAwait(false);
        var declared = map.Projects.ToDictionary(static entry => entry.Path, static entry => entry.Key, StringComparer.OrdinalIgnoreCase);
        var submodules = ReadSubmodulePaths(root);
        var wanted = markers.Count > 0 ? markers : DefaultScanMarkers;
        var maximumDepth = patterns.Count == 0 ? depth : patterns.Max(static pattern => pattern.Contains("**") ? 8 : pattern.Length);
        var candidates = new List<Dictionary<string, object?>>();
        var visited = 0;
        var truncated = false;
        var queue = new Queue<(string Full, string Relative, int Depth)>();
        queue.Enqueue((root, string.Empty, 0));
        while (queue.Count > 0)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            var (full, relative, level) = queue.Dequeue();
            if (level > 0)
            {
                var matches = patterns.Count == 0 || patterns.Any(pattern => GlobMatches(pattern, 0, relative.Split('/'), 0));
                var found = matches ? FindMarkers(full, wanted) : [];
                var submodule = File.Exists(Path.Combine(full, ".git")) || submodules.Contains(relative);
                if (matches && (found.Count > 0 || submodule || patterns.Count > 0 && markers.Count == 0))
                {
                    if (candidates.Count >= MaximumScanCandidates) { truncated = true; break; }
                    candidates.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["type"] = "alta.project.manifest.candidate",
                        ["version"] = 1,
                        ["correlationId"] = context.CorrelationId,
                        ["path"] = relative,
                        ["key"] = ProjectManifest.KeyFrom(relative),
                        ["markers"] = found,
                        ["submodule"] = submodule ? true : null,
                        ["declared"] = declared.GetValueOrDefault(relative),
                    });
                }
            }

            if (level >= maximumDepth) continue;
            IEnumerable<string> children;
            try
            {
                children = Directory.EnumerateDirectories(full);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var child in children.Order(StringComparer.OrdinalIgnoreCase))
            {
                var name = Path.GetFileName(child);
                if (ScanSkipped.Contains(name) || name.StartsWith('.') && level == 0 && name is not ".github") continue;
                try
                {
                    // A link is not followed: it may lead out of the repository, or back into it.
                    if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0) continue;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    continue;
                }

                if (++visited > MaximumScanFolders) { truncated = true; queue.Clear(); break; }
                queue.Enqueue((child, relative.Length == 0 ? name : relative + "/" + name, level + 1));
            }
        }

        foreach (var candidate in candidates) AltaJsonlWriter.WriteRecord(context.Stdout, candidate);
        var workspaces = WorkspaceFiles.Where(file => File.Exists(Path.Combine(root, file.File))).ToArray();
        foreach (var (file, kind) in workspaces)
        {
            AltaJsonlWriter.WriteRecord(context.Stdout, new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["type"] = "alta.project.manifest.workspace",
                ["version"] = 1,
                ["correlationId"] = context.CorrelationId,
                ["file"] = file,
                ["kind"] = kind,
            });
        }

        AltaJsonlWriter.WriteRecord(context.Stdout, new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["type"] = "alta.project.manifest.scan",
            ["version"] = 1,
            ["correlationId"] = context.CorrelationId,
            ["projectId"] = repository.Id,
            ["projectPath"] = root,
            ["count"] = candidates.Count,
            ["foldersVisited"] = Math.Min(visited, MaximumScanFolders),
            ["truncated"] = truncated,
            ["submodules"] = submodules.Order(StringComparer.Ordinal).ToArray(),
            ["solutions"] = Directory.EnumerateFiles(root, "*.sln*").Select(Path.GetFileName).Order(StringComparer.Ordinal).Take(50).ToArray(),
        });
        return AltaExitCodes.Success;
    }

    private static async ValueTask<int> HandleManifestAddAsync(AltaCommandContext context, string? path, ManifestProjectOptions options)
    {
        const string CommandPath = "alta project manifest add";
        if (NormalizeOptionalText(path) is not { } given)
        {
            return UsageError(context, "usage.missingPath", "The folder of the project is required, relative to the root of the repository.", CommandPath);
        }

        if (options.WriteProblem() is { } writeProblem) return UsageError(context, "usage.invalidWrite", writeProblem, CommandPath);
        return await HandleManifestChangeAsync(context, options.Project, CommandPath, given, (root, value) =>
        {
            // A full path inside the repository is taken as its relative path.
            var relative = Path.IsPathFullyQualified(value) ? Path.GetRelativePath(root, value) : value;
            return ProjectManifestEditor.AddProject(root, NormalizeOptionalText(options.Key),
                options.Edit() with { Path = relative });
        }).ConfigureAwait(false);
    }

    private static async ValueTask<int> HandleManifestSetAsync(AltaCommandContext context, string? key, ManifestProjectOptions options)
    {
        const string CommandPath = "alta project manifest set";
        if (options.WriteProblem() is { } writeProblem) return UsageError(context, "usage.invalidWrite", writeProblem, CommandPath);
        if (options.NoLabels && options.Labels.Count > 0) return UsageError(context, "usage.labelConflict", "Use either --label or --no-labels, not both.", CommandPath);
        return await HandleManifestChangeAsync(context, options.Project, CommandPath, key,
            (root, value) => ProjectManifestEditor.SetProject(root, value, options.Edit())).ConfigureAwait(false);
    }

    // Runs a change of the map of the repository, and writes the map as it reads after it.
    private static async ValueTask<int> HandleManifestChangeAsync(AltaCommandContext context, string? projectRef, string commandPath, string? key,
        Func<string, string, ProjectManifestChange> change)
    {
        if (NormalizeOptionalText(key) is not { } value)
        {
            return UsageError(context, "usage.missingKey", "A key is required.", commandPath);
        }

        if (await RefuseSettingsChangeAsync(context, "change the project map of a repository").ConfigureAwait(false) is { } settingsRefused)
        {
            return settingsRefused;
        }

        var (catalog, repository, exitCode) = await ResolveManifestRepositoryAsync(context, projectRef, commandPath).ConfigureAwait(false);
        if (repository is null) return exitCode;
        if (await RefuseOtherProjectToSandboxAsync(context, repository.Id, "change the project map of another project").ConfigureAwait(false) is { } refused)
        {
            return refused;
        }

        ProjectManifestChange result;
        try
        {
            result = change(repository.ProjectPath, value);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            AltaJsonlWriter.WriteError(context.Stderr, context.CorrelationId, "manifest.writeFailed", AltaExitCodes.Failure, exception.Message);
            return AltaExitCodes.Failure;
        }

        switch (result.Status)
        {
            case ProjectManifestChangeStatus.NotFound:
                return NotFound(context, "manifest.notFound", result.Message ?? "Not found.");
            case ProjectManifestChangeStatus.Invalid:
                return UsageError(context, "manifest.invalid", result.Message ?? "The change is not valid.", commandPath);
            case ProjectManifestChangeStatus.Unsupported:
                return Unsupported(context, "manifest.unsupported", result.Message ?? "The map cannot be edited as it is written.");
        }

        AltaJsonlWriter.WriteRecord(context.Stdout, new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["type"] = "alta.project.manifest.changed",
            ["version"] = 1,
            ["correlationId"] = context.CorrelationId,
            ["projectId"] = repository.Id,
            ["manifestPath"] = ProjectManifest.PathOf(repository.ProjectPath),
            ["key"] = result.Key,
            ["changed"] = result.Status == ProjectManifestChangeStatus.Updated,
            ["createdLabels"] = result.CreatedLabels is { Count: > 0 } created ? created : null,
            ["next"] = result.Status == ProjectManifestChangeStatus.Updated
                ? "The map is a file of the repository: tell the user to review it in Changes and commit it."
                : null,
        });
        if (result.Status == ProjectManifestChangeStatus.Updated) NotifySpacesChanged(context);
        return AltaExitCodes.Success;
    }

    // Whether the parts of a path match the parts of a pattern: `*` and `?` within a name, `**` for any folders.
    private static bool GlobMatches(string[] pattern, int at, string[] parts, int part)
    {
        if (at == pattern.Length) return part == parts.Length;
        if (pattern[at] == "**")
        {
            for (var skip = part; skip <= parts.Length; skip++)
                if (GlobMatches(pattern, at + 1, parts, skip)) return true;
            return false;
        }

        return part < parts.Length && FileSystemName.MatchesSimpleExpression(pattern[at], parts[part], ignoreCase: true)
            && GlobMatches(pattern, at + 1, parts, part + 1);
    }

    // The files of a folder that a marker names, at most a few.
    private static List<string> FindMarkers(string folder, IReadOnlyList<string> markers)
    {
        var found = new List<string>();
        try
        {
            foreach (var file in Directory.EnumerateFiles(folder))
            {
                var name = Path.GetFileName(file);
                if (markers.Any(marker => FileSystemName.MatchesSimpleExpression(marker, name, ignoreCase: true)) && found.Count < 8) found.Add(name);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }

        return found;
    }

    // The folders `.gitmodules` names, relative to the root.
    private static HashSet<string> ReadSubmodulePaths(string root)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var file = Path.Combine(root, ".gitmodules");
            if (!File.Exists(file) || new FileInfo(file).Length > 256 * 1024) return paths;
            foreach (var line in File.ReadLines(file))
            {
                var trimmed = line.Trim();
                if (!trimmed.StartsWith("path", StringComparison.Ordinal) || trimmed.IndexOf('=') is not (> 0 and var equals)) continue;
                if (trimmed[..equals].Trim() != "path") continue;
                if (ProjectManifest.NormalizePath(trimmed[(equals + 1)..].Trim(), out _) is { } path) paths.Add(path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }

        return paths;
    }

    private sealed class ManifestProjectOptions
    {
        public string? Project { get; set; }
        public string? Key { get; set; }
        public string? Path { get; set; }
        public string? Name { get; set; }
        public string? Description { get; set; }
        public List<string> Labels { get; } = [];
        public bool NoLabels { get; set; }
        public bool? Optional { get; set; }
        public string? Write { get; set; }

        public string? WriteProblem() => Write is null or "repository" or "folder" ? null : "--write is `repository` or `folder`.";

        public ProjectManifestEdit Edit() => new(Path, Name, Description, NoLabels ? [] : Labels.Count > 0 ? Labels : null, Optional,
            Write switch { "folder" => ProjectWriteScope.Folder, "repository" => ProjectWriteScope.Repository, _ => null });
    }
}
