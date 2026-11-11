import assert from "node:assert/strict";
import test from "node:test";
import { edge, withCanvas } from "../statistics/browserHarness";
import { testLimit } from "../testTime";

const options = { entry: "../fleet/createServer.mount.tsx", fixture: "serverFixture", rpcFixture: "../demo-api.ts", styles: ["../fleet/fleet.css", "../machines/machines.css"],
  width: 1100, height: 900, reducedMotion: true } as const;
const type = (selector: string, value: string) => `(() => { const input = document.querySelector(${JSON.stringify(selector)});
  Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set.call(input, ${JSON.stringify(value)});
  input.dispatchEvent(new Event('input', { bubbles: true })); return true; })()`;
const calls = async (page: { evaluate: (script: string) => Promise<unknown> }) => (await page.evaluate(`[...window.serverFixture.calls]`) as string[]).map(call => JSON.parse(call));

test("Create my server leads with a machine over SSH, says what blocks it from here, then adds the managed machines", { skip: !edge, timeout: testLimit(90_000) }, async () => {
  await withCanvas(async page => {
    await page.until(`document.querySelectorAll('.create-server-dialog .add-machine-choice').length === 3`, "the three places");
    assert.deepEqual(await page.evaluate(`[...document.querySelectorAll('.create-server-dialog .add-machine-choice strong')].map(item => item.textContent)`),
      ["On a machine over SSHRecommended", "On this computer", "It runs already"]);
    await page.evaluate(`document.querySelector('.create-server-dialog .add-machine-choice').click()`);
    await page.until(`document.querySelector('.create-server-dialog input[aria-label=Computer]')`, "the form");
    await page.evaluate(type(".create-server-dialog input[aria-label=Computer]", "me@box"));
    // The owner's name starts as the full name of the person signed in to this computer.
    assert.equal(await page.evaluate(`document.querySelector(".create-server-dialog input[aria-label='Your name']").value`), "Alex Example");
    await page.evaluate(type(".create-server-dialog input[aria-label='Your name']", "Alex"));
    // An address in front of the server is the default; on the local network, the server's own certificate comes first, and
    // plain HTTP is chosen knowingly, each with what it means.
    assert.equal(await page.evaluate(`document.querySelector('.create-server-reach input:checked').value`), "address");
    assert.deepEqual(await page.evaluate(`[...document.querySelectorAll('.create-server-reach code')].map(code => code.textContent)`), ["https://box:2586", "http://box:2586"],
      "the addresses the machines will use");
    await page.evaluate(`document.querySelector('.create-server-reach input[value=encrypted]').click()`);
    await page.until(`document.querySelector('.create-server-reach-note')?.textContent.includes('every device checks it at each connection')`, "what the certificate of its own means");
    await page.shot("create-server-encrypted");
    await page.evaluate(`document.querySelector('.create-server-reach input[value=network]').click()`);
    await page.until(`document.querySelector('.create-server-reach-note')?.textContent.startsWith('Anyone on the network can read')`, "what plain HTTP means");
    await page.clickText(".create-server-dialog .bp6-button", "Create my server");

    await page.until(`document.querySelector('.create-server-dialog .add-machine-failure')`, "the reach fails");
    assert.match(await page.evaluate(`document.querySelector('.add-machine-failure').textContent`) as string, /firewall of box blocks port 2586\. On box, run sudo ufw allow 2586\/tcp/u);
    await page.shot("create-server-blocked");
    await page.clickText(".create-server-dialog .bp6-button", "Start over");
    await page.until(`document.querySelector('.create-server-dialog [role=status]')?.textContent === 'Your server runs on box, and you are its owner.'`, "the server is claimed");
    // The owner's own sessions show on the server once a space is linked: offered right away, all spaces in one click.
    await page.until(`document.querySelectorAll('.create-server-dialog .fleet-server-spaces input[type=checkbox]').length === 2`, "the spaces of the person");
    await page.clickText(".create-server-dialog .fleet-server-spaces .bp6-button", "Link all my spaces");
    await page.until(`[...document.querySelectorAll('.create-server-dialog .fleet-server-spaces input')].every(input => input.checked)`, "every space linked");
    await page.clickText(".create-server-machines .bp6-button", "Add");
    await page.until(`document.querySelector('.create-server-joined')?.textContent.startsWith('Joined')`, "the machine joined");
    await page.shot("create-server-done");
    assert.deepEqual((await calls(page)).filter(call => call.ssh || call.join || call.link), [{ ssh: "me@box", address: null, port: 2586, ownCertificate: false },
      { ssh: "me@box", address: null, port: 2586, ownCertificate: false },
      { link: "default", server: "link-1" }, { link: "home", server: "link-1" }, { join: "mac", link: "link-1" }]);
    // The owner's recovery key came with the server: Done gives its words to the page, which shows them once.
    assert.equal(await page.evaluate(`document.querySelector('.create-server-dialog .create-server-recovery-note')?.textContent`), "Done shows your recovery key next, once.");
    await page.clickText(".create-server-dialog .bp6-button", "Done");
    const closed = (await calls(page)).find(call => "close" in call);
    assert.equal(closed?.close, true);
    assert.equal((closed?.recoveryWords as string).split(" ").length, 32, "the 32 words of the recovery key");
    assert.deepEqual(page.problems, []);
  }, options);
});

test("Create my server behind a name of a tailnet offers to publish it with Tailscale Serve, with the password sudo asks for it", { skip: !edge, timeout: testLimit(90_000) }, async () => {
  await withCanvas(async page => {
    await page.until(`document.querySelectorAll('.create-server-dialog .add-machine-choice').length === 3`, "the three places");
    await page.evaluate(`document.querySelector('.create-server-dialog .add-machine-choice').click()`);
    await page.until(`document.querySelector('.create-server-dialog input[aria-label=Computer]')`, "the form");
    await page.evaluate(type(".create-server-dialog input[aria-label=Computer]", "me@box"));
    await page.evaluate(type(".create-server-dialog input[aria-label='Address of the server']", "box.tail1234.ts.net:2586"));
    await page.evaluate(type(".create-server-dialog input[aria-label='Your name']", "Alex"));
    await page.clickText(".create-server-dialog .bp6-button", "Create my server");

    // Nothing is published at that name yet: the command is shown, and CodeAlta offers to run it there, with the password sudo asks.
    await page.until(`document.querySelector('.create-server-dialog .add-machine-failure input[aria-label="Password on the machine"]')`, "the reach fails, and the password is asked");
    assert.match(await page.evaluate(`document.querySelector('.add-machine-failure').textContent`) as string, /Tailscale Serve does not publish its address on box yet/u);
    assert.equal(await page.evaluate(`document.querySelector('.add-machine-failure .add-machine-detail').textContent`), "sudo tailscale serve --bg --https=2586 http://127.0.0.1:2586");
    assert.deepEqual(await page.evaluate(`[...document.querySelectorAll('.create-server-dialog .bp6-dialog-footer .bp6-button')].map(button =>
      [button.textContent, button.classList.contains('bp6-intent-primary'), button.disabled])`),
      [["Back", false, false], ["Start over", false, false], ["Publish it with Tailscale Serve", true, true]], "the person who ran the command starts over without a password");
    assert.match(await page.evaluate(`document.querySelector('.add-machine-failure').textContent`) as string, /give your password on the machine for CodeAlta to publish it with sudo \(it is not kept\)/u);
    await page.evaluate(type(".create-server-dialog input[aria-label='Password on the machine']", "s3cret"));
    await page.until(`![...document.querySelectorAll('.create-server-dialog .bp6-dialog-footer .bp6-button')].find(button => button.textContent === 'Publish it with Tailscale Serve').disabled`, "the password given");
    await page.shot("create-server-tailscale-serve");
    await page.clickText(".create-server-dialog .bp6-button", "Publish it with Tailscale Serve");
    await page.until(`document.querySelector('.create-server-dialog [role=status]')?.textContent === 'Your server runs on box, and you are its owner.'`, "the server is claimed");
    const created = { ssh: "me@box", address: "https://box.tail1234.ts.net:2586", port: 2586, ownCertificate: false };
    assert.deepEqual((await calls(page)).filter(call => call.ssh), [created, { ...created, tailscaleServe: true, sudo: true }], "the password goes with the try that publishes: one try");
    assert.deepEqual(page.problems, []);
  }, options);
});

test("Create my server offers a private Dev Tunnel without requiring its address in advance", { skip: !edge, timeout: testLimit(90_000) }, async () => {
  await withCanvas(async page => {
    await page.until(`document.querySelector('.create-server-dialog .add-machine-choice')`, "the places");
    await page.evaluate(`document.querySelector('.create-server-dialog .add-machine-choice').click()`);
    await page.until(`document.querySelector('.create-server-dialog input[aria-label=Computer]')`, "the form");
    await page.evaluate(type(".create-server-dialog input[aria-label=Computer]", "me@box"));
    await page.evaluate(`document.querySelector('.create-server-reach input[value=devtunnel]').click()`);
    assert.equal(await page.evaluate(`!!document.querySelector('.create-server-dialog input[aria-label="Address of the server"]')`), false);
    await page.until(`document.querySelector('.create-server-reach-note')?.textContent.includes('device code')`, "private tunnel setup explained");
    await page.clickText(".create-server-dialog .bp6-button", "Create my server");
    await page.until(`document.querySelector('.create-server-dialog [role=status]')?.textContent.includes('you are its owner')`, "the tunnel's address was discovered");
    assert.deepEqual((await calls(page)).filter(call => call.ssh), [{ ssh: "me@box", address: null, port: 2586, ownCertificate: false, devTunnel: true }]);
    assert.deepEqual(page.problems, []);
  }, options);
});

test("Connect to my server speaks to the owner, links every space of the person, and is approved by another computer of theirs", { skip: !edge, timeout: testLimit(90_000) }, async () => {
  await withCanvas(async page => {
    await page.evaluate(`window.serverFixture.show('joinOwn')`);
    await page.until(`document.querySelector('.join-organization-dialog.join-organization-own input')`, "the address");
    assert.equal(await page.evaluate(`document.querySelector('.join-organization-dialog .bp6-dialog-header').textContent`), "Connect to my server");
    assert.doesNotMatch(await page.evaluate(`document.querySelector('.join-organization-dialog').textContent`) as string, /administrator/u, "at home, the person is their own administrator");
    await page.evaluate(type(".join-organization-dialog input", "http://box:2586"));
    await page.clickText(".join-organization-dialog .bp6-button", "Next");
    await page.until(`document.querySelector('.join-organization-dialog input[aria-label="Enrollment code"]')`, "the code");
    assert.match(await page.evaluate(`document.querySelector('.join-organization-dialog p').textContent`) as string, /Invite a computer/u);
    await page.evaluate(type(".join-organization-dialog input", "CODE-2"));
    await page.clickText(".join-organization-dialog .bp6-button", "Sign in");
    await page.until(`document.querySelectorAll('.join-organization-spaces input[type=checkbox]').length === 2`, "the spaces of the person");
    assert.deepEqual(await page.evaluate(`[...document.querySelectorAll('.join-organization-spaces input')].map(input => input.checked)`), [true, true], "all of them at first");
    await page.shot("join-own-server-spaces");
    await page.clickText(".join-organization-dialog .bp6-button", "Link the spaces");
    await page.until(`document.querySelector('.join-organization-dialog [role=status]')?.textContent.includes('Another of your computers approves this one.')`, "the approval");
    assert.deepEqual((await calls(page)).filter(call => call.link || call.space), [{ link: "default", server: "corp" }, { link: "home", server: "corp" }], "no new Work space");
    assert.deepEqual(page.problems, []);
  }, options);
});

test("Join an organization goes from the server to signing in, the space and the approval", { skip: !edge, timeout: testLimit(90_000) }, async () => {
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
    // Once approved, the dialog says so: the person does not wait on, nor look for it elsewhere.
    await page.evaluate(`window.serverFixture.approveJoin()`);
    await page.until(`document.querySelector('.join-organization-dialog [role=status]')?.textContent.includes('This computer is approved: it is one of yours on Contoso.')`, "the approval to be told");
    assert.deepEqual((await calls(page)).filter(call => call.joinOrg || call.space || call.link), [{ joinOrg: "https://fleet.contoso.com", token: "CODE-1" }, { space: "Work" },
      { link: "work-1", server: "corp" }]);
  }, options);
});

test("Join an organization signs in through the browser with the server's methods, and an enrollment code stays at hand", { skip: !edge, timeout: testLimit(90_000) }, async () => {
  await withCanvas(async page => {
    await page.evaluate(`window.serverFixture.show('join')`);
    await page.until(`document.querySelector('.join-organization-dialog input')`, "the address");
    await page.evaluate(type(".join-organization-dialog input", "sso.contoso.com"));
    await page.clickText(".join-organization-dialog .bp6-button", "Next");
    await page.until(`document.querySelectorAll('.join-organization-methods .bp6-button').length === 2`, "the methods of the server");
    assert.deepEqual(await page.evaluate(`[...document.querySelectorAll('.join-organization-methods .bp6-button')].map(button => button.textContent)`),
      ["Continue with Contoso", "Continue with Passkey"], "the company's sign-in first");
    await page.shot("join-organization-sign-in");

    // The code stays one click away, and back.
    await page.clickText(".join-organization-dialog .bp6-button", "Use an enrollment code instead");
    await page.until(`document.querySelector('.join-organization-dialog input[aria-label="Enrollment code"]')`, "the code");
    await page.clickText(".join-organization-dialog .bp6-button", "Sign in with an account instead");
    await page.until(`document.querySelector('.join-organization-methods [data-method=company]')`, "the methods again");

    await page.clickText(".join-organization-methods .bp6-button", "Continue with Contoso");
    await page.until(`document.querySelector('.join-organization-spaces')`, "the space");
    await page.clickText(".join-organization-dialog .bp6-button", "Link the space");
    await page.until(`document.querySelector('.join-organization-dialog [role=status]')?.textContent.includes('An administrator of Contoso approves this computer.')`,
      "a person's first computer waits for the owner");
    assert.deepEqual((await calls(page)).filter(call => call.joinSignIn || call.joinOrg), [{ joinSignIn: "https://sso.contoso.com", method: "company", handle: null }]);
    assert.deepEqual(page.problems, []);
  }, options);
});

test("Join an organization shows the mark of each brand, and a setup code typed as an enrollment code leads to It runs already", { skip: !edge, timeout: testLimit(90_000) }, async () => {
  await withCanvas(async page => {
    await page.evaluate(`window.serverFixture.show('join')`);
    await page.until(`document.querySelector('.join-organization-dialog input')`, "the address");
    await page.evaluate(type(".join-organization-dialog input", "everything.contoso.com"));
    await page.clickText(".join-organization-dialog .bp6-button", "Next");
    await page.until(`document.querySelectorAll('.join-organization-methods .bp6-button').length === 5`, "the five methods");
    // Each method carries its mark, 16 px, in front of its label; only the branded ones are never on the primary blue.
    assert.deepEqual(await page.evaluate(`[...document.querySelectorAll('.join-organization-methods .bp6-button')].map(button => {
      const icon = button.querySelector('svg'); const box = icon.getBoundingClientRect(); const text = button.querySelector('.bp6-button-text').getBoundingClientRect();
      return [button.dataset.method, icon.dataset.brand ?? 'app', Math.round(box.width), Math.round(box.height), box.right <= text.left, button.classList.contains('bp6-intent-primary')]; })`),
    [["company", "app", 16, 16, true, true], ["passkey", "app", 16, 16, true, false], ["microsoft", "microsoft", 16, 16, true, false], ["google", "google", 16, 16, true, false],
      ["github", "github", 16, 16, true, false]]);
    await page.shot("join-organization-sign-in-marks");

    // A setup code: the dialog says so, and its button opens It runs already with the address.
    await page.clickText(".join-organization-dialog .bp6-button", "Back");
    await page.evaluate(type(".join-organization-dialog input", "box.local:2586"));
    await page.clickText(".join-organization-dialog .bp6-button", "Next");
    await page.until(`document.querySelector('.join-organization-dialog input[aria-label="Enrollment code"]')`, "the code");
    await page.evaluate(type(".join-organization-dialog input[aria-label='Enrollment code']", "SETUP-CODE"));
    await page.clickText(".join-organization-dialog .bp6-button", "Sign in");
    await page.until(`document.querySelector('.join-organization-dialog [role=alert]')`, "the answer");
    assert.match(await page.evaluate(`document.querySelector('.join-organization-dialog [role=alert]').textContent`) as string,
      /^This server has no owner yet: its code is a setup code\. Use Create my server… → It runs already\./u);
    assert.equal(await page.evaluate(`document.querySelectorAll('.join-organization-dialog [role=alert] .muted-text').length`), 0, "the server's own sentence says the same: not repeated");
    await page.shot("join-organization-setup-code");
    await page.clickText(".join-organization-dialog [role=alert] .bp6-button", "Set it up…");
    assert.deepEqual((await calls(page)).filter(call => call.setUp), [{ setUp: "https://box.local:2586" }]);
    assert.deepEqual(page.problems, []);
  }, options);
});

test("Join an organization with GitHub shows the code to enter at GitHub, opens GitHub's page, and Cancel stops the wait", { skip: !edge, timeout: testLimit(90_000) }, async () => {
  await withCanvas(async page => {
    await page.evaluate(`window.serverFixture.show('join')`);
    await page.until(`document.querySelector('.join-organization-dialog input')`, "the address");
    await page.evaluate(type(".join-organization-dialog input", "github.contoso.com"));
    await page.clickText(".join-organization-dialog .bp6-button", "Next");
    await page.until(`document.querySelector('.join-organization-methods [data-method=github]')`, "GitHub offered");

    // The code comes first; Cancel stops the sign-in that waits for it.
    await page.clickText(".join-organization-methods .bp6-button", "Continue with GitHub");
    await page.until(`document.querySelector('.fleet-sign-in-code code')?.textContent === 'WDJB-MJHT'`, "the code to enter");
    await page.clickText(".join-organization-dialog .bp6-dialog-footer .bp6-button", "Cancel");
    await page.until(`document.querySelector('.join-organization-dialog [role=alert]')?.textContent === 'The sign-in was cancelled.'`, "cancelled");
    await page.until(`window.serverFixture.calls.some(call => call.includes('joinCancelled'))`, "the wait stopped");
    assert.equal(await page.evaluate(`!!document.querySelector('.fleet-sign-in-code')`), false);

    // Again: the button opens GitHub's page, the person enters the code there, and the join goes on.
    await page.clickText(".join-organization-methods .bp6-button", "Continue with GitHub");
    await page.until(`document.querySelector('.fleet-sign-in-code code')?.textContent === 'WDJB-MJHT'`, "the code again");
    await page.shot("join-organization-github-code");
    await page.clickText(".fleet-sign-in-code .bp6-button", "Copy the code and open GitHub");
    await page.until(`document.querySelector('.join-organization-spaces')`, "the space");
    await page.clickText(".join-organization-dialog .bp6-button", "Link the space");
    await page.until(`document.querySelector('.join-organization-dialog [role=status]')?.textContent.includes('Another of your computers approves this one.')`,
      "a person who linked GitHub has a computer that approves this one");
    const handle = "0123456789abcdef0123456789abcdef";
    assert.deepEqual((await calls(page)).filter(call => call.startCode || call.joinSignIn || call.openPage || call.joinCancelled), [
      { startCode: "https://github.contoso.com", method: "github" }, { joinSignIn: "https://github.contoso.com", method: "github", handle }, { joinCancelled: "github" },
      { startCode: "https://github.contoso.com", method: "github" }, { joinSignIn: "https://github.contoso.com", method: "github", handle }, { openPage: handle }]);
    assert.deepEqual(page.problems, []);
  }, options);
});

test("A device that waits comes as a toast with its words, and the Fleet button counts it until it is approved", { skip: !edge, timeout: testLimit(90_000) }, async () => {
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

test("A computer that joined with an enrollment code is approved once the person signed in again with a way they have", { skip: !edge, timeout: testLimit(90_000) }, async () => {
  await withCanvas(async page => {
    await page.until(`document.querySelector('.fleet-approval-toast')`, "the toast of the machine");
    await page.evaluate(`window.serverFixture.addLaptop()`);
    await page.until(`[...document.querySelectorAll('.fleet-approval-toast strong')].some(item => item.textContent === 'Laptop asks to join Home.')`, "the toast of the laptop");
    await page.evaluate(`[...document.querySelectorAll('.fleet-approval-toast')].find(item => item.textContent.includes('Laptop asks')).querySelector('.bp6-button').click()`);
    // The server asked for a sign-in again: the toast offers the ways the person has, and the laptop still waits.
    await page.until(`document.querySelector('.fleet-approval-confirm')?.textContent.includes('Approving Laptop needs you to sign in again with a way you have.')`, "the sign-in again");
    assert.equal(await page.evaluate(`document.querySelector('.fleet-approvals-button').getAttribute('aria-label')`), "Fleet: 2 devices wait for your approval");
    await page.until(`(() => { const toast = document.querySelector('.fleet-approval-confirm')?.closest('.bp6-toast');
      return !!toast && !toast.classList.contains('bp6-toast-enter-active') && [...toast.querySelectorAll('.bp6-button')].some(button => button.textContent === 'Continue with Passkey'); })()`,
      "the ways to sign in again");
    await page.clickText(".fleet-approval-confirm .bp6-button", "Continue with Passkey");
    await page.until(`!document.querySelector('.fleet-approval-confirm')`, "the approval");
    assert.deepEqual((await calls(page)).filter(call => call.approve === "laptop"), [
      { approve: "laptop", words: "acid acorn actor agent alarm album", kind: "controller" },
      { approve: "laptop", words: "acid acorn actor agent alarm album", kind: "controller", confirmWith: "passkey" }], "approved again with the way chosen");
  }, options);
});
