using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Plugins.Tests;

/// <summary>Inert production-core tests; no generator, resolver, ALC, metadata file or runtime is constructed.</summary>
[TestClass]
public sealed class PluginAuthoringProfileTests
{
    [TestMethod]
    public void NeutralProfile_ExcludesAllTerminalAuthoringReferences()
    {
        var files = Render(PluginAuthoringProfile.Neutral);
        var targets = XDocument.Parse(files.Single(file => file.FileName == "Directory.Build.targets").Content);
        var names = targets.Descendants().Attributes("Include").Select(attribute => attribute.Value).ToArray();
        Assert.IsFalse(names.Any(PluginAuthoringPolicy.IsTerminalAssembly));
        CollectionAssert.IsSubsetOf(new[] { "CodeAlta.Plugins.Abstractions", "CodeAlta.Agent", "CodeAlta.Catalog", "Microsoft.Extensions.AI.Abstractions", "XenoAtom.CommandLine", "XenoAtom.Logging" }, names);
    }

    [TestMethod]
    public void TerminalProfile_PreservesAllRichAuthoringReferences()
    {
        var targets = XDocument.Parse(Render(PluginAuthoringProfile.Terminal).Single(file => file.FileName == "Directory.Build.targets").Content);
        var packages = targets.Descendants("PackageReference").ToArray();
        CollectionAssert.AreEquivalent(PluginRootBuildFileGenerator.DefaultSharedPackageNames.ToArray(), packages.Select(item => item.Attribute("Include")!.Value).ToArray());
        Assert.AreEqual(5, packages.Count(item => PluginAuthoringPolicy.IsTerminalAssembly(item.Attribute("Include")!.Value)));
        Assert.IsTrue(packages.All(item => item.Element("ExcludeAssets")!.Value == "runtime;native"));
        var tui = targets.Descendants("Reference").Single(item => item.Attribute("Include")!.Value == "CodeAlta.Plugins.Tui");
        Assert.IsNull(tui.Attribute("Condition"));
        Assert.AreEqual("false", tui.Element("Private")!.Value);
        Assert.AreEqual("3.9.0", packages.Single(item => item.Attribute("Include")!.Value == "XenoAtom.Terminal.UI").Attribute("Version")!.Value);
    }

    [TestMethod]
    public void ExplicitOverrides_CannotBypassReservedIdentities()
    {
        var hosts = PluginAuthoringPolicy.GetHostReferences(PluginAuthoringProfile.Neutral, ["Example.Contracts"]);
        CollectionAssert.IsSubsetOf(new[] { "CodeAlta.Plugins.Abstractions", "Example.Contracts" }, hosts);
        var shared = PluginAuthoringPolicy.GetSharedAssemblies(PluginAuthoringProfile.Neutral, ["Example.Contracts"]);
        CollectionAssert.IsSubsetOf(new[] { "CodeAlta.Plugins.Abstractions", "Example.Contracts" }, shared);
        Assert.ThrowsExactly<ArgumentException>(() => PluginAuthoringPolicy.GetHostReferences(PluginAuthoringProfile.Neutral, ["CodeAlta.Plugins.Tui"]));
        Assert.ThrowsExactly<ArgumentException>(() => PluginAuthoringPolicy.GetPackageReferences(PluginAuthoringProfile.Neutral, ["xenoatom.terminal.ui"]));
        Assert.ThrowsExactly<ArgumentException>(() => PluginAuthoringPolicy.GetSharedAssemblies(PluginAuthoringProfile.Neutral, ["XenoAtom.Terminal"]));
        foreach (var name in new[] { " CodeAlta.Plugins.Tui ", "Example;CodeAlta.Plugins.Tui", "$(TerminalPackage)", "Example, Version=1.0.0" })
            Assert.ThrowsExactly<ArgumentException>(() => PluginAuthoringPolicy.GetHostReferences(PluginAuthoringProfile.Neutral, [name]));
        var options = Options(PluginAuthoringProfile.Neutral) with { SharedPackageNames = ["XenoAtom.Terminal.UI"] };
        Assert.ThrowsExactly<ArgumentException>(() => PluginRootBuildFileGenerator.CreateFilesCore(options, "/literal/host"));
    }

    [TestMethod]
    public void InvalidProfile_IsRejectedBeforeRendering()
    {
        var invalid = (PluginAuthoringProfile)42;
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => PluginAuthoringPolicy.Validate(invalid));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => PluginRootBuildFileGenerator.CreateFilesCore(Options(invalid) with { GlobalJsonContent = "not json" }, "/literal/host"));
    }

    [TestMethod]
    public void GeneratedFiles_AreIndependentOfPhysicalDllPresence()
    {
        foreach (var profile in new[] { PluginAuthoringProfile.Neutral, PluginAuthoringProfile.Terminal })
        {
            var files = Render(profile);
            Assert.AreEqual(4, files.Count);
            Assert.IsFalse(files.Any(file => file.Content.Contains("Exists(", StringComparison.Ordinal)));
            CollectionAssert.AreEqual(files.Select(file => file.Content).ToArray(), Render(profile).Select(file => file.Content).ToArray());
        }
    }

    [TestMethod]
    public void GeneratedFiles_ProfileStampChangesDeterministically()
    {
        var neutral = Render(PluginAuthoringProfile.Neutral);
        var terminal = Render(PluginAuthoringProfile.Terminal);
        var neutralProps = neutral.Single(file => file.FileName == "Directory.Build.props").Content;
        var terminalProps = terminal.Single(file => file.FileName == "Directory.Build.props").Content;
        Assert.IsTrue(neutralProps.Contains("<CodeAltaPluginAuthoringProfile>Neutral</CodeAltaPluginAuthoringProfile>", StringComparison.Ordinal));
        Assert.IsTrue(terminalProps.Contains("<CodeAltaPluginAuthoringProfile>Terminal</CodeAltaPluginAuthoringProfile>", StringComparison.Ordinal));
        Assert.IsTrue(neutralProps.Contains("<CodeAltaPluginAuthoringPolicyVersion>1</CodeAltaPluginAuthoringPolicyVersion>", StringComparison.Ordinal));
        Assert.IsTrue(neutralProps.Contains("<CodeAltaPluginHostApiVersion>1.0.0</CodeAltaPluginHostApiVersion>", StringComparison.Ordinal));
        Assert.AreNotEqual(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(neutralProps))), Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(terminalProps))));
        Assert.AreEqual(neutralProps, Render(PluginAuthoringProfile.Neutral).Single(file => file.FileName == "Directory.Build.props").Content);
    }

    [TestMethod]
    public void ManagedNames_NeutralRejectsTerminalFamily()
    {
        IReadOnlySet<string> empty = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in new[] { "CodeAlta.Plugins.Tui", "XenoAtom.Terminal", "xenoatom.terminal.ui", "XenoAtom.Terminal.Future" })
            Assert.AreEqual(PluginAssemblyBinding.Forbidden, PluginAuthoringPolicy.ClassifyAssembly(PluginAuthoringProfile.Neutral, name, empty));
        Assert.AreEqual(PluginAssemblyBinding.PrivateOrDefault, PluginAuthoringPolicy.ClassifyAssembly(PluginAuthoringProfile.Neutral, "XenoAtom.TerminallyUnrelated", empty));
    }

    [TestMethod]
    public void ManagedNames_TerminalRequiresHostIdentity()
    {
        IReadOnlySet<string> empty = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in new[] { "CodeAlta.Plugins.Tui", "XenoAtom.Terminal", "XenoAtom.Terminal.UI.Future" })
            Assert.AreEqual(PluginAssemblyBinding.Host, PluginAuthoringPolicy.ClassifyAssembly(PluginAuthoringProfile.Terminal, name, empty));
        var calls = new List<string>();
        string? Private(AssemblyName name) { calls.Add("private:" + name.Name); return "private"; }
        string? Default(AssemblyName name) { calls.Add("default:" + name.Name); return "host"; }
        var hostResolution = PluginAssemblyReferenceAdmission.ResolveReference(new AssemblyName("Example"), false, true, Private, Default);
        Assert.AreEqual("host", hostResolution?.Path);
        Assert.IsTrue(hostResolution?.IsDefault is true);
        CollectionAssert.AreEqual(new[] { "default:Example" }, calls);
        calls.Clear();
        var privateResolution = PluginAssemblyReferenceAdmission.ResolveReference(new AssemblyName("Example"), false, false, Private, Default);
        Assert.AreEqual("private", privateResolution?.Path);
        Assert.IsTrue(privateResolution?.IsDefault is false);
        CollectionAssert.AreEqual(new[] { "private:Example" }, calls);
        calls.Clear();
        var fallback = PluginAssemblyReferenceAdmission.ResolveReference(new AssemblyName("Example"), false, false, name => { calls.Add("missing:" + name.Name); return null; }, Default);
        Assert.AreEqual("host", fallback?.Path);
        Assert.IsTrue(fallback?.IsDefault is true);
        CollectionAssert.AreEqual(new[] { "missing:Example", "default:Example" }, calls);
        calls.Clear();
        var defaultChild = PluginAssemblyReferenceAdmission.ResolveReference(new AssemblyName("Example"), true, false, Private, Default);
        Assert.AreEqual("host", defaultChild?.Path);
        Assert.IsTrue(defaultChild?.IsDefault is true);
        CollectionAssert.AreEqual(new[] { "default:Example" }, calls);
        foreach (var profile in new[] { PluginAuthoringProfile.Neutral, PluginAuthoringProfile.Terminal })
        {
            foreach (var name in new[] { "CodeAlta.Plugins.Abstractions", "CodeAlta.Plugins.Tui", "XenoAtom.Terminal.UI", "Example.Shared" })
            {
                var reads = 0;
                Assert.ThrowsExactly<FileLoadException>(() => PluginAssemblyReferenceAdmission.ValidateClosure(
                    profile, "masquerading-entry", _ => { reads++; return new PluginAssemblyMetadata(name, []); },
                    (_, _) => throw new AssertFailedException("Reserved main identity must fail before dependency resolution."),
                    _ => throw new AssertFailedException("Main artifact is never skipped as platform."), ["Example.Shared"]));
                Assert.AreEqual(1, reads);
            }
        }
    }

    [TestMethod]
    public void ReferenceAdmission_RejectsDirectTerminalReference()
    {
        var reads = new List<string>();
        var resolutions = new List<string>();
        Assert.ThrowsExactly<FileLoadException>(() => PluginAssemblyReferenceAdmission.ValidateClosure(
            PluginAuthoringProfile.Neutral, "entry",
            path => { reads.Add(path); return new PluginAssemblyMetadata("Entry", [new AssemblyName("CodeAlta.Plugins.Tui")]); },
            (name, _) => { resolutions.Add(name.Name!); return new PluginAssemblyReferenceResolution("must-not-open", false); },
            _ => throw new AssertFailedException("Forbidden names must not reach platform admission.")));
        CollectionAssert.AreEqual(new[] { "entry" }, reads);
        Assert.AreEqual(0, resolutions.Count);
    }

    [TestMethod]
    public void ReferenceAdmission_RejectsTransitiveTerminalReference()
    {
        var reads = new List<string>();
        var resolutions = new List<string>();
        Assert.ThrowsExactly<FileLoadException>(() => PluginAssemblyReferenceAdmission.ValidateClosure(
            PluginAuthoringProfile.Neutral, "entry",
            path => { reads.Add(path); return new PluginAssemblyMetadata(path == "entry" ? "Entry" : "Intermediate", [new AssemblyName(path == "entry" ? "Intermediate" : "XenoAtom.Terminal.UI")]); },
            (name, _) => { resolutions.Add(name.Name!); return new PluginAssemblyReferenceResolution("intermediate", false); }, _ => false));
        CollectionAssert.AreEqual(new[] { "entry", "intermediate" }, reads);
        CollectionAssert.AreEqual(new[] { "Intermediate" }, resolutions);
    }

    [TestMethod]
    public void ReferenceAdmission_HandlesCyclesWithoutRepeatedReads()
    {
        var reads = new List<string>();
        PluginAssemblyReferenceAdmission.ValidateClosure(PluginAuthoringProfile.Neutral, "entry",
            path =>
            {
                reads.Add(path);
                return new PluginAssemblyMetadata(path switch { "entry" => "Entry", "system-named-app" => "System.NotAPlatformAssembly", _ => "ThirdParty" }, path == "entry"
                    ? [new AssemblyName("System.NotAPlatformAssembly"), new AssemblyName("ActualCore"), new AssemblyName("ThirdParty")]
                    : [new AssemblyName("Entry")]);
            },
            (name, _) => new PluginAssemblyReferenceResolution(name.Name switch { "Entry" => "entry", "ActualCore" => "actual-core", "ThirdParty" => "third-party", _ => "system-named-app" }, false),
            path => path == "actual-core");
        CollectionAssert.AreEquivalent(new[] { "entry", "system-named-app", "third-party" }, reads);
        Assert.AreEqual(3, reads.Count);

        // The same file can be reached in both domains. Its Default visit must not be skipped merely
        // because the private visit was clean: a private Dependency must not mask Default's terminal one.
        reads.Clear();
        var resolutions = new List<string>();
        Assert.ThrowsExactly<FileLoadException>(() => PluginAssemblyReferenceAdmission.ValidateClosure(
            PluginAuthoringProfile.Neutral, "entry",
            path =>
            {
                reads.Add(path);
                return path switch
                {
                    "entry" => new PluginAssemblyMetadata("Entry", [new AssemblyName("SharedFile"), new AssemblyName("Host")]),
                    "shared-file" => new PluginAssemblyMetadata("SharedFile", [new AssemblyName("Dependency")]),
                    "host" => new PluginAssemblyMetadata("Host", [new AssemblyName("SharedFile")]),
                    "clean-private" => new PluginAssemblyMetadata("Dependency", []),
                    "terminal-default" => new PluginAssemblyMetadata("Dependency", [new AssemblyName("XenoAtom.Terminal.UI")]),
                    _ => throw new AssertFailedException("Unexpected metadata read: " + path),
                };
            },
            (name, sourceIsDefault) => PluginAssemblyReferenceAdmission.ResolveReference(name, sourceIsDefault, name.Name == "Host",
                reference =>
                {
                    resolutions.Add("private:" + reference.Name);
                    return reference.Name == "SharedFile" ? "shared-file" : "clean-private";
                },
                reference =>
                {
                    resolutions.Add("default:" + reference.Name);
                    return reference.Name switch { "Host" => "host", "SharedFile" => "shared-file", _ => "terminal-default" };
                }), _ => false));
        CollectionAssert.AreEqual(new[] { "entry", "shared-file", "clean-private", "host", "shared-file", "terminal-default" }, reads);
        CollectionAssert.AreEqual(new[] { "private:SharedFile", "private:Dependency", "default:Host", "default:SharedFile", "default:Dependency" }, resolutions);
    }

    [TestMethod]
    public void ReferenceAdmission_RejectsUninspectablePrivateDependency()
    {
        var reads = new List<string>();
        Assert.ThrowsExactly<FileLoadException>(() => PluginAssemblyReferenceAdmission.ValidateClosure(
            PluginAuthoringProfile.Neutral, "entry", path => { reads.Add(path); return new PluginAssemblyMetadata("Entry", [new AssemblyName("Missing")]); }, (_, _) => null, _ => false));
        CollectionAssert.AreEqual(new[] { "entry" }, reads);
        Assert.ThrowsExactly<FileLoadException>(() => PluginAssemblyReferenceAdmission.ValidateClosure(
            PluginAuthoringProfile.Neutral, "entry", _ => throw new FileLoadException("Literal uninspectable metadata."), (_, _) => new PluginAssemblyReferenceResolution("unused", false), _ => false));
    }

    [TestMethod]
    public void GenerationAdmission_ExcludesFailedRootsAndPreservesRequestIdentity()
    {
        var first = Request("/literal/GOOD", "first");
        var failed = Request("/literal/failed", "failed");
        var last = Request("/literal/good", "last");
        var admitted = PluginAuthoringPolicy.FilterBuildRequests([first, failed, last], ["/literal/good"]);
        Assert.AreEqual(2, admitted.Count);
        Assert.AreSame(first, admitted[0]);
        Assert.AreSame(last, admitted[1]);
    }

    [TestMethod]
    public void GenerationAdmission_AllFailedRootsProduceNoBuildRequests()
    {
        Assert.AreEqual(0, PluginAuthoringPolicy.FilterBuildRequests([Request("/literal/failed", "failed")], []).Count);
        Assert.AreEqual(0, PluginAuthoringPolicy.FilterBuildRequests([], ["/literal/good"]).Count);
    }

    private static PluginRootBuildFileOptions Options(PluginAuthoringProfile profile) => new()
    {
        AuthoringProfile = profile,
        CodeAltaExeFolder = "not used by the normalized rendering core",
        GlobalJsonContent = "{\"sdk\":{\"version\":\"10.0.100\"}}",
        PackageVersions = [new PluginPackageVersion { Include = "XenoAtom.Terminal.UI", Version = "3.9.0" }],
    };

    private static IReadOnlyList<PluginRootBuildFileGenerator.GeneratedFile> Render(PluginAuthoringProfile profile)
        => PluginRootBuildFileGenerator.CreateFilesCore(Options(profile), "/literal/host");

    private static PluginBuildRequest Request(string root, string id) => new()
    {
        Package = new SourcePluginPackage
        {
            PackageId = id,
            Root = new PluginRoot { RootPath = root, Scope = PluginScope.Global },
            PackageDirectory = root + "/" + id,
            EntryFilePath = root + "/" + id + "/plugin.cs",
        },
    };
}
