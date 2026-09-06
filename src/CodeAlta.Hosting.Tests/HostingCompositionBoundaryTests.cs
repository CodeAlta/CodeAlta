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
            typeof(ConfiguredCopilotAuthentication), typeof(ConfiguredXaiAuthentication),
            typeof(ConfiguredCodexAuthentication), typeof(CodexAccountMetadata),
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
        var authenticationMethods = typeof(ConfiguredCopilotAuthentication).GetMethods(
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.DeclaredOnly);
        CollectionAssert.AreEquivalent(new[]
        {
            "LoginWithDeviceCodeAsync", "DeleteCredentialAsync", "GetCredentialStatusAsync",
        }, authenticationMethods.Select(method => method.Name).ToArray());
        Assert.IsFalse(authenticationMethods.SelectMany(method => method.GetParameters()).Any(parameter => parameter.IsOptional));
        var xaiAuthenticationMethods = typeof(ConfiguredXaiAuthentication).GetMethods(
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.DeclaredOnly);
        CollectionAssert.AreEquivalent(new[]
        {
            "LoginWithBrowserAsync", "LoginWithDeviceCodeAsync", "DeleteCredentialAsync", "GetCredentialStatusAsync",
        }, xaiAuthenticationMethods.Select(method => method.Name).ToArray());
        Assert.IsFalse(xaiAuthenticationMethods.SelectMany(method => method.GetParameters()).Any(parameter => parameter.IsOptional));
        var codexAuthenticationMethods = typeof(ConfiguredCodexAuthentication).GetMethods(
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.DeclaredOnly);
        CollectionAssert.AreEquivalent(new[] { "DeleteCredentialAsync", "ReadAccountMetadataAsync", "LoginWithDeviceCodeAsync", "LoginWithBrowserAsync", "TestAuthenticationAsync" }, codexAuthenticationMethods.Select(method => method.Name).ToArray());
        var codexDeletionMethod = codexAuthenticationMethods.Single(method => method.Name == "DeleteCredentialAsync");
        Assert.AreEqual(typeof(Task), codexDeletionMethod.ReturnType);
        CollectionAssert.AreEqual(new[]
        {
            typeof(CodeAlta.Catalog.CodeAltaProviderDocument), typeof(Func<string>), typeof(Func<string>), typeof(CancellationToken),
        }, codexDeletionMethod.GetParameters().Select(parameter => parameter.ParameterType).ToArray());
        Assert.IsFalse(codexAuthenticationMethods.SelectMany(method => method.GetParameters()).Any(parameter => parameter.IsOptional));
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

    [TestMethod]
    public void CopilotAuthentication_PublicForwardingAndTuiWiringKeepConcreteWorkBehindValidatedCores()
    {
        var root = SourceRoot();
        var source = File.ReadAllText(Path.Combine(root, "CodeAlta.Hosting", "ConfiguredCopilotAuthentication.cs")).Replace("\r\n", "\n");
        var tui = File.ReadAllText(Path.Combine(root, "CodeAlta.Tui", "App", "ProviderFrontendCoordinator.cs")).Replace("\r\n", "\n");
        foreach (var (method, returnType, hasCallback) in new[]
        {
            ("LoginWithDeviceCodeAsync", "Task<CopilotDirectLoginResult>", true),
            ("DeleteCredentialAsync", "Task", false),
            ("GetCredentialStatusAsync", "Task<CopilotDirectLoginResult?>", false),
        })
        {
            var start = source.IndexOf($"    public static {returnType} {method}(", StringComparison.Ordinal);
            Assert.IsTrue(start >= 0);
            var end = source.IndexOf(';', start);
            Assert.IsTrue(end > start);
            var callbackParameter = hasCallback ? "        Func<CopilotDirectDeviceCode, CancellationToken, ValueTask> onDeviceCode,\n" : string.Empty;
            var callbackArgument = hasCallback ? "onDeviceCode, " : string.Empty;
            var expected = $"    public static {returnType} {method}(\n"
                + "        CodeAltaProviderDocument definition,\n"
                + "        Func<string> getStateRootPath,\n"
                + "        Func<string> formatInvalidProvider,\n"
                + callbackParameter
                + "        CancellationToken cancellationToken)\n"
                + $"        => {method}(\n"
                + "            definition, getStateRootPath, formatInvalidProvider,\n"
                + $"            static () => new CopilotDirectLoginManager(new HttpClient()).{method},\n"
                + $"            {callbackArgument}cancellationToken);";
            Assert.AreEqual(expected, source[start..(end + 1)]);

            // Merely passing the lambda above must not execute its constructor. The characterized
            // internal overload performs required-object/type validation before invoking the factory.
            var coreStart = source.IndexOf($"    internal static async {returnType} {method}(", StringComparison.Ordinal);
            Assert.IsTrue(coreStart >= 0);
            var coreEnd = source.IndexOf("\n    }", coreStart, StringComparison.Ordinal);
            Assert.IsTrue(coreEnd > coreStart);
            var core = source[coreStart..coreEnd];
            var callbackGuard = hasCallback ? "        ArgumentNullException.ThrowIfNull(onDeviceCode);\n" : string.Empty;
            StringAssert.Contains(core, "        ArgumentNullException.ThrowIfNull(definition);\n"
                + callbackGuard
                + "        ArgumentNullException.ThrowIfNull(getStateRootPath);\n"
                + "        ArgumentNullException.ThrowIfNull(formatInvalidProvider);\n"
                + "        ArgumentNullException.ThrowIfNull(createOperation);");
            var typeCheck = core.IndexOf("if (!string.Equals(definition.ProviderType, \"copilot\", StringComparison.Ordinal))", StringComparison.Ordinal);
            var construct = core.IndexOf("var operation = createOperation();", StringComparison.Ordinal);
            var invoke = core.IndexOf("await operation(CreateCopilotDirectLoginOptions(", StringComparison.Ordinal);
            Assert.IsTrue(typeCheck >= 0 && construct > typeCheck && invoke > construct);
            StringAssert.Contains(core, "throw new InvalidOperationException(formatInvalidProvider());");
        }

        foreach (var (entry, method) in new[]
        {
            ("LoginCopilotDirectWithBrowserAsync", "LoginWithDeviceCodeAsync"),
            ("LoginCopilotDirectWithDeviceCodeAsync", "LoginWithDeviceCodeAsync"),
            ("LogoutCopilotDirectAsync", "DeleteCredentialAsync"),
            ("TestCopilotDirectAuthenticationAsync", "GetCredentialStatusAsync"),
        })
        {
            var start = tui.IndexOf($"    public async Task<ProviderTestResult> {entry}(", StringComparison.Ordinal);
            Assert.IsTrue(start >= 0);
            var end = tui.IndexOf("\n    }", start, StringComparison.Ordinal);
            Assert.IsTrue(end > start);
            StringAssert.Contains(tui[start..end], $"await ConfiguredCopilotAuthentication.{method}(\n"
                + "            definition,\n"
                + "            GetProviderStateRootPath,\n"
                + "            static () => SR.T(\"Select a Copilot provider first.\"),");
        }

        foreach (var removed in new[]
        {
            "CopilotDirectLoginManager", "CreateCopilotDirectLoginOptions", "CopilotDirectLoginOperation",
            "CopilotDirectDeleteCredentialOperation", "CopilotDirectCredentialStatusOperation",
            "LoginCopilotDirectCoreAsync", "DeleteCopilotDirectCredentialCoreAsync", "GetCopilotDirectCredentialStatusCoreAsync",
        })
        {
            Assert.IsFalse(tui.Contains(removed, StringComparison.Ordinal));
        }

        foreach (var forbidden in new[] { "Environment.", "Process.", "SR.T(", "ConfigureAwait(false)", "Task.Run(", "ThrowIfCancellationRequested(", "new ModelProviderRegistry" })
        {
            Assert.IsFalse(source.Contains(forbidden, StringComparison.Ordinal));
        }

        var cores = source[source.IndexOf("    // Mandatory call-scoped seams:", StringComparison.Ordinal)..];
        Assert.IsFalse(cores.Contains("new CopilotDirectLoginManager", StringComparison.Ordinal));
        Assert.IsFalse(cores.Contains("new HttpClient", StringComparison.Ordinal));
        Assert.IsFalse(cores.Contains("Dispose(", StringComparison.Ordinal));
        Assert.IsFalse(cores.Contains("DisposeAsync(", StringComparison.Ordinal));
    }

    [TestMethod]
    public void XaiAuthentication_PublicForwardingAndTuiWiringKeepConcreteWorkBehindValidatedCores()
    {
        var root = SourceRoot();
        var source = File.ReadAllText(Path.Combine(root, "CodeAlta.Hosting", "ConfiguredXaiAuthentication.cs")).Replace("\r\n", "\n");
        var tui = File.ReadAllText(Path.Combine(root, "CodeAlta.Tui", "App", "ProviderFrontendCoordinator.cs")).Replace("\r\n", "\n");
        foreach (var (method, returnType, callbackType, callbackName) in new[]
        {
            ("LoginWithBrowserAsync", "Task<XaiDirectLoginResult>", "XaiDirectBrowserAuthorization", "onAuthorize"),
            ("LoginWithDeviceCodeAsync", "Task<XaiDirectLoginResult>", "XaiDirectDeviceCode", "onDeviceCode"),
            ("DeleteCredentialAsync", "Task", string.Empty, string.Empty),
            ("GetCredentialStatusAsync", "Task<XaiDirectLoginResult?>", string.Empty, string.Empty),
        })
        {
            var start = source.IndexOf($"    public static {returnType} {method}(", StringComparison.Ordinal);
            Assert.IsTrue(start >= 0);
            var end = source.IndexOf(';', start);
            Assert.IsTrue(end > start);
            var callbackParameter = callbackName.Length > 0 ? $"        Func<{callbackType}, CancellationToken, ValueTask> {callbackName},\n" : string.Empty;
            var callbackArgument = callbackName.Length > 0 ? $"{callbackName}, " : string.Empty;
            var expected = $"    public static {returnType} {method}(\n"
                + "        CodeAltaProviderDocument definition,\n"
                + "        Func<string> getStateRootPath,\n"
                + "        Func<string> formatInvalidProvider,\n"
                + callbackParameter
                + "        CancellationToken cancellationToken)\n"
                + $"        => {method}(\n"
                + "            definition, getStateRootPath, formatInvalidProvider,\n"
                + $"            static () => new XaiDirectLoginManager(new HttpClient()).{method},\n"
                + $"            {callbackArgument}cancellationToken);";
            Assert.AreEqual(expected, source[start..(end + 1)]);

            // Source-only check: passing this lambda must not construct a manager. Required-object
            // and ordinal type validation in the characterized overload precede its invocation.
            var coreStart = source.IndexOf($"    internal static async {returnType} {method}(", StringComparison.Ordinal);
            Assert.IsTrue(coreStart >= 0);
            var coreEnd = source.IndexOf("\n    }", coreStart, StringComparison.Ordinal);
            Assert.IsTrue(coreEnd > coreStart);
            var core = source[coreStart..coreEnd];
            var callbackGuard = callbackName.Length > 0 ? $"        ArgumentNullException.ThrowIfNull({callbackName});\n" : string.Empty;
            var guards = "        ArgumentNullException.ThrowIfNull(definition);\n"
                + callbackGuard
                + "        ArgumentNullException.ThrowIfNull(getStateRootPath);\n"
                + "        ArgumentNullException.ThrowIfNull(formatInvalidProvider);\n"
                + "        ArgumentNullException.ThrowIfNull(createOperation);";
            var guardStart = core.IndexOf(guards, StringComparison.Ordinal);
            var typeCheck = core.IndexOf("if (!string.Equals(definition.ProviderType, \"xai\", StringComparison.Ordinal))", StringComparison.Ordinal);
            var construct = core.IndexOf("var operation = createOperation();", StringComparison.Ordinal);
            var invoke = core.IndexOf("await operation(CreateXaiDirectLoginOptions(", StringComparison.Ordinal);
            Assert.IsTrue(guardStart >= 0 && typeCheck > guardStart + guards.Length && construct > typeCheck && invoke > construct);
            StringAssert.Contains(core, "throw new InvalidOperationException(formatInvalidProvider());");
        }

        foreach (var (entry, method, callback) in new[]
        {
            ("LoginXaiDirectWithBrowserAsync", "LoginWithBrowserAsync", "            (authorization, _) => ReportXaiDirectBrowserAuthorization(authorization, reportStatus, TryOpenBrowser),\n"),
            ("LoginXaiDirectWithDeviceCodeAsync", "LoginWithDeviceCodeAsync", "            (deviceCode, _) => ReportXaiDirectDeviceCode(deviceCode, reportStatus),\n"),
            ("LogoutXaiDirectAsync", "DeleteCredentialAsync", string.Empty),
            ("TestXaiDirectAuthenticationAsync", "GetCredentialStatusAsync", string.Empty),
        })
        {
            var start = tui.IndexOf($"    public async Task<ProviderTestResult> {entry}(", StringComparison.Ordinal);
            Assert.IsTrue(start >= 0);
            var end = tui.IndexOf("\n    }", start, StringComparison.Ordinal);
            Assert.IsTrue(end > start);
            StringAssert.Contains(tui[start..end], $"await ConfiguredXaiAuthentication.{method}(\n"
                + "            definition,\n"
                + "            GetProviderStateRootPath,\n"
                + "            static () => SR.T(\"Select an xAI provider first.\"),\n"
                + callback
                + "            cancellationToken);");
        }

        foreach (var removed in new[]
        {
            "XaiDirectLoginManager", "CreateXaiDirectLoginOptions", "XaiDirectBrowserLoginOperation", "XaiDirectDeviceLoginOperation",
            "XaiDirectDeleteCredentialOperation", "XaiDirectCredentialStatusOperation",
            "LoginXaiDirectWithBrowserCoreAsync", "LoginXaiDirectWithDeviceCodeCoreAsync", "DeleteXaiDirectCredentialCoreAsync", "GetXaiDirectCredentialStatusCoreAsync",
        })
        {
            Assert.IsFalse(tui.Contains(removed, StringComparison.Ordinal));
        }

        foreach (var forbidden in new[]
        {
            "Environment.", "Process.", "File.", "Directory.", "Path.", "SR.T(", "ConfigureAwait(false)", "Task.Run(",
            "ThrowIfCancellationRequested(", "new ModelProviderRegistry", "XaiOAuthClient", "XaiDirectCredentialStore", "HttpListener",
            "Dispose(", "DisposeAsync(",
        })
        {
            Assert.IsFalse(source.Contains(forbidden, StringComparison.Ordinal));
        }

        var cores = source[source.IndexOf("    // Mandatory call-scoped seams:", StringComparison.Ordinal)..];
        Assert.IsFalse(cores.Contains("new XaiDirectLoginManager", StringComparison.Ordinal));
        Assert.IsFalse(cores.Contains("new HttpClient", StringComparison.Ordinal));
    }

    [TestMethod]
    public void CodexDeletion_PublicForwardingAndTuiWiringPreserveDeferredConstructionAndOtherLoginHelper()
    {
        // Source-only: these exact expressions are not executed and do not qualify real constructors/storage.
        var root = SourceRoot();
        var source = File.ReadAllText(Path.Combine(root, "CodeAlta.Hosting", "ConfiguredCodexAuthentication.cs")).Replace("\r\n", "\n");
        var tui = File.ReadAllText(Path.Combine(root, "CodeAlta.Tui", "App", "ProviderFrontendCoordinator.cs")).Replace("\r\n", "\n");
        var start = source.IndexOf("    public static Task DeleteCredentialAsync(", StringComparison.Ordinal);
        Assert.IsTrue(start >= 0);
        var end = source.IndexOf(';', start);
        Assert.IsTrue(end > start);
        var expectedPublic = "    public static Task DeleteCredentialAsync(\n"
            + "        CodeAltaProviderDocument definition,\n"
            + "        Func<string> getStateRootPath,\n"
            + "        Func<string> formatInvalidProvider,\n"
            + "        CancellationToken cancellationToken)\n"
            + "        => DeleteCredentialAsync(\n"
            + "            definition, getStateRootPath, formatInvalidProvider,\n"
            + "            static (providerDefinition, getStateRootPath) => new OpenAICodexSubscriptionLoginManager(\n"
            + "                new FileOpenAICodexSubscriptionCredentialStore(getStateRootPath()),\n"
            + "                new OpenAICodexSubscriptionOAuthClient(new HttpClient()),\n"
            + "                providerDefinition.ProviderKey).DeleteCredentialAsync,\n"
            + "            cancellationToken);";
        Assert.AreEqual(expectedPublic, source[start..(end + 1)]);

        var coreStart = source.IndexOf("    internal static async Task DeleteCredentialAsync(", StringComparison.Ordinal);
        Assert.IsTrue(coreStart >= 0);
        var coreEnd = source.IndexOf("\n    }", coreStart, StringComparison.Ordinal);
        Assert.IsTrue(coreEnd > coreStart);
        var expectedCore = "    internal static async Task DeleteCredentialAsync(\n"
            + "        CodeAltaProviderDocument definition,\n"
            + "        Func<string> getStateRootPath,\n"
            + "        Func<string> formatInvalidProvider,\n"
            + "        Func<CodeAltaProviderDocument, Func<string>, CodexSubscriptionDeleteCredentialOperation> createOperation,\n"
            + "        CancellationToken cancellationToken)\n"
            + "    {\n"
            + "        ArgumentNullException.ThrowIfNull(definition);\n"
            + "        ArgumentNullException.ThrowIfNull(getStateRootPath);\n"
            + "        ArgumentNullException.ThrowIfNull(formatInvalidProvider);\n"
            + "        ArgumentNullException.ThrowIfNull(createOperation);\n\n"
            + "        if (!string.Equals(definition.ProviderType, \"codex\", StringComparison.Ordinal))\n"
            + "        {\n"
            + "            throw new InvalidOperationException(formatInvalidProvider());\n"
            + "        }\n\n"
            + "        var operation = createOperation(definition, getStateRootPath);\n"
            + "        await operation(cancellationToken);";
        Assert.AreEqual(expectedCore, source[coreStart..coreEnd]);
        StringAssert.Contains(source, "internal delegate ValueTask CodexSubscriptionDeleteCredentialOperation(CancellationToken cancellationToken);");

        var logoutStart = tui.IndexOf("    public async Task<ProviderTestResult> LogoutCodexSubscriptionAsync(", StringComparison.Ordinal);
        Assert.IsTrue(logoutStart >= 0);
        var logoutEnd = tui.IndexOf("\n    }", logoutStart, StringComparison.Ordinal);
        Assert.IsTrue(logoutEnd > logoutStart);
        var expectedLogout = "    public async Task<ProviderTestResult> LogoutCodexSubscriptionAsync(\n"
            + "        CodeAltaProviderDocument definition,\n"
            + "        CancellationToken cancellationToken = default)\n"
            + "    {\n"
            + "        ArgumentNullException.ThrowIfNull(definition);\n\n"
            + "        await ConfiguredCodexAuthentication.DeleteCredentialAsync(\n"
            + "            definition,\n"
            + "            GetProviderStateRootPath,\n"
            + "            static () => SR.T(\"Select a Codex provider first.\"),\n"
            + "            cancellationToken);\n"
            + "        return new ProviderTestResult(true, SR.T(\"Deleted CodeAlta-owned ChatGPT/Codex credentials for this provider.\"), 0);";
        Assert.AreEqual(expectedLogout, tui[logoutStart..logoutEnd]);
        Assert.IsFalse(tui.Contains("DeleteCodexSubscriptionCredentialCoreAsync", StringComparison.Ordinal));
        Assert.IsFalse(tui.Contains("CodexSubscriptionDeleteCredentialOperation", StringComparison.Ordinal));

        // Preserve the original shared manager helper even though browser's factory is now in Hosting.
        // The unused helper is protected; do not forbid all Codex manager construction in the TUI.
        var expectedHelper = "    private OpenAICodexSubscriptionLoginManager CreateCodexSubscriptionLoginManager(CodeAltaProviderDocument definition)\n"
            + "    {\n"
            + "        if (!string.Equals(definition.ProviderType, \"codex\", StringComparison.Ordinal))\n"
            + "        {\n"
            + "            throw new InvalidOperationException(SR.T(\"Select a Codex provider first.\"));\n"
            + "        }\n\n"
            + "        return new OpenAICodexSubscriptionLoginManager(\n"
            + "            new FileOpenAICodexSubscriptionCredentialStore(GetProviderStateRootPath()),\n"
            + "            new OpenAICodexSubscriptionOAuthClient(new HttpClient()),\n"
            + "            definition.ProviderKey);\n"
            + "    }";
        StringAssert.Contains(tui, expectedHelper);
        foreach (var entry in new[] { "LoginCodexSubscriptionWithBrowserAsync" })
        {
            var entryStart = tui.IndexOf($"    public async Task<ProviderTestResult> {entry}(", StringComparison.Ordinal);
            Assert.IsTrue(entryStart >= 0);
            var entryEnd = tui.IndexOf("\n    }", entryStart, StringComparison.Ordinal);
            Assert.IsTrue(entryEnd > entryStart);
            StringAssert.Contains(tui[entryStart..entryEnd], "await ConfiguredCodexAuthentication.LoginWithBrowserAsync(\n"
                + "            definition,\n"
                + "            GetProviderStateRootPath,\n"
                + "            static () => SR.T(\"Select a Codex provider first.\"),\n"
                + "            authorizeUri => ReportCodexBrowserAuthorization(authorizeUri, reportStatus),\n"
                + "            TryOpenBrowser,\n"
                + "            static () => SR.T(\"ChatGPT browser login completed\"),\n"
                + "            (prefix, rawAccountId) => result = FormatCodexBrowserLoginResult(prefix, rawAccountId),\n"
                + "            cancellationToken);\n"
                + "        return result;");
            Assert.IsFalse(tui[entryStart..entryEnd].Contains("CreateCodexSubscriptionLoginManager(definition)", StringComparison.Ordinal));
            Assert.IsFalse(tui[entryStart..entryEnd].Contains("new OpenAICodexSubscriptionLoginManager", StringComparison.Ordinal));
        }

        foreach (var forbidden in new[]
        {
            "Environment.", "Process.", "File.", "Directory.", "Path.", "SR.T(", "ConfigureAwait(false)", "Task.Run(",
            "ThrowIfCancellationRequested(", "Dispose(", "DisposeAsync(",
        })
        {
            Assert.IsFalse(source.Contains(forbidden, StringComparison.Ordinal));
        }
    }

    [TestMethod]
    public void CodexAccountMetadata_PublicShapeAndSourceWiringPreservePostLoadProjectionAndSynchronousPresentation()
    {
        // Type metadata and named checkout sources only; no operation or provider constructor is executed.
        var method = typeof(ConfiguredCodexAuthentication).GetMethods(
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.DeclaredOnly)
            .Single(candidate => candidate.Name == "ReadAccountMetadataAsync");
        Assert.AreEqual(typeof(Task), method.ReturnType);
        CollectionAssert.AreEqual(new[]
        {
            typeof(CodeAlta.Catalog.CodeAltaProviderDocument), typeof(Func<string>), typeof(Action<CodexAccountMetadata>), typeof(CancellationToken),
        }, method.GetParameters().Select(parameter => parameter.ParameterType).ToArray());
        Assert.IsFalse(method.GetParameters().Any(parameter => parameter.IsOptional));
        Assert.IsTrue(typeof(CodexAccountMetadata).IsSealed);
        var properties = typeof(CodexAccountMetadata).GetProperties(
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly);
        CollectionAssert.AreEquivalent(new[] { "AccountId", "AccountLabel" }, properties.Select(property => property.Name).ToArray());
        var nullability = new System.Reflection.NullabilityInfoContext();
        foreach (var property in properties)
        {
            Assert.AreEqual(typeof(string), property.PropertyType);
            Assert.AreEqual(System.Reflection.NullabilityState.Nullable, nullability.Create(property).ReadState);
        }
        var constructor = typeof(CodexAccountMetadata).GetConstructors().Single();
        CollectionAssert.AreEqual(new[] { typeof(string), typeof(string) }, constructor.GetParameters().Select(parameter => parameter.ParameterType).ToArray());
        CollectionAssert.AreEqual(new[] { "AccountId", "AccountLabel" }, constructor.GetParameters().Select(parameter => parameter.Name).ToArray());
        foreach (var parameter in constructor.GetParameters())
        {
            Assert.IsFalse(parameter.IsOptional);
            Assert.AreEqual(System.Reflection.NullabilityState.Nullable, nullability.Create(parameter).ReadState);
        }

        var root = SourceRoot();
        var source = File.ReadAllText(Path.Combine(root, "CodeAlta.Hosting", "ConfiguredCodexAuthentication.cs")).Replace("\r\n", "\n");
        var tui = File.ReadAllText(Path.Combine(root, "CodeAlta.Tui", "App", "ProviderFrontendCoordinator.cs")).Replace("\r\n", "\n");
        var start = source.IndexOf("    public static Task ReadAccountMetadataAsync(", StringComparison.Ordinal);
        Assert.IsTrue(start >= 0);
        var end = source.IndexOf("\n    internal static async Task ReadAccountMetadataAsync(", start, StringComparison.Ordinal);
        Assert.IsTrue(end > start);
        var expectedPublic = """
            public static Task ReadAccountMetadataAsync(
                CodeAltaProviderDocument definition,
                Func<string> getStateRootPath,
                Action<CodexAccountMetadata?> onMetadata,
                CancellationToken cancellationToken)
                => ReadAccountMetadataAsync(
                    definition, getStateRootPath, onMetadata,
                    static (providerDefinition, getStateRootPath) =>
                    {
                        var store = new FileOpenAICodexSubscriptionCredentialStore(getStateRootPath());
                        return async (onMetadata, token) =>
                        {
                            var credential = await store.LoadAsync(providerDefinition.ProviderKey, token);
                            if (credential is null)
                            {
                                onMetadata(null);
                                return;
                            }

                            var accountId = OpenAICodexSubscriptionAuthManager.ResolveAccountId(providerDefinition.AccountId, credential);
                            var accountLabel = credential.AccountLabel;
                            onMetadata(new CodexAccountMetadata(accountId, accountLabel));
                        };
                    },
                    cancellationToken);
        """.Replace("\r\n", "\n");
        Assert.AreEqual(expectedPublic + "\n", source[start..end]);
        var coreEnd = source.IndexOf("\n    }", end, StringComparison.Ordinal);
        Assert.IsTrue(coreEnd > end);
        var expectedCore = """
            internal static async Task ReadAccountMetadataAsync(
                CodeAltaProviderDocument definition,
                Func<string> getStateRootPath,
                Action<CodexAccountMetadata?> onMetadata,
                Func<CodeAltaProviderDocument, Func<string>, CodexAccountLookupOperation> createOperation,
                CancellationToken cancellationToken)
            {
                ArgumentNullException.ThrowIfNull(definition);
                ArgumentNullException.ThrowIfNull(getStateRootPath);
                ArgumentNullException.ThrowIfNull(onMetadata);
                ArgumentNullException.ThrowIfNull(createOperation);

                var operation = createOperation(definition, getStateRootPath);
                await operation(onMetadata, cancellationToken);
        """.Replace("\r\n", "\n");
        Assert.AreEqual(expectedCore, source[(end + 1)..coreEnd]);
        StringAssert.Contains(source, "public sealed record CodexAccountMetadata(string? AccountId, string? AccountLabel);");
        StringAssert.Contains(source, "internal delegate ValueTask CodexAccountLookupOperation(\n    Action<CodexAccountMetadata?> onMetadata,\n    CancellationToken cancellationToken);");

        var routeStart = tui.IndexOf("    public async Task<ProviderTestResult> ListCodexSubscriptionAccountsAsync(", StringComparison.Ordinal);
        Assert.IsTrue(routeStart >= 0);
        var routeEnd = tui.IndexOf("\n    }", routeStart, StringComparison.Ordinal);
        Assert.IsTrue(routeEnd > routeStart);
        var expectedRoute = """
            public async Task<ProviderTestResult> ListCodexSubscriptionAccountsAsync(
                CodeAltaProviderDocument definition,
                CancellationToken cancellationToken = default)
            {
                ArgumentNullException.ThrowIfNull(definition);

                ProviderTestResult result = default;
                await ConfiguredCodexAuthentication.ReadAccountMetadataAsync(
                    definition,
                    GetProviderStateRootPath,
                    metadata => result = FormatCodexAccountMetadataResult(metadata),
                    cancellationToken);
                return result;
        """.Replace("\r\n", "\n");
        Assert.AreEqual(expectedRoute, tui[routeStart..routeEnd]);
        var expectedFormatter = """
            internal static ProviderTestResult FormatCodexAccountMetadataResult(CodexAccountMetadata? metadata)
            {
                if (metadata is null)
                {
                    return new ProviderTestResult(false, SR.T("Login required before account/workspace metadata can be listed."), 0);
                }

                var accountId = metadata.AccountId;
                var accountLabel = string.IsNullOrWhiteSpace(metadata.AccountLabel) ? SR.T("ChatGPT account/workspace") : metadata.AccountLabel;
                var accountMessage = string.IsNullOrWhiteSpace(accountId)
                    ? SR.T("{0}: token did not expose an account/workspace id; enter one in Account/Workspace Id if required.", accountLabel)
                    : SR.T("{0}: {1}", accountLabel, accountId);
                return new ProviderTestResult(true, accountMessage, string.IsNullOrWhiteSpace(accountId) ? 0 : 1);
            }
        """.Replace("\r\n", "\n");
        StringAssert.Contains(tui, expectedFormatter);
        foreach (var removed in new[] { "ReadCodexAccountMetadataCoreAsync", "record CodexAccountMetadata", "delegate ValueTask CodexAccountLookupOperation" })
        {
            Assert.IsFalse(tui.Contains(removed, StringComparison.Ordinal));
        }
        // The preceding deletion guard retains the exact login helper and browser login caller checks.
        StringAssert.Contains(tui, "private OpenAICodexSubscriptionAuthManager CreateCodexSubscriptionAuthManager(CodeAltaProviderDocument definition)");
        StringAssert.Contains(tui, "await ConfiguredCodexAuthentication.TestAuthenticationAsync(\n            definition,\n            GetProviderStateRootPath,\n            static () => SR.T(\"Select a Codex provider first.\"),\n            rawAccountId => result = FormatCodexAuthenticationResult(rawAccountId),\n            cancellationToken);");
        StringAssert.Contains(tui, "private string GetProviderStateRootPath()\n        => _ownedServices?.CatalogOptions.GlobalRoot\n           ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), \".alta\");");
        var adapter = File.ReadAllText(Path.Combine(root, "CodeAlta.Tui", "App", "IModelProviderDialogService.cs")).Replace("\r\n", "\n");
        StringAssert.Contains(adapter, "public Task<ProviderTestResult> ListAccountsAsync(CodeAltaProviderDocument definition, CancellationToken cancellationToken = default)\n        => _providerUi.ListCodexSubscriptionAccountsAsync(definition, cancellationToken);");
        var dialog = File.ReadAllText(Path.Combine(root, "CodeAlta.Tui", "Views", "ModelProvidersDialog.cs")).Replace("\r\n", "\n");
        StringAssert.Contains(dialog, "definition => _modelProviders.ListAccountsAsync(definition)");
        StringAssert.Contains(dialog, "canCancel: false,\n            (definition, _, _) => actionAsync(definition));");
    }

    [TestMethod]
    public void CodexDeviceLogin_HostingWiringPreservesExactFactoryCallbacksPresentationAndOtherRoutes()
    {
        // Type metadata and four named checkout sources only; no provider constructor or operation executes.
        var method = typeof(ConfiguredCodexAuthentication).GetMethods(
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.DeclaredOnly)
            .Single(candidate => candidate.Name == "LoginWithDeviceCodeAsync");
        Assert.AreEqual(typeof(Task), method.ReturnType);
        var parameters = method.GetParameters();
        CollectionAssert.AreEqual(new[]
        {
            typeof(CodeAlta.Catalog.CodeAltaProviderDocument), typeof(Func<string>), typeof(Func<string>),
            typeof(Action<string, string>), typeof(Func<string>), typeof(Action<string, string>), typeof(CancellationToken),
        }, parameters.Select(parameter => parameter.ParameterType).ToArray());
        CollectionAssert.AreEqual(new[]
        {
            "definition", "getStateRootPath", "formatInvalidProvider", "reportDeviceCode",
            "formatCompletionPrefix", "onCompleted", "cancellationToken",
        }, parameters.Select(parameter => parameter.Name).ToArray());
        Assert.IsFalse(parameters.Any(parameter => parameter.IsOptional));
        var completionNullability = new System.Reflection.NullabilityInfoContext().Create(parameters[5]);
        Assert.AreEqual(System.Reflection.NullabilityState.NotNull, completionNullability.ReadState);
        Assert.AreEqual(System.Reflection.NullabilityState.NotNull, completionNullability.GenericTypeArguments[0].ReadState);
        Assert.AreEqual(System.Reflection.NullabilityState.Nullable, completionNullability.GenericTypeArguments[1].ReadState);
        var root = SourceRoot();
        var source = File.ReadAllText(Path.Combine(root, "CodeAlta.Hosting", "ConfiguredCodexAuthentication.cs")).Replace("\r\n", "\n");
        var tui = File.ReadAllText(Path.Combine(root, "CodeAlta.Tui", "App", "ProviderFrontendCoordinator.cs")).Replace("\r\n", "\n");
        var adapter = File.ReadAllText(Path.Combine(root, "CodeAlta.Tui", "App", "IModelProviderDialogService.cs")).Replace("\r\n", "\n");
        var dialog = File.ReadAllText(Path.Combine(root, "CodeAlta.Tui", "Views", "ModelProvidersDialog.cs")).Replace("\r\n", "\n");
        var device = ReadMethod(tui, "    public async Task<ProviderTestResult> LoginCodexSubscriptionWithDeviceCodeAsync(");
        var expectedDevice = """
            public async Task<ProviderTestResult> LoginCodexSubscriptionWithDeviceCodeAsync(
                CodeAltaProviderDocument definition,
                Action<string> reportStatus,
                CancellationToken cancellationToken = default)
            {
                ArgumentNullException.ThrowIfNull(definition);
                ArgumentNullException.ThrowIfNull(reportStatus);

                ProviderTestResult result = default;
                await ConfiguredCodexAuthentication.LoginWithDeviceCodeAsync(
                    definition,
                    GetProviderStateRootPath,
                    static () => SR.T("Select a Codex provider first."),
                    (verificationUri, userCode) => ReportCodexDeviceCode(verificationUri, userCode, reportStatus),
                    static () => SR.T("ChatGPT device-code login completed"),
                    (prefix, rawAccountId) => result = FormatCodexDeviceLoginResult(prefix, rawAccountId),
                    cancellationToken);
                return result;
            }
        """.Replace("\r\n", "\n");
        Assert.AreEqual(expectedDevice, device);

        var publicStart = source.IndexOf("    public static Task LoginWithDeviceCodeAsync(", StringComparison.Ordinal);
        Assert.IsTrue(publicStart >= 0);
        var coreStart = source.IndexOf("\n    internal static async Task LoginWithDeviceCodeAsync(", publicStart, StringComparison.Ordinal);
        Assert.IsTrue(coreStart > publicStart);
        var publicDevice = source[publicStart..coreStart];
        var expectedPublic = """
            public static Task LoginWithDeviceCodeAsync(
                CodeAltaProviderDocument definition,
                Func<string> getStateRootPath,
                Func<string> formatInvalidProvider,
                Action<string, string> reportDeviceCode,
                Func<string> formatCompletionPrefix,
                Action<string, string?> onCompleted,
                CancellationToken cancellationToken)
                => LoginWithDeviceCodeAsync(
                    definition, getStateRootPath, formatInvalidProvider, reportDeviceCode, formatCompletionPrefix, onCompleted,
                    static (providerDefinition, getStateRootPath) =>
                    {
                        var manager = new OpenAICodexSubscriptionLoginManager(
                            new FileOpenAICodexSubscriptionCredentialStore(getStateRootPath()),
                            new OpenAICodexSubscriptionOAuthClient(new HttpClient()),
                            providerDefinition.ProviderKey);
                        return async (reportDeviceCode, formatCompletionPrefix, onCompleted, token) =>
                        {
                            var credential = await manager.CompleteDeviceLoginAsync(
                                (deviceCode, _) =>
                                {
                                    reportDeviceCode(deviceCode.VerificationUri, deviceCode.UserCode);
                                    return ValueTask.CompletedTask;
                                },
                                cancellationToken: token);
                            var prefix = formatCompletionPrefix();
                            // Approved once-only raw ID capture AFTER prefix localization, not resolver output.
                            // The provider returns a fresh, unexposed credential with a plain auto-property.
                            var rawAccountId = credential.AccountId;
                            onCompleted(prefix, rawAccountId);
                        };
                    },
                    cancellationToken);
        """.Replace("\r\n", "\n");
        Assert.AreEqual(expectedPublic + "\n", publicDevice);

        // Scope the factory inside DEVICE, never select an earlier account/deletion factory.
        var factoryStart = publicDevice.IndexOf("            static (providerDefinition, getStateRootPath) =>", StringComparison.Ordinal);
        Assert.IsTrue(factoryStart >= 0);
        var factoryEnd = publicDevice.IndexOf("\n            },", factoryStart, StringComparison.Ordinal);
        Assert.IsTrue(factoryEnd > factoryStart);
        var factory = publicDevice[factoryStart..factoryEnd];
        StringAssert.Contains(factory, "var manager = new OpenAICodexSubscriptionLoginManager(\n                    new FileOpenAICodexSubscriptionCredentialStore(getStateRootPath()),\n                    new OpenAICodexSubscriptionOAuthClient(new HttpClient()),\n                    providerDefinition.ProviderKey);");
        var prefix = factory.IndexOf("var prefix = formatCompletionPrefix();", StringComparison.Ordinal);
        var projection = factory.IndexOf("var rawAccountId = credential.AccountId;", StringComparison.Ordinal);
        var completion = factory.IndexOf("onCompleted(prefix, rawAccountId);", StringComparison.Ordinal);
        Assert.IsTrue(prefix >= 0 && projection > prefix && completion > projection);
        Assert.IsFalse(factory[prefix..completion].Contains("await ", StringComparison.Ordinal));
        Assert.AreEqual(projection + "var rawAccountId = ".Length, factory.IndexOf("credential.AccountId", StringComparison.Ordinal));
        Assert.AreEqual(-1, factory.IndexOf("credential.AccountId", projection + "var rawAccountId = credential.AccountId".Length, StringComparison.Ordinal));
        foreach (var forbidden in new[]
        {
            "ResolveAccountId", "AccountLabel", "CodexAccountMetadata", "DeviceAuthId", "timeProvider:",
            "AuthSource", "Enabled", "definition.AccountId", "providerDefinition.AccountId", "new Uri(",
            "TryOpenBrowser", "ConfigureAwait(", "Task.Run(",
            "ThrowIfCancellationRequested(", "Dispose(", "DisposeAsync(", "catch",
        })
        {
            Assert.IsFalse(device.Contains(forbidden, StringComparison.Ordinal));
            Assert.IsFalse(factory.Contains(forbidden, StringComparison.Ordinal));
        }
        Assert.IsFalse(factory.Contains("ConfiguredCodexAuthentication", StringComparison.Ordinal));
        foreach (var removed in new[] { "LoginCodexDeviceCoreAsync", "CodexDeviceLoginOperation", "CompleteDeviceLoginAsync" })
        {
            Assert.IsFalse(tui.Contains(removed, StringComparison.Ordinal));
        }
        foreach (var concrete in new[] { "OpenAICodexSubscriptionLoginManager", "FileOpenAICodexSubscriptionCredentialStore", "OpenAICodexSubscriptionOAuthClient", "HttpClient", "credential." })
        {
            Assert.IsFalse(device.Contains(concrete, StringComparison.Ordinal));
        }

        var expectedCore = """
            internal static async Task LoginWithDeviceCodeAsync(
                CodeAltaProviderDocument definition,
                Func<string> getStateRootPath,
                Func<string> formatInvalidProvider,
                Action<string, string> reportDeviceCode,
                Func<string> formatCompletionPrefix,
                Action<string, string?> onCompleted,
                Func<CodeAltaProviderDocument, Func<string>, CodexDeviceLoginOperation> createOperation,
                CancellationToken cancellationToken)
            {
                ArgumentNullException.ThrowIfNull(definition);
                ArgumentNullException.ThrowIfNull(getStateRootPath);
                ArgumentNullException.ThrowIfNull(formatInvalidProvider);
                ArgumentNullException.ThrowIfNull(reportDeviceCode);
                ArgumentNullException.ThrowIfNull(formatCompletionPrefix);
                ArgumentNullException.ThrowIfNull(onCompleted);
                ArgumentNullException.ThrowIfNull(createOperation);

                if (!string.Equals(definition.ProviderType, "codex", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(formatInvalidProvider());
                }

                var operation = createOperation(definition, getStateRootPath);
                await operation(reportDeviceCode, formatCompletionPrefix, onCompleted, cancellationToken);
            }
        """.Replace("\r\n", "\n");
        Assert.AreEqual(expectedCore, ReadMethod(source, "    internal static async Task LoginWithDeviceCodeAsync("));
        StringAssert.Contains(source, "internal delegate ValueTask CodexDeviceLoginOperation(\n    Action<string, string> reportDeviceCode,\n    Func<string> formatCompletionPrefix,\n    Action<string, string?> onCompleted,\n    CancellationToken cancellationToken);");
        StringAssert.Contains(tui, "    internal static void ReportCodexDeviceCode(string verificationUri, string userCode, Action<string> reportStatus)\n        => reportStatus(SR.T(\"Open {0} and enter code {1}. Waiting for ChatGPT authorization...\", verificationUri, userCode));");
        var expectedCompletion = """
            internal static ProviderTestResult FormatCodexDeviceLoginResult(string prefix, string? rawAccountId)
            {
                var account = string.IsNullOrWhiteSpace(rawAccountId) ? SR.T("account/workspace unknown") : rawAccountId;
                return new ProviderTestResult(true, SR.T("{0} · account/workspace: {1}.", prefix, account), 0);
            }
        """.Replace("\r\n", "\n");
        Assert.AreEqual(expectedCompletion, ReadMethod(tui, "    internal static ProviderTestResult FormatCodexDeviceLoginResult("));

        var expectedBrowser = """
            public async Task<ProviderTestResult> LoginCodexSubscriptionWithBrowserAsync(
                CodeAltaProviderDocument definition,
                Action<string> reportStatus,
                CancellationToken cancellationToken = default)
            {
                ArgumentNullException.ThrowIfNull(definition);
                ArgumentNullException.ThrowIfNull(reportStatus);

                ProviderTestResult result = default;
                await ConfiguredCodexAuthentication.LoginWithBrowserAsync(
                    definition,
                    GetProviderStateRootPath,
                    static () => SR.T("Select a Codex provider first."),
                    authorizeUri => ReportCodexBrowserAuthorization(authorizeUri, reportStatus),
                    TryOpenBrowser,
                    static () => SR.T("ChatGPT browser login completed"),
                    (prefix, rawAccountId) => result = FormatCodexBrowserLoginResult(prefix, rawAccountId),
                    cancellationToken);
                return result;
            }
        """.Replace("\r\n", "\n");
        Assert.AreEqual(expectedBrowser, ReadMethod(tui, "    public async Task<ProviderTestResult> LoginCodexSubscriptionWithBrowserAsync("));
        var expectedHelper = """
            private OpenAICodexSubscriptionLoginManager CreateCodexSubscriptionLoginManager(CodeAltaProviderDocument definition)
            {
                if (!string.Equals(definition.ProviderType, "codex", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(SR.T("Select a Codex provider first."));
                }

                return new OpenAICodexSubscriptionLoginManager(
                    new FileOpenAICodexSubscriptionCredentialStore(GetProviderStateRootPath()),
                    new OpenAICodexSubscriptionOAuthClient(new HttpClient()),
                    definition.ProviderKey);
            }
        """.Replace("\r\n", "\n");
        Assert.AreEqual(expectedHelper, ReadMethod(tui, "    private OpenAICodexSubscriptionLoginManager CreateCodexSubscriptionLoginManager("));
        var expectedFormatter = """
            private static string FormatCodexCredentialMessage(string prefix, OpenAICodexSubscriptionCredential credential)
            {
                var account = string.IsNullOrWhiteSpace(credential.AccountId) ? SR.T("account/workspace unknown") : credential.AccountId;
                return SR.T("{0} · account/workspace: {1}.", prefix, account);
            }
        """.Replace("\r\n", "\n");
        Assert.AreEqual(expectedFormatter, ReadMethod(tui, "    private static string FormatCodexCredentialMessage("));
        StringAssert.Contains(tui, "    private string GetProviderStateRootPath()\n        => _ownedServices?.CatalogOptions.GlobalRoot\n           ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), \".alta\");");

        var expectedAdapter = """
            public Task<ProviderTestResult> LoginWithDeviceCodeAsync(CodeAltaProviderDocument definition, Action<string> reportStatus, CancellationToken cancellationToken = default)
                => definition.ProviderType switch
                {
                    "copilot" => _providerUi.LoginCopilotDirectWithDeviceCodeAsync(definition, reportStatus, cancellationToken),
                    "xai" => _providerUi.LoginXaiDirectWithDeviceCodeAsync(definition, reportStatus, cancellationToken),
                    _ => _providerUi.LoginCodexSubscriptionWithDeviceCodeAsync(definition, reportStatus, cancellationToken),
                };
        """.Replace("\r\n", "\n");
        StringAssert.Contains(adapter, expectedAdapter);
        StringAssert.Contains(dialog, "                CreateCancelableProviderActionButton(\n                    item,\n                    SR.T(\"Device Login\"),\n                    SR.T(\"Cancel Device Login\"),\n                    ProviderDialogOperationKind.CodexDeviceLogin,\n                    SR.T(\"start ChatGPT device-code login\"),\n                    SR.T(\"Requesting ChatGPT device code...\"),\n                    _modelProviders.LoginWithDeviceCodeAsync),");
        // Reporting returns after queuing dispatcher work, not after the prompt has rendered.
        StringAssert.Contains(dialog, "        QueueBackgroundOperation(\n            cancellationToken => actionAsync(\n                definition,\n                message => _ = _dialog.Dispatcher.InvokeAsync(\n                    () =>\n                    {\n                        CaptureActiveLoginDetails(message);\n                        SetStatus($\"[primary]{AnsiMarkup.Escape(message)}[/]\");\n                    }),\n                cancellationToken),");
        StringAssert.Contains(dialog, "                if (ex is OperationCanceledException || ex.GetBaseException() is OperationCanceledException)\n                {\n                    SetStatus($\"[warning]{SR.T(\"Provider operation canceled.\")}[/]\");\n                    return;\n                }");

        static string ReadMethod(string source, string signature)
        {
            var start = source.IndexOf(signature, StringComparison.Ordinal);
            Assert.IsTrue(start >= 0);
            Assert.AreEqual(-1, source.IndexOf(signature, start + signature.Length, StringComparison.Ordinal));
            var end = source.IndexOf("\n    }", start, StringComparison.Ordinal);
            Assert.IsTrue(end > start);
            return source[start..(end + "\n    }".Length)];
        }
    }

    [TestMethod]
    public void CodexBrowserLogin_HostingWiringPreservesExactFactoryAndProtectedBoundaries()
    {
        // Type metadata and four named checkout sources only. No concrete Begin/listener/protocol execution or qualification.
        // The preceding device guard also compares the complete browser entry and original shared helpers.
        var method = typeof(ConfiguredCodexAuthentication).GetMethods(
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.DeclaredOnly)
            .Single(candidate => candidate.Name == "LoginWithBrowserAsync");
        Assert.AreEqual(typeof(Task), method.ReturnType);
        var parameters = method.GetParameters();
        CollectionAssert.AreEqual(new[]
        {
            typeof(CodeAlta.Catalog.CodeAltaProviderDocument), typeof(Func<string>), typeof(Func<string>),
            typeof(Action<Uri>), typeof(Action<Uri>), typeof(Func<string>), typeof(Action<string, string>), typeof(CancellationToken),
        }, parameters.Select(parameter => parameter.ParameterType).ToArray());
        CollectionAssert.AreEqual(new[]
        {
            "definition", "getStateRootPath", "formatInvalidProvider", "reportAuthorization", "openBrowser",
            "formatCompletionPrefix", "onCompleted", "cancellationToken",
        }, parameters.Select(parameter => parameter.Name).ToArray());
        Assert.IsFalse(parameters.Any(parameter => parameter.IsOptional));
        var nullability = new System.Reflection.NullabilityInfoContext();
        foreach (var parameter in parameters.Take(7))
        {
            Assert.AreEqual(System.Reflection.NullabilityState.NotNull, nullability.Create(parameter).ReadState);
        }
        Assert.AreEqual(System.Reflection.NullabilityState.NotNull, nullability.Create(parameters[3]).GenericTypeArguments[0].ReadState);
        Assert.AreEqual(System.Reflection.NullabilityState.NotNull, nullability.Create(parameters[4]).GenericTypeArguments[0].ReadState);
        var completionNullability = nullability.Create(parameters[6]);
        Assert.AreEqual(System.Reflection.NullabilityState.NotNull, completionNullability.GenericTypeArguments[0].ReadState);
        Assert.AreEqual(System.Reflection.NullabilityState.Nullable, completionNullability.GenericTypeArguments[1].ReadState);
        var root = SourceRoot();
        var tui = File.ReadAllText(Path.Combine(root, "CodeAlta.Tui", "App", "ProviderFrontendCoordinator.cs")).Replace("\r\n", "\n");
        var source = File.ReadAllText(Path.Combine(root, "CodeAlta.Hosting", "ConfiguredCodexAuthentication.cs")).Replace("\r\n", "\n");
        var adapter = File.ReadAllText(Path.Combine(root, "CodeAlta.Tui", "App", "IModelProviderDialogService.cs")).Replace("\r\n", "\n");
        var dialog = File.ReadAllText(Path.Combine(root, "CodeAlta.Tui", "Views", "ModelProvidersDialog.cs")).Replace("\r\n", "\n");
        var browser = ReadMethod(tui, "    public async Task<ProviderTestResult> LoginCodexSubscriptionWithBrowserAsync(");
        StringAssert.Contains(browser, "    public async Task<ProviderTestResult> LoginCodexSubscriptionWithBrowserAsync(\n"
            + "        CodeAltaProviderDocument definition,\n"
            + "        Action<string> reportStatus,\n"
            + "        CancellationToken cancellationToken = default)\n"
            + "    {\n"
            + "        ArgumentNullException.ThrowIfNull(definition);\n"
            + "        ArgumentNullException.ThrowIfNull(reportStatus);");
        StringAssert.Contains(browser, "            cancellationToken);\n        return result;\n    }");

        var publicStart = source.IndexOf("    public static Task LoginWithBrowserAsync(", StringComparison.Ordinal);
        Assert.IsTrue(publicStart >= 0);
        var coreStart = source.IndexOf("\n    internal static async Task LoginWithBrowserAsync(", publicStart, StringComparison.Ordinal);
        Assert.IsTrue(coreStart > publicStart);
        var publicBrowser = source[publicStart..coreStart];
        var expectedPublic = """
            public static Task LoginWithBrowserAsync(
                CodeAltaProviderDocument definition,
                Func<string> getStateRootPath,
                Func<string> formatInvalidProvider,
                Action<Uri> reportAuthorization,
                Action<Uri> openBrowser,
                Func<string> formatCompletionPrefix,
                Action<string, string?> onCompleted,
                CancellationToken cancellationToken)
                => LoginWithBrowserAsync(
                    definition, getStateRootPath, formatInvalidProvider, reportAuthorization, openBrowser, formatCompletionPrefix, onCompleted,
                    static (providerDefinition, getStateRootPath) =>
                    {
                        var manager = new OpenAICodexSubscriptionLoginManager(
                            new FileOpenAICodexSubscriptionCredentialStore(getStateRootPath()),
                            new OpenAICodexSubscriptionOAuthClient(new HttpClient()),
                            providerDefinition.ProviderKey);
                        return async (reportAuthorization, openBrowser, formatCompletionPrefix, onCompleted, token) =>
                        {
                            var login = manager.BeginBrowserLogin(providerDefinition.AccountId);
                            var waitForCallbackTask = manager.WaitForBrowserCallbackAsync(login, token).AsTask();
                            reportAuthorization(login.AuthorizeUri);
                            openBrowser(login.AuthorizeUri);
                            var credential = await waitForCallbackTask;
                            var prefix = formatCompletionPrefix();
                            // Approved browser-specific post-prefix snapshot: the nonblank branch now reads once,
                            // not twice. The fresh sealed credential has a plain property and is saved before return.
                            // This is not arbitrary-property or task identity/stack/settlement equivalence.
                            var rawAccountId = credential.AccountId;
                            onCompleted(prefix, rawAccountId);
                        };
                    },
                    cancellationToken);
        """.Replace("\r\n", "\n");
        Assert.AreEqual(expectedPublic + "\n", publicBrowser);

        // Scope the factory inside Hosting BROWSER, not an account/deletion/device or shared helper region.
        var factoryStart = publicBrowser.IndexOf("            static (providerDefinition, getStateRootPath) =>", StringComparison.Ordinal);
        Assert.IsTrue(factoryStart >= 0);
        var factoryEnd = publicBrowser.IndexOf("\n            },", factoryStart, StringComparison.Ordinal);
        Assert.IsTrue(factoryEnd > factoryStart);
        var factory = publicBrowser[factoryStart..factoryEnd];
        var expectedFactory = "                var manager = new OpenAICodexSubscriptionLoginManager(\n"
            + "                    new FileOpenAICodexSubscriptionCredentialStore(getStateRootPath()),\n"
            + "                    new OpenAICodexSubscriptionOAuthClient(new HttpClient()),\n"
            + "                    providerDefinition.ProviderKey);\n"
            + "                return async (reportAuthorization, openBrowser, formatCompletionPrefix, onCompleted, token) =>\n"
            + "                {\n"
            + "                    var login = manager.BeginBrowserLogin(providerDefinition.AccountId);\n"
            + "                    var waitForCallbackTask = manager.WaitForBrowserCallbackAsync(login, token).AsTask();\n"
            + "                    reportAuthorization(login.AuthorizeUri);\n"
            + "                    openBrowser(login.AuthorizeUri);\n"
            + "                    var credential = await waitForCallbackTask;\n"
            + "                    var prefix = formatCompletionPrefix();";
        StringAssert.Contains(factory, expectedFactory);
        Assert.AreEqual(2, factory.Split("login.AuthorizeUri", StringSplitOptions.None).Length - 1);
        Assert.AreEqual(1, factory.Split("providerDefinition.AccountId", StringSplitOptions.None).Length - 1);
        Assert.AreEqual(1, factory.Split("providerDefinition.ProviderKey", StringSplitOptions.None).Length - 1);
        var prefix = factory.IndexOf("var prefix = formatCompletionPrefix();", StringComparison.Ordinal);
        var projection = factory.IndexOf("var rawAccountId = credential.AccountId;", StringComparison.Ordinal);
        var completion = factory.IndexOf("onCompleted(prefix, rawAccountId);", StringComparison.Ordinal);
        Assert.IsTrue(prefix >= 0 && projection > prefix && completion > projection);
        Assert.IsFalse(factory[prefix..completion].Contains("await ", StringComparison.Ordinal));
        Assert.AreEqual(1, factory.Split("credential.AccountId", StringSplitOptions.None).Length - 1);
        StringAssert.Contains(factory, "var rawAccountId = credential.AccountId;\n                    onCompleted(prefix, rawAccountId);\n                };");

        var core = ReadMethod(source, "    internal static async Task LoginWithBrowserAsync(");
        var expectedCore = """
            internal static async Task LoginWithBrowserAsync(
                CodeAltaProviderDocument definition,
                Func<string> getStateRootPath,
                Func<string> formatInvalidProvider,
                Action<Uri> reportAuthorization,
                Action<Uri> openBrowser,
                Func<string> formatCompletionPrefix,
                Action<string, string?> onCompleted,
                Func<CodeAltaProviderDocument, Func<string>, CodexBrowserLoginOperation> createOperation,
                CancellationToken cancellationToken)
            {
                ArgumentNullException.ThrowIfNull(definition);
                ArgumentNullException.ThrowIfNull(getStateRootPath);
                ArgumentNullException.ThrowIfNull(formatInvalidProvider);
                ArgumentNullException.ThrowIfNull(reportAuthorization);
                ArgumentNullException.ThrowIfNull(openBrowser);
                ArgumentNullException.ThrowIfNull(formatCompletionPrefix);
                ArgumentNullException.ThrowIfNull(onCompleted);
                ArgumentNullException.ThrowIfNull(createOperation);

                if (!string.Equals(definition.ProviderType, "codex", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(formatInvalidProvider());
                }

                var operation = createOperation(definition, getStateRootPath);
                await operation(reportAuthorization, openBrowser, formatCompletionPrefix, onCompleted, cancellationToken);
            }
        """.Replace("\r\n", "\n");
        Assert.AreEqual(expectedCore, core);
        StringAssert.Contains(source, "internal delegate ValueTask CodexBrowserLoginOperation(\n    Action<Uri> reportAuthorization,\n    Action<Uri> openBrowser,\n    Func<string> formatCompletionPrefix,\n    Action<string, string?> onCompleted,\n    CancellationToken cancellationToken);");
        StringAssert.Contains(tui, "    internal static void ReportCodexBrowserAuthorization(Uri authorizeUri, Action<string> reportStatus)\n        => reportStatus(SR.T(\"Open ChatGPT login in your browser: {0}\", authorizeUri));");
        var expectedCompletion = """
            internal static ProviderTestResult FormatCodexBrowserLoginResult(string prefix, string? rawAccountId)
            {
                var account = string.IsNullOrWhiteSpace(rawAccountId) ? SR.T("account/workspace unknown") : rawAccountId;
                return new ProviderTestResult(true, SR.T("{0} · account/workspace: {1}.", prefix, account), 0);
            }
        """.Replace("\r\n", "\n");
        Assert.AreEqual(expectedCompletion, ReadMethod(tui, "    internal static ProviderTestResult FormatCodexBrowserLoginResult("));
        StringAssert.Contains(tui, "    private string GetProviderStateRootPath()\n        => _ownedServices?.CatalogOptions.GlobalRoot\n           ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), \".alta\");");
        foreach (var forbidden in new[]
        {
            "ResolveAccountId", "AccountLabel", "CodexAccountMetadata", "AuthSource", "Enabled", "new Uri(",
            "ConfigureAwait(", "Task.Run(", "ThrowIfCancellationRequested(", "Dispose(", "DisposeAsync(",
            "catch", "finally", "WhenAll(", "WaitAsync(", "Pkce", "login.State", "HttpListener", "ToString(",
        })
        {
            Assert.IsFalse(browser.Contains(forbidden, StringComparison.Ordinal));
            Assert.IsFalse(core.Contains(forbidden, StringComparison.Ordinal));
            Assert.IsFalse(factory.Contains(forbidden, StringComparison.Ordinal));
        }
        Assert.IsFalse(factory.Contains("ConfiguredCodexAuthentication", StringComparison.Ordinal));
        foreach (var absent in new[] { "LoginCodexBrowserCoreAsync", "CodexBrowserLoginOperation", "BeginBrowserLogin", "WaitForBrowserCallbackAsync" })
        {
            Assert.IsFalse(tui.Contains(absent, StringComparison.Ordinal));
        }
        foreach (var concrete in new[] { "OpenAICodexSubscriptionLoginManager", "FileOpenAICodexSubscriptionCredentialStore", "OpenAICodexSubscriptionOAuthClient", "HttpClient", "credential." })
        {
            Assert.IsFalse(browser.Contains(concrete, StringComparison.Ordinal));
            Assert.IsFalse(core.Contains(concrete, StringComparison.Ordinal));
        }
        foreach (var presentation in new[] { "SR.T(", "Process.", "Environment.", "TryOpenBrowser", "ReportCodexBrowserAuthorization", "FormatCodexBrowserLoginResult" })
        {
            Assert.IsFalse(publicBrowser.Contains(presentation, StringComparison.Ordinal));
            Assert.IsFalse(core.Contains(presentation, StringComparison.Ordinal));
        }

        var expectedAdapter = """
            public Task<ProviderTestResult> LoginWithBrowserAsync(CodeAltaProviderDocument definition, Action<string> reportStatus, CancellationToken cancellationToken = default)
                => definition.ProviderType switch
                {
                    "copilot" => _providerUi.LoginCopilotDirectWithBrowserAsync(definition, reportStatus, cancellationToken),
                    "xai" => _providerUi.LoginXaiDirectWithBrowserAsync(definition, reportStatus, cancellationToken),
                    _ => _providerUi.LoginCodexSubscriptionWithBrowserAsync(definition, reportStatus, cancellationToken),
                };
        """.Replace("\r\n", "\n");
        StringAssert.Contains(adapter, expectedAdapter);
        StringAssert.Contains(dialog, "                CreateCancelableProviderActionButton(\n                    item,\n                    SR.T(\"Browser Login\"),\n                    SR.T(\"Cancel Browser Login\"),\n                    ProviderDialogOperationKind.CodexBrowserLogin,\n                    SR.T(\"start ChatGPT browser login\"),\n                    SR.T(\"Starting ChatGPT browser login...\"),\n                    _modelProviders.LoginWithBrowserAsync),");
        StringAssert.Contains(dialog, "                    if (IsActiveOperation(operationKind))\n                    {\n                        CancelActiveOperation();\n                        return;\n                    }");
        StringAssert.Contains(dialog, "                        operationKind,\n                        canCancel: true,\n                        actionAsync);");
        // Callback return means queued dispatcher work, not that rendering or the route has settled.
        StringAssert.Contains(dialog, "        QueueBackgroundOperation(\n            cancellationToken => actionAsync(\n                definition,\n                message => _ = _dialog.Dispatcher.InvokeAsync(\n                    () =>\n                    {\n                        CaptureActiveLoginDetails(message);\n                        SetStatus($\"[primary]{AnsiMarkup.Escape(message)}[/]\");\n                    }),\n                cancellationToken),");
        var expectedOpener = """
            private static void TryOpenBrowser(Uri uri)
            {
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = uri.ToString(),
                        UseShellExecute = true,
                    });
                }
                catch (Exception)
                {
                    // Headless terminals can use the reported authorization URL instead.
                }
            }
        """.Replace("\r\n", "\n");
        Assert.AreEqual(expectedOpener, ReadMethod(tui, "    private static void TryOpenBrowser("));

        static string ReadMethod(string text, string signature)
        {
            var start = text.IndexOf(signature, StringComparison.Ordinal);
            Assert.IsTrue(start >= 0);
            Assert.AreEqual(-1, text.IndexOf(signature, start + signature.Length, StringComparison.Ordinal));
            var end = text.IndexOf("\n    }", start, StringComparison.Ordinal);
            Assert.IsTrue(end > start);
            return text[start..(end + "\n    }".Length)];
        }
    }

    [TestMethod]
    public void CodexAuthenticationTest_HostingWiringPreservesExactFactoryCorePresentationAndDialog()
    {
        // Type metadata and four named checkout sources only. These assertions prove wiring/order, not concrete
        // discovery, import, refresh, storage or protocol qualification. No provider code executes.
        var method = typeof(ConfiguredCodexAuthentication).GetMethods(
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.DeclaredOnly)
            .Single(candidate => candidate.Name == "TestAuthenticationAsync");
        Assert.AreEqual(typeof(Task), method.ReturnType);
        var parameters = method.GetParameters();
        CollectionAssert.AreEqual(new[]
        {
            typeof(CodeAlta.Catalog.CodeAltaProviderDocument), typeof(Func<string>), typeof(Func<string>),
            typeof(Action<string>), typeof(CancellationToken),
        }, parameters.Select(parameter => parameter.ParameterType).ToArray());
        CollectionAssert.AreEqual(new[]
        {
            "definition", "getStateRootPath", "formatInvalidProvider", "onAuthenticated", "cancellationToken",
        }, parameters.Select(parameter => parameter.Name).ToArray());
        Assert.IsFalse(parameters.Any(parameter => parameter.IsOptional));
        var nullability = new System.Reflection.NullabilityInfoContext();
        foreach (var parameter in parameters.Take(4))
        {
            Assert.AreEqual(System.Reflection.NullabilityState.NotNull, nullability.Create(parameter).ReadState);
        }
        Assert.AreEqual(System.Reflection.NullabilityState.Nullable, nullability.Create(parameters[3]).GenericTypeArguments[0].ReadState);
        var root = SourceRoot();
        var tui = File.ReadAllText(Path.Combine(root, "CodeAlta.Tui", "App", "ProviderFrontendCoordinator.cs")).Replace("\r\n", "\n");
        var source = File.ReadAllText(Path.Combine(root, "CodeAlta.Hosting", "ConfiguredCodexAuthentication.cs")).Replace("\r\n", "\n");
        var adapter = File.ReadAllText(Path.Combine(root, "CodeAlta.Tui", "App", "IModelProviderDialogService.cs")).Replace("\r\n", "\n");
        var dialog = File.ReadAllText(Path.Combine(root, "CodeAlta.Tui", "Views", "ModelProvidersDialog.cs")).Replace("\r\n", "\n");
        var authentication = ReadMethod(tui, "    public async Task<ProviderTestResult> TestCodexSubscriptionAuthenticationAsync(");
        var expectedAuthentication = """
            public async Task<ProviderTestResult> TestCodexSubscriptionAuthenticationAsync(
                CodeAltaProviderDocument definition,
                CancellationToken cancellationToken = default)
            {
                ArgumentNullException.ThrowIfNull(definition);

                ProviderTestResult result = default;
                await ConfiguredCodexAuthentication.TestAuthenticationAsync(
                    definition,
                    GetProviderStateRootPath,
                    static () => SR.T("Select a Codex provider first."),
                    rawAccountId => result = FormatCodexAuthenticationResult(rawAccountId),
                    cancellationToken);
                return result;
            }
        """.Replace("\r\n", "\n");
        Assert.AreEqual(expectedAuthentication, authentication);

        var publicStart = source.IndexOf("    public static Task TestAuthenticationAsync(", StringComparison.Ordinal);
        Assert.IsTrue(publicStart >= 0);
        var coreStart = source.IndexOf("\n    internal static async Task TestCodexAuthenticationCoreAsync(", publicStart, StringComparison.Ordinal);
        Assert.IsTrue(coreStart > publicStart);
        var publicAuthentication = source[publicStart..coreStart];
        var expectedPublic = """
            public static Task TestAuthenticationAsync(
                CodeAltaProviderDocument definition,
                Func<string> getStateRootPath,
                Func<string> formatInvalidProvider,
                Action<string?> onAuthenticated,
                CancellationToken cancellationToken)
                => TestCodexAuthenticationCoreAsync(
                    definition, getStateRootPath, formatInvalidProvider, onAuthenticated,
                    static (providerDefinition, getStateRootPath) =>
                    {
                        var authManager = new OpenAICodexSubscriptionAuthManager(
                            new FileOpenAICodexSubscriptionCredentialStore(getStateRootPath()),
                            new OpenAICodexSubscriptionOAuthClient(new HttpClient()),
                            providerDefinition.ProviderKey,
                            providerDefinition.AuthSource ?? "codealta_oauth",
                            providerDefinition.AccountId,
                            CodexAuthFileReader.ResolveCodexHome());
                        return async (onAuthenticated, token) =>
                        {
                            var context = await authManager.GetAccountContextAsync(token);
                            // Approved once-only read from the fresh sealed positional context.
                            // This is the provider-resolved context ID, not the raw credential ID.
                            var rawAccountId = context.AccountId;
                            onAuthenticated(rawAccountId);
                        };
                    },
                    cancellationToken);
        """.Replace("\r\n", "\n");
        Assert.AreEqual(expectedPublic + "\n", publicAuthentication);

        // Select ONLY inside authentication, not earlier browser/device/account/deletion routes.
        var factoryStart = publicAuthentication.IndexOf("            static (providerDefinition, getStateRootPath) =>", StringComparison.Ordinal);
        Assert.IsTrue(factoryStart >= 0);
        var factoryEnd = publicAuthentication.IndexOf("\n            },", factoryStart, StringComparison.Ordinal);
        Assert.IsTrue(factoryEnd > factoryStart);
        var factory = publicAuthentication[factoryStart..factoryEnd];
        var expectedConstructor = """
                        var authManager = new OpenAICodexSubscriptionAuthManager(
                            new FileOpenAICodexSubscriptionCredentialStore(getStateRootPath()),
                            new OpenAICodexSubscriptionOAuthClient(new HttpClient()),
                            providerDefinition.ProviderKey,
                            providerDefinition.AuthSource ?? "codealta_oauth",
                            providerDefinition.AccountId,
                            CodexAuthFileReader.ResolveCodexHome());
        """.Replace("\r\n", "\n");
        StringAssert.Contains(factory, expectedConstructor);
        // Evaluation reaches home for EVERY matching type/source before manager/key validation;
        // mismatch/root/store failure stops earlier. An invalid key is not a discovery barrier.
        var context = factory.IndexOf("var context = await authManager.GetAccountContextAsync(token);", StringComparison.Ordinal);
        var projection = factory.IndexOf("var rawAccountId = context.AccountId;", StringComparison.Ordinal);
        var completion = factory.IndexOf("onAuthenticated(rawAccountId);", StringComparison.Ordinal);
        Assert.IsTrue(context >= 0 && projection > context && completion > projection);
        Assert.IsFalse(factory[projection..completion].Contains("await ", StringComparison.Ordinal));
        Assert.AreEqual(projection + "var rawAccountId = ".Length, factory.IndexOf("context.AccountId", StringComparison.Ordinal));
        Assert.AreEqual(-1, factory.IndexOf("context.AccountId", projection + "var rawAccountId = context.AccountId".Length, StringComparison.Ordinal));

        var core = ReadMethod(source, "    internal static async Task TestCodexAuthenticationCoreAsync(");
        var expectedCore = """
            internal static async Task TestCodexAuthenticationCoreAsync(
                CodeAltaProviderDocument definition,
                Func<string> getStateRootPath,
                Func<string> formatInvalidProvider,
                Action<string?> onAuthenticated,
                Func<CodeAltaProviderDocument, Func<string>, CodexAuthenticationTestOperation> createOperation,
                CancellationToken cancellationToken)
            {
                ArgumentNullException.ThrowIfNull(definition);
                ArgumentNullException.ThrowIfNull(getStateRootPath);
                ArgumentNullException.ThrowIfNull(formatInvalidProvider);
                ArgumentNullException.ThrowIfNull(onAuthenticated);
                ArgumentNullException.ThrowIfNull(createOperation);

                if (!string.Equals(definition.ProviderType, "codex", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(formatInvalidProvider());
                }

                var operation = createOperation(definition, getStateRootPath);
                await operation(onAuthenticated, cancellationToken);
            }
        """.Replace("\r\n", "\n");
        Assert.AreEqual(expectedCore, core);
        StringAssert.Contains(source, "internal delegate ValueTask CodexAuthenticationTestOperation(\n    Action<string?> onAuthenticated,\n    CancellationToken cancellationToken);");
        var expectedFormatter = """
            internal static ProviderTestResult FormatCodexAuthenticationResult(string? rawAccountId)
            {
                var account = string.IsNullOrWhiteSpace(rawAccountId) ? SR.T("no account/workspace id in token") : rawAccountId;
                return new ProviderTestResult(true, SR.T("Authenticated without sending a model turn · account/workspace: {0}.", account), 0);
            }
        """.Replace("\r\n", "\n");
        Assert.AreEqual(expectedFormatter, ReadMethod(tui, "    internal static ProviderTestResult FormatCodexAuthenticationResult("));
        foreach (var forbidden in new[]
        {
            "Enabled", "Trim(", "IsNullOrWhiteSpace", "ConfigureAwait(", "Task.Run(",
            "ThrowIfCancellationRequested(", "Dispose(", "DisposeAsync(", "using var", "catch", "finally",
            "WaitAsync(", "WhenAll(", "CreateLinkedTokenSource", "ResolveAccountId", "AccountLabel",
            "CodexAccountMetadata", "formatCompletionPrefix", "TryOpenBrowser",
        })
        {
            Assert.IsFalse(authentication.Contains(forbidden, StringComparison.Ordinal));
            Assert.IsFalse(publicAuthentication.Contains(forbidden, StringComparison.Ordinal));
            Assert.IsFalse(core.Contains(forbidden, StringComparison.Ordinal));
        }
        foreach (var forbidden in new[]
        {
            "OpenAICodexSubscriptionAuthManager", "FileOpenAICodexSubscriptionCredentialStore",
            "HttpClient", "ResolveCodexHome", "getStateRootPath()", "SR.T(", "Environment.",
            "AuthSource", "AccountId", "ProviderKey",
        })
        {
            Assert.IsFalse(core.Contains(forbidden, StringComparison.Ordinal));
        }
        foreach (var concrete in new[] { "OpenAICodexSubscriptionAuthManager", "FileOpenAICodexSubscriptionCredentialStore", "HttpClient", "ResolveCodexHome", "GetAccountContextAsync" })
        {
            Assert.IsFalse(authentication.Contains(concrete, StringComparison.Ordinal));
        }
        foreach (var presentation in new[] { "SR.T(", "Process.", "Environment.", "FormatCodexAuthenticationResult" })
        {
            Assert.IsFalse(publicAuthentication.Contains(presentation, StringComparison.Ordinal));
        }
        // Hosting owns the only orchestration core; protected unused TUI manager helpers remain.
        Assert.IsFalse(tui.Contains("CodexAuthenticationTestOperation", StringComparison.Ordinal));
        Assert.IsFalse(tui.Contains("TestCodexAuthenticationCoreAsync", StringComparison.Ordinal));
        var expectedAdapter = """
            public Task<ProviderTestResult> TestAuthenticationAsync(CodeAltaProviderDocument definition, CancellationToken cancellationToken = default)
                => definition.ProviderType switch
                {
                    "copilot" => _providerUi.TestCopilotDirectAuthenticationAsync(definition, cancellationToken),
                    "xai" => _providerUi.TestXaiDirectAuthenticationAsync(definition, cancellationToken),
                    _ => _providerUi.TestCodexSubscriptionAuthenticationAsync(definition, cancellationToken),
                };
        """.Replace("\r\n", "\n");
        StringAssert.Contains(adapter, expectedAdapter);
        StringAssert.Contains(dialog, "                new Button(SR.T(\"Test Auth\"))\n                    .Tone(ControlTone.Primary)\n                    .Click(() => StartProviderAction(\n                        item,\n                        SR.T(\"test ChatGPT authentication\"),\n                        SR.T(\"Testing ChatGPT authentication without sending a model turn...\"),\n                        definition => _modelProviders.TestAuthenticationAsync(definition))),");
        var expectedDefinitionOnlyAction = """
            private void StartProviderAction(
                ModelProviderEditorItemViewModel item,
                string operationDescription,
                string progressMessage,
                Func<CodeAltaProviderDocument, Task<ProviderTestResult>> actionAsync)
            {
                StartProviderAction(
                    item,
                    operationDescription,
                    progressMessage,
                    ProviderDialogOperationKind.None,
                    canCancel: false,
                    (definition, _, _) => actionAsync(definition));
            }
        """.Replace("\r\n", "\n");
        StringAssert.Contains(dialog, expectedDefinitionOnlyAction);
        StringAssert.Contains(dialog, "var message = ex.GetBaseException().Message;");

        static string ReadMethod(string text, string signature)
        {
            var start = text.IndexOf(signature, StringComparison.Ordinal);
            Assert.IsTrue(start >= 0);
            Assert.AreEqual(-1, text.IndexOf(signature, start + signature.Length, StringComparison.Ordinal));
            var end = text.IndexOf("\n    }", start, StringComparison.Ordinal);
            Assert.IsTrue(end > start);
            return text[start..(end + "\n    }".Length)];
        }
    }

    // Compile-time checkout path: inspect only named source/project files, never discover profile ancestors.
    private static string SourceRoot([CallerFilePath] string sourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile)!, ".."));
}
