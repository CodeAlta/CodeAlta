import assert from "node:assert/strict";
import { existsSync, readFileSync } from "node:fs";
import test from "node:test";
import * as demo from "./demo-api";
import { boot, configuration, workspace } from "./demo-api";
import { listedSchemes } from "./colorSchemeLibrary";

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

test("browser demo keeps the user's color schemes in the browser", async () => {
  const stored = new Map<string, string>();
  const storage = { getItem: (key: string) => stored.get(key) ?? null, setItem: (key: string, value: string) => { stored.set(key, value); } };
  const before = Object.getOwnPropertyDescriptor(globalThis, "localStorage");
  Object.defineProperty(globalThis, "localStorage", { value: storage, configurable: true });
  try {
    const none = { background: null, text: null, muted: null, accent: null, success: null, warning: null, danger: null };
    assert.deepEqual(await demo.colorSchemes.list(), { status: "ok", directory: null, schemes: [], problems: [] });
    const saved = await demo.colorSchemes.save({ id: null, name: " Deep Sea ", base: "plum", light: null, dark: { ...none, background: "#0b1d2a" }, darker: null });
    assert.deepEqual(saved, { status: "ok", id: "deep-sea", message: null });
    assert.equal((await demo.colorSchemes.save({ id: null, name: "Deep sea", base: null, light: null, dark: null, darker: null })).id, "deep-sea-2");
    assert.equal((await demo.colorSchemes.save({ id: "deep-sea", name: "Deeper", base: "plum", light: null, dark: null, darker: null })).status, "ok");
    const listed = (await demo.colorSchemes.list()).schemes;
    assert.deepEqual(listed.map(scheme => [scheme.id, scheme.name, scheme.base]), [["deep-sea", "Deeper", "plum"], ["deep-sea-2", "Deep sea", "blueprint"]]);
    // The page reads them as it reads the host's.
    assert.deepEqual(listedSchemes(listed).map(scheme => scheme.id), ["deep-sea", "deep-sea-2"]);
    assert.equal((await demo.colorSchemes.save({ id: null, name: "  ", base: null, light: null, dark: null, darker: null })).status, "invalid");
    assert.equal((await demo.colorSchemes.delete({ id: "deep-sea" })).status, "ok");
    assert.equal((await demo.colorSchemes.delete({ id: "deep-sea" })).status, "not_found");
    assert.deepEqual((await demo.colorSchemes.list()).schemes.map(scheme => scheme.id), ["deep-sea-2"]);
  } finally {
    if (before) Object.defineProperty(globalThis, "localStorage", before); else delete (globalThis as { localStorage?: unknown }).localStorage;
  }
});

test("a listing keeps the entries that are color schemes", () => {
  assert.deepEqual(listedSchemes([{ id: "a", name: "A", base: "kiwi", dark: { accent: "#f80" } }, { id: "../b" }, null, "c"]),
    [{ id: "a", name: "A", base: "kiwi", light: {}, dark: { accent: "#ff8800" }, darker: {} }]);
});
