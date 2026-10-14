using CodeAlta.Agent;

namespace CodeAlta.Catalog.Tests;

[TestClass]
public sealed class SpaceIdentityEnvironmentTests
{
    private const string Home = "/home/alex";

    [TestMethod]
    public void AnIdentityThatSetsNothing_InjectsNothing()
    {
        Assert.AreSame(AgentProcessEnvironment.Empty, Create(null));
        Assert.AreSame(AgentProcessEnvironment.Empty, Create(new SpaceIdentity()));
        Assert.AreSame(AgentProcessEnvironment.Empty, Create(new SpaceIdentity(new SpaceGitIdentity(), new SpaceGitHubIdentity(), new SpaceJiraIdentity())));
        // Jira is for the Jira plugin, which reads it from the space: it is no variable of the processes.
        Assert.AreSame(AgentProcessEnvironment.Empty, Create(new SpaceIdentity(Jira: new SpaceJiraIdentity("contoso.atlassian.net", "alex@contoso.example", "CONTOSO_JIRA_TOKEN"))));
        // Signing off is a line of the instructions, not a setting of Git.
        Assert.AreSame(AgentProcessEnvironment.Empty, Create(new SpaceIdentity(new SpaceGitIdentity(SignOff: true))));
    }

    [TestMethod]
    public void TheAuthor_IsTheCommitterToo_AndTheGitHubProfileIsAFolderOfTheUser()
    {
        var environment = Create(new SpaceIdentity(new SpaceGitIdentity("A. Mutel", "alex@contoso.example"), new SpaceGitHubIdentity("~/.config/gh-contoso")));

        Assert.AreEqual("A. Mutel", environment.Get("GIT_AUTHOR_NAME"));
        Assert.AreEqual("A. Mutel", environment.Get("GIT_COMMITTER_NAME"));
        Assert.AreEqual("alex@contoso.example", environment.Get("GIT_AUTHOR_EMAIL"));
        Assert.AreEqual("alex@contoso.example", environment.Get("GIT_COMMITTER_EMAIL"));
        Assert.AreEqual(Path.Join(Home, ".config/gh-contoso"), environment.Get("GH_CONFIG_DIR"));
        Assert.IsNull(environment.Get("GIT_SSH_COMMAND"));
        Assert.IsNull(environment.Get("GIT_CONFIG_COUNT"), "Nothing signs: the usual signing settings stay.");
    }

    [TestMethod]
    public void TheSshKey_IsTheOnlyOneOffered_QuotedForTheShellGitRunsItIn()
    {
        var posix = SpaceIdentityEnvironment.Create(new SpaceIdentity(new SpaceGitIdentity(SshKey: "~/.ssh/contoso key")), static _ => null, Home, windows: false);
        Assert.AreEqual($"ssh -i '{Path.Join(Home, ".ssh/contoso key")}' -o IdentitiesOnly=yes", posix.Get("GIT_SSH_COMMAND"));

        var windows = SpaceIdentityEnvironment.Create(new SpaceIdentity(new SpaceGitIdentity(SshKey: @"C:\Users\Alex O'Brien\.ssh\contoso")), static _ => null, @"C:\Users\Alex O'Brien", windows: true);
        Assert.AreEqual("ssh -i 'C:/Users/Alex O'\\''Brien/.ssh/contoso' -o IdentitiesOnly=yes", windows.Get("GIT_SSH_COMMAND"));

        Assert.AreEqual("'it'\\''s; rm -rf $HOME'", SpaceIdentityEnvironment.QuotePosix("it's; rm -rf $HOME"));
    }

    [TestMethod]
    public void Signing_IsGivenToGit_AfterTheSettingsTheEnvironmentAlreadyHas()
    {
        var ssh = new SpaceIdentity(new SpaceGitIdentity(Sign: SpaceGitSigning.Ssh, SigningKey: "~/.ssh/contoso.pub"));
        var alone = Create(ssh);
        AssertSettings(alone, 0, ("user.signingkey", Path.Join(Home, ".ssh/contoso.pub")), ("gpg.format", "ssh"), ("commit.gpgsign", "true"));

        // Two settings of the user's environment stay: the space's come after them, and the count covers all.
        var merged = SpaceIdentityEnvironment.Create(ssh, static name => name == "GIT_CONFIG_COUNT" ? "2" : null, Home, windows: false);
        AssertSettings(merged, 2, ("user.signingkey", Path.Join(Home, ".ssh/contoso.pub")), ("gpg.format", "ssh"), ("commit.gpgsign", "true"));
        Assert.IsNull(merged.Get("GIT_CONFIG_KEY_0"), "The inherited settings are not rewritten.");

        // A count that is not one is not built on.
        AssertSettings(SpaceIdentityEnvironment.Create(ssh, static _ => "lots", Home, windows: false), 0,
            ("user.signingkey", Path.Join(Home, ".ssh/contoso.pub")), ("gpg.format", "ssh"), ("commit.gpgsign", "true"));

        AssertSettings(Create(new SpaceIdentity(new SpaceGitIdentity(Sign: SpaceGitSigning.Gpg, SigningKey: "3AA5C34371567BD2"))), 0,
            ("user.signingkey", "3AA5C34371567BD2"), ("gpg.format", "openpgp"), ("commit.gpgsign", "true"));
        // Off turns off the signing the user's settings may ask for.
        AssertSettings(Create(new SpaceIdentity(new SpaceGitIdentity(Sign: SpaceGitSigning.Off))), 0, ("commit.gpgsign", "false"));
    }

    [TestMethod]
    public void AnEnvironment_IsEqualToAnotherWithTheSameVariables_AndShowsNoValue()
    {
        var one = new AgentProcessEnvironment([new("B", "2"), new("A", "1")]);
        var two = new AgentProcessEnvironment([new("A", "1"), new("B", "2")]);

        Assert.AreEqual(one, two);
        Assert.AreEqual(one.GetHashCode(), two.GetHashCode());
        Assert.AreNotEqual(one, new AgentProcessEnvironment([new("A", "1"), new("B", "3")]));
        Assert.AreEqual("A, B", one.ToString());
        Assert.ThrowsExactly<ArgumentException>(() => new AgentProcessEnvironment([new("A=B", "1")]));

        var target = new Dictionary<string, string?> { ["A"] = "0", ["C"] = "3" };
        one.ApplyTo(target);
        CollectionAssert.AreEquivalent(new Dictionary<string, string?> { ["A"] = "1", ["B"] = "2", ["C"] = "3" }, target);
    }

    private static AgentProcessEnvironment Create(SpaceIdentity? identity)
        => SpaceIdentityEnvironment.Create(identity, static _ => null, Home, windows: false);

    private static void AssertSettings(AgentProcessEnvironment environment, int first, params (string Key, string Value)[] settings)
    {
        Assert.AreEqual((first + settings.Length).ToString(System.Globalization.CultureInfo.InvariantCulture), environment.Get("GIT_CONFIG_COUNT"));
        for (var index = 0; index < settings.Length; index++)
        {
            Assert.AreEqual(settings[index].Key, environment.Get($"GIT_CONFIG_KEY_{first + index}"));
            Assert.AreEqual(settings[index].Value, environment.Get($"GIT_CONFIG_VALUE_{first + index}"));
        }
    }
}
