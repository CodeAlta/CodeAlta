import assert from "node:assert/strict";
import test from "node:test";
import { edge, withCanvas } from "../statistics/browserHarness";

// The bookmarks of a timeline in headless Edge, under the production content security policy and React StrictMode: the
// button of a message, the rail beside the timeline, and going to a message that the window does not hold yet.

const options = { entry: "../bookmarks/timelineBookmarks.mount.tsx", fixture: "timelineFixture", rpcFixture: "../demo-api.ts", styles: ["../bookmarks/bookmarks.css"], width: 1100, height: 800 } as const;
const ready = `document.querySelector('.history[data-window-ready="true"]') && document.querySelectorAll('.timeline-message').length > 0`;
const rowOf = (contentId: string) => `document.querySelector('[data-row-offset="' + timelineFixture.journal.find(entry => entry.contentId === ${JSON.stringify(contentId)}).offset + '"]')`;
const marks = `[...document.querySelectorAll('.bookmark-rail-mark')]`;

test("a message is bookmarked from its row, its mark joins the rail, and removing it takes both away", { skip: !edge, timeout: 120_000 }, async () => {
  await withCanvas(async page => {
    await page.until(ready, "the latest turn");
    assert.equal(await page.evaluate(`document.querySelectorAll('.timeline-message').length`), 20, "the window holds the last turn");
    await page.until(`${marks}.length === 1`, "the mark of the bookmark that was kept");
    assert.equal(await page.evaluate(`document.querySelectorAll('[data-bookmarked="true"]').length`), 0, "its message is not in the window");
    assert.equal(await page.evaluate(`${marks}[0].getAttribute('aria-label')`), "Go to the bookmark: The old decision");

    // The button of a row is drawn under the pointer; pressing it bookmarks the message.
    const answer = rowOf("msg:40:19");
    assert.equal(await page.evaluate(`getComputedStyle(${answer}.querySelector('.bookmark-message')).opacity`), "0");
    await page.click(`[data-row-offset="${await page.evaluate<string>(`${answer}.dataset.rowOffset`)}"] .bookmark-message`);
    await page.until(`${answer}.dataset.bookmarked === 'true'`, "the bookmarked row");
    assert.equal(await page.evaluate(`${answer}.querySelector('.bookmark-message').getAttribute('aria-pressed')`), "true");
    assert.equal(await page.evaluate(`${answer}.querySelector('.bookmark-message').getAttribute('aria-label')`), "Remove bookmark");
    await page.until(`${marks}.length === 2`, "its mark in the rail");
    // The rail follows the conversation: the message of the third turn first.
    assert.deepEqual(await page.evaluate(`${marks}.map(mark => mark.getAttribute('aria-label'))`), ["Go to the bookmark: The old decision", "Go to the bookmark: Answer 19 of turn 40. It says something about step 19."]);
    await page.until(`${marks}[1].dataset.inView === 'true' && !${marks}[0].dataset.inView`, "the mark of the message in view");
    // A bookmarked message keeps its button drawn when the pointer is elsewhere.
    await page.command("Input.dispatchMouseEvent", { type: "mouseMoved", x: 5, y: 5 });
    await page.until(`getComputedStyle(${answer}.querySelector('.bookmark-message')).opacity === '1'`, "the button of a bookmarked message");
    // The rail is beside the timeline, inside its area, and does not cover the text of the messages.
    const place = await page.evaluate<{ inside: boolean; clear: boolean }>(`(() => {
      const rail = document.querySelector('.bookmark-rail').getBoundingClientRect(), area = document.querySelector('.session-timeline-area').getBoundingClientRect();
      const body = ${answer}.querySelector('.message-body').getBoundingClientRect();
      return { inside: rail.left >= area.left && rail.right <= area.right && rail.top >= area.top && rail.bottom <= area.bottom, clear: rail.left >= body.right - 1 };
    })()`);
    assert.deepEqual(place, { inside: true, clear: true });

    // Pressing the button again removes the bookmark and its mark.
    await page.click(`[data-row-offset="${await page.evaluate<string>(`${answer}.dataset.rowOffset`)}"] .bookmark-message`);
    await page.until(`!${answer}.dataset.bookmarked && ${marks}.length === 1`, "the bookmark removed");
    assert.deepEqual(await page.evaluate(`timelineFixture.state.calls.map(call => call.split(':')[0])`), ["add", "remove"]);
  }, options);
});

test("a mark of the rail goes to its message: older history is read until the window holds it, then the message is shown", { skip: !edge, timeout: 120_000 }, async () => {
  await withCanvas(async page => {
    await page.until(ready, "the latest turn");
    await page.until(`${marks}.length === 1`, "the mark of the old bookmark");
    const reads = await page.evaluate<number>(`timelineFixture.state.reads.length`);

    // The card of the mark says what it leads to.
    const point = await page.evaluate<{ x: number; y: number }>(`(() => { const box = ${marks}[0].getBoundingClientRect(); return { x: box.left + box.width - 4, y: box.top + box.height / 2 }; })()`);
    await page.command("Input.dispatchMouseEvent", { type: "mouseMoved", x: point.x, y: point.y });
    await page.until(`document.querySelector('.bookmark-rail-card')`, "the card of the mark");
    assert.equal(await page.evaluate(`document.querySelector('.bookmark-rail-card strong').textContent`), "The old decision");
    assert.equal(await page.evaluate(`document.querySelector('.bookmark-rail-card-kind').textContent`), "Assistant message");
    assert.equal(await page.evaluate(`${marks}[0].hasAttribute('aria-haspopup')`), false, "the card is no menu");
    // A timeline that goes behind another tab while the pointer is on a mark leaves no card behind.
    await page.evaluate(`document.querySelector('.session-workspace').style.display = 'none'`);
    await page.until(`!document.querySelector('.bookmark-rail-card')`, "the card gone with its timeline");
    await page.evaluate(`document.querySelector('.session-workspace').style.display = 'flex'`);
    await page.until(`document.querySelector('.timeline-scroll').clientWidth > 0`, "the timeline again");

    await page.click('.bookmark-rail-mark');
    await page.until(`timelineFixture.state.outcomes.length === 1`, "the end of the request");
    assert.deepEqual(await page.evaluate(`timelineFixture.state.outcomes`), ["shown"]);
    assert.ok(await page.evaluate<number>(`timelineFixture.state.reads.length`) > reads + 3, "older history was read, a chunk of turns at a time");
    const target = rowOf("msg:3:5");
    // The message is at the top of the timeline, bookmarked, and pointed out.
    const shown = await page.evaluate<{ top: number; bookmarked: string; revealed: string | null; following: boolean }>(`(() => {
      const scroll = document.querySelector('.timeline-scroll'), row = ${target};
      return { top: Math.round(row.getBoundingClientRect().top - scroll.getBoundingClientRect().top), bookmarked: row.dataset.bookmarked, revealed: row.getAttribute('data-revealed'),
        following: scroll.scrollTop >= scroll.scrollHeight - scroll.clientHeight - 2 };
    })()`);
    assert.deepEqual(shown, { top: 8, bookmarked: "true", revealed: "true", following: false });
    await page.until(`${marks}[0].dataset.inView === 'true'`, "the mark of the message in view");
    assert.equal(await page.evaluate(`timelineFixture.reveals.forSession('session')`), null, "the request is ended");
    // The timeline stays where the reader was taken: nothing brings it back to its end.
    await new Promise(resolve => setTimeout(resolve, 700));
    assert.equal(await page.evaluate(`Math.round(${target}.getBoundingClientRect().top - document.querySelector('.timeline-scroll').getBoundingClientRect().top)`), 8);
    await page.until(`!${target}.hasAttribute('data-revealed')`, "the mark of the row fading away");

    // Stepping: nothing before the only bookmark in view; a new one further down is the next.
    assert.equal(await page.evaluate(`timelineFixture.step(-1)`), false);
    const later = await page.evaluate<string>(`timelineFixture.journal.find(entry => entry.contentId === 'user:30').offset`);
    await page.evaluate(`timelineFixture.hub.add('session', ${JSON.stringify(later)})`);
    await page.until(`${marks}.length === 2`, "the second bookmark");
    assert.equal(await page.evaluate(`timelineFixture.step(1)`), true);
    await page.until(`timelineFixture.state.outcomes.length === 2`, "the step");
    assert.equal(await page.evaluate(`Math.round(${rowOf("user:30")}.getBoundingClientRect().top - document.querySelector('.timeline-scroll').getBoundingClientRect().top)`), 8);
    assert.equal(await page.evaluate(`timelineFixture.step(1)`), false, "nothing after the last bookmark");
    assert.equal(await page.evaluate(`timelineFixture.step(-1)`), true);
    await page.until(`timelineFixture.state.outcomes.length === 3`, "the step back");
    assert.equal(await page.evaluate(`Math.round(${target}.getBoundingClientRect().top - document.querySelector('.timeline-scroll').getBoundingClientRect().top)`), 8);

    // The keyboard moves through the marks of the rail, and Enter goes to one.
    await page.evaluate(`${marks}[0].focus()`);
    await page.key("ArrowDown", 40);
    assert.equal(await page.evaluate(`document.activeElement === ${marks}[1]`), true);
    await page.key("Enter", 13, "\r");
    await page.until(`timelineFixture.state.outcomes.length === 4`, "the mark chosen with the keyboard");
    assert.deepEqual(await page.evaluate(`timelineFixture.state.outcomes`), ["shown", "shown", "shown", "shown"]);

    // A message that the journal does not hold at the offset it is asked at: the request ends, and says so.
    await page.evaluate(`timelineFixture.reveals.open('session', '1050')`);
    await page.until(`timelineFixture.state.outcomes.length === 5`, "a request for nothing");
    assert.equal(await page.evaluate(`timelineFixture.state.outcomes[4]`), "missing");
  }, options);
});
