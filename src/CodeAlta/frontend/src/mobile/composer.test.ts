import assert from "node:assert/strict";
import test from "node:test";
import { memoryStore } from "./api";
import { nextFontStep, readFontStep, readTheme } from "./appearance";
import { coalesce, composerView } from "./composer";

test("idle, the composer sends", () => {
  assert.deepEqual(composerView(false, true, true), { primary: "send", secondary: null, stop: false, hint: "", submit: "send" });
  assert.deepEqual(composerView(false, true, true, true), composerView(false, true, true), "queueing first changes nothing while idle");
});

test("while a turn runs: Stop alone until text is typed, then Queue, and Steer when the session can be steered", () => {
  assert.deepEqual(composerView(true, false, true), { primary: null, secondary: null, stop: true, hint: "Working… type to steer or queue", submit: "steer" });
  assert.deepEqual(composerView(true, true, true), { primary: "steer", secondary: "queue", stop: true, hint: "Steer: into this turn · Queue: runs after this turn", submit: "steer" });
  assert.deepEqual(composerView(true, true, false), { primary: null, secondary: "queue", stop: true, hint: "Queue: runs after this turn", submit: "queue" });
});

test("a phone set to queue first makes Queue the primary action, and Steer the second one", () => {
  assert.deepEqual(composerView(true, true, true, true), { primary: "queue", secondary: "steer", stop: true, hint: "Queue: runs after this turn · Steer: into this turn", submit: "queue" });
  assert.equal(composerView(true, false, true, true).submit, "queue");
  assert.deepEqual(composerView(true, true, false, true), composerView(true, true, false));
});

test("files held go with Queue while a turn runs, and a steer of the text leaves them for later", () => {
  assert.deepEqual(composerView(false, false, true, false, true), composerView(false, true, true), "idle, files alone are sent");
  assert.deepEqual(composerView(true, false, true, false, true), { primary: null, secondary: "queue", stop: true, hint: "Queue: the files go after this turn", submit: "queue" });
  assert.deepEqual(composerView(true, true, true, false, true),
    { primary: "steer", secondary: "queue", stop: true, hint: "Steer: the text into this turn, the files wait · Queue: runs after this turn, with the files", submit: "steer" });
  assert.deepEqual(composerView(true, true, true, true, true),
    { primary: "queue", secondary: "steer", stop: true, hint: "Queue: runs after this turn, with the files · Steer: the text into this turn, the files wait", submit: "queue" });
  assert.deepEqual(composerView(true, true, false, false, true), { primary: null, secondary: "queue", stop: true, hint: "Queue: runs after this turn, with the files", submit: "queue" });
});

test("a coalesced task runs once at a time, and once more for every call made while it ran", async () => {
  let runs = 0, release: (() => void) | null = null;
  const task = coalesce(async () => { runs++; await new Promise<void>(resolve => { release = resolve; }); });
  const first = task(), second = task(), third = task();
  assert.equal(runs, 1);
  release!();
  await new Promise(resolve => setImmediate(resolve));
  assert.equal(runs, 2, "the calls made while it ran are one more run");
  release!();
  await Promise.all([first, second, third]);
  assert.equal(runs, 2);
  const fourth = task();
  assert.equal(runs, 3);
  release!();
  await fourth;
});

test("the theme and the text size kept on the phone, else the system's theme and the browser's size", () => {
  assert.equal(readTheme(memoryStore(), true), "light");
  assert.equal(readTheme(memoryStore({ "alta.theme": "dark" }), true), "dark");
  assert.equal(readTheme(memoryStore({ "alta.theme": "pink" }), false), "dark");
  assert.equal(readFontStep(memoryStore()), 2);
  assert.equal(readFontStep(memoryStore({ "alta.fontStep": "5" })), 5);
  assert.equal(readFontStep(memoryStore({ "alta.fontStep": "9" })), 2);
  assert.deepEqual([1, 2, 3, 4, 5].map(nextFontStep), [2, 3, 4, 5, 1]);
});
