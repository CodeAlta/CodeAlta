using CodeAlta.Desktop.Fleet;
using CodeAlta.Desktop.Rpc;
using CodeAlta.Desktop.SessionHosts;
using CodeAlta.Fleet.Client;

namespace CodeAlta.Desktop.Tests;

/// <summary>
/// The link of a sign-in dialog of a machine (#414): the window never navigates to the page and opens no window of its own, so a
/// click asks the host to open it in the browser of this computer, an <c>https</c> page only.
/// </summary>
[TestClass]
public sealed class MachineSignInPageTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task TheLinkOfASignIn_OpensAnHttpsPageInTheBrowserOfThisComputer_AndNothingElse()
    {
        var token = TestContext.CancellationToken;
        var root = Directory.CreateTempSubdirectory("codealta-signin-page-").FullName;
        try
        {
            var registry = new SessionHostRegistry(new SessionHostRegistryTests.RecordingSessionHost(null));
            await using var fleet = new DesktopFleet(Path.Combine(root, "desktop-fleet"), registry.TakeMembership(), "desktop",
                static (machine, identity) => new RemoteSessionHost(machine, cancellation => FleetNodeClient.ConnectAsync(machine, identity, cancellation), new RemoteSessionHostOptions(),
                    localDeviceId: identity.DeviceId));
            var epoch = Guid.NewGuid().ToString("D");
            var opened = new List<Uri>();
            var browserOpens = true;
            var service = new MachinesService(fleet, epoch) { OpenSignInPage = page => { opened.Add(page); return browserOpens; } };
            const string Page = "https://claude.com/cai/oauth/authorize?code=true&state=s1";

            Assert.AreEqual("ok", (await service.OpenSignInPageAsync(new MachineOpenPageRequest(epoch, Page), token)).Status);
            foreach (var refused in new[] { "http://example.com/sign-in", "javascript:alert(1)", "file:///etc/passwd", "not a page" })
                Assert.AreEqual("invalid_request", (await service.OpenSignInPageAsync(new MachineOpenPageRequest(epoch, refused), token)).Status, refused);
            Assert.AreNotEqual("ok", (await service.OpenSignInPageAsync(new MachineOpenPageRequest(Guid.NewGuid().ToString("D"), Page), token)).Status, "another epoch");
            browserOpens = false;
            Assert.AreEqual("signin.browser", (await service.OpenSignInPageAsync(new MachineOpenPageRequest(epoch, Page), token)).Status, "the browser did not open");

            CollectionAssert.AreEqual(new[] { new Uri(Page), new Uri(Page) }, opened);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
