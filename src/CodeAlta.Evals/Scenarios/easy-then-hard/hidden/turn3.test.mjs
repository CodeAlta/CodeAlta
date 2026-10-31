import { test } from "node:test";
import assert from "node:assert/strict";

const root = process.env.EVAL_PROJECT_URL;
const { convert } = await import(new URL("src/currency.js", root));
const { memoize } = await import(new URL("src/util.js", root));
const { summarize } = await import(new URL("src/report.js", root));

test("conversions of the same amount from other currencies differ", () => {
  assert.equal(convert(1000, { from: "USD", to: "EUR" }), 921);
  assert.equal(convert(1000, { from: "GBP", to: "EUR" }), 1188);
  assert.equal(convert(1000, { from: "EUR", to: "USD" }), 1086);
  assert.equal(convert(1000, { from: "CHF", to: "GBP" }), 889);
});

test("memoize tells objects apart", () => {
  let calls = 0;
  const f = memoize((a, b) => { calls++; return a.x + b; });
  assert.equal(f({ x: 1 }, 1), 2);
  assert.equal(f({ x: 2 }, 1), 3);
  assert.equal(f({ x: 1 }, 1), 2);
  assert.ok(calls >= 2);
});

test("a report in another currency", () => {
  assert.equal(summarize([{ id: 1, amount: 1000, currency: "EUR" }, { id: 2, amount: 1000, currency: "GBP" }], "USD").total, 1086 + 1290);
});
