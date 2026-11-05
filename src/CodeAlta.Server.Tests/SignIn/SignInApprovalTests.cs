using CodeAlta.Fleet.Identity;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Fleet.Protocol.Api;
using CodeAlta.Fleet.Server;
using CodeAlta.Server.Identity.SignIn;

namespace CodeAlta.Server.Tests.SignIn;

/// <summary>
/// Confirming a change of the company sign-in on another of the owner's devices (<c>doc/fleet/identity.md</c>, confirming
/// dangerous actions): the device that makes the change asks; another controller device of the same person checks the
/// change it is shown against its digest and signs that digest; the change is then applied once, as approved.
/// </summary>
[TestClass]
public sealed class SignInApprovalTests
{
    [TestMethod]
    public async Task AnotherOfTheOwnersDevicesApprovesExactlyTheChange_WhichAppliesOnce()
    {
        await using var provider = await FakeOidcProvider.StartAsync(clientSecret: "company-secret");
        await using var server = await ServerFixture.StartAsync(signIn: SignInTests.Options(provider));
        using var root = new TempDirectory();
        using var desktopKey = DeviceIdentity.LoadOrCreate(Path.Combine(root.Path, "desktop"));
        using var laptopKey = DeviceIdentity.LoadOrCreate(Path.Combine(root.Path, "laptop"));
        using var desktop = new FleetServerClient(server.BaseAddress, desktopKey);
        using var laptop = new FleetServerClient(server.BaseAddress, laptopKey);
        await desktop.SetUpAsync(server.SetupCode, "Alex", "Desktop", "Contoso", CancellationToken.None);
        var token = await desktop.CreateEnrollmentTokenAsync(null, "laptop", CancellationToken.None);
        await laptop.EnrollAsync(token.Token, "Laptop", FleetDeviceKinds.Controller, CancellationToken.None);
        await desktop.ApproveAsync(laptopKey.DeviceId, FleetDeviceKinds.Controller, CancellationToken.None);

        var held = await desktop.GetSignInSettingsAsync();
        SetSignInSettingsRequest Change(params string[] domains) => new(held.Version, [FleetSignInMethods.Company, FleetSignInMethods.Passkey],
            new CompanySignInSettings(provider.Issuer.AbsoluteUri, provider.ClientId, "company-secret", "Contoso", AllowedDomains: domains), false);
        var change = Change("contoso.example");
        Assert.AreEqual(SignInErrorCodes.ConfirmationRequired, (await Assert.ThrowsExactlyAsync<FleetServerException>(() => desktop.SetSignInSettingsAsync(change))).Code);

        // The desktop asks; the change travels with the hash of the secret only, and the desktop never approves its own change.
        var asked = await desktop.RequestSignInApprovalAsync(change);
        Assert.AreEqual("pending", asked.Status);
        Assert.IsFalse(System.Text.Json.JsonSerializer.Serialize(asked.Change).Contains("company-secret", StringComparison.Ordinal), "no device is shown the secret");
        Assert.AreEqual(FleetErrorCodes.Forbidden, (await Assert.ThrowsExactlyAsync<FleetServerException>(() => desktop.ApproveSignInChangeAsync(asked))).Code);

        // The laptop sees it, and refuses to sign what the server shows if it is not the change the server would apply.
        var listed = (await laptop.ListSignInApprovalsAsync()).Single();
        Assert.AreEqual((asked.Id, "Desktop"), (listed.Id, listed.RequesterName));
        Assert.ThrowsExactly<InvalidOperationException>(() => laptop.ApproveSignInChangeAsync(listed with { Change = listed.Change with { AllowedDomains = ["gmail.com"] } })
            .GetAwaiter().GetResult());
        Assert.AreEqual("approved", (await laptop.ApproveSignInChangeAsync(listed)).Status);
        Assert.AreEqual("approved", (await desktop.GetSignInApprovalAsync(asked.Id)).Status);

        // The approval binds to its change: another change does not use it, and it is then spent.
        var other = await Assert.ThrowsExactlyAsync<FleetServerException>(() => desktop.SetSignInSettingsAsync(Change("contoso.example", "gmail.com") with { ApprovalId = asked.Id }));
        Assert.AreEqual(SignInErrorCodes.ConfirmationRequired, other.Code);
        Assert.AreEqual(SignInErrorCodes.ConfirmationRequired,
            (await Assert.ThrowsExactlyAsync<FleetServerException>(() => desktop.SetSignInSettingsAsync(change with { ApprovalId = asked.Id }))).Code, "an approval works once");

        // Approved again, the change applies, once.
        var again = await desktop.RequestSignInApprovalAsync(change);
        await laptop.ApproveSignInChangeAsync((await laptop.ListSignInApprovalsAsync()).Single(approval => approval.Id == again.Id));
        var applied = await desktop.SetSignInSettingsAsync(change with { ApprovalId = again.Id });
        CollectionAssert.AreEqual(new[] { "contoso.example" }, applied.Company!.AllowedDomains!.ToArray());
        Assert.AreEqual(0, (await laptop.ListSignInApprovalsAsync()).Count, "nothing waits any more");

        // Only the approving device's own signature counts: the laptop cannot apply the desktop's change.
        var third = await desktop.RequestSignInApprovalAsync(change with { Version = applied.Version, Company = change.Company! with { DisplayName = "Contoso Ltd" } });
        await laptop.ApproveSignInChangeAsync((await laptop.ListSignInApprovalsAsync()).Single());
        Assert.AreEqual(SignInErrorCodes.ConfirmationRequired, (await Assert.ThrowsExactlyAsync<FleetServerException>(() => laptop.SetSignInSettingsAsync(
            change with { Version = applied.Version, Company = change.Company! with { DisplayName = "Contoso Ltd" }, ApprovalId = third.Id }))).Code);

        var audit = (await server.SendAsync(TestDevice.From(desktopKey), HttpMethod.Get, "/api/v1/audit?limit=500")).Read<ListResponse<AuditRecordDto>>().Items;
        Assert.AreEqual(3, audit.Count(static record => record.Action == "sign_in.approval.approve" && record.Result == "success"));
    }
}
