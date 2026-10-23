import assert from "node:assert/strict";
import test from "node:test";
import { edge, withCanvas, type Page } from "./statistics/browserHarness";

// A triple click in a card of the timeline, in headless Edge, with the selection of Chromium and with the one of WebKit.

const options = { entry: "../paragraphSelection.mount.ts", fixture: "paragraphFixture", width: 900, height: 600 } as const;
const selection = `(() => { const range = getSelection().getRangeAt(0), card = node => (node.nodeType === 1 ? node : node.parentElement).closest('article');
  return { text: getSelection().toString(), sameCard: card(range.endContainer) === card(range.startContainer), inParagraph: document.getElementById('last').contains(range.endContainer) }; })()`;

async function tripleClick(page: Page, selector: string) {
  const { x, y } = await page.evaluate<{ x: number; y: number }>(`(() => { const box = document.querySelector('${selector}').getBoundingClientRect(); return { x: box.x + 8, y: box.y + box.height / 2 }; })()`);
  await page.command("Input.dispatchMouseEvent", { type: "mouseMoved", x, y });
  for (let clickCount = 1; clickCount <= 3; clickCount++) {
    await page.command("Input.dispatchMouseEvent", { type: "mousePressed", x, y, button: "left", buttons: 1, clickCount });
    await page.command("Input.dispatchMouseEvent", { type: "mouseReleased", x, y, button: "left", buttons: 0, clickCount });
  }
}

test("a triple click on the last paragraph of a card selects that paragraph only", { skip: !edge, timeout: 120_000 }, async () => {
  await withCanvas(async page => {
    await tripleClick(page, "#last");
    assert.deepEqual(await page.evaluate(selection), { text: "The last paragraph of the card.", sameCard: true, inParagraph: true }, "Chromium");

    // WebKit ends the selection at the start of the next card, and paints everything between as selected.
    await page.evaluate(`getSelection().removeAllRanges(); paragraphFixture.state.webKit = true`);
    await tripleClick(page, "#last");
    await page.until(`getSelection().toString() === "The last paragraph of the card."`, "the paragraph alone");
    assert.deepEqual(await page.evaluate(selection), { text: "The last paragraph of the card.", sameCard: true, inParagraph: true }, "WebKit");
  }, options);
});

test("a range that runs past the paragraph it starts in ends with it", { skip: !edge, timeout: 120_000 }, async () => {
  await withCanvas(async page => {
    const keep = (start: string, end: string) => page.evaluate(`paragraphFixture.keep(${start}, ${end})`);
    const next = `[document.querySelector('#next strong').firstChild, 0]`;
    assert.deepEqual(await keep(`[document.querySelector('#last strong').firstChild, 1]`, next), { changed: true, text: "ast paragraph of the card." },
      "from inside an inline element, to the next card");
    assert.deepEqual(await keep(`[document.getElementById('first').firstChild, 0]`, `[document.getElementById('code').firstChild, 0]`), { changed: true, text: "The first paragraph." },
      "to the next paragraph of the same card");
    assert.deepEqual(await keep(`[document.getElementById('code').firstChild, 0]`, `[document.getElementById('code').firstChild, 9]`), { changed: false, text: "line one\n" },
      "a line of a code block ends inside its block");
  }, options);
});
