import { test } from "node:test";
import assert from "node:assert/strict";

const root = process.env.EVAL_PROJECT_URL;
const { parseQuery, matches, QueryError } = await import(new URL("src/query.js", root));
const doc = { title: "Red apples and green pears", body: "Fresh fruit from the market", tag: "food" };
const m = (text, d = doc) => matches(parseQuery(text), d);

test("terms match any field, case-insensitively, as substrings", () => {
  assert.equal(m("APPLE"), true);
  assert.equal(m("market"), true);
  assert.equal(m("banana"), false);
});

test("adjacent terms are an implicit AND", () => {
  assert.equal(m("apple pear"), true);
  assert.equal(m("apple banana"), false);
});

test("NOT binds tighter than AND, AND tighter than OR", () => {
  assert.equal(m("banana OR apple pear"), true);
  assert.equal(m("banana OR apple banana"), false);
  assert.equal(m("NOT banana apple"), true);
  assert.equal(m("NOT apple OR pear"), true);
  assert.equal(m("NOT (apple OR pear)"), false);
  assert.equal(m("apple AND NOT pear"), false);
  assert.equal(m("not banana and apple"), true);
});

test("parentheses group", () => {
  assert.equal(m("(banana OR apple) pear"), true);
  assert.equal(m("(banana OR kiwi) pear"), false);
  assert.equal(m("((apple))"), true);
});

test("quoted phrases match as a whole, with backslash escapes", () => {
  assert.equal(m('"red apples"'), true);
  assert.equal(m('"apples red"'), false);
  assert.equal(m('"say \\"hi\\""', { title: 'they say "hi" here' }), true);
  assert.equal(m('"OR"', { title: "or else" }), true);
});

test("field:value terms look in that field only", () => {
  assert.equal(m("tag:food"), true);
  assert.equal(m("tag:fruit"), false);
  assert.equal(m('title:"green pears"'), true);
  assert.equal(m("missing:food"), false);
});

test("errors give the position of their cause", () => {
  const position = (text) => { try { parseQuery(text); } catch (error) { assert.ok(error instanceof QueryError, String(error)); return error.position; } return null; };
  assert.equal(position(""), 0);
  assert.equal(position("   "), 0);
  assert.equal(position('apple "red'), 6);
  assert.equal(position("(apple OR pear"), 0);
  assert.equal(position("apple)"), 5);
  assert.equal(position("apple OR"), 6);
  assert.equal(position("AND apple"), 0);
  assert.equal(position("apple AND AND pear"), 10);
  assert.equal(position("apple NOT"), 6);
  assert.equal(position("()"), 1);
});
