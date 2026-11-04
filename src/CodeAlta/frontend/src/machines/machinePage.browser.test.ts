import assert from "node:assert/strict";
import test from "node:test";
import { edge, withCanvas } from "../statistics/browserHarness";

const options = { entry: "../machines/machinePage.mount.tsx", fixture: "machineFixture", rpcFixture: "../demo-api.ts", styles: ["../machines/machines.css", "../secrets/secrets.css"],
  width: 1100, height: 900, reducedMotion: true } as const;
const type = (selector: string, value: string) => `(() => { const input = document.querySelector(${JSON.stringify(selector)});
  Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set.call(input, ${JSON.stringify(value)});
  input.dispatchEvent(new Event('input', { bubbles: true })); return true; })()`;
const choose = (selector: string, value: string) => `(() => { const select = document.querySelector(${JSON.stringify(selector)});
  Object.getOwnPropertyDescriptor(HTMLSelectElement.prototype, 'value').set.call(select, ${JSON.stringify(value)});
  select.dispatchEvent(new Event('change', { bubbles: true })); return true; })()`;
const calls = async (page: { evaluate: (script: string) => Promise<unknown> }) => (await page.evaluate(`[...window.machineFixture.calls]`) as string[]).map(call => JSON.parse(call));

test("the computer that manages a machine changes what its devices may do, its projects and its version from its page", { skip: !edge, timeout: 90_000 }, async () => {
  await withCanvas(async page => {
    await page.until(`document.querySelector('.machine-devices [role=row]:nth-child(2)')`, "the devices");
    // This computer first, with every permission and its own switch to stop managing.
    assert.deepEqual(await page.evaluate(`[...document.querySelectorAll('.machine-device:not(.machine-device-head) .machine-device-name strong')].map(item => item.textContent)`),
      ["desktop", "phone"]);
    assert.deepEqual(await page.evaluate(`[...document.querySelectorAll('.machine-device:nth-child(3) input[type=checkbox]')].map(input => input.checked)`),
      [true, false, false, false, false, false], "the phone may only view");
    await page.shot("machine-page");

    // A switch gives a permission at once.
    await page.evaluate(`document.querySelector('input[aria-label="Move — phone"]').click()`);
    await page.until(`document.querySelector('input[aria-label="Move — phone"]')?.checked === true`, "move given");

    // Revoke asks in a popover beside the row.
    await page.evaluate(`document.querySelector('button[aria-label="Revoke phone"]').click()`);
    await page.until(`document.querySelector('.machine-confirm')`, "the confirmation");
    assert.equal(await page.evaluate(`document.querySelector('.machine-confirm p').textContent`), "Revoke phone?");
    await page.clickText(".machine-confirm .bp6-button", "Revoke");
    await page.until(`document.querySelector('.machine-device[data-revoked=true]')`, "the phone revoked");

    // A URL clones, and the machine's note is shown.
    await page.evaluate(type(".machine-project-add input", "https://github.com/owner/repo"));
    await page.until(`document.querySelector('.machine-project-add button[type=submit]')?.textContent === 'Clone'`, "the Clone button");
    await page.clickText(".machine-project-add .bp6-button", "Clone");
    await page.until(`[...document.querySelectorAll('.machine-projects strong')].some(item => item.textContent === 'repo')`, "the clone listed");
    await page.until(`document.querySelector('.machine-section[aria-label=Projects]')?.textContent.includes('Cloned into')`, "the note");

    // Check for updates, then the update it found.
    await page.clickText(".machine-section[aria-label=Updates] .bp6-button", "Check for updates");
    await page.until(`[...document.querySelectorAll('.machine-section[aria-label=Updates] .bp6-button')].some(button => button.textContent === 'Update to 4.0.1')`, "the update");
    await page.clickText(".machine-section[aria-label=Updates] .bp6-button", "Update to 4.0.1");
    await page.until(`document.querySelector('.machine-section[aria-label=Updates] [role=status]')?.textContent.includes('switches to 4.0.1')`, "staged");

    // The log.
    await page.clickText(".machine-section[aria-label=Log] .bp6-button", "Show the log");
    await page.until(`document.querySelector('.machine-log')?.textContent.includes('CodeAlta Node started.')`, "the log");

    assert.deepEqual(await calls(page), [{ permission: ["phone", "move", true] }, { revoke: "phone" }, { clone: "https://github.com/owner/repo" },
      { update: "check" }, { update: "install" }]);
    assert.deepEqual(page.problems, []);
  }, options);
});

test("the computer that manages a machine sees why each provider is not ready, gives one its key and signs another in with a code", { skip: !edge, timeout: 90_000 }, async () => {
  await withCanvas(async page => {
    const section = ".machine-providers-section";
    await page.until(`document.querySelectorAll('${section} .machine-providers li').length === 3`, "the providers of the machine");
    assert.deepEqual(await page.evaluate(`[...document.querySelectorAll('${section} li')].map(row => row.dataset.state + ': ' + (row.querySelector('.machine-provider-problem')?.textContent ?? ''))`),
      ["ready: ", "not_ready: It has no key: the variable OPENAI_API_KEY is not set on the machine.", "not_ready: It is not signed in on the machine."],
      "the provider skipped for missing credentials says it on the page");
    await page.evaluate(`document.querySelector('${section}').scrollIntoView({ block: "nearest" })`);
    await page.shot("machine-providers");

    // Its key, typed here: the machine keeps it in its secrets, and the provider is ready.
    await page.clickText(`${section} .bp6-button`, "Add its key…");
    await page.until(`document.querySelector('.machine-provider-dialog input[type=password]')`, "the key field");
    await page.evaluate(type(".machine-provider-dialog input[type=password]", "sk-test-123"));
    await page.shot("machine-provider-key");
    await page.clickText(".machine-provider-dialog .bp6-button", "Save");
    await page.until(`document.querySelector('${section} li:nth-child(2)')?.dataset.state === 'ready'`, "OpenAI ready");
    assert.match(await page.evaluate(`document.querySelector('${section} li:nth-child(2) small').textContent`) as string, /key in the secret PROVIDER_OPENAI_API_KEY/u);

    // Copilot signs in with a code: the machine gives it, the page shows it with Copy, and follows the machine until it is signed in.
    await page.clickText(`${section} .bp6-button`, "Sign in…");
    await page.until(`document.querySelector('.machine-provider-signin-dialog .provider-account-code code')?.textContent === 'WDJB-MJHT'`, "the code");
    await page.until(`document.querySelector('.machine-provider-signin-dialog [role=status]')?.textContent.includes('signed in on build-box as octocat')`, "signed in");
    await page.clickText(".machine-provider-signin-dialog .bp6-button", "Done");
    await page.until(`document.querySelector('${section} li:nth-child(3)')?.dataset.state === 'ready'`, "Copilot ready");

    // A new provider: its type, its key.
    await page.clickText(`${section} .bp6-button`, "Add a provider…");
    await page.until(`document.querySelector('.machine-provider-dialog select')`, "the type");
    await page.evaluate(choose(".machine-provider-dialog select", "anthropic"));
    await page.evaluate(type(".machine-provider-dialog input[type=password]", "sk-ant-1"));
    await page.clickText(".machine-provider-dialog .bp6-button", "Add");
    await page.until(`document.querySelectorAll('${section} li').length === 4`, "the new provider");

    const asked = (await calls(page)).filter(call => call.setProvider || call.signIn);
    assert.deepEqual(asked, [{ setProvider: "openai", type: null, keyGiven: true, makeDefault: false }, { signIn: "copilot" },
      { setProvider: "anthropic", type: "anthropic", keyGiven: true, makeDefault: false }]);
    assert.ok(!JSON.stringify(await calls(page)).includes("sk-"), "the key is never recorded by the page");
    assert.deepEqual(page.problems, []);
  }, options);
});

test("the computer that manages a machine sets, rotates and deletes its secrets and edits its variables, never seeing a value back", { skip: !edge, timeout: 90_000 },
  async () => {
    await withCanvas(async page => {
      const section = ".machine-section[aria-label=Secrets]";
      await page.until(`document.querySelector('${section} .secrets-empty')`, "the secrets of the machine");
      assert.equal(await page.evaluate(`document.querySelector('${section} .secrets-empty').textContent`), "No secret on this machine.");
      assert.equal(await page.evaluate(`document.querySelector('${section} [data-variable=GRAFANA_URL] input').value`), "https://grafana.example");

      // Add a secret…: its name and its value, sent to the machine; the row shows its name, its reference and its version only.
      await page.clickText(`${section} .bp6-button`, "Add a secret…");
      await page.until(`document.querySelector('${section} .secret-new[data-kind=secret]')`, "the form");
      await page.evaluate(type(`${section} .secret-new[data-kind=secret] input[aria-label="Name of the secret"]`, "GRAFANA_TOKEN"));
      await page.evaluate(type(`${section} .secret-new[data-kind=secret] input[aria-label="Value of the secret"]`, "glsa_value_1"));
      await page.clickText(`${section} .secret-new[data-kind=secret] .bp6-button`, "Add");
      await page.until(`document.querySelector('${section} [data-secret=GRAFANA_TOKEN]')?.textContent.includes('Version 1')`, "the secret listed");
      assert.equal(await page.evaluate(`document.querySelector('${section} [data-secret=GRAFANA_TOKEN] .secret-reference').textContent`), "${secret:GRAFANA_TOKEN}");
      await page.shot("machine-page-secrets");

      // Rotate: a new value in a popover beside the row, a new version.
      await page.clickText(`${section} [data-secret=GRAFANA_TOKEN] .bp6-button`, "Rotate");
      await page.until(`document.querySelector('.secret-rotate input')`, "the new value");
      await page.evaluate(type(".secret-rotate input", "glsa_value_2"));
      await page.clickText(".secret-rotate .bp6-button", "Rotate");
      await page.until(`document.querySelector('${section} [data-secret=GRAFANA_TOKEN]')?.textContent.includes('Version 2')`, "rotated");

      // A variable is edited in place.
      await page.evaluate(type(`${section} [data-variable=GRAFANA_URL] input`, "https://grafana.example/v2"));
      await page.clickText(`${section} [data-variable=GRAFANA_URL] .bp6-button`, "Save");
      await page.until(`!document.querySelector('${section} [data-variable=GRAFANA_URL] .bp6-intent-primary')`, "saved");

      // Delete asks beside the row.
      await page.evaluate(`document.querySelector('${section} button[aria-label="Delete GRAFANA_TOKEN"]').click()`);
      await page.until(`[...document.querySelectorAll('.bp6-popover .bp6-button')].some(button => button.textContent === 'Delete')`, "the confirmation");
      await page.clickText(".bp6-popover .bp6-button", "Delete");
      await page.until(`document.querySelector('${section} .secrets-empty')`, "deleted");

      assert.equal(await page.evaluate(`document.body.textContent.includes('glsa_value')`), false, "no value of a secret is ever on the page");
      assert.deepEqual(await calls(page), [{ setSecret: "GRAFANA_TOKEN", machine: "managed" }, { rotateSecret: "GRAFANA_TOKEN" },
        { setVariable: "GRAFANA_URL", value: "https://grafana.example/v2" }, { deleteSecret: "GRAFANA_TOKEN" }]);
      assert.deepEqual(page.problems, []);
    }, options);
  });

test("the computer that manages a machine lets agents of other machines in, space by space, and turning on their bypass asks first", { skip: !edge, timeout: 90_000 },
  async () => {
    await withCanvas(async page => {
      const section = ".machine-agents";
      const bypass = `${section} input[aria-label="Bypass permissions"]`;
      const spaces = `[...document.querySelectorAll('${section} .machine-agent-spaces label')].map(label => [label.textContent, label.querySelector('input').checked])`;
      await page.until(`document.querySelector('${section} .machine-agent-spaces input')`, "the spaces of the machine");
      assert.deepEqual(await page.evaluate(spaces), [["Default", false], ["Work", false]], "off in every space");
      assert.equal(await page.evaluate(`document.querySelector('${section} .machine-section-head .bp6-tag').textContent`), "Off");
      assert.deepEqual(await page.evaluate(`[...document.querySelectorAll('${section} select option')].map(option => [option.value, option.textContent])`),
        [["", "As their parent"], ["workspace", "workspace"], ["strict", "strict"]]);

      // A space lets them in at once.
      await page.evaluate(`[...document.querySelectorAll('${section} .machine-agent-spaces label')].find(label => label.textContent === 'Work').querySelector('input').click()`);
      await page.until(`document.querySelector('${section} .machine-section-head .bp6-tag')?.textContent === 'On'`, "agents on");
      assert.deepEqual(await page.evaluate(spaces), [["Default", false], ["Work", true]]);

      // The sandbox their sessions run in at least.
      await page.evaluate(`(() => { const select = document.querySelector('${section} select');
        Object.getOwnPropertyDescriptor(HTMLSelectElement.prototype, 'value').set.call(select, 'strict');
        select.dispatchEvent(new Event('change', { bubbles: true })); })()`);
      await page.until(`document.querySelector('${section} select')?.value === 'strict' && !document.querySelector('${section} select').disabled`, "strict");

      // Turning on the bypass asks beside the switch; nothing changes before the person confirms.
      await page.evaluate(`document.querySelector('${bypass}').click()`);
      await page.until(`document.querySelector('.machine-bypass-confirm')`, "the confirmation");
      assert.equal(await page.evaluate(`document.querySelector('.machine-bypass-confirm p').textContent`), "Let their sessions bypass permissions when their parent does?");
      assert.equal(await page.evaluate(`document.querySelector('${bypass}').checked`), false);
      assert.deepEqual(await calls(page), [{ agentSpace: ["work", true] }, { agentSandbox: "strict" }]);
      await page.shot("machine-page-agents");
      await page.clickText(".machine-bypass-confirm .bp6-button", "Allow");
      await page.until(`document.querySelector('${bypass}')?.checked === true`, "bypass allowed");

      // Turning it off does not ask.
      await page.evaluate(`document.querySelector('${bypass}').click()`);
      await page.until(`document.querySelector('${bypass}')?.checked === false && !document.querySelector('${bypass}').disabled`, "bypass off");
      assert.equal(await page.evaluate(`document.querySelector('.machine-bypass-confirm')`), null);

      assert.deepEqual(await calls(page), [{ agentSpace: ["work", true] }, { agentSandbox: "strict" }, { agentBypass: true }, { agentBypass: false }]);
      assert.deepEqual(page.problems, []);
    }, options);
  });

test("giving up managing a machine asks first, and a machine this computer does not manage says how to be given it", { skip: !edge, timeout: 90_000 }, async () => {
  await withCanvas(async page => {
    await page.until(`document.querySelector('input[aria-label="Manage — desktop"]')`, "this computer's switch");
    await page.evaluate(`document.querySelector('input[aria-label="Manage — desktop"]').click()`);
    await page.until(`document.querySelector('.machine-confirm')?.textContent.includes('Stop managing this machine from here?')`, "the confirmation");
    assert.deepEqual(await calls(page), [], "nothing changes before the person confirms");

    await page.evaluate(`window.machineFixture.show('other')`);
    await page.until(`document.querySelector('.machine-command')`, "the command for the owner");
    assert.equal(await page.evaluate(`document.querySelector('.machine-command').textContent`), "altanode permissions me --allow manage");
    assert.equal(await page.evaluate(`document.querySelector('.machine-devices')`), null, "no switch for a machine this computer does not manage");
    assert.equal(await page.evaluate(`document.querySelector('.machine-agents')`), null, "nor for the agents of other machines");
    await page.shot("machine-page-not-managed");
    assert.deepEqual(page.problems, []);
  }, options);
});

test("the computer that manages a machine adds and removes its labels on its page", { skip: !edge, timeout: 90_000 }, async () => {
  await withCanvas(async page => {
    await page.until(`document.querySelector('.machine-labels .bp6-tag')`, "the labels");
    assert.deepEqual(await page.evaluate(`[...document.querySelectorAll('.machine-labels .bp6-tag')].map(tag => tag.textContent)`), ["gpu"]);
    // Labels are typed with commas or spaces; one that is not a label keeps Add off.
    await page.evaluate(type(".machine-labels input", "bad label!"));
    await page.until(`document.querySelector('.machine-labels button[type=submit]')?.disabled === true`, "refused");
    await page.evaluate(type(".machine-labels input", "prod, always-on"));
    await page.clickText(".machine-labels .bp6-button", "Add");
    await page.until(`document.querySelectorAll('.machine-labels .bp6-tag').length === 3`, "added");
    // The cross of a tag removes it.
    await page.evaluate(`[...document.querySelectorAll('.machine-labels .bp6-tag')].find(tag => tag.textContent === 'gpu').querySelector('button').click()`);
    await page.until(`document.querySelectorAll('.machine-labels .bp6-tag').length === 2`, "removed");
    const changes = (await calls(page)).filter(call => "labels" in call);
    assert.deepEqual(changes, [{ labels: ["gpu", "prod", "always-on"] }, { labels: ["prod", "always-on"] }]);
    assert.deepEqual(page.problems, []);
  }, options);
});
