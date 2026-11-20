import assert from "node:assert/strict";
import test from "node:test";
import { createElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import type { SecretsList } from "#neoastra";
import { ShellLanguageContext } from "../shellLanguage";
import { translate } from "../localization";
import { EnvironmentTab } from "./EnvironmentTab";
import { ProviderKeySecret } from "./ProviderKeySecret";
import { SecretsOverview } from "./SecretsOverview";
import { RecentUses, SecretsBody, type SecretsActions } from "./SecretsSettings";
import { NewValueForm, ProjectRestriction, SecretsTable, VariablesTable } from "./SecretsTable";
import { isSecretName, referenceOf, secretNameProblem, suggestedSecretName } from "./secrets";
import { localSecret, noCredentials } from "./credentials";
import { environmentStateText, issueText, keepLastSecretsTab, missingSecrets, offeredSecretsTabs, projectIssues, projectRelativePath, projectsWithEnvironment, readLastSecretsTab,
  readSecretsPageLink, secretProjectNames, secretSearchText, secretSourceText, secretsPageLink, secretsTabSetting, secretsTabStatus, secretsTabs, type EnvironmentList } from "./secretsTabs";

const never = () => assert.fail("rendering must not act");
const actions: SecretsActions = { setSecret: never, rotateSecret: never, deleteSecret: never, setVariable: never, deleteVariable: never };
const t = (key: Parameters<typeof translate>[1], parameters?: Parameters<typeof translate>[2]) => translate("en", key, parameters);
const projects = [{ id: "acme-web", name: "acme-web", icon: null }, { id: "acme-api", name: "acme-api", icon: null }, { id: "docs", name: "Docs", icon: null }];
const listing: SecretsList = {
  status: "ok", protection: "dpapi",
  secrets: [
    { ...localSecret({ name: "ACME_GH_TOKEN", version: 1, setBy: "alex (CodeAlta Desktop)", setAt: "2026-11-05T10:00:00Z" }), projectIds: ["acme-web", "acme-api"],
      usedBy: [{ kind: "project", name: "acme-web" }, { kind: "provider", name: "github" }] },
    localSecret({ name: "GRAFANA_TOKEN", version: 2, setBy: "alex (CodeAlta Desktop)", setAt: "2026-11-03T10:00:00Z" }),
  ],
  variables: [{ name: "GRAFANA_URL", value: "https://grafana.example", version: 1, setBy: "alex (altanode)", setAt: "2026-11-02T10:00:00Z" }],
  ...noCredentials, projects,
};
const environment: EnvironmentList = {
  status: "ok",
  projects: [
    { id: "acme-web", name: "acme-web", icon: null, path: "C:\\code\\acme-web\\.alta\\config.toml", secrets: ["ACME_GH_TOKEN", "NPM_TOKEN"], profiles: [],
      issues: [],
      variables: [
        { name: "GH_TOKEN", value: "${secret:ACME_GH_TOKEN}", local: true, issues: [] },
        { name: "NPM_TOKEN", value: "${secret:NPM_TOKEN}", local: true, issues: [{ kind: "secret", name: "NPM_TOKEN", state: "not_set", message: null }] },
        { name: "NODE_ENV", value: "development", local: false, issues: [] }] },
    { id: "acme-api", name: "acme-api", icon: null, path: "C:\\code\\acme-api\\.alta\\config.toml", secrets: [], variables: [],
      profiles: [{ name: "kubectl", variables: ["KUBECONFIG"] }], issues: [{ kind: "secret", name: "GRAFANA_TOKEN", state: "restricted", message: "GRAFANA_TOKEN is limited to the projects docs." }] },
    { id: "docs", name: "Docs", icon: null, path: "C:\\code\\docs\\.alta\\config.toml", secrets: [], variables: [], profiles: [], issues: [] },
  ],
  spaces: [{ id: "work", name: "Work", profiles: [{ name: "azure", variables: ["AZURE_CONFIG_DIR"] }] }],
};
const render = (element: ReturnType<typeof createElement>) => renderToStaticMarkup(createElement(ShellLanguageContext.Provider,
  { value: { locale: "en", choice: "en", setLanguage: never } }, element));
const secretsTable = (extra: Partial<Parameters<typeof SecretsTable>[0]> = {}) => render(createElement(SecretsTable, { listing, busy: false, run: never, actions, adding: null, onAdding: never, ...extra }));

test("a secret is one line of a table: its name, where it is kept, its projects and its actions; never a value", () => {
  const html = secretsTable({ restriction: { setProjects: never } });

  assert.match(html, /<table class="fleet-table" aria-label="Secrets">/);
  assert.match(html, /data-secret="GRAFANA_TOKEN"[^>]*data-source="this-computer"|data-source="this-computer"[^>]*data-secret="GRAFANA_TOKEN"/);
  assert.match(html, /<code title="GRAFANA_TOKEN">GRAFANA_TOKEN<\/code>/, "the name, whole in its tooltip where the row cuts it");
  assert.match(html, /aria-label="Copy \$\{secret:GRAFANA_TOKEN\}"/, "the reference is copied from the row");
  assert.match(html, /This computer · v2/);
  assert.match(html, />All projects</, "a secret every project is given");
  assert.match(html, />acme-web, acme-api</, "the projects a secret is limited to");
  assert.match(html, /aria-label="Rotate GRAFANA_TOKEN"/);
  assert.match(html, /aria-label="Delete GRAFANA_TOKEN"/);
  assert.match(html, /aria-label="Filter the secrets"/);
  assert.match(html, />Add a secret…</);
  // A list of names: there is no field that holds the value of a secret.
  assert.doesNotMatch(html, /aria-label="Value of GRAFANA_TOKEN"/);
  assert.doesNotMatch(html, /type="password"/);
});

test("the details of a secret give its reference, who set it, its projects to choose and what names it", () => {
  const html = secretsTable({ restriction: { setProjects: never }, initialSelected: "ACME_GH_TOKEN" });

  assert.match(html, /data-secret-details="ACME_GH_TOKEN"/);
  assert.match(html, /<span class="secret-reference">\$\{secret:ACME_GH_TOKEN\}<\/span>/);
  assert.match(html, /by alex \(CodeAlta Desktop\)/);
  assert.match(html, />Only these projects</);
  assert.match(html, /data-project="acme-web"[^>]*checked=""|checked=""[^>]*data-project="acme-web"/, "a project the secret is limited to is checked");
  assert.doesNotMatch(html, /data-project="docs"[^>]*checked=""|checked=""[^>]*data-project="docs"/);
  assert.match(html, /data-used-by="project"/);
  assert.match(html, /data-used-by="provider"/);
  assert.match(html, />github</);
  // The page of a machine shows the projects of a secret without the way to change them.
  const machine = secretsTable({ initialSelected: "ACME_GH_TOKEN" });
  assert.match(machine, />acme-web, acme-api</);
  assert.doesNotMatch(machine, />Only these projects</);
});

test("a secret given to every project offers the choice of projects, none checked", () => {
  const html = render(createElement(ProjectRestriction, { secret: listing.secrets[1], projects, busy: false, run: never, restriction: { setProjects: never } }));
  assert.match(html, />All projects</);
  assert.match(html, />Only these projects</);
  assert.doesNotMatch(html, /data-project=/, "the projects are listed once the secret is limited");
  assert.deepEqual(secretProjectNames(listing.secrets[0], projects), ["acme-web", "acme-api"]);
  assert.deepEqual(secretProjectNames({ projectIds: ["gone", "docs"] }, projects), ["Docs", "gone"], "a project the catalog no longer has keeps its id");
});

test("a variable shows its value, editable, and its reference", () => {
  const html = render(createElement(VariablesTable, { listing, busy: false, run: never, actions, adding: false, onAdding: never }));

  assert.match(html, /data-variable="GRAFANA_URL"/);
  assert.match(html, /aria-label="Value of GRAFANA_URL"[^>]*value="https:\/\/grafana.example"|value="https:\/\/grafana.example"[^>]*aria-label="Value of GRAFANA_URL"/);
  assert.match(html, /aria-label="Copy \$\{var:GRAFANA_URL\}"/);
  assert.match(html, /aria-label="Delete GRAFANA_URL"/);
  assert.match(html, />Add a variable…</);
  assert.doesNotMatch(html, />Save</, "Save comes once the value changed");
});

test("a machine without a secret says so, and the form that adds one hides its value", () => {
  const empty: SecretsList = { status: "ok", protection: null, secrets: [], variables: [], ...noCredentials };
  const body = render(createElement(SecretsBody, { listing: empty, busy: false, run: never, actions }));
  assert.match(body, /No secret on this machine\./);
  assert.match(body, /No variable on this machine\./);
  assert.doesNotMatch(body, /aria-label="Filter the secrets"/, "nothing to filter");

  const form = render(createElement(NewValueForm, { kind: "secret", taken: [], busy: false, onSave: never, initialName: "NPM_TOKEN" }));
  assert.match(form, /data-kind="secret"/);
  assert.match(form, /type="password"[^>]*aria-label="Value of the secret"|aria-label="Value of the secret"[^>]*type="password"/);
  assert.match(form, /value="NPM_TOKEN"/, "the name something asked for");
});

test("names are those of environment variables, once each", () => {
  assert.ok(isSecretName("GRAFANA_TOKEN"));
  assert.ok(isSecretName("_x1"));
  assert.ok(!isSecretName("1TOKEN"));
  assert.ok(!isSecretName("MY-TOKEN"));
  assert.equal(secretNameProblem("", []), null);
  assert.equal(secretNameProblem("my-token", []), "Letters, digits and _, not starting with a digit.");
  assert.equal(secretNameProblem("grafana_token", ["GRAFANA_TOKEN"]), "This name is already used.");
  assert.equal(referenceOf("secret", "A"), "${secret:A}");
  assert.equal(suggestedSecretName("openai"), "OPENAI_API_KEY");
  assert.equal(suggestedSecretName("my-azure.prod"), "MY_AZURE_PROD_API_KEY");
});

test("a provider names the secret of its key, says when it is not set, and offers to keep a key as a secret", () => {
  const kept = render(createElement(ProviderKeySecret, { provider: { key: "openai", hasApiKey: false, apiKeySecret: "OPENAI_API_KEY", apiKeySecretSet: false }, disabled: false, onKeep: never }));
  assert.match(kept, /The key is the secret OPENAI_API_KEY\./);
  assert.match(kept, /The secret OPENAI_API_KEY is not set on this machine\./);

  assert.match(render(createElement(ProviderKeySecret, { provider: { key: "openai", hasApiKey: true }, disabled: false, onKeep: never })), />Keep as a secret…</);
  assert.match(render(createElement(ProviderKeySecret, { provider: { key: "openai", hasApiKey: false, keyInEnvironment: true }, disabled: false, onKeep: never })), />Keep as a secret…</);
  assert.equal(render(createElement(ProviderKeySecret, { provider: { key: "codex", hasApiKey: false }, disabled: false, onKeep: never })), "");
});

test("a tab is opened by its link or by its setting, and the page remembers the one it was left on", () => {
  assert.equal(secretsPageLink("environment"), "secrets/environment");
  assert.equal(readSecretsPageLink("secrets/environment"), "environment");
  assert.equal(readSecretsPageLink("secrets/signIns"), "signIns");
  assert.equal(readSecretsPageLink("secrets"), null, "the page alone opens where it was left");
  assert.equal(readSecretsPageLink("secrets", "secrets.vaults"), "vaults");
  assert.equal(readSecretsPageLink("secrets", "secrets.sign_ins"), "signIns");
  assert.equal(readSecretsPageLink("secrets", "sessions.title"), null);
  for (const other of ["providers", "secrets/nothing", "secrets/vaults/more", "fleetSecrets"]) assert.equal(readSecretsPageLink(other, "secrets.vaults"), undefined, other);
  for (const tab of secretsTabs) if (tab !== "overview") assert.equal(readSecretsPageLink("secrets", secretsTabSetting(tab)), tab, `the setting of ${tab}`);
  assert.equal(secretsTabSetting("overview"), null);

  const kept = new Map<string, string>();
  const storage = { getItem: (key: string) => kept.get(key) ?? null, setItem: (key: string, value: string) => { kept.set(key, value); } };
  assert.equal(readLastSecretsTab(storage), "overview");
  keepLastSecretsTab("variables", storage);
  assert.equal(readLastSecretsTab(storage), "variables");
  kept.set([...kept.keys()][0], "nothing");
  assert.equal(readLastSecretsTab(storage), "overview", "a tab that no longer exists");
  assert.equal(readLastSecretsTab(null), "overview");
});

test("each tab says what it holds in a few words, and a host offers the tabs it has", () => {
  const locked = { ...listing, secrets: [...listing.secrets, { ...localSecret({ name: "KEY", version: 1, setBy: "alex", setAt: "2026-11-01T10:00:00Z" }), provider: "personal", state: "locked" }],
    uses: [{ name: "KEY", use: "provider openai", at: "2026-11-12T09:04:00Z", outcome: "given" }] };
  assert.equal(secretsTabStatus("secrets", locked, environment, t), "3 · 1 locked");
  assert.equal(secretsTabStatus("secrets", listing, environment, t), "2");
  assert.equal(secretsTabStatus("variables", listing, environment, t), "1");
  assert.equal(secretsTabStatus("environment", listing, environment, t), "2 projects");
  assert.equal(secretsTabStatus("vaults", listing, environment, t), "This computer");
  assert.equal(secretsTabStatus("activity", locked, environment, t), "1 recent");
  assert.equal(secretsTabStatus("activity", listing, environment, t), null);
  assert.equal(secretsTabStatus("overview", listing, environment, t), null);
  assert.deepEqual(offeredSecretsTabs(listing, null), ["overview", "secrets", "variables", "activity"], "a host without vaults nor projects");
  assert.deepEqual(offeredSecretsTabs({ types: [{ type: "1password" }] as unknown as SecretsList["types"] }, environment), [...secretsTabs]);
});

test("a secret is found by its name, where it is kept and its projects", () => {
  assert.equal(secretSourceText(listing.secrets[1], t), "This computer · v2");
  assert.equal(secretSourceText({ ...listing.secrets[1], provider: "personal", providerName: "1Password", label: "Private › Anthropic" }, t), "1Password › Private › Anthropic");
  assert.match(secretSearchText(listing.secrets[0], projects, t), /ACME_GH_TOKEN This computer · v1 {2}acme-web acme-api/);
});

test("the environment lists only the projects that set one, with what is missing on this computer", () => {
  assert.deepEqual(projectsWithEnvironment(environment).map(project => project.id), ["acme-web", "acme-api"], "a project without variables nor profiles is only offered");
  assert.deepEqual(projectsWithEnvironment(null), []);
  const [web, api] = environment.projects;
  assert.equal(environmentStateText(web, t), "1 secret not set");
  assert.equal(environmentStateText(api, t), "GRAFANA_TOKEN is limited to other projects");
  assert.equal(environmentStateText(environment.projects[2], t), "All set");
  assert.deepEqual(projectIssues(web).map(issue => issueText(issue, t)), ["NPM_TOKEN is not set"]);
  assert.equal(issueText({ kind: "secret", name: "KEY", state: "locked", message: null }, t), "KEY is in a locked vault");
  assert.equal(issueText({ kind: "env", name: "HOME_DIR", state: "unavailable", message: "HOME_DIR is not in the environment." }, t), "HOME_DIR is not in the environment.");
  assert.deepEqual(missingSecrets(environment), ["NPM_TOKEN"]);

  const html = render(createElement(EnvironmentTab, { environment, listing, busy: false, run: never, actions: { setVariable: never, deleteVariable: never },
    picking: false, onPicking: never, onSetSecret: never, onOpenFile: never, onOpenSpace: never, initialSelected: "acme-web" }));
  assert.match(html, /data-project="acme-web"/);
  assert.match(html, /data-project="acme-api"/);
  assert.doesNotMatch(html, /data-project="docs"/, "a project that sets nothing is not listed");
  assert.match(html, />1 secret not set</);
  assert.match(html, /1 CLI profile</);
  assert.match(html, /<table class="environment-variable-list" aria-label="Environment variables of acme-web">/);
  // The editor of the selected project: its variables, what is missing, the file, and no Delete for an inherited variable.
  assert.match(html, /data-environment="acme-web"/);
  assert.match(html, /data-environment-variable="GH_TOKEN"/);
  assert.match(html, /value="\$\{secret:ACME_GH_TOKEN\}"/);
  assert.match(html, /NPM_TOKEN is not set/);
  assert.match(html, /aria-label="Set NPM_TOKEN…"/);
  assert.match(html, /data-environment-variable="NODE_ENV" data-inherited="true"/);
  assert.match(html, />Inherited</);
  assert.match(html, /aria-label="Delete GH_TOKEN"/);
  assert.doesNotMatch(html, /aria-label="Delete NODE_ENV"/);
  assert.match(html, /C:\\code\\acme-web\\\.alta\\config\.toml/);
  assert.match(html, /aria-label="Name of the environment variable"/);
  // The spaces whose Identity sets CLI profiles, each with the way to that section.
  assert.match(html, /data-space="work"/);
  assert.match(html, /aria-label="Identity of Work"/);
  assert.match(html, /AZURE_CONFIG_DIR/);
});

test("the file of a project is named under its folder for the editor", () => {
  assert.equal(projectRelativePath("C:\\code\\acme-web", "C:\\code\\acme-web\\.alta\\config.toml"), ".alta/config.toml");
  assert.equal(projectRelativePath("/home/me/acme/", "/home/me/acme/.alta/config.toml"), ".alta/config.toml");
  assert.equal(projectRelativePath("c:/code/acme-web", "C:\\Code\\acme-web\\.alta\\config.toml"), ".alta/config.toml", "a folder of Windows, whatever its case");
  assert.equal(projectRelativePath("/home/me/acme", "/home/me/acme-other/.alta/config.toml"), "/home/me/acme-other/.alta/config.toml");
});

test("the overview has a card per concept, with its count, what needs attention and its first action", () => {
  const html = render(createElement(SecretsOverview, { listing, environment, offered: [...secretsTabs], busy: false, onOpen: never, onAddSecret: never, onAddVariable: never,
    onSetEnvironment: never, onAddVault: never, onAddSignIn: never }));
  for (const tab of ["secrets", "variables", "environment", "vaults", "signIns"]) assert.match(html, new RegExp(`data-overview="${tab}"`), tab);
  assert.match(html, /data-overview="secrets" data-attention="true"/);
  assert.match(html, /NPM_TOKEN is not set/, "a secret a project names that is not set on this computer");
  assert.match(html, /2 projects miss a value/);
  assert.match(html, />Set the environment of a project…</);
  assert.match(html, />Add a vault…</);
  // A host without vaults has no card for them.
  const few = render(createElement(SecretsOverview, { listing, environment: null, offered: offeredSecretsTabs(listing, null), busy: false, onOpen: never, onAddSecret: never,
    onAddVariable: never, onSetEnvironment: never, onAddVault: never, onAddSignIn: never }));
  assert.doesNotMatch(few, /data-overview="vaults"/);
  assert.doesNotMatch(few, /data-overview="environment"/);
});

test("the last uses are a table: the secret, for what, when and how it went", () => {
  const used = { ...listing, uses: [{ name: "ACME_GH_TOKEN", use: "session of acme-web", at: "2026-11-12T09:04:00Z", outcome: "given" },
    { name: "GRAFANA_TOKEN", use: null, at: "2026-11-12T09:05:00Z", outcome: "refused" }] };
  const html = render(createElement(RecentUses, { listing: used }));
  assert.match(html, /<table class="fleet-table" aria-label="Recent uses">/);
  assert.match(html, /data-outcome="given"/);
  assert.match(html, />session of acme-web</);
  assert.match(html, />declined</);
  assert.ok(html.indexOf("GRAFANA_TOKEN") < html.indexOf("ACME_GH_TOKEN"), "the latest first");
  assert.match(render(createElement(RecentUses, { listing })), /No secret was used yet\./);
});
