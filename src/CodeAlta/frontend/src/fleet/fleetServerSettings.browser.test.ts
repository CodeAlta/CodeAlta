import assert from "node:assert/strict";
import test from "node:test";
import { edge, withCanvas } from "../statistics/browserHarness";

const options = { entry: "../fleet/fleetServerSettings.mount.tsx", fixture: "fleetFixture", rpcFixture: "../demo-api.ts", styles: ["../fleet/fleet.css"], width: 1100,
  height: 800, reducedMotion: true } as const;
// Chooses an option of a native select as the user does: React reads the change event.
const choose = (selector: string, value: string) => `(() => { const select = document.querySelector(${JSON.stringify(selector)});
  Object.getOwnPropertyDescriptor(HTMLSelectElement.prototype, 'value').set.call(select, ${JSON.stringify(value)});
  select.dispatchEvent(new Event('change', { bubbles: true })); return true; })()`;

// What the page asked the host, in order.
const calls = async (page: { evaluate: (script: string) => Promise<unknown> }) => (await page.evaluate(`[...window.fleetFixture.calls]`) as string[]).map(call => JSON.parse(call));

// Types in an input as the user does: React reads the input event.
const type = (selector: string, value: string) => `(() => { const input = document.querySelector(${JSON.stringify(selector)});
  Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set.call(input, ${JSON.stringify(value)});
  input.dispatchEvent(new Event('input', { bubbles: true })); return true; })()`;

test("Invite a computer… shows the address and the code alone, each with Copy; Add a machine… shows the command of a node", { skip: !edge, timeout: 90_000 }, async () => {
  await withCanvas(async page => {
    await page.until(`document.querySelector('.fleet-server-card')`, "the server");
    await page.clickText(".fleet-server-card .bp6-button", "Invite a computer…");
    await page.until(`document.querySelector('.fleet-token-dialog.fleet-token-computer .fleet-token-code input')?.value === 'JOIN-5678'`, "the code");
    assert.deepEqual(await page.evaluate(`[...document.querySelectorAll('.fleet-token-dialog input')].map(input => input.value)`), ["https://fleet.example", "JOIN-5678"],
      "the code alone, not inside a command");
    assert.equal(await page.evaluate(`document.querySelector('.fleet-token-dialog .bp6-dialog-header').textContent`), "Invite a computer");
    await page.shot("fleet-invite-computer");
    await page.clickText(".fleet-token-dialog .bp6-button", "Done");
    await page.until(`!document.querySelector('.fleet-token-dialog')`, "closed");

    await page.clickText(".fleet-server-card .bp6-button", "Add a machine…");
    await page.until(`document.querySelector('.fleet-token-dialog.fleet-token-machine input')?.value === 'altanode join https://fleet.example --token JOIN-5678'`, "the command");
    assert.deepEqual(page.problems, []);
  }, options);
});

test("the devices say when entries of the device list come from a newer version of CodeAlta, which this one skipped", { skip: !edge, timeout: 90_000 }, async () => {
  await withCanvas(async page => {
    await page.until(`document.querySelector('.fleet-device-row')`, "the devices");
    assert.equal(await page.evaluate(`document.querySelector('.fleet-device-list-skipped')`), null, "nothing to say while none is skipped");
    // A newer version extended the list: the next read of the devices says it, and how to read them.
    await page.evaluate(`window.fleetFixture.skip(2)`);
    await page.clickText(".fleet-device-row .bp6-button", "Approve…");
    await page.clickText(".bp6-popover .bp6-button", "It shows the same words");
    await page.until(`document.querySelector('.fleet-device-list-skipped')`, "the skipped entries");
    assert.equal(await page.evaluate(`document.querySelector('.fleet-device-list-skipped').textContent`),
      "2 entries of the device list come from a newer version of CodeAlta and were skipped: update CodeAlta.");
    assert.deepEqual(page.problems, []);
  }, options);
});

test("another computer of the owner asks to change the company sign-in: this one shows the change and approves it", { skip: !edge, timeout: 90_000 }, async () => {
  await withCanvas(async page => {
    await page.until(`document.querySelector('.fleet-sign-in-summary')`, "the sign-in of the server");
    await page.evaluate(`window.fleetFixture.ask()`);
    // The row reads again now and then; Link… and back is not needed: a change of the page reads it at once.
    await page.clickText(".fleet-server-sign-in .bp6-button", "Link…");
    await page.clickText(".bp6-menu-item", "Add a passkey");
    await page.until(`document.querySelector('.fleet-sign-in-approval')`, "the change to approve");
    assert.deepEqual(await page.evaluate(`[...document.querySelectorAll('.fleet-sign-in-approval li')].map(item => item.textContent)`),
      ["Issuer: https://login.contoso.example · contoso-client", "Name shown: Contoso", "Groups claim: roles", "Email domains let in: contoso.example",
        "Roles from groups: g-admins → admin", "Sign in with: company, passkey", "Require company sign-in", "Break-glass owner: you", "A new client secret"]);
    assert.match(await page.evaluate(`document.querySelector('.fleet-sign-in-approval').textContent`) as string, /^Office desktop asks to change the company sign-in\./u);
    await page.clickText(".fleet-sign-in-approval .bp6-button", "Approve");
    await page.until(`!document.querySelector('.fleet-sign-in-approval')`, "approved");
    // The approval names the digest of the change shown: the host signs that change or nothing.
    assert.ok((await page.evaluate(`[...window.fleetFixture.calls]`) as string[]).some(call => call.includes('"approved":"approval-1","digest":"digest-of-the-change-shown"')));
    assert.deepEqual(page.problems, []);
  }, options);
});

test("the owner's computer updates the fleet at a time it chooses, follows the campaign, cancels it and sets the policy", { skip: !edge, timeout: 90_000 }, async () => {
  await withCanvas(async page => {
    // The card of the server says its version and its policy.
    await page.until(`document.querySelector('.fleet-updates-summary')`, "the updates of the server");
    assert.equal(await page.evaluate(`document.querySelector('.fleet-updates-summary').textContent`), "Server 4.0.1 · Manual");

    // Update the fleet… : the latest version, now, every machine by default, with each machine's version.
    await page.clickText(".fleet-server-updates .bp6-button", "Update the fleet…");
    await page.until(`document.querySelector('.fleet-update-dialog .fleet-update-machines')`, "the form");
    assert.deepEqual(await page.evaluate(`[...document.querySelectorAll('.fleet-update-machines label')].map(label => [label.textContent, label.querySelector('input').checked])`),
      [["Mac mini 4.0.1", true], ["Linux box 4.0.1 · Offline", true]]);
    await page.shot("fleet-update-dialog");

    // At a time, on the Mac only.
    await page.evaluate(choose(".fleet-update-dialog .fleet-telemetry-choices select", "at"));
    await page.until(`document.querySelector('.fleet-update-dialog [role=alert]')?.textContent === 'Choose a date and a time.'`, "a time is needed");
    await page.evaluate(type(".fleet-update-dialog input[type=datetime-local]", "2099-01-02T03:04"));
    await page.evaluate(`document.querySelectorAll('.fleet-update-machines input')[1].click()`);
    await page.clickText(".fleet-update-dialog .bp6-button", "Update");
    await page.until(`!document.querySelector('.fleet-update-dialog')`, "the form closes");
    const [started] = (await page.evaluate(`[...window.fleetFixture.calls]`) as string[]).map(call => JSON.parse(call));
    assert.equal(started.update.notBefore, new Date("2099-01-02T03:04").toISOString(), "the time of this computer, in UTC");
    assert.deepEqual([started.update.version, started.update.feed, started.update.machines], [null, null, ["mac"]]);
    assert.ok(started.update.timeZone, "the time zone the time was chosen in");

    // The campaign shows with each machine, and Cancel before the server's step.
    await page.until(`document.querySelector('.fleet-campaign')?.dataset.state === 'scheduled'`, "the campaign");
    assert.deepEqual(await page.evaluate(`[...document.querySelectorAll('.fleet-campaign-machines li')].map(item => item.querySelector('.bp6-tag').textContent)`),
      ["Pending", "Switching when idle"], "the server last, then the Mac");
    await page.shot("fleet-update-campaign");
    await page.clickText(".fleet-campaign .bp6-button", "Cancel");
    await page.until(`document.querySelector('.fleet-campaign')?.dataset.state === 'cancelled'`, "cancelled");
    assert.equal(await page.evaluate(`[...document.querySelectorAll('.fleet-campaign .bp6-button')].length`), 0, "nothing to cancel any more");

    // Policy… : pinned to a version.
    await page.clickText(".fleet-server-updates .bp6-button", "Policy…");
    await page.until(`document.querySelector('.fleet-policy-dialog select')`, "the policy form");
    await page.evaluate(choose(".fleet-policy-dialog select", "pinned"));
    await page.until(`document.querySelector('.fleet-policy-dialog [role=alert]')?.textContent === 'This is not a version.'`, "a version is needed");
    await page.evaluate(type(".fleet-policy-dialog input", "4.0.1"));
    await page.clickText(".fleet-policy-dialog .bp6-button", "Save");
    await page.until(`!document.querySelector('.fleet-policy-dialog')`, "the policy form closes");
    await page.until(`document.querySelector('.fleet-updates-summary')?.textContent === 'Server 4.0.1 · Pinned · 4.0.1'`, "the policy read again");
    const calls = (await page.evaluate(`[...window.fleetFixture.calls]`) as string[]).map(call => JSON.parse(call));
    assert.deepEqual(calls.slice(1), [{ cancel: "c1" }, { policy: { mode: "pinned", pinnedVersion: "4.0.1", window: null } }]);
    assert.deepEqual(page.problems, []);
  }, options);
});

test("a new device of the person is approved in a popover beside its row, once its words are the ones the device shows", { skip: !edge, timeout: 90_000 }, async () => {
  await withCanvas(async page => {
    await page.until(`document.querySelector('.fleet-device-row[data-status=pending]')`, "the new device");
    assert.equal(await page.evaluate(`document.querySelector('.fleet-device-row[data-status=pending] .fleet-device-title').textContent`), "LaptopNew device");
    assert.equal(await page.evaluate(`document.querySelectorAll('.fleet-device-row[data-status=pending] .fleet-device-words').length`), 0,
      "the words show in the confirmation, where they are compared");

    // Approve… asks first, beside the row, with the six words of the device's key.
    await page.clickText(".fleet-device-row[data-status=pending] .bp6-button", "Approve…");
    await page.until(`document.querySelector('.fleet-device-confirm')`, "the confirmation");
    assert.equal(await page.evaluate(`document.querySelector('.fleet-device-confirm p').textContent`), "Does Laptop show these words?");
    assert.deepEqual(await page.evaluate(`[...document.querySelectorAll('.fleet-device-confirm .fleet-device-words .bp6-tag')].map(tag => tag.textContent)`),
      ["otter", "river", "robin", "saddle", "salmon", "pearl"]);
    assert.deepEqual(await page.evaluate(`[...window.fleetFixture.calls]`), [], "nothing is approved before the words are confirmed");
    await page.shot("fleet-device-confirm");

    // The approval names the words compared: the host signs the key of those words only.
    await page.clickText(".fleet-device-confirm .bp6-button", "It shows the same words");
    await page.until(`!document.querySelector('.fleet-device-row[data-status=pending]')`, "approved");
    assert.deepEqual((await page.evaluate(`[...window.fleetFixture.calls]`) as string[]).map(call => JSON.parse(call)),
      [{ approve: "laptop", words: "otter river robin saddle salmon pearl", kind: "controller" }]);
    assert.deepEqual(page.problems, []);
  }, options);
});

test("the owner makes a recovery key, sees its words once with Copy, and closes them only after typing two of them back", { skip: !edge, timeout: 90_000 }, async () => {
  await withCanvas(async page => {
    await page.until(`document.querySelector('.fleet-server-recovery')`, "the recovery key of the server");
    assert.equal(await page.evaluate(`document.querySelector('.fleet-server-recovery-row .bp6-tag').textContent`), "None");
    await page.clickText(".fleet-server-recovery .bp6-button", "Make a recovery key…");

    // The 32 words, numbered, and Copy.
    await page.until(`document.querySelectorAll('.fleet-recovery-words li').length === 32`, "the words");
    const words = await page.evaluate(`[...document.querySelectorAll('.fleet-recovery-words li')].map(item => item.textContent)`) as string[];
    assert.deepEqual(words.slice(0, 3), ["acid", "acorn", "actor"]);
    await page.shot("fleet-recovery-words");
    await page.clickText(".fleet-recovery-dialog .bp6-button", "Copy");
    await page.until(`[...document.querySelectorAll('.fleet-recovery-dialog .bp6-button')].some(button => button.textContent === 'Copied')`, "copied");

    // A click outside does not close it; the check asks two words by their number.
    await page.evaluate(`document.querySelector('.bp6-overlay-backdrop')?.dispatchEvent(new MouseEvent('mousedown', { bubbles: true }))`);
    assert.ok(await page.evaluate(`!!document.querySelector('.fleet-recovery-dialog')`), "still open");
    await page.clickText(".fleet-recovery-dialog .bp6-button", "I wrote them down");
    await page.until(`document.querySelectorAll('.fleet-recovery-check input').length === 2`, "the check");
    const asked = (await page.evaluate(`[...document.querySelectorAll('.fleet-recovery-check label span')].map(span => span.textContent)`) as string[])
      .map(label => Number(label.replace("Word ", "")));
    const done = `[...document.querySelectorAll('.fleet-recovery-dialog .bp6-button')].find(button => button.textContent === 'Done')`;
    await page.evaluate(type(".fleet-recovery-check label:nth-of-type(1) input", words[asked[0] - 1]));
    await page.evaluate(type(".fleet-recovery-check label:nth-of-type(2) input", "wrong"));
    assert.equal(await page.evaluate(`${done}.disabled`), true, "a wrong word keeps it open");
    await page.evaluate(type(".fleet-recovery-check label:nth-of-type(2) input", ` ${words[asked[1] - 1].toUpperCase()} `));
    await page.until(`${done}.disabled === false`, "both words right");
    await page.evaluate(`${done}.click()`);
    await page.until(`!document.querySelector('.fleet-recovery-dialog')`, "the dialog closes");
    await page.until(`document.querySelector('.fleet-server-recovery-row .bp6-tag')?.textContent === 'Kept'`, "the server read again");
    assert.deepEqual((await page.evaluate(`[...window.fleetFixture.calls]`) as string[]).map(call => JSON.parse(call)),
      [{ recoveryKey: "made" }, { copied: words.join(" ") }]);

    // A new one is confirmed beside the row first.
    await page.clickText(".fleet-server-recovery .bp6-button", "New recovery key…");
    await page.until(`document.querySelector('.fleet-server-confirm .bp6-intent-danger')`, "the confirmation");
    assert.equal(await page.evaluate(`document.querySelector('.fleet-server-confirm p').textContent`), "Make a new recovery key?");

    // A person with ways to sign in signs in again with one of them first: the key of this computer alone makes none.
    await page.evaluate(`window.fleetFixture.logins()`);
    await page.clickText(".fleet-server-confirm .bp6-button", "Make a new one");
    await page.until(`document.querySelector('.fleet-server-recovery .fleet-sign-in-confirm')?.textContent.includes('Making a recovery key needs you to sign in again with a way you have.')`,
      "the sign-in again");
    await page.until(`[...document.querySelectorAll('.fleet-server-recovery .fleet-sign-in-confirm-methods .bp6-button')].some(button => button.textContent === 'Continue with Microsoft')`,
      "the ways the person has");
    await page.clickText(".fleet-server-recovery .fleet-sign-in-confirm-methods .bp6-button", "Continue with Microsoft");
    await page.until(`document.querySelector('.fleet-recovery-dialog')`, "the words of the new one");
    assert.deepEqual((await page.evaluate(`[...window.fleetFixture.calls]`) as string[]).map(call => JSON.parse(call)).filter(call => call.recoveryKey),
      [{ recoveryKey: "made" }, { recoveryKey: "made", confirmWith: "microsoft" }]);
    assert.deepEqual(page.problems, []);
  }, options);
});

// Types in a text area as the user does.
const typeArea = (selector: string, value: string) => `(() => { const area = document.querySelector(${JSON.stringify(selector)});
  Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype, 'value').set.call(area, ${JSON.stringify(value)});
  area.dispatchEvent(new Event('input', { bubbles: true })); return true; })()`;

test("a computer without a server recovers with the 32 words of the recovery key from the empty page", { skip: !edge, timeout: 90_000 }, async () => {
  await withCanvas(async page => {
    // Leaving the only server empties the page, where Recover with the recovery key… is offered.
    await page.until(`document.querySelector('button[aria-label="Leave https://fleet.example"]')`, "the card of the server");
    await page.evaluate(`document.querySelector('button[aria-label="Leave https://fleet.example"]').click()`);
    await page.clickText(".fleet-server-confirm .bp6-button", "Leave");
    await page.until(`document.querySelector('.fleet-server-empty')`, "the empty page");
    assert.match(await page.evaluate(`document.querySelector('.fleet-server-empty').textContent`) as string, /Create a server for your machines, or join/u,
      "the empty page says what the two buttons above do");
    await page.clickText(".fleet-server-empty .bp6-button", "Recover with the recovery key…");
    await page.until(`document.querySelector('.fleet-server-dialog textarea')`, "the recovery dialog");
    assert.equal(await page.evaluate(`document.querySelector('.fleet-server-dialog .bp6-dialog-header').textContent`), "Recover with the recovery key");
    const recover = `[...document.querySelectorAll('.fleet-server-dialog .bp6-button')].find(button => button.textContent === 'Recover')`;
    const words = "acid acorn actor agent alarm album alien alpha amber anchor angel ankle apple apron arena arrow atlas attic autumn award bacon badge bagel baker "
      + "bamboo banjo barn basil basket beach beacon bear";

    // Recover waits for an address and the 32 words, counted as they are typed.
    await page.evaluate(type(".fleet-server-dialog input", "https://fleet.example/"));
    await page.evaluate(typeArea(".fleet-server-dialog textarea", words.split(" ").slice(0, 31).join(" ")));
    await page.until(`document.querySelector('.fleet-server-dialog small')?.textContent === '31 of 32 words'`, "the count");
    assert.equal(await page.evaluate(`${recover}.disabled`), true);
    await page.shot("fleet-recover-dialog");

    // A mistyped word is said by its position, without the words.
    await page.evaluate(typeArea(".fleet-server-dialog textarea", words.replace("alarm", "zzzz")));
    await page.until(`${recover}.disabled === false`, "32 words");
    await page.evaluate(`${recover}.click()`);
    await page.until(`document.querySelector('.fleet-server-dialog [role=alert]')?.textContent === 'Word 5 is not a word of a recovery key: check how it is written.'`, "the position");

    // Words that are not a recovery key of the server are refused, and the dialog stays.
    await page.evaluate(typeArea(".fleet-server-dialog textarea", words.replace("bear", "bird")));
    await page.until(`${recover}.disabled === false`, "32 words");
    await page.evaluate(`${recover}.click()`);
    await page.until(`document.querySelector('.fleet-server-dialog [role=alert]')?.textContent === 'These words are not a recovery key of this server.'`, "refused");

    // The right ones, spaced and on lines as they were written down, link this computer again.
    await page.evaluate(typeArea(".fleet-server-dialog textarea", ` ${words.split(" ").slice(0, 16).join(" ")}\n${words.split(" ").slice(16).join("  ")} `));
    await page.evaluate(`${recover}.click()`);
    await page.until(`!document.querySelector('.fleet-server-dialog')`, "the dialog closes");
    await page.until(`document.querySelector('.fleet-server-card')`, "the server again");
    const calls = (await page.evaluate(`[...window.fleetFixture.calls]`) as string[]).map(call => JSON.parse(call)).filter(call => call.recover);
    assert.deepEqual(calls.map(call => [call.recover, call.words === words]), [["https://fleet.example", false], ["https://fleet.example", false], ["https://fleet.example", true]]);
    assert.deepEqual(page.problems, []);
  }, options);
});

test("another computer of the person asks to add a way to sign in: this one approves it; and a link asks another computer instead of a sign-in again", { skip: !edge, timeout: 90_000 }, async () => {
  await withCanvas(async page => {
    await page.until(`document.querySelector('.fleet-sign-in-summary')`, "the sign-in of the server");
    await page.evaluate(`window.fleetFixture.askLink()`);
    // The first way in links at once, and the row reads again.
    await page.clickText(".fleet-server-sign-in .bp6-button", "Link…");
    await page.clickText(".bp6-menu-item", "Add a passkey");
    await page.until(`document.querySelector('.fleet-sign-in-link-approval')`, "the link to approve");
    assert.match(await page.evaluate(`document.querySelector('.fleet-sign-in-link-approval').textContent`) as string, /^Laptop asks to add a way to sign in: Passkey\./u);
    await page.clickText(".fleet-sign-in-link-approval .bp6-button", "Approve");
    await page.until(`!document.querySelector('.fleet-sign-in-link-approval')`, "approved");
    // The approval names the digest of the link shown: the host signs that link or nothing.
    assert.ok((await calls(page)).some(call => call.approvedLink === "link-1" && call.digest === "digest-of-the-link-shown"));

    // A second way in offers another computer's approval beside a sign-in again.
    await page.clickText(".fleet-server-sign-in .bp6-button", "Link…");
    await page.until(`[...document.querySelectorAll('.bp6-menu-item')].some(item => item.textContent === 'Link Microsoft')`, "Microsoft offered");
    await page.clickText(".bp6-menu-item", "Link Microsoft");
    await page.until(`[...document.querySelectorAll('.fleet-sign-in-confirm-methods .bp6-button')].some(button => button.textContent === 'Approve on another of my computers')`,
      "the approval on another computer offered");
    await page.clickText(".fleet-sign-in-confirm-methods .bp6-button", "Approve on another of my computers");
    await page.until(`window.fleetFixture.calls.some(call => call.includes('"confirmWith":"device"'))`, "the link waits for another computer");
    assert.ok((await calls(page)).some(call => call.linkSignIn === "microsoft" && call.confirmWith === "device"));

    // Or the recovery key: its words, typed here, approve the link on this computer; words that are not the key are said so.
    await page.clickText(".fleet-server-sign-in .bp6-button", "Link…");
    await page.until(`[...document.querySelectorAll('.bp6-menu-item')].some(item => item.textContent === 'Link Microsoft')`, "Microsoft offered again");
    await page.clickText(".bp6-menu-item", "Link Microsoft");
    await page.until(`[...document.querySelectorAll('.fleet-sign-in-confirm-methods .bp6-button')].some(button => button.textContent === 'Use my recovery key')`, "the recovery key offered");
    await page.clickText(".fleet-sign-in-confirm-methods .bp6-button", "Use my recovery key");
    await page.until(`document.querySelector('.fleet-sign-in-recovery-words input')`, "the field of the words");
    const type = (words: string) => page.evaluate(`(() => { const input = document.querySelector('.fleet-sign-in-recovery-words input');
      Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set.call(input, ${JSON.stringify(words)}); input.dispatchEvent(new Event('input', { bubbles: true })); return true; })()`);
    await type("not the words");
    await page.clickText(".fleet-sign-in-recovery-words .bp6-button", "Approve with my recovery key");
    await page.until(`document.querySelector('.fleet-server-sign-in [role=alert]')?.textContent === 'These words are not a recovery key of this server.'`, "the words refused");
    assert.deepEqual(page.problems, []);
  }, options);
});

test("each linked account has its mark and a remove button asking beside the row; a company account has none, and Link… still offers an account already linked", { skip: !edge, timeout: 90_000 }, async () => {
  await withCanvas(async page => {
    await page.until(`document.querySelector('.fleet-sign-in-summary')`, "the sign-in of the server");
    await page.evaluate(`window.fleetFixture.logins()`);
    await page.clickText(".fleet-server-sign-in .bp6-button", "Link…");
    await page.clickText(".bp6-menu-item", "Add a passkey");
    await page.until(`document.querySelectorAll('.fleet-sign-in-logins .bp6-tag').length === 3`, "the ways in of the person");
    assert.deepEqual(await page.evaluate(`[...document.querySelectorAll('.fleet-sign-in-login')].map(row => [row.textContent, !!row.querySelector('svg'),
      row.querySelector('.fleet-sign-in-login-remove')?.getAttribute('aria-label') ?? null])`), [
      ["Company sign-in · alex@contoso.example", true, null],
      ["Microsoft · alex@outlook.com", true, "Remove Microsoft · alex@outlook.com"],
      ["Passkey · Passkey on Windows", true, "Remove Passkey · Passkey on Windows"]], "each with its mark; the company's account without a remove button");
    assert.equal(await page.evaluate(`document.querySelector('.fleet-sign-in-login .bp6-tag').title`),
      "Managed by your organization: an admin removes a person in People and access");

    // Link… still offers an account already linked: linking it again refreshes its name.
    await page.clickText(".fleet-server-sign-in .bp6-button", "Link…");
    await page.until(`[...document.querySelectorAll('.bp6-menu-item')].some(item => item.textContent === 'Link Microsoft')`, "Microsoft offered again");
    await page.clickText(".fleet-server-sign-in .bp6-button", "Link…");
    await page.until(`!document.querySelector('.bp6-menu-item')`, "the menu closed");

    // Remove asks beside the row; Remove does it, and the row lists what is left.
    await page.evaluate(`document.querySelector('[aria-label="Remove Microsoft · alex@outlook.com"]').click()`);
    await page.until(`document.querySelector('.confirm-form strong')?.textContent === 'Remove Microsoft from your ways to sign in?'`, "the question");
    assert.equal(await page.evaluate(`document.querySelector('.confirm-form .confirm-subject').textContent`), "alex@outlook.com");
    await page.shot("fleet-sign-in-remove");
    await page.clickText(".confirm-form .bp6-button", "Remove");
    await page.until(`document.querySelectorAll('.fleet-sign-in-logins .bp6-tag').length === 2 && !document.querySelector('.confirm-form')`, "removed");

    // The last way in of an owner without a recovery key stays: the question says why, and Cancel leaves it.
    await page.evaluate(`document.querySelector('[aria-label="Remove Passkey · Passkey on Windows"]').click()`);
    await page.until(`document.querySelector('.confirm-form')`, "the question");
    await page.clickText(".confirm-form .bp6-button", "Remove");
    await page.until(`document.querySelector('.confirm-form .confirm-error')?.textContent.startsWith('This is your last way to sign in')`, "why it stays");
    await page.clickText(".confirm-form .bp6-button", "Cancel");
    await page.until(`!document.querySelector('.confirm-form')`, "cancelled");
    assert.deepEqual((await calls(page)).filter(call => call.removeLogin), [{ removeLogin: "a_microsoft" }, { removeLogin: "p_windows" }]);
    assert.deepEqual(page.problems, []);
  }, options);
});

test("the owner links a passkey, turns company sign-in on and requires it, and the page never holds the secret", { skip: !edge, timeout: 90_000 }, async () => {
  await withCanvas(async page => {
    await page.until(`document.querySelector('.fleet-sign-in-summary')`, "the sign-in of the server");
    assert.equal(await page.evaluate(`document.querySelector('.fleet-sign-in-summary').textContent`), "Sign in with Passkey, Microsoft, GitHub",
      "Google is not registered by the project yet");
    assert.match(await page.evaluate(`document.querySelector('.fleet-sign-in-logins').textContent`) as string, /None yet\./u);

    // Link… adds a passkey through the browser: the host does it, the row lists it.
    await page.clickText(".fleet-server-sign-in .bp6-button", "Link…");
    await page.until(`[...document.querySelectorAll('.bp6-menu-item')].some(item => item.textContent === 'Add a passkey')`, "what may be linked");
    assert.deepEqual(await page.evaluate(`[...document.querySelectorAll('.bp6-menu-item')].map(item => item.textContent)`), ["Add a passkey", "Link Microsoft", "Link GitHub"]);
    await page.clickText(".bp6-menu-item", "Add a passkey");
    await page.until(`document.querySelector('.fleet-sign-in-logins .bp6-tag')?.textContent === 'Passkey · Passkey on Windows'`, "the passkey listed");

    // Link GitHub: a second way in asks the person to sign in again with the passkey first; then the code to enter at
    // GitHub shows, Copy opens GitHub's page, and Cancel stops the wait.
    await page.clickText(".fleet-server-sign-in .bp6-button", "Link…");
    await page.until(`[...document.querySelectorAll('.bp6-menu-item')].some(item => item.textContent === 'Link GitHub')`, "GitHub may be linked");
    await page.clickText(".bp6-menu-item", "Link GitHub");
    await page.until(`document.querySelector('.fleet-sign-in-confirm')?.textContent.startsWith('Adding a way to sign in needs you to sign in again with one you have, or to approve it on another of your computers.')`,
      "the sign-in again asked");
    await page.clickText(".fleet-sign-in-confirm-methods .bp6-button", "Continue with Passkey");
    await page.until(`document.querySelector('.fleet-sign-in-code code')?.textContent === 'WDJB-MJHT'`, "the code to enter");
    assert.ok((await calls(page)).some(call => call.startCode === "github" && call.confirmWith === "passkey"), "the link starts once the person signed in again");
    await page.clickText(".fleet-sign-in-code .bp6-button", "Copy the code and open GitHub");
    await page.until(`window.fleetFixture.calls.some(call => call.includes('openPage'))`, "GitHub's page opened");
    await page.clickText(".fleet-sign-in-code .bp6-button", "Cancel");
    await page.until(`document.querySelector('.fleet-server-sign-in [role=alert]')?.textContent === 'The sign-in was cancelled.'`, "cancelled");
    await page.until(`window.fleetFixture.calls.some(call => call.includes('linkCancelled'))`, "the wait stopped");

    // Change…: company sign-in with its provider, then required; the secret goes to the host once and never comes back.
    await page.clickText(".fleet-server-sign-in .bp6-button", "Change…");
    await page.until(`document.querySelector('.fleet-sign-in-dialog .fleet-sign-in-methods')`, "the form");
    assert.deepEqual(await page.evaluate(`[...document.querySelectorAll('.fleet-sign-in-methods label')].map(label => [label.textContent, label.querySelector('input').checked])`),
      [["Passkey", true], ["Microsoft", true], ["Google · Not registered yet", true], ["GitHub", true]]);
    // The redirect to register is the one company sign-in comes back to: localhost, since Entra ID's Web platform takes no 127.0.0.1.
    assert.equal(await page.evaluate(`[...document.querySelectorAll('.fleet-sign-in-dialog small')].map(small => small.textContent).find(text => text.startsWith('Redirect'))`),
      "Redirect to register at the provider: http://localhost/callback (any port; on Entra ID, a Web platform redirect).");
    await page.evaluate(`[...document.querySelectorAll('.fleet-sign-in-dialog .bp6-switch')].find(label => label.textContent === 'Company sign-in').querySelector('input').click()`);
    await page.until(`document.querySelector('.fleet-sign-in-dialog [role=alert]')?.textContent === 'Company sign-in needs its issuer and client id.'`, "the provider is needed");
    // Entra ID never confirms an email: with its issuer the dialog says its accounts are let in by tenant id; another issuer needs no such word.
    assert.equal(await page.evaluate(`document.querySelector('.fleet-sign-in-entra')`), null);
    await page.evaluate(type(".fleet-sign-in-dialog .fleet-server-form .bp6-input", "https://login.microsoftonline.com/organizations/v2.0"));
    await page.until(`document.querySelector('.fleet-sign-in-entra')`, "the warning about Entra ID");
    assert.equal(await page.evaluate(`document.querySelector('.fleet-sign-in-entra').textContent`),
      "Entra ID does not confirm emails, so an email domain would refuse all its accounts: they are let in by tenant id.");
    await page.evaluate(`(() => { const inputs = [...document.querySelectorAll('.fleet-sign-in-dialog .fleet-server-form .bp6-input')];
      const set = (input, value) => { Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set.call(input, value); input.dispatchEvent(new Event('input', { bubbles: true })); };
      set(inputs[0], 'https://login.contoso.example/v2.0'); set(inputs[1], 'contoso-client'); set(inputs[2], 'the-secret'); set(inputs[3], 'Contoso'); return true; })()`);
    assert.equal(await page.evaluate(`document.querySelector('.fleet-sign-in-entra')`), null, "no word about Entra ID for another issuer");
    await page.evaluate(`[...document.querySelectorAll('.fleet-sign-in-dialog .bp6-switch')].find(label => label.textContent === 'Require company sign-in').querySelector('input').click()`);
    // Whose accounts it lets in is named: nothing named is refused.
    await page.until(`document.querySelector('.fleet-sign-in-dialog [role=alert]')?.textContent === 'Company sign-in needs the email domains or the tenant ids it lets in.'`, "the domains are needed");
    await page.evaluate(`(() => { const input = [...document.querySelectorAll('.fleet-sign-in-dialog .fleet-server-form .bp6-input')][5];
      Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set.call(input, 'Contoso.example, fabrikam.example'); input.dispatchEvent(new Event('input', { bubbles: true })); return true; })()`);
    await page.until(`!document.querySelector('.fleet-sign-in-dialog [role=alert]')`, "the form holds");
    await page.shot("fleet-sign-in-dialog");
    // Changing the company sign-in asks the owner to sign in again, with a method linked to them.
    await page.clickText(".fleet-sign-in-dialog .bp6-button", "Save");
    await page.until(`[...document.querySelectorAll('.fleet-sign-in-confirm-methods .bp6-button')].some(button => button.textContent === 'Continue with Passkey')`, "the confirmation asked");
    await page.clickText(".fleet-sign-in-confirm-methods .bp6-button", "Continue with Passkey");
    await page.until(`!document.querySelector('.fleet-sign-in-dialog')`, "the form closes");
    const saved = (await page.evaluate(`[...window.fleetFixture.calls]`) as string[]).map(call => JSON.parse(call)).filter(call => call.signIn || call.linkSignIn);
    const change = { version: 0, enabled: ["passkey", "microsoft", "google", "github", "company"], issuer: "https://login.contoso.example/v2.0", clientId: "contoso-client",
      secret: "the-secret", name: "Contoso", requireSso: true, domains: ["contoso.example", "fabrikam.example"] };
    assert.deepEqual(saved, [{ linkSignIn: "passkey" }, { linkSignIn: "github" }, { signIn: { ...change, confirmWith: null } }, { signIn: { ...change, confirmWith: "passkey" } }]);
    await page.until(`document.querySelector('.fleet-sign-in-summary')?.textContent === 'Company sign-in required'`, "the row read again");
    await page.clickText(".fleet-server-sign-in .bp6-button", "Change…");
    await page.until(`document.querySelector('.fleet-sign-in-dialog input[type=password]')`, "the form again");
    assert.deepEqual(await page.evaluate(`[document.querySelector('.fleet-sign-in-dialog input[type=password]').value, document.querySelector('.fleet-sign-in-dialog input[type=password]').placeholder]`),
      ["", "••••"], "the secret is set, never shown");
    assert.deepEqual(page.problems, []);
  }, options);
});

test("confirming a change with an account that is not theirs names the ways in that are, not that no one linked the account", { skip: !edge, timeout: 90_000 }, async () => {
  await withCanvas(async page => {
    await page.until(`document.querySelector('.fleet-sign-in-summary')`, "the sign-in of the server");
    for (const item of ["Add a passkey", "Link Microsoft"]) {
      await page.clickText(".fleet-server-sign-in .bp6-button", "Link…");
      await page.until(`[...document.querySelectorAll('.bp6-menu-item')].some(entry => entry.textContent === ${JSON.stringify(item)})`, item);
      await page.clickText(".bp6-menu-item", item);
      // A second way in asks first to sign in again with the one the person has.
      if (item !== "Add a passkey") {
        await page.until(`[...document.querySelectorAll('.fleet-sign-in-confirm-methods .bp6-button')].some(button => button.textContent === 'Continue with Passkey')`,
          "the sign-in again asked");
        await page.clickText(".fleet-sign-in-confirm-methods .bp6-button", "Continue with Passkey");
      }
      await page.until(`!document.querySelector('.bp6-popover-open') && !document.querySelector('.fleet-server-sign-in .bp6-button:disabled')`, `${item} done`);
    }
    await page.until(`document.querySelectorAll('.fleet-sign-in-logins .bp6-tag').length === 2`, "the two ways in listed");
    await page.clickText(".fleet-server-sign-in .bp6-button", "Change…");
    await page.until(`document.querySelector('.fleet-sign-in-dialog .fleet-sign-in-methods')`, "the form");
    await page.evaluate(`[...document.querySelectorAll('.fleet-sign-in-dialog .bp6-switch')].find(label => label.textContent === 'Company sign-in').querySelector('input').click()`);
    await page.evaluate(`(() => { const inputs = [...document.querySelectorAll('.fleet-sign-in-dialog .fleet-server-form .bp6-input')];
      const set = (input, value) => { Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set.call(input, value); input.dispatchEvent(new Event('input', { bubbles: true })); };
      set(inputs[0], 'https://login.contoso.example/v2.0'); set(inputs[1], 'contoso-client'); set(inputs[5], 'contoso.example'); return true; })()`);
    await page.until(`!document.querySelector('.fleet-sign-in-dialog [role=alert]')`, "the form holds");
    await page.clickText(".fleet-sign-in-dialog .bp6-button", "Save");
    await page.until(`[...document.querySelectorAll('.fleet-sign-in-confirm-methods .bp6-button')].some(button => button.textContent === 'Continue with Microsoft')`, "the confirmation asked");
    await page.clickText(".fleet-sign-in-confirm-methods .bp6-button", "Continue with Microsoft");
    await page.until(`document.querySelector('.fleet-sign-in-dialog [role=alert]')`, "the refusal");
    assert.equal(await page.evaluate(`document.querySelector('.fleet-sign-in-dialog [role=alert]').textContent`),
      "This Microsoft account is not one of yours here: use alex@example.com or Passkey.");
    assert.deepEqual(page.problems, []);
  }, options);
});

test("the owner's computer sees the telemetry of the fleet and changes it without the page holding a secret",{ skip: !edge, timeout: 90_000 }, async () => {
  await withCanvas(async page => {
    // The card of the server says where the machines send, in one line.
    await page.until(`document.querySelector('.fleet-telemetry-summary')`, "the telemetry of the server");
    assert.equal(await page.evaluate(`document.querySelector('.fleet-telemetry-summary').textContent`),
      "Machines send through this server · traces, metrics → https://otlp.example:4318");

    // Change… opens the form with the setting; the header held by the server shows masked, its value empty.
    await page.clickText(".fleet-server-telemetry .bp6-button", "Change…");
    await page.until(`document.querySelector('.fleet-telemetry-dialog .fleet-telemetry-header')`, "the form");
    assert.deepEqual(await page.evaluate(`[...document.querySelectorAll('.fleet-telemetry-header input')].map(input => [input.value, input.placeholder, input.type])`),
      [["x-api-key", "Name", "text"], ["", "••••6789", "password"]]);
    assert.deepEqual(await page.evaluate(`[...document.querySelectorAll('.fleet-telemetry-signals input')].map(input => input.checked)`), [true, true, false]);
    await page.shot("fleet-telemetry-dialog");

    // Logs too, then Save: the header goes without a value, so that the host keeps the one it holds.
    await page.evaluate(`document.querySelectorAll('.fleet-telemetry-signals input')[2].click()`);
    await page.clickText(".fleet-telemetry-dialog .bp6-button", "Save");
    await page.until(`!document.querySelector('.fleet-telemetry-dialog')`, "the form closes");
    assert.deepEqual((await page.evaluate(`[...window.fleetFixture.calls]`) as string[]).map(call => JSON.parse(call)), [{ enabled: true,
      endpoint: "https://otlp.example:4318", route: "server", signals: ["traces", "metrics", "logs"], headers: [{ name: "x-api-key", value: null }] }]);
    await page.until(`document.querySelector('.fleet-telemetry-summary')?.textContent.includes('traces, metrics, logs')`, "the summary read again");
    await page.shot("fleet-telemetry-card");

    // A form that cannot be signed says why, and Save waits.
    await page.clickText(".fleet-server-telemetry .bp6-button", "Change…");
    await page.until(`document.querySelector('.fleet-telemetry-dialog .fleet-telemetry-choices select')`, "the form again");
    await page.evaluate(choose(".fleet-telemetry-choices select", "grpc"));
    await page.until(`document.querySelector('.fleet-telemetry-dialog [role=alert]')?.textContent === 'The server forwards OTLP over HTTP only.'`, "the reason");
    assert.equal(await page.evaluate(`[...document.querySelectorAll('.fleet-telemetry-dialog .bp6-button')].find(button => button.textContent === 'Save').disabled`), true);
    assert.deepEqual(page.problems, []);
  }, options);
});
