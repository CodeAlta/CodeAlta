import assert from "node:assert/strict";
import test from "node:test";
import type { Plugin } from "esbuild";
import { edge, withCanvas } from "../statistics/browserHarness";

// The form of the chip shows no editor: the worker of the editor the composer module names is never started.
const noWorker: Plugin = { name: "no-editor-worker", setup(bundle) {
  bundle.onResolve({ filter: /^monaco-editor\/.*\?worker$/ }, args => ({ path: args.path, namespace: "no-worker" }));
  bundle.onLoad({ filter: /.*/, namespace: "no-worker" }, () => ({ loader: "js", contents: "export default class {}" }));
} };
const options = { entry: "../sandbox/sandboxSession.mount.tsx", fixture: "sandboxSessionFixture", rpcFixture: "../demo-api.ts", width: 1100, height: 900, plugins: [noWorker] } as const;
const choose = (selector: string, value: string) => `(() => { const select = document.querySelector(${JSON.stringify(selector)});
  Object.getOwnPropertyDescriptor(HTMLSelectElement.prototype, 'value').set.call(select, ${JSON.stringify(value)});
  select.dispatchEvent(new Event('change', { bubbles: true })); return true; })()`;

test("a command to run outside the sandbox is a card of its own: the agent's reason quoted, allowed once or denied", { skip: !edge, timeout: 90_000 }, async () => {
  await withCanvas(async page => {
    await page.until(`document.querySelector('.command-permission-panel h3')`, "the request");
    assert.equal(await page.evaluate(`document.querySelector('.command-permission-panel h3').textContent`), "Run outside the sandbox?");
    assert.equal(await page.evaluate(`document.querySelector('.command-permission-panel').dataset.outsideSandbox`), "true");
    assert.equal(await page.evaluate(`document.querySelector('[data-permission-command]').textContent`), "dotnet nuget push bin/Lib.1.2.0.nupkg --source nuget.org");
    assert.equal(await page.evaluate(`document.querySelector('blockquote[data-permission-reason]').textContent`),
      "“Publishing needs the API key in your NuGet.Config, which the sandbox cannot read.”");
    // Once at most: there is nothing to allow from now on.
    assert.deepEqual(await page.evaluate(`[...document.querySelectorAll('[data-permission-decision]')].map(button => button.dataset.permissionDecision)`), ["allow_once", "deny"]);
    await page.until(`!document.querySelector('[data-permission-decision=allow_once]').disabled`, "the choices armed");
    await page.click("[data-permission-decision=allow_once]");
    await page.until(`sandboxSessionFixture.decisions.length === 1`, "the decision");
    assert.equal(await page.evaluate(`sandboxSessionFixture.decisions[0].decision`), "allow_once");
    assert.deepEqual(page.problems, []);
  }, options);
});

test("the sandbox of a project is chosen beside its row: the choice is saved at once and closes the popover", { skip: !edge, timeout: 90_000 }, async () => {
  await withCanvas(async page => {
    await page.until(`document.querySelector('.sandbox-project-form select')?.value === 'strict'`, "the sandbox of the project");
    assert.deepEqual(await page.evaluate(`[...document.querySelectorAll('.sandbox-project-form option')].map(option => option.textContent)`),
      ["Follow the settings", "Not sandboxed", "workspace", "strict"]);
    await page.until(`document.activeElement === document.querySelector('.sandbox-project-form select')`, "the focus on the select");
    await page.evaluate(choose(".sandbox-project-form select", ""));
    await page.until(`sandboxSessionFixture.scopes.length === 1 && !window.projectSandboxOpen`, "the save, and the popover closed");
    assert.deepEqual(await page.evaluate(`(() => { const request = sandboxSessionFixture.scopes[0]; return [request.kind, request.id, request.modelId, request.sandbox]; })()`),
      ["project", "lib", null, null]);
    assert.deepEqual(page.problems, []);
  }, options);
});

test("the Agent chip marks a sandboxed session, and its Sandbox row follows the settings or gives the session its own", { skip: !edge, timeout: 90_000 }, async () => {
  await withCanvas(async page => {
    await page.until(`document.querySelector('.composer-selection .composer-selection-sandbox')`, "the mark of the sandbox");
    assert.equal(await page.evaluate(`document.querySelector('.composer-selection-sandbox').title`), "Sandbox: workspace · From its provider");
    assert.ok((await page.evaluate<string>(`document.querySelector('.composer-selection').title`)).endsWith("\nSandbox: workspace · From its provider"));
    await page.click(".composer-selection");
    await page.until(`document.querySelector('#composer-sandbox-s')`, "the form of the chip");
    assert.deepEqual(await page.evaluate(`[...document.querySelectorAll('#composer-sandbox-s option')].map(option => option.textContent)`),
      ["workspace · Default", "Off", "workspace", "strict"]);
    assert.equal(await page.evaluate(`document.querySelector('#composer-sandbox-s').closest('.bp6-form-group').querySelector('.bp6-form-helper-text').textContent`), "From its provider");

    // Off for this session: the mark goes, the chip says it.
    await page.evaluate(choose("#composer-sandbox-s", "off"));
    await page.until(`!document.querySelector('.composer-selection-sandbox')`, "the mark gone");
    assert.ok((await page.evaluate<string>(`document.querySelector('.composer-selection').title`)).endsWith("\nSandbox: Off"));
    // A profile of its own, then one that cannot be used: the mark and the row say why.
    await page.evaluate(choose("#composer-sandbox-s", "strict"));
    await page.until(`document.querySelector('.composer-selection-sandbox')?.title === 'Sandbox: strict · Chosen for this session'`, "the profile of the session");
    await page.evaluate(`breakProfile()`);
    await page.until(`document.querySelector('.composer-selection-sandbox[data-problem="true"]')`, "the problem marked");
    assert.equal(await page.evaluate(`document.querySelector('#composer-sandbox-s').closest('.bp6-form-group').querySelector('[role=alert]').textContent`),
      "There is no sandbox profile 'gone'.");
    await page.evaluate(choose("#composer-sandbox-s", ""));
    await page.until(`sandboxSessionFixture.chosen.length === 3`, "back to the settings");
    assert.deepEqual(await page.evaluate(`sandboxSessionFixture.chosen`), ["off", "strict", null]);
    assert.deepEqual(page.problems, []);
  }, options);
});

test("the options of a session are behind More options: the switch of sub-sessions, marked on the chip when it is off", { skip: !edge, timeout: 90_000 }, async () => {
  await withCanvas(async page => {
    await page.until(`document.querySelector('.composer-selection')`, "the chip");
    assert.equal(await page.evaluate(`!!document.querySelector('.composer-selection-no-sub-sessions')`), false, "no mark with the defaults");
    await page.click(".composer-selection");
    await page.until(`document.querySelector('.composer-selection-more-button')`, "the form of the chip");
    // Closed with the defaults: the form stays short, and the button says nothing changed.
    assert.equal(await page.evaluate(`document.querySelector('.composer-selection-more-button').getAttribute('aria-expanded')`), "false");
    assert.equal(await page.evaluate(`!!document.querySelector('#composer-sub-sessions-s')`), false);
    assert.equal(await page.evaluate(`!!document.querySelector('.composer-selection-more-count')`), false);
    await page.click(".composer-selection-more-button");
    await page.until(`document.querySelector('#composer-sub-sessions-s')`, "the switch");
    assert.equal(await page.evaluate(`document.querySelector('#composer-sub-sessions-s').checked`), true);
    assert.equal(await page.evaluate(`document.querySelector('#composer-sub-sessions-s').closest('.bp6-form-group').querySelector('.bp6-form-helper-text').textContent`),
      "The agent can start sessions of its own (sub-agents).");

    // Off: the session records it, the button counts it, and the chip marks it.
    await page.click("#composer-sub-sessions-s");
    await page.until(`sandboxSessionFixture.optionsChosen.length === 1`, "the choice");
    assert.deepEqual(await page.evaluate(`sandboxSessionFixture.optionsChosen[0]`), { subSessions: false });
    await page.until(`document.querySelector('.composer-selection-more-count')?.textContent === '1'`, "the count");
    assert.equal(await page.evaluate(`document.querySelector('#composer-sub-sessions-s').closest('.bp6-form-group').querySelector('.bp6-form-helper-text').textContent`),
      "The agent does all the work itself: it starts no session.");
    await page.until(`document.querySelector('.composer-selection-no-sub-sessions')?.title === 'Sub-sessions: Off'`, "the mark of the chip");
    assert.ok((await page.evaluate<string>(`document.querySelector('.composer-selection').title`)).endsWith("\nSub-sessions: Off"));

    // On again: the defaults, which a session records as nothing.
    await page.click("#composer-sub-sessions-s");
    await page.until(`sandboxSessionFixture.optionsChosen.length === 2`, "the second choice");
    assert.equal(await page.evaluate(`sandboxSessionFixture.optionsChosen[1]`), null);
    await page.until(`!document.querySelector('.composer-selection-no-sub-sessions')`, "the mark gone");
    assert.deepEqual(page.problems, []);
  }, options);
});
