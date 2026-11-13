import { test } from "node:test";
import assert from "node:assert/strict";
import { importCustomers } from "../src/importCustomers.js";

test("importCustomers reads the customers after the header", () => {
  assert.deepEqual(importCustomers("id,name,email\n1,Ann,ann@example.com\n2,Bob,bob@example.com\n"), [
    { id: "1", name: "Ann", email: "ann@example.com" },
    { id: "2", name: "Bob", email: "bob@example.com" },
  ]);
});
