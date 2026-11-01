import assert from "node:assert/strict";
import test from "node:test";
import { edge, withCanvas } from "./statistics/browserHarness";

const options = { entry: "../messageLineBreaks.mount.tsx", fixture: "lineBreaks", width: 900, height: 600 } as const;

test("a line of a message follows the one above it, and a paragraph stands further away than a line", { skip: !edge, timeout: 60_000 }, async () => {
  await withCanvas(async page => {
    await page.until("window.lineBreaks", "the page is ready");
    for (const category of ["user", "assistant"]) {
      await page.evaluate(`lineBreaks.show(${JSON.stringify(category)}, "Test1\\nTest2\\n\\nTest3\\nTest4")`);
      await page.until("!!document.querySelector('.markdown-content p')", "the message is shown");
      const [one, two, three, four] = await page.evaluate<number[]>("lineBreaks.wordTops('Test1', 'Test2', 'Test3', 'Test4')");
      const line = await page.evaluate<number>("parseFloat(getComputedStyle(document.querySelector('.markdown-content p')).lineHeight)");
      // One line down, not two: a line break of the text is not also kept as a line of its own.
      assert.ok(Math.abs(two - one - line) < 1, `${category}: the second line is ${two - one}px under the first, a line is ${line}px`);
      assert.ok(Math.abs(four - three - line) < 1, `${category}: the fourth line is ${four - three}px under the third, a line is ${line}px`);
      assert.ok(three - two > two - one, `${category}: the paragraph is ${three - two}px under the line above it, and a line ${two - one}px`);
    }
    // A paragraph that is plain text, not Markdown, still keeps the lines of its text.
    await page.evaluate(`lineBreaks.show("assistant", "text", "one\\ntwo")`);
    assert.equal(await page.evaluate("getComputedStyle(document.querySelector('.timeline-summary')).whiteSpaceCollapse"), "preserve");
  }, options);
});
