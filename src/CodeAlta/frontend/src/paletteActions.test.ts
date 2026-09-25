import assert from "node:assert/strict";
import test from "node:test";
import { paletteAvailable, paletteCommands, type PaletteContext } from "./paletteActions";

const catalog: PaletteContext = { workspace: true, selection: { sessionId: "one", projectId: null },
  epoch: null, infoReady: true, promptReady: true, searchReady: true };

test("palette only lists implemented navigation and currently available inspection/focus actions", () => {
  const global = paletteCommands({ ...catalog, workspace: false, selection: null, infoReady: false,
    promptReady: false, searchReady: false }).map(command => command.label);
  assert.deepEqual(global, ["Settings", "Providers", "Models", "Agent Prompts", "MCP Servers"]);
  assert.deepEqual(paletteCommands(catalog).map(command => command.label), [...global,
    "Session Info", "Focus prompt", "Focus session search"]);
  assert.deepEqual(paletteCommands({ ...catalog, epoch: "e1" }).map(command => command.label), [...global,
    "Reminders", "Session Info", "Focus prompt", "Focus session search"]);
});

test("selected commands recheck captured session/project/host and real focus targets", () => {
  const captured = { ...catalog, epoch: "e1" };
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
