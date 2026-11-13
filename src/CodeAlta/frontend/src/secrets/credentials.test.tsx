import assert from "node:assert/strict";
import test from "node:test";
import { createElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import type { CredentialProviderRpcView, CredentialTypeView, SecretsList } from "#neoastra";
import { ShellLanguageContext } from "../shellLanguage";
import { CredentialProviders } from "./CredentialProviders";
import { SecretsBody } from "./SecretsSettings";
import { commandLine, draftsOf, isProviderId, isSecretReference, isSite, localSecret, matchesQuery, suggestedSecretName, missingSetting, noCredentials, pickedLabel, settingsOf, signInDraftProblem, siteList, stateLabel, suggestedProviderId } from "./credentials";
import { SignInsCard } from "./SignIns";

const never = () => assert.fail("rendering must not act");
const render = (element: ReturnType<typeof createElement>) => renderToStaticMarkup(createElement(ShellLanguageContext.Provider,
  { value: { locale: "en", choice: "en", setLanguage: never } }, element));
const commandType: CredentialTypeView = {
  type: "command", displayName: "A command", description: null, icon: "terminal", iconSvg: null, website: null, referenceHint: "item#field", unlockAsksPassword: false, builtIn: true,
  settings: [
    { key: "read", label: "Command that prints a secret", kind: "list", description: null, placeholder: null, required: true },
    { key: "token", label: "Token", kind: "secret", description: null, placeholder: null, required: false },
    { key: "verbose", label: "Verbose", kind: "boolean", description: null, placeholder: null, required: false },
  ],
};
const provider = (extra: Partial<CredentialProviderRpcView>): CredentialProviderRpcView => ({
  id: "personal", type: "1password", displayName: "1Password", state: "ready", message: null, typeName: "1Password", icon: "1password", account: "alice@example.com",
  programPath: "C:\\op.exe", signInCommand: null, installCommand: null, installPage: null, cache: "session", unlockAtStart: true, unlockAsksPassword: false, secrets: 1, held: 1,
  unlocked: false, unlocking: false, settings: [], ...extra,
});
const listing = (providers: CredentialProviderRpcView[], extra: Partial<SecretsList> = {}): SecretsList => ({
  status: "ok", protection: "dpapi", secrets: [], variables: [], ...noCredentials, providers, canPrompt: true, ...extra,
});

test("a provider id is suggested from its type, and is lowercase letters, digits, - and _", () => {
  assert.equal(suggestedProviderId("1password", []), "personal");
  assert.equal(suggestedProviderId("1password", ["personal"]), "personal-2");
  assert.equal(suggestedProviderId("bitwarden", ["bitwarden", "bitwarden-2"]), "bitwarden-3");
  assert.equal(suggestedProviderId("acme.vault/secrets", []), "acme-vault-secrets");
  assert.ok(isProviderId("personal"));
  assert.ok(!isProviderId("Personal"));
  assert.ok(!isProviderId("1password"));
});

test("the form of a provider sends its settings by kind, a list one item a line, and says what is missing", () => {
  const drafts = draftsOf(commandType, [{ key: "read", text: null, flag: null, list: ["tool", "get", "{ref}"] }]);
  assert.equal(drafts.read, "tool\nget\n{ref}");
  assert.equal(drafts.verbose, false);
  assert.deepEqual(settingsOf(commandType, { ...drafts, token: " ${secret:TOKEN} ", verbose: true }), [
    { key: "read", text: null, flag: null, list: ["tool", "get", "{ref}"] },
    { key: "token", text: "${secret:TOKEN}", flag: null, list: null },
    { key: "verbose", text: null, flag: true, list: null },
  ]);
  assert.equal(missingSetting(commandType, { ...drafts, read: "  " }), "Command that prints a secret");
  assert.equal(missingSetting(commandType, drafts), null);
  assert.ok(isSecretReference("${secret:OP_TOKEN}"));
  assert.ok(!isSecretReference("ops_raw_token_value"));
});

test("states say what the user can do, and labels follow the path of the vault", () => {
  assert.deepEqual(stateLabel("locked"), { label: "Locked", intent: "warning" });
  assert.deepEqual(stateLabel("not_found"), { label: "Not found", intent: "danger" });
  assert.equal(pickedLabel(["Private", "Anthropic"], "credential"), "Private › Anthropic › credential");
  assert.equal(commandLine(["winget", "install", "--id", "AgileBits.1Password.CLI"]), "winget install --id AgileBits.1Password.CLI");
  assert.equal(commandLine(["C:\\Program Files\\op.exe", "signin"]), "\"C:\\Program Files\\op.exe\" signin");
});

test("this computer comes first, then each vault with its logo, account and the action its state needs", () => {
  const html = render(createElement(CredentialProviders, {
    listing: listing([provider({ state: "locked", held: 0, message: "Unlock to let the 1Password app authorize CodeAlta." }),
      provider({ id: "team", type: "command", displayName: "A command", typeName: "A command", icon: "terminal", state: "not_installed", account: null,
        installCommand: ["winget", "install", "tool"], installPage: "https://example.com/install" })]),
    busy: false, run: never, onAdd: never, actions: { unlock: never, lock: never, test: never, remove: never, edit: never, runInTerminal: never },
  }));
  assert.ok(html.indexOf("data-provider=\"this-computer\"") < html.indexOf("data-provider=\"personal\""));
  assert.match(html, /data-brand="1password"/);
  assert.match(html, /alice@example\.com/);
  assert.match(html, />Unlock</);
  assert.match(html, /Unlock to let the 1Password app authorize CodeAlta\./);
  assert.match(html, /<code>winget install tool<\/code>/);
  assert.match(html, />Run in a terminal</);
});

test("the developer instance offers no unlock, and a vault that takes its master password asks it in a field", () => {
  const developer = render(createElement(CredentialProviders, { listing: listing([provider({ state: "locked", held: 0 })], { canPrompt: false }), busy: false, run: never, onAdd: never,
    actions: { unlock: never, lock: never, test: never, remove: never, edit: never } }));
  assert.match(developer, /Unlock in the normal instance/);
  assert.doesNotMatch(developer, />Unlock</);
  const bitwarden = render(createElement(CredentialProviders, { listing: listing([provider({ state: "locked", held: 0, unlockAsksPassword: true })]), busy: false, run: never, onAdd: never,
    actions: { unlock: never, lock: never, test: never, remove: never, edit: never } }));
  assert.match(bitwarden, />Unlock…</);
});

test("a vault that is being asked to unlock says so instead of offering to ask again", () => {
  const actions = { unlock: never, lock: never, test: never, remove: never, edit: never };
  const html = (extra: Partial<CredentialProviderRpcView>) => render(createElement(CredentialProviders,
    { listing: listing([provider({ state: "locked", held: 0, message: "Unlock to let the 1Password app authorize CodeAlta.", ...extra })]), busy: false, run: never, onAdd: never, actions }));
  const asked = html({ unlocking: true });
  assert.match(asked, /Waiting for 1Password: answer its prompt\./);
  assert.doesNotMatch(asked, /Unlock to let the 1Password app authorize CodeAlta/);
  assert.match(asked, /bp6-loading[^>]*>(?:(?!<\/button>).)*Unlock/s, "the button waits with it");
  const idle = html({ unlocking: false });
  assert.match(idle, /Unlock to let the 1Password app authorize CodeAlta\./);
  assert.doesNotMatch(idle, /bp6-loading/);
  // A vault that takes its master password in the page shows no prompt of its own.
  assert.doesNotMatch(html({ unlocking: true, unlockAsksPassword: true }), /answer its prompt/);
});

test("a vault CodeAlta unlocked offers Lock even when it holds no value, such as one used only for sign-ins", () => {
  const actions = { unlock: never, lock: never, test: never, remove: never, edit: never };
  const html = (extra: Partial<CredentialProviderRpcView>) => render(createElement(CredentialProviders, { listing: listing([provider(extra)]), busy: false, run: never, onAdd: never, actions }));
  assert.match(html({ type: "bitwarden", held: 0, secrets: 0, unlocked: true }), />Lock</);
  assert.match(html({ held: 1, unlocked: false }), />Lock</);
  assert.doesNotMatch(html({ held: 0, unlocked: false }), />Lock</, "nothing to forget");
  assert.doesNotMatch(html({ state: "locked", held: 0, unlocked: true }), />Lock</);
});

test("a secret kept in a vault shows its vault and item, its state and Ask before use, never a version nor Rotate", () => {
  const secret = { ...localSecret({ name: "ANTHROPIC_API_KEY", version: 2, setBy: "alex", setAt: "2026-11-12T09:00:00Z" }),
    provider: "personal", providerName: "1Password", icon: "1password", label: "Private › Anthropic › credential", askBeforeUse: "15m", state: "locked", stateMessage: "Locked." };
  const html = render(createElement(SecretsBody, { listing: listing([provider({})], { secrets: [secret] }), busy: false, run: never,
    actions: { setSecret: never, rotateSecret: never, deleteSecret: never, setVariable: never, deleteVariable: never },
    vaults: { keepInVault: never, setAskBeforeUse: never, verification: { available: true, reason: null } } }));
  assert.match(html, /data-source="personal"/);
  assert.match(html, /1Password › Private › Anthropic › credential/);
  assert.match(html, />Locked</);
  assert.match(html, />Asks before use</);
  assert.match(html, /aria-label="Ask before using ANTHROPIC_API_KEY"/);
  assert.doesNotMatch(html, />Rotate</);
  assert.doesNotMatch(html, /Version 2/);
});
test("a sign-in lists its sites as origins, and is saved only with a name, a vault, a user name and a password", () => {
  assert.deepEqual(siteList("https://github.com\n  gitlab.com , \n"), ["https://github.com", "gitlab.com"]);
  for (const site of ["https://github.com", "github.com", "https://github.com:8443", "http://localhost:5173"]) assert.ok(isSite(site), site);
  for (const site of ["https://github.com/login", "http://github.com", "https://github.com?x=1", "ftp://github.com", "https://user@github.com", ""]) assert.ok(!isSite(site), site);
  const draft = { id: null, label: "GitHub", provider: "personal", sites: ["https://github.com"], username: "op://Private/GitHub/username", password: "op://Private/GitHub/password", totp: "" };
  assert.equal(signInDraftProblem(draft), null);
  assert.equal(signInDraftProblem({ ...draft, label: " " }), "A sign-in needs a name.");
  assert.equal(signInDraftProblem({ ...draft, sites: ["https://github.com/login"] }), "A site is an address such as https://github.com, without a path.");
  assert.equal(signInDraftProblem({ ...draft, password: "" }), "A sign-in needs the user name and the password of an item.");
});

test("a sign-in shows its vault, its sites and the sites allowed until a date; never a value", () => {
  const html = render(createElement(SignInsCard, {
    signIns: [{ id: "github", label: "GitHub", provider: "personal", sites: ["https://github.com"], username: "op://Private/GitHub/username", password: "op://Private/GitHub/password",
      totp: null, providerName: "1Password", icon: "1password", grants: [{ origin: "https://github.com", until: "2026-12-12T09:00:00Z" }] }],
    providers: [provider({})], types: [], busy: false, run: never, actions: { save: never, remove: never, revoke: never, browse: never },
  }));
  assert.match(html, /Sign-ins of websites/);
  assert.match(html, /1Password · https:\/\/github\.com/);
  assert.match(html, /Allowed on github\.com until/);
  assert.doesNotMatch(html, /One-time code/, "a sign-in without a one-time code says nothing of it");
  const empty = render(createElement(SignInsCard, { signIns: [], providers: [], types: [], busy: false, run: never, actions: { save: never, remove: never, revoke: never, browse: never } }));
  assert.match(empty, /Add a vault first/);
});

test("a search finds a name by every word, whatever its case and accents", () => {
  assert.ok(matchesQuery("git", "GitHub"));
  assert.ok(matchesQuery("git hub", "GitHub"));
  assert.ok(matchesQuery("ecole", "École"));
  assert.ok(matchesQuery("192.168.1.1 netgear", "192.168.1.1 - netgear"));
  assert.ok(matchesQuery("  ", "anything"));
  assert.ok(!matchesQuery("gitlab", "GitHub"));
});

test("a secret is named after the item and the field picked", () => {
  assert.equal(suggestedSecretName("Private › GitHub › password"), "GITHUB_PASSWORD");
  assert.equal(suggestedSecretName("Private › Anthropic › credential"), "ANTHROPIC_API_KEY");
  assert.equal(suggestedSecretName("Work › Grafana Cloud › token"), "GRAFANA_CLOUD_TOKEN");
  assert.equal(suggestedSecretName("Private › 1fichier.com › username"), "SECRET_1FICHIER_COM_USERNAME");
  assert.equal(suggestedSecretName("Contoso/Azure DevOps#Password"), "PASSWORD");
});

test("a switch lets the developer instance unlock the vaults, and its tag says where to find it", () => {
  const actions = { unlock: never, lock: never, test: never, remove: never, edit: never, setDeveloperUnlock: never };
  const developer = render(createElement(CredentialProviders, { listing: listing([provider({ state: "locked", held: 0 })], { canPrompt: false, developerInstance: true }),
    busy: false, run: never, onAdd: never, actions }));
  assert.match(developer, /Let the developer instance unlock the vaults/);
  assert.match(developer, /Unlock in the normal instance/);
  assert.match(developer, /turn on Let the developer instance unlock the vaults, below/);
  const allowed = render(createElement(CredentialProviders, { listing: listing([provider({ state: "locked", held: 0 })], { canPrompt: true, developerInstance: true, developerUnlock: true }),
    busy: false, run: never, onAdd: never, actions }));
  assert.match(allowed, /Unlock/);
  assert.doesNotMatch(allowed, /Unlock in the normal instance/, "once allowed, the developer instance offers Unlock itself");
  assert.match(allowed, /type="checkbox"[^>]*checked/);
  const none = render(createElement(CredentialProviders, { listing: listing([]), busy: false, run: never, onAdd: never, actions }));
  assert.doesNotMatch(none, /Let the developer instance/, "without a vault there is nothing to let it unlock");
});

