using System.Runtime.CompilerServices;
using System.Xml.Linq;

namespace CodeAlta.Hosting.Tests;

[TestClass]
public sealed class HostingCompositionBoundaryTests
{
    [TestMethod]
    public void HostingProject_ReferencesOnlyCurrentProviderCompositionDependencies()
    {
        var project = XDocument.Load(Path.Combine(SourceRoot(), "CodeAlta.Hosting", "CodeAlta.Hosting.csproj"));
        var projectNames = project.Descendants("ProjectReference")
            .Select(element => Path.GetFileNameWithoutExtension(((string)element.Attribute("Include")!).Replace('\\', '/')))
            .ToArray();
        CollectionAssert.AreEquivalent(new[]
        {
            "CodeAlta.Agent", "CodeAlta.Catalog", "CodeAlta.Agent.Anthropic", "CodeAlta.Agent.Copilot",
            "CodeAlta.Agent.GoogleGenAI", "CodeAlta.Agent.Mistral", "CodeAlta.Agent.OpenAI", "CodeAlta.Agent.Xai",
        }, projectNames);
        CollectionAssert.AreEquivalent(new[] { "Tomlyn", "XenoAtom.Logging" }, project.Descendants("PackageReference")
            .Select(element => (string)element.Attribute("Include")!).ToArray());
        Assert.AreEqual("false", project.Descendants("IsPackable").Single().Value);
        Assert.AreEqual("true", project.Descendants("IsAotCompatible").Single().Value);
        Assert.AreEqual("true", project.Descendants("GenerateDocumentationFile").Single().Value);

        var asset = project.Descendants("Content").Single();
        Assert.AreEqual("ProviderDefaults/provider_defaults.toml", (string?)asset.Attribute("TargetPath"));
        Assert.AreEqual("PreserveNewest", (string?)asset.Attribute("CopyToOutputDirectory"));
        Assert.AreEqual("PreserveNewest", (string?)asset.Attribute("CopyToPublishDirectory"));
    }

    [TestMethod]
    public void HostingAssembly_ExposesOnlyApprovedCompositionApiWithoutFrontendReferencesOrOptionalParameters()
    {
        var assembly = typeof(ConfiguredModelProviderRegistryBuilder).Assembly;
        CollectionAssert.AreEquivalent(new[]
        {
            typeof(ConfiguredModelProviderRegistryBuilder), typeof(ConfiguredProviderInspection),
            typeof(ProviderInspectionTestResult), typeof(ProviderInspectionModelListResult),
        }, assembly.GetExportedTypes());
        Assert.IsFalse(assembly.GetReferencedAssemblies().Any(reference =>
            reference.Name is "alta" or "altatui" or "CodeAlta.Tui" or "CodeAlta" ||
            reference.Name?.StartsWith("XenoAtom.Terminal", StringComparison.Ordinal) == true ||
            reference.Name?.StartsWith("NeoAstra", StringComparison.Ordinal) == true));
        var methods = typeof(ConfiguredModelProviderRegistryBuilder).GetMethods(
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.DeclaredOnly);
        Assert.AreEqual(5, methods.Length);
        Assert.IsFalse(methods.SelectMany(method => method.GetParameters()).Any(parameter => parameter.IsOptional));
        var inspectionMethods = typeof(ConfiguredProviderInspection).GetMethods(
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.DeclaredOnly);
        CollectionAssert.AreEquivalent(new[]
        {
            "TryBuildActiveProviderTestResult", "TryBuildActiveProviderModelListResult", "TestProviderAsync", "ListProviderModelsAsync",
        }, inspectionMethods.Select(method => method.Name).ToArray());
        Assert.IsFalse(inspectionMethods.SelectMany(method => method.GetParameters()).Any(parameter => parameter.IsOptional));
    }

    [TestMethod]
    public void Tui_UsesSharedBuilderAtExistingStartupRefreshAndProbeSitesWithoutDuplicateSources()
    {
        var root = SourceRoot();
        var tui = Path.Combine(root, "CodeAlta.Tui");
        Assert.IsFalse(File.Exists(Path.Combine(tui, "App", "ConfiguredModelProviderRegistryBuilder.cs")));
        Assert.IsFalse(File.Exists(Path.Combine(tui, "App", "RawApiProviderDefaultsCatalog.cs")));
        Assert.IsFalse(File.Exists(Path.Combine(tui, "ProviderDefaults", "provider_defaults.toml")));
        var project = XDocument.Load(Path.Combine(tui, "CodeAlta.Tui.csproj"));
        Assert.IsTrue(project.Descendants("ProjectReference").Any(element =>
            ((string?)element.Attribute("Include"))?.Replace('\\', '/') == "../CodeAlta.Hosting/CodeAlta.Hosting.csproj"));
        Assert.IsFalse(project.Descendants("Content").Any(element =>
            ((string?)element.Attribute("Include"))?.Contains("provider_defaults.toml", StringComparison.Ordinal) == true));

        var startup = File.ReadAllText(Path.Combine(tui, "App", "CodeAltaOwnedServices.cs"));
        StringAssert.Contains(startup, "using CodeAlta.Hosting;");
        StringAssert.Contains(startup, "ConfiguredModelProviderRegistryBuilder.RegisterConfiguredProviders(");
        StringAssert.Contains(startup, "ConfiguredModelProviderRegistryBuilder.RegisterOrReplaceConfiguredProviders(");
        var probe = File.ReadAllText(Path.Combine(tui, "App", "ProviderFrontendCoordinator.cs"));
        StringAssert.Contains(probe, "using CodeAlta.Hosting;");
        StringAssert.Contains(probe, "ConfiguredProviderInspection.TestProviderAsync(");
        StringAssert.Contains(probe, "ConfiguredProviderInspection.ListProviderModelsAsync(");
        StringAssert.Contains(probe, "ConfiguredProviderInspection.TryBuildActiveProviderTestResult(");
        StringAssert.Contains(probe, "ConfiguredProviderInspection.TryBuildActiveProviderModelListResult(");
        Assert.IsFalse(probe.Contains("TryCreateRuntime(", StringComparison.Ordinal));
        Assert.IsFalse(probe.Contains("SortModelsIfRequested(", StringComparison.Ordinal));
        Assert.IsFalse(probe.Contains("TryCreateProviderRuntime", StringComparison.Ordinal));
        Assert.IsFalse(probe.Contains("ProviderCoreAsync(", StringComparison.Ordinal));
        Assert.IsFalse(probe.Contains("ProviderModelsCoreAsync(", StringComparison.Ordinal));
        var inspection = File.ReadAllText(Path.Combine(root, "CodeAlta.Hosting", "ConfiguredProviderInspection.cs"));
        StringAssert.Contains(inspection, "ConfiguredModelProviderRegistryBuilder.TryCreateProviderRegistration(");
        StringAssert.Contains(inspection, "runtime = createRuntime();");
        Assert.IsFalse(inspection.Contains("new ModelProviderRegistry", StringComparison.Ordinal));
        Assert.IsFalse(inspection.Contains("Environment.GetFolderPath", StringComparison.Ordinal));
        Assert.IsFalse(inspection.Contains("ConfigureAwait(false)", StringComparison.Ordinal));
        foreach (var method in new[] { "TestProviderAsync", "ListProviderModelsAsync" })
        {
            // Public production overloads validate all required inputs before selecting the real factory.
            var start = inspection.IndexOf($"> {method}(", StringComparison.Ordinal);
            Assert.IsTrue(start >= 0);
            var forwarding = inspection.IndexOf($"return await {method}(", start, StringComparison.Ordinal);
            Assert.IsTrue(forwarding > start);
            var prefix = inspection[start..forwarding];
            StringAssert.Contains(prefix, "ArgumentNullException.ThrowIfNull(definition);");
            StringAssert.Contains(prefix, "ArgumentException.ThrowIfNullOrWhiteSpace(stateRootPath);");
            StringAssert.Contains(prefix, "ArgumentNullException.ThrowIfNull(formatInvalidSettings);");
            StringAssert.Contains(prefix, "ArgumentNullException.ThrowIfNull(formatSuccess);");
            StringAssert.Contains(inspection[forwarding..], $"return await {method}(definition, stateRootPath, modelCatalog, TryCreateRuntime,");
        }
    }

    // Compile-time checkout path: inspect only named source/project files, never discover profile ancestors.
    private static string SourceRoot([CallerFilePath] string sourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile)!, ".."));
}
