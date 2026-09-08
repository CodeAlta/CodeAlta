using System.Security.Cryptography;
using System.Text;

namespace CodeAlta.Tests;

/// <summary>Mandatory whole-source inverses of parent-qualified d32391ae; no production or filesystem calls.</summary>
internal static class PluginGitHubBackendSeparationSourceInverse
{
    internal static string CurrentPath(string path) => path switch
    {
        "CodeAlta.Plugin.GitHub/GitHubIssuePromptAttachment.cs" => "CodeAlta.Tui/Plugins/GitHub/GitHubIssuePromptAttachment.cs",
        "CodeAlta.Plugin.GitHub/GitHubIssuePickerDialog.cs" => "CodeAlta.Tui/Plugins/GitHub/GitHubIssuePickerDialog.cs",
        "CodeAlta.Plugin.GitHub/GitHubIssueReferenceParser.cs" => "CodeAlta.Tui/Plugins/GitHub/GitHubIssueReferenceParser.cs",
        _ => path,
    };

    // Disjoint reader maps: each changed input is restored exactly once before its older chain.
    internal static string RestoreProfileInput(string path, string source) => path is
        "CodeAlta.Plugins/PluginRuntimeManager.cs" or "CodeAlta.Tests/PluginNeutralContractSourceInverse.cs" or
        "CodeAlta.Desktop.Tests/CodeAlta.Desktop.Tests.csproj"
        ? Restore(path, source) : source;

    internal static string RestoreNeutralInput(string path, string source) => path is
        "CodeAlta.Plugin.GitHub/GitHubPlugin.cs" or "CodeAlta.Plugin.GitHub/CodeAlta.Plugin.GitHub.csproj" or
        "CodeAlta.Plugin.GitHub/GitHubIssuePromptAttachment.cs" or "CodeAlta.Plugin.GitHub/GitHubIssuePickerDialog.cs"
        ? Restore(path, source) : source;

    internal static string Restore(string path, string source)
    {
        source = SourceTestText.DecodeSource(new UTF8Encoding(false, true).GetBytes(source));
        foreach (var (before, after) in Edits(path))
        {
            var expected = SourceTestText.Canonicalize(after);
            Assert.IsTrue(expected.Length > 0, path);
            Assert.AreEqual(1, source.Split(expected, StringSplitOptions.None).Length - 1, path + ": " + expected);
            source = source.Replace(expected, SourceTestText.Canonicalize(before), StringComparison.Ordinal);
        }
        source = SourceTestText.DecodeSource(new UTF8Encoding(false, true).GetBytes(source));
        Assert.AreEqual(Originals.Single(item => item.Path == path).Hash, Hash(source), path);
        return source;
    }

    internal static string Hash(string source) => Convert.ToHexString(SHA256.HashData(new UTF8Encoding(false, true).GetBytes(source)));

    internal static IReadOnlyList<(string Path, string Hash)> Originals =>
    [
        ("CodeAlta.Plugins/PluginRuntimeLifecycle.cs", "376E9C08D3217BE13B4998039D60CB92093B0B095B1DA2601E37FF73C743F30A"),
        ("CodeAlta.Plugins/PluginRuntimeManager.cs", "0F8DCF35BC4165244EF3B0CF9309C7D943EE8C65216508DB8172501E74C2D76B"),
        ("CodeAlta.Plugins/BuiltInPlugins.cs", "F6C996EF40AA8242449DAF008B55063DC5AED5705010DD20933770512878B74B"),
        ("CodeAlta.Plugin.GitHub/GitHubPlugin.cs", "C0AFC6F756C281BA1419795B4A7B22117B5E605FA9A3FA9E5E93356286D2A61C"),
        ("CodeAlta.Plugin.GitHub/GitHubIssueReferenceItem.cs", "8B63F3FBC96F171D8928127835F00A74466FE1A1530F3C07476CABF6F2580D33"),
        ("CodeAlta.Plugin.GitHub/CodeAlta.Plugin.GitHub.csproj", "B6431E36D957725B5D86C7F1B03B939F7C72B6B05623A86DBF5AECF38FAB4256"),
        ("CodeAlta.Tui/App/CodeAltaBuiltInPlugins.cs", "8035D597AC0A02A9F67E28AD52A9CA97F2AD6F29398557DAF72DCE7288E021F4"),
        ("CodeAlta.Plugin.GitHub/GitHubIssuePromptAttachment.cs", "FDE2958E2D1D3343A2D017222BAC508F996B06238CE913FF15528BA2BA5E0FCC"),
        ("CodeAlta.Plugin.GitHub/GitHubIssuePickerDialog.cs", "A4FE7FE597310E22FEAECA12E5D031A9016C4CD649A64A7A62F30A86629B2CA6"),
        ("CodeAlta.Plugin.GitHub/GitHubIssueReferenceParser.cs", "6D3913C02C6E1BEF1E065973CBF4C83CA749DCB5E70E2784F9309FC9A1848ED3"),
        ("CodeAlta.Tests/PluginAuthoringProfileSourceInverse.cs", "474A56E6FFF4967A6FF45835DED58E0326B8282CD5DD8CF4E9FD812A9C560BE9"),
        ("CodeAlta.Tests/PluginNeutralContractSourceInverse.cs", "AF58010966A52FFB88FE9EFEDA450762D59425CCA78F640405A5BAD4F790795F"),
        ("CodeAlta.Tests/PluginNeutralContractSourceTests.cs", "227C9945FFD03C45449F1FCCE9FC638C3713AF69C6CFF3F4EBB1B60B9C39B370"),
        ("CodeAlta.Desktop.Tests/CodeAlta.Desktop.Tests.csproj", "91960209B82418C00906D2912D605E72EAD287A471216135AA7F8D93AE9E01E0"),
        ("CodeAlta.Tests/GitHubPluginTests.cs", "706099D70E533CE71CFA4BA83CA68A6747BC8A844AC960FF0B0C672C92E560A5"),
    ];

    private static IReadOnlyList<(string Before, string After)> Edits(string path) => path switch
    {
        "CodeAlta.Plugins/PluginRuntimeLifecycle.cs" =>
        [
            (GenerationTail, GenerationTail.Replace("\n}", "\n" + FactoryOption + "}")),
            (OldConstruction, NewConstruction),
            (LoggerAnchor, TypeAdmission + LoggerAnchor),
            (CollectionAnchor, SelectionSeam + CollectionAnchor),
        ],
        "CodeAlta.Plugins/PluginRuntimeManager.cs" => [(OldBuiltInActivation, NewBuiltInActivation)],
        "CodeAlta.Plugins/BuiltInPlugins.cs" =>
        [
            (OldFactoryDocs, NewFactoryDocs),
            (ResolveDocs, ResolveDocs + ResolveRemarks),
        ],
        "CodeAlta.Plugin.GitHub/GitHubPlugin.cs" =>
        [
            ("using CodeAlta.Plugins.Abstractions;\nusing CodeAlta.Plugins.Tui;\nusing XenoAtom.Logging;\n", "using CodeAlta.Plugins.Abstractions;\nusing XenoAtom.Logging;\n"),
            (GitHubFieldsAnchor, GitHubFieldsAnchor + GitHubConstruction),
            (OldGitHubContribution, NewGitHubContribution),
            (OldSelectedPath, NewSelectedPath),
        ],
        "CodeAlta.Plugin.GitHub/GitHubIssueReferenceItem.cs" =>
        [
            ("using System.Globalization;\nusing XenoAtom.Terminal.UI;\n", "using System.Globalization;\n"),
            (ItemTail + "\n" + OldAccessors + "}\n", ItemTail + "}\n"),
        ],
        "CodeAlta.Plugin.GitHub/CodeAlta.Plugin.GitHub.csproj" =>
        [
            (LoggingReference + TerminalPackage, LoggingReference),
            (AbstractionsReference + TerminalReference, AbstractionsReference),
        ],
        "CodeAlta.Tui/App/CodeAltaBuiltInPlugins.cs" =>
        [("            PluginType = typeof(GitHubPlugin),\n            Factory = static () => new GitHubPlugin(),\n", "            PluginType = typeof(GitHubPlugin),\n            Factory = static () => new GitHubPlugin(GitHubTerminalContributions.CreatePromptEditorContributions),\n")],
        // Byte-for-byte moves still require their complete parent-supplied source hashes.
        "CodeAlta.Plugin.GitHub/GitHubIssuePromptAttachment.cs" or "CodeAlta.Plugin.GitHub/GitHubIssueReferenceParser.cs" => [],
        "CodeAlta.Plugin.GitHub/GitHubIssuePickerDialog.cs" =>
        [
            (OldColumns, NewColumns),
            (OldIdAccessor, NewIdAccessor),
            (OldTitleAccessor, NewTitleAccessor),
            (OldStateAccessor, NewStateAccessor),
            (OldUpdatedAccessor, NewUpdatedAccessor),
            (OldLinkAccessor, NewLinkAccessor),
        ],
        "CodeAlta.Tests/PluginAuthoringProfileSourceInverse.cs" => [(ProfilePrelude, ProfilePrelude.Replace("        foreach", ProfileHook + "        foreach"))],
        "CodeAlta.Tests/PluginNeutralContractSourceInverse.cs" => [(NeutralPrelude, NeutralPrelude.Replace("        source = PluginAuthoring", NeutralHook + "        source = PluginAuthoring"))],
        "CodeAlta.Tests/PluginNeutralContractSourceTests.cs" =>
        [
            (OldNeutralGitHubAssertion, NewNeutralGitHubAssertion),
            (OldNeutralReferenceAssertion, NewNeutralReferenceAssertion),
            (OldNeutralPackages, NewNeutralPackages),
            ("        return Path.Combine(directory.FullName, path);\n    }\n", "        return Path.Combine(directory.FullName, PluginGitHubBackendSeparationSourceInverse.CurrentPath(path));\n    }\n"),
        ],
        "CodeAlta.Desktop.Tests/CodeAlta.Desktop.Tests.csproj" => [(ProfileLink, ProfileLink + GitHubLink)],
        "CodeAlta.Tests/GitHubPluginTests.cs" => [(OldExistingTest, NewExistingTest)],
        _ => throw new AssertFailedException("No GitHub backend inverse for " + path),
    };

    internal const string GenerationTail = "    /// <summary>Gets the activation generation.</summary>\n    public int ActivationGeneration { get; init; } = 1;\n}\n";
    internal const string FactoryOption = "\n    /// <summary>Gets the explicitly composed built-in factory; source plugins retain type-based activation.</summary>\n    internal Func<PluginBase>? BuiltInFactory { get; init; }\n";
    internal const string OldConstruction = "        try\n        {\n            instance = (PluginBase?)Activator.CreateInstance(discoveredType.Type);\n            if (instance is null)\n";
    internal const string NewConstruction = "        try\n        {\n            instance = CreateInstance(options.BuiltInFactory, () => (PluginBase?)Activator.CreateInstance(discoveredType.Type));\n            if (instance is null)\n";
    internal const string LoggerAnchor = "            var logger = LogManager.GetLogger($\"CodeAlta.Plugin.{discoveredType.Descriptor.RuntimeKey}\");\n";
    internal const string TypeAdmission = """
                if (instance.GetType() != discoveredType.Type)
                {
                    throw new InvalidOperationException($"Plugin factory returned type '{instance.GetType().FullName}' instead of '{discoveredType.Type.FullName}'.");
                }

    """ + "\n";
    internal const string CollectionAnchor = "    private IReadOnlyList<PluginContributionRegistration> CollectContributions(\n";
    internal const string SelectionSeam = """
        // No retry or reflection fallback after a supplied factory returns null or throws (including cancellation).
        internal static PluginBase? CreateInstance(Func<PluginBase>? builtInFactory, Func<PluginBase?> createFromType)
            => builtInFactory is not null ? builtInFactory() : createFromType();

    """ + "\n";
    internal const string OldBuiltInActivation = """
                var activation = await activator.ActivateAsync(
                        discovered,
                        sourcePackage: null,
                        loadContext: null,
                        new PluginActivationOptions { HostInfo = hostInfo, Services = options.Services, ActivationGeneration = ++_activationGeneration },
                        cancellationToken)
    """ + "\n";
    internal const string NewBuiltInActivation = """
                var activation = await activator.ActivateAsync(
                        discovered,
                        sourcePackage: null,
                        loadContext: null,
                        new PluginActivationOptions { HostInfo = hostInfo, Services = options.Services, ActivationGeneration = ++_activationGeneration, BuiltInFactory = builtIn.Factory },
                        cancellationToken)
    """ + "\n";
    internal const string OldFactoryDocs = "    /// <summary>Gets the factory used to create a plugin instance.</summary>\n    public required Func<PluginBase> Factory { get; init; }\n";
    internal const string NewFactoryDocs = """
        /// <summary>Gets the factory invoked once per built-in activation attempt.</summary>
        /// <remarks>
        /// Return a new instance of the resolved concrete plugin type. Metadata resolution can also invoke this
        /// factory when <see cref="PluginType"/> is omitted; this is not an exactly-once lifetime guarantee.
        /// Activation invokes the factory directly, without reflection's constructor-exception wrapping.
        /// Factory cancellation follows the runtime's existing cancellation exclusion, not a reflection fallback.
        /// </remarks>
        public required Func<PluginBase> Factory { get; init; }
    """ + "\n";
    internal const string ResolveDocs = "    /// <summary>Resolves the concrete plugin type.</summary>\n    /// <returns>The concrete plugin type.</returns>\n";
    internal const string ResolveRemarks = "    /// <remarks>Without an explicit type, a factory declared to return <see cref=\"PluginBase\"/> is invoked on each resolution. Specify <see cref=\"PluginType\"/> to avoid metadata-time construction.</remarks>\n";
    internal const string GitHubFieldsAnchor = "    private bool _ghAvailable;\n    private HttpClient? _httpClient;\n";
    internal const string GitHubConstruction = """
        private readonly Func<GitHubPlugin, IEnumerable<PluginPromptEditorContribution>>? _createPromptEditorContributions;

        /// <summary>Initializes a GitHub backend without prompt-editor presentation.</summary>
        public GitHubPlugin()
        {
        }

        /// <summary>Initializes a GitHub backend with explicitly composed prompt-editor contributions.</summary>
        /// <param name="createPromptEditorContributions">A factory receiving this backend when contributions are enumerated.</param>
        /// <remarks>The factory and its sequence are not evaluated during construction. Attachments borrow this backend; runtime initialization and disposal remain owned by the plugin runtime.</remarks>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="createPromptEditorContributions"/> is null.</exception>
        public GitHubPlugin(Func<GitHubPlugin, IEnumerable<PluginPromptEditorContribution>> createPromptEditorContributions)
        {
            ArgumentNullException.ThrowIfNull(createPromptEditorContributions);
            _createPromptEditorContributions = createPromptEditorContributions;
        }
    """ + "\n";
    internal const string OldGitHubContribution = """
        public override IEnumerable<PluginPromptEditorContribution> GetPromptEditorContributions()
        {
            yield return PluginTui.PromptEditor(
                "GitHub issue prompt picker",
                host => new GitHubIssuePromptAttachment(this, host),
                "[#] to reference a GitHub issue");
        }
    """ + "\n";
    internal const string NewGitHubContribution = """
        public override IEnumerable<PluginPromptEditorContribution> GetPromptEditorContributions()
        {
            if (_createPromptEditorContributions is null)
            {
                yield break;
            }

            foreach (var contribution in _createPromptEditorContributions(this))
            {
                yield return contribution;
            }
        }
    """ + "\n";
    internal const string OldSelectedPath = "    internal string? GetSelectedProjectPath()\n        => Services.Workspace.SelectedProjectPath;\n";
    internal const string NewSelectedPath = """
        /// <summary>Gets the selected project path from this backend's runtime workspace.</summary>
        /// <returns>The selected project path, or null when no project is selected.</returns>
        /// <exception cref="InvalidOperationException">Thrown when no runtime context has been attached.</exception>
        public string? GetSelectedProjectPath()
            => Services.Workspace.SelectedProjectPath;
    """ + "\n";
    internal const string ItemTail = "        return value.ToLocalTime().ToString(\"yyyy-MM-dd\", CultureInfo.CurrentCulture);\n    }\n";
    internal const string OldAccessors = """
        internal static class Accessor
        {
            public static readonly BindingAccessor<string> Id = new("Id", static item => ((GitHubIssueReferenceItem)item).Id, null);

            public static readonly BindingAccessor<string> Title = new("Title", static item => ((GitHubIssueReferenceItem)item).Title, null);

            public static readonly BindingAccessor<string> State = new("State", static item => ((GitHubIssueReferenceItem)item).StateText, null);

            public static readonly BindingAccessor<string> Updated = new("Updated", static item => ((GitHubIssueReferenceItem)item).UpdatedText, null);

            public static readonly BindingAccessor<string> Link = new("Link", static item => ((GitHubIssueReferenceItem)item).LinkText, null);
        }
    """ + "\n";
    internal const string LoggingReference = "    <PackageReference Include=\"XenoAtom.Logging\" />\n";
    internal const string TerminalPackage = "    <PackageReference Include=\"XenoAtom.Terminal.UI\" />\n";
    internal const string AbstractionsReference = "    <ProjectReference Include=\"..\\CodeAlta.Plugins.Abstractions\\CodeAlta.Plugins.Abstractions.csproj\" />\n";
    internal const string TerminalReference = "    <ProjectReference Include=\"..\\CodeAlta.Plugins.Tui\\CodeAlta.Plugins.Tui.csproj\" />\n";
    internal const string OldColumns = """
                    .AddColumn(new DataGridColumnInfo<string>("id", "🐙 Issue", true, GitHubIssueReferenceItem.Accessor.Id))
                    .AddColumn(new DataGridColumnInfo<string>("title", "📝 Title", true, GitHubIssueReferenceItem.Accessor.Title))
                    .AddColumn(new DataGridColumnInfo<string>("state", "🚦 State", true, GitHubIssueReferenceItem.Accessor.State))
                    .AddColumn(new DataGridColumnInfo<string>("updated", "🕒 Updated", true, GitHubIssueReferenceItem.Accessor.Updated))
                    .AddColumn(new DataGridColumnInfo<string>("link", "🔗 Link", true, GitHubIssueReferenceItem.Accessor.Link));
    """ + "\n";
    internal static string NewColumns => OldColumns.Replace("GitHubIssueReferenceItem.Accessor.", "GitHubIssueReferenceAccessors.", StringComparison.Ordinal);
    internal const string OldIdAccessor = "            Header = new TextBlock(\"🐙 Issue\"),\n            TypedValueAccessor = GitHubIssueReferenceItem.Accessor.Id,\n";
    internal static string NewIdAccessor => OldIdAccessor.Replace("GitHubIssueReferenceItem.Accessor.", "GitHubIssueReferenceAccessors.", StringComparison.Ordinal);
    internal const string OldTitleAccessor = "            Header = new TextBlock(\"📝 Title\"),\n            TypedValueAccessor = GitHubIssueReferenceItem.Accessor.Title,\n";
    internal static string NewTitleAccessor => OldTitleAccessor.Replace("GitHubIssueReferenceItem.Accessor.", "GitHubIssueReferenceAccessors.", StringComparison.Ordinal);
    internal const string OldStateAccessor = "            Header = new TextBlock(\"🚦 State\"),\n            TypedValueAccessor = GitHubIssueReferenceItem.Accessor.State,\n";
    internal static string NewStateAccessor => OldStateAccessor.Replace("GitHubIssueReferenceItem.Accessor.", "GitHubIssueReferenceAccessors.", StringComparison.Ordinal);
    internal const string OldUpdatedAccessor = "            Header = new TextBlock(\"🕒 Updated\"),\n            TypedValueAccessor = GitHubIssueReferenceItem.Accessor.Updated,\n";
    internal static string NewUpdatedAccessor => OldUpdatedAccessor.Replace("GitHubIssueReferenceItem.Accessor.", "GitHubIssueReferenceAccessors.", StringComparison.Ordinal);
    internal const string OldLinkAccessor = "            Header = new TextBlock(\"🔗 Link\"),\n            TypedValueAccessor = GitHubIssueReferenceItem.Accessor.Link,\n";
    internal static string NewLinkAccessor => OldLinkAccessor.Replace("GitHubIssueReferenceItem.Accessor.", "GitHubIssueReferenceAccessors.", StringComparison.Ordinal);
    internal const string ProfilePrelude = "        source = SourceTestText.DecodeSource(new UTF8Encoding(false, true).GetBytes(source));\n        foreach (var (before, after) in Edits(path))\n";
    internal const string ProfileHook = "        source = PluginGitHubBackendSeparationSourceInverse.RestoreProfileInput(path, source);\n";
    internal const string NeutralPrelude = "        source = SourceTestText.DecodeSource(new UTF8Encoding(false, true).GetBytes(source));\n        source = PluginAuthoringProfileSourceInverse.RestoreNeutralInput(path, source);\n";
    internal const string NeutralHook = "        source = PluginGitHubBackendSeparationSourceInverse.RestoreNeutralInput(path, source);\n";
    internal const string OldNeutralGitHubAssertion = "        RequireOnce(Read(\"CodeAlta.Plugin.GitHub/GitHubPlugin.cs\"), PluginNeutralContractSourceInverse.NewGitHubContribution);\n";
    internal const string NewNeutralGitHubAssertion = "        RequireOnce(PluginGitHubBackendSeparationSourceInverse.Restore(\"CodeAlta.Plugin.GitHub/GitHubPlugin.cs\", Read(\"CodeAlta.Plugin.GitHub/GitHubPlugin.cs\")), PluginNeutralContractSourceInverse.NewGitHubContribution);\n";
    internal const string OldNeutralReferenceAssertion = "        Assert.IsTrue(References(Project(\"CodeAlta.Plugin.GitHub/CodeAlta.Plugin.GitHub.csproj\"), \"ProjectReference\").Any(name => name.Contains(\"CodeAlta.Plugins.Tui.csproj\", StringComparison.Ordinal)));\n";
    internal const string NewNeutralReferenceAssertion = "        Assert.IsFalse(References(Project(\"CodeAlta.Plugin.GitHub/CodeAlta.Plugin.GitHub.csproj\"), \"ProjectReference\").Any(name => name.Contains(\"CodeAlta.Plugins.Tui.csproj\", StringComparison.Ordinal)));\n";
    internal const string OldNeutralPackages = """
            foreach (var path in new[] { "CodeAlta.Plugins.Tui/CodeAlta.Plugins.Tui.csproj", "CodeAlta.Plugin.Mcp/CodeAlta.Plugin.Mcp.csproj", "CodeAlta.Plugin.GitHub/CodeAlta.Plugin.GitHub.csproj" })
                Assert.IsTrue(References(Project(path), "PackageReference").Contains("XenoAtom.Terminal.UI"), path);
    """ + "\n";
    internal const string NewNeutralPackages = """
            foreach (var path in new[] { "CodeAlta.Plugins.Tui/CodeAlta.Plugins.Tui.csproj", "CodeAlta.Plugin.Mcp/CodeAlta.Plugin.Mcp.csproj" })
                Assert.IsTrue(References(Project(path), "PackageReference").Contains("XenoAtom.Terminal.UI"), path);
            Assert.IsFalse(References(Project("CodeAlta.Plugin.GitHub/CodeAlta.Plugin.GitHub.csproj"), "PackageReference").Any(name => name.StartsWith("XenoAtom.Terminal", StringComparison.Ordinal)));
    """ + "\n";
    internal const string ProfileLink = "    <Compile Include=\"../CodeAlta.Tests/PluginAuthoringProfileSourceInverse.cs\" Link=\"PluginAuthoringProfileSourceInverse.cs\" />\n";
    internal const string GitHubLink = "    <Compile Include=\"../CodeAlta.Tests/PluginGitHubBackendSeparationSourceInverse.cs\" Link=\"PluginGitHubBackendSeparationSourceInverse.cs\" />\n";
    internal const string OldExistingTest = """
        public void PluginContributesPromptEditorAttachment()
        {
            var contribution = new GitHubPlugin().GetPromptEditorContributions().Single();

            Assert.AreEqual("GitHub issue prompt picker", contribution.Name);
            Assert.AreEqual("[#] to reference a GitHub issue", contribution.PlaceholderText);
            Assert.IsNotNull(contribution.Attach);
        }
    """ + "\n";
    internal const string NewExistingTest = """
        public void PluginContributesPromptEditorAttachment()
        {
            var contribution = new GitHubPlugin(GitHubTerminalContributions.CreatePromptEditorContributions).GetPromptEditorContributions().Single();

            Assert.AreEqual("GitHub issue prompt picker", contribution.Name);
            Assert.AreEqual("[#] to reference a GitHub issue", contribution.PlaceholderText);
            Assert.IsNotNull(contribution.Attach);
        }
    """ + "\n";
}
