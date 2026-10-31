import { test } from "node:test";
import assert from "node:assert/strict";

const root = process.env.EVAL_PROJECT_URL;
const { buildInvoice, renderInvoice } = await import(new URL("src/invoice.js", root));
const cart = [{ sku: "pen", unitPrice: 199, quantity: 3 }, { sku: "book", unitPrice: 1250, quantity: 1, discount: 10 }];

test("the lines, the subtotal, the tax and the total", () => {
  const invoice = buildInvoice(cart, "FR");
  assert.deepEqual(invoice.lines.map((line) => [line.sku, line.quantity, line.unitPrice, line.discount, line.amount]), [["pen", 3, 199, 0, 597], ["book", 1, 1250, 10, 1125]]);
  assert.equal(invoice.subtotal, 1722);
  assert.equal(invoice.tax, 344);
  assert.equal(invoice.total, 2066);
});

test("a fractional rate rounds the tax to the cent", () => {
  const invoice = buildInvoice(cart, "CH");
  assert.equal(invoice.tax, 139);
  assert.equal(invoice.total, 1861);
});

test("an empty cart is an invoice of zeros", () => {
  const invoice = buildInvoice([], "DE");
  assert.deepEqual([invoice.lines.length, invoice.subtotal, invoice.tax, invoice.total], [0, 0, 0, 0]);
});

test("an unknown region throws", () => {
  assert.throws(() => buildInvoice(cart, "XX"));
});

test("the text has a row per line and the totals", () => {
  const text = renderInvoice(buildInvoice(cart, "FR"));
  for (const part of ["3 x pen", "5.97 EUR", "1 x book", "11.25 EUR", "Subtotal", "17.22 EUR", "Tax (20%)", "3.44 EUR", "Total", "20.66 EUR"]) assert.ok(text.includes(part), part);
  assert.ok(text.indexOf("3 x pen") < text.indexOf("Subtotal") && text.indexOf("Subtotal") < text.indexOf("Tax (20%)"));
});
