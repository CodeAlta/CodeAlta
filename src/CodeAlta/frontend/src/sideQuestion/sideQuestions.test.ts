import assert from "node:assert/strict";
import test from "node:test";
import { createSideQuestions, parseSideQuestion, type SideQuestionEvent } from "./sideQuestions";

test("only /ask at the beginning is a side question, with or without a question", () => {
  assert.equal(parseSideQuestion("/ask"), "");
  assert.equal(parseSideQuestion("  /ASK  why? \n"), "why?");
  assert.equal(parseSideQuestion("/ask first\nsecond"), "first\nsecond");
  for (const text of ["ask", "/asking why", "text /ask why", "/ask?", "/ask-other"]) assert.equal(parseSideQuestion(text), null);
});

test("answers stream separately by session, can be reopened and never call the host again", async () => {
  const calls: string[] = [];
  const store = createSideQuestions(async (session, question) => {
    calls.push(`${session}:${question}`);
    return (async function* (): AsyncGenerator<SideQuestionEvent> {
      yield { kind: "text", value: "hello " };
      yield { kind: "text", value: "world" };
      yield { kind: "done", value: "hello world" };
    })();
  });
  const done = new Promise<void>(resolve => store.subscribe(() => { if (store.view("a").entries[0]?.state === "done") resolve(); }));
  const id = store.ask("a", " why? ")!;
  await done;
  assert.equal(store.view("a").entries[0].answer, "hello world");
  assert.deepEqual(store.view("b").entries, []);
  store.close("a");
  store.list("a");
  store.show("a", id);
  assert.equal(store.view("a").shown, id);
  assert.deepEqual(calls, ["a:why?"]);
});

test("closing or replacing a running answer aborts it and keeps its partial answer", async () => {
  const signals: AbortSignal[] = [];
  const store = createSideQuestions(async (_session, _question, signal) => {
    signals.push(signal);
    return (async function* (): AsyncGenerator<SideQuestionEvent> {
      yield { kind: "text", value: "partial" };
      await new Promise<void>(resolve => signal.addEventListener("abort", () => resolve(), { once: true }));
      yield { kind: "done", value: "too late" };
    })();
  });
  const partial = new Promise<void>(resolve => store.subscribe(() => { if (store.view("a").entries[0]?.answer) resolve(); }));
  store.ask("a", "first");
  await partial;
  store.ask("a", "second");
  assert.equal(signals[0].aborted, true);
  assert.equal(store.view("a").entries[0].state, "stopped");
  store.close("a");
  assert.equal(signals[1].aborted, true);
  assert.equal(store.view("a").entries[1].state, "stopped");
  await Promise.resolve();
  assert.equal(store.view("a").entries[0].answer, "partial");
});

test("closing from a change listener stops the request before it starts", () => {
  const signals: AbortSignal[] = [];
  const store = createSideQuestions(async (_session, _question, signal) => {
    signals.push(signal);
    return (async function* (): AsyncGenerator<SideQuestionEvent> {})();
  });
  store.subscribe(() => { if (store.view("a").shown !== null) store.close("a"); });
  store.ask("a", "hello");
  assert.equal(store.view("a").entries[0].state, "stopped");
  assert.ok(signals.length === 0 || signals[0].aborted);
});

test("the cache is bounded per session and errors stay visible", async () => {
  const store = createSideQuestions(async () => (async function* (): AsyncGenerator<SideQuestionEvent> {
    yield { kind: "error", status: "unsupported", value: "Provider cannot answer" };
  })(), 2);
  const failed = new Promise<void>(resolve => store.subscribe(() => { if (store.view("a").entries.at(-1)?.state === "failed") resolve(); }));
  store.ask("a", "one");
  await failed;
  assert.equal(store.view("a").entries[0].failure?.status, "unsupported");
  store.ask("a", "two");
  store.ask("a", "three");
  assert.deepEqual(store.view("a").entries.map(entry => entry.question), ["two", "three"]);
  store.dispose();
});
