import assert from "node:assert/strict";
import test from "node:test";
import { createElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import { ShellLanguageContext } from "../shellLanguage";
import { SecretUsePromptCard } from "./SecretUsePrompt";
import { confirmationNote, promptedUses, useKey, whoAsks, withoutAnswer, type PromptedUse } from "./secretPrompt";

const never = () => assert.fail("rendering must not act");
const render = (element: ReturnType<typeof createElement>) => renderToStaticMarkup(createElement(ShellLanguageContext.Provider,
  { value: { locale: "en", choice: "en", setLanguage: never } }, element));
const titles = (id: string) => id === "01a1-session" ? "Fix the parser" : null;
const shell: PromptedUse = { name: "GITHUB_TOKEN", use: "shell of the session 01a1-session", at: "2026-11-12T09:00:00Z", sessionId: "01a1-session",
  awaitedUntil: "2026-11-12T09:02:00Z", prompt: true };
const provider: PromptedUse = { name: "ANTHROPIC_API_KEY", use: "provider anthropic", at: "2026-11-12T08:59:00Z", sessionId: null, awaitedUntil: null, prompt: true };
const stale: PromptedUse = { name: "OLD", use: "shell of the session gone", at: "2026-11-12T08:00:00Z", sessionId: "gone", awaitedUntil: null, prompt: false };

test("the prompt asks first about what waits for the answer, and leaves out the shell of a session that no longer waits", () => {
  assert.deepEqual(promptedUses([provider, stale, shell]).map(use => use.name), ["GITHUB_TOKEN", "ANTHROPIC_API_KEY"]);
  assert.deepEqual(promptedUses(null), []);
  assert.equal(useKey({ name: "github_token", use: null }), "GITHUB_TOKEN\n");
});

test("who asks is named: the shell of a session by its title, else the use as the host names it", () => {
  assert.deepEqual(whoAsks(shell, titles), { key: "The shell of the session “{title}” waits for your answer to start.", parameters: { title: "Fix the parser" } });
  assert.deepEqual(whoAsks({ ...shell, awaitedUntil: null }, () => null).parameters, { title: "01a1-session" }, "the id of a session whose title is not known");
  assert.deepEqual(whoAsks(provider, titles), { key: "Asked for the {use}.", parameters: { use: "provider anthropic" } });
  assert.equal(whoAsks({ ...provider, use: "shell of a session" }, titles).key, "Asked by the shell of a session.");
  assert.equal(whoAsks({ ...provider, use: null }, titles).key, "Asked for a use nobody named.");
  assert.equal(withoutAnswer(provider, "en"), null, "nothing waits for the answer of a provider here");
  assert.equal(withoutAnswer(shell, "en")?.key, "Without an answer by {time}, it starts without the secret.");
});

test("a confirmation that the user canceled or the system could not make says so, and one that went through says nothing", () => {
  assert.equal(confirmationNote("ok"), null);
  assert.equal(confirmationNote("canceled")?.key, "The verification was canceled: confirm again, or deny.");
  assert.equal(confirmationNote("unavailable")?.key, "The system cannot verify you now.");
  assert.equal(confirmationNote("stale_epoch")?.key, "The answer could not be sent.");
});

test("the card names the secret and the session, offers Confirm… and Deny, and never a value", () => {
  const html = render(createElement(SecretUsePromptCard, { use: shell, more: 1, canConfirm: true, reason: null, busy: false, note: null, sessionTitle: titles,
    onConfirm: never, onDeny: never }));
  assert.match(html, /role="alertdialog"/);
  assert.match(html, /data-secret="GITHUB_TOKEN"/);
  assert.match(html, /Use the secret GITHUB_TOKEN\?/);
  assert.match(html, /The shell of the session “Fix the parser” waits for your answer to start\./);
  assert.match(html, /Without an answer by/);
  assert.match(html, /1 more waiting/);
  assert.match(html, />Deny</);
  assert.match(html, /title="Windows Hello or Touch ID verifies you first\."[^>]*>|Confirm…/);
  assert.doesNotMatch(html, /<button[^>]*disabled=""[^>]*>(?:(?!<\/button>).)*Confirm…/, "Confirm is offered");
});

test("where the system cannot verify the user, Confirm is not offered and the card says why", () => {
  const html = render(createElement(SecretUsePromptCard, { use: provider, more: 0, canConfirm: false, reason: "Windows Hello is not set up.", busy: false,
    note: confirmationNote("canceled"), sessionTitle: titles, onConfirm: never, onDeny: never }));
  assert.match(html, /Windows Hello is not set up\./);
  assert.match(html, /The verification was canceled: confirm again, or deny\./);
  assert.match(html, /<button[^>]*disabled=""[^>]*>(?:(?!<\/button>).)*Confirm…/, "Confirm is disabled");
  assert.doesNotMatch(html, /more waiting/);
});
