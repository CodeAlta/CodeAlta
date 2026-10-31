import { test } from "node:test";
import assert from "node:assert/strict";

const root = process.env.EVAL_PROJECT_URL;
const format = await import(new URL("src/format.js", root));
const { describeCart } = await import(new URL("src/cart.js", root));

test("formatAmount replaces formatPrice", () => {
  assert.equal(format.formatAmount(1234), "12.34 EUR");
  assert.equal(format.formatAmount(-5, "USD"), "-0.05 USD");
  assert.ok(!("formatPrice" in format));
});

test("the description still formats its amounts", () => {
  assert.equal(describeCart([{ sku: "pen", unitPrice: 199, quantity: 3 }]), "3 x pen: 5.97 EUR\nTotal: 5.97 EUR");
});
