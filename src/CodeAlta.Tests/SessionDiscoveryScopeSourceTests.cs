using System.Runtime.CompilerServices;

namespace CodeAlta.Tests;

/// <summary>Named current-source scope guards only; never constructs a host or runtime or probes discovery roots.</summary>
[TestClass]
public sealed class SessionDiscoveryScopeSourceTests
{
    private const string ScopePath = "CodeAlta.Orchestration/Runtime/SessionDiscoveryScope.cs";
    private const string Options = "CodeAlta.Orchestration/Hosting/CodeAltaHostOptions.cs";
    private const string Host = "CodeAlta.Orchestration/Hosting/CodeAltaHost.cs";
    private const string Runtime = "CodeAlta.Orchestration/Runtime/SessionRuntimeService.cs";
    private const string Template = "CodeAlta.Orchestration/Runtime/AgentInstructionTemplateProvider.cs";
    private const string Builder = "CodeAlta.Orchestration/Runtime/SystemPrompts/SystemPromptBuilder.cs";
    private const string AmbientHome = "UserProfileRoot = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),";
    private const string ScopedHome = "UserProfileRoot = _discoveryScope?.UserProfileRoot ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),";
    private const string ScopePathException = "/// <exception cref=\"ArgumentException\">A supplied scoped working or project path is invalid or outside the instruction boundary.</exception>";

    [TestMethod]
    public void Host_ValidatesScopeBeforeBootstrapAndPropagatesSameInstance()
    {
        var host = Read(Host);
        Before(host, "options.DiscoveryScope?.ValidateHostRoots(options.GlobalRoot, options.CurrentProjectPath);", "Directory.CreateDirectory(globalRoot);");
        Before(host, "options.DiscoveryScope?.ValidateHostRoots(options.GlobalRoot, options.CurrentProjectPath);", "PluginRuntimeManager? pluginRuntime = null;");
        RequireOnce(host, "new AgentInstructionTemplateProvider(skillCatalog, catalogOptions, contentLocator: null, configStore: null, discoveryScope: options.DiscoveryScope)");
        RequireOnce(Read(Options), "public SessionDiscoveryScope? DiscoveryScope { get; init; }");
        RequireOnce(Read(ScopePath), "NormalizeAbsolutePath(globalRoot!, nameof(globalRoot));");
        RequireOnce(Read(ScopePath), "ValidateProjectPath(currentProjectPath!, nameof(currentProjectPath));");
    }

    [TestMethod]
    [Ignore("Code-shape preservation check; disabled in favor of behavioral coverage.")]
    public void Constructors_PreserveExistingSignaturesAndAmbientFallback()
    {
        var runtime = Read(Runtime);
        RequireOnce(runtime, "    public SessionRuntimeService(");
        RequireOnce(runtime, "SkillCatalog? skillCatalog = null)");
        RequireOnce(runtime, "_discoveryScope = instructionTemplateProvider.DiscoveryScope;");
        var template = Read(Template);
        RequireOnce(template, "internal SessionDiscoveryScope? DiscoveryScope { get; }");
        RequireOnce(template, "CodeAltaConfigStore? configStore = null)\n        : this(skillCatalog, catalogOptions, contentLocator, configStore, discoveryScope: null)");
        RequireOnce(template, "DiscoveryScope = discoveryScope;");
        RequireOnce(template, "ISystemPromptContentLocator? contentLocator,\n        CodeAltaConfigStore? configStore,\n        SessionDiscoveryScope? discoveryScope)");
        Assert.AreEqual(3, Count(runtime, ScopedHome));
        Assert.AreEqual(2, Count(template, "UserProfileRoot = DiscoveryScope?.UserProfileRoot ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),"));
    }

    [TestMethod]
    [Ignore("Code-shape preservation check; disabled in favor of behavioral coverage.")]
    public void Runtime_ValidatesBeforePersistenceAndDiscoveryAndReplacesThreeHomeReads()
    {
        var source = Read(Runtime);
        Assert.AreEqual(6, Count(source, ScopePathException));
        var start = source.IndexOf("private void ValidateDiscoveryPaths(", StringComparison.Ordinal);
        var end = source.IndexOf("private SkillCatalogQuery BuildSkillCatalogQuery(", StringComparison.Ordinal);
        Assert.IsTrue(start >= 0 && end > start);
        var validationBody = source[start..end];
        StringAssert.Contains(validationBody, "if (_discoveryScope is null)\n        {\n            return;");
        foreach (var validation in new[]
        {
            "_discoveryScope.ValidateProjectPath(session.WorkingDirectory, nameof(session.WorkingDirectory));",
            "_discoveryScope.ValidateProjectPath(options.WorkingDirectory, nameof(options.WorkingDirectory));",
            "foreach (var root in options.ProjectRoots)\n            {\n                _discoveryScope.ValidateProjectPath(root, nameof(options.ProjectRoots));",
            "ValidateDiscoveryProjectRoot(project?.ProjectPath);",
            "_discoveryScope.ValidateProjectPath(projectRoot, nameof(projectRoot));",
        }) RequireOnce(validationBody, validation);
        Before(source, "ValidateDiscoveryPaths(null, options, project);", "var previousProject = await _projectCatalog.GetByPathAsync");
        Assert.AreEqual(2, Count(source, "ArgumentException.ThrowIfNullOrWhiteSpace(options.WorkingDirectory);\n        ValidateDiscoveryPaths(session, options);"));
        StringAssert.Contains(source, "var project = await ResolveProjectAsync(session, cancellationToken).ConfigureAwait(false);\n        ValidateDiscoveryPaths(session, options, project);");
        RequireOnce(source, "ValidateDiscoveryPaths(session, options);\n        var project = await ResolveProjectAsync(session, cancellationToken).ConfigureAwait(false);\n        var query = BuildSkillCatalogQuery(project, options.ProjectRoots);");
        Before(source, "_discoveryScope.ValidateProjectPath(root, nameof(projectRoots));", "foreach (var projectRoot in projectRoots.Where");
        RequireOnce(source, "private string? ResolveKnownAgentPromptId(string? promptId, string? projectRoot)\n    {\n        ValidateDiscoveryProjectRoot(projectRoot);");
        RequireOnce(source, "private AgentPromptUsageInfo? ResolveAgentPromptUsage(SystemPromptBundle? promptBundle, string? projectRoot)\n    {\n        ValidateDiscoveryProjectRoot(projectRoot);");
        Assert.AreEqual(3, Count(source, ScopedHome));
        Assert.AreEqual(0, Count(source, AmbientHome));
    }

    [TestMethod]
    public void TemplateProvider_ValidatesBeforeSkillsAndPropagatesScopeAndBothHomeReads()
    {
        var source = Read(Template);
        Assert.AreEqual(3, Count(source, ScopePathException));
        RequireOnce(source, "if (DiscoveryScope is null)\n        {\n            return;");
        RequireOnce(source, "DiscoveryScope.ValidateProjectPath(session.WorkingDirectory, nameof(session.WorkingDirectory));");
        RequireOnce(source, "DiscoveryScope.ValidateProjectPath(project.ProjectPath, nameof(project.ProjectPath));");
        Before(source, "ValidateDiscoveryPaths(session, project);", "var projectRoots = string.IsNullOrWhiteSpace(project?.ProjectPath)");
        Before(source, "ValidateDiscoveryPaths(session, project);", "AvailableSkillsMarkdown = BuildSkillsDeveloperInstructions(session, project),");
        RequireOnce(source, "DiscoveryScope = DiscoveryScope,");
        Assert.AreEqual(2, Count(source, "UserProfileRoot = DiscoveryScope?.UserProfileRoot ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),"));
        Assert.AreEqual(0, Count(source, AmbientHome));
    }

    [TestMethod]
    public void PromptBuilder_ValidatesEveryRootBeforeDiscoveryOrExistenceProbes()
    {
        var source = Read(Builder);
        RequireOnce(source, "if (request.DiscoveryScope is not { } scope)\n        {\n            return;");
        foreach (var path in new[] { "request.Session.WorkingDirectory", "request.WorkingDirectory", "request.Project.ProjectPath" })
            RequireOnce(source, $"scope.ValidateProjectPath({path}, nameof({path}));");
        RequireOnce(source, "foreach (var root in request.ProjectRoots)\n        {\n            scope.ValidateProjectPath(root, nameof(request.ProjectRoots));");
        Before(source, "ValidateDiscoveryPaths(request);", "var projectRoot = NormalizeOptionalRoot(");
        Before(source, "ValidateDiscoveryPaths(request);", "_contentLocator.GetRoots(");
        Before(source, "discoveryScope.ValidateProjectPath(workingDirectory, nameof(workingDirectory));", "!Directory.Exists(root)");
        Before(source, "discoveryScope.ValidateProjectPath(root, nameof(projectRoots));", "!Directory.Exists(root)");
        Assert.AreEqual(2, Count(source, "UserProfileRoot = request.DiscoveryScope?.UserProfileRoot ?? request.UserProfileRoot,"));
        RequireOnce(source, "public SessionDiscoveryScope? DiscoveryScope { get; init; }");
    }

    [TestMethod]
    public void InstructionWalk_UsesProductionPureAncestryAndPreservesSelectionOrder()
    {
        var source = Read(Builder);
        RequireOnce(source, "foreach (var directory in SessionDiscoveryScope.GetInstructionAncestors(root, discoveryScope))");
        RequireOnce(source, "projectRoot is null ? [] : [projectRoot], request.DiscoveryScope);");
        const string selection = "                    .Where(File.Exists)\n                    .Select(path => new FileInfo(path))\n                    .OrderByDescending(static file => file.Length)\n                    .ThenBy(static file => file.FullName, StringComparer.OrdinalIgnoreCase)\n                    .FirstOrDefault();";
        RequireOnce(source, selection);
        var scope = Read(ScopePath);
        foreach (var forbidden in new[] { "Directory.", "File.", "Environment.", "GetFolderPath", "GetEnvironmentVariable" })
            Assert.IsFalse(scope.Contains(forbidden, StringComparison.Ordinal), forbidden);
        RequireOnce(scope, "Path.GetDirectoryName(current)");
        RequireOnce(scope, "stack.ToArray()");
    }

    private static int Count(string source, string fragment)
        => source.Split(SourceTestText.Canonicalize(fragment), StringSplitOptions.None).Length - 1;
    private static void RequireOnce(string source, string fragment) => Assert.AreEqual(1, Count(source, fragment), fragment);
    private static void Before(string source, string first, string second)
    {
        RequireOnce(source, first);
        RequireOnce(source, second);
        Assert.IsTrue(source.IndexOf(first, StringComparison.Ordinal) < source.IndexOf(second, StringComparison.Ordinal), first);
    }
    private static string Read(string path, [CallerFilePath] string caller = "")
        => SourceTestText.DecodeSource(File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(caller)!, "..", path)));
}
