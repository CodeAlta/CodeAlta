import assert from "node:assert/strict";
import test from "node:test";
import { createElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import type { SecretsList } from "#neoastra";
import { ShellLanguageContext } from "../shellLanguage";
import { ProviderKeySecret } from "./ProviderKeySecret";
import { SecretsBody, type SecretsActions } from "./SecretsSettings";
import { isSecretName, referenceOf, secretNameProblem, suggestedSecretName } from "./secrets";

const never = () => assert.fail("rendering must not act");
const actions: SecretsActions = { setSecret: never, rotateSecret: never, deleteSecret: never, setVariable: never, deleteVariable: never };
const listing: SecretsList = {
  status: "ok", protection: "dpapi",
  secrets: [{ name: "GRAFANA_TOKEN", version: 2, setBy: "alex (CodeAlta Desktop)", setAt: "2026-11-03T10:00:00Z" }],
  variables: [{ name: "GRAFANA_URL", value: "https://grafana.example", version: 1, setBy: "alex (altanode)", setAt: "2026-11-02T10:00:00Z" }],
};
const render = (element: ReturnType<typeof createElement>) => renderToStaticMarkup(createElement(ShellLanguageContext.Provider,
  { value: { locale: "en", choice: "en", setLanguage: never } }, element));

test("a secret is listed by name, version, and who set it when, with Rotate and Delete; never with a value", () => {
  const html = render(createElement(SecretsBody, { listing, busy: false, run: never, actions }));

  assert.match(html, /data-secret="GRAFANA_TOKEN"/);
  assert.match(html, /<code>GRAFANA_TOKEN<\/code>/);
  assert.match(html, /\$\{secret:GRAFANA_TOKEN\}/);
  assert.match(html, /Version 2/);
  assert.match(html, /by alex \(CodeAlta Desktop\)/);
  assert.match(html, />Rotate</);
  assert.match(html, /aria-label="Delete GRAFANA_TOKEN"/);
  // A list of names: there is no field that holds the value of a secret.
  assert.doesNotMatch(html, /aria-label="Value of GRAFANA_TOKEN"/);
});

test("a variable shows its value, editable, and its reference", () => {
  const html = render(createElement(SecretsBody, { listing, busy: false, run: never, actions }));

  assert.match(html, /data-variable="GRAFANA_URL"/);
  assert.match(html, /aria-label="Value of GRAFANA_URL"[^>]*value="https:\/\/grafana.example"|value="https:\/\/grafana.example"[^>]*aria-label="Value of GRAFANA_URL"/);
  assert.match(html, /\$\{var:GRAFANA_URL\}/);
  assert.match(html, /aria-label="Delete GRAFANA_URL"/);
  assert.match(html, /aria-label="Name of the variable"/);
});

test("a machine without a secret says so, and Add shows a form whose value is hidden", () => {
  const empty: SecretsList = { status: "ok", protection: null, secrets: [], variables: [] };
  assert.match(render(createElement(SecretsBody, { listing: empty, busy: false, run: never, actions })), /No secret on this machine\./);

  const html = render(createElement(SecretsBody, { listing: empty, busy: false, adding: true, run: never, actions }));
  assert.match(html, /data-kind="secret"/);
  assert.match(html, /type="password"[^>]*aria-label="Value of the secret"|aria-label="Value of the secret"[^>]*type="password"/);
  assert.doesNotMatch(html, /No secret on this machine\./);
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
