import assert from "node:assert/strict";
import test from "node:test";
import { paletteAvailable, paletteCommands, type PaletteContext } from "./paletteActions";

const catalog: PaletteContext = { workspace: true, selection: { sessionId: "one", projectId: null },
  epoch: null, infoReady: true, promptReady: true, searchReady: true };

test("cached model command requires an actual enabled exact composer target", () => {
  const trigger = {} as HTMLButtonElement;
  const current = { ...catalog, epoch: "epoch", modelChooserReady: true, modelChooserTrigger: trigger };
  assert.ok(paletteCommands(current).some(command => command.id === "chooseModel"));
  for (const changed of [{ ...current, epoch: null }, { ...current, modelChooserReady: false },
    { ...current, modelChooserTrigger: {} as HTMLButtonElement }, { ...current, selection: null }]) {
    assert.equal(paletteAvailable("chooseModel", current, changed), false);
  }
  assert.equal(paletteAvailable("chooseModel", catalog, catalog), false);
});

test("implemented inspection and shell entries require original generation and scope", () => {
  const current = { ...catalog, epoch: "original", commandGeneration: 7, accessScope: "exact-project-path",
    skillsReady: true, usageReady: true, shellReady: true };
  const ids = paletteCommands(current).map(command => command.id);
  for (const id of ["skills", "usage", "openProject", "help"] as const) {
    assert.ok(ids.includes(id), `missing implemented command ${id}`);
    assert.equal(paletteAvailable(id, current, { ...current, commandGeneration: 9 }), false, "ABA cannot revive capture");
  }
  for (const id of ["skills", "usage"] as const) {
    assert.equal(paletteAvailable(id, current, { ...current, accessScope: "replacement" }), false);
    assert.equal(paletteAvailable(id, current, { ...current, epoch: null }), false);
    assert.equal(paletteAvailable(id, current, { ...current, usageReady: false, skillsReady: false }), false);
  }
  for (const id of ["logs", "openProject", "help"] as const)
    assert.equal(paletteAvailable(id, current, { ...current, epoch: null }), false, "revocation without a generation change still rejects original capture");
});

test("local draft focus is scoped and never grants session inspection actions", () => {
  const draft = { ...catalog, selection: null, localDraftScope: 'local-draft:"project"' };
  assert.equal(paletteAvailable("focusPrompt", draft, draft), true);
  assert.equal(paletteAvailable("focusPrompt", draft, { ...draft, localDraftScope: "local-draft:null" }), false);
  assert.equal(paletteAvailable("focusPrompt", draft, catalog), false);
  assert.equal(paletteAvailable("sessionInfo", draft, draft), false);
  assert.equal(paletteAvailable("reminders", draft, draft), false);
});

test("palette only lists implemented navigation and currently available inspection/focus actions", () => {
  const global = paletteCommands({ ...catalog, workspace: false, selection: null, infoReady: false,
    promptReady: false, searchReady: false }).map(command => command.label);
  assert.deepEqual(global, ["Settings", "About", "Application Logs", "Providers", "Models", "Agent Prompts", "MCP Servers"]);
  assert.deepEqual(paletteCommands(catalog).map(command => command.label), [...global,
    "Session Info", "Focus prompt", "Focus session search"]);
  assert.deepEqual(paletteCommands({ ...catalog, epoch: "e1" }).map(command => command.label), [...global,
    "Reminders", "Session Info", "Focus prompt", "Focus session search"]);
});

test("selected commands recheck captured session/project/host and real focus targets", () => {
  const captured = { ...catalog, epoch: "e1" };
  assert.equal(paletteAvailable("logs", { ...catalog, workspace: false }, { ...catalog, workspace: false }), true);
  assert.equal(paletteAvailable("about", captured, { ...captured, epoch: "e2", workspace: false }), true);
  assert.equal(paletteAvailable("reminders", captured, { ...captured, epoch: "e2" }), false);
  assert.equal(paletteAvailable("reminders", captured, { ...captured, selection: { sessionId: "two", projectId: null } }), false);
  assert.equal(paletteAvailable("reminders", captured, { ...captured, selection: { sessionId: "one", projectId: "project" } }), false);
  assert.equal(paletteAvailable("sessionInfo", captured, { ...captured, epoch: null }), true);
  assert.equal(paletteAvailable("sessionInfo", captured, { ...captured, infoReady: false }), false);
  assert.equal(paletteAvailable("focusPrompt", captured, { ...captured, promptReady: false }), false);
  assert.equal(paletteAvailable("focusPrompt", captured, { ...captured, selection: { sessionId: "two", projectId: null } }), false);
  assert.equal(paletteAvailable("focusPrompt", captured, { ...captured, epoch: "e2" }), false);
  assert.equal(paletteAvailable("focusSearch", captured, { ...captured, workspace: false }), false);
});
