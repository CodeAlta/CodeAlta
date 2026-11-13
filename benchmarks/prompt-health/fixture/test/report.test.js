import { test } from "node:test";
import assert from "node:assert/strict";
import { monthlyReport } from "../src/report.js";

test("monthlyReport sums the sales by category", () => {
  const report = monthlyReport([
    { category: "bakery", amount: "12.50" },
    { category: "dairy", amount: "3.20" },
    { category: "bakery", amount: "7.50" },
  ]);
  assert.deepEqual(report, [{ category: "bakery", total: "20.00" }, { category: "dairy", total: "3.20" }]);
});
