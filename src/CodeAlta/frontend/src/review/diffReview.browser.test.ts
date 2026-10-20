import assert from "node:assert/strict";
import test from "node:test";
import { browserExecutable } from "../browserTarget";
import { ctrl, openReviewPage, shift, type Point } from "./reviewBrowserHarness";

const edge = browserExecutable;

type Box = { top: number; height: number } | null;

// The review of the changes as the Changes tab draws it: real Monaco diffs with their comment layers, the view of all
// files and the whole tab, over a reviews hub whose host is in memory, driven with the mouse and the keys of the page.
test("the review of the changes is written and shown in the diffs of the Changes tab", { skip: !edge, timeout: 240_000 }, async t => {
  const page = await openReviewPage(new URL("./diffReview.mount.tsx", import.meta.url), "!!window.diffReviewFixture", new URL("../demo-api.ts", import.meta.url));
  const { command, evaluate, value, until, frames, click, clickOn, key, type } = page;
  const f = "diffReviewFixture";
  // A key of the review (a letter), typed as the keyboard does.
  const press = async (letter: string) => {
    const code = letter.toUpperCase().charCodeAt(0);
    await command("Input.dispatchKeyEvent", { type: "keyDown", key: letter, code: `Key${letter.toUpperCase()}`, text: letter, unmodifiedText: letter, windowsVirtualKeyCode: code, nativeVirtualKeyCode: code });
    await command("Input.dispatchKeyEvent", { type: "keyUp", key: letter, code: `Key${letter.toUpperCase()}`, windowsVirtualKeyCode: code, nativeVirtualKeyCode: code });
  };
  try {
    const glyph = (side: "old" | "new", line: number) => value<Point>(`${f}.glyph("${side}", ${line})`);
    const tops = (side: "old" | "new", ...lines: number[]) => value<(number | null)[]>(`[${lines.join(",")}].map(line => ${f}.lineTop("${side}", line))`);
    const zone = (side: "old" | "new", id: string | null) => value<Box>(`${f}.zone("${side}", ${JSON.stringify(id)})`);
    const composer = `document.querySelector("#diff .review-zone .review-editor textarea")`;
    const composerFocused = `!!${composer} && document.activeElement === ${composer}`;
    const focusShown = `[document.activeElement?.tagName, document.activeElement?.className, ${composer}?.getAttribute("aria-label")]`;
    const threadOf = (id: string) => value<Record<string, unknown> | null>(`${f}.server.threads.find(thread => thread.id === ${JSON.stringify(id)}) ?? null`);
    // A zone is under its line: its top is the bottom of the line (20 pixels high).
    const under = (side: "old" | "new", id: string | null, line: number) =>
      until(`${f}.zone("${side}", ${JSON.stringify(id)})?.top === ${f}.lineTop("${side}", ${line}) + 20`, `[${f}.zone("${side}", ${JSON.stringify(id)}), ${f}.lineTop("${side}", ${line})]`);
    // Writes a comment in the composer that has the keyboard, and waits for its thread on the host.
    const write = async (text: string, id: string) => {
      await type(text);
      await key("Enter", ctrl);
      await until(`${f}.server.threads.some(thread => thread.id === "${id}") && ${f}.state.threads.some(thread => thread.id === "${id}")`);
    };

    await t.test("a click on the glyph margin of either side opens a composer there, Ctrl+Enter writes a thread of that side, and both sides stay in line", async () => {
      await evaluate(`${f}.show("diff")`);
      await until(`${f}.state.editors && ${f}.state.editors.diff.getLineChanges()?.length === 3`, "null", 10_000);
      await frames();
      // Registered with the review (twice: StrictMode mounts the effects again).
      assert.deepEqual(await value(`[...new Set(${f}.state.calls)]`), ["register:src/file.txt"]);
      assert.equal(await value(`${f}.state.inline.at(-1)`), false);

      await click(await glyph("old", 51));
      await until(composerFocused, focusShown);
      await under("old", null, 51);
      assert.equal(await value(`${composer}.getAttribute("aria-label")`), "Comment on line 51 of file.txt");
      await write("On the old side", "c1");
      assert.deepEqual(await threadOf("c1").then(thread => thread && [thread.target, thread.path, thread.side, thread.startLine, thread.endLine, thread.body]),
        ["changes", "src/file.txt", "old", 51, 51, "On the old side"]);
      // The host was asked for a comment on the file of the diff.
      assert.deepEqual(await value(`${f}.server.asked.filter(entry => entry.call === "add")`),
        [{ call: "add", path: "src/file.txt", startLine: 51, endLine: 51, body: "On the old side", suggestion: null }]);
      await until(`!${composer}`);
      await under("old", "c1", 51);
      // The old side's card says it is on the old side.
      assert.match(await value<string>(`document.querySelector('#diff [data-thread-id="c1"] .review-card-head span').textContent`), /line 51 \(old\)/);

      await click(await glyph("new", 51));
      await until(composerFocused, focusShown);
      await under("new", null, 51);
      await write("On the new side", "c2");
      assert.deepEqual(await threadOf("c2").then(thread => thread && [thread.path, thread.side, thread.startLine, thread.endLine]), ["src/file.txt", "new", 51, 51]);
      await until(`!${composer}`);
      await under("new", "c2", 51);
      await under("old", "c1", 51);
      // Each card is in the editor of its side only.
      assert.deepEqual(await value(`[...document.querySelectorAll("#diff [data-thread-id]")].map(card => [card.dataset.threadId, !!card.closest(".editor.original"), !!card.closest(".editor.modified")])`),
        [["c1", true, false], ["c2", false, true]]);
      // Below the two cards, both sides are in line again, and so are their contents.
      await frames();
      assert.deepEqual(await tops("old", 52, 53, 55), await tops("new", 52, 53, 55));
      assert.equal(await value(`${f}.state.editors.original.getContentHeight() === ${f}.state.editors.modified.getContentHeight()`), true);
      // The lines of each thread are marked in the glyph margin of their side.
      assert.equal(await value(`[...document.querySelectorAll("#diff .editor.original .review-glyph-thread")].some(node => Math.round(node.getBoundingClientRect().top) === ${f}.lineTop("old", 51))`), true);
      assert.equal(await value(`[...document.querySelectorAll("#diff .editor.modified .review-glyph-thread")].some(node => Math.round(node.getBoundingClientRect().top) === ${f}.lineTop("new", 51))`), true);
    });

    await t.test("a click and then a Shift-click on the glyph margin of a side comment the range of lines between them", async () => {
      await click(await glyph("new", 47));
      await until(composerFocused, focusShown);
      await click(await glyph("new", 49), shift);
      await until(`${composer}?.getAttribute("aria-label") === "Comment on lines 47 to 49 of file.txt"`, `${composer}?.getAttribute("aria-label")`);
      await until(composerFocused, focusShown);
      await under("new", null, 49);
      await write("A range", "c3");
      assert.deepEqual(await threadOf("c3").then(thread => thread && [thread.side, thread.startLine, thread.endLine]), ["new", 47, 49]);
      await under("new", "c3", 49);

      // The same on the old side, upwards.
      await click(await glyph("old", 49));
      await until(composerFocused, focusShown);
      await click(await glyph("old", 48), shift);
      await until(`${composer}?.getAttribute("aria-label") === "Comment on lines 48 to 49 of file.txt"`, `${composer}?.getAttribute("aria-label")`);
      await write("An old range", "c4");
      assert.deepEqual(await threadOf("c4").then(thread => thread && [thread.side, thread.startLine, thread.endLine]), ["old", 48, 49]);
      await under("old", "c4", 49);
      await frames();
      assert.deepEqual(await tops("old", 50, 53), await tops("new", 50, 53));
    });

    await t.test("in one column, the threads of the old side are listed above the diff, and Show side by side draws them again", async () => {
      await evaluate(`${f}.state.set({ sideBySide: false })`);
      await until(`${f}.state.inline.at(-1) === true`);
      await frames();
      // The new side's threads are still drawn under their lines.
      await under("new", "c2", 51);
      await under("new", "c3", 49);
      // The old side's are not in the diff, but in the notes, with their cards.
      assert.deepEqual(await value(`[...document.querySelectorAll("#diff [data-thread-id]")].map(card => card.dataset.threadId).sort()`), ["c2", "c3"]);
      // In line order: c4 (lines 48-49) before c1 (line 51).
      assert.deepEqual(await value(`[...document.querySelectorAll("#notes .review-notes-old [data-thread-id]")].map(card => card.dataset.threadId)`), ["c4", "c1"]);
      assert.equal(await value(`document.querySelector("#notes .review-notes-old header span").textContent`), "2 comments on the old side, not drawn in one column");
      assert.equal(await value(`document.querySelectorAll("#notes [data-thread-id]").length`), 2);

      await clickOn(`[...document.querySelectorAll("#notes .review-notes-old button")].find(button => button.textContent === "Show side by side")`);
      await until(`${f}.state.inline.at(-1) === false`);
      assert.equal(await value(`${f}.state.calls.includes("sideBySide")`), true);
      await until(`!document.querySelector("#notes .review-notes-old")`);
      // The cards are back in the old side's editor (where exactly: see the next test).
      await until(`["c1", "c4"].every(id => !!document.querySelector(\`#diff .editor.original [data-thread-id="\${id}"]\`)?.offsetParent)`);
      assert.deepEqual(await value(`[...document.querySelectorAll("#diff .editor.modified [data-thread-id]")].map(card => card.dataset.threadId).sort()`), ["c2", "c3"]);
    });

    // A layer whose editor goes (the old side in one column) leaves its zones in Monaco: `useLineCommentLayer` clears its
    // own map of zones but never removes them from the editor. The strip of old line numbers keeps an empty zone per
    // thread, which pushes the old numbers away from their rows, and side by side again the layer adds a second zone for
    // each thread on the same node: the card is drawn under the empty zones, away from its line.
    await t.test("after one column, the old side's cards are under their lines again, with no empty zone left", async () => {
      await frames();
      // Back side by side (the previous test): what Monaco draws between line 51 and line 52 of the old side.
      const card = (await zone("old", "c1"))!, [line51] = await tops("old", 51);
      const back = { cardTop: card.top - line51!, between: await value<number>(`${f}.span("old", 51, 52)`) - card.height };
      // In one column, the old side shows no zone of a comment.
      await evaluate(`${f}.state.set({ sideBySide: false })`);
      await until(`${f}.state.inline.at(-1) === true`);
      await frames();
      const inline = await value<{ height: number; filled: boolean }[]>(`${f}.commentZones("old")`);
      await evaluate(`${f}.state.set({ sideBySide: true })`);
      await until(`${f}.state.inline.at(-1) === false && !!${f}.zone("old", "c1")`);
      await frames();
      assert.deepEqual({ ...back, inline }, { cardTop: 20, between: 20, inline: [] });
    });

    await t.test("a thread asked for in a folded region unfolds it, and its card takes the keyboard", async () => {
      await evaluate(`${f}.put({ side: "new", startLine: 200, endLine: 200, body: "Far below" }); ${f}.put({ side: "old", startLine: 150, endLine: 150, body: "Far on the old side" })`);
      await until(`${f}.state.threads.some(thread => thread.id === "c5") && ${f}.state.threads.some(thread => thread.id === "c6")`);
      await frames();
      // Folded away: nothing of them shows.
      assert.deepEqual([await zone("new", "c5"), await zone("old", "c6"), (await tops("new", 200))[0]], [null, null, null]);

      await evaluate(`${f}.want("c5")`);
      await until(`document.activeElement?.dataset?.threadId === "c5"`, `[document.activeElement?.tagName, document.activeElement?.className, ${f}.state.calls]`);
      assert.equal(await value(`${f}.state.calls.includes("shown:c5")`), true);
      await under("new", "c5", 200);
      assert.equal(await value(`!!document.activeElement.closest("#diff .editor.modified .review-zone")`), true);
      // The old side unfolded with it, and the lines under the card are in line (old line = new line + 5 below the removal).
      await frames();
      assert.deepEqual(await tops("old", 205, 206), await tops("new", 200, 201));

      await evaluate(`${f}.want("c6")`);
      await until(`document.activeElement?.dataset?.threadId === "c6"`, `[document.activeElement?.tagName, ${f}.state.calls]`);
      assert.equal(await value(`${f}.state.calls.includes("shown:c6")`), true);
      await under("old", "c6", 150);
      await frames();
      assert.deepEqual(await tops("old", 151), await tops("new", 146));
    });

    await t.test("in the view of all files, a file with a thread is given the room of its card before its diff is read, and has it once drawn", async () => {
      // Without StrictMode: its second mount of each diff disposes the first one while Monaco compares its texts, and
      // Monaco then logs "no diff result available" (a development-only double mount, not one of the application).
      await evaluate(`${f}.show("all", false)`);
      const id = await value<string>(`${f}.put({ path: "a.txt", side: "new", startLine: 201, endLine: 201, body: "On a" })`);
      await until(`${f}.state.threads.length === 1 && document.querySelectorAll(".changes-all > .changes-section").length === 2`);
      await frames();
      const bodies = `Object.fromEntries([...document.querySelectorAll(".changes-all > .changes-section")].map(section => {
        const body = section.querySelector(".changes-section-body");
        return [section.dataset.path, { pending: body.dataset.pending === "true", height: body.offsetHeight, editor: !!body.querySelector(".monaco-editor .view-line") }];
      }))`;
      type Bodies = Record<"a.txt" | "b.txt", { pending: boolean; height: number; editor: boolean }>;
      // Not read yet: the two files are alike, but the one with a thread waits with the room of a card more.
      const waiting = await value<Bodies>(bodies);
      assert.equal(await value(`${f}.reads.count`), 0);
      assert.deepEqual([waiting["a.txt"].pending, waiting["b.txt"].pending], [true, true], JSON.stringify(waiting));
      assert.equal(waiting["a.txt"].height - waiting["b.txt"].height, 96, JSON.stringify(waiting));

      await evaluate(`${f}.release()`);
      const card = `document.querySelector('.changes-section[data-path="a.txt"] .editor.modified .review-zone [data-thread-id="${id}"]')`;
      await until(`(b => b["a.txt"].editor && b["b.txt"].editor)(${bodies}) && !!${card}?.offsetParent`, bodies, 10_000);
      // Drawn: the body of the file with a thread is taller by the zone of its card, both sides of its diff in line under it.
      await until(`(b => b["a.txt"].height - b["b.txt"].height)(${bodies}) === ${card}.closest(".review-zone").offsetHeight`,
        `[${bodies}, ${card}.closest(".review-zone").offsetHeight]`);
      assert.ok(await value<number>(`${card}.closest(".review-zone").offsetHeight`) > 40);
      assert.equal(await value(`${card}.closest(".review-zone").getBoundingClientRect().top - [...document.querySelectorAll('.changes-section[data-path="a.txt"] .editor.modified .view-line')]
        .find(line => line.textContent.replace(/\u00a0/g, " ") === "a.txt changed 201").getBoundingClientRect().bottom`), 0);
      assert.equal(await value(`[...document.querySelectorAll('.changes-section[data-path="b.txt"] .review-zone')].length`), 0);
    });

    await t.test("the Changes tab: Review turns the review on, a comment of the list is shown in its file, n goes from thread to thread across files, v marks the file viewed", async () => {
      await evaluate(`${f}.show("panel")`);
      const panel = `document.querySelector(".changes-panel")`;
      // The diff of a file is shown, its texts compared.
      const diffOf = (path: string) => `${panel}.querySelector(".changes-diff")?.getAttribute("aria-label") === "${path}" && ${f}.compared("${path}")`;
      await until(`!!${panel}?.querySelector('.changes-file[data-path="b.txt"]') && ${diffOf("a.txt")}`, `${panel}?.textContent`, 10_000);
      const toggle = `${panel}.querySelector(".changes-review-toggle")`;
      // Without a comment, the tab is not in review mode; the toggle turns it on and off.
      assert.deepEqual(await value(`[${panel}.dataset.review ?? null, ${toggle}.getAttribute("aria-pressed"), ${panel}.querySelectorAll(".changes-side-view").length]`), [null, "false", 0]);
      await clickOn(toggle);
      await until(`${panel}.dataset.review === "true" && ${toggle}.getAttribute("aria-pressed") === "true"`);
      assert.deepEqual(await value(`[...${panel}.querySelectorAll(".changes-side-view")].map(tab => tab.textContent)`), ["Files", "Comments0"]);
      assert.equal(await value(`!!${panel}.querySelector(".changes-diff-header .review-viewed")`), true);
      await clickOn(toggle);
      await until(`!${panel}.dataset.review && !${panel}.querySelector(".changes-side-view") && !${panel}.querySelector(".review-viewed")`);
      await clickOn(toggle);
      await until(`${panel}.dataset.review === "true"`);

      const [c1, c2, c3] = await value<string[]>(`[${f}.put({ path: "a.txt", side: "new", startLine: 51, endLine: 51, body: "On a" }),
        ${f}.put({ path: "b.txt", side: "new", startLine: 51, endLine: 51, body: "On b" }), ${f}.put({ path: "b.txt", side: "new", startLine: 250, endLine: 250, body: "Far in b" })]`);
      const count = (path: string) => `${panel}.querySelector('.changes-file[data-path="${path}"] .changes-row-threads')?.textContent`;
      await until(`${count("a.txt")} === "1" && ${count("b.txt")} === "2"`, `[${count("a.txt")}, ${count("b.txt")}]`);
      // The card of a.txt is drawn in its diff.
      await until(`!!${panel}.querySelector('.changes-diff .editor.modified .review-zone [data-thread-id="${c1}"]')?.offsetParent`);

      await clickOn(`[...${panel}.querySelectorAll(".changes-side-view")].find(tab => tab.textContent.startsWith("Comments"))`);
      await until(`${panel}.querySelectorAll(".changes-comments [data-thread-row]").length === 3`);
      assert.deepEqual(await value(`[...${panel}.querySelectorAll(".changes-comments .review-list-file")].map(group => [group.getAttribute("aria-label"),
        [...group.querySelectorAll("[data-thread-row]")].map(row => row.dataset.threadRow)])`), [["a.txt", [c1]], ["b.txt", [c2, c3]]]);
      const focused = (id: string) => `document.activeElement?.dataset?.threadId === "${id}" && !!document.activeElement.closest(".changes-diff .review-zone")`;
      const focusShown = `[document.activeElement?.tagName, document.activeElement?.className, ${panel}.querySelector(".changes-diff")?.getAttribute("aria-label")]`;
      await clickOn(`${panel}.querySelector('.changes-comments [data-thread-row="${c2}"]')`);
      await until(`${diffOf("b.txt")} && ${focused(c2)}`, focusShown);
      assert.equal(await value(`${panel}.querySelector('.changes-comments [data-thread-row="${c2}"]').getAttribute("aria-current")`), null, "shown: the list no longer marks it as asked for");

      // n: the next thread of the file, in a folded region, and then the first one of the review, in the other file.
      await press("n");
      await until(focused(c3), focusShown);
      assert.equal(await value(`${panel}.querySelector(".changes-diff").getAttribute("aria-label")`), "b.txt");
      await press("n");
      await until(`${diffOf("a.txt")} && ${focused(c1)}`, focusShown);
      await press("p");
      await until(`${diffOf("b.txt")} && ${focused(c3)}`, focusShown);
      await press("n");
      await until(`${diffOf("a.txt")} && ${focused(c1)}`, focusShown);

      // v: the selected file is viewed at its revision, and v again takes the mark away.
      await press("v");
      await until(`${f}.viewedAsked.length === 1 && ${panel}.querySelector(".changes-diff-header .review-viewed input")?.checked`, `${f}.viewedAsked`);
      assert.deepEqual(await value(`${f}.viewedAsked`), [{ target: "changes", path: "a.txt", revision: "rev-a.txt" }]);
      assert.match(await value<string>(`${panel}.querySelector(".review-bar")?.textContent ?? ""`), /1\/2 files viewed/);
      await clickOn(`[...${panel}.querySelectorAll(".changes-side-view")].find(tab => tab.textContent === "Files")`);
      await until(`!!${panel}.querySelector('.changes-file[data-path="a.txt"] .changes-row-viewed') && !${panel}.querySelector('.changes-file[data-path="b.txt"] .changes-row-viewed')`);
      await evaluate(`${panel}.querySelector('.changes-diff [data-thread-id="${c1}"]').focus()`);
      await press("v");
      await until(`${f}.viewedAsked.length === 2 && !${panel}.querySelector(".changes-diff-header .review-viewed input").checked`, `${f}.viewedAsked`);
      assert.deepEqual(await value(`${f}.viewedAsked[1]`), { target: "changes", path: "a.txt", revision: null });
    });

    assert.deepEqual(page.errors, []);
  } finally {
    await page.close();
  }
});
