import assert from "node:assert/strict";
import test from "node:test";
import { browserExecutable } from "../browserTarget";
import { fixtureChoices, session } from "./fixtureServer";
import { openPhone } from "./phoneBrowser";
import { testLimit } from "../testTime";

// Where the pictures of this test go, when a person wants to look at them.
const shots = process.env.CODEALTA_SHOTS ?? "";
const providers = [{ id: "codex", name: "Codex" }, { id: "claude", name: "Claude Code" }];

test("the options of a session move it to another provider of its space while it is idle, as the desktop does", { skip: !browserExecutable, timeout: testLimit(120_000) }, async () => {
  const phone = await openPhone(server => {
    server.state.sessions = [session("s1", "Fix the parser", "p1", 1), session("s2", "Write the docs", "p1", 5, { live: "running" })];
    for (const id of ["s1", "s2"]) {
      server.state.choices.set(id, { ...fixtureChoices, providers, current: { provider: "codex", providerName: "Codex", model: "gpt-6.1-sol", modelName: "GPT-6.1 Sol",
        reasoning: "low", agent: "default", agentName: "Default", permissionMode: null } });
    }
  }, shots);
  const { server, evaluate, waitFor, click, shot } = phone;
  const shown = (selector: string) => `!document.querySelector(${JSON.stringify(selector)}).hidden`;
  const choose = (value: string) => evaluate(`(() => { const select = document.getElementById("session-provider"); select.value = ${JSON.stringify(value)};
    select.dispatchEvent(new Event("change", { bubbles: true })); })()`);
  const moves = () => server.requests.filter(request => request.method === "POST" && request.path === "/api/sessions/s1/provider");
  try {
    await phone.signIn();
    await evaluate("location.hash = '#/session/s1'");
    await waitFor(`document.querySelector('#detail-title').textContent === "Fix the parser"`, "the session");

    // The providers of its space, the one it runs on chosen.
    await click("#pick-more");
    await waitFor(shown("#session-more-sheet"), "the options of the session");
    await waitFor(shown("#session-provider-field"), "the provider of the session");
    assert.deepEqual(await evaluate("[...document.querySelectorAll('#session-provider option')].map(o => o.textContent)"), ["Codex", "Claude Code"]);
    assert.equal(await evaluate("document.getElementById('session-provider').value"), "codex");
    assert.equal(await evaluate("document.getElementById('session-provider').disabled"), false);

    // A refusal says why, and the session stays where it was.
    server.state.providerError = "A prompt waits in the queue of the session: send it or remove it, then change the provider.";
    await choose("claude");
    await waitFor(`document.getElementById('session-provider-text').textContent.includes("A prompt waits in the queue")`, "the refusal");
    assert.equal(moves().length, 1);
    assert.equal(await evaluate("document.getElementById('session-provider').value"), "codex");

    // A pick of a model of the provider it leaves does not follow it: the next prompt runs with the default of the new one.
    await click("#session-more-done");
    await click("#pick-model");
    await waitFor(`!document.getElementById('model-sheet').hidden`, "the model sheet");
    await evaluate(`[...document.querySelectorAll("#model-list button")].find(b => b.textContent.includes("Argon")).click()`);
    await click("#model-done");
    await waitFor(`document.getElementById('pick-model').classList.contains('pending')`, "a model picked for the next prompt");
    await click("#pick-more");
    await choose("claude");
    await waitUntil(() => moves().length === 2, "the move asked");
    assert.deepEqual(moves()[1].body, { provider: "claude" });
    await waitFor(`document.getElementById('session-provider').value === "claude" && !document.getElementById('session-provider').disabled`, "the session on its new provider");
    assert.equal(await evaluate("document.getElementById('pick-model').classList.contains('pending')"), false, "the model picked for the old provider is dropped");
    await shot("provider-options.png");
    await click("#session-more-done");

    // A session at work changes provider once it is idle.
    await evaluate("location.hash = '#/session/s2'");
    await waitFor(`document.querySelector('#detail-title').textContent === "Write the docs"`, "the session at work");
    await click("#pick-more");
    await waitFor(shown("#session-provider-field"), "its provider");
    assert.equal(await evaluate("document.getElementById('session-provider').disabled"), true);
    assert.match(await evaluate<string>("document.getElementById('session-provider-text').textContent"), /once the session is idle/);
    assert.deepEqual(phone.failures, []);
  } finally {
    await phone.close();
  }

  async function waitUntil(condition: () => boolean, what: string) {
    const deadline = Date.now() + testLimit(8000);
    while (!condition()) {
      if (Date.now() > deadline) assert.fail(`${what}: not seen`);
      await new Promise(resolve => setTimeout(resolve, 50));
    }
  }
});
