import assert from "node:assert/strict";
import test from "node:test";
import type { McpServerEntry } from "#neoastra";
import { mcpServerEdit, mcpServerForm, mcpServerFormDirty, scopedKey, settingsFailure, validateMcpServerForm } from "./settingsEditing";

const entry: McpServerEntry = { key: "files", scope: "Project", transport: "Stdio", enabled: true, command: "npx", arguments: ["-y", "server", "[redacted]"],
  argumentsRedacted: true, workingDirectory: null, url: null, urlRedacted: false, disabledTools: [], overridesGlobal: false, shadowed: false,
  environment: [{ name: "TOKEN", hasValue: true }, { name: "EMPTY", hasValue: false }], headers: [], authorized: false, authorizationExpiresAt: null };

test("a failed settings call explains itself, success is silent", () => {
  assert.equal(settingsFailure("ok"), null);
  assert.equal(settingsFailure("stale_epoch")!.intent, "warning");
  assert.deepEqual(settingsFailure("invalid", "bad key"), { key: "The values were not accepted.", intent: "danger", detail: "bad key" });
  assert.equal(settingsFailure("archived_project")!.key, settingsFailure("unknown_project")!.key);
  assert.equal(settingsFailure("something_new")!.key, "The operation did not complete.");
});

test("an existing server maps to a form without revealing stored values, and back to an edit that keeps them", () => {
  const form = mcpServerForm(entry);
  assert.equal(form.scope, "Project");
  assert.equal(form.arguments, "-y\nserver\n[redacted]");
  assert.deepEqual(form.environment, [{ name: "TOKEN", value: "", stored: true }, { name: "EMPTY", value: "", stored: false }]);
  assert.equal(mcpServerFormDirty(form, mcpServerForm(entry)), false);
  const edit = mcpServerEdit({ ...form, command: " npx ", arguments: "-y\n\n server \n[redacted]\n" });
  assert.equal(edit.command, "npx");
  assert.deepEqual(edit.arguments, ["-y", "server", "[redacted]"]);
  assert.deepEqual(edit.environment, [{ name: "TOKEN", value: null }, { name: "EMPTY", value: "" }]);
  assert.equal(edit.url, null);
  assert.equal(edit.headers, null);
  assert.equal(mcpServerFormDirty({ ...form, enabled: false }, form), true);
});

test("an HTTP server sends its URL and headers only", () => {
  const edit = mcpServerEdit({ ...mcpServerForm(null), key: "remote", transport: "Http", url: " https://example.test/mcp ", command: "ignored",
    headers: [{ name: " Authorization ", value: "Bearer x", stored: false }], environment: [{ name: "X", value: "1", stored: false }] });
  assert.deepEqual(edit, { key: "remote", transport: "Http", enabled: true, command: null, arguments: null, workingDirectory: null,
    url: "https://example.test/mcp", environment: null, headers: [{ name: "Authorization", value: "Bearer x" }] });
});

test("validation names the first problem and allows keeping an item's own name", () => {
  const blank = mcpServerForm(null);
  assert.match(validateMcpServerForm(blank, [], null)!, /1 to 128/);
  assert.match(validateMcpServerForm({ ...blank, key: "bad name" }, [], null)!, /1 to 128/);
  assert.match(validateMcpServerForm({ ...blank, key: "a" }, [], null)!, /command/);
  assert.match(validateMcpServerForm({ ...blank, key: "a", transport: "Http", url: "ftp://x" }, [], null)!, /URL/);
  assert.equal(validateMcpServerForm({ ...blank, key: "a", transport: "Http", url: "[redacted]" }, [], null), null);
  assert.match(validateMcpServerForm({ ...blank, key: "files", scope: "Project", command: "x" }, [entry], null)!, /already exists/);
  assert.equal(validateMcpServerForm({ ...blank, key: "files", scope: "Global", command: "x" }, [entry], null), null);
  assert.equal(validateMcpServerForm(mcpServerForm(entry), [entry], entry), null);
  assert.match(validateMcpServerForm({ ...mcpServerForm(entry), environment: [{ name: "", value: "1", stored: false }] }, [entry], entry)!, /needs a name/);
  assert.match(validateMcpServerForm({ ...mcpServerForm(entry), environment: [{ name: "A", value: "1", stored: false }, { name: "A", value: "2", stored: false }] }, [entry], entry)!, /unique/);
  assert.notEqual(scopedKey("Global", "a"), scopedKey("Project", "a"));
});
