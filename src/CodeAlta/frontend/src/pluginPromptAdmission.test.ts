import assert from "node:assert/strict";
import test from "node:test";
import { createPluginPromptAdmissions, type PluginPromptOutcome } from "./pluginPromptAdmission";

const deferred = <T,>() => { let resolve!: (value: T) => void; const promise = new Promise<T>(r => { resolve = r; }); return { resolve, promise }; };

test("plugin response waits for downstream ownership while close remains readable", async () => {
  const held = deferred<PluginPromptOutcome>();
  const answers: unknown[] = [];
  let signal!: AbortSignal;
  const owner = createPluginPromptAdmissions((_, lifetime) => { signal = lifetime; return held.promise; }, (id, accepted) => answers.push([id, accepted]));
  owner.receive("request", {});
  assert.deepEqual(answers, []);
  owner.close("request");
  assert.equal(signal.aborted, true);
  assert.deepEqual(answers, [], "close cannot release an in-flight host command");
  held.resolve(true); await Promise.resolve();
  assert.deepEqual(answers, [["request", true]], "a definitive downstream owner receipt permits handoff");
});

test("canceled transport and thrown dispatch are unknown, not conclusive cancellation", async () => {
  const held = deferred<PluginPromptOutcome>();
  const answers: unknown[] = [];
  const owner = createPluginPromptAdmissions(() => held.promise, (...value) => answers.push(value));
  owner.receive("request", {}); owner.dispose(); held.resolve(null); await Promise.resolve();
  assert.deepEqual(answers, []);
  const throwingAnswers: unknown[] = [];
  const throwing = createPluginPromptAdmissions(() => { throw Error("transport"); }, (...value) => throwingAnswers.push(value));
  throwing.receive("unknown", {}); await Promise.resolve();
  assert.deepEqual(throwingAnswers, []);
});

test("close before delivery fences late work; duplicate events never dispatch twice", async () => {
  let calls = 0;
  const answers: unknown[] = [];
  const owner = createPluginPromptAdmissions(() => { calls++; return true; }, (...value) => answers.push(value));
  owner.close("closed"); owner.receive("closed", {});
  assert.equal(calls, 0);
  assert.deepEqual(answers, [], "an unknown close might own an earlier page's unfinished command");
  owner.receive("once", {}); owner.receive("once", {}); await Promise.resolve(); owner.receive("once", {});
  assert.equal(calls, 1);
  assert.deepEqual(answers, [["once", true], ["once", true]]);
});

test("old epoch completion cannot acknowledge a request in a replacement owner", async () => {
  const held = deferred<PluginPromptOutcome>();
  const answers: unknown[] = [];
  const owner = createPluginPromptAdmissions(() => held.promise, (...value) => answers.push(value));
  owner.receive("same-id", {}); owner.dispose(); held.resolve(true); await Promise.resolve();
  assert.deepEqual(answers, []);
});

test("a captured request conclusively refused before dispatch can release its handoff", async () => {
  const answers: unknown[] = [];
  const owner = createPluginPromptAdmissions(() => false, (...value) => answers.push(value));
  owner.receive("refused", {}); await Promise.resolve();
  assert.deepEqual(answers, [["refused", false]]);
});

test("held requests do not block a second delivery and lost responses never redispatch on reconnect", async () => {
  const held = deferred<PluginPromptOutcome>(), answers: unknown[] = [];
  let calls = 0;
  const owner = createPluginPromptAdmissions((request: string) => { calls++; return request === "held" ? held.promise : true; },
    (...value) => { answers.push(value); throw Error("response lost"); });
  owner.receive("first", "held"); owner.receive("second", "ready"); await Promise.resolve();
  assert.deepEqual(answers, [["second", true]]);
  owner.disconnect(); owner.close("first"); held.resolve(null); await Promise.resolve();
  owner.receive("first", "held");
  assert.equal(calls, 2);
  assert.deepEqual(answers, [["second", true]]);
});

test("a lost response retries only the answer, not the already accepted command", async () => {
  let dispatches = 0, responses = 0;
  const owner = createPluginPromptAdmissions(() => { dispatches++; return true; }, () => { responses++; });
  owner.receive("once", {}); await Promise.resolve();
  // The response transport can lose this answer; reconnect reuses the original captured result.
  owner.disconnect(); owner.receive("once", {});
  assert.equal(dispatches, 1);
  assert.equal(responses, 2);
});

test("null and rejected admissions remain unacknowledged through later close and disconnect", async () => {
  for (const dispatch of [() => Promise.resolve(null), () => Promise.reject(Error("lost"))]) {
    const answers: unknown[] = [];
    let calls = 0;
    const owner = createPluginPromptAdmissions(() => { calls++; return dispatch(); }, (...value) => answers.push(value));
    owner.receive("unknown", {}); await Promise.resolve();
    owner.close("unknown"); owner.disconnect(); owner.receive("unknown", {});
    assert.equal(calls, 1);
    assert.deepEqual(answers, []);
  }
});
