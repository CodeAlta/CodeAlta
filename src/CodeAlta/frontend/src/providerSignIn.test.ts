import assert from "node:assert/strict";
import test from "node:test";
import { loginFailure, loginModeLabel, loginPrompt, providerDefault } from "./providerSignIn";

test("a sign-in prompt shows only an https address and a printable code", () => {
  assert.deepEqual(loginPrompt({ url: "https://github.com/login/device", userCode: "ABCD-1234", browserOpened: true }),
    { url: "https://github.com/login/device", userCode: "ABCD-1234", browserOpened: true });
  assert.deepEqual(loginPrompt({ url: "http://127.0.0.1:1/x", userCode: null, browserOpened: true }), { url: null, userCode: null, browserOpened: false });
  assert.equal(loginPrompt({ url: "https://a b", userCode: "two words", browserOpened: false }).url, null);
  assert.equal(loginPrompt({ url: null, userCode: "two words", browserOpened: false }).userCode, null);
  assert.equal(loginModeLabel("device"), "Sign in with a device code");
  assert.equal(loginModeLabel("browser"), "Sign in with the browser");
  assert.equal(loginFailure("canceled").intent, "warning");
  assert.equal(loginFailure("login_failed").intent, "danger");
  assert.equal(loginFailure(null).key, "The sign-in did not complete.");
});

test("a blank field falls back to the provider's own default, then to its adapter type's", () => {
  const types = [{ type: "codex", defaults: { displayName: "Codex", apiUrl: "https://api.openai.com/v1" } },
    { type: "openai-chat", defaults: { displayName: null, apiUrl: null } }];
  const original = { type: "openai-chat", defaults: { displayName: "DeepSeek", apiUrl: "https://api.deepseek.com" } };
  assert.equal(providerDefault("apiUrl", "openai-chat", original, types), "https://api.deepseek.com");
  assert.equal(providerDefault("displayName", "codex", original, types), "Codex", "after changing the adapter type the old provider's defaults no longer apply");
  assert.equal(providerDefault("apiUrl", "openai-chat", null, types), null);
  assert.equal(providerDefault("apiUrl", "unknown", null, types), null);
});
