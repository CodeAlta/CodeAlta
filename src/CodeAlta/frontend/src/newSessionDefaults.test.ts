import assert from "node:assert/strict";
import test from "node:test";
import { defaultModelId, defaultReasoningEffort } from "./newSessionDefaults";

const model = (id: string, efforts: string[] = [], defaultEffort: string | null = null) => ({ id, efforts, defaultEffort });

test("a new session starts with the configured model when offered, else the first model", () => {
  const models = [model("a"), model("b")];
  assert.equal(defaultModelId(models, "b"), "b");
  assert.equal(defaultModelId(models, "missing"), "a");
  assert.equal(defaultModelId(models, null), "a");
  assert.equal(defaultModelId([], "b"), null);
});

test("the starting reasoning effort prefers the configured one, then High, the model default and the first effort", () => {
  assert.equal(defaultReasoningEffort(model("a", ["Low", "Medium", "High", "XHigh"], "Medium"), "xhigh"), "XHigh");
  assert.equal(defaultReasoningEffort(model("a", ["Low", "Medium", "High"], "Medium"), "max"), "High");
  assert.equal(defaultReasoningEffort(model("a", ["low", "medium"], "medium")), "medium");
  assert.equal(defaultReasoningEffort(model("a", ["minimal", "low"], "none")), "minimal");
  assert.equal(defaultReasoningEffort(model("a", [], "medium")), null);
  assert.equal(defaultReasoningEffort(undefined, "high"), null);
});
