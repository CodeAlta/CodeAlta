using System.Net;
using System.Text;
using CodeAlta.Agent;
using CodeAlta.Catalog;
using CodeAlta.Desktop.Rpc;
using CodeAlta.Desktop.Spaces;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Desktop.Tests;

[TestClass]
public sealed class SpaceIdentityRpcTests
{
    private const string Epoch = "5f1c2d3e-8a7b-4c6d-9e0f-1a2b3c4d5e6f";

    [TestMethod]
    public async Task EachCard_IsSavedOnItsOwn_AndAnEmptyCardGoesBackToTheUsualSettings()
    {
        using var fixture = new Fixture();
        Assert.IsTrue((await fixture.Spaces.CreateAsync("Business")).Succeeded);

        var read = await fixture.Service.GetAsync(new(Epoch, "business"), default);
        Assert.AreEqual(("ok", null), (read.Status, read.Identity));
        Assert.AreEqual(("Usual Name", "usual@example.com"), (read.Usual!.GitName, read.Usual.GitEmail), "The usual settings are the global ones of Git.");

        var git = await fixture.Service.UpdateAsync(new(Epoch, "business", "git", new("A. Mutel", "alex@contoso.example", null, "ssh", "~/.ssh/contoso.pub", true), null, null), default);
        Assert.AreEqual("ok", git.Status, git.Message);
        await fixture.Service.UpdateAsync(new(Epoch, "business", "github", null, new("~/.config/gh-contoso"), null), default);
        var saved = (await fixture.Spaces.GetAsync("business"))!.Settings.Identity!;
        Assert.AreEqual(new SpaceGitIdentity("A. Mutel", "alex@contoso.example", null, SpaceGitSigning.Ssh, "~/.ssh/contoso.pub", true), saved.Git, "Saving GitHub keeps Git.");
        Assert.AreEqual("~/.config/gh-contoso", saved.GitHub!.ConfigDir);
        StringAssert.Contains(await File.ReadAllTextAsync(Path.Combine(fixture.Options.SpacesRoot, "business.md")), "gh-contoso");

        // A card emptied is the usual setting again; with nothing left, the space has no identity.
        await fixture.Service.UpdateAsync(new(Epoch, "business", "git", new(" ", null, null, null, null, false), null, null), default);
        var cleared = await fixture.Service.UpdateAsync(new(Epoch, "business", "github", null, new(""), null), default);
        Assert.AreEqual(("ok", null), (cleared.Status, cleared.Identity));
        Assert.IsNull((await fixture.Spaces.GetAsync("business"))!.Settings.Identity);
    }

    [TestMethod]
    public async Task WhatCannotBeSaved_IsRefusedWithWhatIsWrong()
    {
        using var fixture = new Fixture();
        Assert.IsTrue((await fixture.Spaces.CreateAsync("Business")).Succeeded);

        var email = await fixture.Service.UpdateAsync(new(Epoch, "business", "git", new(null, "alex at contoso", null, null, null, null), null, null), default);
        var token = await fixture.Service.UpdateAsync(new(Epoch, "business", "jira", null, null, new("contoso.atlassian.net", null, "sk-the-token-itself!")), default);
        var signing = await fixture.Service.UpdateAsync(new(Epoch, "business", "git", new(null, null, null, "gpg", null, null), null, null), default);

        Assert.AreEqual(("invalid", "'alex at contoso' is not an email."), (email.Status, email.Message));
        Assert.AreEqual("invalid", token.Status);
        StringAssert.Contains(token.Message, "never by the token itself");
        Assert.AreEqual(("invalid", "Signing commits needs the key to sign them with."), (signing.Status, signing.Message));
        Assert.IsNull((await fixture.Spaces.GetAsync("business"))!.Settings.Identity);
        Assert.AreEqual("not_found", (await fixture.Service.UpdateAsync(new(Epoch, "nowhere", "git", new("A", null, null, null, null, null), null, null), default)).Status);
        Assert.AreEqual("stale_epoch", (await fixture.Service.GetAsync(new("another", "business"), default)).Status);
        Assert.AreEqual("unavailable", (await new SpaceIdentityService().GetAsync(new(Epoch, "business"), default)).Status);
    }

    [TestMethod]
    public async Task TheGitCard_IsCheckedWithItsEnvironment_AndSaysWhatIsMissing()
    {
        using var fixture = new Fixture();
        var key = Path.Combine(fixture.Root, "contoso.pub");
        await File.WriteAllTextAsync(key, "ssh-ed25519 AAAA");
        fixture.Tools["git var"] = environment => new(0, $"{environment.Get("GIT_AUTHOR_NAME")} <{environment.Get("GIT_AUTHOR_EMAIL")}> 1760000000 +0200\n", "");

        var checkedGit = await fixture.Service.CheckAsync(new(Epoch, "git", new("A. Mutel", "alex@contoso.example", Path.Combine(fixture.Root, "missing"), "ssh", key, true), null, null), default);

        Assert.AreEqual("problem", checkedGit.Status);
        CollectionAssert.AreEqual(new[]
        {
            ("ok", "Commits as A. Mutel <alex@contoso.example>"),
            ("error", $"The SSH key {Path.Combine(fixture.Root, "missing")} does not exist."),
            ("ok", "Signs with the SSH key " + key),
            ("ok", "Signed-off-by: A. Mutel <alex@contoso.example>"),
        }, checkedGit.Lines.Select(static line => (line.Kind, line.Text)).ToArray());

        // Without GPG installed, GPG signing says so; without an author, Git says what it needs.
        fixture.Tools["git var"] = _ => new(128, "", "Author identity unknown\n*** Please tell me who you are.");
        var gpg = await fixture.Service.CheckAsync(new(Epoch, "git", new(null, null, null, "gpg", "3AA5C34371567BD2", null), null, null), default);
        CollectionAssert.AreEqual(new[] { ("error", "Git has no author: Author identity unknown"), ("error", "GPG was not found.") },
            gpg.Lines.Select(static line => (line.Kind, line.Text)).ToArray());
    }

    [TestMethod]
    public async Task TheGitHubCard_AsksTheCliForTheAccountsOfItsProfile()
    {
        using var fixture = new Fixture();
        var folder = Path.Combine(fixture.Root, "gh-contoso");
        string? asked = null;
        fixture.Tools["gh auth"] = environment =>
        {
            asked = environment.Get("GH_CONFIG_DIR");
            return new(0, "", "github.com\n  ✓ Logged in to github.com account alex-contoso (keyring)\n  - Active account: true\n");
        };

        var signedIn = await fixture.Service.CheckAsync(new(Epoch, "github", null, new(folder), null), default);
        Assert.AreEqual(folder, asked, "The CLI runs with the profile of the card.");
        CollectionAssert.AreEqual(new[] { ("info", $"The folder {folder} does not exist yet: the login command creates it."), ("ok", "Logged in to github.com account alex-contoso (keyring)") },
            signedIn.Lines.Select(static line => (line.Kind, line.Text)).ToArray());

        fixture.Tools["gh auth"] = _ => new(1, "", "You are not logged into any GitHub hosts. To log in, run: gh auth login");
        Directory.CreateDirectory(folder);
        var signedOut = await fixture.Service.CheckAsync(new(Epoch, "github", null, new(folder), null), default);
        Assert.AreEqual(("problem", "Not signed in to GitHub with this profile: run the login command."), (signedOut.Status, signedOut.Lines.Single().Text));
    }

    [TestMethod]
    public async Task TheJiraCard_SignsInWithTheTokenOfItsVariable_WithoutShowingIt()
    {
        using var fixture = new Fixture();
        fixture.Variables["CONTOSO_JIRA_TOKEN"] = "secret-token";
        fixture.Jira.Answer = request => request.RequestUri!.AbsolutePath == "/rest/api/3/myself"
            && request.Headers.Authorization?.Parameter == Convert.ToBase64String(Encoding.UTF8.GetBytes("alex@contoso.example:secret-token"))
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"displayName\":\"A. Mutel\"}") }
                : new HttpResponseMessage(HttpStatusCode.Unauthorized);

        var signedIn = await fixture.Service.CheckAsync(new(Epoch, "jira", null, null, new("Contoso.atlassian.net", "alex@contoso.example", "CONTOSO_JIRA_TOKEN")), default);
        Assert.AreEqual(("ok", "Signed in to contoso.atlassian.net as A. Mutel (alex@contoso.example)."), (signedIn.Status, signedIn.Lines.Single().Text));
        Assert.AreEqual("https://contoso.atlassian.net/rest/api/3/myself", fixture.Jira.Requests.Single());

        var refused = await fixture.Service.CheckAsync(new(Epoch, "jira", null, null, new("contoso.atlassian.net", "other@contoso.example", "CONTOSO_JIRA_TOKEN")), default);
        Assert.AreEqual("Jira refused the token of CONTOSO_JIRA_TOKEN for other@contoso.example.", refused.Lines.Single().Text);
        Assert.IsFalse(refused.Lines.Concat(signedIn.Lines).Any(static line => line.Text.Contains("secret-token", StringComparison.Ordinal)));

        var unset = await fixture.Service.CheckAsync(new(Epoch, "jira", null, null, new("contoso.atlassian.net", "alex@contoso.example", "ELSEWHERE_TOKEN")), default);
        Assert.AreEqual("The variable ELSEWHERE_TOKEN is not set in the environment of CodeAlta.", unset.Lines.Single().Text);

        fixture.Jira.Answer = request => new HttpResponseMessage(request.RequestUri!.AbsolutePath == "/rest/api/3/serverInfo" ? HttpStatusCode.OK : HttpStatusCode.NotFound);
        var browser = await fixture.Service.CheckAsync(new(Epoch, "jira", null, null, new("contoso.atlassian.net", null, null)), default);
        Assert.AreEqual(("ok", "contoso.atlassian.net answers."), (browser.Lines[0].Kind, browser.Lines[0].Text));
    }

    [TestMethod]
    public async Task ASession_IsShownTheIdentityItAttachedWith()
    {
        using var fixture = new Fixture();
        var business = new SpaceDescriptor
        {
            Id = "business",
            Name = "Business",
            Settings = new SpaceSettings(Identity: new SpaceIdentity(new SpaceGitIdentity("A. Mutel", "alex@contoso.example", Sign: SpaceGitSigning.Ssh, SigningKey: "k", SignOff: true),
                new SpaceGitHubIdentity("/home/alex/gh-contoso"), new SpaceJiraIdentity("contoso.atlassian.net", "alex@contoso.example", "CONTOSO_JIRA_TOKEN"))),
        };
        fixture.Sessions["session-1"] = new SessionIdentityResolver(null).Of(business);

        var shown = await fixture.Service.SessionAsync(new(Epoch, "session-1"), default);
        var usual = await fixture.Service.SessionAsync(new(Epoch, "session-2"), default);

        Assert.AreEqual(new SessionIdentityResponse("ok", "business", "A. Mutel <alex@contoso.example>", "ssh", true, "/home/alex/gh-contoso", "contoso.atlassian.net", "alex@contoso.example",
            shown.Cli), shown);
        Assert.AreEqual(0, shown.Cli.Count);
        Assert.AreEqual(new SessionIdentityResponse("ok", null, null, null, false, null, null, null, usual.Cli), usual);
        Assert.AreEqual(0, usual.Cli.Count);
    }

    [TestMethod]
    public async Task TheCliProfiles_AreSavedTogether_InTheirOrder_AndASecretIsRefused()
    {
        using var fixture = new Fixture();
        Assert.IsTrue((await fixture.Spaces.CreateAsync("Business")).Succeeded);
        await fixture.Service.UpdateAsync(new(Epoch, "business", "github", null, new("~/.config/gh-contoso"), null), default);

        var saved = await fixture.Service.UpdateAsync(new(Epoch, "business", "cli", null, null, null,
        [
            new(" azure ", [new("AZURE_CONFIG_DIR", " ~/.azure-contoso "), new("", " ")], "az account show", " "),
            new("kubectl", [new("KUBECONFIG", "~/.kube/contoso")], null, null),
        ]), default);

        Assert.AreEqual("ok", saved.Status, saved.Message);
        CollectionAssert.AreEqual(new[] { "azure", "kubectl" }, saved.Identity!.Cli!.Select(static profile => profile.Name).ToArray());
        Assert.AreEqual(("AZURE_CONFIG_DIR", "~/.azure-contoso"), (saved.Identity.Cli![0].Env!.Single().Name, saved.Identity.Cli[0].Env!.Single().Value),
            "A line of variable left empty is no variable, and the values are trimmed.");
        Assert.AreEqual(("az account show", null), (saved.Identity.Cli[0].Check, saved.Identity.Cli[0].Login));
        Assert.AreEqual("~/.config/gh-contoso", saved.Identity.GitHub!.ConfigDir, "Saving the CLI profiles keeps the other cards.");

        var secret = await fixture.Service.UpdateAsync(new(Epoch, "business", "cli", null, null, null, [new("vault", [new("VAULT_TOKEN", "hvs.secret")], null, null)]), default);
        Assert.AreEqual("invalid", secret.Status);
        StringAssert.Contains(secret.Message, "VAULT_TOKEN holds a secret");
        var conflict = await fixture.Service.UpdateAsync(new(Epoch, "business", "cli", null, null, null,
            [new("kubectl", [new("KUBECONFIG", "~/.kube/a")], null, null), new("helm", [new("KUBECONFIG", "~/.kube/b")], null, null)]), default);
        Assert.AreEqual(("invalid", "KUBECONFIG is given two values, by the CLI profiles 'kubectl' and 'helm'."), (conflict.Status, conflict.Message));
        Assert.AreEqual(2, (await fixture.Spaces.GetAsync("business"))!.Settings.Identity!.Cli!.Count, "What was refused changed nothing.");

        // No profile left: the CLI profiles are gone from the file, the other cards stay.
        var cleared = await fixture.Service.UpdateAsync(new(Epoch, "business", "cli", null, null, null, []), default);
        Assert.AreEqual(("ok", null), (cleared.Status, cleared.Identity!.Cli));
        Assert.AreEqual("invalid", (await fixture.Service.UpdateAsync(new(Epoch, "business", "shell", null, null, null), default)).Status);
    }

    [TestMethod]
    public async Task AFileWithAWrongCliProfile_IsReadWithoutIt_AndThePageIsToldWhatWasLeftOut()
    {
        using var fixture = new Fixture();
        Directory.CreateDirectory(fixture.Options.SpacesRoot);
        await File.WriteAllTextAsync(Path.Combine(fixture.Options.SpacesRoot, "business.md"), """
            ---
            id: business
            kind: space
            name: Business
            order: 1
            identity:
              cli:
                kubectl: { env: { KUBECONFIG: ~/.kube/a } }
                helm: { env: { KUBECONFIG: ~/.kube/b } }
            ---
            """);

        var read = await fixture.Service.GetAsync(new(Epoch, "business"), default);

        Assert.AreEqual("ok", read.Status);
        Assert.AreEqual("KUBECONFIG is given two values, by the CLI profiles 'kubectl' and 'helm'.", read.Message);
        CollectionAssert.AreEqual(new[] { "kubectl" }, read.Identity!.Cli!.Select(static profile => profile.Name).ToArray());
    }

    [TestMethod]
    public async Task ACliProfile_IsCheckedWithItsVariablesAlone_AndItsCommand()
    {
        using var fixture = new Fixture();
        fixture.Variables["CONTOSO_AWS_PROFILE"] = "contoso-sso";
        AgentProcessEnvironment? given = null;
        fixture.Shell = (command, environment) =>
        {
            given = environment;
            return command == "aws sts get-caller-identity" ? new(0, "{\n  \"Account\": \"123456789012\"\n}\n", "") : new(255, "", "Unable to locate credentials.");
        };

        var signedIn = await fixture.Service.CheckAsync(new(Epoch, "cli", null, null, null,
            new("aws", [new("AWS_PROFILE", "${env:CONTOSO_AWS_PROFILE}"), new("AWS_CONFIG_FILE", "~/.aws/contoso"), new("AWS_SECRET_ACCESS_KEY", "${env:CONTOSO_AWS_SECRET}")],
                "aws sts get-caller-identity", null)), default);

        Assert.AreEqual("contoso-sso", given!.Get("AWS_PROFILE"));
        Assert.AreEqual($"{fixture.Root}/.aws/contoso", given.Get("AWS_CONFIG_FILE"));
        Assert.IsNull(given.Get("GH_CONFIG_DIR"), "The check runs with the variables of its profile alone.");
        CollectionAssert.AreEqual(new[]
        {
            ("ok", "AWS_PROFILE is set from ${env:CONTOSO_AWS_PROFILE}."),
            ("ok", $"AWS_CONFIG_FILE={fixture.Root}/.aws/contoso"),
            ("error", "AWS_SECRET_ACCESS_KEY is left out: the variable CONTOSO_AWS_SECRET is not set in the environment of CodeAlta."),
            ("ok", "{"),
            ("ok", "\"Account\": \"123456789012\""),
            ("ok", "}"),
        }, signedIn.Lines.Select(static line => (line.Kind, line.Text)).ToArray());
        Assert.IsFalse(signedIn.Lines.Any(static line => line.Text.Contains("contoso-sso", StringComparison.Ordinal)), "What a reference gives is not shown.");

        var failed = await fixture.Service.CheckAsync(new(Epoch, "cli", null, null, null, new("aws", [new("AWS_PROFILE", "contoso")], "aws s3 ls", null)), default);
        Assert.AreEqual("problem", failed.Status);
        CollectionAssert.AreEqual(new[] { ("ok", "AWS_PROFILE=contoso"), ("error", "Unable to locate credentials."), ("error", "The check command ended with the code 255.") },
            failed.Lines.Select(static line => (line.Kind, line.Text)).ToArray());

        var without = await fixture.Service.CheckAsync(new(Epoch, "cli", null, null, null, new("kubectl", [new("KUBECONFIG", "/srv/kube")], null, null)), default);
        Assert.AreEqual(("ok", "info"), (without.Status, without.Lines[^1].Kind));
        var wrong = await fixture.Service.CheckAsync(new(Epoch, "cli", null, null, null, new("", [], null, null)), default);
        Assert.AreEqual("problem", wrong.Status);
    }

    [TestMethod]
    public async Task ASession_IsShownItsCliProfiles_AsWritten_WithTheOnesOfItsProject()
    {
        using var fixture = new Fixture();
        var business = new SpaceDescriptor
        {
            Id = "business",
            Name = "Business",
            Settings = new SpaceSettings(Identity: new SpaceIdentity(Cli:
            [
                new("azure", [new("AZURE_CONFIG_DIR", "~/.azure-contoso")]),
                new("kubectl", [new("KUBECONFIG", "~/.kube/contoso")]),
            ])),
        };
        fixture.Sessions["session-1"] = new SessionIdentityResolver(null, identity => SpaceIdentityEnvironment.Create(identity, static _ => null, "/home/alex", windows: false))
            .Of(business, [new("kubectl", [new("KUBECONFIG", "${env:PROD_KUBECONFIG}")])]);

        var shown = await fixture.Service.SessionAsync(new(Epoch, "session-1"), default);

        Assert.AreEqual(2, shown.Cli.Count);
        Assert.AreEqual(("azure", "space", "AZURE_CONFIG_DIR", "~/.azure-contoso", 0), (shown.Cli[0].Name, shown.Cli[0].Source, shown.Cli[0].Env.Single().Name, shown.Cli[0].Env.Single().Value, shown.Cli[0].Unset.Count));
        Assert.AreEqual(("kubectl", "project", "${env:PROD_KUBECONFIG}"), (shown.Cli[1].Name, shown.Cli[1].Source, shown.Cli[1].Env.Single().Value));
        CollectionAssert.AreEqual(new[] { "KUBECONFIG" }, shown.Cli[1].Unset.ToArray(), "A variable whose reference is not set is said to be left out.");
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Directory.CreateTempSubdirectory("codealta-identity-rpc-").FullName;
        public CatalogOptions Options { get; }
        public SpaceCatalog Spaces { get; }
        public SpaceIdentityService Service { get; }
        public Dictionary<string, Func<AgentProcessEnvironment, IdentityToolRun>> Tools { get; } = [];
        public Dictionary<string, string> Variables { get; } = [];
        public Dictionary<string, SessionIdentity> Sessions { get; } = [];
        public Func<string, AgentProcessEnvironment, IdentityToolRun>? Shell { get; set; }
        public JiraHandler Jira { get; } = new();

        public Fixture()
        {
            Options = new CatalogOptions { GlobalRoot = Directory.CreateDirectory(Path.Combine(Root, "global")).FullName };
            Spaces = new SpaceCatalog(new ProjectCatalog(Options));
            Tools["git config"] = _ => new(0, "user.name Usual Name\nuser.email usual@example.com\n", "");
            var checks = new SpaceIdentityChecks(Run, Jira, name => Variables.GetValueOrDefault(name), Root, OperatingSystem.IsWindows(),
                (command, environment, _) => Task.FromResult(Shell?.Invoke(command, environment) ?? new IdentityToolRun(-1, "", "")));
            Service = new SpaceIdentityService(Spaces, (id, _) => Task.FromResult(Sessions.GetValueOrDefault(id) ?? SessionIdentity.Usual), Epoch, checks);
        }

        // A tool the test answers for, by its name and its first argument; any other one is not installed.
        private Task<IdentityToolRun> Run(string fileName, IReadOnlyList<string> arguments, AgentProcessEnvironment environment, CancellationToken cancellationToken)
            => Task.FromResult(Tools.TryGetValue($"{fileName} {arguments[0]}", out var answer) ? answer(environment) : new IdentityToolRun(-1, "", ""));

        public void Dispose()
        {
            Service.Dispose();
            Directory.Delete(Root, true);
        }
    }

    private sealed class JiraHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Answer { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.NotFound);
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.AbsoluteUri);
            return Task.FromResult(Answer(request));
        }
    }
}
