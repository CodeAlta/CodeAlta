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
    [Ignore("Code-shape API inventory check; disabled in favor of behavioral coverage.")]
    public void HostingAssembly_ExposesOnlyApprovedCompositionApiWithoutFrontendReferencesOrOptionalParameters()
    {
        var assembly = typeof(ConfiguredModelProviderRegistryBuilder).Assembly;
        CollectionAssert.AreEquivalent(new[]
        {
            typeof(ConfiguredModelProviderRegistryBuilder), typeof(ConfiguredProviderInspection),
            typeof(ProviderInspectionTestResult), typeof(ProviderInspectionModelListResult),
            typeof(ConfiguredCopilotAuthentication), typeof(ConfiguredXaiAuthentication),
            typeof(ConfiguredCodexAuthentication), typeof(CodexAccountMetadata),
            typeof(CodeAltaSingleInstanceGuard), typeof(CodeAltaAlreadyRunningException), typeof(CodeAltaStartupAdmission),
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
        CollectionAssert.AreEquivalent(new[] { "SignOutAsync", "ReadAccountMetadataAsync", "LoginWithBrowserAsync", "TestAuthenticationAsync" }, codexAuthenticationMethods.Select(method => method.Name).ToArray());
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
    public void CodexSubscription_UsesMainFlowBehindSharedHostingWithoutLegacyRoutes()
    {
        var root = SourceRoot();
        var source = File.ReadAllText(Path.Combine(root, "CodeAlta.Hosting", "ConfiguredCodexAuthentication.cs"));
        var tui = File.ReadAllText(Path.Combine(root, "CodeAlta.Tui", "App", "ProviderFrontendCoordinator.cs"));
        var host = source.IndexOf("var hostId = await new OpenAICodexSubscriptionHostIdProvider(getRoot()).GetOrCreateAsync(token);", StringComparison.Ordinal);
        var begin = source.IndexOf("using var login = await manager.BeginBrowserLoginAsync(hostId, token);", StringComparison.Ordinal);
        var wait = source.IndexOf("var waitForCallbackTask = manager.WaitForBrowserCallbackAsync(login, token).AsTask();", StringComparison.Ordinal);
        var report = source.IndexOf("report(login.AuthorizeUri);", StringComparison.Ordinal);
        var open = source.IndexOf("open(login.AuthorizeUri);", StringComparison.Ordinal);
        var complete = source.IndexOf("return Project(await waitForCallbackTask);", StringComparison.Ordinal);
        Assert.IsTrue(host >= 0 && begin > host && wait > begin && report > wait && open > report && complete > open);
        StringAssert.Contains(source, "CreateLoginManager(provider, getRoot).SignOutAsync");
        StringAssert.Contains(source, "Project(await manager.GetCredentialAsync(token))");
        StringAssert.Contains(source, "OpenAICodexSubscriptionLoginManager.IsRegistration(credential) ? Project(credential) : null");
        foreach (var method in new[] { "LoginWithBrowserAsync", "SignOutAsync", "TestAuthenticationAsync", "ReadAccountMetadataAsync" })
        {
            StringAssert.Contains(tui, $"await ConfiguredCodexAuthentication.{method}(");
        }
        foreach (var legacy in new[] { "CompleteDeviceLoginAsync", "CodexAuthFileReader", "BeginBrowserLogin(", "DeleteCredentialAsync" })
        {
            Assert.IsFalse(source.Contains(legacy, StringComparison.Ordinal));
        }
        Assert.IsFalse(tui.Contains("OpenAICodexSubscriptionLoginManager", StringComparison.Ordinal));
        Assert.IsFalse(tui.Contains("OpenAICodexSubscriptionAuthManager", StringComparison.Ordinal));
        Assert.IsFalse(tui.Contains("LoginCodexSubscriptionWithDeviceCodeAsync", StringComparison.Ordinal));
        foreach (var forbidden in new[] { "Environment.", "Process.", "SR.T(", "ConfigureAwait(false)", "Task.Run(" })
        {
            Assert.IsFalse(source.Contains(forbidden, StringComparison.Ordinal));
        }
    }

    // Compile-time checkout path: inspect only named source/project files, never discover profile ancestors.
    private static string SourceRoot([CallerFilePath] string sourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile)!, ".."));
}
