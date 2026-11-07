using CodeAlta.Fleet.NodeApi;

namespace CodeAlta.Node.Tests;

/// <summary>
/// The tools of a node signed in from the desktop (<c>doc/fleet/node.md</c>, credentials on a node): their login runs on the node
/// without a terminal or a browser, and what they print gives the desktop the page and the code; never a shell.
/// </summary>
[TestClass]
public sealed class NodeCliSignInTests
{
    [TestMethod]
    public void WhatTheToolsPrint_GivesThePageAndTheDeviceCode()
    {
        // What gh prints without a terminal (gh auth login --web), as seen on a Linux machine.
        var gh = "! Failed to copy one-time code to clipboard\n  No clipboard utilities available.\n! First copy your one-time code: EB71-6B69\n"
            + "Open this URL to continue in your web browser: https://github.com/login/device\n";
        Assert.AreEqual((FleetMachineSignInWays.Device, "https://github.com/login/device", "EB71-6B69"), NodeSignIns.ReadPrompt(gh));
        // What az login --use-device-code prints.
        var az = "To sign in, use a web browser to open the page https://microsoft.com/devicelogin and enter the code ABCD12345 to authenticate.\n";
        Assert.AreEqual((FleetMachineSignInWays.Device, "https://microsoft.com/devicelogin", "ABCD12345"), NodeSignIns.ReadPrompt(az));
        // A tool that shows a code on its page and asks for it back.
        var pasted = "Go to the following link in your browser:\n\n    https://accounts.example.com/o/oauth2/auth?client_id=x&state=y\n\nEnter authorization code: ";
        Assert.AreEqual((FleetMachineSignInWays.Code, "https://accounts.example.com/o/oauth2/auth?client_id=x&state=y", (string?)null), NodeSignIns.ReadPrompt(pasted));
        Assert.IsNull(NodeSignIns.ReadPrompt("! First copy your one-time code: EB71-6B69\n"), "no page yet");
        Assert.IsNull(NodeSignIns.ReadPrompt("Visit https://example.com/docs for help.\n"), "a page without a code or a question");
        // A read that stops within the page or the code gives nothing yet: the next one gives them whole.
        Assert.IsNull(NodeSignIns.ReadPrompt("! First copy your one-time code: EB71-6B69\nOpen this URL to continue in your web browser: https://git"));
        Assert.IsNull(NodeSignIns.ReadPrompt("To sign in, use a web browser to open the page https://microsoft.com/devicelogin and enter the code ABCD"));
        Assert.AreEqual("ABCD12345", NodeSignIns.ReadPrompt(az)?.Code);
    }

    // What claude setup-token prints in a terminal (macOS, Claude Code 2.1): a link of the terminal, words placed by moves of the
    // cursor, then the question; once the code is given, the token.
    [TestMethod]
    public void WhatClaudeSetupTokenPrints_GivesItsPage_ThenItsToken()
    {
        const string Page = "https://claude.com/cai/oauth/authorize?code=true&client_id=x&response_type=code&redirect_uri=https%3A%2F%2Fplatform.claude.com%2Foauth%2Fcode%2Fcallback&state=s";
        var link = $"\u001b[2GBrowser\u001b[10Gdidn't\u001b[17Gopen?\r\n\u001b]8;id=6oappq;{Page}\u0007{Page}\u001b]8;;\u0007\r\n";
        Assert.IsNull(NodeSignIns.ReadClaudePage(link), "not before it asks for the code");
        var asked = link + "\r\r\n\u001b[2GPaste\u001b[8Gcode\u001b[13Ghere\u001b[18Gif\u001b[21Gprompted\u001b[30G>\r\r\n";
        Assert.AreEqual(Page, NodeSignIns.ReadClaudePage(asked));
        Assert.IsNull(NodeSignIns.ReadClaudeToken(asked));
        const string Token = "sk-ant-oat01-AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA_BBBBBBBBBBBBBBBB-CCCCCCCC";
        Assert.AreEqual(Token, NodeSignIns.ReadClaudeToken(asked + $"\u001b[32m\u001b[2G{Token}\u001b[0m\r\n\u001b[2GStore\u001b[8Gthis\u001b[13Gtoken\r\n"));
        Assert.IsNull(NodeSignIns.ReadClaudeToken(asked + $"\u001b[2G{Token[..40]}"), "a read that stops within the token gives none yet");
        StringAssert.StartsWith(NodeSignIns.ClaudeSecretName("claude-work"), "CLAUDE_CODE_OAUTH_TOKEN_CLAUDE_WORK_");
        Assert.AreNotEqual(NodeSignIns.ClaudeSecretName("claude-work"), NodeSignIns.ClaudeSecretName("claude_work"), "two providers never share a token");
        Assert.AreEqual(NodeSignIns.ClaudeSecretName("Claude"), NodeSignIns.ClaudeSecretName("claude"), "a key is one key whatever its case");
        Assert.IsTrue(CodeAlta.Catalog.Secrets.LocalSecretStore.IsName(NodeSignIns.ClaudeSecretName(new string('k', 200))));
        Assert.IsFalse(NodeSignIns.IsPlainForCmd("login & calc"), "cmd.exe would run what follows &");
        Assert.IsTrue(NodeSignIns.IsPlainForCmd("--tenant=contoso.onmicrosoft.com"));
    }

    [TestMethod]
    public void ALoginCommand_IsSplitIntoItsWords_AndAzureSignsInWithItsDeviceCode()
    {
        CollectionAssert.AreEqual(new[] { "az", "login", "--tenant", "contoso.onmicrosoft.com" }, NodeSignIns.SplitCommand(" az  login --tenant contoso.onmicrosoft.com ").ToArray());
        CollectionAssert.AreEqual(new[] { "gcloud", "auth", "login", "--project", "my project", "" }, NodeSignIns.SplitCommand("gcloud auth login --project \"my project\" ''").ToArray());
        CollectionAssert.AreEqual(new[] { "az", "login", "--tenant", "t", "--use-device-code" }, NodeSignIns.LoginCommand(["az", "login", "--tenant", "t"]).ToArray());
        CollectionAssert.AreEqual(new[] { "az", "login", "--use-device-code" }, NodeSignIns.LoginCommand(["az", "login", "--use-device-code"]).ToArray());
        CollectionAssert.AreEqual(new[] { "aws", "sso", "login" }, NodeSignIns.LoginCommand(["aws", "sso", "login"]).ToArray());
        Assert.IsFalse(NodeSignIns.AllowedTools.Contains("bash"), "never a shell");
        Assert.IsFalse(NodeSignIns.AllowedTools.Contains("sh"));
    }
}
