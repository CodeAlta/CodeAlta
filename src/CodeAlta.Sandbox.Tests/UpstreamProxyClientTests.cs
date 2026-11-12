using CodeAlta.Sandbox.Network;

namespace CodeAlta.Sandbox.Tests;

/// <summary>What the side of a sandbox that talks to the proxy of the organization reads of its answers.</summary>
[TestClass]
public sealed class UpstreamProxyClientTests
{
    [TestMethod]
    public void TheChallenges_OfOneHeader_AreAListOfSchemes_WithTheirParameters()
    {
        var challenges = UpstreamProxyClient.ParseChallenges([" Negotiate, NTLM, Basic realm=\"corp, inc\", charset=\"UTF-8\""]);
        CollectionAssert.AreEquivalent(new[] { "Negotiate", "NTLM", "Basic" }, challenges.Keys.ToArray());
        Assert.AreEqual("", challenges["Negotiate"]);
        Assert.AreEqual("", challenges["NTLM"]);
        // A comma in a quoted string separates nothing; the auth-params after the first stay with their challenge.
        Assert.AreEqual("realm=\"corp, inc\", charset=\"UTF-8\"", challenges["Basic"]);
    }

    [TestMethod]
    public void TheChallenges_OfSeveralHeaders_KeepTheirTokens()
    {
        var challenges = UpstreamProxyClient.ParseChallenges(["NTLM TlRMTVNTUAACAAAADAAMADAAAAA=", "Basic realm=\"a \\\"quoted\\\", name\"", "Digest realm=\"x\", nonce=\"y\""]);
        Assert.AreEqual("TlRMTVNTUAACAAAADAAMADAAAAA=", challenges["NTLM"], "a token68 ends with its padding, which is no auth-param");
        Assert.AreEqual("realm=\"a \\\"quoted\\\", name\"", challenges["Basic"]);
        Assert.AreEqual("realm=\"x\", nonce=\"y\"", challenges["Digest"]);
        Assert.IsEmpty(UpstreamProxyClient.ParseChallenges(["", " , "]));
        Assert.AreEqual("", UpstreamProxyClient.ParseChallenges(["basic"])["Basic"], "schemes are matched in any case");
    }
}
