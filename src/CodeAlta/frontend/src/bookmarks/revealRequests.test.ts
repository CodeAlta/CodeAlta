import assert from "node:assert/strict";
import test from "node:test";
import { createRevealRequests, revealStep, revealWaitMilliseconds } from "./revealRequests";

test("a session follows its own request, which a later one replaces and only its own end closes", () => {
  const requests = createRevealRequests();
  let told = 0;
  const stop = requests.subscribe(() => { told++; });
  assert.equal(requests.forSession("a"), null);
  const first = requests.open("a", "1200");
  assert.deepEqual(first, { sessionId: "a", offset: "1200" });
  assert.equal(requests.forSession("a"), first, "the same object until it ends");
  assert.equal(requests.forSession("b"), null);
  assert.equal(told, 1);

  // The same message asked for again is another request: the pane shows it again.
  const again = requests.open("a", "1200");
  assert.notEqual(again, first);
  assert.equal(told, 2);
  requests.done(first);
  assert.equal(requests.forSession("a"), again, "the end of a request that was replaced closes nothing");
  assert.equal(told, 2);

  const other = requests.open("b", "40");
  assert.equal(requests.forSession("a"), null, "one message is gone to at a time");
  requests.done(other);
  assert.equal(requests.forSession("b"), null);
  assert.equal(told, 4);
  stop();
  requests.open("a", "1");
  assert.equal(told, 4);
});

test("a request that no pane took is dropped after a moment; one a pane took lasts until it ends", () => {
  const timers: { run: () => void; milliseconds: number }[] = [];
  const requests = createRevealRequests({ set: (run, milliseconds) => { const timer = { run, milliseconds }; timers.push(timer); return timer; }, clear: timer => { timers.splice(timers.indexOf(timer as typeof timers[number]), 1); } });
  let told = 0;
  requests.subscribe(() => { told++; });

  // The session never opens: the request must not wait for the day it does.
  requests.open("never", "10");
  assert.deepEqual(timers.map(timer => timer.milliseconds), [revealWaitMilliseconds]);
  timers.shift()!.run();
  assert.equal(requests.forSession("never"), null);
  assert.equal(told, 2);

  // The pane of the session took it: reading a long history may take longer than the wait.
  const taken = requests.open("open", "20");
  assert.equal(requests.forSession("open"), taken);
  timers.shift()!.run();
  assert.equal(requests.forSession("open"), taken);
  assert.equal(told, 3);
  requests.done(taken);
  assert.equal(told, 4);

  // A request that was replaced or ended leaves no timer behind.
  const first = requests.open("a", "1");
  requests.open("a", "2");
  assert.equal(timers.length, 1);
  requests.done(first);
  assert.equal(timers.length, 1);
  requests.done(requests.forSession("a")!);
  assert.equal(timers.length, 0);
});

test("a window shows a message it holds, reads older history for one before it, and newer history after it slid away", () => {
  const window = [{ offset: "900" }, { offset: "1000" }, { offset: "1200" }];
  assert.equal(revealStep(window, true, false, "1000"), "shown");
  assert.equal(revealStep(window, true, false, "900"), "shown");
  assert.equal(revealStep(window, true, false, "400"), "older");
  assert.equal(revealStep(window, false, false, "400"), "missing", "the journal has nothing older");
  // Offsets are compared as numbers written in decimal, not as text.
  assert.equal(revealStep(window, true, false, "99"), "older");
  assert.equal(revealStep([{ offset: "99" }, { offset: "1000" }], true, false, "1000"), "shown");
  // An offset inside the window that is no record of it names nothing the timeline has.
  assert.equal(revealStep(window, true, false, "950"), "missing");
  assert.equal(revealStep(window, true, false, "5000"), "missing", "a window that reaches the end of the journal has nothing after it");
  assert.equal(revealStep(window, true, true, "5000"), "newest");
  assert.equal(revealStep(window, true, true, "1000"), "shown");
  assert.equal(revealStep([], true, false, "10"), "older");
  assert.equal(revealStep([], false, false, "10"), "missing");
});
