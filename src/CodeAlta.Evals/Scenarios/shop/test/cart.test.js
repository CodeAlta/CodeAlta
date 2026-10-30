import { test } from "node:test";
import assert from "node:assert/strict";
import { addItem, removeItem, cartTotal, describeCart } from "../src/cart.js";

test("adding the same sku adds its quantity", () => {
  const cart = addItem(addItem([], { sku: "pen", unitPrice: 199, quantity: 1 }), { sku: "pen", unitPrice: 199, quantity: 2 });
  assert.deepEqual(cart, [{ sku: "pen", unitPrice: 199, quantity: 3 }]);
});

test("removing a sku removes its line", () => {
  assert.deepEqual(removeItem([{ sku: "pen", unitPrice: 199, quantity: 1 }], "pen"), []);
});

test("the total applies the discount of each line", () => {
  assert.equal(cartTotal([{ sku: "pen", unitPrice: 199, quantity: 3 }, { sku: "book", unitPrice: 1250, quantity: 1, discount: 10 }]), 1722);
});

test("the description ends with the total", () => {
  assert.equal(describeCart([{ sku: "pen", unitPrice: 199, quantity: 3 }]), "3 x pen: 5.97 EUR\nTotal: 5.97 EUR");
});
