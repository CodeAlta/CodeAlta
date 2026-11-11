using CodeAlta.Agent;
using CodeAlta.Browser.Tools;
using CodeAlta.Credentials;
using CodeAlta.Desktop;

namespace CodeAlta.Desktop.Tests;

[TestClass]
public sealed class DesktopSignInsTests
{
    [TestMethod]
    public async Task A_sign_in_asks_the_card_once_for_a_site_allowed_for_30_days_and_lets_the_agent_choose_among_several()
    {
        var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "codealta-signins-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            using var broker = new CredentialBroker(new CredentialBrokerOptions { GlobalRoot = root });
            broker.SignIns.Save(new SignIn("github", "GitHub", "personal", ["https://github.com"], "op://Private/GitHub/username", "op://Private/GitHub/password", null), "alice");
            List<AgentPermissionRequest> asked = [];
            var answer = AgentPermissionDecisionKind.AllowAlways;
            Task<AgentPermissionDecision> Ask(string session, AgentPermissionRequest request, CancellationToken token)
            {
                asked.Add(request);
                return Task.FromResult(answer == AgentPermissionDecisionKind.Deny ? AgentPermissionDecision.DeniedByUser() : new AgentPermissionDecision(answer));
            }

            using (var other = await DesktopSignIns.FillAsync(broker, Ask, "session", "https://gist.github.com", null, CancellationToken.None))
            {
                Assert.AreEqual(LoginFillStatus.NoSignIn, other.Status, "a subdomain is another site");
            }

            Assert.IsEmpty(asked);
            using (var first = await DesktopSignIns.FillAsync(broker, Ask, "session", "https://github.com", null, CancellationToken.None))
            {
                // No vault is configured here: the read fails after the card, with a message that names no value.
                Assert.AreEqual(LoginFillStatus.Unsupported, first.Status);
                StringAssert.Contains(first.Message, "personal");
            }

            var card = (AgentCredentialPermissionRequest)asked.Single();
            Assert.AreEqual((AgentCredentialPermissionRequest.SignInAction, "https://github.com", "With GitHub of personal", "session"), (card.Action, card.Subject, card.Detail, card.SessionId));
            Assert.IsTrue(broker.SignIns.IsGranted("github", "https://github.com"), "Allow for this site keeps a grant");
            using (await DesktopSignIns.FillAsync(broker, Ask, "session", "https://github.com", null, CancellationToken.None)) { }
            Assert.HasCount(1, asked, "a site allowed is not asked again");

            broker.SignIns.Save(new SignIn("work", "GitHub (work)", "personal", ["https://github.com"], "op://Work/GitHub/username", "op://Work/GitHub/password", null), "alice");
            using (var several = await DesktopSignIns.FillAsync(broker, Ask, "session", "https://github.com", null, CancellationToken.None))
            {
                Assert.AreEqual(LoginFillStatus.Choose, several.Status);
                CollectionAssert.AreEquivalent(new[] { "github", "work" }, several.Choices.Select(static choice => choice.Id).ToArray());
            }

            answer = AgentPermissionDecisionKind.Deny;
            using (var denied = await DesktopSignIns.FillAsync(broker, Ask, "session", "https://github.com", "work", CancellationToken.None))
            {
                Assert.AreEqual(LoginFillStatus.Denied, denied.Status);
            }

            Assert.IsFalse(broker.SignIns.IsGranted("work", "https://github.com"));
            using (var unknown = await DesktopSignIns.FillAsync(broker, Ask, "session", "https://github.com", "nothing", CancellationToken.None))
            {
                Assert.AreEqual(LoginFillStatus.NoSignIn, unknown.Status);
            }

            using var none = await DesktopSignIns.FillAsync(null, Ask, "session", "https://github.com", null, CancellationToken.None);
            Assert.AreEqual(LoginFillStatus.Unsupported, none.Status);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
