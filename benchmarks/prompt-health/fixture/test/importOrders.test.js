import { test } from "node:test";
import assert from "node:assert/strict";
import { importOrders } from "../src/importOrders.js";

test("importOrders reads every order, with or without a final new line", () => {
  const expected = [{ id: "1", customer: "ann", cents: 1250 }, { id: "2", customer: "bob", cents: 300 }];
  assert.deepEqual(importOrders("id,customer,amount\n1,ann,12.50\n2,bob,3.00\n"), expected);
  assert.deepEqual(importOrders("id,customer,amount\n1,ann,12.50\n2,bob,3.00"), expected);
});
