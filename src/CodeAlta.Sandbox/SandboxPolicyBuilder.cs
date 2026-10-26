using System.Security.Cryptography;
using System.Text;

namespace CodeAlta.Sandbox;

/// <summary>Builds the policy of a session from its profile, its folders and the computer.</summary>
public static class SandboxPolicyBuilder
{
    // The entries of a working tree that other programs read as code or settings: git runs hooks and reads
    // commands from its configuration (core.fsmonitor, core.hooksPath, filters), CodeAlta builds plugins and starts
    // MCP servers from `.alta`, editors and coding agents run tasks and hooks from their folders.
    private static readonly string[] ProtectedEntries =
    [
        ".vscode", ".claude", ".codex", ".copilot", ".gemini", ".cursor", ".mcp.json", Path.Combine(".github", "mcp.json"),
        Path.Combine(".alta", "config.toml"), Path.Combine(".alta", "mcp.json"), Path.Combine(".alta", "plugins"),
        Path.Combine(".alta", "skills"), Path.Combine(".alta", "prompts"), Path.Combine(".alta", "plugin-data"),
        Path.Combine(".alta", "local"), Path.Combine(".alta", "issues.toml"),
    ];

    // Below the home folder: what holds secrets of CodeAlta, of tools and of the user. They are outside every
    // grant by default; they are denied when a profile grants a folder that holds them.
    private static readonly string[] HomeSecrets =
    [
        ".alta", ".ssh", ".gnupg", ".aws", ".azure", ".kube", ".docker", Path.Combine(".config", "gh"), Path.Combine(".config", "git", "credentials"),
        ".claude", ".codex", ".copilot", ".gemini", ".cursor", ".grok", Path.Combine("AppData", "Roaming", "Cursor"), Path.Combine(".config", "cursor"), ".npmrc", ".yarnrc", ".pypirc", ".netrc", "_netrc", ".git-credentials",
        ".bash_history", ".zsh_history", Path.Combine(".local", "share", "keyrings"), Path.Combine("Library", "Keychains"),
        Path.Combine("AppData", "Roaming", "NuGet"), Path.Combine("AppData", "Roaming", "Microsoft", "Credentials"),
        Path.Combine("AppData", "Local", "Microsoft", "Credentials"), Path.Combine("AppData", "Roaming", "Microsoft", "Windows", "PowerShell", "PSReadLine"),
        Path.Combine("AppData", "Local", "Google", "Chrome", "User Data"), Path.Combine("AppData", "Local", "Microsoft", "Edge", "User Data"),
        Path.Combine("AppData", "Roaming", "Mozilla"),
    ];

    // The variables of the application a program needs to start and find its tools; nothing else of the
    // application's environment reaches the sandbox unless the profile names it.
    private static readonly string[] WindowsPassedVariables =
    [
        "SystemRoot", "windir", "SystemDrive", "ComSpec", "PATH", "PATHEXT", "ProgramFiles", "ProgramFiles(x86)", "ProgramW6432", "ProgramData",
        "CommonProgramFiles", "CommonProgramFiles(x86)", "CommonProgramW6432", "NUMBER_OF_PROCESSORS", "PROCESSOR_ARCHITECTURE",
        "PROCESSOR_IDENTIFIER", "PROCESSOR_LEVEL", "PROCESSOR_REVISION", "OS", "DOTNET_ROOT", "DOTNET_ROOT(x86)",
    ];

    private static readonly string[] UnixPassedVariables = ["PATH", "LANG", "LANGUAGE", "LC_ALL", "LC_CTYPE", "TERM", "TZ", "USER", "LOGNAME", "DOTNET_ROOT"];

    // Parts of a variable name that say it holds a secret: such a variable of the session is passed on only when
    // the profile names it.
    private static readonly string[] SecretNameParts = ["TOKEN", "SECRET", "PASSWORD", "PASSWD", "CREDENTIAL", "API_KEY", "APIKEY", "PRIVATE_KEY", "ACCESS_KEY"];

    /// <summary>Builds the policy of one session.</summary>
    /// <param name="profile">The profile of the session.</param>
    /// <param name="folders">The folders the session works in.</param>
    /// <param name="host">What the computer gives the sandbox.</param>
    /// <param name="sessionEnvironment">The variables the session gives its processes (its identity, its markers); those named like a secret are dropped unless the profile names them.</param>
    /// <param name="sessionKey">A key of the session, for its private temporary folder.</param>
    /// <returns>The policy.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">The working directory is not a full path.</exception>
    public static SandboxPolicy Build(ResolvedSandboxProfile profile, SandboxSessionFolders folders, SandboxHost host,
        IEnumerable<KeyValuePair<string, string>> sessionEnvironment, string sessionKey)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(folders);
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(sessionEnvironment);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionKey);
        if (!Path.IsPathFullyQualified(folders.WorkingDirectory)) throw new ArgumentException("The working directory must be a full path.", nameof(folders));

        var comparer = PathComparer;
        var home = Full(host.HomeDirectory);
        var workingDirectory = Full(folders.WorkingDirectory);
        var profileRoot = Path.Combine(Full(host.DataRoot), "profiles", SafeName(profile.Name));
        var sandboxHome = folders.OwnHome is { } ownHome ? Path.Combine(Full(host.DataRoot), "agents", SafeName(ownHome), "home") : Path.Combine(profileRoot, "home");
        var caches = Path.Combine(profileRoot, "caches");
        var sessionRoot = Path.Combine(Full(host.DataRoot), "sessions", SafeName(sessionKey));
        var temp = Path.Combine(sessionRoot, "temp");
        var secrets = HomeSecrets.Select(entry => Path.Combine(home, entry)).Concat(host.SecretFolders.Select(Full)).ToList();
        // A working directory in a folder of secrets is not granted: a global session works in the folder of CodeAlta,
        // and works in a folder of its own in the sandbox. The worktrees CodeAlta keeps there are the exception.
        var codeAltaRoots = host.SecretFolders.Select(Full).Append(Path.Combine(home, ".alta"));
        if (secrets.Any(secret => IsInside(secret, workingDirectory))
            && !codeAltaRoots.Any(root => IsInside(Path.Combine(root, "worktrees"), workingDirectory) && !comparer.Equals(Path.Combine(root, "worktrees"), workingDirectory)))
        {
            workingDirectory = Path.Combine(sessionRoot, "work");
        }

        var readWrite = new List<string> { workingDirectory };
        var gitFolders = GitFolders(workingDirectory, folders.Repositories.Select(Full).ToList());
        // The common folder of a worktree holds its own git folder: that one is not granted by itself.
        readWrite.AddRange(gitFolders.Take(1).Where(folder => !IsInside(workingDirectory, folder)));
        readWrite.AddRange(folders.Writable.Select(Full));
        if (folders.Scratchpad is { } scratchpad) readWrite.Add(Full(scratchpad));
        readWrite.AddRange(profile.Write.Select(path => Expand(path, home)));
        readWrite.AddRange([sandboxHome, caches, temp]);

        var readOnly = new List<string>();
        readOnly.AddRange(host.ToolFolders.Select(Full));
        readOnly.AddRange(host.ProgramFolders.Select(Full));
        readOnly.AddRange(profile.Read.Select(path => Expand(path, home)));
        var userGitConfig = Path.Combine(home, ".gitconfig");
        if (File.Exists(userGitConfig)) readOnly.Add(userGitConfig);
        var userPackages = Path.Combine(home, ".nuget", "packages");
        if (Directory.Exists(userPackages)) readOnly.Add(userPackages);

        var protectedEntries = folders.Protected.Select(Full).ToList();
        var roots =readWrite.Take(1).Concat(folders.Writable.Select(Full)).Distinct(comparer).ToList();
        foreach (var root in roots)
        {
            protectedEntries.AddRange(ProtectedEntries.Select(entry => Path.Combine(root, entry)));
        }

        // The git folder of each writable root is protected as it is found, whatever a `.git` file names.
        foreach (var gitFolder in gitFolders.Concat(roots.Select(static root => Path.Combine(root, ".git")).Where(Directory.Exists)).Distinct(comparer))
        {
            protectedEntries.Add(Path.Combine(gitFolder, "hooks"));
            protectedEntries.Add(Path.Combine(gitFolder, "config"));
            protectedEntries.Add(Path.Combine(gitFolder, "config.worktree"));
        }

        var granted = readWrite.Concat(readOnly).ToList();
        var denied = profile.Deny.Select(path => Expand(path, home)).ToList();
        // A secret is denied where a grant would reach it; the session's own folders (a worktree under ~/.alta)
        // stay granted, since a granted child of a denied folder keeps its grant.
        denied.AddRange(secrets.Where(secret => granted.Any(grant => IsInside(grant, secret) && !comparer.Equals(grant, secret))));

        return new SandboxPolicy
        {
            ProfileName = profile.Name,
            WorkingDirectory = workingDirectory,
            ReadWrite = Distinct(readWrite),
            ReadOnly = Distinct(readOnly.Where(path => !readWrite.Any(grant => comparer.Equals(grant, path)))),
            Denied = Distinct(denied),
            Protected = Distinct(protectedEntries),
            Network = profile.Network,
            Hosts = profile.Hosts,
            LocalPorts = profile.LocalPorts,
            Environment = Environment(profile, host, sessionEnvironment, sandboxHome, caches, temp, userGitConfig, userPackages),
            Temp = temp,
            Home = sandboxHome,
            DataRoot = Full(host.DataRoot),
            McpServers = profile.McpServers,
            Outside = profile.Outside,
        };
    }

    /// <summary>Gets whether a variable name says that it holds a secret.</summary>
    /// <param name="name">The name.</param>
    /// <returns>True for a name such as <c>GITHUB_TOKEN</c> or <c>OPENAI_API_KEY</c>.</returns>
    public static bool IsSecretName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var upper = name.ToUpperInvariant();
        return SecretNameParts.Any(upper.Contains) || upper.EndsWith("_KEY", StringComparison.Ordinal);
    }

    // The variables that load code into a process as it starts (a startup hook of .NET, a profiler, a preloaded
    // library): a variable of the session, which a repository can set, never puts code into the worker this way.
    private static bool IsLoaderName(string name)
    {
        var upper = name.ToUpperInvariant();
        return upper is "DOTNET_STARTUP_HOOKS" or "DOTNET_ADDITIONAL_DEPS" or "DOTNET_SHARED_STORE" or "DOTNET_HOST_PATH" or "NODE_OPTIONS"
               || upper.StartsWith("CORECLR_", StringComparison.Ordinal) || upper.StartsWith("COR_", StringComparison.Ordinal)
               || upper.StartsWith("LD_", StringComparison.Ordinal) || upper.StartsWith("DYLD_", StringComparison.Ordinal);
    }

    private static IReadOnlyDictionary<string, string> Environment(ResolvedSandboxProfile profile, SandboxHost host,
        IEnumerable<KeyValuePair<string, string>> sessionEnvironment, string sandboxHome, string caches, string temp, string userGitConfig, string userPackages)
    {
        var environment = new Dictionary<string, string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var name in OperatingSystem.IsWindows() ? WindowsPassedVariables : UnixPassedVariables)
        {
            if (Lookup(host.Environment, name) is { } value) environment[name] = value;
        }

        // The session's own variables (its identity, CODEALTA_SESSION_ID...), but none named like a secret, nor one that
        // carries a secret of the application under another name (an identity that sets `LEAK = "${env:OPENAI_API_KEY}"`).
        var secretValues = host.Environment.Where(static pair => IsSecretName(pair.Key) && pair.Value.Length >= 6).Select(static pair => pair.Value).ToList();
        foreach (var (name, value) in sessionEnvironment)
        {
            if (!IsSecretName(name) && !IsLoaderName(name) && !secretValues.Any(secret => value.Contains(secret, StringComparison.Ordinal))) environment[name] = value;
        }

        // What the profile passes on, by name, secrets included: the user named them.
        foreach (var name in profile.Env)
        {
            if (Lookup(host.Environment, name) is { } value) environment[name] = value;
        }

        environment["HOME"] = sandboxHome;
        environment["TEMP"] = temp;
        environment["TMP"] = temp;
        environment["TMPDIR"] = temp;
        if (OperatingSystem.IsWindows())
        {
            environment["USERPROFILE"] = sandboxHome;
            environment["APPDATA"] = Path.Combine(sandboxHome, "AppData", "Roaming");
            environment["LOCALAPPDATA"] = Path.Combine(sandboxHome, "AppData", "Local");
        }
        else
        {
            environment["XDG_CONFIG_HOME"] = Path.Combine(sandboxHome, ".config");
            environment["XDG_CACHE_HOME"] = Path.Combine(caches, "xdg");
            environment["XDG_DATA_HOME"] = Path.Combine(sandboxHome, ".local", "share");
        }

        // Caches of the sandbox: what it downloads never lands where the user's own builds read it. NuGet reads
        // the user's packages as a fallback, read-only.
        environment["NUGET_PACKAGES"] = Path.Combine(caches, "nuget");
        if (Directory.Exists(userPackages)) environment["NUGET_FALLBACK_PACKAGES"] = userPackages;
        environment["npm_config_cache"] = Path.Combine(caches, "npm");
        environment["YARN_CACHE_FOLDER"] = Path.Combine(caches, "yarn");
        environment["PIP_CACHE_DIR"] = Path.Combine(caches, "pip");
        environment["DOTNET_CLI_HOME"] = sandboxHome;
        environment["DOTNET_NOLOGO"] = "1";
        environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1";
        // MSBuild and the compiler server of Roslyn put their pipes in `/tmp`, which a sandbox of macOS does not write:
        // the compiler then waits for its server before it compiles in the build. (The worker nodes of MSBuild need
        // `-m:1`, which the agent is told.)
        if (OperatingSystem.IsMacOS()) environment["UseSharedCompilation"] = "false";
        if (File.Exists(userGitConfig)) environment["GIT_CONFIG_GLOBAL"] = userGitConfig;
        environment["CODEALTA_SANDBOX"] = profile.Name;
        return environment;
    }

    // The git folders of a working tree: its own, or the common folder of a worktree first, then the folder of the
    // worktree in it. The `.git` file of a worktree is written by the sandbox: what it names is taken only when it is
    // the git folder of one of the repositories CodeAlta gives, and one of its worktrees; else nothing is.
    internal static IReadOnlyList<string> GitFolders(string workingDirectory, IReadOnlyList<string> repositories)
    {
        var dotGit = Path.Combine(workingDirectory, ".git");
        if (Directory.Exists(dotGit)) return [dotGit];
        if (!File.Exists(dotGit)) return [];
        try
        {
            var line = File.ReadLines(dotGit).FirstOrDefault(static text => text.StartsWith("gitdir:", StringComparison.OrdinalIgnoreCase));
            if (line is null) return [];
            var gitDir = Full(Path.GetFullPath(line["gitdir:".Length..].Trim(), workingDirectory));
            var commonFile = Path.Combine(gitDir, "commondir");
            if (!File.Exists(commonFile)) return [];
            var common = Full(Path.GetFullPath(File.ReadAllText(commonFile).Trim(), gitDir));
            var known = repositories.Select(static repository => Path.Combine(repository, ".git"))
                .Any(folder => PathComparer.Equals(folder, common) && Directory.Exists(folder));
            var worktrees = Path.Combine(common, "worktrees");
            return known && IsInside(worktrees, gitDir) && !PathComparer.Equals(worktrees, gitDir) ? [common, gitDir] : [];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return [];
        }
    }

    private static string? Lookup(IReadOnlyDictionary<string, string> environment, string name)
    {
        if (environment.TryGetValue(name, out var value)) return value;
        if (!OperatingSystem.IsWindows()) return null;
        foreach (var (key, candidate) in environment)
        {
            if (key.Equals(name, StringComparison.OrdinalIgnoreCase)) return candidate;
        }

        return null;
    }

    private static string Expand(string path, string home)
    {
        var trimmed = path.Trim();
        if (trimmed == "~") return home;
        if (trimmed.StartsWith("~/", StringComparison.Ordinal) || trimmed.StartsWith("~\\", StringComparison.Ordinal)) trimmed = Path.Combine(home, trimmed[2..]);
        return Full(trimmed);
    }

    private static string Full(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static string SafeName(string value)
    {
        if (value.All(static ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_') && value.Length <= 64) return value;
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..16].ToLowerInvariant();
    }

    private static IReadOnlyList<string> Distinct(IEnumerable<string> paths) => [.. paths.Distinct(PathComparer)];

    internal static StringComparer PathComparer => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    internal static bool IsInside(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative == "." || (!relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative));
    }
}
