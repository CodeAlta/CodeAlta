using System.Collections.Frozen;
using System.Net.Http.Json;
using CodeAlta.Desktop.Fleet;
using CodeAlta.Desktop.Rpc;
using CodeAlta.Fleet.Identity;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Fleet.Protocol.Api;
using CodeAlta.Fleet.Server;
using CodeAlta.Orchestration.Hosting;
using CodeAlta.Server.Hosting;
using CodeAlta.Server.Tests.SignIn;

namespace CodeAlta.Desktop.Tests;

/// <summary>
/// Approving a change of the company sign-in on another computer of the owner (<c>doc/fleet/identity.md</c>, confirming
/// dangerous actions): the computer signs only the change its page showed. A server that shows one change and answers
/// another one when Approve is clicked gets nothing signed; and the page is shown every field of the change that matters
/// to who may sign in.
/// </summary>
[TestClass]
public sealed class FleetSignInApprovalBindingTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task TheComputerSignsOnlyTheChangeItsPageShowed_AndThePageShowsEveryFieldOfIt()
    {
        var token = TestContext.CancellationToken;
        var root = Directory.CreateTempSubdirectory("codealta-fleet-approval-").FullName;
        string At(string name) => Directory.CreateDirectory(Path.Combine(root, name)).FullName;
        try
        {
            await using var provider = await FakeOidcProvider.StartAsync(clientId: "contoso-codealta", clientSecret: "contoso-secret");
            await using var server = await CoordinatorServer.StartAsync(new CoordinatorServerOptions
            {
                DataRoot = At("server"), Urls = ["http://127.0.0.1:0"], ConfigureLogging = static _ => { }, EnvironmentVariable = static _ => null,
                SignIn = new CodeAlta.Server.Identity.SignIn.SignInOptions { AllowLoopbackHttpProviders = true },
            }, token);
            var address = server.Addresses.First(static uri => uri.Scheme == "http");

            // The owner's desktop, whose transport the test controls: it can make the server answer another change.
            using var transport = new SwappingHandler();
            await using var ownerHost = await HostAsync(At("owner"), root, token);
            await using var ownerServers = new DesktopFleetServers(ownerHost, "Owner desktop", "4.0.0") { Handler = transport };
            var epoch = Guid.NewGuid().ToString("D");
            var owner = new FleetServerService(ownerServers, epoch) { Spaces = ownerHost.SpaceCatalog, IsPaired = static _ => false };
            var created = await owner.CreateAsync(new FleetServerCreateRequest(epoch, address.ToString(), server.SetupCode!.Code, "Alex", "Contoso"), token);
            Assert.AreEqual("ok", created.Status, created.Message);
            var linkId = created.Server!.Id;

            // The owner's laptop, another controller device of the same person, asks for the change.
            using var laptopKey = DeviceIdentity.LoadOrCreate(At("laptop"));
            using var laptop = new FleetServerClient(address, laptopKey);
            var joinCode = await ownerServers.CreateEnrollmentTokenAsync(linkId, TimeSpan.FromMinutes(10), "laptop", token);
            await laptop.EnrollAsync(joinCode.Token, "Laptop", FleetDeviceKinds.Controller, token);
            var pending = (await owner.DevicesAsync(new FleetServerLinkRequest(epoch, linkId), token)).Devices.Single(static device => device.Status == "pending");
            Assert.AreEqual("ok", (await owner.ApproveAsync(new FleetServerDeviceRequest(epoch, linkId, pending.Id, pending.Words, "controller"), token)).Status);
            var held = await laptop.GetSignInSettingsAsync(token);
            var shown = new SetSignInSettingsRequest(held.Version, [FleetSignInMethods.Company, FleetSignInMethods.Passkey],
                new CompanySignInSettings(provider.Issuer.AbsoluteUri, provider.ClientId, "contoso-secret", "Contoso", "roles", [new GroupRoleDto("g-admins", "admin")],
                    ["contoso.example"]), false);
            var asked = await laptop.RequestSignInApprovalAsync(shown, token);

            // The page shows the change with every field that decides who signs in, and the digest of what it shows.
            var read = await owner.SignInSettingsAsync(new FleetServerLinkRequest(epoch, linkId), token);
            var view = read.Approvals!.Single();
            Assert.AreEqual(asked.Id, view.Id);
            Assert.AreEqual(("roles", "Contoso"), (view.GroupsClaim, view.DisplayName));
            Assert.AreEqual(("g-admins", "admin"), (view.GroupRoles!.Single().Group, view.GroupRoles!.Single().Role));
            CollectionAssert.AreEqual(new[] { FleetSignInMethods.Company, FleetSignInMethods.Passkey }, view.Enabled);
            Assert.IsFalse(view.CompanyOff);
            Assert.AreEqual(FleetSignInApprovals.Digest(asked.Change), view.Digest);

            // The server now answers another change, self-consistent with its own digest: the computer signs nothing.
            transport.Swap = change => change with { AllowedDomains = ["gmail.com"] };
            var refused = await owner.ApproveSignInChangeAsync(new FleetSignInApprovalRequest(epoch, linkId, view.Id, view.Digest), token);
            Assert.AreEqual("sign_in.approval_mismatch", refused.Status, refused.Message);
            Assert.AreEqual(0, transport.Approvals, "nothing was signed");

            // The server answers the change shown: it is approved, and the laptop applies it.
            transport.Swap = null;
            var approved = await owner.ApproveSignInChangeAsync(new FleetSignInApprovalRequest(epoch, linkId, view.Id, view.Digest), token);
            Assert.AreEqual("ok", approved.Status, approved.Message);
            Assert.AreEqual(1, transport.Approvals);
            var applied = await laptop.SetSignInSettingsAsync(shown with { ApprovalId = asked.Id }, token);
            Assert.AreEqual("roles", applied.Company!.GroupsClaim);

            // A change that turns off required company sign-in, or company sign-in itself, says so.
            var off = await laptop.RequestSignInApprovalAsync(new SetSignInSettingsRequest(applied.Version, [FleetSignInMethods.Passkey], null, false), token);
            var offView = (await owner.SignInSettingsAsync(new FleetServerLinkRequest(epoch, linkId), token)).Approvals!.Single(approval => approval.Id == off.Id);
            Assert.IsTrue(offView.CompanyOff);
            Assert.IsNull(offView.Issuer);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static Task<CodeAltaHost> HostAsync(string globalRoot, string root, CancellationToken token)
        => CodeAltaHost.CreateAsync(new CodeAltaHostOptions
        {
            GlobalRoot = globalRoot, CurrentProjectPath = Directory.CreateDirectory(Path.Combine(globalRoot, "project")).FullName,
            DiscoveryScope = new(Directory.CreateDirectory(Path.Combine(globalRoot, "home")).FullName, root),
            BuiltInSkillRoot = Directory.CreateDirectory(Path.Combine(globalRoot, "builtin")).FullName,
            PluginEnvironment = FrozenDictionary<string, string?>.Empty, StartPlugins = false, IsHeadless = true, OwnsLogging = false,
        }, token);

    // The transport of the owner's desktop: it counts the approvals it sends, and can make the server's list of approvals
    // answer another change than the one it holds, with the digest of that other change, as a server that lies would.
    private sealed class SwappingHandler() : DelegatingHandler(new SocketsHttpHandler())
    {
        public Func<SignInChangeDto, SignInChangeDto>? Swap { get; set; }

        public int Approvals { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Post && path.StartsWith("/api/v1/sign-in/settings/approvals/", StringComparison.Ordinal) && path.EndsWith("/approve", StringComparison.Ordinal))
                Approvals++;
            var response = await base.SendAsync(request, cancellationToken);
            if (Swap is not { } swap || request.Method != HttpMethod.Get || path != "/api/v1/sign-in/settings/approvals" || !response.IsSuccessStatusCode) return response;
            var list = await response.Content.ReadFromJsonAsync(FleetSignInJsonContext.Default.ListResponseSignInApprovalDto, cancellationToken);
            var lied = new ListResponse<SignInApprovalDto>([.. list!.Items.Select(approval =>
            {
                var change = swap(approval.Change);
                return approval with { Change = change, Digest = FleetSignInApprovals.Digest(change) };
            })]);
            response.Content = JsonContent.Create(lied, FleetSignInJsonContext.Default.ListResponseSignInApprovalDto);
            return response;
        }
    }
}
