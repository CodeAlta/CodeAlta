using System.Collections.Frozen;
using CodeAlta.Desktop.Fleet;
using CodeAlta.Desktop.Rpc;
using CodeAlta.Fleet.Identity;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Fleet.Protocol.Api;
using CodeAlta.Fleet.Server;
using CodeAlta.Orchestration.Hosting;
using CodeAlta.Server.Hosting;

namespace CodeAlta.Desktop.Tests;

/// <summary>
/// People and access from the owner's computer (<c>doc/fleet/access-control.md</c>): a person invited joins with the code and
/// is approved; a team is made and the person put in it; the access document is signed only for the changes the person read,
/// as this computer reads them; a proposal of an agent is applied with the person's own request; a person who leaves is
/// deprovisioned and their devices signed out of the device list; who can do what comes out as CSV safe for a spreadsheet.
/// </summary>
[TestClass]
public sealed class FleetAccessRpcTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task TheOwnersComputerInvitesAPerson_RunsTeams_SignsWhatItRead_AndDeprovisions()
    {
        var token = TestContext.CancellationToken;
        var root = Directory.CreateTempSubdirectory("codealta-fleet-access-rpc-").FullName;
        string At(string name) => Directory.CreateDirectory(Path.Combine(root, name)).FullName;
        try
        {
            await using var server = await CoordinatorServer.StartAsync(new CoordinatorServerOptions
            {
                DataRoot = At("server"), Urls = ["http://127.0.0.1:0"], ConfigureLogging = static _ => { }, EnvironmentVariable = static _ => null,
            }, token);
            var address = server.Addresses.First(static uri => uri.Scheme == "http");
            await using var host = await CodeAltaHost.CreateAsync(new CodeAltaHostOptions
            {
                GlobalRoot = At("global"), CurrentProjectPath = At("project"), DiscoveryScope = new(At("home"), root), BuiltInSkillRoot = At("builtin"),
                PluginEnvironment = FrozenDictionary<string, string?>.Empty, StartPlugins = false, IsHeadless = true, OwnsLogging = false,
            }, token);
            await using var servers = new DesktopFleetServers(host, "Desktop", "4.0.0");
            var epoch = Guid.NewGuid().ToString("D");
            var service = new FleetServerService(servers, epoch) { Spaces = host.SpaceCatalog };
            var created = await service.CreateAsync(new FleetServerCreateRequest(epoch, address.ToString(), server.SetupCode!.Code, "Alex", "Home"), token);
            Assert.AreEqual("ok", created.Status, created.Message);
            var linkId = created.Server!.Id;
            Assert.AreEqual("ok", (await service.CreateRecoveryKeyAsync(new FleetRecoveryKeyRequest(epoch, linkId), token)).Status);

            var home = await service.AccessAsync(new FleetServerLinkRequest(epoch, linkId), token);
            Assert.AreEqual(("ok", "owner", true, false), (home.Status, home.Me!.Role, home.Signer, home.Advanced), "a home server is not advanced");
            Assert.AreEqual(0, home.Document!.Pending.Length, "a household needs no access document");

            // Bea is invited; her computer joins with the code and the owner approves it.
            var invitation = await service.InvitePersonAsync(new FleetAccessInviteRequest(epoch, linkId, "Bea", null), token);
            Assert.AreEqual("ok", invitation.Status, invitation.Message);
            Assert.AreEqual(address.ToString().TrimEnd('/'), invitation.ServerUrl!.TrimEnd('/'));
            using var bea = DeviceIdentity.LoadOrCreate(At("bea"));
            using (var beaClient = new FleetServerClient(address, bea)) await beaClient.EnrollAsync(invitation.Token!, "Bea laptop", FleetDeviceKinds.Controller, token);
            var approved = await service.ApproveAsync(new FleetServerDeviceRequest(epoch, linkId, bea.DeviceId, bea.Fingerprint.ToShortWords(), FleetDeviceKinds.Controller), token);
            Assert.AreEqual("ok", approved.Status, approved.Message);
            var beaId = (await service.AccessAsync(new FleetServerLinkRequest(epoch, linkId), token)).People.Single(person => person.Name == "Bea").Id;

            Assert.AreEqual("ok", (await service.CreateTeamAsync(new FleetAccessNameRequest(epoch, linkId, "Infra"), token)).Status);
            var team = (await service.AccessAsync(new FleetServerLinkRequest(epoch, linkId), token)).Teams.Single();
            Assert.IsTrue(team.CanManage);
            Assert.AreEqual("ok", (await service.SetTeamMemberAsync(new FleetAccessTeamMemberRequest(epoch, linkId, team.Id, beaId, FleetTeamRoles.Member), token)).Status);

            // The document: signed only for what the person read.
            var preview = await service.PrepareAccessDocumentAsync(new FleetServerLinkRequest(epoch, linkId), token);
            Assert.AreEqual("ok", preview.Status, preview.Message);
            CollectionAssert.Contains(preview.Changes, "Team Infra is created");
            CollectionAssert.Contains(preview.Changes, "Bea is a member of Infra");
            Assert.AreEqual("changed", (await service.SignAccessDocumentAsync(new FleetAccessSignRequest(epoch, linkId, ["Team Infra is created"]), token)).Status,
                "nothing is signed but what was read");
            Assert.AreEqual("ok", (await service.SignAccessDocumentAsync(new FleetAccessSignRequest(epoch, linkId, preview.Changes), token)).Status);
            var signed = await service.AccessAsync(new FleetServerLinkRequest(epoch, linkId), token);
            Assert.AreEqual((1, 0), (signed.Document!.Number, signed.Document.Pending.Length));
            Assert.AreEqual(0, (await service.PrepareAccessDocumentAsync(new FleetServerLinkRequest(epoch, linkId), token)).Changes.Length);

            // An agent of Bea proposes that she maintains the team; the owner applies it with their own request.
            using (var beaClient = new FleetServerClient(address, bea))
                await beaClient.ProposeAccessAsync(new CreateAccessProposalRequest(FleetAccessProposalKinds.TeamMember, "Bea runs the team",
                    $"{{\"teamId\":\"{team.Id}\",\"personId\":\"{beaId}\",\"role\":\"maintainer\"}}", "session-1"), token);
            var proposal = (await service.AccessAsync(new FleetServerLinkRequest(epoch, linkId), token)).Proposals.Single();
            Assert.AreEqual(("Bea", "session-1", FleetAccessProposalStates.Open), (proposal.PersonName, proposal.SessionId, proposal.State));
            Assert.AreEqual("ok", (await service.DecideProposalAsync(new FleetAccessProposalDecision(epoch, linkId, proposal.Id, true), token)).Status);
            var applied = await service.AccessAsync(new FleetServerLinkRequest(epoch, linkId), token);
            Assert.AreEqual(FleetTeamRoles.Maintainer, applied.Teams.Single().Members.Single(member => member.PersonId == beaId).Role);
            Assert.AreEqual(FleetAccessProposalStates.Applied, applied.Proposals.Single().State);

            // Bea leaves: suspended, her device signed out of the list.
            var left = await service.DeprovisionPersonAsync(new FleetAccessDeprovisionRequest(epoch, linkId, beaId, null, null), token);
            Assert.AreEqual("ok", left.Status, left.Message);
            Assert.AreEqual(0, left.NotRevoked.Length, "her devices are signed out of the device list");
            var after = await service.AccessAsync(new FleetServerLinkRequest(epoch, linkId), token);
            Assert.AreEqual(FleetPersonStatuses.Suspended, after.People.Single(person => person.Id == beaId).Status);
            CollectionAssert.Contains(after.Document!.Pending, "Bea is suspended: their work stops on team machines");
            using (var beaClient = new FleetServerClient(address, bea))
                await Assert.ThrowsExactlyAsync<FleetServerException>(() => beaClient.GetAccessSelfAsync(token), "her devices are refused");

            var review = await service.AccessReviewAsync(new FleetServerLinkRequest(epoch, linkId), token);
            Assert.AreEqual("ok", review.Status, review.Message);
            StringAssert.StartsWith(review.Csv, "principal_kind,principal_id,principal,capability,scope_kind,scope_id,source");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    [TestMethod]
    public void TheCsvOfAReviewQuotesWhatASpreadsheetWouldRun()
    {
        var csv = FleetServerService.ReviewCsv(new AccessReviewDto(DateTimeOffset.UnixEpoch,
            [new AccessReviewRowDto("person", "p1", "=HYPERLINK(\"x\")", "audit.read", "organization", null, "role:owner"),
             new AccessReviewRowDto("team", "t1", "Web, Mobile", "*", "team", "t1", "team:t1:maintainer")]));
        var lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.AreEqual("person,p1,\"'=HYPERLINK(\"\"x\"\")\",audit.read,organization,,role:owner", lines[1]);
        Assert.AreEqual("team,t1,\"Web, Mobile\",*,team,t1,team:t1:maintainer", lines[2]);
    }
}
