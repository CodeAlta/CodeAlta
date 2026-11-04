using System.Net;
using System.Text;
using CodeAlta.Fleet.Protocol.Api;
using CodeAlta.Server.Identity.SignIn;

namespace CodeAlta.Server.Tests.SignIn;

/// <summary>
/// Company sign-in with the multi-tenant authorities of Entra ID (<c>doc/fleet/identity.md</c>, company SSO): their
/// discovery document names the issuer with a placeholder for the tenant of each account. The server accepts it when it
/// is set, as it does when it verifies a token, for Microsoft's own sign-in only, and keeps the tenants the owner names.
/// </summary>
[TestClass]
public sealed class EntraAuthorityTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task TheOrganizationsAuthorityOfEntraIdIsAccepted_ButATenantPlaceholderElsewhereIsNot()
    {
        using var discovery = new DiscoveryHandler();
        using var client = new ExternalSignInClient(new SignInOptions { ProviderHandler = discovery }, TimeProvider.System);
        ExternalProvider Company(string authority) => new(FleetSignInMethods.Company, ExternalProviderKind.OpenIdConnect, new Uri(authority), new SignInClient("client"),
            "openid profile email");

        // As Microsoft answers for https://login.microsoftonline.com/organizations/v2.0 and /common/v2.0.
        await client.ProbeAsync(Company("https://login.microsoftonline.com/organizations/v2.0"), TestContext.CancellationToken);
        await client.ProbeAsync(Company("https://login.microsoftonline.com/common/v2.0"), TestContext.CancellationToken);

        // Another provider that names a placeholder is not Microsoft's: its issuer is taken as it reads, and does not match.
        var elsewhere = await Assert.ThrowsExactlyAsync<ExternalSignInException>(() => client.ProbeAsync(Company("https://idp.example/organizations/v2.0"),
            TestContext.CancellationToken));
        Assert.AreEqual(SignInErrorCodes.ProviderUnavailable, elsewhere.Code);
    }

    [TestMethod]
    public async Task TheOwnerSetsTheOrganizationsAuthority_WithTheTenantsItLetsIn()
    {
        using var discovery = new DiscoveryHandler();
        await using var server = await ServerFixture.StartAsync(signIn: new SignInOptions { ProviderHandler = discovery });
        using var owner = new TestDevice("Desktop");
        using var key = new SoftwareAuthenticator();
        await server.SetUpAsync(owner);
        var origin = server.BaseAddress.GetLeftPart(UriPartial.Authority);
        var register = (await server.SendAsync(owner, HttpMethod.Post, "/api/v1/sign-in/start", new StartSignInRequest(FleetSignInMethods.Passkey, owner.PublicKey,
            "http://127.0.0.1:53682/callback", FleetSignInPurposes.Link))).Read<StartSignInResponse>();
        var options = (await server.SendAsync(null, HttpMethod.Post, "/api/v1/sign-in/passkey/options", new PasskeyOptionsRequest(register.AttemptId))).Read<PasskeyOptionsResponse>();
        var finish = (await server.SendAsync(null, HttpMethod.Post, "/api/v1/sign-in/passkey/finish", new PasskeyFinishRequest(register.AttemptId,
            key.Create(options.OptionsJson, origin)))).Read<PasskeyFinishResponse>();
        var back = new Uri(finish.RedirectUrl);
        Assert.AreEqual(HttpStatusCode.OK, (await server.SendAsync(owner, HttpMethod.Post, "/api/v1/sign-in/complete",
            new CompleteSignInRequest(register.AttemptId, FakeOidcProvider.Query(back, "code")!, FakeOidcProvider.Query(back, "state")!))).Status);
        var confirm = (await server.SendAsync(owner, HttpMethod.Post, "/api/v1/sign-in/start", new StartSignInRequest(FleetSignInMethods.Passkey, owner.PublicKey,
            "http://127.0.0.1:53682/callback", FleetSignInPurposes.Confirm))).Read<StartSignInResponse>();
        options = (await server.SendAsync(null, HttpMethod.Post, "/api/v1/sign-in/passkey/options", new PasskeyOptionsRequest(confirm.AttemptId))).Read<PasskeyOptionsResponse>();
        finish = (await server.SendAsync(null, HttpMethod.Post, "/api/v1/sign-in/passkey/finish", new PasskeyFinishRequest(confirm.AttemptId,
            key.Get(options.OptionsJson, origin)))).Read<PasskeyFinishResponse>();
        back = new Uri(finish.RedirectUrl);
        var confirmation = (await server.SendAsync(owner, HttpMethod.Post, "/api/v1/sign-in/complete",
            new CompleteSignInRequest(confirm.AttemptId, FakeOidcProvider.Query(back, "code")!, FakeOidcProvider.Query(back, "state")!))).Read<CompleteSignInResponse>().Grant;

        var held = (await server.SendAsync(owner, HttpMethod.Get, "/api/v1/sign-in/settings")).Read<SignInSettingsDto>();
        var set = await server.SendAsync(owner, HttpMethod.Put, "/api/v1/sign-in/settings", new SetSignInSettingsRequest(held.Version,
            [FleetSignInMethods.Company, FleetSignInMethods.Passkey],
            new CompanySignInSettings("https://login.microsoftonline.com/organizations/v2.0", "client", "secret", "Contoso", AllowedTenants: ["72f988bf-86f1-41af-91ab-2d7cd011db47"]),
            false, Confirmation: confirmation));
        Assert.AreEqual(HttpStatusCode.OK, set.Status, set.Body.ToString());
        CollectionAssert.AreEqual(new[] { "72f988bf-86f1-41af-91ab-2d7cd011db47" }, set.Read<SignInSettingsDto>().Company!.AllowedTenants!.ToArray());
    }

    // Microsoft's discovery documents of its multi-tenant authorities, and a provider elsewhere that copies their placeholder.
    private sealed class DiscoveryHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            var root = uri.GetLeftPart(UriPartial.Authority);
            var body = uri.AbsolutePath.EndsWith("/.well-known/openid-configuration", StringComparison.Ordinal)
                ? $$"""
                  {"issuer":"{{root}}/{tenantid}/v2.0","authorization_endpoint":"{{root}}/organizations/oauth2/v2.0/authorize",
                   "token_endpoint":"{{root}}/organizations/oauth2/v2.0/token","jwks_uri":"{{root}}/organizations/discovery/v2.0/keys",
                   "response_types_supported":["code"],"subject_types_supported":["pairwise"],"id_token_signing_alg_values_supported":["RS256"]}
                  """
                : """{"keys":[]}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
}
