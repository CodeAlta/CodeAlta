using CodeAlta.Agent.OpenAI.Codex;
using CodeAlta.Catalog;
using CodeAlta.Hosting;
using CodeAlta.Tui.App;

namespace CodeAlta.Tests;

[TestClass]
public sealed class CodexSubscriptionPresentationTests
{
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void Projection_ContainsOnlyDisplayMetadataAndPermission(bool permitted)
    {
        var credential = new OpenAICodexSubscriptionCredential
        {
            AccountLabel = "Workspace", Subject = "subject", ClientId = "issued-client",
            AccessToken = "must-not-escape", RefreshToken = "must-not-escape", IdToken = "must-not-escape",
            Scopes = permitted ? [OpenAICodexSubscriptionOAuthDefaults.DirectTokenScope, "resource.invoke"] : [],
        };

        var metadata = ConfiguredCodexAuthentication.Project(credential);

        Assert.AreEqual(new CodexAccountMetadata("Workspace", "subject", "issued-client", permitted), metadata);
        Assert.IsFalse(metadata.ToString().Contains("must-not-escape", StringComparison.Ordinal));
    }

    [TestMethod]
    public void LoginStatus_IsSignedInOnlyWhileTheRegistrationHoldsTokens()
    {
        var credential = new OpenAICodexSubscriptionCredential
        {
            AccountLabel = "Workspace", Subject = "subject", ClientId = "issued-client",
            AccessToken = "must-not-escape", RefreshToken = "must-not-escape",
            Scopes = [OpenAICodexSubscriptionOAuthDefaults.DirectTokenScope, "resource.invoke"],
        };

        var signedIn = ConfiguredProviderLogin.FromCodex(credential);
        Assert.AreEqual(new ProviderLoginStatus(true, "Workspace", null, null), signedIn);
        Assert.IsFalse(signedIn.ToString().Contains("must-not-escape", StringComparison.Ordinal));

        // Sign-out keeps the registration and clears its tokens.
        credential.AccessToken = string.Empty;
        credential.RefreshToken = null;
        Assert.AreEqual(new ProviderLoginStatus(false, null, null, null), ConfiguredProviderLogin.FromCodex(credential));
        Assert.IsFalse(ConfiguredProviderLogin.FromCodex((OpenAICodexSubscriptionCredential?)null).SignedIn);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void BrowserLogin_ReportsPlanPermissionAndIssuedRegistration(bool permitted)
    {
        var metadata = new CodexAccountMetadata("Workspace", "subject", "issued-client", permitted);

        var result = ProviderFrontendCoordinator.FormatCodexBrowserLoginResult(metadata);

        Assert.AreEqual(permitted, result.Success);
        var prefix = permitted
            ? SR.T("ChatGPT browser login completed")
            : SR.T("Signed in, but ChatGPT plan usage is disabled. Continue with ChatGPT to enable it, or configure an API-key provider");
        Assert.AreEqual(SR.T("{0} · ChatGPT account: {1} · registration: {2}.", prefix, "Workspace", "issued-client"), result.Message);
        Assert.AreEqual(0, result.ModelCount);
    }

    [TestMethod]
    [DataRow("Workspace", "subject", "Workspace")]
    [DataRow(null, "subject", "subject")]
    [DataRow("", "subject", "")]
    [DataRow(null, null, null)]
    public void AccountInfo_PreservesMainLabelFallback(string? label, string? subject, string? expected)
    {
        var metadata = new CodexAccountMetadata(label, subject, "issued-client", false);

        var result = ProviderFrontendCoordinator.FormatCodexAccountMetadataResult(metadata);

        Assert.IsTrue(result.Success);
        Assert.AreEqual(1, result.ModelCount);
        Assert.AreEqual(SR.T("{0} · ChatGPT account: {1} · registration: {2}.",
            SR.T("Saved ChatGPT registration for this provider"), expected ?? SR.T("account/workspace unknown"), "issued-client"), result.Message);
    }

    [TestMethod]
    public void AccountInfo_MissingRegistrationRequiresLogin()
    {
        var result = ProviderFrontendCoordinator.FormatCodexAccountMetadataResult(null);

        Assert.IsFalse(result.Success);
        Assert.AreEqual(SR.T("Login required before account/workspace metadata can be listed."), result.Message);
        Assert.AreEqual(0, result.ModelCount);
    }

    [TestMethod]
    public void AuthenticationTest_ReportsAccountAndRegistrationWithoutModelTurn()
    {
        var metadata = new CodexAccountMetadata(null, "subject", "issued-client", true);

        var result = ProviderFrontendCoordinator.FormatCodexAuthenticationResult(metadata);

        Assert.IsTrue(result.Success);
        Assert.AreEqual(SR.T("{0} · ChatGPT account: {1} · registration: {2}.",
            SR.T("Authenticated without sending a model turn"), "subject", "issued-client"), result.Message);
        Assert.AreEqual(0, result.ModelCount);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void SignOut_ReportsWhetherRemoteRevocationWasConfirmed(bool revoked)
    {
        var result = ProviderFrontendCoordinator.FormatCodexSignOutResult(revoked);

        Assert.IsTrue(result.Success);
        Assert.AreEqual(revoked
            ? SR.T("Signed out of ChatGPT. The account registration is retained for your next sign-in.")
            : SR.T("Signed out locally, but remote revocation was not confirmed. Disconnect CodeAlta in ChatGPT Settings if needed."), result.Message);
    }
}
