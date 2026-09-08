using System.Runtime.CompilerServices;
using System.Text;
using Inverse = CodeAlta.Tests.SessionDiscoveryScopeSourceInverse;

namespace CodeAlta.Tests;

/// <summary>Named source reads and pure inverse checks only; never constructs a host or runtime.</summary>
[TestClass]
public sealed class SessionDiscoveryScopeSourceTests
{
    private const string ScopePath = "CodeAlta.Orchestration/Runtime/SessionDiscoveryScope.cs";

    // Complete content-read inventory for these nine methods. The called inverse entry points
    // transform supplied strings only: their transitive content-read inventory is empty.
    internal static IReadOnlyList<string> DirectContentPaths =>
    [
        Inverse.Options, Inverse.Host, Inverse.Runtime, Inverse.Template, Inverse.Builder,
        Inverse.Profile, Inverse.Mcp, Inverse.Statistics, Inverse.Desktop, ScopePath,
    ];
    internal static IReadOnlyList<string> TransitiveContentPaths => [];

    [TestMethod]
    public void Host_ValidatesScopeBeforeBootstrapAndPropagatesSameInstance()
    {
        var host = Read(Inverse.Host);
        Before(host, "options.DiscoveryScope?.ValidateHostRoots(options.GlobalRoot, options.CurrentProjectPath);", "Directory.CreateDirectory(globalRoot);");
        Before(host, "options.DiscoveryScope?.ValidateHostRoots(options.GlobalRoot, options.CurrentProjectPath);", "PluginRuntimeManager? pluginRuntime = null;");
        RequireOnce(host, "new AgentInstructionTemplateProvider(skillCatalog, catalogOptions, contentLocator: null, configStore: null, discoveryScope: options.DiscoveryScope)");
        RequireOnce(Read(Inverse.Options), Inverse.ScopeOption);
        RequireOnce(Read(ScopePath), "NormalizeAbsolutePath(globalRoot!, nameof(globalRoot));");
        RequireOnce(Read(ScopePath), "ValidateProjectPath(currentProjectPath!, nameof(currentProjectPath));");
    }

    [TestMethod]
    public void Constructors_PreserveExistingSignaturesAndAmbientFallback()
    {
        var runtime = Read(Inverse.Runtime);
        var restored = Inverse.Restore(Inverse.Runtime, runtime);
        var signature = runtime[runtime.IndexOf("    public SessionRuntimeService(", StringComparison.Ordinal)..];
        signature = signature[..signature.IndexOf("    {", StringComparison.Ordinal)];
        RequireOnce(restored, signature);
        RequireOnce(runtime, "_discoveryScope = instructionTemplateProvider.DiscoveryScope;");
        var template = Read(Inverse.Template);
        RequireOnce(template, "internal SessionDiscoveryScope? DiscoveryScope { get; }");
        RequireOnce(template, "CodeAltaConfigStore? configStore = null)\n        : this(skillCatalog, catalogOptions, contentLocator, configStore, discoveryScope: null)");
        RequireOnce(template, "DiscoveryScope = discoveryScope;");
        RequireOnce(template, Inverse.TemplateOverload);
        Assert.AreEqual(3, Count(runtime, Inverse.ScopedHome));
        Assert.AreEqual(2, Count(template, "UserProfileRoot = DiscoveryScope?.UserProfileRoot ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),"));
    }

    [TestMethod]
    public void Runtime_ValidatesBeforePersistenceAndDiscoveryAndReplacesThreeHomeReads()
    {
        var source = Read(Inverse.Runtime);
        Assert.AreEqual(6, Count(source, Inverse.ScopePathException));
        RequireOnce(source, Inverse.RuntimeValidation);
        Before(source, "ValidateDiscoveryPaths(null, options, project);", "var previousProject = await _projectCatalog.GetByPathAsync");
        RequireOnce(source, "ArgumentException.ThrowIfNullOrWhiteSpace(options.WorkingDirectory);\n        ValidateDiscoveryPaths(session, options);");
        RequireOnce(source, "var project = await ResolveProjectAsync(session, cancellationToken).ConfigureAwait(false);\n        ValidateDiscoveryPaths(session, options, project);\n        RuntimeSessionEntry? existing = null;");
        RequireOnce(source, "ValidateDiscoveryPaths(session, options);\n" + Inverse.SkillActivation);
        Before(source, "_discoveryScope.ValidateProjectPath(root, nameof(projectRoots));", "foreach (var projectRoot in projectRoots.Where");
        RequireOnce(source, "private string? ResolveKnownAgentPromptId(string? promptId, string? projectRoot)\n    {\n        ValidateDiscoveryProjectRoot(projectRoot);");
        RequireOnce(source, "private AgentPromptUsageInfo? ResolveAgentPromptUsage(SystemPromptBundle? promptBundle, string? projectRoot)\n    {\n        ValidateDiscoveryProjectRoot(projectRoot);");
        Assert.AreEqual(3, Count(source, Inverse.ScopedHome));
        Assert.AreEqual(0, Count(source, Inverse.AmbientHome));
        Inverse.Restore(Inverse.Runtime, source);
    }

    [TestMethod]
    public void TemplateProvider_ValidatesBeforeSkillsAndPropagatesScopeAndBothHomeReads()
    {
        var source = Read(Inverse.Template);
        Assert.AreEqual(3, Count(source, Inverse.ScopePathException));
        RequireOnce(source, Inverse.TemplateValidation);
        Before(source, "ValidateDiscoveryPaths(session, project);", "var projectRoots = string.IsNullOrWhiteSpace(project?.ProjectPath)");
        Before(source, "ValidateDiscoveryPaths(session, project);", "AvailableSkillsMarkdown = BuildSkillsDeveloperInstructions(session, project),");
        RequireOnce(source, "DiscoveryScope = DiscoveryScope,");
        Assert.AreEqual(2, Count(source, "UserProfileRoot = DiscoveryScope?.UserProfileRoot ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),"));
        Assert.AreEqual(0, Count(source, Inverse.AmbientHome));
        Inverse.Restore(Inverse.Template, source);
    }

    [TestMethod]
    public void PromptBuilder_ValidatesEveryRootBeforeDiscoveryOrExistenceProbes()
    {
        var source = Read(Inverse.Builder);
        RequireOnce(source, Inverse.BuilderValidation);
        Before(source, "ValidateDiscoveryPaths(request);", "var projectRoot = NormalizeOptionalRoot(");
        Before(source, "ValidateDiscoveryPaths(request);", "_contentLocator.GetRoots(");
        RequireOnce(source, Inverse.WalkValidation);
        Before(source, Inverse.WalkValidation, "!Directory.Exists(root)");
        Assert.AreEqual(2, Count(source, "UserProfileRoot = request.DiscoveryScope?.UserProfileRoot ?? request.UserProfileRoot,"));
        RequireOnce(source, "public SessionDiscoveryScope? DiscoveryScope { get; init; }");
        Inverse.Restore(Inverse.Builder, source);
    }

    [TestMethod]
    public void InstructionWalk_UsesProductionPureAncestryAndPreservesSelectionOrder()
    {
        var source = Read(Inverse.Builder);
        RequireOnce(source, Inverse.NewWalk);
        RequireOnce(source, "projectRoot is null ? [] : [projectRoot], request.DiscoveryScope);");
        var original = Inverse.Restore(Inverse.Builder, source);
        RequireOnce(original, Inverse.OldWalk);
        const string selection = "                    .Where(File.Exists)\n                    .Select(path => new FileInfo(path))\n                    .OrderByDescending(static file => file.Length)\n                    .ThenBy(static file => file.FullName, StringComparer.OrdinalIgnoreCase)\n                    .FirstOrDefault();";
        RequireOnce(source, selection);
        RequireOnce(original, selection);
        var scope = Read(ScopePath);
        foreach (var forbidden in new[] { "Directory.", "File.", "Environment.", "GetFolderPath", "GetEnvironmentVariable" })
            Assert.IsFalse(scope.Contains(forbidden, StringComparison.Ordinal), forbidden);
        RequireOnce(scope, "Path.GetDirectoryName(current)");
        RequireOnce(scope, "stack.ToArray()");
    }

    [TestMethod]
    public void Preservation_RestoresAllNineWholeOriginalsAcrossNewlineRepresentations()
    {
        Assert.AreEqual(9, Inverse.Originals.Count);
        Assert.AreEqual(10, DirectContentPaths.Count);
        Assert.AreEqual(10, DirectContentPaths.Distinct(StringComparer.Ordinal).Count());
        Assert.AreEqual(0, TransitiveContentPaths.Count);
        foreach (var (path, hash) in Inverse.Originals)
        {
            var canonical = Read(path);
            foreach (var representation in Representations(canonical))
            {
                var restored = Inverse.Restore(path, representation);
                Assert.AreEqual(hash, Inverse.Hash(restored), path);
                Assert.IsTrue(restored.EndsWith('\n'), path);
                Assert.IsFalse(restored.Contains('\r'), path);
            }
        }
    }

    [TestMethod]
    public void Preservation_RejectsMissingDuplicateAndUnrelatedSourceChanges()
    {
        foreach (var (path, _) in Inverse.Originals)
        {
            var source = Read(path);
            var edit = SourceTestText.Canonicalize(Inverse.Edits(path)[0].After);
            Assert.ThrowsExactly<AssertFailedException>(() => Inverse.Restore(path, source.Replace(edit, "", StringComparison.Ordinal)), path);
            Assert.ThrowsExactly<AssertFailedException>(() => Inverse.Restore(path, source + edit + "\n"), path);
            Assert.ThrowsExactly<AssertFailedException>(() => Inverse.Restore(path, "// unrelated change\n" + source), path);
            var original = Inverse.Restore(path, source);
            Assert.ThrowsExactly<AssertFailedException>(() => Inverse.Restore(path, original), path);
        }
        Assert.ThrowsExactly<AssertFailedException>(() => Inverse.Restore("unknown.cs", "// unknown\n"));
    }

    [TestMethod]
    public void Preservation_NewestPreMapsPreserveInheritedChains()
    {
        RequireOnce(Read(Inverse.Profile), Inverse.RestoreStart + Inverse.ProfileMap);
        RequireOnce(Read(Inverse.Mcp), Inverse.RestoreStart + Inverse.McpMap);
        RequireOnce(Read(Inverse.Statistics), Inverse.RestoreStart + Inverse.StatisticsMap);
        RequireOnce(Read(Inverse.Desktop), Inverse.StatisticsLink + Inverse.NewLinks);
        foreach (var (path, _) in Inverse.Originals)
        {
            var source = Read(path);
            var restored = Inverse.Restore(path, source);
            Assert.AreEqual(path is Inverse.Host or Inverse.Options ? restored : source, Inverse.RestoreProfileInput(path, source), path);
            Assert.AreEqual(path is Inverse.Profile ? restored : source, Inverse.RestoreMcpInput(path, source), path);
            Assert.AreEqual(path is Inverse.Mcp or Inverse.Desktop ? restored : source, Inverse.RestoreStatisticsInput(path, source), path);
        }
        foreach (var path in new[] { Inverse.Host, Inverse.Options })
            PluginAuthoringProfileSourceInverse.Restore(path, Read(path));
        PluginMcpBackendSeparationSourceInverse.Restore(Inverse.Profile, Read(Inverse.Profile));
        PluginGitHubBackendSeparationSourceInverse.Restore(Inverse.Profile, Read(Inverse.Profile));
        foreach (var path in new[] { Inverse.Mcp, Inverse.Desktop })
            PluginStatisticsBackendSeparationSourceInverse.Restore(path, Read(path));
        const string untouched = "not a source document";
        Assert.AreSame(untouched, Inverse.RestoreProfileInput("unmapped", untouched));
        Assert.AreSame(untouched, Inverse.RestoreMcpInput("unmapped", untouched));
        Assert.AreSame(untouched, Inverse.RestoreStatisticsInput("unmapped", untouched));
    }

    private static IEnumerable<string> Representations(string source)
    {
        yield return source;
        yield return source.Replace("\n", "\r\n", StringComparison.Ordinal);
        var mixed = new StringBuilder();
        var crlf = false;
        foreach (var character in source)
        {
            if (character == '\n' && (crlf = !crlf)) mixed.Append('\r');
            mixed.Append(character);
        }
        yield return mixed.ToString();
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
    {
        Assert.IsTrue(DirectContentPaths.Contains(path, StringComparer.Ordinal), path);
        return SourceTestText.DecodeSource(File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(caller)!, "..", path)));
    }
}
