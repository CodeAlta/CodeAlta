import assert from "node:assert/strict";
import test from "node:test";
import type { LocalSearchEvent, LocalSearchStatusResponse } from "#neoastra";
import { createLocalSearchHub, type LocalSearchApi } from "./localSearchHub";

const settle = () => new Promise(resolve => setTimeout(resolve, 0));

test("an event of the host that asks for the dialog of search by meaning reaches the window once, and the status is read with each event", async () => {
  let push: ((event: IteratorResult<LocalSearchEvent>) => void) | null = null;
  const queue: IteratorResult<LocalSearchEvent>[] = [];
  const send = (event: LocalSearchEvent) => { const value = { value: event, done: false }; if (push) { const resolve = push; push = null; resolve(value); } else queue.push(value); };
  let reads = 0;
  const api = {
    status: async () => { reads++; return { status: "ok", settings: null } as unknown as LocalSearchStatusResponse; },
    watch: async () => ({ [Symbol.asyncIterator]: () => ({ next: () => queue.length ? Promise.resolve(queue.shift()!) : new Promise(resolve => { push = resolve; }) }) }),
  } as unknown as LocalSearchApi;
  const hub = createLocalSearchHub(api);
  const opened: string[] = [];
  const stop = hub.onOpen(what => opened.push(what));
  const disconnect = hub.connect("epoch");
  await settle();
  send({ revision: 0, open: null });
  send({ revision: 1, open: "meaning" });
  await settle(); await settle();
  assert.deepEqual(opened, ["meaning"]);
  assert.ok(reads >= 3, "read at the start and at each event");

  stop();
  send({ revision: 2, open: "meaning" });
  await settle(); await settle();
  assert.deepEqual(opened, ["meaning"], "nothing is heard once the listener is gone");
  disconnect();
});
