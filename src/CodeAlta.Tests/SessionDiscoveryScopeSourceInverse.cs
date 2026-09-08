using System.Security.Cryptography;
using System.Text;

namespace CodeAlta.Tests;

/// <summary>Mandatory whole-original inverses of parent-anchored 4a57dfea; pure source transformations only.</summary>
internal static class SessionDiscoveryScopeSourceInverse
{
    internal const string Options = "CodeAlta.Orchestration/Hosting/CodeAltaHostOptions.cs";
    internal const string Host = "CodeAlta.Orchestration/Hosting/CodeAltaHost.cs";
    internal const string Runtime = "CodeAlta.Orchestration/Runtime/SessionRuntimeService.cs";
    internal const string Template = "CodeAlta.Orchestration/Runtime/AgentInstructionTemplateProvider.cs";
    internal const string Builder = "CodeAlta.Orchestration/Runtime/SystemPrompts/SystemPromptBuilder.cs";
    internal const string Profile = "CodeAlta.Tests/PluginAuthoringProfileSourceInverse.cs";
    internal const string Mcp = "CodeAlta.Tests/PluginMcpBackendSeparationSourceInverse.cs";
    internal const string Statistics = "CodeAlta.Tests/PluginStatisticsBackendSeparationSourceInverse.cs";
    internal const string Desktop = "CodeAlta.Desktop.Tests/CodeAlta.Desktop.Tests.csproj";

    internal static string RestoreProfileInput(string path, string source)
        => path is Host or Options ? Restore(path, source) : source;
    internal static string RestoreMcpInput(string path, string source)
        => path is Profile ? Restore(path, source) : source;
    internal static string RestoreStatisticsInput(string path, string source)
        => path is Mcp or Desktop ? Restore(path, source) : source;

    internal static string Restore(string path, string source)
    {
        source = OwnedSessionCommandSourceInverse.RestoreDiscoveryInput(path, source);
        source = SourceTestText.DecodeSource(new UTF8Encoding(false, true).GetBytes(source));
        foreach (var (before, after, count) in Edits(path))
        {
            var expected = SourceTestText.Canonicalize(after);
            Assert.IsTrue(expected.Length > 0, path);
            Assert.AreEqual(count, source.Split(expected, StringSplitOptions.None).Length - 1, path + ": " + expected);
            source = source.Replace(expected, SourceTestText.Canonicalize(before), StringComparison.Ordinal);
        }
        source = SourceTestText.DecodeSource(new UTF8Encoding(false, true).GetBytes(source));
        Assert.AreEqual(Originals.Single(item => item.Path == path).Hash, Hash(source), path);
        return source;
    }

    internal static string Hash(string source)
        => Convert.ToHexString(SHA256.HashData(new UTF8Encoding(false, true).GetBytes(source)));

    internal static IReadOnlyList<(string Path, string Hash)> Originals =>
    [
        (Options, "379C0863BFD8C2E3E5131594864CCD0D74D88E5059F1D2743371CA1FA4D1748F"),
        (Host, "FA4963811255559242C36ED631DFB239A5A7DC4D656CF8BB7BC9CAE564E13FA8"),
        (Runtime, "9B43D798FEFD04F6D9DC1E98CE3A31AC1971938A95D6EBDD01C091F6D05E174F"),
        (Template, "36882D2756FCE0AE2014CDC6FDAF78BF5765315627E1A99ECAD9BBBC646F5360"),
        (Builder, "4AAEEF1E59D7FAFC1F9DF74D851F7979EDEF9019D1572B43BAFE32B6B91ECECA"),
        (Profile, "029C83B967C416E09D167CB15FAD2921A1796274C1BB0D964981F5E90DCE58C9"),
        (Mcp, "5F8052D8E86F984ABE506A9736C50A61156C6B0D8CFA9F9F35EC9DE7F19473CB"),
        (Statistics, "7BEED8F819A7B7EA984144E977DE756A2F41C18C6D22C47CB3AC3AEDFC6DDEF1"),
        (Desktop, "2050FA50A6DC8163397B6B21683A5DBFE977F6B69DEDC5CBC0CD6003B1E9600D"),
    ];

    internal static IReadOnlyList<(string Before, string After, int Count)> Edits(string path) => path switch
    {
        Options =>
        [
            ("using CodeAlta.Agent;\n", "using CodeAlta.Agent;\nusing CodeAlta.Orchestration.Runtime;\n", 1),
            ("    public string? GlobalRoot { get; init; }\n", "    public string? GlobalRoot { get; init; }\n" + ScopeOption, 1),
        ],
        Host =>
        [
            (HostArgumentDocs, HostArgumentDocs + "    /// <exception cref=\"ArgumentException\">Scoped host roots are missing, not absolute, or the project is outside the instruction boundary.</exception>\n", 1),
            (HostValidation, HostValidation + "        options.DiscoveryScope?.ValidateHostRoots(options.GlobalRoot, options.CurrentProjectPath);\n", 1),
            ("            var instructionTemplateProvider = new AgentInstructionTemplateProvider(skillCatalog, catalogOptions);", "            var instructionTemplateProvider = new AgentInstructionTemplateProvider(skillCatalog, catalogOptions, contentLocator: null, configStore: null, discoveryScope: options.DiscoveryScope);", 1),
        ],
        Runtime =>
        [
            ("    public async Task<SessionViewDescriptor> CreateGlobalSessionAsync(", ScopePathException + "    public async Task<SessionViewDescriptor> CreateGlobalSessionAsync(", 2),
            ("    public async Task<SessionViewDescriptor> CreateProjectSessionAsync(", ScopePathException + "    public async Task<SessionViewDescriptor> CreateProjectSessionAsync(", 2),
            ("    public async Task<AgentSessionHandleId> EnsureCoordinatorSessionAsync(", ScopePathException + "    public async Task<AgentSessionHandleId> EnsureCoordinatorSessionAsync(", 1),
            ("    public async Task<AgentRunId> SendAsync(", ScopePathException + "    public async Task<AgentRunId> SendAsync(", 1),
            ("    /// <exception cref=\"ArgumentException\">Thrown when <paramref name=\"skillName\"/> is empty.</exception>", "    /// <exception cref=\"ArgumentException\">Thrown when <paramref name=\"skillName\"/> is empty or a supplied scoped working or project path is invalid or outside the instruction boundary.</exception>", 2),
            ("    private readonly AgentInstructionTemplateProvider _instructionTemplateProvider;\n", "    private readonly AgentInstructionTemplateProvider _instructionTemplateProvider;\n    private readonly SessionDiscoveryScope? _discoveryScope;\n", 1),
            ("        _instructionTemplateProvider = instructionTemplateProvider;\n", "        _instructionTemplateProvider = instructionTemplateProvider;\n        _discoveryScope = instructionTemplateProvider.DiscoveryScope;\n", 1),
            ("        ArgumentNullException.ThrowIfNull(options);\n\n        var now = DateTimeOffset.UtcNow;", "        ArgumentNullException.ThrowIfNull(options);\n        ValidateDiscoveryPaths(null, options);\n\n        var now = DateTimeOffset.UtcNow;", 1),
            ("        var requestedProjectId = project.Id;", "        ValidateDiscoveryPaths(null, options, project);\n\n        var requestedProjectId = project.Id;", 1),
            ("        ArgumentException.ThrowIfNullOrWhiteSpace(options.WorkingDirectory);\n", "        ArgumentException.ThrowIfNullOrWhiteSpace(options.WorkingDirectory);\n        ValidateDiscoveryPaths(session, options);\n", 1),
            ("        var project = await ResolveProjectAsync(session, cancellationToken).ConfigureAwait(false);\n        RuntimeSessionEntry? existing = null;", "        var project = await ResolveProjectAsync(session, cancellationToken).ConfigureAwait(false);\n        ValidateDiscoveryPaths(session, options, project);\n        RuntimeSessionEntry? existing = null;", 1),
            (SkillActivation, "        ValidateDiscoveryPaths(session, options);\n" + SkillActivation, 1),
            ("        var resolvedProjectRoots = new List<string>();", "        if (_discoveryScope is not null)\n        {\n            ValidateDiscoveryProjectRoot(project?.ProjectPath);\n            foreach (var root in projectRoots)\n            {\n                _discoveryScope.ValidateProjectPath(root, nameof(projectRoots));\n            }\n        }\n\n        var resolvedProjectRoots = new List<string>();", 1),
            (AmbientHome, ScopedHome, 3),
            ("    private string? ResolveKnownAgentPromptId(string? promptId, string? projectRoot)\n    {\n", "    private string? ResolveKnownAgentPromptId(string? promptId, string? projectRoot)\n    {\n        ValidateDiscoveryProjectRoot(projectRoot);\n", 1),
            ("    private AgentPromptUsageInfo? ResolveAgentPromptUsage(SystemPromptBundle? promptBundle, string? projectRoot)\n    {\n", "    private AgentPromptUsageInfo? ResolveAgentPromptUsage(SystemPromptBundle? promptBundle, string? projectRoot)\n    {\n        ValidateDiscoveryProjectRoot(projectRoot);\n", 1),
            ("    private SkillCatalogQuery BuildSkillCatalogQuery(", RuntimeValidation + "    private SkillCatalogQuery BuildSkillCatalogQuery(", 1),
        ],
        Template =>
        [
            ("    /// <returns>The file-backed instruction bundle selected for the session.</returns>\n", "    /// <returns>The file-backed instruction bundle selected for the session.</returns>\n" + ScopePathException, 2),
            ("    private SystemPromptBundle BuildPromptBundle(", "    /// <summary>Validates scoped paths before composing skills and file-backed prompt content.</summary>\n" + ScopePathException + "    private SystemPromptBundle BuildPromptBundle(", 1),
            ("    private readonly SystemPromptBuilder _promptBuilder;\n", "    private readonly SystemPromptBuilder _promptBuilder;\n\n    internal SessionDiscoveryScope? DiscoveryScope { get; }\n", 1),
            (TemplateConstructorEnd, TemplateConstructorEnd.Replace("    {\n", TemplateOverload + "    {\n", StringComparison.Ordinal), 1),
            ("        _skillCatalog = skillCatalog;\n", "        DiscoveryScope = discoveryScope;\n        _skillCatalog = skillCatalog;\n", 1),
            ("        var projectRoots = string.IsNullOrWhiteSpace(project?.ProjectPath)", "        ValidateDiscoveryPaths(session, project);\n        var projectRoots = string.IsNullOrWhiteSpace(project?.ProjectPath)", 1),
            (AmbientHome, "UserProfileRoot = DiscoveryScope?.UserProfileRoot ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),", 2),
            ("            AvailableSkillsMarkdown = BuildSkillsDeveloperInstructions(session, project),", "            DiscoveryScope = DiscoveryScope,\n            AvailableSkillsMarkdown = BuildSkillsDeveloperInstructions(session, project),", 1),
            ("    private string? BuildSkillsDeveloperInstructions(\n", TemplateValidation + "    private string? BuildSkillsDeveloperInstructions(\n", 1),
        ],
        Builder =>
        [
            ("        ArgumentNullException.ThrowIfNull(request.Session);\n", "        ArgumentNullException.ThrowIfNull(request.Session);\n        ValidateDiscoveryPaths(request);\n", 1),
            ("UserProfileRoot = request.UserProfileRoot,", "UserProfileRoot = request.DiscoveryScope?.UserProfileRoot ?? request.UserProfileRoot,", 2),
            ("projectRoot is null ? [] : [projectRoot]);", "projectRoot is null ? [] : [projectRoot], request.DiscoveryScope);", 1),
            ("    private static IReadOnlyList<string> EnumerateProjectInstructionFiles(string? workingDirectory, IReadOnlyList<string> projectRoots)\n    {\n", "    private static IReadOnlyList<string> EnumerateProjectInstructionFiles(string? workingDirectory, IReadOnlyList<string> projectRoots, SessionDiscoveryScope? discoveryScope)\n    {\n" + WalkValidation, 1),
            (OldWalk, NewWalk, 1),
            ("    private static string? BuildProjectContext(", BuilderValidation + "    private static string? BuildProjectContext(", 1),
            ("    public string? UserProfileRoot { get; init; }\n", "    public string? UserProfileRoot { get; init; }\n\n    /// <summary>Gets optional explicit home and lexical instruction ancestry limits; not a filesystem sandbox.</summary>\n    public SessionDiscoveryScope? DiscoveryScope { get; init; }\n", 1),
            ("    /// <exception cref=\"InvalidOperationException\">Thrown when required prompt content is missing or invalid.</exception>\n", "    /// <exception cref=\"InvalidOperationException\">Thrown when required prompt content is missing or invalid.</exception>\n    /// <exception cref=\"ArgumentException\">A supplied scoped working or project path is invalid or outside the instruction boundary.</exception>\n", 1),
        ],
        Profile => [(RestoreStart, RestoreStart + ProfileMap, 1)],
        Mcp => [(RestoreStart, RestoreStart + McpMap, 1)],
        Statistics => [(RestoreStart, RestoreStart + StatisticsMap, 1)],
        Desktop => [(StatisticsLink, StatisticsLink + NewLinks, 1)],
        _ => throw new AssertFailedException("No discovery scope inverse for " + path),
    };

    internal const string AmbientHome = "UserProfileRoot = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),";
    internal const string ScopedHome = "UserProfileRoot = _discoveryScope?.UserProfileRoot ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),";
    internal const string RestoreStart = "    internal static string Restore(string path, string source)\n    {\n";
    internal const string ProfileMap = "        source = SessionDiscoveryScopeSourceInverse.RestoreProfileInput(path, source);\n";
    internal const string McpMap = "        source = SessionDiscoveryScopeSourceInverse.RestoreMcpInput(path, source);\n";
    internal const string StatisticsMap = "        source = SessionDiscoveryScopeSourceInverse.RestoreStatisticsInput(path, source);\n";
    internal const string StatisticsLink = "    <Compile Include=\"../CodeAlta.Tests/PluginStatisticsBackendSeparationSourceInverse.cs\" Link=\"PluginStatisticsBackendSeparationSourceInverse.cs\" />\n";
    internal const string NewLinks = "    <Compile Include=\"../CodeAlta.Tests/SessionDiscoveryScopeSourceInverse.cs\" Link=\"SessionDiscoveryScopeSourceInverse.cs\" />\n    <Compile Include=\"../CodeAlta.Orchestration/Runtime/SessionDiscoveryScope.cs\" Link=\"SessionDiscoveryScope.cs\" />\n";
    internal const string HostValidation = "        if (!Enum.IsDefined(options.PluginAuthoringProfile)) throw new ArgumentOutOfRangeException(nameof(options.PluginAuthoringProfile));\n";
    internal const string HostArgumentDocs = "    /// <exception cref=\"ArgumentOutOfRangeException\">Thrown when the plugin authoring profile is invalid, before host acquisition.</exception>\n";
    internal const string SkillActivation = "        var project = await ResolveProjectAsync(session, cancellationToken).ConfigureAwait(false);\n        var query = BuildSkillCatalogQuery(project, options.ProjectRoots);";
    internal const string ScopeOption = "\n    /// <summary>Gets optional explicit home and lexical instruction ancestry limits.</summary>\n    /// <remarks>Requires explicit absolute global/project roots; does not isolate plugins, providers, authentication or filesystem links.</remarks>\n    public SessionDiscoveryScope? DiscoveryScope { get; init; }\n";
    internal const string ScopePathException = "    /// <exception cref=\"ArgumentException\">A supplied scoped working or project path is invalid or outside the instruction boundary.</exception>\n";
    internal const string TemplateConstructorEnd = "        CodeAltaConfigStore? configStore = null)\n    {\n";
    internal const string TemplateOverload = "        : this(skillCatalog, catalogOptions, contentLocator, configStore, discoveryScope: null)\n    {\n    }\n\n    /// <summary>Initializes instruction discovery with an optional explicit lexical scope.</summary>\n    /// <param name=\"skillCatalog\">Optional skill catalog.</param>\n    /// <param name=\"catalogOptions\">Optional catalog roots.</param>\n    /// <param name=\"contentLocator\">Optional prompt content locator.</param>\n    /// <param name=\"configStore\">Optional configuration store.</param>\n    /// <param name=\"discoveryScope\">Optional explicit discovery home and instruction ancestry boundary.</param>\n    /// <remarks>Working and project paths are validated when building instructions; invalid scoped paths throw <see cref=\"ArgumentException\"/> before prompt or skill discovery.</remarks>\n    public AgentInstructionTemplateProvider(\n        SkillCatalog? skillCatalog,\n        CatalogOptions? catalogOptions,\n        ISystemPromptContentLocator? contentLocator,\n        CodeAltaConfigStore? configStore,\n        SessionDiscoveryScope? discoveryScope)\n";

    internal const string RuntimeValidation = """
        private void ValidateDiscoveryPaths(SessionViewDescriptor? session, SessionExecutionOptions? options, ProjectDescriptor? project = null)
        {
            if (_discoveryScope is null)
            {
                return;
            }

            if (session?.WorkingDirectory is not null)
            {
                _discoveryScope.ValidateProjectPath(session.WorkingDirectory, nameof(session.WorkingDirectory));
            }

            if (options is not null)
            {
                _discoveryScope.ValidateProjectPath(options.WorkingDirectory, nameof(options.WorkingDirectory));
                foreach (var root in options.ProjectRoots)
                {
                    _discoveryScope.ValidateProjectPath(root, nameof(options.ProjectRoots));
                }
            }

            ValidateDiscoveryProjectRoot(project?.ProjectPath);
        }

        private void ValidateDiscoveryProjectRoot(string? projectRoot)
        {
            if (_discoveryScope is not null && projectRoot is not null)
            {
                _discoveryScope.ValidateProjectPath(projectRoot, nameof(projectRoot));
            }
        }

    """ + "\n";
    internal const string TemplateValidation = """
        private void ValidateDiscoveryPaths(SessionViewDescriptor session, ProjectDescriptor? project)
        {
            if (DiscoveryScope is null)
            {
                return;
            }

            if (session.WorkingDirectory is not null)
            {
                DiscoveryScope.ValidateProjectPath(session.WorkingDirectory, nameof(session.WorkingDirectory));
            }

            if (project?.ProjectPath is not null)
            {
                DiscoveryScope.ValidateProjectPath(project.ProjectPath, nameof(project.ProjectPath));
            }
        }

    """ + "\n";
    internal const string BuilderValidation = """
        private static void ValidateDiscoveryPaths(SystemPromptBuildRequest request)
        {
            if (request.DiscoveryScope is not { } scope)
            {
                return;
            }

            if (request.Session.WorkingDirectory is not null)
            {
                scope.ValidateProjectPath(request.Session.WorkingDirectory, nameof(request.Session.WorkingDirectory));
            }

            if (request.WorkingDirectory is not null)
            {
                scope.ValidateProjectPath(request.WorkingDirectory, nameof(request.WorkingDirectory));
            }

            if (request.Project?.ProjectPath is not null)
            {
                scope.ValidateProjectPath(request.Project.ProjectPath, nameof(request.Project.ProjectPath));
            }

            foreach (var root in request.ProjectRoots)
            {
                scope.ValidateProjectPath(root, nameof(request.ProjectRoots));
            }
        }

    """ + "\n";
    internal const string WalkValidation = """
            if (discoveryScope is not null)
            {
                if (workingDirectory is not null)
                {
                    discoveryScope.ValidateProjectPath(workingDirectory, nameof(workingDirectory));
                }

                foreach (var root in projectRoots)
                {
                    discoveryScope.ValidateProjectPath(root, nameof(projectRoots));
                }
            }

    """ + "\n";
    internal const string OldWalk = """
                var current = Path.GetFullPath(root);
                var stack = new Stack<string>();
                while (!string.IsNullOrWhiteSpace(current))
                {
                    stack.Push(current);
                    var parent = Directory.GetParent(current);
                    if (parent is null)
                    {
                        break;
                    }

                    current = parent.FullName;
                }

                while (stack.Count > 0)
                {
                    var directory = stack.Pop();
    """ + "\n";
    internal const string NewWalk = """
                foreach (var directory in SessionDiscoveryScope.GetInstructionAncestors(root, discoveryScope))
                {
    """ + "\n";
}
