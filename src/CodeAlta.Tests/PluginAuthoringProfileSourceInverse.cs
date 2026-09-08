using System.Security.Cryptography;
using System.Text;

namespace CodeAlta.Tests;

/// <summary>Mandatory context-bearing whole-source inverses of parent-audited 85c25ff2; terminal-free Desktop link.</summary>
internal static class PluginAuthoringProfileSourceInverse
{
    internal static string Restore(string path, string source)
    {
        source = SessionDiscoveryScopeSourceInverse.RestoreProfileInput(path, source);
        source = SourceTestText.DecodeSource(new UTF8Encoding(false, true).GetBytes(source));
        source = PluginGitHubBackendSeparationSourceInverse.RestoreProfileInput(path, source);
        source = PluginMcpBackendSeparationSourceInverse.RestoreProfileInput(path, source);
        foreach (var (before, after) in Edits(path))
        {
            var expected = SourceTestText.Canonicalize(after);
            Assert.IsTrue(expected.Length > 0, path);
            Assert.AreEqual(1, source.Split(expected, StringSplitOptions.None).Length - 1, path);
            source = source.Replace(expected, SourceTestText.Canonicalize(before), StringComparison.Ordinal);
        }
        source = SourceTestText.DecodeSource(new UTF8Encoding(false, true).GetBytes(source));
        Assert.AreEqual(Originals.Single(item => item.Path == path).Hash,
            Convert.ToHexString(SHA256.HashData(new UTF8Encoding(false, true).GetBytes(source))), path);
        return source;
    }

    // Unchanged catalog inputs pass through; every mapped input requires NEW text and its complete old hash.
    internal static string RestoreUiContentInput(string path, string source) => path is
        "CodeAlta.Plugins/PluginRootBuildFiles.cs" or "CodeAlta.Plugins/PluginAssemblyLoading.cs" or
        "CodeAlta.Plugins/PluginRuntimeManager.cs" or "CodeAlta.Orchestration/Hosting/CodeAltaHostOptions.cs" or
        "CodeAlta.Orchestration/Hosting/CodeAltaHost.cs" or "CodeAlta.Tui/Program.cs" or
        "CodeAlta.Tui/App/CodeAltaOwnedServices.cs" or "CodeAlta.Tests/PluginFeedbackExtractionSourceTests.cs"
        ? Restore(path, source) : source;

    internal static string RestoreFeedbackInput(string path, string source) => path is
        "CodeAlta.Plugins/PluginRuntimeManager.cs" or "CodeAlta.Orchestration/Hosting/CodeAltaHostOptions.cs" or
        "CodeAlta.Orchestration/Hosting/CodeAltaHost.cs" or "CodeAlta.Tui/Program.cs" or "CodeAlta.Tui/App/CodeAltaOwnedServices.cs"
        ? Restore(path, source) : source;

    internal static string RestoreNeutralInput(string path, string source) => path is
        "CodeAlta.Tests/PluginUiContentExtractionSourceTests.cs" or "CodeAlta.Desktop.Tests/CodeAlta.Desktop.Tests.csproj" or
        "CodeAlta.Desktop.Tests/DesktopWorkspaceSourceTests.cs"
        ? Restore(path, source) : source;

    internal static IReadOnlyList<(string Path, string Hash)> Originals =>
    [
        ("CodeAlta.Plugins/PluginRootBuildFiles.cs", "2E7F8F188A77A18B8B7D01133C39C71DC44859B4B79192CD7CF43F596956E985"),
        ("CodeAlta.Plugins/PluginAssemblyLoading.cs", "35E89CB09BB18CCB69D3639613059C00BA35A8DB3F4AEBDAC197BDDE62C11381"),
        ("CodeAlta.Plugins/PluginRuntimeManager.cs", "8BED175D3E2359CE8C4F21F07A0073FD145A20542F121A6B75580265A596102C"),
        ("CodeAlta.Orchestration/Hosting/CodeAltaHostOptions.cs", "B0C8559B4597DE7212C618779AA8CB564E8BA336C1BCC848403C02609103D7DD"),
        ("CodeAlta.Orchestration/Hosting/CodeAltaHost.cs", "7F98979EC226FB9EEC7A9426340703E21301B5A04C82279B2C8C027EC6BA5078"),
        ("CodeAlta.Tui/Program.cs", "2F3AA23636A7DC1BA69721DEFCF2918AAFA5FF163D93EFE17E1093AD37286753"),
        ("CodeAlta.Tui/App/CodeAltaOwnedServices.cs", "91BBD3719CE05B6326F386E11B9B3B0D7D492B95DFBDCE6E4BF9549BC07DCC6E"),
        ("CodeAlta.Tests/PluginUiContentExtractionSourceTests.cs", "A920E84DC4E51B07B385818865BC16F45A79745927E83E0CDC93B90BA50E5C4F"),
        ("CodeAlta.Tests/PluginFeedbackExtractionSourceTests.cs", "CC9D9BDB7B56C1B7BBC1195C951E9943416DAE20B55EE7D80C137A36ECB3DE25"),
        ("CodeAlta.Tests/PluginNeutralContractSourceInverse.cs", "7B490CA44DE9B6DFEA2E90528A7C2FE7609E5484F494B45337522285A0B93CF6"),
        ("CodeAlta.Desktop.Tests/CodeAlta.Desktop.Tests.csproj", "67358F1655407CD2C72173353CCD1C5ABF7C725AF0BABFE588769296D0ABF589"),
        ("CodeAlta.Desktop.Tests/DesktopWorkspaceSourceTests.cs", "A54806174B0CB00B5F4B576AFBD3366D39A22EA9C81CA6464C2B2B315F7C5471"),
    ];

    private static IReadOnlyList<(string Before, string After)> Edits(string path) => path switch
    {
        "CodeAlta.Plugins/PluginRootBuildFiles.cs" =>
        [
            ("public sealed record PluginRootBuildFileOptions\n{\n", "public sealed record PluginRootBuildFileOptions\n{\n" + BuildProfileOption),
            (OldReferenceOptions, NewReferenceOptions),
            ("    /// <summary>Gets the default shared external package references.</summary>", "    /// <summary>Gets the Terminal compatibility catalog of shared external package references.</summary>"),
            ("    /// <exception cref=\"FormatException\">Thrown when <see cref=\"PluginRootBuildFileOptions.GlobalJsonContent\"/> is not a JSON object.</exception>", "    /// <exception cref=\"FormatException\">Thrown when <see cref=\"PluginRootBuildFileOptions.GlobalJsonContent\"/> is not a JSON object.</exception>\n" + BuildExceptions),
            ("        ArgumentNullException.ThrowIfNull(options.GlobalJsonContent);\n\n        Directory.CreateDirectory(root.RootPath);", "        ArgumentNullException.ThrowIfNull(options.GlobalJsonContent);\n        PluginAuthoringPolicy.ValidateBuildOptions(options);\n\n        Directory.CreateDirectory(root.RootPath);"),
            (OldRendererEntry, NewRendererEntry),
            ("    <CodeAltaPluginRoot>$(MSBuildThisFileDirectory)</CodeAltaPluginRoot>\n", "    <CodeAltaPluginRoot>$(MSBuildThisFileDirectory)</CodeAltaPluginRoot>\n" + ProfileStamp),
            ("foreach (var assemblyName in options.HostAssemblyNames.OrderBy", "foreach (var assemblyName in PluginAuthoringPolicy.GetHostReferences(options.AuthoringProfile, options.HostAssemblyNames).OrderBy"),
            (OldOptionalTui + "        targets.AppendLine(\"  </ItemGroup>\");\n        targets.AppendLine(\"  <ItemGroup>\");\n        foreach (var packageName in options.SharedPackageNames.OrderBy", "        targets.AppendLine(\"  </ItemGroup>\");\n        targets.AppendLine(\"  <ItemGroup>\");\n        foreach (var packageName in PluginAuthoringPolicy.GetPackageReferences(options.AuthoringProfile, options.SharedPackageNames).OrderBy"),
            ("    private sealed record GeneratedFile(string FileName, string Content, string Marker);", "    internal sealed record GeneratedFile(string FileName, string Content, string Marker);"),
        ],
        "CodeAlta.Plugins/PluginAssemblyLoading.cs" =>
        [
            ("    /// <exception cref=\"ArgumentException\">Thrown when <paramref name=\"mainAssemblyPath\"/> is empty.</exception>", "    /// <exception cref=\"ArgumentException\">Thrown when the main path or an additional name is invalid, or a name requires Terminal.</exception>"),
            ("    private readonly IReadOnlySet<string> _hostSharedAssemblyNames;\n", "    private readonly IReadOnlySet<string> _hostSharedAssemblyNames;\n    private readonly PluginAuthoringProfile _authoringProfile;\n"),
            (OldContextConstructor, NewContextConstructors),
            (OldSharedLoad, NewSharedLoad),
            ("    public string? ResolveManagedAssemblyPath(AssemblyName assemblyName)", "    /// <exception cref=\"FileLoadException\">Thrown when a terminal assembly is requested under Neutral; reserved identities never resolve privately.</exception>\n    public string? ResolveManagedAssemblyPath(AssemblyName assemblyName)"),
            ("        if (!string.IsNullOrWhiteSpace(assemblyName.Name) && _hostSharedAssemblyNames.Contains(assemblyName.Name))\n        {\n            return null;\n        }", "        if (IsHostShared(assemblyName))\n        {\n            return null;\n        }"),
            ("        return _resolver.ResolveAssemblyToPath(assemblyName);\n    }\n", "        return _resolver.ResolveAssemblyToPath(assemblyName);\n    }\n" + AdmissionMethods),
            ("    /// <summary>Gets the default host-shared assembly simple names.</summary>", "    /// <summary>Gets the Terminal compatibility catalog of host-shared assembly simple names.</summary>"),
            ("    private readonly IReadOnlyList<string> _hostSharedAssemblyNames;\n", "    private readonly IReadOnlyList<string> _hostSharedAssemblyNames;\n    private readonly PluginAuthoringProfile _authoringProfile;\n"),
            (OldLoaderConstructor, NewLoaderConstructors),
            ("            loadContext = new PluginAssemblyLoadContext(outputAssemblyPath, _hostSharedAssemblyNames);", "            loadContext = new PluginAssemblyLoadContext(outputAssemblyPath, _authoringProfile, _hostSharedAssemblyNames);\n            loadContext.ValidateMainAssembly(outputAssemblyPath);"),
        ],
        "CodeAlta.Plugins/PluginRuntimeManager.cs" =>
        [
            ("public sealed record PluginRuntimeManagerOptions\n{\n", "public sealed record PluginRuntimeManagerOptions\n{\n" + RuntimeProfileOption),
            ("    public async ValueTask<PluginRuntimeManagerStartResult> StartAsync(\n", "    /// <exception cref=\"ArgumentOutOfRangeException\">Thrown when the authoring profile is invalid, before startup acquires resources.</exception>\n    public async ValueTask<PluginRuntimeManagerStartResult> StartAsync(\n"),
            ("        ArgumentNullException.ThrowIfNull(options.StartupFeedback);\n", "        ArgumentNullException.ThrowIfNull(options.StartupFeedback);\n        PluginAuthoringPolicy.Validate(options.AuthoringProfile);\n"),
            ("            var generationOptions = new PluginRootBuildFileOptions\n            {\n", "            var generationOptions = new PluginRootBuildFileOptions\n            {\n                AuthoringProfile = options.AuthoringProfile,\n"),
            ("            foreach (var root in plan.BuildRequests.Select", "            var successfulRoots = new List<string>();\n            foreach (var root in plan.BuildRequests.Select"),
            ("                diagnostics.AddRange(generation.Diagnostics);\n            }\n", "                diagnostics.AddRange(generation.Diagnostics);\n                if (generation.Succeeded) successfulRoots.Add(root.RootPath);\n            }\n            var admittedBuildRequests = PluginAuthoringPolicy.FilterBuildRequests(plan.BuildRequests, successfulRoots);\n"),
            ("scheduler.BuildAsync(plan.BuildRequests, token)", "scheduler.BuildAsync(admittedBuildRequests, token)"),
            ("            var loader = new PluginAssemblyLoader();", "            var loader = new PluginAssemblyLoader(options.AuthoringProfile);"),
            ("            HostApiVersion = \"1.0.0\",", "            HostApiVersion = PluginAuthoringPolicy.HostApiVersion,"),
        ],
        "CodeAlta.Orchestration/Hosting/CodeAltaHostOptions.cs" =>
        [
            ("    public IPluginStartupFeedback PluginStartupFeedback { get; init; } = new SilentPluginStartupFeedback();\n", "    public IPluginStartupFeedback PluginStartupFeedback { get; init; } = new SilentPluginStartupFeedback();\n" + HostProfileOption),
        ],
        "CodeAlta.Orchestration/Hosting/CodeAltaHost.cs" =>
        [
            ("    public static async Task<CodeAltaHost> CreateAsync(\n", "    /// <exception cref=\"ArgumentOutOfRangeException\">Thrown when the plugin authoring profile is invalid, before host acquisition.</exception>\n    public static async Task<CodeAltaHost> CreateAsync(\n"),
            ("        ArgumentNullException.ThrowIfNull(options.PluginStartupFeedback);\n", "        ArgumentNullException.ThrowIfNull(options.PluginStartupFeedback);\n        if (!Enum.IsDefined(options.PluginAuthoringProfile)) throw new ArgumentOutOfRangeException(nameof(options.PluginAuthoringProfile));\n"),
            ("                            StartupFeedback = options.PluginStartupFeedback,\n", "                            StartupFeedback = options.PluginStartupFeedback,\n                            AuthoringProfile = options.PluginAuthoringProfile,\n"),
        ],
        "CodeAlta.Tui/Program.cs" =>
        [
            ("                    StartupFeedback = new CodeAlta.Tui.Plugins.TerminalPluginStartupFeedback(),\n", "                    StartupFeedback = new CodeAlta.Tui.Plugins.TerminalPluginStartupFeedback(),\n                    AuthoringProfile = PluginAuthoringProfile.Terminal,\n"),
        ],
        "CodeAlta.Tui/App/CodeAltaOwnedServices.cs" =>
        [
            ("                        PluginStartupFeedback = new CodeAlta.Tui.Plugins.TerminalPluginStartupFeedback(),\n", "                        PluginStartupFeedback = new CodeAlta.Tui.Plugins.TerminalPluginStartupFeedback(),\n                        PluginAuthoringProfile = PluginAuthoringProfile.Terminal,\n"),
        ],
        "CodeAlta.Tests/PluginUiContentExtractionSourceTests.cs" =>
        [
            ("        => SourceTestText.DecodeSource(File.ReadAllBytes(SourcePath(path)));", "        => PluginAuthoringProfileSourceInverse.RestoreUiContentInput(path, SourceTestText.DecodeSource(File.ReadAllBytes(SourcePath(path))));"),
        ],
        "CodeAlta.Tests/PluginFeedbackExtractionSourceTests.cs" =>
        [
            ("        source = Canonical(source);\n        return RestoreCore(path, source);", "        source = Canonical(source);\n        source = PluginAuthoringProfileSourceInverse.RestoreFeedbackInput(path, source);\n        return RestoreCore(path, source);"),
        ],
        "CodeAlta.Tests/PluginNeutralContractSourceInverse.cs" =>
        [
            ("        source = SourceTestText.DecodeSource(new UTF8Encoding(false, true).GetBytes(source));\n        foreach (var (before, after) in Edits(path))", "        source = SourceTestText.DecodeSource(new UTF8Encoding(false, true).GetBytes(source));\n        source = PluginAuthoringProfileSourceInverse.RestoreNeutralInput(path, source);\n        foreach (var (before, after) in Edits(path))"),
        ],
        "CodeAlta.Desktop.Tests/CodeAlta.Desktop.Tests.csproj" =>
        [
            (NeutralLink, NeutralLink + ProfileLink),
        ],
        "CodeAlta.Desktop.Tests/DesktopWorkspaceSourceTests.cs" =>
        [
            (OldWorkspaceFrozenLoop, NewWorkspaceFrozenLoop),
        ],
        _ => throw new AssertFailedException("No authoring-profile inverse for " + path),
    };

    private const string OldWorkspaceFrozenLoop = """
        foreach (var (path, hash) in Frozen) Assert.AreEqual(hash, Hash(Read(path)), path);
        var appBytes = Encoding.UTF8.GetByteCount(Read("CodeAlta.Tui/App/CodeAltaApp.cs").Replace("\n", "\r\n", StringComparison.Ordinal));
        Assert.AreEqual(47026, appBytes);
        Assert.IsTrue(appBytes < 47064);
""";
    private const string NewWorkspaceFrozenLoop = """
        foreach (var (path, hash) in Frozen)
        {
            var source = Read(path);
            if (path == "CodeAlta.Tests/PluginFeedbackExtractionSourceTests.cs")
                source = PluginAuthoringProfileSourceInverse.Restore(path, source);
            Assert.AreEqual(hash, Hash(source), path);
        }
        var appBytes = Encoding.UTF8.GetByteCount(Read("CodeAlta.Tui/App/CodeAltaApp.cs").Replace("\n", "\r\n", StringComparison.Ordinal));
        Assert.AreEqual(47026, appBytes);
        Assert.IsTrue(appBytes < 47064);
""";

    private const string BuildProfileOption = "    /// <summary>Gets the explicit assembly authoring profile; reusable callers default to Neutral.</summary>\n    /// <remarks>Rich source-plugin hosts must select Terminal explicitly, independently of interactivity.</remarks>\n    public PluginAuthoringProfile AuthoringProfile { get; init; } = PluginAuthoringProfile.Neutral;\n\n";
    private const string RuntimeProfileOption = "    /// <summary>Gets the explicit source-plugin authoring profile; defaults to Neutral.</summary>\n    /// <remarks>Terminal hosts must opt in even on noninteractive or CLI paths. This does not select presentation capabilities.</remarks>\n    public PluginAuthoringProfile AuthoringProfile { get; init; } = PluginAuthoringProfile.Neutral;\n\n";
    private const string HostProfileOption = "\n    /// <summary>Gets the source-plugin authoring profile for runtimes started by this host; defaults to Neutral.</summary>\n    /// <remarks>Terminal hosts must opt in independently of interactivity. A supplied prestarted runtime remains borrowed and is not reprofiled.</remarks>\n    public PluginAuthoringProfile PluginAuthoringProfile { get; init; } = PluginAuthoringProfile.Neutral;\n";
    private const string BuildExceptions = "    /// <exception cref=\"ArgumentOutOfRangeException\">Thrown when the authoring profile is invalid.</exception>\n    /// <exception cref=\"ArgumentException\">Thrown when an additional reference is invalid or incompatible with the profile; validation precedes root mutation.</exception>";
    private const string ProfileStamp = "    <CodeAltaPluginAuthoringProfile>{options.AuthoringProfile}</CodeAltaPluginAuthoringProfile>\n    <CodeAltaPluginAuthoringPolicyVersion>{PluginAuthoringPolicy.PolicyVersion}</CodeAltaPluginAuthoringPolicyVersion>\n    <CodeAltaPluginHostApiVersion>{PluginAuthoringPolicy.HostApiVersion}</CodeAltaPluginHostApiVersion>\n";
    private const string NeutralLink = "    <Compile Include=\"../CodeAlta.Tests/PluginNeutralContractSourceInverse.cs\" Link=\"PluginNeutralContractSourceInverse.cs\" />\n";
    private const string ProfileLink = "    <Compile Include=\"../CodeAlta.Tests/PluginAuthoringProfileSourceInverse.cs\" Link=\"PluginAuthoringProfileSourceInverse.cs\" />\n";

    private const string OldReferenceOptions = """
    /// <summary>Gets host CodeAlta assemblies referenced from the default load context.</summary>
    public IReadOnlyList<string> HostAssemblyNames { get; init; } = PluginRootBuildFileGenerator.DefaultHostAssemblyNames;

    /// <summary>Gets shared external authoring package names referenced with runtime and native assets excluded.</summary>
    public IReadOnlyList<string> SharedPackageNames { get; init; } = PluginRootBuildFileGenerator.DefaultSharedPackageNames;
""";
    private const string NewReferenceOptions = """
    /// <summary>Gets additional host assembly references, or null to use only the profile defaults.</summary>
    /// <remarks>Migration: lists are now nullable and additive, not replacement defaults. Mandatory identities remain shared; Neutral rejects terminal names.</remarks>
    public IReadOnlyList<string>? HostAssemblyNames { get; init; }

    /// <summary>Gets additional authoring packages with runtime/native assets excluded, or null for profile defaults.</summary>
    /// <remarks>Migration: select Terminal for the former rich defaults. Explicit lists extend, rather than remove, the profile's mandatory references.</remarks>
    public IReadOnlyList<string>? SharedPackageNames { get; init; }
""";
    private const string OldRendererEntry = """
    private static IReadOnlyList<GeneratedFile> CreateFiles(PluginRootBuildFileOptions options)
    {
        var codeAltaExeFolder = XmlEscape(PluginRuntimePathService.NormalizeDirectory(options.CodeAltaExeFolder));
""";
    private const string NewRendererEntry = """
    private static IReadOnlyList<GeneratedFile> CreateFiles(PluginRootBuildFileOptions options)
        => CreateFilesCore(options, PluginRuntimePathService.NormalizeDirectory(options.CodeAltaExeFolder));

    internal static IReadOnlyList<GeneratedFile> CreateFilesCore(PluginRootBuildFileOptions options, string normalizedCodeAltaExeFolder)
    {
        PluginAuthoringPolicy.ValidateBuildOptions(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(normalizedCodeAltaExeFolder);
        var codeAltaExeFolder = XmlEscape(normalizedCodeAltaExeFolder);
""";
    private const string OldOptionalTui = """
        targets.AppendLine("    <Reference Include=\"CodeAlta.Plugins.Tui\" Condition=\"Exists('$(CodeAltaExeFolder)\\CodeAlta.Plugins.Tui.dll')\">");
        targets.AppendLine("      <HintPath>$(CodeAltaExeFolder)\\CodeAlta.Plugins.Tui.dll</HintPath>");
        targets.AppendLine("      <Private>false</Private>");
        targets.AppendLine("    </Reference>");
""" + "\n";

    private const string OldContextConstructor = """
    public PluginAssemblyLoadContext(string mainAssemblyPath, IEnumerable<string>? hostSharedAssemblyNames = null)
        : base($"CodeAlta.Plugin:{Path.GetFileNameWithoutExtension(mainAssemblyPath)}:{Guid.NewGuid():N}", isCollectible: true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mainAssemblyPath);
        _resolver = new AssemblyDependencyResolver(mainAssemblyPath);
        _hostSharedAssemblyNames = new HashSet<string>(
            hostSharedAssemblyNames ?? PluginAssemblyLoader.DefaultHostSharedAssemblyNames,
            StringComparer.OrdinalIgnoreCase);
    }
""";
    private const string NewContextConstructors = """
    /// <remarks>Defaults to Neutral. Additional shared names cannot remove mandatory identities; rich hosts must select Terminal.</remarks>
    public PluginAssemblyLoadContext(string mainAssemblyPath, IEnumerable<string>? hostSharedAssemblyNames = null)
        : this(mainAssemblyPath, PluginAuthoringProfile.Neutral, hostSharedAssemblyNames)
    {
    }

    /// <summary>Creates a collectible context using an explicit authoring profile.</summary>
    /// <param name="mainAssemblyPath">The plugin output assembly path.</param>
    /// <param name="authoringProfile">The host's assembly authoring profile, not its interactivity.</param>
    /// <exception cref="ArgumentException">Thrown when the main path is empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the profile is invalid, before context creation.</exception>
    public PluginAssemblyLoadContext(string mainAssemblyPath, PluginAuthoringProfile authoringProfile)
        : this(mainAssemblyPath, authoringProfile, null)
    {
    }

    /// <summary>Creates a context with additional shared identities; mandatory profile identities cannot be removed.</summary>
    /// <param name="mainAssemblyPath">The plugin output assembly path.</param>
    /// <param name="authoringProfile">The explicit host authoring profile.</param>
    /// <param name="hostSharedAssemblyNames">Additional shared names, or null for the profile defaults.</param>
    /// <exception cref="ArgumentException">Thrown when a path/name is invalid or an additional name conflicts with Neutral.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the profile is invalid, before context creation.</exception>
    public PluginAssemblyLoadContext(string mainAssemblyPath, PluginAuthoringProfile authoringProfile, IEnumerable<string>? hostSharedAssemblyNames)
        : this(CreateOptions(mainAssemblyPath, authoringProfile, hostSharedAssemblyNames))
    {
    }

    private PluginAssemblyLoadContext((string Path, PluginAuthoringProfile Profile, string[] SharedNames) options)
        : base($"CodeAlta.Plugin:{Path.GetFileNameWithoutExtension(options.Path)}:{Guid.NewGuid():N}", isCollectible: true)
    {
        _authoringProfile = options.Profile;
        _hostSharedAssemblyNames = new HashSet<string>(options.SharedNames, StringComparer.OrdinalIgnoreCase);
        _resolver = new AssemblyDependencyResolver(options.Path);
    }

    private static (string Path, PluginAuthoringProfile Profile, string[] SharedNames) CreateOptions(
        string path, PluginAuthoringProfile profile, IEnumerable<string>? additionalNames)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return (path, profile, PluginAuthoringPolicy.GetSharedAssemblies(profile, additionalNames));
    }
""";
    private const string OldSharedLoad = """
        if (!string.IsNullOrWhiteSpace(assemblyName.Name) && _hostSharedAssemblyNames.Contains(assemblyName.Name))
        {
            return AssemblyLoadContext.Default.Assemblies.FirstOrDefault(assembly =>
                string.Equals(assembly.GetName().Name, assemblyName.Name, StringComparison.OrdinalIgnoreCase));
        }
""";
    private const string NewSharedLoad = """
        if (IsHostShared(assemblyName))
        {
            return AssemblyLoadContext.Default.Assemblies.FirstOrDefault(assembly =>
                string.Equals(assembly.GetName().Name, assemblyName.Name, StringComparison.OrdinalIgnoreCase))
                ?? AssemblyLoadContext.Default.LoadFromAssemblyName(assemblyName);
        }
""";
    private const string AdmissionMethods = """

    internal void ValidateMainAssembly(string mainAssemblyPath)
        => PluginAssemblyReferenceAdmission.Inspect(mainAssemblyPath, _authoringProfile, _hostSharedAssemblyNames, _resolver.ResolveAssemblyToPath);

    private bool IsHostShared(AssemblyName assemblyName)
    {
        var binding = PluginAuthoringPolicy.ClassifyAssembly(_authoringProfile, assemblyName.Name ?? string.Empty, _hostSharedAssemblyNames);
        if (binding == PluginAssemblyBinding.Forbidden)
            throw new FileLoadException($"Assembly '{assemblyName.Name}' requires the Terminal authoring profile.");
        return binding == PluginAssemblyBinding.Host;
    }
""" + "\n";
    private const string OldLoaderConstructor = """
    public PluginAssemblyLoader(IEnumerable<string>? hostSharedAssemblyNames = null)
    {
        _hostSharedAssemblyNames = (hostSharedAssemblyNames ?? DefaultHostSharedAssemblyNames).ToArray();
    }
""";
    private const string NewLoaderConstructors = """
    /// <remarks>Defaults to Neutral. Lists now add shared identities instead of replacing the mandatory defaults.</remarks>
    /// <exception cref="ArgumentException">Thrown when an additional name is invalid or requires Terminal.</exception>
    public PluginAssemblyLoader(IEnumerable<string>? hostSharedAssemblyNames = null)
        : this(PluginAuthoringProfile.Neutral, hostSharedAssemblyNames)
    {
    }

    /// <summary>Creates a loader for an explicit host authoring profile.</summary>
    /// <param name="authoringProfile">The assembly profile, independent of presentation capabilities.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the profile is invalid.</exception>
    public PluginAssemblyLoader(PluginAuthoringProfile authoringProfile)
        : this(authoringProfile, null)
    {
    }

    /// <summary>Creates a loader retaining mandatory profile identities alongside additional shared names.</summary>
    /// <param name="authoringProfile">The explicit authoring profile.</param>
    /// <param name="hostSharedAssemblyNames">Additional shared names, or null for profile defaults.</param>
    /// <exception cref="ArgumentException">Thrown when an additional name is invalid or conflicts with Neutral.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the profile is invalid.</exception>
    public PluginAssemblyLoader(PluginAuthoringProfile authoringProfile, IEnumerable<string>? hostSharedAssemblyNames)
    {
        _hostSharedAssemblyNames = PluginAuthoringPolicy.GetSharedAssemblies(authoringProfile, hostSharedAssemblyNames);
        _authoringProfile = authoringProfile;
    }
""";
}
