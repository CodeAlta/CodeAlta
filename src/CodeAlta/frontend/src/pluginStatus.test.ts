import assert from "node:assert/strict";
import test from "node:test";
import { composerStatusItems, sameComposerStatus } from "./pluginStatus";

const mcp = { pluginId: "mcp", name: "mcp-status", label: "MCP", text: "2/3 · 1 unavailable · tools not loaded", tone: "warning", settingsPage: "mcp" };

test("composer status accepts the items of the asked project", () => {
  assert.deepEqual(composerStatusItems({ status: "ok", projectId: "p1", items: [mcp] }, "p1"), [
    { key: "mcp/mcp-status", pluginId: "mcp", label: "MCP", text: "2/3 · 1 unavailable · tools not loaded", tone: "warning", settingsPage: "mcp" }]);
  assert.deepEqual(composerStatusItems({ status: "ok", projectId: null, items: [] }, null), []);
  assert.deepEqual(composerStatusItems({ status: "ok", items: [] }, null), []);
});

test("composer status refuses another project's answer, refusals and malformed answers", () => {
  assert.equal(composerStatusItems({ status: "ok", projectId: "p2", items: [mcp] }, "p1"), null);
  assert.equal(composerStatusItems({ status: "stale_epoch", projectId: "p1", items: [] }, "p1"), null);
  assert.equal(composerStatusItems({ status: "ok", projectId: "p1" }, "p1"), null);
  assert.equal(composerStatusItems(null, "p1"), null);
});

test("composer status leaves out malformed and repeated items and defaults an unknown tone", () => {
  const items = composerStatusItems({ status: "ok", projectId: null, items: [
    mcp, mcp, null, { ...mcp, name: "other", tone: "loud", settingsPage: "../x" }, { ...mcp, name: "bad", text: "two\nlines" },
    { ...mcp, name: "empty", label: "", text: "" }, { ...mcp, pluginId: "has space" }] }, null);
  assert.deepEqual(items?.map(item => [item.key, item.tone, item.settingsPage]), [["mcp/mcp-status", "warning", "mcp"], ["mcp/other", "info", null]]);
});

test("composer status compares what is shown", () => {
  const [item] = composerStatusItems({ status: "ok", projectId: null, items: [mcp] }, null)!;
  assert.equal(sameComposerStatus([item], [{ ...item }]), true);
  assert.equal(sameComposerStatus([item], [{ ...item, text: "3/3 · tools not loaded" }]), false);
  assert.equal(sameComposerStatus([item], []), false);
});
