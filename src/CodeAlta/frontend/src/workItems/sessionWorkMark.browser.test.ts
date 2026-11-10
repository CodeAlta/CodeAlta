import assert from "node:assert/strict";
import test from "node:test";
import { edge, withCanvas, type Page } from "../statistics/browserHarness";
import { testLimit } from "../testTime";

const options = { entry: "../workItems/sessionWorkMark.mount.tsx", fixture: "markFixture", rpcFixture: "../demo-api.ts", width: 900, height: 650,
  styles: ["../workItems/workItems.css"] } as const;
const mark = (session: string) => `document.querySelector('.session-row[data-session="${session}"] .session-work-mark')`;
const card = `document.querySelector('.task-reference-card')`;
const tip = `document.querySelector('.overflow-tip')`;

async function point(page: Page, element: string) {
  const box = await page.evaluate<{ x: number; y: number; width: number; height: number }>(`${element}.getBoundingClientRect().toJSON()`);
  await page.command("Input.dispatchMouseEvent", { type: "mouseMoved", x: box.x + box.width / 2, y: box.y + box.height / 2 });
}

test("the work mark of a session row shows its items, short id or count, and opens them without selecting the row", { skip: !edge, timeout: testLimit(90_000) }, async () => {
  await withCanvas(async page => {
    await page.until(`document.querySelectorAll('.session-work-mark').length === 3`, "the marks of the three rows");
    // A wide row says the short id of its item, or the count of its items; a narrow one keeps the glyph.
    assert.deepEqual(await page.evaluate(`[${mark("s1")}, ${mark("s2")}, ${mark("s3")}].map(element => element.textContent)`), ["q9rh", "2", "p53b"]);
    assert.equal(await page.evaluate(`getComputedStyle(${mark("s1")}.querySelector('.session-work-id')).display !== 'none'`), true);
    assert.equal(await page.evaluate(`getComputedStyle(${mark("s3")}.querySelector('.session-work-id')).display`), "none");

    // The title of the row shows its tip; the mark puts it away and shows its card, and the row under it is not lit.
    await point(page, `document.querySelector('.session-row[data-session="s1"] .session-title')`);
    await page.until(`!!${tip}`, "the tip of the cut title");
    await point(page, mark("s1"));
    await page.until(`!${tip} && ${card}?.textContent.includes('Scan the devices in the background')`, "the card of the item instead of the tip");
    assert.equal(await page.evaluate(`${card}.textContent.includes('q9rh') && ${card}.textContent.includes('Why Scan the devices in the background.')`), true);
    assert.equal(await page.evaluate(`${mark("s1")}.getAttribute('title')`), "", "no tooltip of the row over the mark");
    assert.equal(await page.evaluate(`getComputedStyle(document.querySelector('.session-row[data-session="s1"]')).backgroundColor`), "rgba(0, 0, 0, 0)");
    assert.notEqual(await page.evaluate(`getComputedStyle(${mark("s1")}).backgroundColor`), "rgba(0, 0, 0, 0)");

    // A click opens the item, and does not select the session.
    await page.click(`.session-row[data-session="s1"] .session-work-mark`);
    await page.until(`markFixture.opened.length === 1`, "the item opened by a click");
    assert.deepEqual(await page.evaluate(`[markFixture.opened, markFixture.selected, !!${card}]`), [["2026-11-09-scan-the-devices-q9rh"], [], false]);

    // Several items: the card lists them, and a click shows a menu of them.
    await point(page, mark("s2"));
    await page.until(`${card}?.querySelectorAll('.task-reference-card-item').length === 2`, "the card of the two items");
    assert.equal(await page.evaluate(`${card}.textContent.includes('Move sessions between projects') && ${card}.textContent.includes('Report the provider error')`), true);
    await page.click(`.session-row[data-session="s2"] .session-work-mark`);
    await page.until(`document.querySelectorAll('.bp6-menu-item').length === 2 && !${card}`, "the menu of the items");
    await page.clickText(".bp6-menu-item .bp6-fill", "Report the provider error");
    await page.until(`markFixture.opened.length === 2`, "the item chosen in the menu opened");
    assert.deepEqual(await page.evaluate(`[markFixture.opened[1], markFixture.selected]`), ["2026-11-09-report-the-provider-error-k2mz", []]);
    assert.deepEqual(page.problems, []);
  }, options);
});
