import assert from "node:assert/strict";
import test from "node:test";
import { browserExecutable } from "../browserTarget";
import type { TimelineEvent } from "./contract";
import { session } from "./fixtureServer";
import { openPhone } from "./phoneBrowser";
import { testLimit } from "../testTime";

// Where the pictures of this test go, when a person wants to look at them.
const shots = process.env.CODEALTA_SHOTS ?? "";

test("the phone finds in the timeline of a session and in a file it shows, with the folds that hold the text opened while it looks", { skip: !browserExecutable, timeout: testLimit(120_000) }, async () => {
  const now = new Date().toISOString();
  const events: TimelineEvent[] = [];
  let seq = 0;
  const event = (kind: string, text: string, extra: Partial<TimelineEvent> = {}) => { const item = { seq: ++seq, at: now, kind, text, activityId: null, ...extra }; events.push(item); return item; };
  const long = "A line of words that takes room on the screen of a phone. ".repeat(10);
  let linkSeq = 0;
  // Thirty turns: a prompt, a thought and a call, an answer; a few answers hold the needle, one thought the haystack.
  for (let turn = 0; turn < 30; turn++) {
    event("user.message", `Prompt ${turn}. ${long}`);
    event("assistant.reasoning", turn === 28 ? "Looking in the haystack first.\n\nThen the rest of the files is read." : `Weighing turn ${turn}.`);
    event("activity.toolcall.started", "read_file", { activityId: `call-${turn}`, subject: `src/part${turn}.cs` });
    event("activity.toolcall.completed", "read_file", { activityId: `call-${turn}`, subject: `src/part${turn}.cs` });
    const answer = event("assistant.message", `Answer ${turn}. ${[3, 12, 26, 28].includes(turn) ? "Found the needle here. " : ""}${turn === 29 ? "See [the code](src/a.cs#L1200). " : ""}${long}`);
    if (turn === 29) linkSeq = answer.seq;
  }
  const code = Array.from({ length: 2500 }, (_, index) => `code ${index + 1}`).join("\n");
  const phone = await openPhone(server => {
    server.state.sessions = [session("s1", "A long session", "p1", 0)];
    server.state.events.set("s1", events);
    server.state.messagesPerPage = 10;
    server.state.linkedFiles.set(`s1/${linkSeq}/src/a.cs#L1200`, code);
  }, shots);
  const { server, evaluate, waitFor, click, shot } = phone;
  const bar = ".session-find";
  const type = (selector: string, text: string) => evaluate(`(() => { const box = document.querySelector(${JSON.stringify(`${selector} .find-bar-query`)}); box.value = ${JSON.stringify(text)}; box.dispatchEvent(new InputEvent("input", { bubbles: true })); })()`);
  const countOf = (selector: string) => evaluate<string>(`document.querySelector(${JSON.stringify(`${selector} .find-bar-count`)}).textContent`);
  const shownText = () => evaluate<string>('CSS.highlights.get("find-in-page-current") ? [...CSS.highlights.get("find-in-page-current")][0].toString() : ""');
  // The place shown in the viewer: the search of the session under it has its own.
  const shownInViewer = '[...CSS.highlights.get("find-in-page-current")].find(range => range.startContainer.parentElement.closest(".output-viewer"))';
  const shownBlock = () => evaluate<string>('[...CSS.highlights.get("find-in-page-current")][0].startContainer.parentElement.closest("#timeline > li").dataset.key');
  // Whether the place shown is between the bar and the bottom of the screen.
  const shownInSight = (selector: string) => evaluate<boolean>(`(() => {
    const range = [...CSS.highlights.get("find-in-page-current")][0], box = range.getBoundingClientRect(), top = document.querySelector(${JSON.stringify(selector)}).getBoundingClientRect().bottom;
    return box.height > 0 && box.top >= top && box.bottom <= innerHeight;
  })()`);
  const hidden = (selector: string) => evaluate<boolean>(`getComputedStyle(document.querySelector(${JSON.stringify(selector)})).display === "none"`);
  const openActivities = () => evaluate<number>('document.querySelectorAll("#timeline li.activity[data-expanded=true]").length');
  try {
    await phone.signIn();
    await click(".row[data-id=s1] .card-link");
    await waitFor('document.querySelectorAll("#timeline .msg").length >= 10', "the latest page");
    assert.equal(await evaluate('document.querySelector("#older").hidden'), false, "older messages are not held");

    // ---- The magnifier opens the bar at the top, with the keyboard, in place of the composer ----
    await click("#detail-find");
    await waitFor(`!document.querySelector("${bar}").hidden`, "the bar");
    assert.equal(await evaluate(`document.activeElement === document.querySelector("${bar} .find-bar-query")`), true, "the box has the keyboard");
    assert.equal(await evaluate(`document.querySelector("${bar} .find-bar-query").type + " " + document.querySelector("${bar} .find-bar-query").enterKeyHint`), "search search");
    assert.equal(await hidden("#composer"), true, "the composer makes room");
    assert.equal(await evaluate(`document.querySelector("${bar}").getBoundingClientRect().top`), 0);
    assert.equal(await evaluate("history.state?.sessionFind"), true, "an entry of its own in the history");
    const sizes = await evaluate<number[][]>(`[...document.querySelectorAll("${bar} .find-bar-row > button")].map(e => { const r = e.getBoundingClientRect(); return [r.width, r.height]; })`);
    assert.ok(sizes.length === 4 && sizes.every(([width, height]) => width >= 44 && height >= 44), JSON.stringify(sizes));
    assert.ok(await evaluate<boolean>(`document.querySelector("${bar}").scrollWidth <= innerWidth && document.documentElement.scrollWidth <= innerWidth`), "the bar fits the phone");

    // ---- As it is typed: the places of the loaded pages, the one nearest the reader shown, "+" while older pages exist ----
    await type(bar, "needle");
    await waitFor(`document.querySelector("${bar} .find-bar-count").textContent.endsWith("of 2+")`, "the places of the loaded pages");
    assert.equal(await evaluate('CSS.highlights.get("find-in-page").size'), 2);
    assert.equal(await shownText(), "needle");
    assert.equal(await countOf(bar), "2 of 2+", "at the end of the timeline, the newest place");
    assert.ok(await shownInSight(bar), "the place shown is in sight, under the bar");
    await shot("find-session.png");
    // The arrows go round, and do not open the keyboard.
    await evaluate(`document.querySelector("${bar} .find-bar-query").blur()`);
    await click(`${bar} .find-bar-next`);
    assert.equal(await countOf(bar), "1 of 2+", "after the last, the first");
    assert.ok(await shownInSight(bar));
    await click(`${bar} .find-bar-previous`);
    assert.equal(await countOf(bar), "2 of 2+");
    assert.notEqual(await evaluate("document.activeElement?.className"), "find-bar-query", "the keyboard stays closed");

    // ---- A folded activity that holds the text opens while it is looked for; the user's own folds stay theirs ----
    assert.equal(await openActivities(), 0);
    await type(bar, "h");
    await waitFor(`document.querySelector("${bar} .find-bar-count").textContent.includes("of")`, "searched");
    assert.equal(await openActivities(), 0, "one letter opens nothing");
    await type(bar, "haystack");
    await waitFor(`document.querySelector("${bar} .find-bar-count").textContent === "1 of 1+"`, "the thought found");
    assert.equal(await openActivities(), 1);
    assert.match(await evaluate<string>('document.querySelector("#timeline li.activity[data-expanded=true]").textContent'), /haystack/u);
    assert.ok(await shownInSight(bar));
    // What a call works on is found too, in its activity opened for it.
    await type(bar, "part27.cs");
    await waitFor(`[...CSS.highlights.get("find-in-page-current") ?? []][0]?.toString() === "part27.cs"`, "the call found");
    assert.equal(await countOf(bar), "1 of 1+");
    assert.equal(await openActivities(), 1, "the activity that held the haystack folded again");
    // A tap folds it, in the set of the search.
    await click('#timeline li.activity[data-expanded=true] .act-toggle');
    await waitFor(`document.querySelector("${bar} .find-bar-count").textContent === "0 of 0+"`, "the call folded away");
    assert.equal(await openActivities(), 0);
    assert.ok(await evaluate<boolean>(`document.querySelector("${bar} .find-bar-count").dataset.empty === "true"`), "said in red");

    // ---- The Search key goes to the nearest place and closes the keyboard ----
    await type(bar, "needle");
    await waitFor(`document.querySelector("${bar} .find-bar-count").textContent.endsWith("of 2+")`, "the needle again");
    await evaluate(`(() => { const box = document.querySelector("${bar} .find-bar-query"); box.focus(); box.dispatchEvent(new KeyboardEvent("keydown", { key: "Enter", bubbles: true, cancelable: true })); })()`);
    assert.equal(await evaluate(`document.activeElement === document.querySelector("${bar} .find-bar-query")`), false, "the keyboard closed");
    assert.ok(await shownInSight(bar));

    // ---- "Search older messages" reads the older pages and searches them too; the place shown stays ----
    const before = await shownBlock();
    assert.equal(await evaluate(`document.querySelector("${bar} .find-bar-more").textContent`), "Search older messages");
    await click(`${bar} .find-bar-more`);
    await waitFor(`document.querySelector("${bar} .find-bar-count").textContent.endsWith("of 4")`, "the whole session searched", testLimit(20_000));
    assert.equal(await evaluate('document.querySelector("#older").hidden'), true, "every page read");
    assert.equal(await evaluate(`document.querySelector("${bar} .find-bar-more").hidden`), true);
    assert.equal(await shownBlock(), before, "the place shown stays");
    assert.equal(await countOf(bar), "4 of 4");

    // ---- While the agent writes, the place shown stays, and what it writes is searched ----
    event("assistant.message", "Another needle, new.");
    server.push({ id: "s1", projectId: "p1", live: "idle", kind: "assistant", line: "Another needle, new.", at: new Date().toISOString(), activity: null, canSteer: false });
    await waitFor(`document.querySelector("${bar} .find-bar-count").textContent.endsWith("of 5")`, "what the agent wrote searched");
    assert.equal(await shownBlock(), before);
    assert.equal(await countOf(bar), "4 of 5");

    // ---- Match case, Whole word and Regex are behind "Aa" ----
    await click(`${bar} .find-bar-options`);
    assert.equal(await evaluate(`document.querySelector("${bar} .find-bar-menu").hidden`), false);
    assert.deepEqual(await evaluate(`[...document.querySelectorAll("${bar} .find-bar-toggle")].map(e => e.textContent + ":" + e.getAttribute("aria-checked"))`),
      ["Match case:false", "Match whole word:false", "Use regular expression:false"]);
    await click(`${bar} .find-bar-toggle[data-option=matchCase]`);
    await type(bar, "Needle");
    await waitFor(`document.querySelector("${bar} .find-bar-count").textContent === "0 of 0"`, "no Needle with a capital");
    await click(`${bar} .find-bar-toggle[data-option=matchCase]`);
    await click(`${bar} .find-bar-toggle[data-option=regex]`);
    await type(bar, "needle|haystack");
    await waitFor(`document.querySelector("${bar} .find-bar-count").textContent.endsWith("of 6")`, "a regular expression");
    await click(`${bar} .find-bar-toggle[data-option=regex]`);
    await evaluate(`document.body.dispatchEvent(new PointerEvent("pointerdown", { bubbles: true }))`);
    assert.equal(await evaluate(`document.querySelector("${bar} .find-bar-menu").hidden`), true, "a tap elsewhere closes the menu");

    // ---- A file shown full screen is searched in what it holds, then whole on request; Back closes its search, then it ----
    await type(bar, "needle");
    await waitFor(`document.querySelector("${bar} .find-bar-count").textContent.endsWith("of 5")`, "the needle again");
    await evaluate('[...document.querySelectorAll("#timeline li.msg a")].find(a => a.textContent === "the code").click()');
    await waitFor('document.querySelector(".output-viewer-text")?.textContent.length === 16000', "the page of the file");
    assert.equal(await evaluate("history.state?.outputViewer && history.state?.sessionFind"), true, "the search of the session stays under the file");
    await click(".output-viewer-find");
    await waitFor('!document.querySelector(".output-find").hidden', "the bar of the file");
    assert.equal(await hidden(".output-viewer-bar"), true, "in place of the title");
    await type(".output-find", "code 2499");
    await waitFor('document.querySelector(".output-find .find-bar-count").textContent === "0 of 0+"', "not in the page held");
    assert.equal(await evaluate('document.querySelector(".output-find .find-bar-more").textContent'), "Search the whole file");
    await click(".output-find .find-bar-more");
    await waitFor('document.querySelector(".output-find .find-bar-count").textContent === "1 of 1"', "the whole file searched");
    assert.equal(await evaluate('document.querySelector(".output-viewer-text").textContent'), code);
    assert.equal(await evaluate('document.querySelector(".output-viewer-mark").textContent'), "code 1200\n", "the linked line stays marked");
    assert.equal(await evaluate(`${shownInViewer}.toString()`), "code 2499");
    assert.ok(await evaluate<boolean>(`(() => {
      const range = ${shownInViewer}, box = range.getBoundingClientRect(), stage = document.querySelector(".output-viewer-stage").getBoundingClientRect();
      return box.height > 0 && box.top >= stage.top && box.bottom <= stage.bottom;
    })()`), "the place shown is in sight in the viewer");
    await type(".output-find", "code 24");
    await waitFor('document.querySelector(".output-find .find-bar-count").textContent.endsWith(" of 111")', "every place of the file");
    await shot("find-file.png");
    await evaluate("history.back()");
    await waitFor('document.querySelector(".output-find")?.hidden', "the search of the file closed");
    assert.ok(await evaluate("!!document.querySelector('.output-viewer')"), "the file stays open");
    assert.equal(await hidden(".output-viewer-bar"), false);
    await evaluate("history.back()");
    await waitFor('!document.querySelector(".output-viewer")', "the file closed");
    assert.equal(await evaluate(`document.querySelector("${bar}").hidden`), false, "the search of the session stays open");
    assert.equal(await evaluate('CSS.highlights.get("find-in-page").size'), 5, "the places of the file are no longer marked");

    // ---- Back closes the bar: the composer comes back, the marks go, the folds the search opened fold again ----
    await type(bar, "haystack");
    await waitFor(`document.querySelector("${bar} .find-bar-count").textContent === "1 of 1"`, "the haystack again");
    assert.equal(await openActivities(), 1);
    await evaluate("history.back()");
    await waitFor(`document.querySelector("${bar}").hidden`, "the bar closed");
    assert.equal(await hidden("#composer"), false, "the composer is back");
    assert.equal(await evaluate('CSS.highlights.has("find-in-page") || CSS.highlights.has("find-in-page-current")'), false);
    assert.equal(await openActivities(), 0, "the activity folded again");
    // The bar opens again on what it looked for.
    await click("#detail-find");
    await waitFor(`document.querySelector("${bar} .find-bar-count").textContent === "1 of 1"`, "the text kept");
    await click(`${bar} .find-bar-close`);
    await waitFor(`document.querySelector("${bar}").hidden`, "closed by its button");
    assert.equal(await evaluate("!!history.state?.sessionFind"), false, "its entry is gone");
    assert.deepEqual(phone.failures, []);
  } finally {
    await phone.close();
  }
});
