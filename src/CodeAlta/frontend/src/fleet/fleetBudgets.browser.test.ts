import assert from "node:assert/strict";
import test from "node:test";
import { edge, withCanvas } from "../statistics/browserHarness";

const options = { entry: "../fleet/fleetBudgets.mount.tsx", fixture: "fleetBudgetsFixture", rpcFixture: "../demo-api.ts",
  styles: ["../secrets/secrets.css", "../fleet/fleet.css", "../fleet/fleetSecrets.css", "../fleet/fleetBudgets.css"], width: 1300, height: 900, reducedMotion: true } as const;
// Types in an input as the user does: React reads the input event.
const type = (selector: string, value: string) => `(() => { const input = document.querySelector(${JSON.stringify(selector)});
  Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set.call(input, ${JSON.stringify(value)});
  input.dispatchEvent(new Event('input', { bubbles: true })); return true; })()`;
// Chooses an option of a native select as the user does.
const choose = (selector: string, value: string) => `(() => { const select = document.querySelector(${JSON.stringify(selector)});
  Object.getOwnPropertyDescriptor(HTMLSelectElement.prototype, 'value').set.call(select, ${JSON.stringify(value)});
  select.dispatchEvent(new Event('change', { bubbles: true })); return true; })()`;
const calls = `[...window.fleetBudgetsFixture.calls].map(call => JSON.parse(call))`;

test("each budget shows its scope, rules and consumption, and a budget is added and removed from the page", { skip: !edge, timeout: 90_000 }, async () => {
  await withCanvas(async page => {
    await page.until(`document.querySelectorAll('.fleet-budget-row').length === 2`, "the budgets");
    await page.shot("fleet-budgets-large");
    const rows = await page.evaluate(`[...document.querySelectorAll('.fleet-budget-row')].map(row => [row.dataset.budget, row.dataset.state,
      row.querySelector('.secret-reference').textContent, [...row.querySelectorAll('.fleet-secret-tags .bp6-tag')].map(tag => tag.textContent),
      row.querySelector('.fleet-budget-usage .secret-meta').textContent.split(' · ').slice(0, 2).join(' · ')])`) as unknown[];
    assert.deepEqual(rows, [
      ["Home tokens", "reached", "All my work", ["Per month", "Enforced", "Holds new runs"], "2M of 2M tokens · 100%"],
      ["Build box spending of the week", "warning", "Machine: Build box", ["Per week", "Advisory", "Stops new runs"], "$34 of $40 · 85%"],
    ]);
    assert.equal(await page.evaluate(`document.querySelector('[data-budget="Home tokens"] .bp6-progress-bar').classList.contains('bp6-intent-danger')`), true, "a reached budget is red");
    assert.equal(await page.evaluate(`[...document.querySelectorAll('.secret-row')].filter(row => row.scrollWidth > row.clientWidth + 1).length`), 0, "nothing overflows");
    assert.match(await page.evaluate(`document.querySelector('.fleet-notification-row').textContent`) as string, /Build box is low on disk space: 900 MB left\./);

    // Add a budget…: a project, in dollars, per day, advisory.
    await page.clickText(".fleet-secrets-head .bp6-button", "Add a budget…");
    await page.until(`document.querySelector('.fleet-budget-form')`, "the form");
    await page.evaluate(type(".fleet-budget-form input[aria-label=Name]", "API per day"));
    await page.evaluate(choose(".fleet-budget-form select[aria-label='Where it applies']", "project:api"));
    await page.evaluate(choose(".fleet-budget-form select[aria-label='What it limits']", "cost"));
    await page.evaluate(type(".fleet-budget-form input[aria-label=Limit]", "12.50"));
    await page.evaluate(choose(".fleet-budget-form select[aria-label=Period]", "day"));
    await page.evaluate(choose(".fleet-budget-form select[aria-label='How it holds']", "advisory"));
    await page.shot("fleet-budgets-add");
    await page.clickText(".fleet-budget-form .bp6-button", "Add");
    await page.until(`!document.querySelector('.fleet-budget-form')`, "the form closes");
    assert.deepEqual((await page.evaluate(calls) as unknown[])[0], { set: { id: null, name: "API per day", scopeKind: "project", scopeId: "api", measure: "cost", limit: 12.5,
      period: "day", mode: "advisory", reached: "hold" } });

    // Edit beside its row, in a popover: the limit of the month goes up.
    await page.evaluate(`document.querySelector('[data-budget="Home tokens"] button[aria-label="Edit Home tokens"]').click()`);
    await page.until(`document.querySelector('.bp6-popover .fleet-budget-form')`, "the popover");
    await page.evaluate(type(".bp6-popover .fleet-budget-form input[aria-label=Limit]", "3000000"));
    await page.clickText(".bp6-popover .fleet-budget-form .bp6-button", "Save");
    await page.until(`window.fleetBudgetsFixture.calls.length === 2`, "saved");
    assert.deepEqual((await page.evaluate(calls) as unknown[])[1], { set: { id: "b1", name: "Home tokens", scopeKind: "person", scopeId: null, measure: "tokens", limit: 3_000_000,
      period: "month", mode: "enforced", reached: "hold" } });

    // Remove, confirmed beside its row.
    await page.evaluate(`document.querySelector('[data-budget="Build box spending of the week"] button[aria-label="Remove Build box spending of the week"]').click()`);
    await page.until(`document.querySelector('.bp6-popover')?.textContent.includes('Remove this budget?')`, "the confirmation");
    await page.clickText(".bp6-popover .bp6-button", "Remove");
    await page.until(`window.fleetBudgetsFixture.calls.length === 3`, "removed");
    assert.deepEqual((await page.evaluate(calls) as unknown[])[2], { remove: "b2" });
    assert.deepEqual(page.problems, []);
  }, options);
});

test("a notification closed with the ✕ of its toast is dismissed for the person, and does not come back when the window starts again",
  { skip: !edge, timeout: 90_000 }, async () => {
    await withCanvas(async page => {
      await page.until(`document.querySelector('.fleet-hold-row')`, "the page");
      await page.evaluate(`window.fleetBudgetsFixture.showButton()`);
      await page.until(`document.querySelector('.fleet-notification-toast')`, "the toast of the notification");
      await page.evaluate(`document.querySelector('.fleet-notification-toast').closest('.bp6-toast').querySelector('button[aria-label=Close]').click()`);
      await page.until(`document.querySelector('.fleet-approvals-button')?.getAttribute('aria-label') === 'Fleet: 1 thing needs you'`, "it no longer waits");
      assert.deepEqual(await page.evaluate(calls), [{ read: "n1" }], "closing it is dismissing it, on the server, for every computer of the person");
      await page.until(`!document.querySelector('.fleet-notification-toast')`, "its toast is gone");

      // The window starts again: the run held still waits, the notification is not shown again.
      await page.evaluate(`window.fleetBudgetsFixture.hideButton()`);
      await page.until(`!document.querySelector('.fleet-approvals-button')`, "the window closed");
      await page.evaluate(`window.fleetBudgetsFixture.showButton()`);
      await page.until(`document.querySelector('.fleet-approvals-button')?.getAttribute('aria-label') === 'Fleet: 1 thing needs you'`, "read again");
      await page.until(`document.querySelector('.fleet-hold-toast')`, "the toast of the run");
      assert.equal(await page.evaluate(`document.querySelectorAll('.fleet-notification-toast').length`), 0, "no toast of the notification");
      assert.deepEqual(await page.evaluate(calls), [{ read: "n1" }], "the hold toast taken away and shown again marks nothing");
      assert.deepEqual(page.problems, []);
    }, options);
  });

test("a session started on a machine whose budget holds its prompt says it waits, and Continue anyway beside it continues that run",
  { skip: !edge, timeout: 90_000 }, async () => {
    await withCanvas(async page => {
      await page.until(`document.querySelector('.fleet-hold-row')`, "the page");
      await page.evaluate(`window.fleetBudgetsFixture.heldStart()`);
      await page.until(`document.querySelector('.fleet-held-start-toast')`, "the toast of the start");
      assert.equal(await page.evaluate(`document.querySelector('.fleet-held-start-toast strong').textContent`),
        "The session was started on Mac mini. Its prompt waits for a reached budget of the machine: it is sent once you continue it anyway.");
      await page.shot("fleet-held-start-toast");
      await page.evaluate(`[...document.querySelectorAll('.fleet-held-start-toast .bp6-button')].find(button => button.textContent === 'Continue anyway').click()`);
      await page.until(`!document.querySelector('.fleet-held-start-toast')`, "continued, it goes away");
      assert.deepEqual(await page.evaluate(calls), [{ continue: "h1" }], "the hold of that run, found by its session");
      assert.deepEqual(page.problems, []);
    }, options);
  });

test("a run held by a budget is continued anyway from the Fleet button's toast, the notification is dismissed, and the page stays whole when narrow",
  { skip: !edge, timeout: 90_000 }, async () => {
    await withCanvas(async page => {
      await page.until(`document.querySelector('.fleet-hold-row')`, "the run that waits");
      await page.evaluate(`window.fleetBudgetsFixture.showButton()`);
      await page.until(`document.querySelector('.fleet-approvals-button')?.getAttribute('aria-label') === 'Fleet: 2 things need you'`, "the mark");
      await page.until(`document.querySelector('.fleet-hold-toast')`, "the toast of the run");
      assert.equal(await page.evaluate(`document.querySelector('.fleet-hold-toast strong').textContent`), "A run on Mac mini waits for Home tokens.");
      await page.until(`document.querySelector('.fleet-notification-toast')`, "the toast of the notification");
      assert.equal(await page.evaluate(`document.querySelector('.fleet-notification-toast strong').textContent`), "Build box is low on disk space: 900 MB left.");
      await page.shot("fleet-budgets-toasts");
      // Two toasts that come together stay in the appear transition of the toaster: their buttons are pressed directly.
      await page.evaluate(`[...document.querySelectorAll('.fleet-hold-toast .bp6-button')].find(button => button.textContent === 'Continue anyway').click()`);
      await page.until(`document.querySelector('.fleet-approvals-button')?.getAttribute('aria-label') === 'Fleet: 1 thing needs you'`, "the run no longer waits");
      await page.until(`!document.querySelector('.fleet-hold-toast')`, "its toast goes away");
      await page.evaluate(`[...document.querySelectorAll('.fleet-notification-toast .bp6-button')].find(button => button.textContent === 'Dismiss').click()`);
      await page.until(`document.querySelector('.fleet-approvals-button')?.getAttribute('aria-label') === 'Fleet'`, "nothing waits");
      assert.deepEqual(await page.evaluate(calls), [{ continue: "h1" }, { read: "n1" }]);

      // A window of 900 by 650: rows wrap, none overflows.
      await page.evaluate(`window.fleetBudgetsFixture.setWidth(430)`);
      await page.until(`document.querySelector('.fleet-budgets-settings').getBoundingClientRect().width < 450`, "the narrow page");
      await page.shot("fleet-budgets-narrow");
      assert.equal(await page.evaluate(`[...document.querySelectorAll('.secret-row')].filter(row => row.scrollWidth > row.clientWidth + 1).length`), 0, "no row overflows");
      assert.deepEqual(page.problems, []);
    }, options);
  });
