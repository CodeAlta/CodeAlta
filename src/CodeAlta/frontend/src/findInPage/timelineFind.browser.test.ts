import assert from "node:assert/strict";
import test from "node:test";
import { edge, withCanvas, type Page } from "../statistics/browserHarness";

// The search in the timeline of a session in headless Edge, under React StrictMode, with the real keys.

const options = { entry: "../findInPage/timelineFind.mount.tsx", fixture: "timelineFindFixture", rpcFixture: "../demo-api.ts", width: 1100, height: 700, styles: ["../timelineActivity/timelineActivity.css"] } as const;
const scroller = `document.querySelector('.timeline-scroll')`;
const bar = `document.querySelector('.find-in-page')`;
const input = `${bar}.querySelector('input')`;
const count = `${bar}.querySelector('.find-in-page-count').textContent`;
const marks = "(CSS.highlights.get('find-in-page')?.size ?? 0)";
const atEnd = `Math.abs(${scroller}.scrollHeight - ${scroller}.clientHeight - ${scroller}.scrollTop) <= 1`;
const current = "[...CSS.highlights.get('find-in-page-current')][0]";
/** Whether the place shown is drawn inside the view of the timeline. */
const currentSeen = `(() => { const box = ${current}.getBoundingClientRect(), view = ${scroller}.getBoundingClientRect(); return box.height > 0 && box.top >= view.top && box.bottom <= view.bottom; })()`;
const strip = `document.querySelector('.find-in-page-ticks')`;
const ticks = `document.querySelectorAll('.find-in-page-ticks > span').length`;
/** How far the mark of the place shown is from where the place is along the scrollbar, in pixels. */
const currentTickOff = `(() => { const view = ${scroller}, frame = view.getBoundingClientRect(), mark = document.querySelector('.find-in-page-ticks > span[data-current]');
  if (!mark) return 999; const at = (${current}.getBoundingClientRect().top - frame.top + view.scrollTop) / view.scrollHeight * view.clientHeight;
  return Math.abs(mark.getBoundingClientRect().top - frame.top - at); })()`;

/** Ctrl+F, Cmd+F on macOS: the find in a session. The search of the sessions is Ctrl+F on every system (`ctrl`). */
async function find(page: Page, ctrl = false) {
  const modifiers = !ctrl && process.platform === "darwin" ? 4 : 2;
  await page.command("Input.dispatchKeyEvent", { type: "rawKeyDown", key: "f", code: "KeyF", windowsVirtualKeyCode: 70, modifiers });
  await page.command("Input.dispatchKeyEvent", { type: "keyUp", key: "f", code: "KeyF", windowsVirtualKeyCode: 70, modifiers });
}
const key = async (page: Page, name: string, code: number, modifiers = 0) => {
  await page.command("Input.dispatchKeyEvent", { type: "rawKeyDown", key: name, code: name, windowsVirtualKeyCode: code, modifiers });
  await page.command("Input.dispatchKeyEvent", { type: "keyUp", key: name, code: name, windowsVirtualKeyCode: code, modifiers });
};
/** Replaces what is typed in the box. */
const typed = (page: Page, text: string) => page.evaluate(`(() => { const box = ${input};
  Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, "value").set.call(box, ${JSON.stringify(text)}); box.dispatchEvent(new Event("input", { bubbles: true })); })()`);

test("Ctrl+F in a session finds in its timeline: every match marked and counted, from the prompt too, the reader taken to them", { skip: !edge, timeout: 180_000 }, async () => {
  await withCanvas(async page => {
    await page.until(`document.querySelector('.history[data-window-ready="true"]') && document.querySelectorAll('.timeline-message').length > 0`, "the timeline read");
    await page.until(`${atEnd} && ${scroller}.dataset.following === 'true'`, "the reader at its end");

    // The keyboard is in the prompt: Ctrl+F finds in the timeline, and the window does not search the sessions.
    await page.evaluate("document.querySelector('#session-prompt').focus()");
    await find(page);
    await page.until(`document.activeElement === ${input}`, "the box takes the keyboard");
    assert.equal(await page.evaluate("timelineFindFixture.state.searchedSessions"), 0);
    assert.equal(await page.evaluate("timelineFindFixture.state.promptSearched"), 0, "The text editor of the prompt does not open its own search.");
    assert.equal(await page.evaluate(`${input}.placeholder`), "Find in this session");

    // Every place in the turn that is loaded, whatever its case. With none after the reader at the end, the nearest before them.
    await typed(page, "needle");
    await page.until(`${count} === '2 of 2+' && ${marks} === 2`, "both places counted; the older turns may hold more");
    assert.equal(await page.evaluate(`${current}.toString()`), "NEEDLE");
    await page.until(currentSeen, "the place shown is in view, though its row was not laid out");
    await page.until(`${scroller}.dataset.following === 'false'`, "the reader is at the place now, not following the end");
    const top = await page.evaluate<number>(`${scroller}.scrollTop`);
    assert.equal(await page.evaluate("timelineFindFixture.state.moved > 0"), true);

    // The places are marked along the scrollbar, the place shown apart, and the scrollbar still takes the mouse.
    await page.until(`${ticks} === 2 && ${currentTickOff} <= 3`, "two marks over the scrollbar, the place shown where it is");
    const track = await page.evaluate<{ strip: number[]; view: number[]; under: boolean }>(`(() => { const box = ${strip}.getBoundingClientRect(), view = ${scroller}, frame = view.getBoundingClientRect();
      return { strip: [box.top, box.height, box.right, box.width], view: [frame.top, view.clientHeight, frame.right, view.offsetWidth - view.clientWidth],
        under: document.elementFromPoint(box.left + box.width / 2, box.top + box.height / 2) === view }; })()`);
    assert.deepEqual(track.strip, track.view, "The marks lie on the scrollbar.");
    assert.ok(track.strip[3] > 0, "There is a scrollbar.");
    assert.equal(track.under, true, "The mouse goes through the marks to the scrollbar.");
    const shownTick = `document.querySelector('.find-in-page-ticks > span[data-current]').style.top`;
    const before = await page.evaluate<string>(shownTick);

    // Enter goes on, around, and Shift+Enter back; each place is shown.
    await key(page, "Enter", 13);
    await page.until(`${count} === '1 of 2+'`, "next, around to the first");
    assert.equal(await page.evaluate(`${current}.toString()`), "needle");
    await page.until(currentSeen, "the first place in view");
    await page.until(`${shownTick} !== ${JSON.stringify(before)}`, "the mark of the place shown goes with it");
    await key(page, "Enter", 13, 8);
    await page.until(`${count} === '2 of 2+' && ${currentSeen}`, "back");
    assert.ok(Math.abs(await page.evaluate<number>(`${scroller}.scrollTop`) - top) < 400, "back near where it was");

    // The times and the buttons of the messages are not words of the timeline.
    const time = await page.evaluate<string>(`document.querySelector('.message-actions time').textContent`);
    assert.ok(time.length > 0, "A message says its time.");
    await typed(page, time);
    await page.until(`${count}.startsWith('0 of 0')`, "the time is not searched");
    await page.until(`!${strip}`, "nothing found marks nothing along the scrollbar");

    // A word in folded work opens the fold while it is looked for, and it folds again after.
    assert.equal(await page.evaluate(`document.querySelector('.timeline-activity').dataset.expanded`), "false");
    await typed(page, "foldedword");
    await page.until(`${count} === '1 of 1+' && document.querySelector('.timeline-activity').dataset.expanded === 'true'`, "the fold opened and its place counted");
    await page.until(currentSeen, "the place in the fold in view");
    await typed(page, "needle");
    await page.until(`${count}.endsWith('of 2+') && document.querySelector('.timeline-activity').dataset.expanded === 'false'`, "it folds again");

    // Loading all the messages searches them too, and the count no longer says more may be there.
    await page.evaluate(`${bar}.querySelector('button[aria-label="Load all messages"]').click()`);
    await page.until(`${count}.endsWith('of 6') && ${marks} === 6`, "the older turns are found, and the count is whole");
    assert.equal(await page.evaluate(`!!${bar}.querySelector('button[aria-label="Load all messages"]')`), false, "Nothing left to load.");
    assert.equal(await page.evaluate(`${current}.toString()`), "NEEDLE", "The place shown stays, though places came before it.");
    assert.equal(await page.evaluate(`${count}`), "6 of 6");
    await page.until(`${ticks} === 6 && ${currentTickOff} <= 3`, "the older turns are marked too, and the marks follow the taller timeline");

    // Escape closes it, takes its marks away and gives the keyboard back to the prompt.
    await key(page, "Escape", 27);
    await page.until(`!${bar} && !${strip} && !CSS.highlights.has('find-in-page') && !CSS.highlights.has('find-in-page-current')`, "closed");
    assert.equal(await page.evaluate("document.activeElement?.id"), "session-prompt");

    // Outside the session Ctrl+F is still the search of the sessions.
    await page.evaluate("(() => { const other = document.createElement('button'); other.id = 'outside'; document.body.appendChild(other); other.focus(); })()");
    await find(page, true);
    await page.until("timelineFindFixture.state.searchedSessions === 1", "the search of the sessions");
    assert.equal(await page.evaluate(bar), null);
  }, options);
});
