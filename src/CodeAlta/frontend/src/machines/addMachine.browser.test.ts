import assert from "node:assert/strict";
import test from "node:test";
import { edge, withCanvas } from "../statistics/browserHarness";

const options = { entry: "../machines/addMachine.mount.tsx", fixture: "addMachineFixture", rpcFixture: "../demo-api.ts", styles: ["../machines/machines.css"], width: 1100,
  height: 860, reducedMotion: true } as const;
const type = (selector: string, value: string) => `(() => { const input = document.querySelector(${JSON.stringify(selector)});
  Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set.call(input, ${JSON.stringify(value)});
  input.dispatchEvent(new Event('input', { bubbles: true })); return true; })()`;
const calls = async (page: { evaluate: (script: string) => Promise<unknown> }) => (await page.evaluate(`[...window.addMachineFixture.calls]`) as string[]).map(call => JSON.parse(call));

test("over SSH, the assistant shows each step, says what to do when ssh does not know the computer, and ends on the paired machine", { skip: !edge, timeout: 90_000 }, async () => {
  await withCanvas(async page => {
    await page.until(`document.querySelectorAll('.add-machine-choice').length === 3`, "the three ways");
    assert.deepEqual(await page.evaluate(`[...document.querySelectorAll('.add-machine-choice strong')].map(item => item.textContent)`),
      ["Over SSHRecommended", "With a pairing link", "Through my server · Home"]);
    await page.shot("add-machine-choices");
    await page.evaluate(`document.querySelector('.add-machine-choice').click()`);
    await page.until(`document.querySelector('.add-machine-field input')`, "the SSH form");
    assert.equal(await page.evaluate(`[...document.querySelectorAll('.add-machine-dialog .bp6-button')].find(button => button.textContent === 'Add the machine').disabled`), true);
    await page.evaluate(type(".add-machine-field input", "me@pc-ai"));
    // Managing the machine from here is asked: off until the person ticks it.
    assert.equal(await page.evaluate(`document.querySelector('.add-machine-manage input').checked`), false);
    await page.evaluate(`document.querySelector('.add-machine-manage input').click()`);
    await page.clickText(".add-machine-dialog .bp6-button", "Add the machine");

    // ssh does not know the computer: the step fails with what to do and the key to compare, and the setup starts over trusting it.
    await page.until(`document.querySelector('.add-machine-failure')`, "the failure");
    assert.match(await page.evaluate(`document.querySelector('.add-machine-failure').textContent`) as string, /does not know the SSH key of me@pc-ai yet/u);
    assert.equal(await page.evaluate(`document.querySelector('.add-machine-host-key code')?.textContent`), "SHA256:Qm9vLWtleS1maW5nZXJwcmludA (ED25519)");
    await page.shot("add-machine-host-key");
    await page.clickText(".add-machine-dialog .bp6-button", "Trust it and start over");
    await page.until(`document.querySelectorAll('.add-machine-steps li[data-state=done]').length === 5`, "every step done");
    assert.deepEqual(await page.evaluate(`[...document.querySelectorAll('.add-machine-steps li small')].map(item => item.textContent)`),
      ["me@pc-ai", "Linux x86_64 · .NET SDK 10.0.114", "CodeAlta Node 4.0.0 · ~/.dotnet/tools", "systemd --user unit, port 2585", "apple river stone cloud amber tiger"]);
    await page.until(`document.querySelector('.add-machine-dialog [role=status]')?.textContent.includes('pc-ai is paired with this computer and managed from here.')`, "the outcome");
    await page.shot("add-machine-ssh-done");
    await page.clickText(".add-machine-dialog .bp6-button", "Open its page");
    assert.deepEqual(await calls(page), [{ ssh: "me@pc-ai", trust: null, port: 2585 }, { ssh: "me@pc-ai", trust: "SHA256:Qm9vLWtleS1maW5nZXJwcmludA (ED25519)", port: 2585 }, { open: "pc-ai-id" }]);
    assert.deepEqual(page.problems, []);
  }, options);
});

test("through the server, only the node whose words the person confirms is approved, as a node: a node the server planted is not", { skip: !edge, timeout: 90_000 }, async () => {
  await withCanvas(async page => {
    await page.until(`[...document.querySelectorAll('.add-machine-choice')][2]?.disabled === false`, "the server choice");
    await page.evaluate(`[...document.querySelectorAll('.add-machine-choice')][2].click()`);
    await page.until(`document.querySelector('.add-machine-dialog input[readonly]')?.value === 'altanode join https://fleet.example --token JOIN-1234'`, "the command");
    // The planted node comes first, alone: nothing is approved by itself, it waits with its words like the machine after it.
    await page.until(`document.querySelectorAll('.add-machine-joining li').length === 2`, "both pending nodes, each with its words");
    assert.deepEqual(await page.evaluate(`[...document.querySelectorAll('.add-machine-joining li')].map(item => [...item.querySelectorAll('.bp6-tag')].map(tag => tag.textContent).join(' '))`),
      ["acid acorn actor agent alarm album", "maple orbit tiger cloud amber stone"]);
    assert.deepEqual(await calls(page), [{ token: "home" }], "no node is approved without the person");
    await page.shot("add-machine-server-joining");

    // The person approves the one that shows the words altanode join printed on the machine.
    await page.evaluate(`[...document.querySelectorAll('.add-machine-joining li')][1].querySelector('.bp6-button').click()`);
    await page.until(`document.querySelector('.add-machine-dialog [role=status]')?.textContent.includes('build-box joined Home and is approved.')`, "the approval");
    assert.deepEqual(await page.evaluate(`[...document.querySelectorAll('.add-machine-words .bp6-tag')].map(tag => tag.textContent)`),
      ["maple", "orbit", "tiger", "cloud", "amber", "stone"]);
    await page.shot("add-machine-server-approved");
    assert.deepEqual(await calls(page), [{ token: "home" }, { approve: "box", kind: "node", words: "maple orbit tiger cloud amber stone" }],
      "the machine of the confirmed words only, as a node: the host signs the key of those words");
    assert.deepEqual(page.problems, []);
  }, options);
});
