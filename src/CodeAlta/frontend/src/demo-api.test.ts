import assert from "node:assert/strict";
import test from "node:test";
import { boot, configuration, workspace } from "./demo-api";

test("browser demo loads without a host or external data", async () => {
  const status = await boot.status();
  const snapshot = await workspace.snapshot();

  assert.equal(status.state, "demo");
  assert.equal(status.hostAvailable, false);
  assert.equal(snapshot.configured, true);
  assert.ok(snapshot.projects.length >= 2);
  assert.ok(snapshot.sessions.some(session => session.workspacePath === snapshot.projects[0].path));
  const inventory = await configuration.snapshot();
  assert.ok(inventory.providers.some(provider => provider.enabled));
  assert.ok(inventory.plugins.some(plugin => plugin.state === "Active"));
});

test("browser demo history follows the selected session and has no paging", async () => {
  const page = await workspace.history({ sessionId: "chosen-session", cursor: null });

  assert.equal(page.status, "ok");
  assert.equal(page.next, null);
  assert.ok(page.entries.length >= 2);
  assert.ok(page.entries.every(entry => entry.sessionId === "chosen-session"));
});
