import assert from "node:assert/strict";
import test from "node:test";
import { edge, withCanvas } from "../statistics/browserHarness";
import { testLimit } from "../testTime";

const options = { entry: "../workItems/workItemsFocus.mount.tsx", fixture: "focusFixture", rpcFixture: "../demo-api.ts", width: 1100, height: 700,
  styles: ["../workItems/workItems.css"] } as const;
// Whether the selected row is inside the list that scrolls, and how far the list is scrolled.
const selectedInView = `(() => { const list = document.querySelector('.work-list'), row = document.querySelector('.work-row[data-selected]');
  if (!row) return null; const box = row.getBoundingClientRect(), frame = list.getBoundingClientRect(); return box.top >= frame.top && box.bottom <= frame.bottom; })()`;

test("a task another part of the window asks for is selected and scrolled into view", { skip: !edge, timeout: testLimit(60_000) }, async () => {
  await withCanvas(async page => {
    await page.until(`document.querySelectorAll('.work-row').length === 40`, "the forty tasks");
    assert.equal(await page.evaluate(`document.querySelector('.work-list').scrollHeight > document.querySelector('.work-list').clientHeight`), true, "the list scrolls");
    await page.evaluate(`focusFixture.focus({ projectId: "p", key: focusFixture.key(40) })`);
    await page.until(`document.querySelector('.work-row[data-selected] strong')?.textContent === 'Task 40'`, "the last task selected");
    await page.until(`${selectedInView} === true`, "the selected task scrolled into view");
    // Asked again after the list was scrolled back to the top: the task is brought into view once more.
    await page.evaluate(`document.querySelector('.work-list').scrollTop = 0`);
    await page.until(`${selectedInView} === false`, "the list back at the top");
    await page.evaluate(`focusFixture.focus({ projectId: "p", key: focusFixture.key(40) })`);
    await page.until(`${selectedInView} === true`, "the same task brought into view again");
  }, options);
});
