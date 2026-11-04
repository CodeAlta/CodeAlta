using System.Collections.Frozen;
using CodeAlta.Desktop.Fleet;
using CodeAlta.Desktop.Rpc;
using CodeAlta.Fleet.Server;
using CodeAlta.Orchestration.Hosting;
using CodeAlta.Server.Hosting;
using CodeAlta.Server.Identity.SignIn;
using CodeAlta.Server.Tests.SignIn;

namespace CodeAlta.Desktop.Tests;

/// <summary>
/// Sign-in end to end (<c>doc/fleet/identity.md</c>): a CodeAlta Server of this process on a port of the system, a fake
/// OpenID Connect provider of the company, and two desktops with profiles of their own. The owner's desktop sets the
/// server up and turns company sign-in on through the <c>fleetServer</c> service; a new computer joins through it — its
/// "system browser" follows the authorization address, the provider signs the person in and redirects to the loopback
/// address of that computer — and waits, pending, as the first computer of a person company sign-in created, until the
/// owner's desktop signs it into the device list.
/// </summary>
[TestClass]
public sealed class FleetSignInEndToEndTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(60);

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task ANewComputerJoinsThroughCompanySignIn_AndTheOwnerApprovesIt()
    {
        var token = TestContext.CancellationToken;
        var root = Directory.CreateTempSubdirectory("codealta-fleet-sign-in-").FullName;
        string At(string name) => Directory.CreateDirectory(Path.Combine(root, name)).FullName;
        try
        {
            await using var provider = await FakeOidcProvider.StartAsync(clientId: "contoso-codealta", clientSecret: "contoso-secret");
            provider.Account = new FakeAccount("sam-oid", "sam@contoso.example", "Sam Rivera", ["0b1e-engineering"]);
            await using var server = await CoordinatorServer.StartAsync(new CoordinatorServerOptions
            {
                DataRoot = At("server"), Urls = ["http://127.0.0.1:0"], ConfigureLogging = static _ => { }, EnvironmentVariable = static _ => null,
                SignIn = new SignInOptions { AllowLoopbackHttpProviders = true },
            }, token);
            var address = server.Addresses.First(static uri => uri.Scheme == "http").ToString();

            // The owner's desktop sets the server up and turns company sign-in on.
            await using var ownerHost = await HostAsync(At("owner"), root, token);
            await using var ownerServers = new DesktopFleetServers(ownerHost, "Owner desktop", "4.0.0");
            var ownerEpoch = Guid.NewGuid().ToString("D");
            var owner = new FleetServerService(ownerServers, ownerEpoch) { Spaces = ownerHost.SpaceCatalog, IsPaired = static _ => false };
            var created = await owner.CreateAsync(new FleetServerCreateRequest(ownerEpoch, address, server.SetupCode!.Code, "Alex", "Contoso"), token);
            Assert.AreEqual("ok", created.Status, created.Message);
            var linkId = created.Server!.Id;
            var held = await owner.SignInSettingsAsync(new FleetServerLinkRequest(ownerEpoch, linkId), token);
            Assert.AreEqual("ok", held.Status, held.Message);
            Assert.IsFalse(held.Settings!.Methods.Single(static method => method.Id == "microsoft").Available, "the public client of the project is a placeholder");
            var set = await owner.SetSignInSettingsAsync(new FleetSignInSettingsRequest(ownerEpoch, linkId, held.Settings.Version, ["company", "passkey"],
                provider.Issuer.AbsoluteUri, provider.ClientId, provider.ClientSecret, "Contoso", null, false, ["contoso.example"]), token);
            Assert.AreEqual("ok", set.Status, set.Message);
            Assert.IsTrue(set.Settings!.HasClientSecret);

            // A new computer: the methods of the server, then company sign-in through its browser.
            await using var laptopHost = await HostAsync(At("laptop"), root, token);
            var browsed = new List<Task>();
            await using var laptopServers = new DesktopFleetServers(laptopHost, "Sam laptop", "4.0.0")
            {
                // The system browser: it follows the provider's redirect to the loopback address of this computer.
                OpenBrowser = authorization =>
                {
                    browsed.Add(Task.Run(async () =>
                    {
                        using var browser = new HttpClient();
                        using var answer = await browser.GetAsync(authorization, token);
                        Assert.IsTrue(answer.IsSuccessStatusCode, $"the loopback page answered {answer.StatusCode}");
                    }, token));
                    return true;
                },
            };
            var laptopEpoch = Guid.NewGuid().ToString("D");
            var laptop = new FleetServerService(laptopServers, laptopEpoch) { Spaces = laptopHost.SpaceCatalog, IsPaired = static _ => false };
            var methods = await laptop.SignInMethodsAsync(new FleetSignInMethodsRequest(laptopEpoch, address), token);
            Assert.AreEqual("ok", methods.Status, methods.Message);
            CollectionAssert.AreEqual(new[] { "company", "passkey" }, methods.Methods.Select(static method => method.Id).ToArray());
            var joined = await laptop.JoinWithSignInAsync(new FleetSignInJoinRequest(laptopEpoch, address, "company"), token);
            Assert.AreEqual("ok", joined.Status, joined.Message);
            await Task.WhenAll(browsed);
            Assert.AreEqual(("Sam Rivera", false, "pending"), (joined.Person, joined.HasDevices, joined.Server!.Link));
            Assert.AreEqual("already_linked", (await laptop.JoinWithSignInAsync(new FleetSignInJoinRequest(laptopEpoch, address, "company"), token)).Status);

            // The owner's desktop sees a new computer of a new person, and signs it in after comparing its words.
            var status = await laptop.StatusAsync(new FleetServerRequest(laptopEpoch), token);
            var pending = (await owner.DevicesAsync(new FleetServerLinkRequest(ownerEpoch, linkId), token)).Devices.Single(static device => device.Status == "pending");
            Assert.AreEqual(("controller", status.Words), (pending.Kind, pending.Words));
            var approved = await owner.ApproveAsync(new FleetServerDeviceRequest(ownerEpoch, linkId, pending.Id, pending.Words, "controller"), token);
            Assert.AreEqual("ok", approved.Status, approved.Message);

            // The computer of the new person is then a member: its link becomes approved and it reads its own sign-in methods.
            var deadline = DateTime.UtcNow + Patience;
            while ((await laptop.StatusAsync(new FleetServerRequest(laptopEpoch), token)).Servers.Single().Link != "approved")
            {
                if (DateTime.UtcNow > deadline) Assert.Fail("The new computer was not approved.");
                await Task.Delay(200, token);
            }

            var own = await laptop.SignInSettingsAsync(new FleetServerLinkRequest(laptopEpoch, joined.Server.Id), token);
            Assert.AreEqual("ok", own.Status, own.Message);
            Assert.IsNull(own.Settings, "a member does not see the settings of the organization");
            Assert.AreEqual(("company", "sam@contoso.example"), (own.Logins.Single().Method, own.Logins.Single().Name));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    [TestMethod]
    public async Task AComputerJoinsWithTheGitHubAccountItsPersonLinked_ByACodeEnteredAtGitHub_AndACancelStopsThePolls()
    {
        var token = TestContext.CancellationToken;
        var root = Directory.CreateTempSubdirectory("codealta-fleet-github-").FullName;
        string At(string name) => Directory.CreateDirectory(Path.Combine(root, name)).FullName;
        try
        {
            await using var github = await FakeGitHubDeviceFlow.StartAsync();
            github.Interval = 1;
            var account = new FakeAccount("octo-alex", "alex@example.com", "Alex Martin", [], 4242);
            await using var server = await CoordinatorServer.StartAsync(new CoordinatorServerOptions
            {
                DataRoot = At("server"), Urls = ["http://127.0.0.1:0"], ConfigureLogging = static _ => { }, EnvironmentVariable = static _ => null,
                SignIn = new SignInOptions
                {
                    AllowLoopbackHttpProviders = true, GitHub = new SignInClient(github.ClientId), GitHubAddress = github.Address, GitHubApiAddress = github.Address,
                },
            }, token);
            var address = server.Addresses.First(static uri => uri.Scheme == "http").ToString();

            // The owner's desktop sets the server up and links its GitHub account: the code comes first, the page opens GitHub only.
            var opened = new List<Uri>();
            await using var ownerHost = await HostAsync(At("owner"), root, token);
            await using var ownerServers = new DesktopFleetServers(ownerHost, "Owner desktop", "4.0.0") { OpenBrowser = page => { lock (opened) opened.Add(page); return true; } };
            var ownerEpoch = Guid.NewGuid().ToString("D");
            var owner = new FleetServerService(ownerServers, ownerEpoch) { Spaces = ownerHost.SpaceCatalog, IsPaired = static _ => false };
            var created = await owner.CreateAsync(new FleetServerCreateRequest(ownerEpoch, address, server.SetupCode!.Code, "Alex", "Home"), token);
            Assert.AreEqual("ok", created.Status, created.Message);
            var linkId = created.Server!.Id;
            var shown = await owner.StartSignInCodeAsync(new FleetSignInCodeRequest(ownerEpoch, "github", null, linkId), token);
            Assert.AreEqual("ok", shown.Status, shown.Message);
            var code = shown.Code!;
            Assert.AreEqual((github.UserCodes.Single(), github.Address.AbsoluteUri.TrimEnd('/') + "/login/device"), (code.UserCode, code.Url));
            Assert.AreEqual("sign_in.browser", owner.OpenSignInPage(new FleetSignInPageRequest(ownerEpoch, "another-handle")).Status, "only the page of a code that waits opens");
            Assert.AreEqual("ok", owner.OpenSignInPage(new FleetSignInPageRequest(ownerEpoch, code.Handle)).Status);
            CollectionAssert.AreEqual(new[] { new Uri(code.Url) }, opened);
            github.Approve(code.UserCode, account);
            var linked = await owner.LinkSignInAsync(new FleetSignInLinkRequest(ownerEpoch, linkId, "github", code.Handle), token);
            Assert.AreEqual("ok", linked.Status, linked.Message);
            Assert.AreEqual("sign_in.browser", owner.OpenSignInPage(new FleetSignInPageRequest(ownerEpoch, code.Handle)).Status, "the page of a sign-in that ended no longer opens");
            Assert.AreEqual("sign_in.invalid_attempt", (await owner.LinkSignInAsync(new FleetSignInLinkRequest(ownerEpoch, linkId, "github", code.Handle), token)).Status,
                "a code continues once");
            Assert.AreEqual("sign_in.invalid_attempt", (await owner.LinkSignInAsync(new FleetSignInLinkRequest(ownerEpoch, linkId, "github"), token)).Status, "GitHub needs its code");

            // A new computer joins with that account: a device of the person exists, so it approves the new one.
            await using var laptopHost = await HostAsync(At("laptop"), root, token);
            await using var laptopServers = new DesktopFleetServers(laptopHost, "Alex laptop", "4.0.0") { OpenBrowser = static _ => true };
            var laptopEpoch = Guid.NewGuid().ToString("D");
            var laptop = new FleetServerService(laptopServers, laptopEpoch) { Spaces = laptopHost.SpaceCatalog, IsPaired = static _ => false };
            CollectionAssert.Contains((await laptop.SignInMethodsAsync(new FleetSignInMethodsRequest(laptopEpoch, address), token)).Methods.Select(static method => method.Id).ToArray(),
                "github");

            // The person gives up: the polls stop with the call.
            var abandoned = (await laptop.StartSignInCodeAsync(new FleetSignInCodeRequest(laptopEpoch, "github", address, null), token)).Code!;
            using (var giveUp = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                giveUp.CancelAfter(TimeSpan.FromSeconds(1.5));
                Assert.AreEqual("sign_in.cancelled", (await laptop.JoinWithSignInAsync(new FleetSignInJoinRequest(laptopEpoch, address, "github", abandoned.Handle), giveUp.Token)).Status);
            }

            var polls = github.Polls;
            await Task.Delay(TimeSpan.FromSeconds(2.5), token);
            Assert.AreEqual(polls, github.Polls, "a cancelled sign-in polls no more");

            var started = (await laptop.StartSignInCodeAsync(new FleetSignInCodeRequest(laptopEpoch, "github", address, null), token)).Code!;
            github.Approve(started.UserCode, account);
            var joined = await laptop.JoinWithSignInAsync(new FleetSignInJoinRequest(laptopEpoch, address, "github", started.Handle), token);
            Assert.AreEqual("ok", joined.Status, joined.Message);
            Assert.AreEqual(("Alex", true, "pending"), (joined.Person, joined.HasDevices, joined.Server!.Link), "the owner who linked the account, by the name they set");
            Assert.AreEqual(0, github.Secrets);
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
}
