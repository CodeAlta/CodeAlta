using CodeAlta.Fleet.Protocol.Api;
using CodeAlta.Server.Identity.SignIn;

namespace CodeAlta.Server.Tests.SignIn;

/// <summary>
/// A confirmation is a fresh sign-in (<c>doc/fleet/identity.md</c>, confirming dangerous actions): its ID token says, signed,
/// when the person authenticated (<c>auth_time</c>). A token that does not say it confirms nothing, from Microsoft's own
/// authorities too, where a key holder with a session there could drop <c>prompt=login</c> from the address.
/// </summary>
[TestClass]
public sealed class FreshAuthenticationTests
{
    private static readonly DateTimeOffset Started = new(2026, 11, 5, 9, 0, 0, TimeSpan.Zero);

    [TestMethod]
    [DataRow(FleetSignInMethods.Microsoft, "https://login.microsoftonline.com/common/v2.0")]
    [DataRow(FleetSignInMethods.Company, "https://login.microsoftonline.com/organizations/v2.0")]
    [DataRow(FleetSignInMethods.Company, "https://login.microsoftonline.com/7d2c8a0e-0000-4000-8000-000000000001/v2.0")]
    [DataRow(FleetSignInMethods.Google, "https://accounts.google.com")]
    [DataRow(FleetSignInMethods.Company, "https://idp.example")]
    public void AConfirmationWithoutTheTimeOfItsAuthenticationIsNotFresh_AtEveryProvider(string method, string authority)
    {
        var provider = new ExternalProvider(method, ExternalProviderKind.OpenIdConnect, new Uri(authority), new SignInClient("client"), "openid profile email");
        var missing = Assert.ThrowsExactly<ExternalSignInException>(() => ExternalSignInClient.RequireFreshAuthentication(provider, new Dictionary<string, object>(), Started));
        Assert.AreEqual(SignInErrorCodes.NotFresh, missing.Code);
        StringAssert.Contains(missing.Message, "auth_time");

        // A sign-in of before the confirmation started is not fresh either; one of now is.
        var before = new Dictionary<string, object> { ["auth_time"] = (Started - TimeSpan.FromHours(1)).ToUnixTimeSeconds() };
        Assert.AreEqual(SignInErrorCodes.NotFresh, Assert.ThrowsExactly<ExternalSignInException>(() => ExternalSignInClient.RequireFreshAuthentication(provider, before, Started)).Code);
        ExternalSignInClient.RequireFreshAuthentication(provider, new Dictionary<string, object> { ["auth_time"] = (Started + TimeSpan.FromSeconds(20)).ToUnixTimeSeconds() }, Started);
    }
}
