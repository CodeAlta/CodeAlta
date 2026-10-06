import assert from "node:assert/strict";
import test from "node:test";
import { commandCategories, commandDefinitions, commandKeys, resolveCommandKey, searchCommands, type CommandKey } from "./commandRegistry";

const key = (value: string, modifiers: Partial<CommandKey> = {}): CommandKey => ({ key: value, ...modifiers });
const ctrl = (value: string, modifiers: Partial<CommandKey> = {}) => key(value, { ctrlKey: true, ...modifiers });

test("every command has a unique id, slash name and gesture, and a known category", () => {
  const ids = commandDefinitions.map(command => command.id);
  const names = commandDefinitions.map(command => command.name);
  const gestures = commandDefinitions.flatMap(command => command.keys ?? []);
  assert.equal(new Set(ids).size, ids.length);
  assert.equal(new Set(names).size, names.length);
  assert.equal(new Set(gestures.map(gesture => gesture.toLowerCase())).size, gestures.length);
  assert.ok(commandDefinitions.every(command => commandCategories.includes(command.category) && /^[a-z_]+$/.test(command.name)));
});

test("single-stroke gestures follow the terminal key map and work inside the prompt editor", () => {
  for (const focus of ["prompt", "text", "none"] as const) {
    assert.equal(resolveCommandKey(ctrl("ArrowLeft", { altKey: true }), false, focus).command, "previousTab");
    assert.equal(resolveCommandKey(ctrl("ArrowRight", { altKey: true }), false, focus).command, "nextTab");
    assert.equal(resolveCommandKey(ctrl("w"), false, focus).command, "closeTab");
    // The key a terminal leaves to the application: Ctrl+W is the shell's.
    assert.equal(resolveCommandKey(ctrl("W", { shiftKey: true }), false, focus).command, "closeTab");
    assert.equal(resolveCommandKey(ctrl("T", { shiftKey: true }), false, focus).command, "reopenTab");
    assert.equal(resolveCommandKey(ctrl("t"), false, focus).command, "nextPrompt");
    assert.equal(resolveCommandKey(ctrl("p"), false, focus).command, "palette");
    assert.equal(resolveCommandKey(ctrl("o"), false, focus).command, "openProject");
    assert.equal(resolveCommandKey(ctrl("e"), false, focus).command, "editFile");
    assert.equal(resolveCommandKey(key("F8"), false, focus).command, "abort");
    assert.equal(resolveCommandKey(key("F10"), false, focus).command, "clearQueue");
    assert.equal(resolveCommandKey(ctrl("F11"), false, focus).command, "compact");
    assert.equal(resolveCommandKey(key("F3"), false, focus).command, "messagePrevious");
    assert.equal(resolveCommandKey(ctrl("F4"), false, focus).command, "messageLatest");
    assert.equal(resolveCommandKey(key("F6"), false, focus).command, "expandPrompt");
    assert.equal(resolveCommandKey(key("F1"), false, focus).command, "help");
    // A new terminal, with the key it has in most editors and with a chord.
    assert.equal(resolveCommandKey(ctrl("`"), false, focus).command, "newTerminal");
    assert.deepEqual(resolveCommandKey(ctrl("j"), true, focus), { command: "newTerminal", chord: false, handled: true });
  }
});

test("caret and typing keys stay with text fields and only navigate outside text", () => {
  assert.equal(resolveCommandKey(key("ArrowUp", { altKey: true }), false, "none").command, "previousSession");
  assert.deepEqual(resolveCommandKey(key("ArrowUp", { altKey: true }), false, "prompt"), { command: null, chord: false, handled: false });
  assert.equal(resolveCommandKey(ctrl("f"), false, "none").command, "searchSessions");
  assert.equal(resolveCommandKey(ctrl("f"), false, "prompt").handled, false);
  assert.equal(resolveCommandKey(key("F2"), false, "text").handled, false);
  for (const typed of ["a", "?", "/", "Enter", "Escape", "ArrowDown"])
    assert.equal(resolveCommandKey(key(typed), false, "prompt").handled, false, typed);
});

test("Ctrl+G starts a chord completed with or without Ctrl; an unknown second key is swallowed", () => {
  assert.deepEqual(resolveCommandKey(ctrl("g"), false, "prompt"), { command: null, chord: true, handled: true });
  // Releasing and pressing modifiers between the strokes does not end the chord.
  assert.deepEqual(resolveCommandKey(key("Control", { ctrlKey: true }), true, "prompt"), { command: null, chord: true, handled: false });
  const expected: [string, string][] = [["t", "sessionInfo"], ["d", "reminders"], ["u", "usage"], ["o", "models"], ["r", "providers"],
    ["h", "prompts"], ["k", "skills"], ["n", "plugins"], ["y", "mcp"], ["l", "logs"], ["a", "about"], ["w", "settings"],
    ["s", "focusSidebar"], ["p", "focusPrompt"], ["g", "toggleNavigator"]];
  for (const [second, command] of expected) {
    assert.deepEqual(resolveCommandKey(ctrl(second), true, "prompt"), { command, chord: false, handled: true }, second);
    assert.deepEqual(resolveCommandKey(key(second), true, "none"), { command, chord: false, handled: true }, second);
  }
  assert.deepEqual(resolveCommandKey(key("z"), true, "prompt"), { command: null, chord: false, handled: true });
  assert.equal(resolveCommandKey(ctrl("t", { shiftKey: true }), true, "none").handled, false);
});

test("composition, already handled keys and held keys never run a command", () => {
  assert.equal(resolveCommandKey(ctrl("w", { isComposing: true }), false, "prompt").handled, false);
  assert.equal(resolveCommandKey(ctrl("w", { keyCode: 229 }), false, "prompt").handled, false);
  assert.equal(resolveCommandKey(ctrl("w", { defaultPrevented: true }), false, "prompt").handled, false);
  assert.deepEqual(resolveCommandKey(ctrl("w", { repeat: true }), false, "none"), { command: null, chord: false, handled: true });
  assert.equal(resolveCommandKey(key("w", { metaKey: true }), false, "none").handled, false);
});

test("help lists bindings then typed hints", () => {
  const help = commandDefinitions.find(command => command.id === "help")!;
  assert.deepEqual(commandKeys(help), ["F1", "?"]);
  assert.deepEqual(commandKeys(commandDefinitions.find(command => command.id === "send")!), ["Enter"]);
});

test("palette search ranks the slash name first, needs every word and hides internal commands", () => {
  const label = (command: { label: string }) => command.label;
  const description = (command: { description: string }) => command.description;
  const names = (query: string) => searchCommands(query, label, description).map(command => command.name);
  assert.equal(names("").length, commandDefinitions.filter(command => !command.hidden).length);
  assert.ok(!names("").includes("steer"));
  assert.equal(names("models")[0], "models");
  assert.equal(names("/models")[0], "models");
  assert.deepEqual(names("model").slice(0, 2), ["model", "model_providers"]);
  assert.equal(names("tab left")[0], "tab_left");
  assert.equal(names("stop")[0], "abort");
  assert.equal(names("/edit")[0], "edit");
  assert.equal(names("open file")[0], "edit");
  assert.equal(names("open")[0], "open");
  assert.deepEqual(names("zzz-nothing"), []);
  assert.ok(names("session").includes("session_info") && names("session").includes("new_session"));
});
