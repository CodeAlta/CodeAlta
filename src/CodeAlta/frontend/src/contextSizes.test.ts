import assert from "node:assert/strict";
import test from "node:test";
import { contextSizes, defaultContextSummary, draftContextInputTokens, hasProviderContextOption, validContext } from "./contextSizes";

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
  assert.equal(defaultContextSummary(model), "272K");
  assert.equal(defaultContextSummary(model, "de"), "272K");
  assert.equal(defaultContextSummary({ defaultContextTokens: 922000 }), "922K");
  assert.equal(defaultContextSummary({ defaultContextTokens: 1050000 }), "1,050K");
  assert.equal(defaultContextSummary({ defaultContextTokens: 131072 }), "131.072K");
  assert.equal(defaultContextSummary({ defaultContextTokens: 131072 }, "de"), "131,072K");
  assert.equal(defaultContextSummary({ defaultContextTokens: Number.MAX_SAFE_INTEGER - 1 }), "9,007,199,254,740.99K");
  assert.equal(defaultContextSummary({ defaultContextTokens: Number.MAX_SAFE_INTEGER }), "9,007,199,254,740.991K");
  assert.equal(defaultContextSummary(undefined), null);
  for (const defaultContextTokens of [-1, 0, 1.5, Infinity, NaN, Number.MAX_SAFE_INTEGER + 1])
    assert.equal(defaultContextSummary({ defaultContextTokens }), null);
});

test("ACP keeps its existing context option", () => {
  assert.equal(hasProviderContextOption([{ id: "context", name: "Context size" }]), true);
  assert.equal(hasProviderContextOption([{ id: "fast", name: "Fast mode" }]), false);
});

test("draft meter uses host-resolved input budgets, never raw capacities or unverified choices", () => {
  const model = { contextSizes: [272000, 922000], defaultContextTokens: 272000, contextTokens: 1050000,
    defaultInputTokens: 272000, contextInputTokens: { "272000": 272000, "922000": 922000 } };
  assert.equal(draftContextInputTokens(model, undefined), 272000);
  assert.equal(draftContextInputTokens(model, { tokens: 922000 }), 922000);
  assert.equal(draftContextInputTokens(model, { tokens: null }), 272000);
  assert.equal(draftContextInputTokens({ ...model, defaultInputTokens: 922000 }, null), 922000, "Default follows the provider preference");
  assert.equal(draftContextInputTokens({ ...model, defaultInputTokens: 258400,
    contextInputTokens: { "922000": 875900 } }, { tokens: 922000 }), 875900, "headroom is resolved by the host, not duplicated in the UI");
  assert.equal(draftContextInputTokens(model, { tokens: 500000 }), null);
  assert.equal(draftContextInputTokens(undefined, { tokens: 922000 }), null);
  assert.equal(draftContextInputTokens({ ...model, defaultInputTokens: null, contextInputTokens: { "922000": null } }, null), null);
  assert.equal(draftContextInputTokens({ ...model, contextInputTokens: { "922000": null } }, { tokens: 922000 }), null);
  assert.equal(draftContextInputTokens({ contextSizes: model.contextSizes, defaultContextTokens: 272000 }, null), null);
  for (const tokens of [-1, 0, 1.5, Infinity, NaN, Number.MAX_SAFE_INTEGER + 1])
    assert.equal(draftContextInputTokens({ ...model, defaultInputTokens: tokens }, null), null);
});
