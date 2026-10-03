namespace CodeAlta.Hosting.Tests;

[TestClass]
public sealed class HostingCompositionBoundaryTests
{
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
}
