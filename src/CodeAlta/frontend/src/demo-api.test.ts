import assert from "node:assert/strict";
import { existsSync, readFileSync } from "node:fs";
import test from "node:test";
import * as demo from "./demo-api";
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

test("browser demo stands in for every service of the generated client", t => {
  // The page imports its services by name: one that the demo lacks keeps the whole page from loading.
  const generated = new URL("../../obj/neoastra/neoastra.ts", import.meta.url);
  if (!existsSync(generated)) { t.skip("the generated client is written by the build"); return; }
  const services = [...readFileSync(generated, "utf8").matchAll(/^export const (\w+) = Object\.freeze\(/gm)].map(match => match[1]);
  assert.ok(services.length > 20);
  assert.deepEqual(services.filter(name => !(name in demo)), []);
});
