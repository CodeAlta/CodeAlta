import assert from "node:assert/strict";
import test from "node:test";
import { contextSizes, defaultContextSummary, hasProviderContextOption, validContext } from "./contextSizes";

test("context choices are exact positive safe endpoints, never inferred from a maximum", () => {
  assert.deepEqual(contextSizes({ contextSizes: [272000, 1050000, 272000, -1, 0, 1.5, Infinity, NaN] }), [272000, 1050000]);
  assert.deepEqual(contextSizes({ defaultContextTokens: 1050000 }), []);
  assert.equal(validContext({ tokens: 500000 }, { contextSizes: [272000, 1050000] }), false);
  assert.equal(validContext({ tokens: null }, undefined), true);
  assert.equal(validContext(undefined, undefined), true);
  assert.equal(validContext({ tokens: NaN }, undefined), false);
  for (const tokens of [-1, 0, 1.5, Infinity, NaN, Number.MAX_SAFE_INTEGER + 1])
    assert.equal(validContext({ tokens }, { contextSizes: [tokens] }), false, "invalid advertised values never authorize a choice");
  assert.equal(validContext({ tokens: 1050000 }, { contextSizes: [1050000, 272000, 1050000] }), true);
});

test("default summary formats only a positive safe resolved capacity", () => {
  const model = { contextSizes: [272000, 1050000], defaultContextTokens: 272000 };
  assert.equal(defaultContextSummary(model), "272,000");
  assert.equal(defaultContextSummary(model, "de"), "272.000");
  assert.equal(defaultContextSummary(undefined), null);
  for (const defaultContextTokens of [-1, 0, 1.5, Infinity, NaN, Number.MAX_SAFE_INTEGER + 1])
    assert.equal(defaultContextSummary({ defaultContextTokens }), null);
});

test("ACP keeps its existing context option", () => {
  assert.equal(hasProviderContextOption([{ id: "context", name: "Context size" }]), true);
  assert.equal(hasProviderContextOption([{ id: "fast", name: "Fast mode" }]), false);
});
