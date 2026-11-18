import assert from "node:assert/strict";
import test from "node:test";
import { edge, withCanvas } from "./statistics/browserHarness";
import { testLimit } from "./testTime";

// The timeline of a session in headless Edge, with a journal of 1,080 messages read whole: the rows far from the view
// rest, a box of their height in their place, and are drawn again where the reader goes. The timeline keeps its length
// and its position, and what looks a row up in the document (the navigation between messages, a selection of everything)
// reaches the rows at rest.
const options = { entry: "../olderHistory.mount.tsx", fixture: "olderFixture", rpcFixture: "../demo-api.ts", width: 1100, height: 800 } as const;
const scroller = `document.querySelector('.timeline-scroll')`;
const rows = `document.querySelector('.messages').children.length`;
const atRest = `document.querySelectorAll('.messages > .timeline-rest').length`;
const drawn = `document.querySelectorAll('.timeline-message').length`;
/** The rows at rest that the view shows a part of. */
const restInView = `(() => { const view = ${scroller}.getBoundingClientRect();
  return [...document.querySelectorAll('.messages > .timeline-rest')].filter(row => { const box = row.getBoundingClientRect(); return box.bottom > view.top && box.top < view.bottom; }).length; })()`;
/** The message at the top of the view: its journal offset and how far below the top it is. */
const topMessage = `(() => { const view = ${scroller}.getBoundingClientRect();
  const row = [...document.querySelectorAll('.timeline-message[data-row-offset]')].find(row => row.getBoundingClientRect().bottom > view.top + 1);
  return { offset: row?.dataset.rowOffset ?? null, top: row ? Math.round(row.getBoundingClientRect().top - view.top) : null, text: row?.textContent.includes('turn') ?? false }; })()`;
const frames = `new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(() => setTimeout(() => resolve(true), 80))))`;

test("the rows far from the view rest at their height and are drawn again where the reader goes", { skip: !edge, timeout: testLimit(120_000) }, async () => {
  await withCanvas(async page => {
    await page.until(`document.querySelector('.history[data-window-ready="true"]') && ${drawn} > 0`, "the latest turn");
    await page.evaluate(`document.querySelector('.load-more').click()`);
    await page.until(`!document.querySelector('.load-more-bar') && ${rows} === 1080`, "the whole session");
    await page.until(`${atRest} > 900`, "the rows far from the view at rest");
    const resting = await page.evaluate<{ drawn: number; height: number; top: number }>(`({ drawn: ${drawn}, height: ${scroller}.scrollHeight, top: ${scroller}.scrollTop })`);
    assert.ok(resting.drawn < 180, `${resting.drawn} messages are drawn`);
    assert.equal(await page.evaluate(`${scroller}.dataset.following`), "true", "the reader is still at the end");

    // The timeline is as long as with every row drawn, and the reader is where they were.
    const whole = await page.evaluate<{ drawn: number; height: number; top: number }>(`(() => {
      document.querySelector('.messages').dispatchEvent(new CustomEvent('codealta:wake-rows', { detail: { hold: 0, later: false } }));
      return { drawn: ${drawn}, height: ${scroller}.scrollHeight, top: ${scroller}.scrollTop }; })()`);
    assert.equal(whole.drawn, 1080);
    assert.ok(Math.abs(whole.height - resting.height) <= 1, `the timeline is ${resting.height} px long with rows at rest and ${whole.height} px with every row drawn`);
    assert.ok(Math.abs(whole.top - resting.top) <= 1);
    await page.until(`${atRest} > 900`, "the rows at rest again");

    // The reader goes to the middle of the session: the rows there are drawn, and stay where they are while others rest.
    await page.evaluate(`(() => { const view = ${scroller}; view.dispatchEvent(new WheelEvent('wheel', { deltaY: -100, bubbles: true })); view.scrollTop = Math.round(view.scrollHeight / 2); })()`);
    await page.until(`${restInView} === 0 && ${topMessage}.text`, "the rows of the middle of the session drawn");
    const middle = await page.evaluate<{ offset: string; top: number }>(topMessage);
    await page.evaluate(frames);
    await page.evaluate(frames);
    assert.deepEqual(await page.evaluate(topMessage), { ...middle, text: true }, "the message at the top of the view did not move");
    assert.ok(await page.evaluate<number>(atRest) > 800, "the rows far from the middle rest");
    assert.ok(Math.abs(await page.evaluate<number>(`${scroller}.scrollHeight`) - resting.height) <= 1, "the timeline kept its length");

    // A jump far from the rows that are drawn (the scrollbar, the Home key) shows no empty place: the rows there are
    // drawn in the frame of the scroll.
    for (const top of [9000, 61000, 30000]) {
      assert.equal(await page.evaluate(`new Promise(resolve => { const view = ${scroller}; view.scrollTop = ${top};
        requestAnimationFrame(() => requestAnimationFrame(() => resolve(${restInView}))); })`), 0, `rows at rest in view after a jump to ${top}`);
    }
    await page.evaluate(`${scroller}.scrollTop = Math.round(${scroller}.scrollHeight / 2)`);
    await page.until(`${restInView} === 0`, "the middle of the session again");

    // The navigation between messages goes through rows at rest: each step shows the message before, with its text.
    for (let step = 0; step < 3; step++) {
      const result = await page.evaluate<{ status: string; label?: string }>(`olderFixture.navigate('messagePrevious')`);
      assert.equal(result.status, "moved");
      assert.match(result.label ?? "", /turn \d+/);
    }
    // To the first message of the session, hundreds of rows at rest away.
    const first = await page.evaluate<{ status: string; label?: string }>(`olderFixture.navigate('messageFirst')`);
    assert.equal(first.status, "moved");
    assert.match(first.label ?? "", /Prompt of turn 1$/);
    await page.until(`${restInView} === 0`, "the first rows drawn");
    assert.equal((await page.evaluate<{ top: number }>(topMessage)).top, 0, "the first message is at the top of the view");
    // A turn asked for by its place in the journal, as a bookmark asks, is shown at the top too.
    assert.equal(await page.evaluate(`olderFixture.showTurn(5)`), true);
    await page.until(`${restInView} === 0 && [...document.querySelectorAll('.timeline-message')].some(row => row.textContent.includes('Prompt of turn 5'))`, "the prompt of turn 5 drawn");
    assert.equal(await page.evaluate(`(() => { const view = ${scroller}.getBoundingClientRect(); const row = [...document.querySelectorAll('.timeline-message')].find(row => row.textContent.includes('Prompt of turn 5'));
      return Math.round(row.getBoundingClientRect().top - view.top); })()`), 8);

    // A selection of everything takes the text of every row.
    await page.evaluate(`${scroller}.focus()`);
    await page.command("Input.dispatchKeyEvent", { type: "rawKeyDown", key: "a", code: "KeyA", windowsVirtualKeyCode: 65, modifiers: 2 });
    await page.command("Input.dispatchKeyEvent", { type: "keyUp", key: "a", code: "KeyA", windowsVirtualKeyCode: 65, modifiers: 2 });
    await page.until(`${atRest} === 0 && ${drawn} === 1080`, "every row drawn for the selection");
    assert.deepEqual(page.problems, []);
  }, options);
});
