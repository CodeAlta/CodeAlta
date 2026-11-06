import assert from "node:assert/strict";
import test from "node:test";
import { orderModels, parseModel } from "./modelOrder";

test("model ids are read as a family, a version and a variant suffix", () => {
  assert.deepEqual(parseModel("claude-opus-4-7"), { family: "claude-opus", version: [4, 7], suffix: "" });
  assert.deepEqual(parseModel("claude-4.5-opus"), { family: "claude-opus", version: [4, 5], suffix: "" });
  assert.deepEqual(parseModel("cursor-kimi-k3"), { family: "kimi", version: [3], suffix: "" });
  assert.deepEqual(parseModel("gpt-6.1-sol-fast"), { family: "gpt-sol", version: [6, 1], suffix: "fast" });
  assert.deepEqual(parseModel("gemini-3.8-flash"), { family: "gemini-flash", version: [3, 8], suffix: "" });
  assert.deepEqual(parseModel("anthropic/claude-opus-4.7"), { family: "anthropic-claude-opus", version: [4, 7], suffix: "" });
  assert.deepEqual(parseModel("claude-sonnet-4-5-20250929"), { family: "claude-sonnet", version: [4, 5], suffix: "" }, "a release date is no version");
});

test("the frontier holds the latest version of each family, the rest goes to other models", () => {
  const ids = ["claude-opus-4-7", "claude-sonnet-4-5", "claude-opus-4-1", "gpt-6.1", "gpt-6.1-fast", "gpt-5"];
  const { frontier, other } = orderModels(ids.map(id => ({ id })));
  assert.deepEqual(frontier.map(m => m.id), ["claude-opus-4-7", "claude-sonnet-4-5", "gpt-6.1", "gpt-6.1-fast"]);
  assert.deepEqual(other.map(m => m.id), ["claude-opus-4-1", "gpt-5"]);
});

test("each part is sorted by vendor, then by version descending, then by family", () => {
  const ids = ["gemini-3.8-flash", "gemini-3-argon", "gemini-4-argon", "gemini-3.5-flash", "gemini-3.8-pro", "gemini-3.8-flash-thinking"];
  const { frontier, other } = orderModels(ids.map(id => ({ id })));
  assert.deepEqual(frontier.map(m => m.id), ["gemini-4-argon", "gemini-3.8-flash", "gemini-3.8-flash-thinking", "gemini-3.8-pro"]);
  assert.deepEqual(other.map(m => m.id), ["gemini-3.5-flash", "gemini-3-argon"]);
  const mixed = orderModels(["gpt-6.1-sol", "gemini-4-argon", "claude-sonnet-4-5", "gemini-3.8-flash"].map(id => ({ id })));
  assert.deepEqual(mixed.frontier.map(m => m.id), ["claude-sonnet-4-5", "gemini-4-argon", "gemini-3.8-flash", "gpt-6.1-sol"], "vendors of a multi-vendor provider stay together");
});
