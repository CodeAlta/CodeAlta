import assert from "node:assert/strict";
import test from "node:test";
import { edge, withCanvas } from "../statistics/browserHarness";

const options = { entry: "../sandbox/sandboxSettings.mount.tsx", fixture: "sandboxFixture", rpcFixture: "../demo-api.ts", width: 1300, height: 1000 } as const;
// Chooses an option of a native select as the user does: React reads the change event.
const choose = (selector: string, value: string) => `(() => { const select = document.querySelector(${JSON.stringify(selector)});
  Object.getOwnPropertyDescriptor(HTMLSelectElement.prototype, 'value').set.call(select, ${JSON.stringify(value)});
  select.dispatchEvent(new Event('change', { bubbles: true })); return true; })()`;
const steps = `[...document.querySelectorAll('.sandbox-check-steps > li[data-passed]')].map(row => row.dataset.passed + ' ' + row.querySelector('.sandbox-check-step').textContent)`;

test("the status of the sandbox and its check, whose steps come as the host gives them", { skip: !edge, timeout: 90_000 }, async () => {
  await withCanvas(async page => {
    await page.until(`document.querySelector('.sandbox-availability[data-available="true"]')`, "the status");
    assert.equal(await page.evaluate(`document.querySelector('.sandbox-availability strong').textContent`), "Available: Windows process container");
    // The profile checked is the one of every session, else workspace.
    assert.equal(await page.evaluate(`document.querySelector('[aria-label="Profile to check"]').value`), "workspace");
    await page.evaluate(choose('[aria-label="Profile to check"]', "strict"));
    await page.clickText(".sandbox-status button", "Check");
    await page.until(`document.querySelectorAll('.sandbox-check-steps > li[data-passed]').length === 1`, "the first step");
    // The check goes on: the window says so, and the button waits for its end.
    assert.equal(await page.evaluate(`!!document.querySelector('.sandbox-check-waiting') && document.querySelector('.sandbox-status button').disabled`), true);
    assert.deepEqual(await page.evaluate(`sandboxFixture.checks`), ["strict"]);
    await page.evaluate(`sandboxFixture.release()`);
    await page.until(`document.querySelector('.sandbox-check-result')`, "the end of the check");
    assert.deepEqual(await page.evaluate(steps), ["true The sandbox of this computer", "true The start of a sandbox", "true dotnet --version", "false A file of CodeAlta out of its reach"]);
    assert.equal(await page.evaluate(`document.querySelector('.sandbox-check-result').textContent`), "strict did not pass the check");
    assert.equal(await page.evaluate(`document.querySelector('.sandbox-check-steps > li[data-passed="false"] .sandbox-check-detail').textContent`), "The file was read.");
    assert.equal(await page.evaluate(`!document.querySelector('.sandbox-check-waiting') && !document.querySelector('.sandbox-status button').disabled`), true);

    // Every session: sandboxed with the profile beside it, then another profile, then none; each saved at once.
    await page.clickText(".sandbox-every-session label.bp6-radio", "Sandboxed with");
    await page.until(`sandboxFixture.savedAll.length === 1`, "the save of every session");
    await page.evaluate(choose('[aria-label="Profile of every session"]', "strict"));
    await page.until(`sandboxFixture.savedAll.length === 2`, "the save of the other profile");
    await page.clickText(".sandbox-every-session label.bp6-radio", "Not sandboxed");
    await page.until(`sandboxFixture.savedAll.length === 3`, "the save of none");
    assert.deepEqual(await page.evaluate(`sandboxFixture.savedAll`), ["workspace", "strict", null]);

    // The places that choose a sandbox, each a link to where it is chosen.
    assert.deepEqual(await page.evaluate(`[...document.querySelectorAll('.sandbox-scope-list > li')].map(row => row.querySelector('.sandbox-scope-name').textContent + ' ' + row.querySelector('.sandbox-scope-profile').textContent)`),
      ["Ollama strict", "Work Not sandboxed", "Codex · gpt-6.1 workspace"]);
    await page.clickText(".sandbox-scope-link .sandbox-scope-name", "Work");
    assert.deepEqual(await page.evaluate(`sandboxFixture.opened.map(scope => scope.kind + ':' + scope.id)`), ["space:work"]);
    assert.deepEqual(page.problems, []);
  }, options);
});

test("a profile of CodeAlta is read-only and starts a profile of the user, which is saved, shown with its problem and removed", { skip: !edge, timeout: 90_000 }, async () => {
  await withCanvas(async page => {
    await page.until(`document.querySelector('[data-sandbox-profile="workspace"][aria-selected="true"]')`, "the first profile");
    // workspace is shown as it resolves, and cannot be changed.
    assert.equal(await page.evaluate(`[...document.querySelectorAll('.sandbox-profile .sandbox-list-read .bp6-tag')].map(tag => tag.textContent).join(',')`), "nuget.org,*.nuget.org,github.com,~/.dotnet");
    assert.equal(await page.evaluate(`!document.querySelector('.sandbox-profile .bp6-tag-input') && document.querySelector('#sandbox-profile-write .sandbox-list-none')?.textContent`), "None",
      "nothing to type in, and an empty list says so");
    assert.equal(await page.evaluate(`document.querySelector('input[name="sandbox-network"][value="hosts"]').checked && document.querySelector('input[name="sandbox-network"][value="hosts"]').disabled`), true);
    assert.equal(await page.evaluate(`!document.querySelector('#sandbox-profile-name')`), true, "a profile of CodeAlta has no name to change");

    await page.clickText(".sandbox-profile-actions button", "New profile from this");
    await page.until(`document.querySelector('#sandbox-profile-name')?.value === 'my-workspace'`, "the new profile");
    assert.equal(await page.evaluate(`document.querySelector('#sandbox-profile-extends').value`), "workspace");
    // Its network is the one of its base until it chooses its own, and the hosts of the base are said, not copied.
    assert.equal(await page.evaluate(`document.querySelector('input[name="sandbox-network"][value=""]').checked && document.querySelector('input[name="sandbox-network"][value=""]').parentElement.textContent`),
      "As in workspace (Allowed hosts)");
    assert.equal(await page.evaluate(`[...document.querySelectorAll('.sandbox-inherited')].some(line => line.textContent === 'From workspace: nuget.org, *.nuget.org, github.com')`), true);
    await page.click("#sandbox-profile-hosts");
    await page.type("api.example.com");
    await page.key("Enter", 13, "\r");
    await page.until(`[...document.querySelectorAll('.sandbox-profile .bp6-tag')].some(tag => tag.textContent === 'api.example.com')`, "the added host");
    await page.click('input[name="sandbox-outside"][value="never"]');
    await page.clickText(".sandbox-profile-actions button", "Save");
    await page.until(`sandboxFixture.savedProfiles.length === 1 && document.querySelector('[data-sandbox-profile="my-workspace"][aria-selected="true"]')`, "the saved profile, shown");
    assert.deepEqual(await page.evaluate(`(() => { const saved = sandboxFixture.savedProfiles[0]; return [saved.originalName, saved.profile.name, saved.profile.extends, saved.profile.network,
      saved.profile.outside, saved.profile.hosts.join(','), saved.profile.builtIn]; })()`), [null, "my-workspace", "workspace", null, "never", "api.example.com", false]);
    // Saved, it has nothing to save until it changes.
    const save = `[...document.querySelectorAll('.sandbox-profile-actions button')].find(button => button.textContent === 'Save')`;
    assert.equal(await page.evaluate(`${save}.disabled`), true);
    // An edit not saved yet stays while another change of the page is saved and the page read again.
    await page.click('input[name="sandbox-outside"][value="ask"]');
    await page.until(`!${save}.disabled`, "the edit");
    const reads = await page.evaluate<number>(`sandboxFixture.reads`);
    await page.clickText(".sandbox-every-session label.bp6-radio", "Sandboxed with");
    await page.until(`sandboxFixture.reads > ${reads}`, "the page read again");
    assert.equal(await page.evaluate(`document.querySelector('input[name="sandbox-outside"][value="ask"]').checked && !${save}.disabled`), true, "the edit is kept");

    // A profile that does not resolve says why.
    await page.click('[data-sandbox-profile="broken"]');
    await page.until(`document.querySelector('.sandbox-profile-problem')`, "the problem of the profile");
    assert.equal(await page.evaluate(`document.querySelector('.sandbox-profile-problem').textContent`), "The sandbox profile 'broken' extends 'gone', which does not exist.");

    // Removing a profile asks in a small popover beside its button.
    await page.click('[data-sandbox-profile="my-workspace"]');
    await page.until(`document.querySelector('#sandbox-profile-name')?.value === 'my-workspace'`, "the profile again");
    await page.clickText(".sandbox-profile-actions button", "Remove");
    await page.until(`document.querySelector('.provider-settings-confirm')`, "the question");
    assert.deepEqual(await page.evaluate(`sandboxFixture.deleted`), [], "asking removes nothing");
    await page.clickText(".provider-settings-confirm button", "Remove");
    await page.until(`sandboxFixture.deleted.length === 1 && !document.querySelector('[data-sandbox-profile="my-workspace"]')`, "the removal");
    assert.deepEqual(await page.evaluate(`sandboxFixture.deleted`), ["my-workspace"]);
    assert.deepEqual(page.problems, []);
  }, options);
});
