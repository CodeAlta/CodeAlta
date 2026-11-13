import { test } from "node:test";
import assert from "node:assert/strict";
import { applyDiscount, totalForCustomer, totalForGuest } from "../src/cart.js";

const cart = { items: [{ price: "10.00", quantity: 2 }, { price: "5.00", quantity: 1 }], taxRate: 0.2 };

test("applyDiscount applies the discount, then the tax", () => {
  assert.equal(applyDiscount(cart, 10), 2700);
});

test("applyDiscount leaves the tax out with skipTax", () => {
  assert.equal(applyDiscount(cart, 10, { skipTax: true }), 2250);
});

test("totals of customers and guests", () => {
  assert.equal(totalForCustomer(cart), 3000);
  assert.equal(totalForGuest(cart), 3150);
});
