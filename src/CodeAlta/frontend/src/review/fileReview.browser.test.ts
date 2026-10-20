import assert from "node:assert/strict";
import test from "node:test";
import { browserExecutable } from "../browserTarget";
import { ctrl, openReviewPage, shift, type Point } from "./reviewBrowserHarness";

const edge = browserExecutable;

// The review of a Markdown file as the window shows it (FileReview, rendered, over a reviews hub whose host is in
// memory), commented block by block with the mouse and the keys; then a review request of an agent in the ask panel
// of its session, answered with a verdict.
test("a Markdown file is reviewed block by block, and a review request is answered with a verdict", { skip: !edge, timeout: 180_000 }, async t => {
  const page = await openReviewPage(new URL("./fileReview.mount.tsx", import.meta.url), `window.fileReviewFixture && !!document.querySelector("#file .markdown-content [data-source-lines]")`,
    new URL("./fileReview.neoastra.mount.ts", import.meta.url));
  const { evaluate, value, until, frames, mouse, click, clickOn, key, type } = page;
  const f = "fileReviewFixture";
  try {
    const add = `document.querySelector("#file .review-block-gutter .review-block-add")`;
    const composer = `document.querySelector("#file .review-block-host .review-editor textarea")`;
    const added = () => value<Record<string, unknown>[]>(`${f}.fileServer.asked.filter(entry => entry.call === "add")`);
    // The middle of a block, a little to the right of its start, where the pointer hovers it.
    const inside = (element: string) => value<Point>(`(box => ({ x: Math.round(box.left + 20), y: Math.round(box.top + Math.min(box.height / 2, 8)) }))((${element}).getBoundingClientRect())`);
    const block = (lines: string) => `${f}.block("${lines}")`;
    const away = () => mouse("mouseMoved", { x: 1150, y: 1080 });
    // Hovers a block and waits for its + at its top.
    const hover = async (lines: string) => {
      await away();
      await until(`!${add}`);
      await mouse("mouseMoved", await inside(block(lines)));
      await until(`!!${add} && Math.abs(${add}.getBoundingClientRect().top - ${block(lines)}.getBoundingClientRect().top) <= 1`,
        `[${add}?.getBoundingClientRect().top, ${block(lines)}.getBoundingClientRect().top]`);
    };
    const blocks = await value<string[]>(`[...document.querySelectorAll("#file .markdown-content [data-source-lines]")].map(node => node.tagName + " " + node.getAttribute("data-source-lines"))`);
    assert.deepEqual(blocks, ["TABLE 1-3", "H1 5-5", "P 7-7", "LI 9-9", "LI 10-10", "TR 12-12", "TR 14-14", "PRE 16-18", "P 22-22"]);

    await t.test("hovering a block shows its + at its top; a click opens the composer, and Ctrl+Enter writes a thread on the block's lines", async () => {
      await hover("7-7");
      await click(await inside(add));
      await until(`document.activeElement === ${composer} && !!${composer}`);
      assert.equal(await value(`${composer}.getAttribute("aria-label")`), "Comment on line 7 of plan.md");
      // The composer is right after the block, which is marked as the one being commented.
      assert.equal(await value(`${block("7-7")}.nextElementSibling.querySelector(".review-editor") !== null`), true);
      assert.equal(await value(`${block("7-7")}.hasAttribute("data-review-range")`), true);
      await type("Say why");
      await key("Enter", ctrl);
      await until(`!!document.querySelector('#file [data-thread-id="c1"]')`);
      assert.deepEqual(await added(), [{ call: "add", path: "plan.md", startLine: 7, endLine: 7, body: "Say why", suggestion: null }]);
      await until(`!${composer}`);
      assert.equal(await value(`${f}.before("c1")`), "P 7-7");
      assert.equal(await value(`${block("7-7")}.hasAttribute("data-review-range")`), false);
      // A bar along the commented block, in the gutter.
      await until(`[...document.querySelectorAll("#file .review-block-bar[data-kind=draft]")].some(bar => Math.abs(bar.getBoundingClientRect().top - ${block("7-7")}.getBoundingClientRect().top) <= 1)`);
    });

    await t.test("a Shift-click on another block's + makes a range that covers both, and its card follows the table of its last line", async () => {
      await hover("9-9");
      await click(await inside(add));
      await until(`${composer}?.getAttribute("aria-label") === "Comment on line 9 of plan.md"`);
      await hover("14-14");
      await click(await inside(add), shift);
      await until(`${composer}?.getAttribute("aria-label") === "Comment on lines 9 to 14 of plan.md"`, `${composer}?.getAttribute("aria-label")`);
      // Every block of the range is marked; the composer goes after the table.
      assert.deepEqual(await value(`[...document.querySelectorAll("#file [data-review-range]")].map(node => node.getAttribute("data-source-lines"))`), ["9-9", "10-10", "12-12", "14-14"]);
      assert.equal(await value(`${composer}.closest(".review-block-host").previousElementSibling.tagName`), "TABLE");
      // The composer of the range keeps the keyboard.
      await until(`document.activeElement === ${composer}`);
      await type("The list and the table");
      await key("Enter", ctrl);
      await until(`!!document.querySelector('#file [data-thread-id="c2"]')`);
      assert.deepEqual((await added()).at(-1), { call: "add", path: "plan.md", startLine: 9, endLine: 14, body: "The list and the table", suggestion: null });
      assert.equal(await value(`${f}.before("c2")`), "TABLE");
    });

    await t.test("Delete block writes a suggestion with no text in one click", async () => {
      await hover("5-5");
      await click(await inside(add));
      await until(`!!${composer}`);
      await clickOn(`[...document.querySelectorAll("#file .review-block-host .review-editor button")].find(button => button.textContent === "Delete block")`);
      await until(`!!document.querySelector('#file [data-thread-id="c3"]')`);
      assert.deepEqual((await added()).at(-1), { call: "add", path: "plan.md", startLine: 5, endLine: 5, body: "", suggestion: "" });
      assert.equal(await value(`${f}.before("c3")`), "H1 5-5");
      assert.equal(await value(`document.querySelector('#file [data-thread-id="c3"] .review-suggestion')?.getAttribute("aria-label")`), "Suggestion: delete these lines");
      assert.deepEqual(await value(`[...document.querySelectorAll('#file [data-thread-id="c3"] .review-suggestion-line')].map(line => line.dataset.kind)`), ["removed"]);
    });

    await t.test("the cards stay after their blocks when the document is rendered again", async () => {
      // Text added at the end: the threads keep their lines, the document is drawn again around them.
      await evaluate(`${f}.setFile(${f}.plan + "\\n\\nAppended paragraph.")`);
      await clickOn(`document.querySelector('#file button[aria-label="Reload"]')`);
      await until(`!!${block("24-24")}`);
      await frames();
      assert.deepEqual(await value(`["c1", "c2", "c3"].map(${f}.before)`), ["P 7-7", "TABLE", "H1 5-5"]);
      // A paragraph inserted above them: the host moves the threads two lines down, and the cards follow their blocks.
      await evaluate(`${f}.setFile(${f}.plan.replace("# Plan\\n", "Intro paragraph.\\n\\n# Plan\\n"))`);
      await clickOn(`document.querySelector('#file button[aria-label="Reload"]')`);
      await until(`!!${block("5-5")} && ${block("5-5")}.textContent === "Intro paragraph."`);
      await until(`${f}.fileServer.threads.map(thread => thread.startLine).join() === "9,11,7"`, `${f}.fileServer.threads.map(thread => thread.startLine)`);
      await until(`["c1", "c2", "c3"].map(${f}.before).join() === "P 9-9,TABLE,H1 7-7"`, `["c1", "c2", "c3"].map(${f}.before)`);
      assert.equal(await value(`document.querySelectorAll("#file .review-block-host").length`), 3);
    });

    await t.test("raw HTML that forges a source line attribute is not a block of the document", async () => {
      // The paragraph the text writes in HTML.
      const spoof = `[...document.querySelectorAll("#file .markdown-content p")].find(node => node.textContent === "spoof")`;
      assert.equal(await value(`${spoof}?.hasAttribute("data-source-lines")`), false);
      await away();
      await until(`!${add}`);
      await mouse("mouseMoved", await inside(spoof));
      await frames();
      assert.equal(await value(`!!${add}`), false);
      // From a block to it, the + stays with the block.
      await hover("24-24");
      const top = await value<number>(`${add}.getBoundingClientRect().top`);
      await mouse("mouseMoved", await inside(spoof));
      await frames();
      assert.equal(await value(`${add}?.getBoundingClientRect().top`), top);
    });

    await t.test("a review request shows its file and its verdict, and Submit review answers with the verdict and the overall comment", async () => {
      await away();
      await evaluate(`${f}.state.show("ask")`);
      await until(`!!document.querySelector("#ask-form .ask-form") && !!document.querySelector('#ask-file [data-thread-id="c1"]')`,
        `[document.querySelector("#ask-form")?.innerHTML.slice(0, 200), document.querySelector("#ask-file")?.innerHTML.slice(0, 200)]`, 10_000);
      assert.equal(await value(`document.querySelector("#ask-form .ask-review-what").textContent`), "The plan");
      assert.equal(await value(`document.querySelector("#ask-form .ask-review-message").textContent.trim()`), "Please review the plan.");
      assert.deepEqual(await value(`[...document.querySelectorAll("#ask-form .review-verdict strong")].map(node => node.textContent)`), ["Approve", "Request changes", "Comment"]);
      // A draft waits: changes are asked by default.
      assert.equal(await value(`document.querySelector("#ask-form .review-verdict input:checked").value`), "changes_requested");
      assert.match(await value<string>(`document.querySelector("#ask-form .ask-review-counts").textContent`), /1 comment to send/);
      assert.equal(await value(`document.querySelector("#ask-file .file-review strong").textContent`), "plan.md");

      await clickOn(`document.querySelector('#ask-form .review-verdict[data-verdict="approved"]')`);
      await until(`document.querySelector("#ask-form .review-verdict input:checked")?.value === "approved"`);
      await clickOn(`document.querySelector('#ask-form textarea[aria-label="Overall comment (optional)"]')`);
      await type("Looks good, go on.");
      await until(`document.querySelector('#ask-form textarea[aria-label="Overall comment (optional)"]').value === "Looks good, go on."`);
      assert.equal(await value(`document.querySelector("#ask-form .ask-answer").textContent`), "Submit review");
      await clickOn(`document.querySelector("#ask-form .ask-answer")`);
      await until(`${f}.host.answers.length === 1`, `${f}.host.answers`);
      const answer = await value<{ expectedHostEpoch: string; action: { answers: unknown[]; review: unknown; handle: { askId: string } } }>(`${f}.host.answers[0]`);
      assert.deepEqual(answer.action.review, { verdict: "approved", overall: "Looks good, go on." });
      assert.deepEqual(answer.action.answers, []);
      assert.equal(answer.action.handle.askId, "44444444-4444-4444-8444-444444444444");
      assert.equal(answer.expectedHostEpoch, "11111111-1111-4111-8111-111111111111");
      assert.deepEqual(await value(`${f}.host.cancels.length`), 0);
    });

    assert.deepEqual(page.errors, []);
  } finally {
    await page.close();
  }
});
