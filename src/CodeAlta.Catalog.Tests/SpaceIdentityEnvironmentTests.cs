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
    public void TheCliProfiles_GiveTheirVariables_WithTheFolderOfTheUserAndTheVariablesTheyReferTo()
    {
        var identity = new SpaceIdentity(Cli:
        [
            new("azure", [new("AZURE_CONFIG_DIR", "~/.azure-contoso")]),
            new("kubectl", [new("KUBECONFIG", "~/.kube/contoso:~/.kube/${env:USER}-dev:/srv/~kube")]),
            new("aws", [new("AWS_PROFILE", "${env:CONTOSO_AWS_PROFILE}"), new("AWS_SECRET_ACCESS_KEY", "${env:CONTOSO_AWS_SECRET}")]),
        ]);
        var variables = new Dictionary<string, string> { ["USER"] = "alex", ["CONTOSO_AWS_PROFILE"] = "contoso-sso" };

        var environment = SpaceIdentityEnvironment.Create(identity, name => variables.GetValueOrDefault(name), Home, windows: false);

        Assert.AreEqual($"{Home}/.azure-contoso", environment.Get("AZURE_CONFIG_DIR"));
        Assert.AreEqual($"{Home}/.kube/contoso:{Home}/.kube/alex-dev:/srv/~kube", environment.Get("KUBECONFIG"), "~ starts each path of a list, and nothing else.");
        Assert.AreEqual("contoso-sso", environment.Get("AWS_PROFILE"));
        Assert.IsNull(environment.Get("AWS_SECRET_ACCESS_KEY"), "A variable whose reference is not set is left out: the tool keeps its own value.");

        // A list of paths is separated by ; on Windows.
        var windows = SpaceIdentityEnvironment.Create(new SpaceIdentity(Cli: [new("kubectl", [new("KUBECONFIG", @"~\.kube\a;~\.kube\b")])]), static _ => null, @"C:\Users\alex", windows: true);
        Assert.AreEqual(@"C:\Users\alex\.kube\a;C:\Users\alex\.kube\b", windows.Get("KUBECONFIG"));

        // The check of one profile says which variable was left out, and why.
        var missing = new List<KeyValuePair<string, string>>();
        var alone = SpaceIdentityEnvironment.Create(identity.Cli![2], name => variables.GetValueOrDefault(name), Home, windows: false, missing);
        Assert.AreEqual("AWS_PROFILE", alone.ToString());
        CollectionAssert.AreEqual(new KeyValuePair<string, string>[] { new("AWS_SECRET_ACCESS_KEY", "CONTOSO_AWS_SECRET") }, missing);
    }

    [TestMethod]
    public void ACliProfile_IsRefused_ForWhatTheIdentityCannotSetOrMustNotKeep()
    {
        static string? Problem(params SpaceCliProfile[] profiles) => new SpaceIdentity(Cli: profiles).Problem();

        Assert.IsNull(Problem(new SpaceCliProfile("azure", [new("AZURE_CONFIG_DIR", "~/.azure-contoso")], "az account show", "az login")));
        Assert.IsNull(Problem(new SpaceCliProfile("kubectl", [new("KUBECONFIG", "~/.kube/a")]), new SpaceCliProfile("helm", [new("KUBECONFIG", "~/.kube/a")])), "The same value twice is no conflict.");
        StringAssert.Contains(Problem(new SpaceCliProfile("kubectl", [new("KUBECONFIG", "~/.kube/a")]), new SpaceCliProfile("helm", [new("KUBECONFIG", "~/.kube/b")])),
            "KUBECONFIG is given two values, by the CLI profiles 'kubectl' and 'helm'");
        StringAssert.Contains(Problem(new SpaceCliProfile("aws", [new("A", "1")]), new SpaceCliProfile("AWS", [new("B", "1")])), "Two CLI profiles are named 'AWS'");
        StringAssert.Contains(Problem(new SpaceCliProfile("my tool", [new("A", "1")])), "is not the name of a CLI profile");
        StringAssert.Contains(Problem(new SpaceCliProfile("empty", [])), "sets no variable");
        StringAssert.Contains(Problem(new SpaceCliProfile("x", [new("GH_CONFIG_DIR", "~/gh")])), "given by the Git and GitHub cards");
        StringAssert.Contains(Problem(new SpaceCliProfile("x", [new("GIT_CONFIG_VALUE_3", "y")])), "given by the Git and GitHub cards");
        StringAssert.Contains(Problem(new SpaceCliProfile("x", [new("A", "1"), new("a", "2")])), "sets a twice");
        StringAssert.Contains(Problem(new SpaceCliProfile("x", [new("A", "${HOME}/x")])), "only ${env:NAME}");
        StringAssert.Contains(Problem(new SpaceCliProfile("x", [new("A", "two\nlines")])), "one line");
        StringAssert.Contains(Problem(new SpaceCliProfile("vault", [new("VAULT_TOKEN", "hvs.secret")])), "VAULT_TOKEN holds a secret");
        StringAssert.Contains(Problem(new SpaceCliProfile("vault", [new("VAULT_TOKEN", "${env:A}${env:B}")])), "holds a secret", "A secret is one reference, nothing else.");
        Assert.IsNull(Problem(new SpaceCliProfile("vault", [new("VAULT_TOKEN", "${env:CONTOSO_VAULT_TOKEN}")])));
        StringAssert.Contains(Problem(new SpaceCliProfile("x", [new("A", "1")], Check: "two\nlines")), "check");

        Assert.IsTrue(SpaceCliProfile.IsSecretName("OPENAI_API_KEY"));
        Assert.IsTrue(SpaceCliProfile.IsSecretName("GITHUB_PAT"));
        Assert.IsFalse(SpaceCliProfile.IsSecretName("PATH"));
        Assert.IsFalse(SpaceCliProfile.IsSecretName("AWS_SHARED_CREDENTIALS_FILE"));
        Assert.IsFalse(SpaceCliProfile.IsSecretName("SSH_KEY_PATH"));
        Assert.IsFalse(SpaceCliProfile.IsSecretName("AZURE_FEDERATED_TOKEN_FILE"), "A name of a place holds where a secret is.");
        Assert.IsFalse(SpaceCliProfile.IsSecretName("VAULT_TOKEN_PATH"));
    }

    [TestMethod]
    public void TheCliProfilesOfAProject_AddToTheOnesOfTheSpace_ByProfileAndByVariable()
    {
        var space = new SpaceIdentity(new SpaceGitIdentity("A. Mutel"), Cli:
        [
            new("kubectl", [new("KUBECONFIG", "~/.kube/contoso"), new("KUBE_EDITOR", "vim")], "kubectl config current-context"),
            new("azure", [new("AZURE_CONFIG_DIR", "~/.azure-contoso")]),
        ]);

        var merged = space.WithProjectCli(
        [
            new("Kubectl", [new("KUBECONFIG", "~/.kube/contoso-prod-readonly")]),
            new("aws", [new("AWS_PROFILE", "contoso-prod")], "aws sts get-caller-identity"),
        ]);

        Assert.AreEqual(space.Git, merged.Git);
        var cli = merged.Cli!;
        CollectionAssert.AreEqual(new[] { "kubectl", "azure", "aws" }, cli.Select(static profile => profile.Name).ToArray());
        CollectionAssert.AreEqual(new KeyValuePair<string, string>[] { new("KUBECONFIG", "~/.kube/contoso-prod-readonly"), new("KUBE_EDITOR", "vim") },
            cli[0].Environment.ToArray(), "The project replaces a variable and keeps the others.");
        Assert.AreEqual("kubectl config current-context", cli[0].Check, "A command the project leaves out is the space's.");
        Assert.AreSame(space, space.WithProjectCli([]));

        // A variable the project gives wins over another profile of the space that gives it the same value as the first.
        var shared = new SpaceIdentity(Cli: [new("aws", [new("AWS_PROFILE", "contoso")]), new("terraform", [new("AWS_PROFILE", "contoso"), new("TF_CLI_CONFIG_FILE", "~/.tf")])]);
        var prod = shared.WithProjectCli([new("aws", [new("AWS_PROFILE", "contoso-prod")])]);
        Assert.AreEqual("contoso-prod", SpaceIdentityEnvironment.Create(prod, static _ => null, Home, windows: false).Get("AWS_PROFILE"));
        CollectionAssert.AreEqual(new KeyValuePair<string, string>[] { new("TF_CLI_CONFIG_FILE", "~/.tf") }, prod.Cli![1].Environment.ToArray());
        Assert.IsNull(SpaceCliProfile.Problem(prod.Cli), "The identity a project gives is valid.");
        Assert.AreEqual("~/.kube/p", new SpaceIdentity().WithProjectCli([new("kubectl", [new("KUBECONFIG", "~/.kube/p")])]).Cli!.Single().Environment.Single().Value,
            "A project gives profiles to a space without identity too.");
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
