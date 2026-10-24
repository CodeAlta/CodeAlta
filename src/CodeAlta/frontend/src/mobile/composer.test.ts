import assert from "node:assert/strict";
import test from "node:test";
import { memoryStore } from "./api";
import { nextFontStep, readFontStep, readTheme } from "./appearance";
import { coalesce, composerView } from "./composer";

test("idle, the composer sends", () => {
  assert.deepEqual(composerView(false, true, true), { primary: "send", queue: false, stop: false, hint: "", submit: "send" });
});

test("while a turn runs: Stop alone until text is typed, then Queue, and Steer when the session can be steered", () => {
  assert.deepEqual(composerView(true, false, true), { primary: null, queue: false, stop: true, hint: "Working… type to steer or queue", submit: "steer" });
  assert.deepEqual(composerView(true, true, true), { primary: "steer", queue: true, stop: true, hint: "Steer: into this turn · Queue: runs after this turn", submit: "steer" });
  assert.deepEqual(composerView(true, true, false), { primary: null, queue: true, stop: true, hint: "Queue: runs after this turn", submit: "queue" });
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
