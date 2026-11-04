using System.Net;
using System.Text.Json;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Fleet.Protocol.Api;
using CodeAlta.Server.Identity;
using CodeAlta.Server.Identity.SignIn;
using CodeAlta.Server.Storage;

namespace CodeAlta.Server.Tests.SignIn;

/// <summary>
/// Sign-in (<c>doc/fleet/identity.md</c>): people prove who they are with a method the owner enabled, through the system
/// browser of a device that comes back to its loopback address; a sign-in grants nothing but the enrollment of that
/// device as a pending controller device of the person, which the device list rules then let in.
/// </summary>
[TestClass]
public sealed class SignInTests
{
    private const string Redirect = "http://127.0.0.1:53682/callback";

    [TestMethod]
    public async Task CompanySignInCreatesThePerson_TheOwnerApprovesTheirFirstDevice_AndThatDeviceTheNext()
    {
        await using var provider = await FakeOidcProvider.StartAsync(clientSecret: "company-secret");
        await using var server = await ServerFixture.StartAsync(signIn: Options(provider));
        using var owner = new TestDevice("Desktop");
        using var node = new TestDevice("Mac mini");
        using var laptop = new TestDevice("Laptop");
        using var phone = new TestDevice("Second laptop");
        var (setup, _) = await server.SetUpWithNodeAsync(owner, node);
        await ConfigureCompanyAsync(server, owner, provider, requireSso: false);

        var methods = (await server.SendAsync(null, HttpMethod.Get, "/api/v1/sign-in/methods")).Read<SignInMethodsResponse>();
        CollectionAssert.Contains(methods.Methods.Select(static method => method.Id).ToArray(), FleetSignInMethods.Company);
        Assert.AreEqual("Contoso", methods.Methods.Single(static method => method.Id == FleetSignInMethods.Company).DisplayName);

        var (start, back) = await StartAsync(server, laptop, FleetSignInMethods.Company);
        var authorization = provider.Authorizations.Last();
        Assert.AreEqual((provider.ClientId, Redirect, "S256"), (authorization["client_id"], authorization["redirect_uri"], authorization["code_challenge_method"]));
        Assert.IsFalse(string.IsNullOrEmpty(authorization["nonce"]), "an OpenID Connect sign-in carries a nonce");
        Assert.AreEqual(start.State, FakeOidcProvider.Query(back, "state"));

        var completed = await CompleteAsync(server, laptop, start, back);
        Assert.AreEqual(HttpStatusCode.OK, completed.Status, completed.Body.ToString());
        var signedIn = completed.Read<CompleteSignInResponse>();
        Assert.AreEqual(("Alex Martin", "alex@contoso.example", false), (signedIn.Person.DisplayName, signedIn.Person.Email, signedIn.Person.HasDevices));
        Assert.IsNotNull(signedIn.Grant);
        var person = await server.Server.Store.GetPersonAsync(signedIn.Person.Id);
        Assert.AreEqual(PersonRoles.Member, person!.Role, "company sign-in creates a member");
        var login = await ((ISignInStore)server.Server.Store).FindLoginAsync(setup.Organization.Id, provider.Issuer.AbsoluteUri.TrimEnd('/'), "user-1");
        CollectionAssert.AreEqual(new[] { "g-engineering", "g-admins" }, JsonSerializer.Deserialize<string[]>(login!.Groups!), "the groups are kept for the roles of milestone 4");

        // The grant enrolls this device only, once, as a pending controller device of the person.
        var other = await server.SendAsync(phone, HttpMethod.Post, "/api/v1/enroll", new EnrollRequest(string.Empty, phone.PublicKey, phone.Name, null, signedIn.Grant));
        Assert.AreEqual(SignInErrorCodes.GrantInvalid, other.Code, "a grant of another device");
        var enrolled = await server.SendAsync(laptop, HttpMethod.Post, "/api/v1/enroll", new EnrollRequest(string.Empty, laptop.PublicKey, laptop.Name, null, signedIn.Grant));
        Assert.AreEqual(HttpStatusCode.Created, enrolled.Status, enrolled.Body.ToString());
        var device = enrolled.Read<EnrollResponse>().Device;
        Assert.AreEqual((FleetDeviceKinds.Controller, DeviceStatuses.Pending, signedIn.Person.Id), (device.Kind, device.Status, device.OwnerId));
        var again = await server.SendAsync(laptop, HttpMethod.Post, "/api/v1/enroll", new EnrollRequest(string.Empty, laptop.PublicKey, laptop.Name, null, signedIn.Grant));
        Assert.AreEqual(SignInErrorCodes.GrantInvalid, again.Code, "a grant works once");
        var early = await server.SendAsync(laptop, HttpMethod.Get, "/api/v1/machines");
        Assert.AreEqual(FleetErrorCodes.PendingApproval, early.Code, "a sign-in grants nothing by itself");

        // The owner signs the first device of a person who has none into the device list.
        var entry = await server.NextEntryAsync(owner, FleetDeviceListOperations.Add, laptop.DeviceId, laptop.PublicKey, FleetDeviceKinds.Controller, signedIn.Person.Id,
            setup.Organization.Id);
        var approved = await server.SendAsync(owner, HttpMethod.Post, $"/api/v1/devices/{laptop.DeviceId}/approve", new DeviceListChangeRequest(entry));
        Assert.AreEqual(HttpStatusCode.OK, approved.Status, approved.Body.ToString());
        Assert.AreEqual(DeviceStatuses.Approved, approved.Read<DeviceDto>().Status);

        // The person's next device: the same account, the same person; the owner no longer signs it, their first device does.
        var (start2, back2) = await StartAsync(server, phone, FleetSignInMethods.Company);
        var second = (await CompleteAsync(server, phone, start2, back2)).Read<CompleteSignInResponse>();
        Assert.AreEqual((signedIn.Person.Id, true), (second.Person.Id, second.Person.HasDevices));
        Assert.AreEqual(HttpStatusCode.Created, (await server.SendAsync(phone, HttpMethod.Post, "/api/v1/enroll",
            new EnrollRequest(string.Empty, phone.PublicKey, phone.Name, null, second.Grant))).Status);
        var byOwner = await server.NextEntryAsync(owner, FleetDeviceListOperations.Add, phone.DeviceId, phone.PublicKey, FleetDeviceKinds.Controller, signedIn.Person.Id,
            setup.Organization.Id);
        Assert.AreEqual(HttpStatusCode.Forbidden, (await server.SendAsync(owner, HttpMethod.Post, $"/api/v1/devices/{phone.DeviceId}/approve", new DeviceListChangeRequest(byOwner))).Status,
            "the owner signs only a person's first device");
        var byLaptop = await server.NextEntryAsync(laptop, FleetDeviceListOperations.Add, phone.DeviceId, phone.PublicKey, FleetDeviceKinds.Controller, signedIn.Person.Id,
            setup.Organization.Id);
        Assert.AreEqual(HttpStatusCode.OK, (await server.SendAsync(laptop, HttpMethod.Post, $"/api/v1/devices/{phone.DeviceId}/approve", new DeviceListChangeRequest(byLaptop))).Status);

        // Every device replays the list with the same rules.
        var list = (await server.SendAsync(owner, HttpMethod.Get, "/api/v1/device-list")).Read<ListResponse<DeviceListEntryDto>>();
        var verified = FleetDeviceListVerifier.Verify(list.Items.Select(static item => item.Entry), setup.Organization.Id);
        Assert.IsTrue(verified.IsValid, verified.Reason);
        Assert.AreEqual(1, (await server.Server.Store.ListPeopleAsync(setup.Organization.Id)).Count(other => other.Id == signedIn.Person.Id), "one person per account");
    }

    [TestMethod]
    public async Task WhatTheProviderAnswersWrongIsRefused()
    {
        await using var provider = await FakeOidcProvider.StartAsync();
        await using var server = await ServerFixture.StartAsync(signIn: Options(provider));
        using var owner = new TestDevice("Desktop");
        using var laptop = new TestDevice("Laptop");
        await server.SetUpAsync(owner);
        await ConfigureCompanyAsync(server, owner, provider, requireSso: false);

        var cases = new (Action<FakeOidcProvider> Tamper, string Code)[]
        {
            (static p => p.Audience = "another-client", SignInErrorCodes.WrongAudience),
            (static p => p.TokenIssuer = "https://evil.example", SignInErrorCodes.WrongIssuer),
            (static p => p.Nonce = "another-nonce", SignInErrorCodes.WrongNonce),
            (static p => p.Lifetime = TimeSpan.FromMinutes(-10), SignInErrorCodes.TokenExpired),
            (static p => p.SignWithUnknownKey = true, SignInErrorCodes.TokenInvalid),
        };
        foreach (var (tamper, code) in cases)
        {
            provider.Audience = null;
            provider.TokenIssuer = null;
            provider.Nonce = null;
            provider.Lifetime = TimeSpan.FromMinutes(5);
            provider.SignWithUnknownKey = false;
            tamper(provider);
            var (start, back) = await StartAsync(server, laptop, FleetSignInMethods.Company);
            var refused = await CompleteAsync(server, laptop, start, back);
            Assert.AreEqual((HttpStatusCode.Unauthorized, code), (refused.Status, refused.Code), refused.Body.ToString());
        }

        var audit = (await server.SendAsync(owner, HttpMethod.Get, "/api/v1/audit?limit=500")).Read<ListResponse<AuditRecordDto>>().Items;
        Assert.AreEqual(cases.Length, audit.Count(static record => record.Action == "sign_in.complete" && record.Result != AuditEntry.Success), "every refusal is audited");
    }

    [TestMethod]
    public async Task ACodeAnAttemptAndAStateWorkOnce_AndOnlyForTheirDevice()
    {
        await using var provider = await FakeOidcProvider.StartAsync();
        await using var server = await ServerFixture.StartAsync(signIn: Options(provider));
        using var owner = new TestDevice("Desktop");
        using var laptop = new TestDevice("Laptop");
        using var thief = new TestDevice("Elsewhere");
        await server.SetUpAsync(owner);
        await ConfigureCompanyAsync(server, owner, provider, requireSso: false);

        var (start, back) = await StartAsync(server, laptop, FleetSignInMethods.Company);
        var stolen = await CompleteAsync(server, thief, start, back);
        Assert.AreEqual(SignInErrorCodes.AttemptInvalid, stolen.Code, "another device does not complete the attempt");
        Assert.AreEqual(HttpStatusCode.OK, (await CompleteAsync(server, laptop, start, back)).Status);
        Assert.AreEqual(SignInErrorCodes.AttemptInvalid, (await CompleteAsync(server, laptop, start, back)).Code, "an attempt completes once");

        // The code replayed in another attempt: the provider refuses it.
        var (next, _) = await StartAsync(server, laptop, FleetSignInMethods.Company);
        var replayed = await server.SendAsync(laptop, HttpMethod.Post, "/api/v1/sign-in/complete",
            new CompleteSignInRequest(next.AttemptId, FakeOidcProvider.Query(back, "code")!, next.State));
        Assert.AreEqual(SignInErrorCodes.CodeRefused, replayed.Code, replayed.Body.ToString());

        // A browser that comes back with the state of another sign-in.
        var (third, thirdBack) = await StartAsync(server, laptop, FleetSignInMethods.Company);
        var crossed = await server.SendAsync(laptop, HttpMethod.Post, "/api/v1/sign-in/complete",
            new CompleteSignInRequest(third.AttemptId, FakeOidcProvider.Query(thirdBack, "code")!, start.State));
        Assert.AreEqual(SignInErrorCodes.StateMismatch, crossed.Code);

        // An attempt expires.
        var (late, lateBack) = await StartAsync(server, laptop, FleetSignInMethods.Company);
        server.Clock.Advance(TimeSpan.FromMinutes(11));
        Assert.AreEqual(SignInErrorCodes.AttemptInvalid, (await CompleteAsync(server, laptop, late, lateBack)).Code);

        // A redirect elsewhere than the device is refused at the start.
        var elsewhere = await server.SendAsync(laptop, HttpMethod.Post, "/api/v1/sign-in/start",
            new StartSignInRequest(FleetSignInMethods.Company, laptop.PublicKey, "https://evil.example/callback"));
        Assert.AreEqual(SignInErrorCodes.RedirectInvalid, elsewhere.Code);
        var unsigned = await server.SendAsync(thief, HttpMethod.Post, "/api/v1/sign-in/start", new StartSignInRequest(FleetSignInMethods.Company, laptop.PublicKey, Redirect));
        Assert.AreEqual(CoordinatorErrorCodes.SignatureInvalid, unsigned.Code, "the start is signed by the key it names");
    }

    [TestMethod]
    public async Task AMethodTheServerDoesNotOfferIsRefused()
    {
        await using var server = await ServerFixture.StartAsync();
        using var owner = new TestDevice("Desktop");
        using var laptop = new TestDevice("Laptop");
        await server.SetUpAsync(owner);

        // The public clients of the CodeAlta project are placeholders until they are registered: their methods are not offered.
        var methods = (await server.SendAsync(null, HttpMethod.Get, "/api/v1/sign-in/methods")).Read<SignInMethodsResponse>();
        CollectionAssert.AreEqual(new[] { FleetSignInMethods.Passkey }, methods.Methods.Select(static method => method.Id).ToArray());
        var settings = (await server.SendAsync(owner, HttpMethod.Get, "/api/v1/sign-in/settings")).Read<SignInSettingsDto>();
        Assert.IsFalse(settings.Methods.Single(static method => method.Id == FleetSignInMethods.Microsoft).Available);

        foreach (var method in new[] { FleetSignInMethods.Microsoft, FleetSignInMethods.Company, "saml" })
        {
            var refused = await server.SendAsync(laptop, HttpMethod.Post, "/api/v1/sign-in/start", new StartSignInRequest(method, laptop.PublicKey, Redirect));
            Assert.AreEqual((HttpStatusCode.Forbidden, SignInErrorCodes.MethodDisabled), (refused.Status, refused.Code), method);
        }

        // The owner disables passkeys: they are refused too.
        var disabled = await server.SendAsync(owner, HttpMethod.Put, "/api/v1/sign-in/settings", new SetSignInSettingsRequest(settings.Version, [], null, false));
        Assert.AreEqual(HttpStatusCode.OK, disabled.Status, disabled.Body.ToString());
        Assert.AreEqual(SignInErrorCodes.MethodDisabled,
            (await server.SendAsync(laptop, HttpMethod.Post, "/api/v1/sign-in/start", new StartSignInRequest(FleetSignInMethods.Passkey, laptop.PublicKey, Redirect))).Code);
    }

    [TestMethod]
    public async Task ALinkedAccountSignsInOnANewDevice_ThatADeviceOfThePersonOrTheirRecoveryKeyConfirms()
    {
        await using var provider = await FakeOidcProvider.StartAsync();
        await using var server = await ServerFixture.StartAsync(signIn: Options(provider));
        using var owner = new TestDevice("Desktop");
        using var laptop = new TestDevice("Laptop");
        using var lost = new TestDevice("New laptop");
        using var recovery = new TestDevice("Recovery key");
        var setup = await server.SetUpAsync(owner);

        // An account nobody linked signs nobody in.
        var (unlinked, unlinkedBack) = await StartAsync(server, laptop, FleetSignInMethods.Microsoft);
        Assert.AreEqual((HttpStatusCode.Forbidden, SignInErrorCodes.NotLinked), Answer(await CompleteAsync(server, laptop, unlinked, unlinkedBack)));

        // The owner links their Microsoft account from their desktop.
        var (link, linkBack) = await StartAsync(server, owner, FleetSignInMethods.Microsoft, FleetSignInPurposes.Link);
        var linked = (await CompleteAsync(server, owner, link, linkBack)).Read<CompleteSignInResponse>();
        Assert.AreEqual((true, setup.Owner.Id, (string?)null), (linked.Linked, linked.Person.Id, linked.Grant));
        var logins = (await server.SendAsync(owner, HttpMethod.Get, "/api/v1/sign-in/logins")).Read<ListResponse<SignInLoginDto>>().Items;
        Assert.AreEqual((FleetSignInMethods.Microsoft, "alex@contoso.example"), (logins.Single().Method, logins.Single().Name));
        var notApproved = await server.SendAsync(laptop, HttpMethod.Post, "/api/v1/sign-in/start",
            new StartSignInRequest(FleetSignInMethods.Microsoft, laptop.PublicKey, Redirect, FleetSignInPurposes.Link));
        Assert.AreEqual(HttpStatusCode.Unauthorized, notApproved.Status, "a device the server does not know links nothing");

        // On a new laptop the same account proves the owner, whose desktop approves it.
        var (start, back) = await StartAsync(server, laptop, FleetSignInMethods.Microsoft);
        var signedIn = (await CompleteAsync(server, laptop, start, back)).Read<CompleteSignInResponse>();
        Assert.AreEqual((setup.Owner.Id, true), (signedIn.Person.Id, signedIn.Person.HasDevices));
        Assert.AreEqual(HttpStatusCode.Created, (await server.SendAsync(laptop, HttpMethod.Post, "/api/v1/enroll",
            new EnrollRequest(string.Empty, laptop.PublicKey, laptop.Name, null, signedIn.Grant))).Status);
        var entry = await server.NextEntryAsync(owner, FleetDeviceListOperations.Add, laptop.DeviceId, laptop.PublicKey, FleetDeviceKinds.Controller, setup.Owner.Id, setup.Organization.Id);
        Assert.AreEqual(HttpStatusCode.OK, (await server.SendAsync(owner, HttpMethod.Post, $"/api/v1/devices/{laptop.DeviceId}/approve", new DeviceListChangeRequest(entry))).Status);

        // Or the recovery key confirms the device the person signed in on, when every device is lost.
        var recoveryEntry = await server.NextEntryAsync(owner, FleetDeviceListOperations.Add, recovery.DeviceId, recovery.PublicKey, FleetDeviceKinds.Recovery, setup.Owner.Id,
            setup.Organization.Id);
        Assert.AreEqual(HttpStatusCode.Created, (await server.SendAsync(owner, HttpMethod.Post, "/api/v1/recovery-keys",
            new CreateRecoveryKeyRequest(recovery.PublicKey, recoveryEntry, "sealed-anchor"))).Status);
        var (again, againBack) = await StartAsync(server, lost, FleetSignInMethods.Microsoft);
        var grant = (await CompleteAsync(server, lost, again, againBack)).Read<CompleteSignInResponse>().Grant;
        Assert.AreEqual(HttpStatusCode.Created, (await server.SendAsync(lost, HttpMethod.Post, "/api/v1/enroll", new EnrollRequest(string.Empty, lost.PublicKey, lost.Name, null, grant))).Status);
        var byRecovery = await server.NextEntryAsync(recovery, FleetDeviceListOperations.Add, lost.DeviceId, lost.PublicKey, FleetDeviceKinds.Controller, setup.Owner.Id,
            setup.Organization.Id);
        var recovered = await server.SendAsync(lost, HttpMethod.Post, "/api/v1/recover", new RecoverRequest(lost.PublicKey, lost.Name, byRecovery));
        Assert.AreEqual(HttpStatusCode.Created, recovered.Status, recovered.Body.ToString());
        Assert.AreEqual(DeviceStatuses.Approved, recovered.Read<DeviceDto>().Status);
    }

    [TestMethod]
    public async Task GitHubSignsInWithTheNumericIdOfTheAccount()
    {
        await using var provider = await FakeOidcProvider.StartAsync(clientSecret: "github-secret");
        await using var server = await ServerFixture.StartAsync(signIn: Options(provider));
        using var owner = new TestDevice("Desktop");
        using var laptop = new TestDevice("Laptop");
        var setup = await server.SetUpAsync(owner);
        var (link, linkBack) = await StartAsync(server, owner, FleetSignInMethods.GitHub, FleetSignInPurposes.Link);
        Assert.AreEqual(HttpStatusCode.OK, (await CompleteAsync(server, owner, link, linkBack)).Status);
        var login = await ((ISignInStore)server.Server.Store).FindLoginAsync(setup.Organization.Id, provider.Issuer.GetLeftPart(UriPartial.Authority), "4242");
        Assert.AreEqual(setup.Owner.Id, login?.PersonId);

        var (start, back) = await StartAsync(server, laptop, FleetSignInMethods.GitHub);
        Assert.IsFalse(provider.Authorizations.Last().ContainsKey("nonce"), "GitHub's OAuth has no ID token, so no nonce");
        Assert.AreEqual(setup.Owner.Id, (await CompleteAsync(server, laptop, start, back)).Read<CompleteSignInResponse>().Person.Id);
    }

    [TestMethod]
    public async Task APasskeyRegistersOnAPersonsDevice_AndSignsThemInOnANewOne()
    {
        await using var server = await ServerFixture.StartAsync();
        using var owner = new TestDevice("Desktop");
        using var laptop = new TestDevice("Laptop");
        using var authenticator = new SoftwareAuthenticator();
        var setup = await server.SetUpAsync(owner);
        var origin = server.BaseAddress.GetLeftPart(UriPartial.Authority);

        var page = await server.Http.GetAsync("/sign-in/passkey");
        Assert.AreEqual(HttpStatusCode.OK, page.StatusCode);
        StringAssert.Contains(page.Headers.GetValues("Content-Security-Policy").Single(), "script-src 'self'");

        // Registration, from the owner's desktop: the server's page runs the ceremony and goes back to the desktop.
        var register = (await server.SendAsync(owner, HttpMethod.Post, "/api/v1/sign-in/start",
            new StartSignInRequest(FleetSignInMethods.Passkey, owner.PublicKey, Redirect, FleetSignInPurposes.Link))).Read<StartSignInResponse>();
        StringAssert.StartsWith(register.AuthorizationUrl, origin + "/sign-in/passkey#");
        var created = await PasskeyCeremonyAsync(server, register, "create", options => authenticator.Create(options, origin));
        var linked = await CompleteAsync(server, owner, register, created);
        Assert.AreEqual(HttpStatusCode.OK, linked.Status, linked.Body.ToString());
        Assert.IsTrue(linked.Read<CompleteSignInResponse>().Linked);
        Assert.AreEqual(authenticator.CredentialId, (await ((ISignInStore)server.Server.Store).ListPasskeysAsync(setup.Owner.Id)).Single().CredentialId);

        // Sign-in on a new laptop with that passkey: the discoverable credential names its person.
        var start = (await server.SendAsync(laptop, HttpMethod.Post, "/api/v1/sign-in/start", new StartSignInRequest(FleetSignInMethods.Passkey, laptop.PublicKey, Redirect)))
            .Read<StartSignInResponse>();
        var back = await PasskeyCeremonyAsync(server, start, "get", options => authenticator.Get(options, origin));
        var signedIn = await CompleteAsync(server, laptop, start, back);
        Assert.AreEqual(HttpStatusCode.OK, signedIn.Status, signedIn.Body.ToString());
        Assert.AreEqual(setup.Owner.Id, signedIn.Read<CompleteSignInResponse>().Person.Id);
        Assert.AreEqual(SignInErrorCodes.AttemptInvalid, (await CompleteAsync(server, laptop, start, back)).Code, "a passkey sign-in completes once");

        // The code of the page is the attempt's: another one does not complete it.
        var guessed = (await server.SendAsync(laptop, HttpMethod.Post, "/api/v1/sign-in/start", new StartSignInRequest(FleetSignInMethods.Passkey, laptop.PublicKey, Redirect)))
            .Read<StartSignInResponse>();
        await PasskeyCeremonyAsync(server, guessed, "get", options => authenticator.Get(options, origin));
        Assert.AreEqual(SignInErrorCodes.PasskeyInvalid, (await server.SendAsync(laptop, HttpMethod.Post, "/api/v1/sign-in/complete",
            new CompleteSignInRequest(guessed.AttemptId, "not-the-code", guessed.State))).Code);

        // An answer to another challenge, or from another origin, does not verify.
        var forged = (await server.SendAsync(laptop, HttpMethod.Post, "/api/v1/sign-in/start", new StartSignInRequest(FleetSignInMethods.Passkey, laptop.PublicKey, Redirect)))
            .Read<StartSignInResponse>();
        var options = (await server.SendAsync(null, HttpMethod.Post, "/api/v1/sign-in/passkey/options", new PasskeyOptionsRequest(forged.AttemptId))).Read<PasskeyOptionsResponse>();
        var wrongChallenge = await server.SendAsync(null, HttpMethod.Post, "/api/v1/sign-in/passkey/finish",
            new PasskeyFinishRequest(forged.AttemptId, authenticator.Get(options.OptionsJson, origin, challengeOverride: "AAAAAAAAAAAAAAAAAAAAAA")));
        Assert.AreEqual(SignInErrorCodes.PasskeyInvalid, wrongChallenge.Code, wrongChallenge.Body.ToString());
        options = (await server.SendAsync(null, HttpMethod.Post, "/api/v1/sign-in/passkey/options", new PasskeyOptionsRequest(forged.AttemptId))).Read<PasskeyOptionsResponse>();
        var wrongOrigin = await server.SendAsync(null, HttpMethod.Post, "/api/v1/sign-in/passkey/finish",
            new PasskeyFinishRequest(forged.AttemptId, authenticator.Get(options.OptionsJson, "https://evil.example")));
        Assert.AreEqual(SignInErrorCodes.PasskeyInvalid, wrongOrigin.Code);
    }

    [TestMethod]
    public async Task RequiredCompanySignIn_LeavesTheBreakGlassOwnersPasskeyAndNothingElse()
    {
        await using var provider = await FakeOidcProvider.StartAsync();
        await using var server = await ServerFixture.StartAsync(signIn: Options(provider));
        using var owner = new TestDevice("Desktop");
        using var node = new TestDevice("Mac mini");
        using var memberLaptop = new TestDevice("Member laptop");
        using var memberNew = new TestDevice("Member new laptop");
        using var ownerNew = new TestDevice("Owner new laptop");
        using var ownerKey = new SoftwareAuthenticator();
        using var memberKey = new SoftwareAuthenticator();
        var (setup, _) = await server.SetUpWithNodeAsync(owner, node);
        var origin = server.BaseAddress.GetLeftPart(UriPartial.Authority);

        // Requiring company sign-in needs it configured first, and only an owner changes it.
        var held = (await server.SendAsync(owner, HttpMethod.Get, "/api/v1/sign-in/settings")).Read<SignInSettingsDto>();
        var premature = await server.SendAsync(owner, HttpMethod.Put, "/api/v1/sign-in/settings",
            new SetSignInSettingsRequest(held.Version, [FleetSignInMethods.Passkey], null, true));
        Assert.AreEqual(SignInErrorCodes.SettingsInvalid, premature.Code);

        // The owner's passkey and Microsoft link, a member created by company sign-in with a device and a passkey of their own.
        await RegisterPasskeyAsync(server, owner, ownerKey, origin);
        var (link, linkBack) = await StartAsync(server, owner, FleetSignInMethods.Microsoft, FleetSignInPurposes.Link);
        Assert.AreEqual(HttpStatusCode.OK, (await CompleteAsync(server, owner, link, linkBack)).Status);
        await ConfigureCompanyAsync(server, owner, provider, requireSso: false);
        provider.Account = new FakeAccount("member-1", "sam@contoso.example", "Sam", ["g-engineering"]);
        var (start, back) = await StartAsync(server, memberLaptop, FleetSignInMethods.Company);
        var member = (await CompleteAsync(server, memberLaptop, start, back)).Read<CompleteSignInResponse>();
        Assert.AreEqual(HttpStatusCode.Created, (await server.SendAsync(memberLaptop, HttpMethod.Post, "/api/v1/enroll",
            new EnrollRequest(string.Empty, memberLaptop.PublicKey, memberLaptop.Name, null, member.Grant))).Status);
        var entry = await server.NextEntryAsync(owner, FleetDeviceListOperations.Add, memberLaptop.DeviceId, memberLaptop.PublicKey, FleetDeviceKinds.Controller, member.Person.Id,
            setup.Organization.Id);
        Assert.AreEqual(HttpStatusCode.OK, (await server.SendAsync(owner, HttpMethod.Post, $"/api/v1/devices/{memberLaptop.DeviceId}/approve", new DeviceListChangeRequest(entry))).Status);
        await RegisterPasskeyAsync(server, memberLaptop, memberKey, origin);
        Assert.AreEqual(HttpStatusCode.Forbidden, (await server.SendAsync(memberLaptop, HttpMethod.Get, "/api/v1/sign-in/settings")).Status, "a member does not read the settings");

        await ConfigureCompanyAsync(server, owner, provider, requireSso: true);
        var methods = (await server.SendAsync(null, HttpMethod.Get, "/api/v1/sign-in/methods")).Read<SignInMethodsResponse>();
        Assert.IsTrue(methods.SsoRequired);
        CollectionAssert.AreEqual(new[] { FleetSignInMethods.Company, FleetSignInMethods.Passkey }, methods.Methods.Select(static method => method.Id).ToArray());
        Assert.IsTrue(methods.Methods.Single(static method => method.Id == FleetSignInMethods.Passkey).BreakGlass);

        // Microsoft is refused at the start; the member's passkey at the end, once it named its person.
        var microsoft = await server.SendAsync(ownerNew, HttpMethod.Post, "/api/v1/sign-in/start", new StartSignInRequest(FleetSignInMethods.Microsoft, ownerNew.PublicKey, Redirect));
        Assert.AreEqual((HttpStatusCode.Forbidden, SignInErrorCodes.SsoRequired), (microsoft.Status, microsoft.Code));
        var memberAttempt = (await server.SendAsync(memberNew, HttpMethod.Post, "/api/v1/sign-in/start",
            new StartSignInRequest(FleetSignInMethods.Passkey, memberNew.PublicKey, Redirect))).Read<StartSignInResponse>();
        var memberBack = await PasskeyCeremonyAsync(server, memberAttempt, "get", options => memberKey.Get(options, origin));
        Assert.AreEqual((HttpStatusCode.Forbidden, SignInErrorCodes.SsoRequired), Answer(await CompleteAsync(server, memberNew, memberAttempt, memberBack)));

        // The break-glass owner still signs in with their passkey; company sign-in still works for the member.
        var ownerAttempt = (await server.SendAsync(ownerNew, HttpMethod.Post, "/api/v1/sign-in/start",
            new StartSignInRequest(FleetSignInMethods.Passkey, ownerNew.PublicKey, Redirect))).Read<StartSignInResponse>();
        var ownerBack = await PasskeyCeremonyAsync(server, ownerAttempt, "get", options => ownerKey.Get(options, origin));
        var breakGlass = await CompleteAsync(server, ownerNew, ownerAttempt, ownerBack);
        Assert.AreEqual(HttpStatusCode.OK, breakGlass.Status, breakGlass.Body.ToString());
        Assert.AreEqual(setup.Owner.Id, breakGlass.Read<CompleteSignInResponse>().Person.Id);
        var (sso, ssoBack) = await StartAsync(server, memberNew, FleetSignInMethods.Company);
        Assert.AreEqual(member.Person.Id, (await CompleteAsync(server, memberNew, sso, ssoBack)).Read<CompleteSignInResponse>().Person.Id);
    }

    [TestMethod]
    public async Task TheCompanyProviderIsCheckedWhenTheOwnerSetsIt_AndItsSecretNeverComesBack()
    {
        await using var provider = await FakeOidcProvider.StartAsync(clientSecret: "company-secret");
        await using var server = await ServerFixture.StartAsync(signIn: Options(provider));
        using var owner = new TestDevice("Desktop");
        await server.SetUpAsync(owner);
        var held = (await server.SendAsync(owner, HttpMethod.Get, "/api/v1/sign-in/settings")).Read<SignInSettingsDto>();
        var unreachable = await server.SendAsync(owner, HttpMethod.Put, "/api/v1/sign-in/settings", new SetSignInSettingsRequest(held.Version, [FleetSignInMethods.Company],
            new CompanySignInSettings("http://127.0.0.1:1/nothing", "client", "secret"), false));
        Assert.AreEqual(SignInErrorCodes.ProviderUnavailable, unreachable.Code);
        var plainHttp = await server.SendAsync(owner, HttpMethod.Put, "/api/v1/sign-in/settings", new SetSignInSettingsRequest(held.Version, [FleetSignInMethods.Company],
            new CompanySignInSettings("http://idp.example", "client", "secret"), false));
        Assert.AreEqual(SignInErrorCodes.SettingsInvalid, plainHttp.Code);

        var settings = await ConfigureCompanyAsync(server, owner, provider, requireSso: false);
        Assert.IsTrue(settings.Company!.HasClientSecret);
        StringAssert.DoesNotMatch((await server.SendAsync(owner, HttpMethod.Get, "/api/v1/sign-in/settings")).Body.ToString(), new System.Text.RegularExpressions.Regex("company-secret"));
        var stale = await server.SendAsync(owner, HttpMethod.Put, "/api/v1/sign-in/settings", new SetSignInSettingsRequest(held.Version, [], null, false));
        Assert.AreEqual((HttpStatusCode.Conflict, SignInErrorCodes.SettingsInvalid), (stale.Status, stale.Code), "a change names the version it read");
        var audit = (await server.SendAsync(owner, HttpMethod.Get, "/api/v1/audit?limit=500")).Read<ListResponse<AuditRecordDto>>().Items;
        Assert.IsFalse(audit.Any(static record => record.Details?.Contains("company-secret", StringComparison.Ordinal) == true), "the audit log never holds the secret");
    }

    internal static SignInOptions Options(FakeOidcProvider provider) => new()
    {
        AllowLoopbackHttpProviders = true,
        Microsoft = new SignInClient(provider.ClientId, provider.ClientSecret),
        MicrosoftAuthority = provider.Issuer,
        Google = new SignInClient(provider.ClientId, provider.ClientSecret),
        GoogleAuthority = provider.Issuer,
        GitHub = new SignInClient(provider.ClientId, provider.ClientSecret),
        GitHubAddress = provider.Issuer,
        GitHubApiAddress = provider.Issuer,
    };

    private static (HttpStatusCode, string?) Answer(ApiAnswer answer) => (answer.Status, answer.Code);

    private static async Task<SignInSettingsDto> ConfigureCompanyAsync(ServerFixture server, TestDevice owner, FakeOidcProvider provider, bool requireSso)
    {
        var held = (await server.SendAsync(owner, HttpMethod.Get, "/api/v1/sign-in/settings")).Read<SignInSettingsDto>();
        var set = await server.SendAsync(owner, HttpMethod.Put, "/api/v1/sign-in/settings", new SetSignInSettingsRequest(held.Version,
            [FleetSignInMethods.Company, FleetSignInMethods.Passkey, FleetSignInMethods.Microsoft, FleetSignInMethods.GitHub],
            new CompanySignInSettings(provider.Issuer.AbsoluteUri, provider.ClientId, held.Company is null ? provider.ClientSecret ?? string.Empty : null, "Contoso"), requireSso));
        Assert.AreEqual(HttpStatusCode.OK, set.Status, set.Body.ToString());
        return set.Read<SignInSettingsDto>();
    }

    private static async Task<(StartSignInResponse Start, Uri Back)> StartAsync(ServerFixture server, TestDevice device, string method, string? purpose = null)
    {
        var started = await server.SendAsync(device, HttpMethod.Post, "/api/v1/sign-in/start", new StartSignInRequest(method, device.PublicKey, Redirect, purpose));
        Assert.AreEqual(HttpStatusCode.OK, started.Status, started.Body.ToString());
        var start = started.Read<StartSignInResponse>();
        return (start, await FakeOidcProvider.AuthorizeAsync(start.AuthorizationUrl));
    }

    private static Task<ApiAnswer> CompleteAsync(ServerFixture server, TestDevice device, StartSignInResponse start, Uri back)
        => server.SendAsync(device, HttpMethod.Post, "/api/v1/sign-in/complete",
            new CompleteSignInRequest(start.AttemptId, FakeOidcProvider.Query(back, "code") ?? string.Empty, FakeOidcProvider.Query(back, "state") ?? string.Empty));

    private static async Task<Uri> PasskeyCeremonyAsync(ServerFixture server, StartSignInResponse start, string ceremony, Func<string, string> answer)
    {
        var options = await server.SendAsync(null, HttpMethod.Post, "/api/v1/sign-in/passkey/options", new PasskeyOptionsRequest(start.AttemptId));
        Assert.AreEqual(HttpStatusCode.OK, options.Status, options.Body.ToString());
        var read = options.Read<PasskeyOptionsResponse>();
        Assert.AreEqual(ceremony, read.Ceremony);
        var finish = await server.SendAsync(null, HttpMethod.Post, "/api/v1/sign-in/passkey/finish", new PasskeyFinishRequest(start.AttemptId, answer(read.OptionsJson)));
        Assert.AreEqual(HttpStatusCode.OK, finish.Status, finish.Body.ToString());
        var back = new Uri(finish.Read<PasskeyFinishResponse>().RedirectUrl);
        StringAssert.StartsWith(back.AbsoluteUri, Redirect + "?code=");
        return back;
    }

    private static async Task RegisterPasskeyAsync(ServerFixture server, TestDevice device, SoftwareAuthenticator authenticator, string origin)
    {
        var start = (await server.SendAsync(device, HttpMethod.Post, "/api/v1/sign-in/start",
            new StartSignInRequest(FleetSignInMethods.Passkey, device.PublicKey, Redirect, FleetSignInPurposes.Link))).Read<StartSignInResponse>();
        var back = await PasskeyCeremonyAsync(server, start, "create", options => authenticator.Create(options, origin));
        var linked = await CompleteAsync(server, device, start, back);
        Assert.AreEqual(HttpStatusCode.OK, linked.Status, linked.Body.ToString());
    }
}
