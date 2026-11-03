import assert from "node:assert/strict";
import test from "node:test";
import { edge, withCanvas } from "../statistics/browserHarness";

const options = { entry: "../fleet/createServer.mount.tsx", fixture: "serverFixture", rpcFixture: "../demo-api.ts", styles: ["../fleet/fleet.css", "../machines/machines.css"],
  width: 1100, height: 900, reducedMotion: true } as const;
const type = (selector: string, value: string) => `(() => { const input = document.querySelector(${JSON.stringify(selector)});
  Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set.call(input, ${JSON.stringify(value)});
  input.dispatchEvent(new Event('input', { bubbles: true })); return true; })()`;
const calls = async (page: { evaluate: (script: string) => Promise<unknown> }) => (await page.evaluate(`[...window.serverFixture.calls]`) as string[]).map(call => JSON.parse(call));

test("Create my server leads with a machine over SSH, says what blocks it from here, then adds the managed machines", { skip: !edge, timeout: 90_000 }, async () => {
  await withCanvas(async page => {
    await page.until(`document.querySelectorAll('.create-server-dialog .add-machine-choice').length === 3`, "the three places");
    assert.deepEqual(await page.evaluate(`[...document.querySelectorAll('.create-server-dialog .add-machine-choice strong')].map(item => item.textContent)`),
      ["On a machine over SSHRecommended", "On this computer", "It runs already"]);
    await page.evaluate(`document.querySelector('.create-server-dialog .add-machine-choice').click()`);
    await page.until(`document.querySelector('.create-server-dialog input[aria-label=Computer]')`, "the form");
    await page.evaluate(type(".create-server-dialog input[aria-label=Computer]", "me@box"));
    await page.evaluate(type(".create-server-dialog input[aria-label='Your name']", "Alex"));
    assert.equal(await page.evaluate(`document.querySelector('.create-server-reach code').textContent`), "http://box:2586", "the address the machines will use");
    await page.clickText(".create-server-dialog .bp6-button", "Create my server");

    await page.until(`document.querySelector('.create-server-dialog .add-machine-failure')`, "the reach fails");
    assert.match(await page.evaluate(`document.querySelector('.add-machine-failure').textContent`) as string, /firewall of box blocks port 2586\. On box, run sudo ufw allow 2586\/tcp/u);
    await page.shot("create-server-blocked");
    await page.clickText(".create-server-dialog .bp6-button", "Try again");
    await page.until(`document.querySelector('.create-server-dialog [role=status]')?.textContent === 'Your server runs on box, and you are its owner.'`, "the server is claimed");
    await page.clickText(".create-server-machines .bp6-button", "Add");
    await page.until(`document.querySelector('.create-server-joined')?.textContent.startsWith('Joined')`, "the machine joined");
    await page.shot("create-server-done");
    assert.deepEqual((await calls(page)).filter(call => call.ssh || call.join), [{ ssh: "me@box", address: null, port: 2586 }, { ssh: "me@box", address: null, port: 2586 },
      { join: "mac", link: "link-1" }]);
    // The owner's recovery key came with the server: Done gives its words to the page, which shows them once.
    assert.equal(await page.evaluate(`document.querySelector('.create-server-dialog .create-server-recovery-note')?.textContent`), "Done shows your recovery key next, once.");
    await page.clickText(".create-server-dialog .bp6-button", "Done");
    const closed = (await calls(page)).find(call => "close" in call);
    assert.equal(closed?.close, true);
    assert.equal((closed?.recoveryWords as string).split(" ").length, 32, "the 32 words of the recovery key");
    assert.deepEqual(page.problems, []);
  }, options);
});

test("Join an organization goes from the server to signing in, the space and the approval", { skip: !edge, timeout: 90_000 }, async () => {
  await withCanvas(async page => {
    await page.evaluate(`window.serverFixture.show('join')`);
    await page.until(`document.querySelector('.join-organization-dialog input')`, "the address");
    await page.evaluate(type(".join-organization-dialog input", "fleet.contoso.com"));
    await page.clickText(".join-organization-dialog .bp6-button", "Next");
    await page.until(`document.querySelector('.join-organization-dialog input[aria-label="Enrollment code"]')`, "the sign-in");
    await page.evaluate(type(".join-organization-dialog input", "CODE-1"));
    await page.clickText(".join-organization-dialog .bp6-button", "Sign in");
    await page.until(`document.querySelector('.join-organization-spaces')`, "the space");
    await page.clickText(".join-organization-dialog .bp6-button", "Link the space");
    await page.until(`document.querySelector('.join-organization-dialog [role=status]')?.textContent.includes('An administrator of Contoso approves this computer.')`, "the approval");
    assert.deepEqual(await page.evaluate(`[...document.querySelectorAll('.join-organization-dialog .add-machine-words .bp6-tag')].map(tag => tag.textContent)`),
      ["cabin", "onion", "leopard", "pigeon", "peach", "needle"]);
    await page.shot("join-organization-approval");
    assert.deepEqual((await calls(page)).filter(call => call.joinOrg || call.space || call.link), [{ joinOrg: "https://fleet.contoso.com", token: "CODE-1" }, { space: "Work" },
      { link: "work-1", server: "corp" }]);
  }, options);
});

test("A device that waits comes as a toast with its words, and the Fleet button counts it until it is approved", { skip: !edge, timeout: 90_000 }, async () => {
  await withCanvas(async page => {
    await page.until(`document.querySelector('.fleet-approvals-button')?.getAttribute('aria-label') === 'Fleet: 1 device waits for your approval'`, "the mark");
    await page.until(`document.querySelector('.fleet-approval-toast')`, "the toast");
    assert.equal(await page.evaluate(`document.querySelector('.fleet-approval-toast strong').textContent`), "build-box asks to join Home as a machine.");
    await page.clickText(".fleet-approval-toast .bp6-button", "Approve");
    await page.until(`document.querySelector('.fleet-approvals-button')?.getAttribute('aria-label') === 'Fleet'`, "nothing waits");
    assert.deepEqual((await calls(page)).filter(call => call.approve), [{ approve: "box", words: "ruby scarf apple ember camera finch", kind: "node" }],
      "the approval names the words and the class the toast showed: the host signs the key of those words only, as that class");
  }, options);
});
