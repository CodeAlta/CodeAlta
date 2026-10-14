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

        Assert.AreEqual(new SessionIdentityResponse("ok", "business", "A. Mutel <alex@contoso.example>", "ssh", true, "/home/alex/gh-contoso", "contoso.atlassian.net", "alex@contoso.example"), shown);
        Assert.AreEqual(new SessionIdentityResponse("ok", null, null, null, false, null, null, null), usual);
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
        public JiraHandler Jira { get; } = new();

        public Fixture()
        {
            Options = new CatalogOptions { GlobalRoot = Directory.CreateDirectory(Path.Combine(Root, "global")).FullName };
            Spaces = new SpaceCatalog(new ProjectCatalog(Options));
            Tools["git config"] = _ => new(0, "user.name Usual Name\nuser.email usual@example.com\n", "");
            var checks = new SpaceIdentityChecks(Run, Jira, name => Variables.GetValueOrDefault(name), Root, OperatingSystem.IsWindows());
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
