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
            typeof(ConfiguredCodexAuthentication),
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
        CollectionAssert.AreEqual(new[] { "DeleteCredentialAsync" }, codexAuthenticationMethods.Select(method => method.Name).ToArray());
        Assert.AreEqual(typeof(Task), codexAuthenticationMethods[0].ReturnType);
        CollectionAssert.AreEqual(new[]
        {
            typeof(CodeAlta.Catalog.CodeAltaProviderDocument), typeof(Func<string>), typeof(Func<string>), typeof(CancellationToken),
        }, codexAuthenticationMethods[0].GetParameters().Select(parameter => parameter.ParameterType).ToArray());
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

        // The unchanged shared manager helper is still required by browser/device login, not duplication
        // of deletion orchestration. Do not forbid all Codex manager construction in the TUI.
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
        foreach (var entry in new[] { "LoginCodexSubscriptionWithBrowserAsync", "LoginCodexSubscriptionWithDeviceCodeAsync" })
        {
            var entryStart = tui.IndexOf($"    public async Task<ProviderTestResult> {entry}(", StringComparison.Ordinal);
            Assert.IsTrue(entryStart >= 0);
            var entryEnd = tui.IndexOf("\n    }", entryStart, StringComparison.Ordinal);
            Assert.IsTrue(entryEnd > entryStart);
            StringAssert.Contains(tui[entryStart..entryEnd], "var manager = CreateCodexSubscriptionLoginManager(definition);");
            Assert.IsFalse(tui[entryStart..entryEnd].Contains("ConfiguredCodexAuthentication", StringComparison.Ordinal));
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

    // Compile-time checkout path: inspect only named source/project files, never discover profile ancestors.
    private static string SourceRoot([CallerFilePath] string sourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile)!, ".."));
}
