import assert from "node:assert/strict";
import test from "node:test";
import { edge, withCanvas } from "../statistics/browserHarness";

const options = { entry: "../workItems/taskReferences.mount.tsx", fixture: "taskFixture", rpcFixture: "../demo-api.ts", width: 900, height: 650 } as const;
const chip = (shortId: string) => `Array.from(document.querySelectorAll('.markdown-content code[data-task-ref]')).find(code => code.textContent === '${shortId}')`;
const card = `document.querySelector('.task-reference-card')`;

test("a code span that names a task of the project is a chip that describes the task and opens it", { skip: !edge, timeout: 90_000 }, async () => {
  await withCanvas(async page => {
    await page.until(`document.querySelectorAll('.markdown-content code[data-task-ref]').length === 2`, "the chips of the two tasks");
    // A task that is done is struck through; an unknown id, a code block and a code span in a link stay what they are.
    assert.deepEqual(await page.evaluate(`Array.from(document.querySelectorAll('.markdown-content code'), code => [code.textContent, code.getAttribute('data-task-ref'),
      code.hasAttribute('data-task-closed'), code.getAttribute('role'), code.getAttribute('aria-label')])`), [
      ["p53b", "2026-11-08-fix-the-parser-p53b", true, "link", "Task p53b: Fix the parser"],
      ["q9rh", "2026-11-08-report-the-provider-error-q9rh", false, "link", "Task q9rh: Report the provider error"],
      ["zzzz", null, false, null, null], ["p53b\n", null, false, null, null], ["q9rh", null, false, null, null]]);
    assert.equal(await page.evaluate(`getComputedStyle(${chip("p53b")}).textDecorationLine`), "line-through");

    // The pointer on a chip shows its card, which the chip names for a screen reader.
    const box = await page.evaluate<{ x: number; y: number; width: number; height: number }>(`${chip("q9rh")}.getBoundingClientRect().toJSON()`);
    await page.command("Input.dispatchMouseEvent", { type: "mouseMoved", x: box.x + box.width / 2, y: box.y + box.height / 2 });
    await page.until(`${card}?.textContent.includes('Report the provider error')`, "the card of the task");
    assert.equal(await page.evaluate(`${card}.textContent.includes('The model list shows nothing when a provider fails.') && ${card}.textContent.includes('To do')`), true);
    assert.equal(await page.evaluate(`${chip("q9rh")}.getAttribute('aria-describedby') === ${card}.id`), true);
    await page.command("Input.dispatchMouseEvent", { type: "mouseMoved", x: 4, y: 600 });
    await page.until(`!${card}`, "the card put away when the pointer leaves");

    await page.click(`.markdown-content code[data-task-ref="2026-11-08-report-the-provider-error-q9rh"]`);
    await page.until(`taskFixture.opened.length === 1`, "the task opened by a click");
    assert.deepEqual(await page.evaluate(`taskFixture.opened`), ["2026-11-08-report-the-provider-error-q9rh"]);

    // The keyboard reaches a chip, which shows its card and opens with Enter.
    await page.evaluate(`${chip("p53b")}.focus()`);
    await page.until(`${card}?.textContent.includes('Fix the parser') && !!${card}.querySelector('.task-reference-card-item[data-closed]')`, "the card of the focused chip");
    await page.key("Enter", 13, "\r");
    await page.until(`taskFixture.opened.length === 2`, "the task opened by Enter");
    assert.equal(await page.evaluate(`taskFixture.opened[1]`), "2026-11-08-fix-the-parser-p53b");

    // A task that is removed is no longer a chip.
    await page.evaluate(`taskFixture.remove("2026-11-08-report-the-provider-error-q9rh")`);
    await page.until(`document.querySelectorAll('.markdown-content code[data-task-ref]').length === 1`, "the chip of the removed task undone");
    assert.equal(await page.evaluate(`Array.from(document.querySelectorAll('.markdown-content code')).find(code => code.textContent === 'q9rh').hasAttribute('role')`), false);
    assert.deepEqual(page.problems, []);
  }, options);
});
