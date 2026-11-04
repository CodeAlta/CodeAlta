import assert from "node:assert/strict";
import test from "node:test";
import { edge, withCanvas } from "../../statistics/browserHarness";

const options = { entry: "../fleet/access/fleetAccess.mount.tsx", fixture: "fleetAccessFixture", rpcFixture: "../demo-api.ts",
  styles: ["../secrets/secrets.css", "../fleet/fleet.css", "../fleet/fleetSecrets.css", "../fleet/access/fleetAccess.css"], width: 1300, height: 1000, reducedMotion: true } as const;
// Types in an input as the user does: React reads the input event.
const type = (selector: string, value: string) => `(() => { const input = document.querySelector(${JSON.stringify(selector)});
  Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set.call(input, ${JSON.stringify(value)});
  input.dispatchEvent(new Event('input', { bubbles: true })); return true; })()`;
// Chooses an option of a native select as the user does.
const choose = (selector: string, value: string) => `(() => { const select = document.querySelector(${JSON.stringify(selector)});
  Object.getOwnPropertyDescriptor(HTMLSelectElement.prototype, 'value').set.call(select, ${JSON.stringify(value)});
  select.dispatchEvent(new Event('change', { bubbles: true })); return true; })()`;
const calls = `[...window.fleetAccessFixture.calls].map(call => JSON.parse(call))`;
const overflow = `[...document.querySelectorAll('.secret-row')].filter(row => row.scrollWidth > row.clientWidth + 1).length`;

test("at home the page shows people and machines, invites a person with a code, and keeps teams under Advanced", { skip: !edge, timeout: 90_000 }, async () => {
  await withCanvas(async page => {
    await page.until(`document.querySelectorAll('.fleet-access-person').length === 3`, "the people");
    await page.shot("fleet-access-home");
    const people = await page.evaluate(`[...document.querySelectorAll('.fleet-access-person')].map(row => [row.dataset.person,
      [...row.querySelectorAll('.fleet-secret-tags .bp6-tag')].map(tag => tag.textContent)])`);
    assert.deepEqual(people, [["Alex", ["You", "Owner"]], ["Bea", []], ["Cy", ["Invited"]]], "the owner's own role is shown, the others' can be changed");
    assert.equal(await page.evaluate(`[...document.querySelectorAll('h2')].some(title => title.textContent === 'Teams')`), false, "no team at home");
    assert.equal(await page.evaluate(`document.querySelector('.fleet-access-waiting')`), null, "nothing waits");

    await page.clickText(".fleet-secrets-head .bp6-button", "Invite a person…");
    await page.until(`document.querySelector('.fleet-access-invite')`, "the form");
    await page.evaluate(type(".fleet-access-invite input[aria-label=Name]", "Dana"));
    await page.clickText(".fleet-access-invite .bp6-button", "Invite");
    await page.until(`document.querySelector('.fleet-access-invitation')`, "the code");
    assert.deepEqual(await page.evaluate(`[...document.querySelectorAll('.fleet-access-invitation input')].map(input => input.value)`),
      ["https://home.example", "aet_example-code"]);
    await page.shot("fleet-access-invitation");
    assert.deepEqual((await page.evaluate(calls) as unknown[])[0], { invite: { name: "Dana", role: "member" } });

    await page.evaluate(`[...document.querySelectorAll('.fleet-access-footer label')].find(label => label.textContent.includes('Show teams')).click()`);
    await page.until(`[...document.querySelectorAll('h2')].some(title => title.textContent === 'Teams')`, "the teams under Advanced");
    assert.deepEqual(page.problems, []);
  }, options);
});

test("in an organization what waits is reviewed and signed, a transfer taken, a proposal applied, and a person who leaves deprovisioned", { skip: !edge, timeout: 90_000 }, async () => {
  await withCanvas(async page => {
    await page.until(`document.querySelectorAll('.fleet-access-person').length === 3`, "the page");
    await page.evaluate(`window.fleetAccessFixture.company()`);
    await page.until(`document.querySelector('.fleet-access-waiting')`, "what waits");
    await page.shot("fleet-access-company");
    assert.equal(await page.evaluate(overflow), 0, "nothing overflows");

    // The changes machines do not follow yet, reviewed as this computer reads them, then signed.
    await page.clickText(".fleet-access-sign .bp6-button", "Review and sign…");
    await page.until(`document.querySelector('.fleet-access-review')`, "the review");
    assert.deepEqual(await page.evaluate(`[...document.querySelectorAll('.fleet-access-review li')].map(item => item.textContent)`),
      ["Max is a member of Infra", "Sam is suspended: their work stops on team machines"]);
    await page.clickText(".fleet-access-review .bp6-button", "Sign");
    await page.until(`window.fleetAccessFixture.calls.length === 1`, "signed");

    await page.clickText("[data-transfer=t1] .bp6-button", "Accept");
    await page.until(`window.fleetAccessFixture.calls.length === 2`, "accepted");
    await page.clickText("[data-proposal=p1] .bp6-button", "Apply");
    await page.until(`window.fleetAccessFixture.calls.length === 3`, "applied");
    assert.match(await page.evaluate(`document.querySelector('[data-proposal=p1] .secret-reference').textContent`) as string,
      /Proposed by an agent of Max · teamId: Infra · personId: Ada · role: member/);

    // The automation whose owner left goes to the team.
    await page.evaluate(`document.querySelector('.fleet-access-waiting [data-resource="nightly-release-notes"] .secret-actions .bp6-button').click()`);
    await page.until(`document.querySelector('.fleet-access-give')`, "where it goes");
    await page.evaluate(choose(".fleet-access-give select", "team:infra"));
    await page.clickText(".fleet-access-give .bp6-button", "Give");
    await page.until(`window.fleetAccessFixture.calls.length === 4`, "offered");

    // Max leaves: what he owned goes to the team.
    await page.evaluate(`document.querySelector('[data-person=Max] button[aria-label="Remove Max from the organization"]').click()`);
    await page.until(`document.querySelector('.fleet-access-remove')`, "the confirmation");
    await page.evaluate(choose(".fleet-access-remove select", "team:infra"));
    await page.shot("fleet-access-deprovision");
    await page.clickText(".fleet-access-remove .bp6-button", "Remove from the organization");
    await page.until(`window.fleetAccessFixture.calls.length === 5`, "deprovisioned");
    assert.deepEqual(await page.evaluate(calls), [
      { sign: { changes: ["Max is a member of Infra", "Sam is suspended: their work stops on team machines"] } },
      { transfer: { id: "t1", verb: "accept" } },
      { proposal: { id: "p1", apply: true } },
      { offer: { resourceKind: "automation", resourceId: "nightly-release-notes", toKind: "team", toId: "infra" } },
      { deprovision: { personId: "max", successorKind: "team", successorId: "infra" } },
    ]);
    assert.equal(await page.evaluate(`document.querySelector('[data-person=Sam] .bp6-intent-danger')?.textContent`), "Suspended");
    assert.equal(await page.evaluate(`document.querySelector('[data-grant="session.view-content"] .bp6-tag')?.textContent`), "Waiting for keys");

    // A window of 900 by 650: rows wrap, none overflows.
    await page.evaluate(`window.fleetAccessFixture.setWidth(430)`);
    await page.until(`document.querySelector('.fleet-access-settings').getBoundingClientRect().width < 450`, "the narrow page");
    await page.shot("fleet-access-narrow");
    assert.equal(await page.evaluate(overflow), 0, "no row overflows");
    assert.deepEqual(page.problems, []);
  }, options);
});
