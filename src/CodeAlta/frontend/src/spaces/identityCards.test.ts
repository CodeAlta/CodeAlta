import assert from "node:assert/strict";
import test from "node:test";
import { cardProblem, emptyCard, expandHome, gitHubLoginCommand, gitItem, jiraItem, readCards, sameCard } from "./identityCards";
import { identityRows } from "./SessionIdentityInfo";

test("the cards of an identity are read from the host, and an empty field goes back as the usual setting", () => {
  const cards = readCards({ git: { name: "A. Mutel", email: null, sshKey: null, sign: "ssh", signingKey: "~/.ssh/contoso.pub", signOff: true },
    gitHub: { configDir: "~/.config/gh-contoso" }, jira: null });
  assert.deepEqual(cards.git, { name: "A. Mutel", email: "", sshKey: "", sign: "ssh", signingKey: "~/.ssh/contoso.pub", signOff: true });
  assert.deepEqual(cards.jira, { site: "", email: "", tokenEnv: "" });
  assert.equal(readCards({ git: { name: null, email: null, sshKey: null, sign: "sometimes", signingKey: null, signOff: null }, gitHub: null, jira: null }).git.sign, "",
    "A way of signing the page does not know is the usual one.");

  assert.deepEqual(gitItem({ ...cards.git, name: "  A. Mutel ", signOff: false }),
    { name: "A. Mutel", email: null, sshKey: null, sign: "ssh", signingKey: "~/.ssh/contoso.pub", signOff: null });
  assert.deepEqual(jiraItem({ site: " contoso.atlassian.net ", email: "", tokenEnv: "CONTOSO_JIRA_TOKEN" }),
    { site: "contoso.atlassian.net", email: null, tokenEnv: "CONTOSO_JIRA_TOKEN" });
  assert.equal(sameCard("git", cards.git, { ...cards.git, name: "A. Mutel  " }), true, "Spaces around a value change nothing.");
  assert.equal(sameCard("git", cards.git, { ...cards.git, signOff: false }), false);
  assert.equal(emptyCard("jira", cards.jira), true);
  assert.equal(emptyCard("github", cards.github), false);
});

test("a card that cannot be saved says why, as the host would", () => {
  const cards = readCards(null);
  assert.equal(cardProblem("git", cards), null);
  assert.equal(cardProblem("git", { ...cards, git: { ...cards.git, email: "alex at contoso" } }), "email");
  assert.equal(cardProblem("git", { ...cards, git: { ...cards.git, sign: "gpg" } }), "signingKey");
  assert.equal(cardProblem("git", { ...cards, git: { ...cards.git, sign: "off" } }), null);
  assert.equal(cardProblem("jira", { ...cards, jira: { site: "contoso.atlassian.net", email: "alex@contoso.example", tokenEnv: "CONTOSO_JIRA_TOKEN" } }), null);
  assert.equal(cardProblem("jira", { ...cards, jira: { site: "https://contoso.atlassian.net/", email: "", tokenEnv: "" } }), null);
  assert.equal(cardProblem("jira", { ...cards, jira: { site: "not a site", email: "", tokenEnv: "" } }), "site");
  assert.equal(cardProblem("jira", { ...cards, jira: { site: "", email: "", tokenEnv: "atat-token-value with spaces" } }), "variable",
    "A token is given by the name of its variable, never by itself.");
});

test("the login command of a GitHub profile is written for the shell of the platform, with the folder quoted", () => {
  assert.equal(expandHome("~/.config/gh-contoso", "C:\\Users\\me"), "C:\\Users\\me\\.config\\gh-contoso");
  assert.equal(expandHome("~", "/home/me"), "/home/me");
  assert.equal(expandHome("/srv/gh", "/home/me"), "/srv/gh");
  assert.equal(gitHubLoginCommand("~/.config/gh-contoso", "C:\\Users\\me", "windows"), "$env:GH_CONFIG_DIR='C:\\Users\\me\\.config\\gh-contoso'; gh auth login");
  assert.equal(gitHubLoginCommand("~/gh/o'brien", "/home/me", "linux"), "GH_CONFIG_DIR='/home/me/gh/o'\\''brien' gh auth login");
  assert.equal(gitHubLoginCommand("C:\\gh\\o'brien", "C:\\Users\\me", "windows"), "$env:GH_CONFIG_DIR='C:\\gh\\o''brien'; gh auth login");
  assert.equal(gitHubLoginCommand("  ", "/home/me", "linux"), null);
});

test("the information window of a session shows its identity, and nothing for the usual settings", () => {
  const reply = { status: "ok", spaceId: "business", gitAuthor: "A. Mutel <alex@contoso.example>", signing: "ssh", signOff: true,
    gitHubProfile: "/home/me/.config/gh-contoso", jiraSite: "contoso.atlassian.net", jiraEmail: "alex@contoso.example" };
  assert.deepEqual(identityRows(reply), [["Git author", "A. Mutel <alex@contoso.example>"], ["Commit signing", "SSH · Signed-off-by"],
    ["GitHub profile", "/home/me/.config/gh-contoso"], ["Jira account", "contoso.atlassian.net · alex@contoso.example"]]);
  assert.equal(identityRows({ ...reply, gitAuthor: null, signing: null, signOff: false, gitHubProfile: null, jiraSite: null, jiraEmail: null }), null);
  assert.equal(identityRows({ ...reply, status: "unavailable" }), null);
});
