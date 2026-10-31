import { test } from "node:test";
import assert from "node:assert/strict";
import { execFileSync } from "node:child_process";
import { mkdtempSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { fileURLToPath } from "node:url";

const root = process.env.EVAL_PROJECT_URL;
const { parseCoupon } = await import(new URL("src/coupons.js", root));
const { cartTotal } = await import(new URL("src/cart.js", root));
const cart = [{ sku: "pen", unitPrice: 199, quantity: 3 }, { sku: "book", unitPrice: 1250, quantity: 1, discount: 10 }];

test("valid codes parse, in any case", () => {
  for (const code of ["SAVE1", "save10", "Save90", "FLAT1", "flat500"]) assert.doesNotThrow(() => parseCoupon(code), code);
});

test("invalid codes throw a RangeError", () => {
  for (const code of ["SAVE0", "SAVE91", "FLAT0", "SAVE", "FLAT-5", "BOGUS10", "", "SAVE10X"]) assert.throws(() => parseCoupon(code), RangeError, code);
});

test("the total without a coupon is unchanged", () => {
  assert.equal(cartTotal(cart), 1722);
  assert.equal(cartTotal(cart, {}), 1722);
});

test("a percent coupon applies after the line discounts, rounded half up", () => {
  assert.equal(cartTotal(cart, { coupon: "SAVE10" }), 1550);
  assert.equal(cartTotal(cart, { coupon: "save15" }), 1464);
});

test("a flat coupon takes cents off, never below zero", () => {
  assert.equal(cartTotal(cart, { coupon: "FLAT500" }), 1222);
  assert.equal(cartTotal(cart, { coupon: "FLAT5000" }), 0);
});

test("the command line applies a coupon", () => {
  const folder = mkdtempSync(join(tmpdir(), "coupon-"));
  const file = join(folder, "cart.json");
  writeFileSync(file, JSON.stringify(cart));
  const output = execFileSync(process.execPath, [join(fileURLToPath(root), "src", "cli.js"), file, "--coupon", "SAVE10"], { encoding: "utf8" });
  assert.match(output, /15\.50 EUR/);
});
