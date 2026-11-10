import assert from "node:assert/strict";
import test from "node:test";
import { keepIfSame } from "./keepIfSame";

test("an answer that says the same keeps the state, so that nothing renders again", () => {
  const previous = { commands: [{ id: "a", keys: ["Ctrl+K"] }], pickers: [], regions: false };
  assert.equal(keepIfSame(previous, { commands: [{ id: "a", keys: ["Ctrl+K"] }], pickers: [], regions: false }), previous);
  assert.equal(keepIfSame(previous, previous), previous);
});

test("an answer that differs, or null around a value, replaces the state", () => {
  const previous = { commands: [{ id: "a" }], regions: false };
  const next = { commands: [{ id: "a" }], regions: true };
  assert.equal(keepIfSame(previous, next), next);
  assert.equal(keepIfSame<typeof previous | null>(null, previous), previous);
  assert.equal(keepIfSame<typeof previous | null>(previous, null), null);
});

test("a value that cannot be written as JSON is replaced", () => {
  const cycle: { self?: unknown } = {}; cycle.self = cycle;
  const next = { self: 1 };
  assert.equal(keepIfSame<unknown>(cycle, next), next);
});

test("a Map, a Set or a function is not data JSON can compare: the answer replaces the state", () => {
  const first = { sizes: new Map([["a", 1]]) }, second = { sizes: new Map([["a", 2]]) };
  assert.equal(keepIfSame(first, second), second, "two maps are not the same for what they hold");
  const sets = { ids: new Set([1]) }, others = { ids: new Set([2]) };
  assert.equal(keepIfSame(sets, others), others);
  const run = { act: () => 1 }, again = { act: () => 2 };
  assert.equal(keepIfSame(run, again), again);
});
