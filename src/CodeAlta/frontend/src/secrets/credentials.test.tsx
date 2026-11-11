import assert from "node:assert/strict";
import test from "node:test";
import { createElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import type { CredentialProviderRpcView, CredentialTypeView, SecretsList } from "#neoastra";
import { ShellLanguageContext } from "../shellLanguage";
import { CredentialProviders } from "./CredentialProviders";
import { SecretsBody } from "./SecretsSettings";
import { commandLine, draftsOf, isProviderId, isSecretReference, localSecret, missingSetting, noCredentials, pickedLabel, settingsOf, stateLabel, suggestedProviderId } from "./credentials";

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
  settings: [], ...extra,
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
