import assert from "node:assert/strict";
import test from "node:test";
import { edge, withCanvas, type Page } from "./statistics/browserHarness";

// The timeline of a session in headless Edge, under the production content security policy and React StrictMode:
// a reader who goes up gets the turns before the loaded ones, a chunk at a time, and stays on the rows in view.

const options = { entry: "../olderHistory.mount.tsx", fixture: "olderFixture", rpcFixture: "../demo-api.ts", width: 1100, height: 800 } as const;
const scroller = `document.querySelector('.timeline-scroll')`;
const ready = `document.querySelector('.history[data-window-ready="true"]') && document.querySelectorAll('.timeline-message').length > 0`;
const reads = `olderFixture.state.reads.length`;
/** The first turn the window holds: the turns are numbered from the start of the journal. */
const firstTurn = `Math.min(...[...document.querySelectorAll('.timeline-message')].map(row => Number(/turn (\\d+)/.exec(row.textContent)?.[1] ?? NaN)).filter(Number.isFinite))`;
/** How far below the top of the view the prompt of a turn is drawn. */
const promptTop = (turn: number) => `(() => {
  const view = ${scroller}, row = [...view.querySelectorAll('.timeline-message')].find(candidate => candidate.textContent.includes('Prompt of turn ${turn}'));
  return Math.round(row.getBoundingClientRect().top - view.getBoundingClientRect().top);
})()`;
const atEnd = `Math.abs(${scroller}.scrollHeight - ${scroller}.clientHeight - ${scroller}.scrollTop) <= 1`;
/** Where the bar of the older messages and its button are, in the content of the timeline. */
const bar = `(() => {
  const view = ${scroller}, box = document.querySelector('.load-more-bar').getBoundingClientRect(), button = document.querySelector('.load-more').getBoundingClientRect();
  return { top: Math.round(box.top - view.getBoundingClientRect().top + view.scrollTop), height: box.height, button: Math.round(button.left), width: button.width };
})()`;
const pause = (milliseconds: number) => new Promise(resolve => setTimeout(resolve, milliseconds));
/** Waits until the timeline has stopped moving, and gives where it is. */
const still = (page: Page) => page.evaluate<number>(`new Promise(resolve => {
  const view = ${scroller}; let last = view.scrollTop, same = 0;
  (function check() { requestAnimationFrame(() => { if (view.scrollTop === last) same++; else { same = 0; last = view.scrollTop; } if (same >= 10) resolve(last); else check(); }); })();
})`);
/** Turns the wheel over the timeline: up with a negative distance. */
const wheel = (page: Page, deltaY: number) => page.command("Input.dispatchMouseEvent", { type: "mouseWheel", x: 500, y: 400, deltaX: 0, deltaY });
/** Presses the mouse in the timeline, where nothing takes the keyboard: the keys then scroll the timeline. */
async function press(page: Page, y = 400) {
  await page.command("Input.dispatchMouseEvent", { type: "mouseMoved", x: 500, y });
  await page.command("Input.dispatchMouseEvent", { type: "mousePressed", x: 500, y, button: "left", clickCount: 1 });
  await page.command("Input.dispatchMouseEvent", { type: "mouseReleased", x: 500, y, button: "left", clickCount: 1 });
}
/** Waits for the window to start at a turn, drawn and still. */
async function turnsFrom(page: Page, turn: number, what: string) {
  await page.until(`${ready} && ${firstTurn} === ${turn}`, what);
  await still(page);
}

test("going up in a timeline reads the turns before, a chunk at a time, and the rows in view stay where they are", { skip: !edge, timeout: 180_000 }, async () => {
  await withCanvas(async page => {
    await turnsFrom(page, 9, "the latest turn");
    assert.equal(await page.evaluate(`document.querySelector('.load-more').textContent`), "Load all messages");
    assert.equal(await page.evaluate(`document.querySelectorAll('.load-more-bar button').length`), 1, "one button: none reads a single chunk");
    const first = await page.evaluate<number>(reads);

    // Nobody scrolls: nothing more is read.
    await pause(700);
    assert.equal(await page.evaluate(reads), first);
    assert.equal(await page.evaluate(`${scroller}.dataset.following`), "true");

    // Going up far from the top of what is loaded reads nothing either.
    await wheel(page, -300);
    assert.ok(await still(page) > 2000, "the latest turn is longer than the view");
    assert.equal(await page.evaluate(reads), first);
    await pause(600);

    // The timeline shows a message near the top by itself, as for a bookmark: nothing is read.
    assert.equal(await page.evaluate(`olderFixture.showTurn(9)`), true);
    await still(page);
    await pause(400);
    assert.equal(await page.evaluate(promptTop(9)), 8);
    assert.equal(await page.evaluate(reads), first);
    const idle = await page.evaluate(bar);

    // The reader goes up there: the chunk before is read. While the read lasts, a line says so in the place of the button.
    await page.evaluate(`olderFixture.hold()`);
    await wheel(page, -120);
    await page.until(`${reads} === ${first + 1}`, "the read of the chunk before");
    await page.until(`document.querySelector('.load-more-reading') && getComputedStyle(document.querySelector('.load-more-reading')).visibility === 'visible'`, "the line of the read");
    assert.equal(await page.evaluate(`document.querySelector('.load-more-reading').textContent`), "Loading previous messages…");
    assert.equal(await page.evaluate(`getComputedStyle(document.querySelector('.load-more')).visibility`), "hidden", "the line takes the place of the button");
    assert.deepEqual(await page.evaluate(bar), idle, "nothing moves when the line appears");
    // One read at a time: going up again while it lasts asks for nothing.
    await wheel(page, -120);
    assert.equal(await still(page), 0, "the reader is at the top of what is loaded");
    assert.equal(await page.evaluate(reads), first + 1);
    const before = await page.evaluate<number>(promptTop(9));
    await page.evaluate(`olderFixture.release()`);
    // At least 300 records are read and the whole turns among them are kept: the two turns before. The reader is
    // on the same row, at the same place.
    await turnsFrom(page, 7, "the chunk before");
    assert.equal(await page.evaluate(promptTop(9)), before, "the rows in view stayed where they were");
    assert.ok(await page.evaluate<number>(`${scroller}.scrollTop`) > 2000, "the rows of the chunk are above the view");
    assert.equal(await page.evaluate(`document.querySelector('.load-more-reading')`), null, "the line is gone");
    assert.equal(await page.evaluate(`getComputedStyle(document.querySelector('.load-more')).visibility`), "visible");
    assert.deepEqual(await page.evaluate(bar), idle, "nothing moved when the line went");
    // It does not run away: the next chunk waits for the reader.
    const one = await page.evaluate<number>(reads);
    await pause(900);
    assert.equal(await page.evaluate(reads), one);
    assert.equal(await page.evaluate(firstTurn), 7);

    // Home, with the keyboard on nothing after a press in the timeline: the view goes to the top of what is
    // loaded, the chunk before is read, and the reader stays on the first row that was loaded.
    await press(page);
    await page.key("Home", 36);
    await turnsFrom(page, 5, "the chunk before, after Home");
    // The chunk may arrive while the view is still on its way: it ends where it was going, on that row.
    const home = await page.evaluate<number>(promptTop(7));
    assert.equal(home, before, "the first row that was loaded is where the top of the timeline was");
    assert.ok(await page.evaluate<number>(`${scroller}.scrollTop`) > 2000);
    const two = await page.evaluate<number>(reads);
    await pause(900);
    assert.equal(await page.evaluate(reads), two, "one chunk for one move up");
    assert.equal(await page.evaluate(promptTop(7)), home);

    // The scrollbar dragged to the top: nothing is read while it is held, the chunk before when it is let go.
    const thumb = await page.evaluate<{ x: number; y: number; top: number }>(`(() => {
      const view = ${scroller}, box = view.getBoundingClientRect();
      const size = Math.max(30, box.height * view.clientHeight / view.scrollHeight);
      return { x: box.left + view.clientWidth + 5, y: box.top + (box.height - size) * view.scrollTop / (view.scrollHeight - view.clientHeight) + size / 2, top: box.top };
    })()`);
    await page.command("Input.dispatchMouseEvent", { type: "mouseMoved", x: thumb.x, y: thumb.y });
    await page.command("Input.dispatchMouseEvent", { type: "mousePressed", x: thumb.x, y: thumb.y, button: "left", clickCount: 1 });
    for (let y = thumb.y - 20; y > thumb.top - 60; y -= 20) await page.command("Input.dispatchMouseEvent", { type: "mouseMoved", x: thumb.x, y, button: "left", buttons: 1 });
    await page.until(`${scroller}.scrollTop === 0`, "the scrollbar at the top");
    await pause(700);
    assert.equal(await page.evaluate(reads), two, "nothing is read under a held scrollbar");
    const held = await page.evaluate<number>(promptTop(5));
    await page.command("Input.dispatchMouseEvent", { type: "mouseReleased", x: thumb.x, y: thumb.top - 40, button: "left", clickCount: 1 });
    await turnsFrom(page, 3, "the chunk before, once the scrollbar is let go");
    assert.equal(await page.evaluate(promptTop(5)), held, "the rows in view stayed where they were");

    // Up to the start of the journal: the last chunk is the two turns that are left, and the bar goes with them.
    await press(page);
    await page.key("Home", 36);
    await turnsFrom(page, 1, "the start of the journal");
    assert.equal(await page.evaluate(`document.querySelector('.load-more-bar')`), null);
    assert.equal(await page.evaluate(promptTop(3)), before, "the reader stays on the first row that was loaded");
    assert.equal(await page.evaluate(`document.querySelectorAll('.timeline-message').length`), 9 * 120);
    // Nothing is left to read: going up reads nothing.
    const all = await page.evaluate<number>(reads);
    await page.key("Home", 36);
    await page.until(`${scroller}.scrollTop === 0`, "the first message of the session");
    await wheel(page, -120);
    await pause(500);
    assert.equal(await page.evaluate(reads), all);
  }, options);
});

test("a short latest turn, a read that fails, a timeline behind another tab, and all the messages at once", { skip: !edge, timeout: 180_000 }, async () => {
  await withCanvas(async page => {
    await page.evaluate(`olderFixture.show({ sessionId: 'short' })`);
    await turnsFrom(page, 7, "the latest turn, two messages");
    assert.equal(await page.evaluate(`document.querySelectorAll('.timeline-message').length`), 2);
    assert.equal(await page.evaluate(`${scroller}.scrollHeight <= ${scroller}.clientHeight`), true, "nothing to scroll");
    const first = await page.evaluate<number>(reads);
    await pause(700);
    assert.equal(await page.evaluate(reads), first, "a view that is not full reads nothing by itself");

    // The wheel going up, with nothing to scroll, reads the turns before. The reader stays at the end, and follows it.
    await wheel(page, -120);
    await turnsFrom(page, 5, "the chunk before the short turn");
    assert.equal(await page.evaluate(`${scroller}.dataset.following`), "true");
    assert.equal(await page.evaluate(atEnd), true, "the short turn is still at the end of the view");
    const one = await page.evaluate<number>(reads);
    await pause(900);
    assert.equal(await page.evaluate(reads), one);

    // A key going up does the same after a press in the timeline. As in the window, where the panel of a tab is
    // focusable, the press leaves the keyboard on what holds the timeline, not on nothing.
    await page.evaluate(`olderFixture.show({ sessionId: 'long' })`);
    await turnsFrom(page, 9, "another session");
    await page.evaluate(`olderFixture.show({ sessionId: 'short' })`);
    await page.until(`${ready} && document.querySelectorAll('.timeline-message').length === 2`, "the short turn again");
    await still(page);
    await page.evaluate(`document.querySelector('.ide-shell').tabIndex = -1`);
    const again = await page.evaluate<number>(reads);
    await press(page);
    await pause(300);
    assert.equal(await page.evaluate(`document.activeElement === document.querySelector('.ide-shell')`), true, "the press puts the keyboard on the panel");
    assert.equal(await page.evaluate(reads), again, "a press reads nothing");
    await page.key("PageUp", 33);
    await turnsFrom(page, 5, "the chunk before, after Page Up");
    assert.equal(await page.evaluate(atEnd), true);
    await page.evaluate(`document.querySelector('.ide-shell').removeAttribute('tabindex'); document.activeElement.blur()`);
    await press(page);

    // A read that fails is shown as it was, and is not tried again by itself.
    await page.evaluate(`olderFixture.state.failures.push('history_changed')`);
    await page.key("Home", 36);
    await page.until(`document.querySelector('.history > .error-text')`, "the failed read");
    assert.match(await page.evaluate<string>(`document.querySelector('.history > .error-text').textContent`), /journal changed/i);
    assert.equal(await page.evaluate(firstTurn), 5, "what was loaded stays");
    const failed = await page.evaluate<number>(reads);
    await wheel(page, -120);
    await pause(700);
    assert.equal(await page.evaluate(reads), failed, "no loop, and no retry in the same move up");
    assert.equal(await page.evaluate(`!!document.querySelector('.history > .error-text')`), true, "the failure stays shown");
    // Going up again later reads the newest history again, which keeps the window; the next move up reads on.
    await pause(500);
    await wheel(page, -120);
    await page.until(`!document.querySelector('.history > .error-text') && ${ready}`, "the read tried again");
    assert.equal(await page.evaluate(`olderFixture.state.reads.at(-1)`), null, "the newest history was read again");
    assert.equal(await page.evaluate(firstTurn), 5);
    const retried = await page.evaluate<number>(reads);
    await pause(700);
    assert.equal(await page.evaluate(reads), retried, "the new read brings no older turns by itself");
    await wheel(page, -120);
    await turnsFrom(page, 3, "the chunk before, after the retry");

    // A timeline behind another tab reads nothing, and coming back reads nothing either: the reader has not moved.
    await page.evaluate(`olderFixture.show({ observing: false })`);
    const hidden = await page.evaluate<number>(reads);
    await page.key("Home", 36);
    await page.until(`${scroller}.scrollTop === 0`, "the top of what is loaded");
    await wheel(page, -120);
    await pause(700);
    assert.equal(await page.evaluate(reads), hidden);
    await page.evaluate(`olderFixture.show({ observing: true })`);
    await pause(700);
    assert.equal(await page.evaluate(reads), hidden);
    assert.equal(await page.evaluate(firstTurn), 3);

    // Going to the first loaded message reads the turns before it.
    assert.equal(await page.evaluate(`olderFixture.navigate('messageFirst').status`), "moved");
    await turnsFrom(page, 1, "the start of the journal, after going to the first message");
    assert.equal(await page.evaluate(`document.querySelector('.load-more-bar')`), null);

    // All the messages at once, for a reader at the end of the latest turn: a counter and Stop while it reads,
    // then the whole session, and the reader is still at its end.
    await page.evaluate(`olderFixture.show({ sessionId: 'long' })`);
    await turnsFrom(page, 9, "the latest turn of the other session");
    await page.evaluate(`olderFixture.hold()`);
    await page.evaluate(`document.querySelector('.load-more').click()`);
    await page.until(`document.querySelector('.load-more-bar > .load-more-progress')`, "the counter of the read");
    assert.match(await page.evaluate<string>(`document.querySelector('.load-more-bar > .load-more-progress').textContent`), /^Loading previous messages… \d+ loaded$/);
    assert.equal(await page.evaluate(`[...document.querySelectorAll('.load-more-bar button')].map(button => button.textContent).join()`), "Stop");
    await page.evaluate(`olderFixture.release()`);
    await page.until(`${ready} && ${firstTurn} === 1 && !document.querySelector('.load-more-bar')`, "the whole session");
    await still(page);
    assert.equal(await page.evaluate(`document.querySelectorAll('.timeline-message').length`), 9 * 120);
    assert.equal(await page.evaluate(`${scroller}.dataset.following`), "true");
    assert.equal(await page.evaluate(atEnd), true);

    // A view that grows at the end of a timeline brings its content down: that is not the reader going up.
    await page.evaluate(`olderFixture.show({ sessionId: 'medium' })`);
    await turnsFrom(page, 7, "a latest turn a little longer than the view");
    const small = await page.evaluate<number>(`${scroller}.scrollTop`);
    assert.ok(small > 0 && small < 600, `the end of the turn is near its top: ${small}`);
    const grown = await page.evaluate<number>(reads);
    await page.resize(1100, 950);
    await page.until(`${scroller}.scrollTop < ${small}`, "the larger view");
    await still(page);
    await pause(600);
    assert.equal(await page.evaluate(reads), grown);
    assert.equal(await page.evaluate(firstTurn), 7);
  }, options);
});
