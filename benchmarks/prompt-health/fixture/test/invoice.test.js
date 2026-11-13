import { test } from "node:test";
import assert from "node:assert/strict";
import { invoiceTotal } from "../src/invoice.js";

test("invoiceTotal sums the amounts of the ERP export", () => {
  assert.equal(invoiceTotal([{ label: "Oven", amount: "1,234.50" }, { label: "Tray", amount: "15.50" }]), "1250.00");
});
