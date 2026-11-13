import { test } from "node:test";
import assert from "node:assert/strict";
import { amountDue } from "../src/checkout.js";

const cart = { items: [{ price: "10.00", quantity: 1 }], taxRate: 0.2 };

test("amountDue charges guests the handling fee", () => {
  assert.equal(amountDue(cart, null, null), 1350);
});

test("amountDue applies the coupon of a customer, without tax when exempt", () => {
  assert.equal(amountDue(cart, { exempt: false }, { percent: 50 }), 600);
  assert.equal(amountDue(cart, { exempt: true }, { percent: 50 }), 500);
});
