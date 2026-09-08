using System.Security.Cryptography;
using System.Text;

namespace CodeAlta.Tests;

/// <summary>Fixed-checkout source wiring and mandatory whole-source restoration; not load/runtime qualification.</summary>
[TestClass]
public sealed class PluginAuthoringProfileSourceTests
{
    [TestMethod]
    public void Routes_PropagateExplicitProfileThroughBothTuiEntries()
    {
        RequireOnce(Read("CodeAlta.Tui/Program.cs"), "                    AuthoringProfile = PluginAuthoringProfile.Terminal,\n");
        RequireOnce(Read("CodeAlta.Tui/App/CodeAltaOwnedServices.cs"), "                        PluginAuthoringProfile = PluginAuthoringProfile.Terminal,\n");
        var host = Read("CodeAlta.Orchestration/Hosting/CodeAltaHost.cs");
        RequireOnce(host, "                            AuthoringProfile = options.PluginAuthoringProfile,\n");
        RequireOnce(host, "            if (options.StartPlugins && options.PrestartedPluginRuntime is null)\n");
        RequireOnce(host, "            ownsPluginRuntime = options.PrestartedPluginRuntime is null;\n");
        RequireOnce(Read("CodeAlta.Orchestration/Hosting/CodeAltaHostOptions.cs"), "public PluginAuthoringProfile PluginAuthoringProfile { get; init; } = PluginAuthoringProfile.Neutral;");
        Before(host, "Enum.IsDefined(options.PluginAuthoringProfile)", "var globalRoot =");
    }

    [TestMethod]
    public void Routes_UseOneProfileForGenerationAndTheExistingLoader()
    {
        var manager = Read("CodeAlta.Plugins/PluginRuntimeManager.cs");
        RequireOnce(manager, "                AuthoringProfile = options.AuthoringProfile,\n");
        RequireOnce(manager, "            var loader = new PluginAssemblyLoader(options.AuthoringProfile);\n");
        RequireOnce(manager, "new PluginAssemblyLoader(");
        RequireOnce(manager, "public PluginAuthoringProfile AuthoringProfile { get; init; } = PluginAuthoringProfile.Neutral;");
        Before(manager, "PluginAuthoringPolicy.Validate(options.AuthoringProfile);", "var configStore =");
        var generator = Read("CodeAlta.Plugins/PluginRootBuildFiles.cs");
        Before(generator, "PluginAuthoringPolicy.ValidateBuildOptions(options);", "Directory.CreateDirectory(root.RootPath);");
        RequireOnce(generator, "        => CreateFilesCore(options, PluginRuntimePathService.NormalizeDirectory(options.CodeAltaExeFolder));\n");
        var core = generator[generator.IndexOf("    internal static IReadOnlyList<GeneratedFile> CreateFilesCore(", StringComparison.Ordinal)..generator.IndexOf("    private static async ValueTask<FileStream> AcquireRootLockAsync", StringComparison.Ordinal)];
        Assert.IsFalse(core.Contains("Environment.", StringComparison.Ordinal));
        Assert.IsFalse(core.Contains("File.Exists", StringComparison.Ordinal));
        Assert.IsFalse(core.Contains("NormalizeDirectory", StringComparison.Ordinal));
        RequireOnce(manager, "            HostApiVersion = PluginAuthoringPolicy.HostApiVersion,\n");
    }

    [TestMethod]
    public void Generation_FailedRootsCannotReachBuildOrCachedLoad()
    {
        var manager = Read("CodeAlta.Plugins/PluginRuntimeManager.cs");
        RequireOnce(manager, "                if (generation.Succeeded) successfulRoots.Add(root.RootPath);\n");
        RequireOnce(manager, "            var admittedBuildRequests = PluginAuthoringPolicy.FilterBuildRequests(plan.BuildRequests, successfulRoots);\n");
        RequireOnce(manager, "buildResults.AddRange(await scheduler.BuildAsync(admittedBuildRequests, token).ConfigureAwait(false));");
        Assert.IsFalse(manager.Contains("scheduler.BuildAsync(plan.BuildRequests", StringComparison.Ordinal));
        RequireOnce(manager, "                diagnostics.AddRange(generation.Diagnostics);\n");
        RequireOnce(manager, "                scheduler.ProgressChanged += OnProgress;\n");
        RequireOnce(manager, "                    scheduler.ProgressChanged -= OnProgress;\n");
        RequireOnce(Read("CodeAlta.Plugins/PluginAuthoringProfile.cs"), "var roots = new HashSet<string>(successfulRoots, StringComparer.OrdinalIgnoreCase);");
    }

    [TestMethod]
    public void Cache_UsesStampedGeneratedFilesBeforeBothFastPaths()
    {
        var manager = Read("CodeAlta.Plugins/PluginRuntimeManager.cs");
        Before(manager, "GenerateAsync(root, generationOptions, token)", "new PluginBuildManifestStore(");
        var build = Read("CodeAlta.Plugins/PluginBuildService.cs");
        RequireOnce(build, "var currentGeneratedHashes = PluginRootBuildFileGenerator.ComputeGeneratedFileHashes(package.Root.RootPath);");
        RequireOnce(build, "if (!DictionaryEquals(manifest.GeneratedFileHashes, currentGeneratedHashes))");
        RequireOnce(build, "GeneratedFileHashes = PluginRootBuildFileGenerator.ComputeGeneratedFileHashes(result.Package.Root.RootPath),");
        RequireOnce(build, "var manifestResult = await _manifestStore.TryGetUpToDateManifestAsync(package, cancellationToken).ConfigureAwait(false);", 2);
        RequireOnce(Read("CodeAlta.Plugins/PluginRootBuildFiles.cs"), "SHA256.HashData(File.ReadAllBytes(path))");
    }

    [TestMethod]
    public void Loader_PreflightsMetadataBeforeDiscoveryWithoutActivation()
    {
        var loader = Read("CodeAlta.Plugins/PluginAssemblyLoading.cs");
        RequireOnce(loader, "            loadContext.ValidateMainAssembly(outputAssemblyPath);\n");
        Before(loader, "loadContext.ValidateMainAssembly(outputAssemblyPath);", "var assembly = loadContext.LoadFromAssemblyPath(outputAssemblyPath);");
        RequireOnce(loader, "        catch (Exception ex) when (ex is FileLoadException or FileNotFoundException or BadImageFormatException)\n");
        RequireOnce(loader, "            loadContext?.Unload();\n");
        var admission = Read("CodeAlta.Plugins/PluginAssemblyReferenceAdmission.cs");
        RequireOnce(admission, "using var pe = new PEReader(stream);");
        RequireOnce(admission, "var reader = pe.GetMetadataReader();");
        RequireOnce(admission, "var definition = reader.GetAssemblyDefinition();");
        RequireOnce(admission, "ValidateClosure(profile, mainAssemblyPath, ReadMetadata, ResolveForAdmission,");
        RequireOnce(admission, "if (PluginAuthoringPolicy.ClassifyAssembly(profile, main.Name, shared) != PluginAssemblyBinding.PrivateOrDefault)");
        Assert.IsFalse(admission.Contains(".GetTypes(", StringComparison.Ordinal));
        Assert.IsFalse(admission.Contains("Activator.", StringComparison.Ordinal));
        Assert.IsFalse(admission.Contains(".LoadFromAssembly", StringComparison.Ordinal));
        RequireOnce(Read("CodeAlta.Plugins/PluginRuntimeLifecycle.cs"), "Activator.CreateInstance(discoveredType.Type)");
        RequireOnce(Read("CodeAlta.Plugins/PluginTypeDiscoveryService.cs"), "assembly.GetTypes()");
    }

    [TestMethod]
    public void Loading_ReservesTerminalIdentityWithoutPrivateFallback()
    {
        var loader = Read("CodeAlta.Plugins/PluginAssemblyLoading.cs");
        RequireOnce(loader, "                ?? AssemblyLoadContext.Default.LoadFromAssemblyName(assemblyName);\n");
        RequireOnce(loader, "        => PluginAssemblyReferenceAdmission.Inspect(mainAssemblyPath, _authoringProfile, _hostSharedAssemblyNames, _resolver.ResolveAssemblyToPath);\n");
        RequireOnce(loader, "if (binding == PluginAssemblyBinding.Forbidden)");
        var admission = Read("CodeAlta.Plugins/PluginAssemblyReferenceAdmission.cs");
        RequireOnce(admission, "if (!sourceIsDefault && !hostShared)");
        RequireOnce(admission, "var visited = new HashSet<PluginAssemblyReferenceResolution> { new(entryPath, false) };");
        RequireOnce(admission, "VisitReferences(dependency, resolved.IsDefault);");
        RequireOnce(admission, "var coreLibraryPath = typeof(object).Assembly.Location;");
        RequireOnce(admission, "path => string.Equals(path, coreLibraryPath, StringComparison.Ordinal)");
        Before(admission, "if (!Path.IsPathFullyQualified(directory))", "if (File.Exists(path))");
        Assert.IsFalse(admission.Contains("StartsWith(\"System.", StringComparison.Ordinal));
        var policy = Read("CodeAlta.Plugins/PluginAuthoringProfile.cs");
        Assert.IsFalse(policy.Contains("IsHeadless", StringComparison.Ordinal));
        Assert.IsFalse(policy.Contains("HasInteractiveUi", StringComparison.Ordinal));
        RequireOnce(Read("CodeAlta.Tui/App/PluginFrontendBridge.cs"), "SupportsTerminalVisuals = true,");
    }

    [TestMethod]
    public void Preservation_RestoresCompleteOriginalsAcrossNewlineVariants()
    {
        Assert.AreEqual(12, PluginAuthoringProfileSourceInverse.Originals.Count);
        foreach (var (path, hash) in PluginAuthoringProfileSourceInverse.Originals)
        {
            foreach (var representation in PluginNeutralContractSourceInverse.Representations(Read(path)))
            {
                var restored = PluginAuthoringProfileSourceInverse.Restore(path, representation);
                Assert.AreEqual(hash, Convert.ToHexString(SHA256.HashData(new UTF8Encoding(false, true).GetBytes(restored))), path);
            }
        }
    }

    [TestMethod]
    public void Preservation_ComposesEveryFrozenHistoricalChain()
    {
        // Actual current fixtures execute their mandatory inverses; no captured substitute fixture is run.
        var neutral = new PluginNeutralContractSourceTests();
        neutral.Dependencies_RemoveOnlyUnusedContractPackages();
        neutral.Preservation_RestoresCompleteOriginalsAcrossNewlineVariants();
        neutral.Preservation_ComposesEveryFrozenHistoricalChain();
        new PluginFeedbackExtractionSourceTests().Manager_UsesNeutralPortAndPreservesStartupOrder();
        new PluginFeedbackExtractionSourceTests().TuiAndHost_ForwardFeedbackWithoutChangingOwnership();
        // Desktop.Tests separately selects actual history -> workspace and the desktop assembly boundary.
    }

    private static void RequireOnce(string source, string text, int count = 1)
        => Assert.AreEqual(count, source.Split(text, StringSplitOptions.None).Length - 1, text);

    private static void Before(string source, string first, string second)
    {
        var firstIndex = source.IndexOf(first, StringComparison.Ordinal);
        var secondIndex = source.IndexOf(second, StringComparison.Ordinal);
        Assert.IsTrue(firstIndex >= 0 && secondIndex > firstIndex, first + " before " + second);
    }

    private static string Read(string path)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CodeAlta.slnx"))) directory = directory.Parent;
        Assert.IsNotNull(directory);
        return SourceTestText.DecodeSource(File.ReadAllBytes(Path.Combine(directory.FullName, path)));
    }
}
