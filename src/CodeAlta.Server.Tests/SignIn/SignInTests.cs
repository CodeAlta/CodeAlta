using System.Net;
using System.Text.Json;
using CodeAlta.Fleet.Identity;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Fleet.Protocol.Api;
using CodeAlta.Fleet.Server;
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
        Assert.AreEqual("select_account", authorization.GetValueOrDefault("prompt"), "the person chooses the account of the company, not the browser's last one");
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
    public async Task ACompanyProviderThatTakesItsSecretInTheHeaderOnlyIsServed()
    {
        await using var provider = await FakeOidcProvider.StartAsync(clientSecret: "okta-secret");
        provider.BasicAuthenticationOnly = true;
        await using var server = await ServerFixture.StartAsync(signIn: Options(provider));
        using var owner = new TestDevice("Desktop");
        using var laptop = new TestDevice("Laptop");
        await server.SetUpAsync(owner);
        await ConfigureCompanyAsync(server, owner, provider, requireSso: false);
        var (start, back) = await StartAsync(server, laptop, FleetSignInMethods.Company);
        var completed = await CompleteAsync(server, laptop, start, back);
        Assert.AreEqual(HttpStatusCode.OK, completed.Status, completed.Body.ToString());
        Assert.AreEqual(0, provider.RefusedTokens);
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
            new CompleteSignInRequest(next.AttemptId, FakeOidcProvider.Query(back, "code")!, next.State!));
        Assert.AreEqual(SignInErrorCodes.CodeRefused, replayed.Code, replayed.Body.ToString());

        // A browser that comes back with the state of another sign-in.
        var (third, thirdBack) = await StartAsync(server, laptop, FleetSignInMethods.Company);
        var crossed = await server.SendAsync(laptop, HttpMethod.Post, "/api/v1/sign-in/complete",
            new CompleteSignInRequest(third.AttemptId, FakeOidcProvider.Query(thirdBack, "code")!, start.State!));
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
    public async Task TheOwnerApprovesAPersonsFirstDevice_EvenWhenTheirDesktopIsTheOnlyDeviceOfTheList_AndOnlyTheFirst()
    {
        await using var provider = await FakeOidcProvider.StartAsync(clientSecret: "company-secret");
        await using var server = await ServerFixture.StartAsync(signIn: Options(provider));
        using var root = new TempDirectory();
        using var desktopKey = DeviceIdentity.LoadOrCreate(Path.Combine(root.Path, "desktop"));
        using var owner = TestDevice.From(desktopKey);
        using var desktop = new FleetServerClient(server.BaseAddress, desktopKey);
        using var laptop = new TestDevice("Laptop");
        using var phone = new TestDevice("Second laptop");
        using var stranger = new TestDevice("Other laptop");
        // The owner's desktop alone in the device list: no node joined yet, and its first entry names no person.
        var setup = await server.SetUpAsync(owner);
        await ConfigureCompanyAsync(server, owner, provider, requireSso: false);

        async Task<CompleteSignInResponse> EnrollAsync(TestDevice device)
        {
            var (start, back) = await StartAsync(server, device, FleetSignInMethods.Company);
            var signedIn = (await CompleteAsync(server, device, start, back)).Read<CompleteSignInResponse>();
            Assert.AreEqual(HttpStatusCode.Created, (await server.SendAsync(device, HttpMethod.Post, "/api/v1/enroll",
                new EnrollRequest(string.Empty, device.PublicKey, device.Name, null, signedIn.Grant))).Status);
            return signedIn;
        }

        Task<ApiAnswer> ApproveAsync(TestDevice signer, TestDevice device, string person) => server.NextEntryAsync(signer, FleetDeviceListOperations.Add, device.DeviceId,
            device.PublicKey, FleetDeviceKinds.Controller, person, setup.Organization.Id).ContinueWith(entry => server.SendAsync(signer, HttpMethod.Post,
                $"/api/v1/devices/{device.DeviceId}/approve", new DeviceListChangeRequest(entry.Result))).Unwrap();

        // An entry that does not name its signer's person would give the owner's desktop to the person it adds: refused.
        var pat = await EnrollAsync(laptop);
        Assert.AreEqual(HttpStatusCode.Forbidden, (await ApproveAsync(owner, laptop, pat.Person.Id)).Status);

        // The owner's desktop signs the first device of a person the company sign-in created, naming its own person.
        Assert.AreEqual(DeviceStatuses.Approved, (await desktop.ApproveAsync(laptop.DeviceId, FleetDeviceKinds.Controller, CancellationToken.None)).Status);

        // Their next device is theirs to approve, not the owner's.
        Assert.AreEqual(pat.Person.Id, (await EnrollAsync(phone)).Person.Id);
        Assert.AreEqual(HttpStatusCode.Forbidden, (await ApproveAsync(owner, phone, pat.Person.Id)).Status, "the owner signs only a person's first device");
        Assert.AreEqual(HttpStatusCode.OK, (await ApproveAsync(laptop, phone, pat.Person.Id)).Status);

        // Another person's first device: the owner again, never another person's device.
        provider.Account = new FakeAccount("user-9", "kim@contoso.example", "Kim Park", []);
        var kim = await EnrollAsync(stranger);
        Assert.AreEqual(HttpStatusCode.Forbidden, (await ApproveAsync(laptop, stranger, kim.Person.Id)).Status, "a member does not sign another person's first device");
        Assert.AreEqual(DeviceStatuses.Approved, (await desktop.ApproveAsync(stranger.DeviceId, FleetDeviceKinds.Controller, CancellationToken.None)).Status);

        // Every device replays the list with the same rules: the desktop is the owner's, each laptop its person's.
        var list = (await server.SendAsync(owner, HttpMethod.Get, "/api/v1/device-list")).Read<ListResponse<DeviceListEntryDto>>();
        var verified = FleetDeviceListVerifier.Verify(list.Items.Select(static item => item.Entry), setup.Organization.Id);
        Assert.IsTrue(verified.IsValid, verified.Reason);
        Assert.AreEqual((setup.Owner.Id, pat.Person.Id, pat.Person.Id, kim.Person.Id), (verified.State.Members[owner.DeviceId].OwnerId,
            verified.State.Members[laptop.DeviceId].OwnerId, verified.State.Members[phone.DeviceId].OwnerId, verified.State.Members[stranger.DeviceId].OwnerId));
    }

    [TestMethod]
    public async Task AMethodTheServerDoesNotOfferIsRefused()
    {
        // A server whose Microsoft and Google clients are placeholders (a build without them) does not offer them.
        await using var server = await ServerFixture.StartAsync(signIn: new SignInOptions
        {
            Microsoft = new SignInClient(PublicSignInClients.PlaceholderPrefix + "microsoft"),
            Google = new SignInClient(PublicSignInClients.PlaceholderPrefix + "google", "secret"),
        });
        using var owner = new TestDevice("Desktop");
        using var laptop = new TestDevice("Laptop");
        await server.SetUpAsync(owner);
        var methods = (await server.SendAsync(null, HttpMethod.Get, "/api/v1/sign-in/methods")).Read<SignInMethodsResponse>();
        CollectionAssert.AreEquivalent(new[] { FleetSignInMethods.Passkey, FleetSignInMethods.GitHub }, methods.Methods.Select(static method => method.Id).ToArray());
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
    public async Task AnIdentityWithoutAName_IsNamedFromItsFirstAndLastName_ElseItsEmail()
    {
        await using var provider = await FakeOidcProvider.StartAsync(clientSecret: "company-secret");
        await using var server = await ServerFixture.StartAsync(signIn: Options(provider));
        using var owner = new TestDevice("Desktop");
        using var node = new TestDevice("Mac mini");
        using var laptop = new TestDevice("Laptop");
        using var other = new TestDevice("Other laptop");
        await server.SetUpWithNodeAsync(owner, node);
        await ConfigureCompanyAsync(server, owner, provider, requireSso: false);

        // No name claim, but its parts (the optional claims of a Microsoft personal account): the person is named from them.
        provider.Account = new FakeAccount("user-2", "pat@contoso.example", null, [], GivenName: "Pat", FamilyName: "Lee");
        var (start, back) = await StartAsync(server, laptop, FleetSignInMethods.Company);
        var completed = await CompleteAsync(server, laptop, start, back);
        Assert.AreEqual(HttpStatusCode.OK, completed.Status, completed.Body.ToString());
        Assert.AreEqual("Pat Lee", completed.Read<CompleteSignInResponse>().Person.DisplayName);

        // Neither: the email.
        provider.Account = new FakeAccount("user-3", "kim@contoso.example", null, []);
        (start, back) = await StartAsync(server, other, FleetSignInMethods.Company);
        completed = await CompleteAsync(server, other, start, back);
        Assert.AreEqual(HttpStatusCode.OK, completed.Status, completed.Body.ToString());
        Assert.AreEqual("kim@contoso.example", completed.Read<CompleteSignInResponse>().Person.DisplayName);
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

        // Or the recovery key confirms the device the person signed in on, when every device is lost. The owner, who has a
        // way in, signs in again with it to make the recovery key.
        var recoveryEntry = await server.NextEntryAsync(owner, FleetDeviceListOperations.Add, recovery.DeviceId, recovery.PublicKey, FleetDeviceKinds.Recovery, setup.Owner.Id,
            setup.Organization.Id);
        Assert.AreEqual(HttpStatusCode.Created, (await server.SendAsync(owner, HttpMethod.Post, "/api/v1/recovery-keys",
            new CreateRecoveryKeyRequest(recovery.PublicKey, recoveryEntry, "sealed-anchor", await ConfirmWithAsync(server, owner, FleetSignInMethods.Microsoft)))).Status);
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
    public async Task GitHubSignsInWithTheDeviceFlow_PolledAtItsInterval_AndKeepsOnlyTheIdAndTheName()
    {
        await using var github = await FakeGitHubDeviceFlow.StartAsync();
        await using var server = await ServerFixture.StartAsync(signIn: GitHubOptions(github));
        using var owner = new TestDevice("Desktop");
        using var laptop = new TestDevice("Laptop");
        var setup = await server.SetUpAsync(owner);
        var account = new FakeAccount("octo-alex", "alex@contoso.example", "Alex Martin", [], 4242);

        // The owner links their GitHub account from their desktop: a code to enter at GitHub, no redirect, no state.
        var link = await StartCodeAsync(server, owner, FleetSignInPurposes.Link);
        Assert.AreEqual((github.UserCodes.Last(), github.Address.AbsoluteUri.TrimEnd('/') + "/login/device", (string?)null, 5),
            (link.UserCode, link.AuthorizationUrl, link.State, link.PollInterval));

        // The server asks GitHub no faster than GitHub asked, whatever the device sends.
        var early = (await PollAsync(server, owner, link)).Read<PollSignInResponse>();
        Assert.AreEqual((true, 5, 0), (early.Pending, early.PollInterval, github.Polls), "the interval passes before the first request for a token");
        server.Clock.Advance(TimeSpan.FromSeconds(5));
        Assert.IsTrue((await PollAsync(server, owner, link)).Read<PollSignInResponse>().Pending);
        Assert.AreEqual(1, github.Polls, "authorization_pending");
        github.SlowDown();
        server.Clock.Advance(TimeSpan.FromSeconds(5));
        var slowed = (await PollAsync(server, owner, link)).Read<PollSignInResponse>();
        Assert.AreEqual((true, 10, 2), (slowed.Pending, slowed.PollInterval, github.Polls), "slow_down adds five seconds");
        server.Clock.Advance(TimeSpan.FromSeconds(5));
        Assert.IsTrue((await PollAsync(server, owner, link)).Read<PollSignInResponse>().Pending);
        Assert.AreEqual(2, github.Polls, "the longer interval holds");

        github.Approve(link.UserCode!, account);
        server.Clock.Advance(TimeSpan.FromSeconds(5));
        var linkedAnswer = await PollAsync(server, owner, link);
        var linked = linkedAnswer.Read<PollSignInResponse>();
        Assert.AreEqual((false, true, setup.Owner.Id), (linked.Pending, linked.Completed!.Linked, linked.Completed.Person.Id), linkedAnswer.Body.ToString());
        Assert.AreEqual(SignInErrorCodes.AttemptInvalid, (await PollAsync(server, owner, link)).Code, "a sign-in completes once");

        // Kept: the numeric id and the name; never the login, the email or the user token.
        var login = await ((ISignInStore)server.Server.Store).FindLoginAsync(setup.Organization.Id, github.Address.GetLeftPart(UriPartial.Authority), "4242");
        Assert.AreEqual((setup.Owner.Id, "Alex Martin", (string?)null), (login?.PersonId, login?.Name, login?.Email));
        var logins = (await server.SendAsync(owner, HttpMethod.Get, "/api/v1/sign-in/logins")).Body.ToString();
        Assert.IsFalse(logins.Contains("octo-alex", StringComparison.Ordinal) || logins.Contains("alex@contoso.example", StringComparison.Ordinal), logins);
        Assert.IsFalse(github.Tokens.Any(token => linkedAnswer.Body.ToString().Contains(token, StringComparison.Ordinal)), "the user token never reaches the device");

        // On a new laptop the same account proves the owner, and the grant enrolls that laptop, pending.
        var start = await StartCodeAsync(server, laptop);
        github.Approve(start.UserCode!, account);
        server.Clock.Advance(TimeSpan.FromSeconds(5));
        var signedIn = (await PollAsync(server, laptop, start)).Read<PollSignInResponse>().Completed!;
        Assert.AreEqual((setup.Owner.Id, true, false), (signedIn.Person.Id, signedIn.Person.HasDevices, signedIn.Linked));
        var enrolled = await server.SendAsync(laptop, HttpMethod.Post, "/api/v1/enroll", new EnrollRequest(string.Empty, laptop.PublicKey, laptop.Name, null, signedIn.Grant));
        Assert.AreEqual(HttpStatusCode.Created, enrolled.Status, enrolled.Body.ToString());
        Assert.AreEqual(DeviceStatuses.Pending, enrolled.Read<EnrollResponse>().Device.Status, "a sign-in grants nothing by itself");
        Assert.AreEqual(0, github.Secrets, "no client secret is ever sent");
    }

    [TestMethod]
    public async Task WhatGitHubRefusesEndsTheSignIn_AndAnAttemptWithACodeIsPolledOnlyByItsDevice()
    {
        await using var github = await FakeGitHubDeviceFlow.StartAsync();
        await using var server = await ServerFixture.StartAsync(signIn: GitHubOptions(github));
        using var owner = new TestDevice("Desktop");
        using var laptop = new TestDevice("Laptop");
        using var thief = new TestDevice("Elsewhere");
        await server.SetUpAsync(owner);
        github.Interval = 1;

        var cases = new (Action<string> Decide, HttpStatusCode Status, string Code)[]
        {
            (github.Deny, HttpStatusCode.Unauthorized, SignInErrorCodes.Denied),
            (github.Expire, HttpStatusCode.Unauthorized, SignInErrorCodes.Expired),
            (code => github.Approve(code, new FakeAccount("someone", "s@example.com", "Someone", [], 999)), HttpStatusCode.Forbidden, SignInErrorCodes.NotLinked),
        };
        foreach (var (decide, status, code) in cases)
        {
            var start = await StartCodeAsync(server, laptop);
            Assert.AreEqual(SignInErrorCodes.AttemptInvalid, (await PollAsync(server, thief, start)).Code, "another device does not poll the attempt");
            Assert.AreEqual(SignInErrorCodes.AttemptInvalid, (await server.SendAsync(laptop, HttpMethod.Post, "/api/v1/sign-in/complete",
                new CompleteSignInRequest(start.AttemptId, "a-code", "a-state"))).Code, "an attempt with a code completes by polling");
            decide(start.UserCode!);
            server.Clock.Advance(TimeSpan.FromSeconds(1));
            Assert.AreEqual((status, code), Answer(await PollAsync(server, laptop, start)), code);
            Assert.AreEqual(SignInErrorCodes.AttemptInvalid, (await PollAsync(server, laptop, start)).Code, "a refusal ends the attempt");
        }

        // An attempt with a code expires with the server's attempt.
        var late = await StartCodeAsync(server, laptop);
        server.Clock.Advance(TimeSpan.FromMinutes(11));
        Assert.AreEqual(SignInErrorCodes.AttemptInvalid, (await PollAsync(server, laptop, late)).Code);

        // A sign-in through a redirect is not polled; a link needs an approved device; an app without the device flow is refused at the start.
        var passkey = (await server.SendAsync(laptop, HttpMethod.Post, "/api/v1/sign-in/start", new StartSignInRequest(FleetSignInMethods.Passkey, laptop.PublicKey, Redirect)))
            .Read<StartSignInResponse>();
        Assert.AreEqual(SignInErrorCodes.AttemptInvalid, (await PollAsync(server, laptop, passkey)).Code);
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await server.SendAsync(laptop, HttpMethod.Post, "/api/v1/sign-in/start",
            new StartSignInRequest(FleetSignInMethods.GitHub, laptop.PublicKey, null, FleetSignInPurposes.Link))).Status, "a device the server does not know links nothing");
        github.DeviceFlowDisabled = true;
        var disabled = await server.SendAsync(laptop, HttpMethod.Post, "/api/v1/sign-in/start", new StartSignInRequest(FleetSignInMethods.GitHub, laptop.PublicKey, null));
        Assert.AreEqual((HttpStatusCode.BadGateway, SignInErrorCodes.ProviderUnavailable), Answer(disabled), disabled.Body.ToString());
        StringAssert.Contains(disabled.Body.ToString(), "device flow");

        var audit = (await server.SendAsync(owner, HttpMethod.Get, "/api/v1/audit?limit=500")).Read<ListResponse<AuditRecordDto>>().Items;
        foreach (var (_, _, code) in cases) Assert.IsTrue(audit.Any(record => record.Action == "sign_in.complete" && record.Result == code), $"{code} is audited");
        Assert.AreEqual(0, github.Secrets);
    }

    [TestMethod]
    public void GitHubTakesItsClientIdAndTheAddressOfAGitHubEnterpriseServerFromTheEnvironment_NeverASecret()
    {
        var defaults = SignInOptions.FromEnvironment(static _ => null);
        Assert.AreEqual(("https://github.com/", "https://api.github.com/", (string?)null), (defaults.GitHubAddress.AbsoluteUri, defaults.GitHubApiAddress.AbsoluteUri,
            defaults.GitHub.ClientSecret));
        Assert.AreEqual(PublicSignInClients.GitHubClientId, defaults.GitHub.ClientId);
        Assert.IsFalse(PublicSignInClients.IsPlaceholder(defaults.GitHub.ClientId), "the CodeAlta GitHub App is registered");
        // The Microsoft app of the CodeAlta project is registered too, a public client without secret.
        Assert.IsFalse(PublicSignInClients.IsPlaceholder(defaults.Microsoft.ClientId), "the CodeAlta Microsoft app is registered");
        Assert.IsNull(defaults.Microsoft.ClientSecret);

        var variables = new Dictionary<string, string>
        {
            ["CODEALTA_SIGNIN_GITHUB_CLIENT_ID"] = " Iv23liCodeAlta ", ["CODEALTA_SIGNIN_GITHUB_CLIENT_SECRET"] = "ignored", ["CODEALTA_SIGNIN_GITHUB_URL"] = "https://github.contoso.example/",
        };
        var enterprise = SignInOptions.FromEnvironment(name => variables.GetValueOrDefault(name));
        Assert.AreEqual(("Iv23liCodeAlta", (string?)null), (enterprise.GitHub.ClientId, enterprise.GitHub.ClientSecret));
        Assert.AreEqual(("https://github.contoso.example/", "https://github.contoso.example/api/v3"), (enterprise.GitHubAddress.AbsoluteUri, enterprise.GitHubApiAddress.AbsoluteUri));

        variables["CODEALTA_SIGNIN_GITHUB_URL"] = "https://github.com";
        Assert.AreEqual("https://api.github.com/", SignInOptions.FromEnvironment(name => variables.GetValueOrDefault(name)).GitHubApiAddress.AbsoluteUri);
        variables["CODEALTA_SIGNIN_GITHUB_URL"] = "http://github.contoso.example";
        Assert.ThrowsExactly<ArgumentException>(() => SignInOptions.FromEnvironment(name => variables.GetValueOrDefault(name)));
    }

    [TestMethod]
    public void TheGoogleClientIsBuiltInOnlyFromTheVariablesOfTheBuild_BothOrNeither()
    {
        // The build writes Google's client id and desktop secret from CODEALTA_SIGNIN_GOOGLE_CLIENT_ID and _CLIENT_SECRET (secrets
        // of the CI, so that the public repository never holds them). The tests run in the environment of the build: with the
        // variables, the built-in client is theirs; without them, it is a placeholder, and the server does not offer Google.
        var id = Environment.GetEnvironmentVariable("CODEALTA_SIGNIN_GOOGLE_CLIENT_ID");
        var secret = Environment.GetEnvironmentVariable("CODEALTA_SIGNIN_GOOGLE_CLIENT_SECRET");
        Assert.AreEqual(PublicSignInClients.IsPlaceholder(PublicSignInClients.GoogleClientId), PublicSignInClients.IsPlaceholder(PublicSignInClients.GoogleClientSecret),
            "the id and the secret come together");
        if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(secret))
        {
            Assert.AreEqual(PublicSignInClients.GoogleClientId, id.Trim(), "the build carries the client id it was given");
            Assert.AreEqual(PublicSignInClients.GoogleClientSecret, secret.Trim(), "the build carries the secret it was given");
        }
        else
        {
            Assert.IsTrue(PublicSignInClients.IsPlaceholder(PublicSignInClients.GoogleClientId), "a build without the variables has no Google client");
        }
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
            new CompleteSignInRequest(guessed.AttemptId, "not-the-code", guessed.State!))).Code);

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
        var (link, linkBack) = await StartAsync(server, owner, FleetSignInMethods.Microsoft, FleetSignInPurposes.Link, await ConfirmAsync(server, owner, ownerKey));
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
        await RegisterPasskeyAsync(server, memberLaptop, memberKey, origin, await ConfirmWithAsync(server, memberLaptop, FleetSignInMethods.Company));
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
    public async Task CompanySignInLetsInOnlyTheVerifiedDomainsAndTheTenantsTheOwnerNamed()
    {
        await using var provider = await FakeOidcProvider.StartAsync(clientSecret: "company-secret");
        await using var server = await ServerFixture.StartAsync(signIn: Options(provider));
        using var owner = new TestDevice("Desktop");
        using var laptop = new TestDevice("Laptop");
        var setup = await server.SetUpAsync(owner);

        // The owner names who the company's provider lets in: nothing named is refused, as Entra ID without a tenant.
        var held = (await server.SendAsync(owner, HttpMethod.Get, "/api/v1/sign-in/settings")).Read<SignInSettingsDto>();
        var open = await server.SendAsync(owner, HttpMethod.Put, "/api/v1/sign-in/settings", new SetSignInSettingsRequest(held.Version, [FleetSignInMethods.Company],
            new CompanySignInSettings(provider.Issuer.AbsoluteUri, provider.ClientId, provider.ClientSecret), false));
        Assert.AreEqual((HttpStatusCode.BadRequest, SignInErrorCodes.SettingsInvalid), (open.Status, open.Code), "a company sign-in that lets every account in");
        var entra = await server.SendAsync(owner, HttpMethod.Put, "/api/v1/sign-in/settings", new SetSignInSettingsRequest(held.Version, [FleetSignInMethods.Company],
            new CompanySignInSettings("https://login.microsoftonline.com/organizations/v2.0", "client", "secret", AllowedDomains: ["contoso.example"]), false));
        Assert.AreEqual(SignInErrorCodes.SettingsInvalid, entra.Code, "Entra ID's emails are not verified: its tenant is named");
        var settings = await ConfigureCompanyAsync(server, owner, provider, requireSso: false, domains: [" Contoso.Example "]);
        CollectionAssert.AreEqual(new[] { "contoso.example" }, settings.Company!.AllowedDomains!.ToArray());

        // Another company's account, and one of the domain whose email the provider did not verify, are refused: no person is made.
        foreach (var outsider in new[] { new FakeAccount("eve-1", "eve@fabrikam.example", "Eve", []), new FakeAccount("eve-2", "eve@contoso.example", "Eve", [], EmailVerified: false),
                     new FakeAccount("eve-3", "eve@contoso.example.evil", "Eve", []) })
        {
            provider.Account = outsider;
            var (start, back) = await StartAsync(server, laptop, FleetSignInMethods.Company);
            var refused = await CompleteAsync(server, laptop, start, back);
            Assert.AreEqual((HttpStatusCode.Forbidden, SignInErrorCodes.IdentityNotAllowed), (refused.Status, refused.Code), outsider.Email);
        }

        Assert.AreEqual(1, (await server.Server.Store.ListPeopleAsync(setup.Organization.Id)).Count, "no person for an identity outside");
        provider.Account = new FakeAccount("sam-1", "sam@CONTOSO.example", "Sam", []);
        var (allowed, allowedBack) = await StartAsync(server, laptop, FleetSignInMethods.Company);
        Assert.AreEqual(HttpStatusCode.OK, (await CompleteAsync(server, laptop, allowed, allowedBack)).Status);

        // With tenants named, the account's tenant decides, whatever its email.
        await ConfigureCompanyAsync(server, owner, provider, requireSso: false, tenants: ["tenant-a"]);
        provider.Account = new FakeAccount("sam-1", "sam@contoso.example", "Sam", [], TenantId: "tenant-b");
        var (other, otherBack) = await StartAsync(server, laptop, FleetSignInMethods.Company);
        Assert.AreEqual(SignInErrorCodes.IdentityNotAllowed, (await CompleteAsync(server, laptop, other, otherBack)).Code, "an account linked before is still checked");
        provider.Account = new FakeAccount("sam-2", "sam@anything.example", "Sam", [], EmailVerified: false, TenantId: "TENANT-A");
        var (tenant, tenantBack) = await StartAsync(server, laptop, FleetSignInMethods.Company);
        Assert.AreEqual(HttpStatusCode.OK, (await CompleteAsync(server, laptop, tenant, tenantBack)).Status);

        var audit = (await server.SendAsync(owner, HttpMethod.Get, "/api/v1/audit?limit=500")).Read<ListResponse<AuditRecordDto>>().Items;
        Assert.AreEqual(4, audit.Count(static record => record.Result == SignInErrorCodes.IdentityNotAllowed), "every refusal is audited");
    }

    [TestMethod]
    public async Task ADomainThatCouldNotBeCheckedForWantOfAConfirmedEmailSaysSo_AndEveryOtherRefusalKeepsItsWords()
    {
        await using var provider = await FakeOidcProvider.StartAsync(clientSecret: "company-secret");
        await using var server = await ServerFixture.StartAsync(signIn: Options(provider));
        using var owner = new TestDevice("Desktop");
        using var laptop = new TestDevice("Laptop");
        await server.SetUpAsync(owner);
        await ConfigureCompanyAsync(server, owner, provider, requireSso: false, domains: ["contoso.example"], tenants: ["tenant-a"]);

        async Task<(string? Code, string? Detail)> RefusedAsync(FakeAccount account)
        {
            provider.Account = account;
            var (start, back) = await StartAsync(server, laptop, FleetSignInMethods.Company);
            var refused = await CompleteAsync(server, laptop, start, back);
            Assert.AreEqual(HttpStatusCode.Forbidden, refused.Status, refused.Body.ToString());
            return (refused.Code, refused.Body.GetProperty("detail").GetString());
        }

        // The tenant is the right one, but the provider (Entra ID) did not confirm the email, so its domain cannot be checked: the refusal says so.
        var unconfirmed = await RefusedAsync(new FakeAccount("sam-1", "sam@contoso.example", "Sam", [], EmailVerified: false, TenantId: "tenant-a"));
        Assert.AreEqual(SignInErrorCodes.IdentityNotAllowed, unconfirmed.Code);
        StringAssert.Contains(unconfirmed.Detail, "did not confirm this account's email");
        StringAssert.Contains(unconfirmed.Detail, "let in its tenant instead");

        // A tenant that is not named, a confirmed email of another domain: the words stay the generic ones.
        foreach (var account in new[] { new FakeAccount("sam-2", "sam@contoso.example", "Sam", [], EmailVerified: false, TenantId: "tenant-b"),
                     new FakeAccount("sam-3", "sam@fabrikam.example", "Sam", [], TenantId: "tenant-a") })
        {
            var generic = await RefusedAsync(account);
            Assert.AreEqual(SignInErrorCodes.IdentityNotAllowed, generic.Code);
            Assert.AreEqual("This account is not of the domains or tenants your organization lets in.", generic.Detail, account.Subject);
        }
    }

    [TestMethod]
    public async Task ChangingTheCompanySignInNeedsTheOwnerToSignInAgain_AConfirmationWorksOnceForItsDeviceAndTenMinutes()
    {
        await using var provider = await FakeOidcProvider.StartAsync(clientSecret: "company-secret");
        await using var server = await ServerFixture.StartAsync(signIn: Options(provider));
        using var owner = new TestDevice("Desktop");
        using var node = new TestDevice("Mac mini");
        using var key = new SoftwareAuthenticator();
        await server.SetUpWithNodeAsync(owner, node);
        var origin = server.BaseAddress.GetLeftPart(UriPartial.Authority);
        await RegisterPasskeyAsync(server, owner, key, origin);
        var company = new CompanySignInSettings(provider.Issuer.AbsoluteUri, provider.ClientId, provider.ClientSecret, "Contoso", AllowedDomains: ["contoso.example"]);
        SetSignInSettingsRequest Change(long version, string? confirmation) => new(version, [FleetSignInMethods.Company, FleetSignInMethods.Passkey], company, false,
            Confirmation: confirmation);

        // A signed request of the owner's device is not enough: a hijacked session or a stolen device key would be.
        var held = (await server.SendAsync(owner, HttpMethod.Get, "/api/v1/sign-in/settings")).Read<SignInSettingsDto>();
        var unconfirmed = await server.SendAsync(owner, HttpMethod.Put, "/api/v1/sign-in/settings", Change(held.Version, null));
        Assert.AreEqual((HttpStatusCode.Forbidden, SignInErrorCodes.ConfirmationRequired), (unconfirmed.Status, unconfirmed.Code), unconfirmed.Body.ToString());
        Assert.AreEqual(HttpStatusCode.Forbidden, (await server.SendAsync(owner, HttpMethod.Put, "/api/v1/sign-in/settings", Change(held.Version, "asc_guessed"))).Status);

        // A confirmation is for the device that signed in again: another device presenting it is refused (only an owner's controller would get that far).
        var confirmation = await ConfirmAsync(server, owner, key);
        Assert.AreEqual(HttpStatusCode.Forbidden, (await server.SendAsync(node, HttpMethod.Put, "/api/v1/sign-in/settings", Change(held.Version, confirmation))).Status);
        var changed = await server.SendAsync(owner, HttpMethod.Put, "/api/v1/sign-in/settings", Change(held.Version, confirmation));
        Assert.AreEqual(HttpStatusCode.OK, changed.Status, changed.Body.ToString());
        var version = changed.Read<SignInSettingsDto>().Version;

        // It works once, and ten minutes.
        company = company with { ClientSecret = null, AllowedDomains = ["contoso.example", "fabrikam.example"] };
        Assert.AreEqual(SignInErrorCodes.ConfirmationRequired, (await server.SendAsync(owner, HttpMethod.Put, "/api/v1/sign-in/settings", Change(version, confirmation))).Code);
        var late = await ConfirmAsync(server, owner, key);
        server.Clock.Advance(TimeSpan.FromMinutes(11));
        Assert.AreEqual(SignInErrorCodes.ConfirmationRequired, (await server.SendAsync(owner, HttpMethod.Put, "/api/v1/sign-in/settings", Change(version, late))).Code);

        // A change refused for another reason does not use it up.
        var fresh = await ConfirmAsync(server, owner, key);
        Assert.AreEqual(HttpStatusCode.Conflict, (await server.SendAsync(owner, HttpMethod.Put, "/api/v1/sign-in/settings", Change(held.Version, fresh))).Status);
        Assert.AreEqual(HttpStatusCode.OK, (await server.SendAsync(owner, HttpMethod.Put, "/api/v1/sign-in/settings", Change(version, fresh))).Status);

        // The other methods are turned on and off without one.
        var now = (await server.SendAsync(owner, HttpMethod.Get, "/api/v1/sign-in/settings")).Read<SignInSettingsDto>();
        var github = await server.SendAsync(owner, HttpMethod.Put, "/api/v1/sign-in/settings", new SetSignInSettingsRequest(now.Version,
            [FleetSignInMethods.Company, FleetSignInMethods.Passkey, FleetSignInMethods.GitHub], company, false));
        Assert.AreEqual(HttpStatusCode.OK, github.Status, github.Body.ToString());

        var audit = (await server.SendAsync(owner, HttpMethod.Get, "/api/v1/audit?limit=500")).Read<ListResponse<AuditRecordDto>>().Items;
        Assert.IsTrue(audit.Count(static record => record.Action == "sign_in.settings.set" && record.Result == SignInErrorCodes.ConfirmationRequired) >= 3, "refusals are audited");
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
            new CompanySignInSettings("http://127.0.0.1:1/nothing", "client", "secret", AllowedDomains: ["contoso.example"]), false));
        Assert.AreEqual(SignInErrorCodes.ProviderUnavailable, unreachable.Code);
        var plainHttp = await server.SendAsync(owner, HttpMethod.Put, "/api/v1/sign-in/settings", new SetSignInSettingsRequest(held.Version, [FleetSignInMethods.Company],
            new CompanySignInSettings("http://idp.example", "client", "secret"), false));
        Assert.AreEqual(SignInErrorCodes.SettingsInvalid, plainHttp.Code);

        var settings = await ConfigureCompanyAsync(server, owner, provider, requireSso: false);
        Assert.IsTrue(settings.Company!.HasClientSecret);
        Assert.IsFalse(server.DatabaseHolds("company-secret"), "the client secret is sealed in the database");
        StringAssert.DoesNotMatch((await server.SendAsync(owner, HttpMethod.Get, "/api/v1/sign-in/settings")).Body.ToString(), new System.Text.RegularExpressions.Regex("company-secret"));
        var stale = await server.SendAsync(owner, HttpMethod.Put, "/api/v1/sign-in/settings", new SetSignInSettingsRequest(held.Version, [], null, false));
        Assert.AreEqual((HttpStatusCode.Conflict, SignInErrorCodes.SettingsInvalid), (stale.Status, stale.Code), "a change names the version it read");
        var audit = (await server.SendAsync(owner, HttpMethod.Get, "/api/v1/audit?limit=500")).Read<ListResponse<AuditRecordDto>>().Items;
        Assert.IsFalse(audit.Any(static record => record.Details?.Contains("company-secret", StringComparison.Ordinal) == true), "the audit log never holds the secret");
    }

    [TestMethod]
    public async Task TheGroupsOfACompanySignInGiveTheRole_AtEachSignIn_ButNeverMakeOrUnmakeAnOwner()
    {
        await using var provider = await FakeOidcProvider.StartAsync(clientSecret: "company-secret");
        await using var server = await ServerFixture.StartAsync(signIn: Options(provider));
        using var owner = new TestDevice("Desktop");
        using var laptop = new TestDevice("Laptop");
        await server.SetUpAsync(owner);
        await ConfigureCompanyAsync(server, owner, provider, requireSso: false);
        var held = (await server.SendAsync(owner, HttpMethod.Get, "/api/v1/sign-in/settings")).Read<SignInSettingsDto>();
        CompanySignInSettings Company(params GroupRoleDto[] roles) => new(provider.Issuer.AbsoluteUri, provider.ClientId, null, "Contoso", null, roles, ["contoso.example"]);
        var ownerRole = await server.SendAsync(owner, HttpMethod.Put, "/api/v1/sign-in/settings", new SetSignInSettingsRequest(held.Version,
            [FleetSignInMethods.Company, FleetSignInMethods.Passkey], Company(new GroupRoleDto("g-admins", FleetOrganizationRoles.Owner)), false));
        Assert.AreEqual((HttpStatusCode.BadRequest, SignInErrorCodes.SettingsInvalid), (ownerRole.Status, ownerRole.Code), "no group makes an owner");
        var twice = await server.SendAsync(owner, HttpMethod.Put, "/api/v1/sign-in/settings", new SetSignInSettingsRequest(held.Version,
            [FleetSignInMethods.Company, FleetSignInMethods.Passkey], Company(new GroupRoleDto("g-admins", "admin"), new GroupRoleDto("g-admins", "auditor")), false));
        Assert.AreEqual(HttpStatusCode.BadRequest, twice.Status, "a group maps to one role");
        var set = await server.SendAsync(owner, HttpMethod.Put, "/api/v1/sign-in/settings", new SetSignInSettingsRequest(held.Version,
            [FleetSignInMethods.Company, FleetSignInMethods.Passkey], Company(new GroupRoleDto("g-admins", FleetOrganizationRoles.Admin), new GroupRoleDto("g-audit", FleetOrganizationRoles.Auditor)),
            false, Confirmation: await ConfirmAsync(server, owner)));
        Assert.AreEqual(HttpStatusCode.OK, set.Status, set.Body.ToString());
        var mapped = set.Read<SignInSettingsDto>().Company!.GroupRoles!;
        Assert.AreEqual(2, mapped.Count);
        // Read again without the mapping, a change keeps it (a client that does not know it); an empty one removes it.
        var kept = await server.SendAsync(owner, HttpMethod.Put, "/api/v1/sign-in/settings", new SetSignInSettingsRequest(set.Read<SignInSettingsDto>().Version,
            [FleetSignInMethods.Company, FleetSignInMethods.Passkey], new CompanySignInSettings(provider.Issuer.AbsoluteUri, provider.ClientId, null, "Contoso"), false));
        Assert.AreEqual(2, kept.Read<SignInSettingsDto>().Company!.GroupRoles!.Count);

        // In g-admins: an admin from the first sign-in; the audit says the groups did it.
        var (start, back) = await StartAsync(server, laptop, FleetSignInMethods.Company);
        var signedIn = (await CompleteAsync(server, laptop, start, back)).Read<CompleteSignInResponse>();
        Assert.AreEqual(PersonRoles.Admin, (await server.Server.Store.GetPersonAsync(signedIn.Person.Id))!.Role);
        var audit = (await server.SendAsync(owner, HttpMethod.Get, "/api/v1/audit?limit=500")).Read<ListResponse<AuditRecordDto>>().Items;
        Assert.IsTrue(audit.Any(record => record.Action == "person.role" && record.Target == signedIn.Person.Id && record.Details?.Contains("sign-in", StringComparison.Ordinal) == true));

        // Out of g-admins, in g-audit: an auditor at the next sign-in; in no mapped group: a member.
        provider.Account = provider.Account with { Groups = ["g-engineering", "g-audit"] };
        (start, back) = await StartAsync(server, laptop, FleetSignInMethods.Company);
        await CompleteAsync(server, laptop, start, back);
        Assert.AreEqual(FleetOrganizationRoles.Auditor, (await server.Server.Store.GetPersonAsync(signedIn.Person.Id))!.Role);
        provider.Account = provider.Account with { Groups = ["g-engineering"] };
        (start, back) = await StartAsync(server, laptop, FleetSignInMethods.Company);
        await CompleteAsync(server, laptop, start, back);
        Assert.AreEqual(PersonRoles.Member, (await server.Server.Store.GetPersonAsync(signedIn.Person.Id))!.Role);

        // An owner stays an owner whatever their groups say.
        Assert.AreEqual(HttpStatusCode.OK, (await server.SendAsync(owner, HttpMethod.Put, $"/api/v1/people/{signedIn.Person.Id}/role", new SetRoleRequest(FleetOrganizationRoles.Owner))).Status);
        (start, back) = await StartAsync(server, laptop, FleetSignInMethods.Company);
        await CompleteAsync(server, laptop, start, back);
        Assert.AreEqual(PersonRoles.Owner, (await server.Server.Store.GetPersonAsync(signedIn.Person.Id))!.Role);
    }

    [TestMethod]
    public async Task APersonRemovesALinkedAccount_WhichThenSignsNobodyIn_ButNeverTheLastWayInOfAnOwnerWithoutARecoveryKey()
    {
        await using var provider = await FakeOidcProvider.StartAsync(clientSecret: "company-secret");
        await using var server = await ServerFixture.StartAsync(signIn: Options(provider));
        using var owner = new TestDevice("Desktop");
        using var laptop = new TestDevice("Laptop");
        using var recovery = new TestDevice("Recovery key");
        using var passkey = new SoftwareAuthenticator();
        var setup = await server.SetUpAsync(owner);
        var (link, linkBack) = await StartAsync(server, owner, FleetSignInMethods.Microsoft, FleetSignInPurposes.Link);
        Assert.AreEqual(HttpStatusCode.OK, (await CompleteAsync(server, owner, link, linkBack)).Status);
        async Task<IReadOnlyList<SignInLoginDto>> LoginsAsync() => (await server.SendAsync(owner, HttpMethod.Get, "/api/v1/sign-in/logins")).Read<ListResponse<SignInLoginDto>>().Items;
        var microsoft = (await LoginsAsync()).Single();
        Assert.IsFalse(string.IsNullOrEmpty(microsoft.Id), "each way to sign in has an id to remove it by");
        Assert.IsFalse(microsoft.Id!.Contains(provider.Issuer.Host, StringComparison.Ordinal), "the id does not show the issuer or the subject of the account");

        // The only way in of an owner without a recovery key stays: removing it could lock the server out.
        var last = await server.SendAsync(owner, HttpMethod.Delete, $"/api/v1/sign-in/logins/{microsoft.Id}");
        Assert.AreEqual((HttpStatusCode.Conflict, SignInErrorCodes.LastWayIn), Answer(last), last.Body.ToString());

        // With a passkey beside it, the account goes: it signs nobody in any more.
        await RegisterPasskeyAsync(server, owner, passkey, server.BaseAddress.GetLeftPart(UriPartial.Authority), await ConfirmWithAsync(server, owner, FleetSignInMethods.Microsoft));
        var removed = await server.SendAsync(owner, HttpMethod.Delete, $"/api/v1/sign-in/logins/{microsoft.Id}");
        Assert.AreEqual(HttpStatusCode.OK, removed.Status, removed.Body.ToString());
        CollectionAssert.AreEqual(new[] { FleetSignInMethods.Passkey }, removed.Read<ListResponse<SignInLoginDto>>().Items.Select(static login => login.Method).ToArray());
        var (start, back) = await StartAsync(server, laptop, FleetSignInMethods.Microsoft);
        Assert.AreEqual((HttpStatusCode.Forbidden, SignInErrorCodes.NotLinked), Answer(await CompleteAsync(server, laptop, start, back)));
        Assert.AreEqual((HttpStatusCode.NotFound, SignInErrorCodes.LoginNotFound),
            Answer(await server.SendAsync(owner, HttpMethod.Delete, $"/api/v1/sign-in/logins/{microsoft.Id}")), "removed once");

        // The passkey is now the last way in; with a recovery key, it may go too.
        var key = (await LoginsAsync()).Single();
        Assert.AreEqual(SignInErrorCodes.LastWayIn, (await server.SendAsync(owner, HttpMethod.Delete, $"/api/v1/sign-in/logins/{key.Id}")).Code);
        var entry = await server.NextEntryAsync(owner, FleetDeviceListOperations.Add, recovery.DeviceId, recovery.PublicKey, FleetDeviceKinds.Recovery, setup.Owner.Id, setup.Organization.Id);
        Assert.AreEqual(HttpStatusCode.Created, (await server.SendAsync(owner, HttpMethod.Post, "/api/v1/recovery-keys",
            new CreateRecoveryKeyRequest(recovery.PublicKey, entry, "sealed-anchor", await ConfirmAsync(server, owner, passkey)))).Status);
        Assert.AreEqual(HttpStatusCode.OK, (await server.SendAsync(owner, HttpMethod.Delete, $"/api/v1/sign-in/logins/{key.Id}")).Status);
        Assert.AreEqual(0, (await LoginsAsync()).Count);

        var audit = (await server.SendAsync(owner, HttpMethod.Get, "/api/v1/audit?limit=500")).Read<ListResponse<AuditRecordDto>>().Items;
        Assert.AreEqual(2, audit.Count(static record => record.Action == "sign_in.login.remove" && record.Result == "success"));
        Assert.AreEqual(2, audit.Count(static record => record.Action == "sign_in.login.remove" && record.Result == SignInErrorCodes.LastWayIn));
    }

    [TestMethod]
    public async Task AnAdminRemovesAnotherPersonsWayIn_AMemberDoesNot_AndACompanyAccountIsManagedByTheOrganization()
    {
        await using var provider = await FakeOidcProvider.StartAsync(clientSecret: "company-secret");
        await using var server = await ServerFixture.StartAsync(signIn: Options(provider));
        using var owner = new TestDevice("Desktop");
        using var node = new TestDevice("Mac mini");
        using var laptop = new TestDevice("Laptop");
        using var passkey = new SoftwareAuthenticator();
        var (setup, _) = await server.SetUpWithNodeAsync(owner, node);
        await ConfigureCompanyAsync(server, owner, provider, requireSso: false);

        // Pat joins through the company's sign-in on their laptop, which the owner approves; Pat adds a passkey.
        provider.Account = new FakeAccount("user-2", "pat@contoso.example", "Pat Lee", []);
        var (start, back) = await StartAsync(server, laptop, FleetSignInMethods.Company);
        var signedIn = (await CompleteAsync(server, laptop, start, back)).Read<CompleteSignInResponse>();
        var pat = signedIn.Person.Id;
        Assert.AreEqual(HttpStatusCode.Created, (await server.SendAsync(laptop, HttpMethod.Post, "/api/v1/enroll",
            new EnrollRequest(string.Empty, laptop.PublicKey, laptop.Name, null, signedIn.Grant))).Status);
        var entry = await server.NextEntryAsync(owner, FleetDeviceListOperations.Add, laptop.DeviceId, laptop.PublicKey, FleetDeviceKinds.Controller, pat, setup.Organization.Id);
        var approved = await server.SendAsync(owner, HttpMethod.Post, $"/api/v1/devices/{laptop.DeviceId}/approve", new DeviceListChangeRequest(entry));
        Assert.AreEqual(HttpStatusCode.OK, approved.Status, approved.Body.ToString());
        await RegisterPasskeyAsync(server, laptop, passkey, server.BaseAddress.GetLeftPart(UriPartial.Authority), await ConfirmWithAsync(server, laptop, FleetSignInMethods.Company));

        // The company's account is the organization's: neither Pat nor an admin removes it here (that is deprovisioning the person).
        var mine = (await server.SendAsync(laptop, HttpMethod.Get, "/api/v1/sign-in/logins")).Read<ListResponse<SignInLoginDto>>().Items;
        var company = mine.Single(static login => login.Method == FleetSignInMethods.Company);
        Assert.AreEqual((HttpStatusCode.Forbidden, SignInErrorCodes.ManagedByOrganization),
            Answer(await server.SendAsync(laptop, HttpMethod.Delete, $"/api/v1/sign-in/logins/{company.Id}")));
        Assert.AreEqual(SignInErrorCodes.ManagedByOrganization, (await server.SendAsync(owner, HttpMethod.Delete, $"/api/v1/sign-in/logins/{company.Id}?person={pat}")).Code);

        // A member neither reads nor removes another person's ways in; an owner does.
        var owners = (await server.SendAsync(owner, HttpMethod.Get, "/api/v1/sign-in/logins")).Read<ListResponse<SignInLoginDto>>().Items;
        Assert.AreEqual(HttpStatusCode.Forbidden, (await server.SendAsync(laptop, HttpMethod.Get, $"/api/v1/sign-in/logins?person={setup.Owner.Id}")).Status);
        Assert.AreEqual(HttpStatusCode.Forbidden, (await server.SendAsync(laptop, HttpMethod.Delete, $"/api/v1/sign-in/logins/{owners[0].Id}?person={setup.Owner.Id}")).Status);
        var patsKeys = (await server.SendAsync(owner, HttpMethod.Get, $"/api/v1/sign-in/logins?person={pat}")).Read<ListResponse<SignInLoginDto>>().Items;
        CollectionAssert.AreEquivalent(new[] { FleetSignInMethods.Company, FleetSignInMethods.Passkey }, patsKeys.Select(static login => login.Method).ToArray());
        var removed = await server.SendAsync(owner, HttpMethod.Delete, $"/api/v1/sign-in/logins/{patsKeys.Single(static login => login.Method == FleetSignInMethods.Passkey).Id}?person={pat}");
        Assert.AreEqual(HttpStatusCode.OK, removed.Status, removed.Body.ToString());
        Assert.AreEqual(FleetSignInMethods.Company, (await server.SendAsync(laptop, HttpMethod.Get, "/api/v1/sign-in/logins")).Read<ListResponse<SignInLoginDto>>().Items.Single().Method);
    }

    [TestMethod]
    public async Task ThePasskeyPageIsAtTheHttpsAddressATrustedProxyServes_AndOnlyATrustedOne()
    {
        // Tailscale Serve or Caddy ends TLS and forwards plain HTTP to the server on the loopback.
        await using var behind = await ServerFixture.StartAsync(configure: options => new Hosting.CoordinatorServerOptions
        {
            DataRoot = options.DataRoot, Urls = options.Urls, TimeProvider = options.TimeProvider, ConfigureLogging = options.ConfigureLogging,
            EnvironmentVariable = options.EnvironmentVariable, TrustedProxies = ["127.0.0.1", "::1"],
        });
        using var laptop = new TestDevice("Laptop");
        using var owner = new TestDevice("Desktop");
        using var other = new TestDevice("Other desktop");
        await behind.SetUpAsync(owner);
        static void Proxied(HttpRequestMessage message) => message.Headers.Add("X-Forwarded-Proto", "https");
        var start = await behind.SendAsync(laptop, HttpMethod.Post, "/api/v1/sign-in/start", new StartSignInRequest(FleetSignInMethods.Passkey, laptop.PublicKey, Redirect),
            tamper: Proxied);
        Assert.AreEqual(HttpStatusCode.OK, start.Status, start.Body.ToString());
        StringAssert.StartsWith(start.Read<StartSignInResponse>().AuthorizationUrl, $"https://{behind.BaseAddress.Authority}/sign-in/passkey#");

        // Without a trusted proxy, a caller's header names no scheme.
        await using var direct = await ServerFixture.StartAsync();
        await direct.SetUpAsync(other);
        var told = await direct.SendAsync(laptop, HttpMethod.Post, "/api/v1/sign-in/start", new StartSignInRequest(FleetSignInMethods.Passkey, laptop.PublicKey, Redirect),
            tamper: Proxied);
        StringAssert.StartsWith(told.Read<StartSignInResponse>().AuthorizationUrl, $"http://{direct.BaseAddress.Authority}/sign-in/passkey#");
    }

    [TestMethod]
    public async Task TheLastWayInCountsWhatSignsInNow_AndRequiringCompanySignInNeedsTheBreakGlassOwnersPasskey()
    {
        await using var provider = await FakeOidcProvider.StartAsync();
        await using var server = await ServerFixture.StartAsync(signIn: Options(provider));
        using var owner = new TestDevice("Desktop");
        using var key = new SoftwareAuthenticator();
        await server.SetUpAsync(owner);
        var origin = server.BaseAddress.GetLeftPart(UriPartial.Authority);
        var (link, linkBack) = await StartAsync(server, owner, FleetSignInMethods.Microsoft, FleetSignInPurposes.Link);
        Assert.AreEqual(HttpStatusCode.OK, (await CompleteAsync(server, owner, link, linkBack)).Status);
        async Task<ApiAnswer> RequireSsoAsync(string confirmation)
        {
            var held = (await server.SendAsync(owner, HttpMethod.Get, "/api/v1/sign-in/settings")).Read<SignInSettingsDto>();
            return await server.SendAsync(owner, HttpMethod.Put, "/api/v1/sign-in/settings", new SetSignInSettingsRequest(held.Version,
                [FleetSignInMethods.Company, FleetSignInMethods.Passkey, FleetSignInMethods.Microsoft],
                new CompanySignInSettings(provider.Issuer.AbsoluteUri, provider.ClientId, held.Company is null ? string.Empty : null, "Contoso", AllowedDomains: ["contoso.example"]),
                true, Confirmation: confirmation));
        }

        // Requiring company sign-in needs a passkey of the break-glass owner: their Microsoft account would sign nobody in then.
        var withoutPasskey = await RequireSsoAsync(await ConfirmWithAsync(server, owner, FleetSignInMethods.Microsoft));
        Assert.AreEqual((HttpStatusCode.BadRequest, SignInErrorCodes.SettingsInvalid), Answer(withoutPasskey), withoutPasskey.Body.ToString());
        await RegisterPasskeyAsync(server, owner, key, origin, await ConfirmWithAsync(server, owner, FleetSignInMethods.Microsoft));
        var required = await RequireSsoAsync(await ConfirmAsync(server, owner, key));
        Assert.AreEqual(HttpStatusCode.OK, required.Status, required.Body.ToString());

        // The owner has no recovery key: the passkey is their last way in that signs in while company sign-in is required.
        var logins = (await server.SendAsync(owner, HttpMethod.Get, "/api/v1/sign-in/logins")).Read<ListResponse<SignInLoginDto>>().Items;
        var passkey = logins.Single(static login => login.Method == FleetSignInMethods.Passkey);
        var refused = await server.SendAsync(owner, HttpMethod.Delete, $"/api/v1/sign-in/logins/{passkey.Id}");
        Assert.AreEqual((HttpStatusCode.Conflict, SignInErrorCodes.LastWayIn), Answer(refused), refused.Body.ToString());

        // The Microsoft account, which signs nobody in now, goes.
        var microsoft = logins.Single(static login => login.Method == FleetSignInMethods.Microsoft);
        Assert.AreEqual(HttpStatusCode.OK, (await server.SendAsync(owner, HttpMethod.Delete, $"/api/v1/sign-in/logins/{microsoft.Id}")).Status);
    }

    [TestMethod]
    public async Task ACompanyAccountCountsAsAWayInOnlyOnceItSignedInUnderTheCompanySettingsAsTheyAreNow()
    {
        await using var provider = await FakeOidcProvider.StartAsync(clientSecret: "company-secret");
        await using var other = await FakeOidcProvider.StartAsync(clientSecret: "other-secret");
        await using var server = await ServerFixture.StartAsync(signIn: Options(provider));
        using var owner = new TestDevice("Desktop");
        using var key = new SoftwareAuthenticator();
        await server.SetUpAsync(owner);
        // The owner has a passkey and a company account, and no recovery key.
        await RegisterPasskeyAsync(server, owner, key, server.BaseAddress.GetLeftPart(UriPartial.Authority));
        await ConfigureCompanyAsync(server, owner, provider, false);
        var (link, back) = await StartAsync(server, owner, FleetSignInMethods.Company, FleetSignInPurposes.Link, await ConfirmAsync(server, owner, key));
        Assert.AreEqual(HttpStatusCode.OK, (await CompleteAsync(server, owner, link, back)).Status);
        var passkey = (await server.SendAsync(owner, HttpMethod.Get, "/api/v1/sign-in/logins")).Read<ListResponse<SignInLoginDto>>().Items
            .Single(static login => login.Method == FleetSignInMethods.Passkey);

        // The company moves to another provider: the account of the one before signs in there no more, so the passkey stays
        // the owner's last way in.
        server.Clock.Advance(TimeSpan.FromSeconds(30));
        await ConfigureCompanyAsync(server, owner, other, false);
        var refused = await server.SendAsync(owner, HttpMethod.Delete, $"/api/v1/sign-in/logins/{passkey.Id}");
        Assert.AreEqual((HttpStatusCode.Conflict, SignInErrorCodes.LastWayIn), Answer(refused), refused.Body.ToString());

        // Back on the first provider, the account counts again only once it signed in under these settings.
        server.Clock.Advance(TimeSpan.FromSeconds(30));
        await ConfigureCompanyAsync(server, owner, provider, false);
        Assert.AreEqual(SignInErrorCodes.LastWayIn, (await server.SendAsync(owner, HttpMethod.Delete, $"/api/v1/sign-in/logins/{passkey.Id}")).Code, "not signed in since");
        server.Clock.Advance(TimeSpan.FromSeconds(30));
        await ConfirmWithAsync(server, owner, FleetSignInMethods.Company);
        var removed = await server.SendAsync(owner, HttpMethod.Delete, $"/api/v1/sign-in/logins/{passkey.Id}");
        Assert.AreEqual(HttpStatusCode.OK, removed.Status, removed.Body.ToString());
    }

    [TestMethod]
    public async Task ACompanySignInThatTheSettingsChangedDuring_DoesNotCountUnderTheNewSettings()
    {
        await using var provider = await FakeOidcProvider.StartAsync(clientSecret: "company-secret");
        await using var server = await ServerFixture.StartAsync(signIn: Options(provider));
        using var owner = new TestDevice("Desktop");
        using var laptop = new TestDevice("Laptop");
        using var key = new SoftwareAuthenticator();
        await server.SetUpAsync(owner);
        // The owner has their sole passkey and a company account of contoso.example, and no recovery key.
        await RegisterPasskeyAsync(server, owner, key, server.BaseAddress.GetLeftPart(UriPartial.Authority));
        await ConfigureCompanyAsync(server, owner, provider, false);
        var (link, back) = await StartAsync(server, owner, FleetSignInMethods.Company, FleetSignInPurposes.Link, await ConfirmAsync(server, owner, key));
        Assert.AreEqual(HttpStatusCode.OK, (await CompleteAsync(server, owner, link, back)).Status);
        var passkey = (await server.SendAsync(owner, HttpMethod.Get, "/api/v1/sign-in/logins")).Read<ListResponse<SignInLoginDto>>().Items
            .Single(static login => login.Method == FleetSignInMethods.Passkey);

        // The account signs in on a laptop; the provider holds its token answer while the company lets in only fabrikam.example.
        var (signIn, signInBack) = await StartAsync(server, laptop, FleetSignInMethods.Company);
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var asked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        provider.TokenDelay = async () => { asked.TrySetResult(); await held.Task; };
        var completing = CompleteAsync(server, laptop, signIn, signInBack);
        await asked.Task.WithinAsync(TestTime.Seconds(30));
        provider.TokenDelay = null;
        await ConfigureCompanyAsync(server, owner, provider, false, domains: ["fabrikam.example"]);
        server.Clock.Advance(TimeSpan.FromSeconds(30));
        held.SetResult();
        await completing;

        // That sign-in was judged by the settings before: the account is not known to sign in under these, which refuse it,
        // so the passkey stays the owner's last way in.
        var refused = await server.SendAsync(owner, HttpMethod.Delete, $"/api/v1/sign-in/logins/{passkey.Id}");
        Assert.AreEqual((HttpStatusCode.Conflict, SignInErrorCodes.LastWayIn), Answer(refused), refused.Body.ToString());
    }

    [TestMethod]
    public async Task APasskeyIsAddedOnlyAtTheSignedCompletion_AndARevokedDeviceAddsNone()
    {
        await using var server = await ServerFixture.StartAsync();
        using var owner = new TestDevice("Desktop");
        using var laptop = new TestDevice("Laptop");
        using var ownerKey = new SoftwareAuthenticator();
        using var stolen = new SoftwareAuthenticator();
        var setup = await server.SetUpAsync(owner);
        var origin = server.BaseAddress.GetLeftPart(UriPartial.Authority);
        await RegisterPasskeyAsync(server, owner, ownerKey, origin);

        // The owner's laptop, a second controller device of theirs, signed in with the passkey and approved.
        var start = (await server.SendAsync(laptop, HttpMethod.Post, "/api/v1/sign-in/start", new StartSignInRequest(FleetSignInMethods.Passkey, laptop.PublicKey, Redirect)))
            .Read<StartSignInResponse>();
        var grant = (await CompleteAsync(server, laptop, start, await PasskeyCeremonyAsync(server, start, "get", options => ownerKey.Get(options, origin))))
            .Read<CompleteSignInResponse>().Grant;
        Assert.AreEqual(HttpStatusCode.Created, (await server.SendAsync(laptop, HttpMethod.Post, "/api/v1/enroll", new EnrollRequest(string.Empty, laptop.PublicKey, laptop.Name, null, grant))).Status);
        var entry = await server.NextEntryAsync(owner, FleetDeviceListOperations.Add, laptop.DeviceId, laptop.PublicKey, FleetDeviceKinds.Controller, setup.Owner.Id, setup.Organization.Id);
        Assert.AreEqual(HttpStatusCode.OK, (await server.SendAsync(owner, HttpMethod.Post, $"/api/v1/devices/{laptop.DeviceId}/approve", new DeviceListChangeRequest(entry))).Status);

        // The laptop registers a passkey; its browser ends the ceremony: nothing is added before the laptop's signed completion.
        var register = (await server.SendAsync(laptop, HttpMethod.Post, "/api/v1/sign-in/start", new StartSignInRequest(FleetSignInMethods.Passkey, laptop.PublicKey, Redirect,
            FleetSignInPurposes.Link, await ConfirmAsync(server, laptop, ownerKey)))).Read<StartSignInResponse>();
        var created = await PasskeyCeremonyAsync(server, register, "create", options => stolen.Create(options, origin));
        Assert.AreEqual(1, (await ((ISignInStore)server.Server.Store).ListPasskeysAsync(setup.Owner.Id)).Count, "the ceremony alone adds nothing");

        // A second registration the laptop started waits for its page.
        var other = (await server.SendAsync(laptop, HttpMethod.Post, "/api/v1/sign-in/start", new StartSignInRequest(FleetSignInMethods.Passkey, laptop.PublicKey, Redirect,
            FleetSignInPurposes.Link, await ConfirmAsync(server, laptop, ownerKey)))).Read<StartSignInResponse>();

        // The laptop is revoked meanwhile: its completion adds no passkey, and the attempt that waits is forgotten.
        var removal = await server.NextEntryAsync(owner, FleetDeviceListOperations.Remove, laptop.DeviceId, null, null, null, setup.Organization.Id);
        Assert.AreEqual(HttpStatusCode.OK, (await server.SendAsync(owner, HttpMethod.Post, $"/api/v1/devices/{laptop.DeviceId}/revoke", new DeviceListChangeRequest(removal))).Status);
        var completed = await CompleteAsync(server, laptop, register, created);
        Assert.AreNotEqual(HttpStatusCode.OK, completed.Status, completed.Body.ToString());
        Assert.AreEqual(1, (await ((ISignInStore)server.Server.Store).ListPasskeysAsync(setup.Owner.Id)).Count, "a revoked device adds no way in");
        var page = await server.SendAsync(null, HttpMethod.Post, "/api/v1/sign-in/passkey/options", new PasskeyOptionsRequest(other.AttemptId));
        Assert.AreEqual(SignInErrorCodes.AttemptInvalid, page.Code, "the page of a revoked device has nothing to finish");
    }

    [TestMethod]
    public async Task AnotherDeviceOfThePersonApprovesALink_WhenThePersonHasNoWayInAnyMore()
    {
        await using var server = await ServerFixture.StartAsync();
        using var desktop = new TestDevice("Desktop");
        using var laptop = new TestDevice("Laptop");
        using var key = new SoftwareAuthenticator();
        var setup = await server.SetUpAsync(desktop);
        var origin = server.BaseAddress.GetLeftPart(UriPartial.Authority);
        var enrolled = await server.EnrollAsync(desktop, laptop, FleetDeviceKinds.Controller);
        var entry = await server.NextEntryAsync(desktop, FleetDeviceListOperations.Add, enrolled.Id, laptop.PublicKey, FleetDeviceKinds.Controller, setup.Owner.Id,
            setup.Organization.Id);
        Assert.AreEqual(HttpStatusCode.OK, (await server.SendAsync(desktop, HttpMethod.Post, $"/api/v1/devices/{laptop.DeviceId}/approve", new DeviceListChangeRequest(entry))).Status);

        // Days later, the owner has no way in and both devices joined long ago: the laptop's key alone adds none.
        server.Clock.Advance(TimeSpan.FromDays(3));
        StartSignInRequest Link(string method, string? approval = null) => new(method, laptop.PublicKey, Redirect, FleetSignInPurposes.Link, null, approval);
        Assert.AreEqual(SignInErrorCodes.FirstWayInExpired, (await server.SendAsync(laptop, HttpMethod.Post, "/api/v1/sign-in/start", Link(FleetSignInMethods.Passkey))).Code);

        // The laptop asks; it does not approve its own link, and an approval that is not signed by the approving device is refused.
        var asked = await server.SendAsync(laptop, HttpMethod.Post, "/api/v1/sign-in/link-approvals", new RequestLinkApprovalRequest(FleetSignInMethods.Passkey));
        Assert.AreEqual(HttpStatusCode.OK, asked.Status, asked.Body.ToString());
        var approval = asked.Read<SignInLinkApprovalDto>();
        Assert.AreEqual((FleetSignInMethods.Passkey, "pending", FleetSignInApprovals.LinkDigest(setup.Organization.Id, laptop.DeviceId, FleetSignInMethods.Passkey)),
            (approval.Method, approval.Status, approval.Digest));
        FleetSignedSignInApproval Signed(TestDevice signer, string digest)
        {
            var payload = FleetSignInApprovals.Serialize(new FleetSignInApproval(1, setup.Organization.Id, approval.Id, laptop.DeviceId, digest, signer.DeviceId, server.Clock.Now));
            return new FleetSignedSignInApproval(payload, signer.Sign(FleetSignInApprovals.GetSignedBytes(payload)));
        }

        var listed = (await server.SendAsync(desktop, HttpMethod.Get, "/api/v1/sign-in/link-approvals")).Read<ListResponse<SignInLinkApprovalDto>>().Items;
        Assert.AreEqual((approval.Id, "Laptop"), (listed.Single().Id, listed.Single().RequesterName));
        Assert.AreEqual(HttpStatusCode.Forbidden, (await server.SendAsync(laptop, HttpMethod.Post, $"/api/v1/sign-in/link-approvals/{approval.Id}/approve",
            Signed(laptop, approval.Digest))).Status, "the device that links does not approve it");
        Assert.AreEqual(HttpStatusCode.BadRequest, (await server.SendAsync(desktop, HttpMethod.Post, $"/api/v1/sign-in/link-approvals/{approval.Id}/approve",
            Signed(desktop, FleetSignInApprovals.LinkDigest(setup.Organization.Id, laptop.DeviceId, FleetSignInMethods.Microsoft)))).Status, "the digest of another link");
        Assert.AreEqual(SignInErrorCodes.ConfirmationRequired, (await server.SendAsync(laptop, HttpMethod.Post, "/api/v1/sign-in/start",
            Link(FleetSignInMethods.Passkey, approval.Id))).Code, "an approval that waits approves nothing");

        // The desktop approves it: the laptop registers its passkey with it, once, and for that method only.
        var approved = await server.SendAsync(desktop, HttpMethod.Post, $"/api/v1/sign-in/link-approvals/{approval.Id}/approve", Signed(desktop, approval.Digest));
        Assert.AreEqual(HttpStatusCode.OK, approved.Status, approved.Body.ToString());
        Assert.AreEqual(("approved", desktop.DeviceId), (approved.Read<SignInLinkApprovalDto>().Status, approved.Read<SignInLinkApprovalDto>().ApprovedBy));
        Assert.AreEqual(SignInErrorCodes.ConfirmationRequired, (await server.SendAsync(laptop, HttpMethod.Post, "/api/v1/sign-in/start",
            Link(FleetSignInMethods.Microsoft, approval.Id))).Code, "an approval is of its method");
        var started = await server.SendAsync(laptop, HttpMethod.Post, "/api/v1/sign-in/start", Link(FleetSignInMethods.Passkey, approval.Id));
        Assert.AreEqual(HttpStatusCode.OK, started.Status, started.Body.ToString());
        var start = started.Read<StartSignInResponse>();
        var created = await PasskeyCeremonyAsync(server, start, "create", options => key.Create(options, origin));
        Assert.AreEqual(HttpStatusCode.OK, (await CompleteAsync(server, laptop, start, created)).Status);
        Assert.AreEqual(1, (await ((ISignInStore)server.Server.Store).ListPasskeysAsync(setup.Owner.Id)).Count);
        Assert.AreEqual(SignInErrorCodes.ConfirmationRequired, (await server.SendAsync(laptop, HttpMethod.Post, "/api/v1/sign-in/start",
            Link(FleetSignInMethods.Passkey, approval.Id))).Code, "an approval works once");

        var audit = (await server.SendAsync(desktop, HttpMethod.Get, "/api/v1/audit?limit=500")).Read<ListResponse<AuditRecordDto>>().Items;
        Assert.AreEqual(1, audit.Count(static record => record.Action == "sign_in.link_approval.approve" && record.Result == AuditEntry.Success));
    }

    [TestMethod]
    public async Task TheRecoveryKeyOfTheOwnerApprovesALinkOnTheirOnlyComputer_NoOneElsesDoes_AndFailedAttemptsAreLimited()
    {
        await using var server = await ServerFixture.StartAsync(configure: options => new Hosting.CoordinatorServerOptions
        {
            DataRoot = options.DataRoot, Urls = options.Urls, TimeProvider = options.TimeProvider, ConfigureLogging = options.ConfigureLogging,
            EnvironmentVariable = options.EnvironmentVariable,
            RateLimits = new AuthenticationRateLimits { RecoverPerPerson = new RateLimit(4, TimeSpan.FromMinutes(15)), PersonSlowPath = TimeSpan.FromHours(1) },
        });
        using var desktop = new TestDevice("Desktop");
        using var recovery = new TestDevice("Recovery key");
        using var stranger = new TestDevice("Not a recovery key");
        using var key = new SoftwareAuthenticator();
        var setup = await server.SetUpAsync(desktop);
        var origin = server.BaseAddress.GetLeftPart(UriPartial.Authority);
        async Task AddRecoveryKeyAsync(TestDevice device, TestDevice recoveryKey, string personId)
        {
            var entry = await server.NextEntryAsync(device, FleetDeviceListOperations.Add, recoveryKey.DeviceId, recoveryKey.PublicKey, FleetDeviceKinds.Recovery, personId,
                setup.Organization.Id);
            var added = await server.SendAsync(device, HttpMethod.Post, "/api/v1/recovery-keys", new CreateRecoveryKeyRequest(recoveryKey.PublicKey, entry, "sealed-anchor"));
            Assert.AreEqual(HttpStatusCode.Created, added.Status, added.Body.ToString());
        }

        await AddRecoveryKeyAsync(desktop, recovery, setup.Owner.Id);
        // Sam, another person of the organization, with a recovery key of their own.
        var (samDevice, samId) = await AccessControlTests.AddPersonAsync(server, desktop, setup, "Sam");
        using var sam = samDevice;
        using var samRecovery = new TestDevice("Sam's recovery key");
        await AddRecoveryKeyAsync(sam, samRecovery, samId);

        // Days later, on the only computer of the owner, who never linked a way in: the device key alone adds none.
        server.Clock.Advance(TimeSpan.FromDays(3));
        Assert.AreEqual(SignInErrorCodes.FirstWayInExpired, (await server.SendAsync(desktop, HttpMethod.Post, "/api/v1/sign-in/start",
            new StartSignInRequest(FleetSignInMethods.Passkey, desktop.PublicKey, Redirect, FleetSignInPurposes.Link))).Code);
        var approval = (await server.SendAsync(desktop, HttpMethod.Post, "/api/v1/sign-in/link-approvals", new RequestLinkApprovalRequest(FleetSignInMethods.Passkey)))
            .Read<SignInLinkApprovalDto>();
        FleetSignedSignInApproval Signed(TestDevice signer, string? digest = null)
        {
            var payload = FleetSignInApprovals.Serialize(new FleetSignInApproval(1, setup.Organization.Id, approval.Id, desktop.DeviceId, digest ?? approval.Digest, signer.DeviceId,
                server.Clock.Now));
            return new FleetSignedSignInApproval(payload, signer.Sign(FleetSignInApprovals.GetSignedBytes(payload)));
        }

        // The approval is signed by the recovery key on the computer that has its words: the request names no device of the server.
        Task<ApiAnswer> ApproveAsync(FleetSignedSignInApproval signed) => server.SendAsync(null, HttpMethod.Post, $"/api/v1/sign-in/link-approvals/{approval.Id}/recovery-key", signed);
        Assert.AreEqual(HttpStatusCode.Forbidden, (await ApproveAsync(Signed(samRecovery))).Status, "the recovery key of another person");
        Assert.AreEqual(HttpStatusCode.Forbidden, (await ApproveAsync(Signed(stranger))).Status, "a key that is no recovery key");
        Assert.AreEqual(HttpStatusCode.BadRequest, (await ApproveAsync(Signed(recovery,
            FleetSignInApprovals.LinkDigest(setup.Organization.Id, desktop.DeviceId, FleetSignInMethods.Microsoft)))).Status, "the digest of another link");
        var approved = await ApproveAsync(Signed(recovery));
        Assert.AreEqual(HttpStatusCode.OK, approved.Status, approved.Body.ToString());
        Assert.AreEqual(("approved", recovery.DeviceId), (approved.Read<SignInLinkApprovalDto>().Status, approved.Read<SignInLinkApprovalDto>().ApprovedBy));

        // The passkey is linked with it, once.
        var started = await server.SendAsync(desktop, HttpMethod.Post, "/api/v1/sign-in/start",
            new StartSignInRequest(FleetSignInMethods.Passkey, desktop.PublicKey, Redirect, FleetSignInPurposes.Link, null, approval.Id));
        Assert.AreEqual(HttpStatusCode.OK, started.Status, started.Body.ToString());
        var start = started.Read<StartSignInResponse>();
        Assert.AreEqual(HttpStatusCode.OK, (await CompleteAsync(server, desktop, start, await PasskeyCeremonyAsync(server, start, "create", options => key.Create(options, origin)))).Status);
        Assert.AreEqual(1, (await ((ISignInStore)server.Server.Store).ListPasskeysAsync(setup.Owner.Id)).Count);

        // Attempts in the name of the owner are limited, as recoveries are: past the limit, the next one waits.
        approval = (await server.SendAsync(desktop, HttpMethod.Post, "/api/v1/sign-in/link-approvals", new RequestLinkApprovalRequest(FleetSignInMethods.Microsoft)))
            .Read<SignInLinkApprovalDto>();
        Assert.AreEqual(HttpStatusCode.TooManyRequests, (await ApproveAsync(Signed(recovery))).Status);

        var audit = (await server.SendAsync(desktop, HttpMethod.Get, "/api/v1/audit?limit=500")).Read<ListResponse<AuditRecordDto>>().Items;
        Assert.AreEqual(1, audit.Count(static record => record.Action == "sign_in.link_approval.recovery_key" && record.Result == AuditEntry.Success));
        Assert.AreEqual(2, audit.Count(static record => record.Action == "sign_in.link_approval.recovery_key" && record.Result == FleetErrorCodes.Forbidden));
    }

    [TestMethod]
    public async Task AConfirmationIsAFreshSignInAtTheProvider_NotTheSessionItKeeps()
    {
        await using var provider = await FakeOidcProvider.StartAsync();
        await using var server = await ServerFixture.StartAsync(signIn: Options(provider));
        using var owner = new TestDevice("Desktop");
        await server.SetUpAsync(owner);
        var (link, linkBack) = await StartAsync(server, owner, FleetSignInMethods.Microsoft, FleetSignInPurposes.Link);
        Assert.AreEqual(HttpStatusCode.OK, (await CompleteAsync(server, owner, link, linkBack)).Status);

        // The provider is asked to authenticate the person again: an ordinary sign-in lets them choose an account only.
        var (confirm, back) = await StartAsync(server, owner, FleetSignInMethods.Microsoft, FleetSignInPurposes.Confirm);
        var asked = provider.Authorizations.Last();
        Assert.AreEqual(("login", "0"), (asked.GetValueOrDefault("prompt"), asked.GetValueOrDefault("max_age")));
        Assert.AreEqual(HttpStatusCode.OK, (await CompleteAsync(server, owner, confirm, back)).Status, "signed in again now");
        using var stranger = new TestDevice("Stranger");
        await StartAsync(server, stranger, FleetSignInMethods.Microsoft);
        Assert.AreEqual(("select_account", null), (provider.Authorizations.Last().GetValueOrDefault("prompt"), provider.Authorizations.Last().GetValueOrDefault("max_age")));

        // A provider that answers with the session the browser kept an hour ago confirms nothing.
        provider.SessionStartedAt = DateTimeOffset.UtcNow - TimeSpan.FromHours(1);
        provider.IgnoresReauthentication = true;
        (confirm, back) = await StartAsync(server, owner, FleetSignInMethods.Microsoft, FleetSignInPurposes.Confirm);
        var stale = await CompleteAsync(server, owner, confirm, back);
        Assert.AreEqual(SignInErrorCodes.NotFresh, stale.Code, stale.Body.ToString());

        // Nor one that does not say when the person signed in, once max_age was asked.
        provider.IgnoresReauthentication = false;
        provider.IssuesAuthTime = false;
        (confirm, back) = await StartAsync(server, owner, FleetSignInMethods.Microsoft, FleetSignInPurposes.Confirm);
        Assert.AreEqual(SignInErrorCodes.NotFresh, (await CompleteAsync(server, owner, confirm, back)).Code);

        // An ordinary sign-in on a new device needs no fresh authentication.
        provider.SessionStartedAt = DateTimeOffset.UtcNow - TimeSpan.FromHours(1);
        using var laptop = new TestDevice("Laptop");
        var (enroll, enrollBack) = await StartAsync(server, laptop, FleetSignInMethods.Microsoft);
        Assert.AreEqual(HttpStatusCode.OK, (await CompleteAsync(server, laptop, enroll, enrollBack)).Status);
    }

    [TestMethod]
    public async Task AWayInIsAddedOnlyWithOneThePersonHas_OrAsTheirFirstSoonAfterTheDeviceJoined()
    {
        await using var provider = await FakeOidcProvider.StartAsync();
        await using var server = await ServerFixture.StartAsync(signIn: Options(provider));
        using var owner = new TestDevice("Desktop");
        using var key = new SoftwareAuthenticator();
        await server.SetUpAsync(owner);
        var origin = server.BaseAddress.GetLeftPart(UriPartial.Authority);

        // The first way in, soon after the setup: the device's signature is enough.
        await RegisterPasskeyAsync(server, owner, key, origin);

        // Another one is not: a stolen device key would add an account of the thief, then confirm with it.
        var alone = await server.SendAsync(owner, HttpMethod.Post, "/api/v1/sign-in/start",
            new StartSignInRequest(FleetSignInMethods.Microsoft, owner.PublicKey, Redirect, FleetSignInPurposes.Link));
        Assert.AreEqual((HttpStatusCode.Forbidden, SignInErrorCodes.ConfirmationRequired), Answer(alone), alone.Body.ToString());
        Assert.AreEqual(SignInErrorCodes.ConfirmationRequired, (await server.SendAsync(owner, HttpMethod.Post, "/api/v1/sign-in/start",
            new StartSignInRequest(FleetSignInMethods.Microsoft, owner.PublicKey, Redirect, FleetSignInPurposes.Link, "asc_guessed"))).Code);

        // With a sign-in of the passkey the person has, from this device, it is linked; that confirmation works once.
        var confirmation = await ConfirmAsync(server, owner, key);
        var (link, back) = await StartAsync(server, owner, FleetSignInMethods.Microsoft, FleetSignInPurposes.Link, confirmation);
        Assert.AreEqual(HttpStatusCode.OK, (await CompleteAsync(server, owner, link, back)).Status);
        Assert.AreEqual(SignInErrorCodes.ConfirmationRequired, (await server.SendAsync(owner, HttpMethod.Post, "/api/v1/sign-in/start",
            new StartSignInRequest(FleetSignInMethods.Google, owner.PublicKey, Redirect, FleetSignInPurposes.Link, confirmation))).Code);

        // A person with no way in, on a device that joined long ago: no first way in from its key alone.
        await using var other = await ServerFixture.StartAsync(signIn: Options(provider));
        using var old = new TestDevice("Old desktop");
        await other.SetUpAsync(old);
        other.Clock.Advance(TimeSpan.FromHours(2));
        var expired = await other.SendAsync(old, HttpMethod.Post, "/api/v1/sign-in/start",
            new StartSignInRequest(FleetSignInMethods.Passkey, old.PublicKey, Redirect, FleetSignInPurposes.Link));
        Assert.AreEqual((HttpStatusCode.Forbidden, SignInErrorCodes.FirstWayInExpired), Answer(expired), expired.Body.ToString());

        var audit = (await server.SendAsync(owner, HttpMethod.Get, "/api/v1/audit?limit=500")).Read<ListResponse<AuditRecordDto>>().Items;
        Assert.AreEqual(3, audit.Count(static record => record.Action == "sign_in.start" && record.Result == SignInErrorCodes.ConfirmationRequired), "refusals are audited");
    }

    [TestMethod]
    public async Task AStolenControllerKeyAloneAdmitsNoOtherControllerNorRecoveryKey_ThatWouldApproveWhatItAsks()
    {
        await using var server = await ServerFixture.StartAsync();
        using var owner = new TestDevice("Desktop");
        using var thief = new TestDevice("Thief's computer");
        using var recovery = new TestDevice("Thief's recovery key");
        using var key = new SoftwareAuthenticator();
        var setup = await server.SetUpAsync(owner);
        var origin = server.BaseAddress.GetLeftPart(UriPartial.Authority);
        // The owner has a passkey; days later, a thief holds the key of their desktop, not the passkey.
        await RegisterPasskeyAsync(server, owner, key, origin);
        server.Clock.Advance(TimeSpan.FromDays(3));

        // The thief's computer joins with an enrollment token the stolen key makes: the stolen key alone does not approve it.
        var pending = await server.EnrollAsync(owner, thief, FleetDeviceKinds.Controller);
        var add = await server.NextEntryAsync(owner, FleetDeviceListOperations.Add, pending.Id, thief.PublicKey, FleetDeviceKinds.Controller, setup.Owner.Id, setup.Organization.Id);
        var alone = await server.SendAsync(owner, HttpMethod.Post, $"/api/v1/devices/{pending.Id}/approve", new DeviceListChangeRequest(add));
        Assert.AreEqual((HttpStatusCode.Forbidden, SignInErrorCodes.ConfirmationRequired), Answer(alone), alone.Body.ToString());
        Assert.AreEqual(SignInErrorCodes.ConfirmationRequired, (await server.SendAsync(owner, HttpMethod.Post, $"/api/v1/devices/{pending.Id}/approve",
            new DeviceListChangeRequest(add, "asc_guessed"))).Code, "a confirmation the thief makes up");

        // Nor a recovery key of the thief's, which would approve a link and recover a computer.
        var recoveryEntry = await server.NextEntryAsync(owner, FleetDeviceListOperations.Add, recovery.DeviceId, recovery.PublicKey, FleetDeviceKinds.Recovery, setup.Owner.Id,
            setup.Organization.Id);
        var made = await server.SendAsync(owner, HttpMethod.Post, "/api/v1/recovery-keys", new CreateRecoveryKeyRequest(recovery.PublicKey, recoveryEntry, "sealed-anchor"));
        Assert.AreEqual((HttpStatusCode.Forbidden, SignInErrorCodes.ConfirmationRequired), Answer(made), made.Body.ToString());

        // So no second computer approves the link the stolen key asks for: nothing approves it.
        var asked = (await server.SendAsync(owner, HttpMethod.Post, "/api/v1/sign-in/link-approvals", new RequestLinkApprovalRequest(FleetSignInMethods.Microsoft)))
            .Read<SignInLinkApprovalDto>();
        var payload = FleetSignInApprovals.Serialize(new FleetSignInApproval(1, setup.Organization.Id, asked.Id, owner.DeviceId, asked.Digest, thief.DeviceId, server.Clock.Now));
        Assert.AreNotEqual(HttpStatusCode.OK, (await server.SendAsync(thief, HttpMethod.Post, $"/api/v1/sign-in/link-approvals/{asked.Id}/approve",
            new FleetSignedSignInApproval(payload, thief.Sign(FleetSignInApprovals.GetSignedBytes(payload))))).Status, "the thief's computer is not approved");
        Assert.AreEqual("pending", (await server.SendAsync(owner, HttpMethod.Get, $"/api/v1/sign-in/link-approvals/{asked.Id}")).Read<SignInLinkApprovalDto>().Status);

        // The owner, who signs in again with their passkey from the desktop, approves a computer of theirs; the confirmation works once.
        var confirmation = await ConfirmAsync(server, owner, key);
        Assert.AreEqual(HttpStatusCode.OK, (await server.SendAsync(owner, HttpMethod.Post, $"/api/v1/devices/{pending.Id}/approve",
            new DeviceListChangeRequest(add, confirmation))).Status);
        var again = await server.NextEntryAsync(owner, FleetDeviceListOperations.Add, recovery.DeviceId, recovery.PublicKey, FleetDeviceKinds.Recovery, setup.Owner.Id,
            setup.Organization.Id);
        Assert.AreEqual(SignInErrorCodes.ConfirmationRequired, (await server.SendAsync(owner, HttpMethod.Post, "/api/v1/recovery-keys",
            new CreateRecoveryKeyRequest(recovery.PublicKey, again, "sealed-anchor", confirmation))).Code, "a confirmation works once");
        Assert.AreEqual(HttpStatusCode.Created, (await server.SendAsync(owner, HttpMethod.Post, "/api/v1/recovery-keys",
            new CreateRecoveryKeyRequest(recovery.PublicKey, again, "sealed-anchor", await ConfirmAsync(server, owner, key)))).Status);

        // Nor does the stolen key alone remove the recovery key the owner has.
        var removal = await server.NextEntryAsync(owner, FleetDeviceListOperations.Remove, recovery.DeviceId, null, null, null, setup.Organization.Id);
        var removed = await server.SendAsync(owner, HttpMethod.Post, $"/api/v1/devices/{recovery.DeviceId}/revoke", new DeviceListChangeRequest(removal));
        Assert.AreEqual((HttpStatusCode.Forbidden, SignInErrorCodes.ConfirmationRequired), Answer(removed), removed.Body.ToString());

        var audit = (await server.SendAsync(owner, HttpMethod.Get, "/api/v1/audit?limit=500")).Read<ListResponse<AuditRecordDto>>().Items;
        Assert.AreEqual(2, audit.Count(static record => record.Action == "device.approve" && record.Result == SignInErrorCodes.ConfirmationRequired), "refusals are audited");
    }

    [TestMethod]
    public async Task APersonWithNoWayInAdmitsAComputerOnlySoonAfterSetup_AndAComputerOfAnEnrollmentTokenRenewsNoWindow()
    {
        await using var server = await ServerFixture.StartAsync();
        using var desktop = new TestDevice("Desktop");
        using var laptop = new TestDevice("Laptop");
        using var third = new TestDevice("Third computer");
        var setup = await server.SetUpAsync(desktop);
        async Task<ApiAnswer> ApproveAsync(TestDevice approver, TestDevice device)
        {
            var pending = await server.EnrollAsync(approver, device, FleetDeviceKinds.Controller);
            var entry = await server.NextEntryAsync(approver, FleetDeviceListOperations.Add, pending.Id, device.PublicKey, FleetDeviceKinds.Controller, setup.Owner.Id,
                setup.Organization.Id);
            return await server.SendAsync(approver, HttpMethod.Post, $"/api/v1/devices/{pending.Id}/approve", new DeviceListChangeRequest(entry));
        }

        // Soon after the setup, the desktop, whose key the setup code proved, admits the laptop by itself.
        server.Clock.Advance(TimeSpan.FromMinutes(55));
        Assert.AreEqual(HttpStatusCode.OK, (await ApproveAsync(desktop, laptop)).Status);

        // The laptop joined with a token the desktop's key made: its approval opens no window of its own, in which its key
        // alone would link a first way in or admit another computer.
        server.Clock.Advance(TimeSpan.FromMinutes(10));
        Assert.AreEqual(SignInErrorCodes.FirstWayInExpired, (await server.SendAsync(laptop, HttpMethod.Post, "/api/v1/sign-in/start",
            new StartSignInRequest(FleetSignInMethods.Passkey, laptop.PublicKey, Redirect, FleetSignInPurposes.Link))).Code);
        var byLaptop = await ApproveAsync(laptop, third);
        Assert.AreEqual((HttpStatusCode.Forbidden, SignInErrorCodes.FirstWayInExpired), Answer(byLaptop), byLaptop.Body.ToString());

        // Past the window, the desktop admits nothing by itself either.
        using var fourth = new TestDevice("Fourth computer");
        Assert.AreEqual(SignInErrorCodes.FirstWayInExpired, (await ApproveAsync(desktop, fourth)).Code);
    }

    internal static SignInOptions Options(FakeOidcProvider provider) => new()
    {
        AllowLoopbackHttpProviders = true,
        Microsoft = new SignInClient(provider.ClientId, provider.ClientSecret),
        MicrosoftAuthority = provider.Issuer,
        Google = new SignInClient(provider.ClientId, provider.ClientSecret),
        GoogleAuthority = provider.Issuer,
        GitHub = new SignInClient(provider.ClientId),
    };

    internal static SignInOptions GitHubOptions(FakeGitHubDeviceFlow github) => new()
    {
        AllowLoopbackHttpProviders = true,
        GitHub = new SignInClient(github.ClientId),
        GitHubAddress = github.Address,
        GitHubApiAddress = github.Address,
    };

    private static async Task<StartSignInResponse> StartCodeAsync(ServerFixture server, TestDevice device, string? purpose = null)
    {
        var started = await server.SendAsync(device, HttpMethod.Post, "/api/v1/sign-in/start", new StartSignInRequest(FleetSignInMethods.GitHub, device.PublicKey, null, purpose));
        Assert.AreEqual(HttpStatusCode.OK, started.Status, started.Body.ToString());
        return started.Read<StartSignInResponse>();
    }

    private static Task<ApiAnswer> PollAsync(ServerFixture server, TestDevice device, StartSignInResponse start)
        => server.SendAsync(device, HttpMethod.Post, "/api/v1/sign-in/poll", new PollSignInRequest(start.AttemptId));

    private static (HttpStatusCode, string?) Answer(ApiAnswer answer) => (answer.Status, answer.Code);

    private static async Task<SignInSettingsDto> ConfigureCompanyAsync(ServerFixture server, TestDevice owner, FakeOidcProvider provider, bool requireSso,
        string[]? domains = null, string[]? tenants = null)
    {
        var confirmation = await ConfirmAsync(server, owner);
        var held = (await server.SendAsync(owner, HttpMethod.Get, "/api/v1/sign-in/settings")).Read<SignInSettingsDto>();
        var set = await server.SendAsync(owner, HttpMethod.Put, "/api/v1/sign-in/settings", new SetSignInSettingsRequest(held.Version,
            [FleetSignInMethods.Company, FleetSignInMethods.Passkey, FleetSignInMethods.Microsoft, FleetSignInMethods.GitHub],
            new CompanySignInSettings(provider.Issuer.AbsoluteUri, provider.ClientId, held.Company is null ? provider.ClientSecret ?? string.Empty : null, "Contoso", null,
                AllowedDomains: domains ?? (tenants is null ? ["contoso.example"] : null), AllowedTenants: tenants), requireSso, Confirmation: confirmation));
        Assert.AreEqual(HttpStatusCode.OK, set.Status, set.Body.ToString());
        return set.Read<SignInSettingsDto>();
    }

    // The owner signs in again from their device with a passkey of theirs (the first one this device registered, or one
    // registered here as their first way in): the confirmation it gives.
    private static async Task<string> ConfirmAsync(ServerFixture server, TestDevice owner, SoftwareAuthenticator? key = null)
    {
        var origin = server.BaseAddress.GetLeftPart(UriPartial.Authority);
        var authenticator = key ?? owner.Passkey;
        if (authenticator is null)
        {
            authenticator = new SoftwareAuthenticator();
            await RegisterPasskeyAsync(server, owner, authenticator, origin);
        }

        var start = (await server.SendAsync(owner, HttpMethod.Post, "/api/v1/sign-in/start",
            new StartSignInRequest(FleetSignInMethods.Passkey, owner.PublicKey, Redirect, FleetSignInPurposes.Confirm))).Read<StartSignInResponse>();
        var back = await PasskeyCeremonyAsync(server, start, "get", options => authenticator.Get(options, origin));
        var confirmed = await CompleteAsync(server, owner, start, back);
        Assert.AreEqual(HttpStatusCode.OK, confirmed.Status, confirmed.Body.ToString());
        var answer = confirmed.Read<CompleteSignInResponse>();
        Assert.IsFalse(answer.Linked);
        return answer.Grant!;
    }

    // The person of a device signs in again with an account linked to them, through the fake provider: the confirmation it gives.
    private static async Task<string> ConfirmWithAsync(ServerFixture server, TestDevice device, string method)
    {
        var (start, back) = await StartAsync(server, device, method, FleetSignInPurposes.Confirm);
        var confirmed = await CompleteAsync(server, device, start, back);
        Assert.AreEqual(HttpStatusCode.OK, confirmed.Status, confirmed.Body.ToString());
        return confirmed.Read<CompleteSignInResponse>().Grant!;
    }

    private static async Task<(StartSignInResponse Start, Uri Back)> StartAsync(ServerFixture server, TestDevice device, string method, string? purpose = null,
        string? confirmation = null)
    {
        var started = await server.SendAsync(device, HttpMethod.Post, "/api/v1/sign-in/start", new StartSignInRequest(method, device.PublicKey, Redirect, purpose, confirmation));
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

    // A passkey registered from a device: the person's first way in, or with a sign-in of one they have (the passkey this
    // device registered first, unless the test gives the confirmation of another method).
    private static async Task RegisterPasskeyAsync(ServerFixture server, TestDevice device, SoftwareAuthenticator authenticator, string origin, string? confirmation = null)
    {
        if (confirmation is null && device.Passkey is { } held && held != authenticator) confirmation = await ConfirmAsync(server, device, held);
        var started = await server.SendAsync(device, HttpMethod.Post, "/api/v1/sign-in/start",
            new StartSignInRequest(FleetSignInMethods.Passkey, device.PublicKey, Redirect, FleetSignInPurposes.Link, confirmation));
        Assert.AreEqual(HttpStatusCode.OK, started.Status, started.Body.ToString());
        var start = started.Read<StartSignInResponse>();
        var back = await PasskeyCeremonyAsync(server, start, "create", options => authenticator.Create(options, origin));
        var linked = await CompleteAsync(server, device, start, back);
        Assert.AreEqual(HttpStatusCode.OK, linked.Status, linked.Body.ToString());
        device.Passkey ??= authenticator;
    }
}