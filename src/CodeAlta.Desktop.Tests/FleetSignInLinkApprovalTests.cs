using System.Collections.Frozen;
using CodeAlta.Desktop.Fleet;
using CodeAlta.Desktop.Rpc;
using CodeAlta.Fleet.Identity;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Fleet.Protocol.Api;
using CodeAlta.Fleet.Server;
using CodeAlta.Orchestration.Hosting;
using CodeAlta.Server.Hosting;
using CodeAlta.Server.Identity.SignIn;
using CodeAlta.Server.Tests.SignIn;

namespace CodeAlta.Desktop.Tests;

/// <summary>
/// Another computer of the person approves the link of a way to sign in (<c>doc/fleet/identity.md</c>, linked sign-in
/// methods), through the <c>fleetServer</c> service: the desktop shows the link another computer asks for and approves it
/// by the digest it showed; and a desktop links with the approval of another computer instead of a sign-in again.
/// </summary>
[TestClass]
public sealed class FleetSignInLinkApprovalTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task AComputerApprovesTheLinkAnotherOneAsks_AndLinksWithTheApprovalOfAnother()
    {
        var token = TestContext.CancellationToken;
        var root = Directory.CreateTempSubdirectory("codealta-fleet-link-approval-").FullName;
        string At(string name) => Directory.CreateDirectory(Path.Combine(root, name)).FullName;
        try
        {
            await using var github = await FakeGitHubDeviceFlow.StartAsync();
            github.Interval = 1;
            await using var server = await CoordinatorServer.StartAsync(new CoordinatorServerOptions
            {
                DataRoot = At("server"), Urls = ["http://127.0.0.1:0"], ConfigureLogging = static _ => { }, EnvironmentVariable = static _ => null,
                SignIn = new SignInOptions { AllowLoopbackHttpProviders = true, GitHub = new SignInClient(github.ClientId), GitHubAddress = github.Address, GitHubApiAddress = github.Address },
            }, token);
            var address = server.Addresses.First(static uri => uri.Scheme == "http");

            await using var host = await CodeAltaHost.CreateAsync(new CodeAltaHostOptions
            {
                GlobalRoot = At("owner"), CurrentProjectPath = At("project"), DiscoveryScope = new(At("home"), root), BuiltInSkillRoot = At("builtin"),
                PluginEnvironment = FrozenDictionary<string, string?>.Empty, StartPlugins = false, IsHeadless = true, OwnsLogging = false,
            }, token);
            await using var servers = new DesktopFleetServers(host, "Owner desktop", "4.0.0") { ApprovalPollInterval = TimeSpan.FromMilliseconds(100) };
            var epoch = Guid.NewGuid().ToString("D");
            var desktop = new FleetServerService(servers, epoch) { Spaces = host.SpaceCatalog, IsPaired = static _ => false };
            var created = await desktop.CreateAsync(new FleetServerCreateRequest(epoch, address.ToString(), server.SetupCode!.Code, "Alex", "Home"), token);
            Assert.AreEqual("ok", created.Status, created.Message);
            var linkId = created.Server!.Id;

            // The owner's laptop, another controller device of theirs.
            using var laptopKey = DeviceIdentity.LoadOrCreate(At("laptop"));
            using var laptop = new FleetServerClient(address, laptopKey);
            var joinCode = await servers.CreateEnrollmentTokenAsync(linkId, TimeSpan.FromMinutes(10), "laptop", token);
            await laptop.EnrollAsync(joinCode.Token, "Laptop", FleetDeviceKinds.Controller, token);
            var pending = (await desktop.DevicesAsync(new FleetServerLinkRequest(epoch, linkId), token)).Devices.Single(static device => device.Status == "pending");
            Assert.AreEqual("ok", (await desktop.ApproveAsync(new FleetServerDeviceRequest(epoch, linkId, pending.Id, pending.Words, "controller"), token)).Status);

            // The laptop asks to add a passkey: the desktop shows it, and approves exactly what it showed.
            var asked = await laptop.RequestLinkApprovalAsync(FleetSignInMethods.Passkey, token);
            var shown = (await desktop.SignInSettingsAsync(new FleetServerLinkRequest(epoch, linkId), token)).LinkApprovals!.Single();
            Assert.AreEqual((asked.Id, "Laptop", FleetSignInMethods.Passkey), (shown.Id, shown.RequestedBy, shown.Method));
            Assert.AreEqual("sign_in.approval_mismatch",
                (await desktop.ApproveSignInLinkAsync(new FleetSignInLinkApprovalRequest(epoch, linkId, shown.Id, "another-digest"), token)).Status);
            Assert.AreEqual("pending", (await laptop.GetLinkApprovalAsync(asked.Id, token)).Status, "nothing was signed");
            Assert.AreEqual("ok", (await desktop.ApproveSignInLinkAsync(new FleetSignInLinkApprovalRequest(epoch, linkId, shown.Id, shown.Digest), token)).Status);
            Assert.AreEqual("approved", (await laptop.GetLinkApprovalAsync(asked.Id, token)).Status);

            // The desktop links GitHub with the laptop's approval: its code starts once the laptop approved.
            var approving = Task.Run(async () =>
            {
                while (true)
                {
                    if ((await laptop.ListLinkApprovalsAsync(token)).FirstOrDefault(approval => approval.RequesterDeviceId != laptop.DeviceId && approval.Status == "pending") is { } waiting)
                    {
                        await laptop.ApproveLinkAsync(waiting, token);
                        return waiting.Method;
                    }

                    await Task.Delay(50, token);
                }
            }, token);
            var code = await desktop.StartSignInCodeAsync(new FleetSignInCodeRequest(epoch, "github", null, linkId, null, "device"), token);
            Assert.AreEqual("ok", code.Status, code.Message);
            Assert.AreEqual(FleetSignInMethods.GitHub, await approving);
            github.Approve(code.Code!.UserCode, new FakeAccount("octo-alex", "alex@example.com", "Alex Martin", [], 4242));
            var linked = await desktop.LinkSignInAsync(new FleetSignInLinkRequest(epoch, linkId, "github", code.Code.Handle), token);
            Assert.AreEqual("ok", linked.Status, linked.Message);
            Assert.AreEqual(FleetSignInMethods.GitHub, (await desktop.SignInSettingsAsync(new FleetServerLinkRequest(epoch, linkId), token)).Logins.Single().Method);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
