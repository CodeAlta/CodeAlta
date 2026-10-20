import assert from "node:assert/strict";
import test from "node:test";
import { browserExecutable } from "../browserTarget";
import { ctrl, openReviewPage, shift, type Point } from "./reviewBrowserHarness";

const edge = browserExecutable;

// The comments of a code editor, as the window draws them: a real Monaco editor, its comment layer and a reviews hub
// over a host in memory, driven with the mouse and the keys of the page. The card of a thread is checked alone after.
test("line comments are written, edited and deleted in the view zones of the code editor", { skip: !edge, timeout: 180_000 }, async t => {
  const page = await openReviewPage(new URL("./lineComments.mount.tsx", import.meta.url), "window.lineCommentsFixture && lineCommentsFixture.state.editor");
  const { evaluate, value, until, frames, mouse, click, clickOn, key, type } = page;
  const f = "lineCommentsFixture";
  try {
    const glyph = (line: number) => value<Point>(`${f}.glyph(${line})`);
    const lineTop = (line: number) => value<number | null>(`${f}.lineTop(${line})`);
    const zoneTop = (id: string | null) => value<number | null>(`${f}.zoneTop(${id === null ? "null" : JSON.stringify(id)})`);
    const composer = `document.querySelector("#editor .review-zone .review-editor textarea")`;
    const composerFocused = `document.activeElement === ${composer} && !!document.activeElement`;
    const focusShown = `[document.activeElement?.tagName, document.activeElement?.className, document.activeElement?.getAttribute("aria-label"), !!${composer}, ${composer}?.offsetParent?.className]`;
    const added = () => value<Record<string, unknown>[]>(`${f}.server.asked.filter(entry => entry.call === "add")`);
    // A zone is under its line: its top is the bottom of the line (20 pixels high).
    const under = async (id: string | null, line: number) => {
      await until(`${f}.zoneTop(${id === null ? "null" : JSON.stringify(id)}) !== null && ${f}.zoneTop(${id === null ? "null" : JSON.stringify(id)}) === ${f}.lineTop(${line}) + 20`,
        `[${f}.zoneTop(${id === null ? "null" : JSON.stringify(id)}), ${f}.lineTop(${line})]`);
    };

    await t.test("hovering the glyph margin of a line shows a + there, and leaving the editor takes it away", async () => {
      await mouse("mouseMoved", await glyph(5));
      await until(`!!document.querySelector("#editor .review-glyph-add")`);
      const box = await value<{ top: number; height: number }>(`(box => ({ top: Math.round(box.top), height: Math.round(box.height) }))(document.querySelector("#editor .review-glyph-add").getBoundingClientRect())`);
      assert.equal(box.top, await lineTop(5));
      assert.ok(box.height > 0);
      await mouse("mouseMoved", await glyph(9));
      await until(`Math.round(document.querySelector("#editor .review-glyph-add")?.getBoundingClientRect().top) === ${f}.lineTop(9)`);
      await mouse("mouseMoved", { x: 1100, y: 1050 });
      await until(`!document.querySelector("#editor .review-glyph-add")`);
    });

    await t.test("a click on the glyph margin opens the composer under the line, and Ctrl+Enter writes a draft there", async () => {
      await click(await glyph(5));
      await until(composerFocused, focusShown);
      await under(null, 5);
      assert.equal(await value(`${composer}.getAttribute("aria-label")`), "Comment on line 5 of file.md");
      await type("First comment");
      await key("Enter", ctrl);
      await until(`${f}.thread("c1") !== null && !!document.querySelector('#editor [data-thread-id="c1"]')`);
      assert.deepEqual(await added(), [{ call: "add", path: "docs/file.md", startLine: 5, endLine: 5, body: "First comment", suggestion: null }]);
      await until(`!document.querySelector("#editor .review-zone .review-editor")`);
      await under("c1", 5);
      assert.equal(await value(`document.querySelector('[data-thread-id="c1"] .review-card-body').textContent.trim()`), "First comment");
      assert.equal(await value(`document.querySelector('[data-thread-id="c1"] .review-chip').textContent`), "draft");
      // The keyboard is back in the editor, on the line after the comment.
      await until(`${f}.state.editor.hasTextFocus() && ${f}.state.editor.getPosition().lineNumber === 6`);
    });

    await t.test("a click and then a Shift-click on the glyph margin comment the range of lines between them", async () => {
      await click(await glyph(10));
      await until(composerFocused, focusShown);
      await under(null, 10);
      await click(await glyph(12), shift);
      await until(`${composer}?.getAttribute("aria-label") === "Comment on lines 10 to 12 of file.md"`, `${composer}?.getAttribute("aria-label")`);
      await under(null, 12);
      await until(composerFocused, focusShown);
      // The lines being commented are marked.
      assert.equal(await value(`document.querySelectorAll("#editor .review-line-range").length > 0`), true);
      await type("Range");
      await key("Enter", ctrl);
      await until(`${f}.thread("c2") !== null && !!document.querySelector('#editor [data-thread-id="c2"]')`);
      assert.deepEqual((await added()).at(-1), { call: "add", path: "docs/file.md", startLine: 10, endLine: 12, body: "Range", suggestion: null });
      await under("c2", 12);
    });

    // A composer moved to other lines (a Shift-click, or a click on another line while one is open) has a new text box,
    // which takes the keyboard as it mounts: its zone is laid out under the new last line, never taken out of the page.
    await t.test("a composer moved by a Shift-click keeps the keyboard", async () => {
      try {
        await click(await glyph(15));
        await until(composerFocused, focusShown);
        await click(await glyph(17), shift);
        await until(`${composer}?.getAttribute("aria-label") === "Comment on lines 15 to 17 of file.md"`);
        await until(composerFocused, focusShown, 2000);
      } finally {
        await clickOn(`[...document.querySelectorAll("#editor .review-zone .review-editor button")].find(button => button.textContent === "Cancel")`);
        await until(`!document.querySelector("#editor .review-zone .review-editor")`);
      }
    });

    await t.test("a drag along the glyph margin comments the lines it went over, and Esc cancels and gives the keyboard back", async () => {
      await mouse("mouseMoved", await glyph(20));
      await mouse("mousePressed", await glyph(20));
      await mouse("mouseMoved", await glyph(21), 0, true);
      await mouse("mouseMoved", await glyph(23), 0, true);
      // The range follows the pointer before the button is released.
      await until(`document.querySelectorAll("#editor .review-line-range").length > 0`);
      await mouse("mouseReleased", await glyph(23));
      await until(`${composer}?.getAttribute("aria-label") === "Comment on lines 20 to 23 of file.md" && ${composerFocused}`, `${composer}?.getAttribute("aria-label")`);
      await under(null, 23);
      await key("Escape");
      await until(`!document.querySelector("#editor .review-zone .review-editor")`);
      await until(`${f}.state.editor.hasTextFocus() && ${f}.state.editor.getPosition().lineNumber === 20`, `${f}.state.editor.getPosition()`);
      assert.equal((await added()).length, 2, "nothing written");
      assert.equal(await value(`${f}.zones()`), 2);
    });

    await t.test("Suggest prefills the suggested text with the text of the lines", async () => {
      await click(await glyph(25));
      await until(composerFocused, focusShown);
      await click(await glyph(26), shift);
      await until(`${composer}?.getAttribute("aria-label") === "Comment on lines 25 to 26 of file.md"`);
      await under(null, 26);
      await clickOn(`[...document.querySelectorAll("#editor .review-zone .review-editor button")].find(button => button.textContent === "Suggest")`);
      await until(`document.querySelector("#editor .review-zone textarea.review-editor-code")?.value === "line 25\\nline 26"`,
        `document.querySelector("#editor .review-zone textarea.review-editor-code")?.value`);
      await until(`document.activeElement === document.querySelector("#editor .review-zone textarea.review-editor-code")`);
      await evaluate(`document.activeElement.select()`);
      await type("line twenty-five\nline 26");
      await key("Enter", ctrl);
      await until(`${f}.thread("c3") !== null`);
      assert.deepEqual((await added()).at(-1), { call: "add", path: "docs/file.md", startLine: 25, endLine: 26, body: "", suggestion: "line twenty-five\nline 26" });
      // The card shows the suggestion as a small diff of the lines.
      await until(`document.querySelectorAll('[data-thread-id="c3"] .review-suggestion-line').length === 3`);
      assert.deepEqual(await value(`[...document.querySelectorAll('[data-thread-id="c3"] .review-suggestion-line')].map(line => line.dataset.kind)`), ["removed", "added", "same"]);
      await evaluate(`${f}.server.api.delete({ expectedEpoch: "epoch", projectId: "p", checkout: null, id: "c3" })`);
      await until(`!document.querySelector('[data-thread-id="c3"]')`);
    });

    await t.test("Edit of a draft changes its text, and Delete with its confirmation removes it with its zone", async () => {
      const card = (id: string) => `document.querySelector('#editor [data-thread-id="${id}"]')`;
      const button = (id: string, text: string, intent = "") => `[...${card(id)}.querySelectorAll("button${intent}")].find(button => button.textContent === "${text}")`;
      await clickOn(button("c1", "Edit"));
      await until(`${card("c1")}.querySelector("textarea")?.value === "First comment"`);
      await evaluate(`${card("c1")}.querySelector("textarea").focus(); ${card("c1")}.querySelector("textarea").select()`);
      await type("First comment, edited");
      await key("Enter", ctrl);
      await until(`${f}.thread("c1").body === "First comment, edited" && ${card("c1")}.querySelector(".review-card-body")?.textContent.trim() === "First comment, edited"`,
        `${f}.thread("c1").body`);
      assert.equal(await value(`${card("c1")}.querySelector("textarea")`), null, "the editor is closed");

      const zones = await value<number>(`${f}.zones()`);
      await clickOn(button("c2", "Delete"));
      await until(`!!${button("c2", "Keep")}`);
      await clickOn(button("c2", "Delete", ".bp6-intent-danger"));
      await until(`${f}.thread("c2") === null && !${card("c2")} && ${f}.zones() === ${zones - 1}`, `${f}.zones()`);
      // Line 13 is right under line 12 again.
      await until(`${f}.lineTop(13) === ${f}.lineTop(12) + 20`);
    });

    await t.test("the zones follow their threads when the text changes above them", async () => {
      await frames();
      await under("c1", 5);
      await evaluate(`${f}.setText("added a\\nadded b\\nadded c\\n" + ${f}.server.text)`);
      await until(`${f}.thread("c1").startLine === 8 && ${f}.state.editor.getModel().getLineContent(8) === "line 5"`);
      await under("c1", 8);
      assert.deepEqual(await value(`${f}.server.threads.map(thread => [thread.id, thread.startLine, thread.endLine])`), [["c1", 8, 8]]);
      // The commented line is marked where it is now.
      await until(`[...document.querySelectorAll("#editor .review-glyph-thread")].some(node => Math.round(node.getBoundingClientRect().top) === ${f}.lineTop(8))`);
    });

    await t.test("a card alone: its chip, its replies, its suggestion and its name", async () => {
      await evaluate(`${f}.showCards([
        { state: "draft", original: "two\\nthree", suggestion: "two\\n3" },
        { state: "open", original: "two\\nthree", replies: [{ author: "agent", sessionId: "s1", body: "Done in the next turn", at: "2026-10-20T10:00:00Z" },
          { author: "user", sessionId: null, body: "Thanks", at: "2026-10-20T10:05:00Z" }] },
        { state: "addressed", original: "two\\nthree", startLine: 4, endLine: 4 },
        { state: "resolved", original: "two\\nthree" },
      ])`);
      await until(`document.querySelectorAll("#cards .review-thread").length === 4`);
      const cards = await value<{ chip: string; label: string; buttons: string[]; focusable: number }[]>(`[...document.querySelectorAll("#cards .review-thread")].map(card => ({
        chip: card.querySelector(".review-chip").textContent, label: card.getAttribute("aria-label"), focusable: card.tabIndex,
        buttons: [...card.querySelectorAll(".review-card-actions button")].map(button => button.textContent) }))`);
      assert.deepEqual(cards.map(card => card.chip), ["draft", "sent", "addressed", "resolved"]);
      assert.deepEqual(cards.map(card => card.label), ["Comment on lines 2 to 3 of file.md · draft", "Comment on lines 2 to 3 of file.md · sent",
        "Comment on line 4 of file.md · addressed", "Comment on lines 2 to 3 of file.md · resolved"]);
      assert.deepEqual(cards.map(card => card.focusable), [0, 0, 0, 0]);
      assert.deepEqual(cards.map(card => card.buttons), [["Edit", "Delete"], ["Edit", "Reply", "Resolve"], ["Reply", "Resolve"], ["Reopen"]]);
      // The suggestion is a small diff: the line kept, the one taken out, the one put in.
      assert.deepEqual(await value(`[...document.querySelectorAll("#cards .review-thread")[0].querySelectorAll(".review-suggestion-line")].map(line => [line.dataset.kind, line.textContent.slice(1).trim()])`),
        [["same", "two"], ["removed", "three"], ["added", "3"]]);
      assert.deepEqual(await value(`[...document.querySelectorAll("#cards .review-thread")[1].querySelectorAll(".review-replies li")].map(item => [item.dataset.author,
        item.querySelector(".review-card-head strong").textContent, item.querySelector(".review-card-body").textContent.trim()])`),
        [["agent", "Agent", "Done in the next turn"], ["user", "You", "Thanks"]]);
      // The card takes the keyboard.
      await evaluate(`document.querySelectorAll("#cards .review-thread")[2].focus()`);
      assert.equal(await value(`document.activeElement === document.querySelectorAll("#cards .review-thread")[2]`), true);
    });

    assert.deepEqual(page.errors, []);
  } finally {
    await page.close();
  }
});
