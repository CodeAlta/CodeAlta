import { test } from "node:test";
import assert from "node:assert/strict";
import { summarize } from "../src/report.js";

test("a report in euros", () => {
  const report = summarize([{ id: 1, amount: 2500, currency: "EUR" }, { id: 2, amount: 1000, currency: "EUR" }], "EUR");
  assert.deepEqual(report, { count: 2, total: 3500, formatted: "35.00 EUR" });
});

test("a report converts each order", () => {
  const orders = [{ id: 1, amount: 1000, currency: "USD" }, { id: 2, amount: 1000, currency: "GBP" }, { id: 3, amount: 250, currency: "EUR" }];
  assert.equal(summarize(orders, "EUR").total, 921 + 1188 + 250);
});
