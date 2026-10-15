import assert from "node:assert/strict";
import test from "node:test";
import { createOrderedCalls } from "./orderedCalls";

// A call of the host that ends when the test says so.
function host() {
  const sent: string[] = [];
  const answers = new Map<string, { resolve: () => void; reject: (error: Error) => void }>();
  const call = (name: string) => () => {
    sent.push(name);
    return new Promise<string>((resolve, reject) => answers.set(name, { resolve: () => resolve(name), reject }));
  };
  return { sent, answers, call };
}

const settle = async () => { for (let turn = 0; turn < 4; turn++) await new Promise<void>(resolve => setTimeout(resolve, 0)); };

test("the calls of a key go one at a time in the order they were asked; the first goes at once", async () => {
  const inOrder = createOrderedCalls();
  const { sent, answers, call } = host();
  const hidden = inOrder("t1", call("hide"));
  assert.deepEqual(sent, ["hide"], "nothing waits: the call goes at once");
  const shown = inOrder("t1", call("show"));
  await settle();
  assert.deepEqual(sent, ["hide"], "the next waits for the answer of the one before");
  answers.get("hide")!.resolve();
  assert.equal(await hidden, "hide");
  await settle();
  assert.deepEqual(sent, ["hide", "show"]);
  answers.get("show")!.resolve();
  assert.equal(await shown, "show");
});

test("another key does not wait, and a call that fails does not stop the next", async () => {
  const inOrder = createOrderedCalls();
  const { sent, answers, call } = host();
  const failed = inOrder("t1", call("attach"));
  inOrder("t2", call("other"));
  assert.deepEqual(sent, ["attach", "other"]);
  const next = inOrder("t1", call("show"));
  answers.get("attach")!.reject(new Error("gone"));
  await assert.rejects(failed);
  await settle();
  assert.deepEqual(sent, ["attach", "other", "show"]);
  answers.get("show")!.resolve();
  assert.equal(await next, "show");
  await settle();
  // Once nothing waits, a call goes at once again.
  inOrder("t1", call("detach"));
  assert.equal(sent.at(-1), "detach");
});
