import assert from "node:assert/strict";
import test from "node:test";
import type { CommandKey } from "./commandRegistry";
import { collectPluginFields, pluginMarkdownSource } from "./pluginHtmlSanitizer";
import { activePluginReference, findPluginCommand, insertPluginReference, pluginCommandAvailable, pluginContributions, pluginGesture, pluginKeymap, pluginRegions,
  resolvePluginKey, samePluginRegions, searchPluginCommands, type PluginCommandView, type PluginPane } from "./pluginUi";

const command = (patch: Partial<PluginCommandView> = {}): PluginCommandView => ({ id: "source:Sample/command:hello", pluginKey: "source:Sample", plugin: "Sample", name: "hello",
  label: "Say hello", description: "Shows a greeting.", group: null, search: null, keys: null, palette: true, help: true,
  needsProject: false, needsSession: false, needsIdle: false, needsBusy: false, ...patch });
const pane = (patch: Partial<PluginPane> = {}): PluginPane => ({ projectId: "project", sessionId: "session", busy: false, draftText: null, ...patch });
const key = (value: string, modifiers: Partial<CommandKey> = {}): CommandKey => ({ key: value, ...modifiers });

test("a key binding is one stroke, or a second stroke after Ctrl+G", () => {
  for (const keys of ["F9", "Ctrl+Shift+K", "Alt+Up", "Ctrl+G Ctrl+Y", "Ctrl+G Y", "Ctrl+Space"]) assert.equal(pluginGesture(keys), keys, keys);
  for (const keys of ["", "K", "Shift+K", "Ctrl+K Ctrl+Y", "Ctrl+G Ctrl+Y Ctrl+Z", "Meta+K", "Ctrl+Ctrl+K", "Ctrl+", "Ctrl+Unknown", 12, null])
    assert.equal(pluginGesture(keys), null, String(keys));
});

test("contributions are taken for the asked project and malformed entries are dropped", () => {
  const reply = { status: "ok", projectId: "project", regions: true,
    commands: [command({ keys: "Ctrl+G Ctrl+Y" }), command(), command({ id: "other", name: "bad name" }), command({ id: "third", name: "third", label: "", keys: "K" }), null],
    pickers: [{ id: "p1", plugin: "Sample", trigger: "!", title: "Things", placeholder: "" }, { id: "p2", plugin: "Sample", trigger: "!", title: "Again", placeholder: null },
      { id: "p3", plugin: "Sample", trigger: "@", title: "Taken", placeholder: null }, { id: "p4", plugin: "Sample", trigger: "ab", title: "Long", placeholder: null }] };
  const view = pluginContributions(reply, "project");
  assert.deepEqual(view?.commands.map(item => [item.id, item.label, item.keys]), [["source:Sample/command:hello", "Say hello", "Ctrl+G Ctrl+Y"], ["third", "third", null]]);
  assert.deepEqual(view?.pickers, [{ id: "p1", plugin: "Sample", trigger: "!", title: "Things", placeholder: null }]);
  assert.equal(view?.regions, true);
  assert.equal(pluginContributions(reply, "another"), null);
  assert.equal(pluginContributions({ ...reply, status: "unavailable" }, "project"), null);
  assert.equal(pluginContributions({ ...reply, commands: "x" }, "project"), null);
  assert.equal(pluginContributions(null, null), null);
  assert.deepEqual(pluginContributions({ status: "ok", projectId: null, commands: [], pickers: [], regions: false }, null), { commands: [], pickers: [], regions: false });
});

test("region content keeps one form and is taken for the asked pane only", () => {
  const item = (patch: Record<string, unknown>) => ({ id: "a", pluginKey: "source:Sample", region: "footer", html: null, markdown: null, text: null, ...patch });
  const reply = { status: "ok", projectId: "project", sessionId: null, items: [item({ html: "<b>x</b>", markdown: "**x**", text: "x" }),
    item({ id: "b", region: "status", markdown: "**y**", text: "y" }), item({ id: "c", region: "bar", text: "z" }), item({ id: "d" }), item({ id: "e", region: "header", text: "no" }),
    item({ text: "same id" })] };
  const items = pluginRegions(reply, "project", null);
  assert.deepEqual(items, [{ id: "a", pluginKey: "source:Sample", region: "footer", html: "<b>x</b>", markdown: null, text: null, script: null, scriptProblem: null },
    { id: "b", pluginKey: "source:Sample", region: "status", html: null, markdown: "**y**", text: null, script: null, scriptProblem: null },
    { id: "c", pluginKey: "source:Sample", region: "bar", html: null, markdown: null, text: "z", script: null, scriptProblem: null }]);
  // A script comes with a fragment and is a path the host serves; anything else is dropped, and a problem the host reports is kept.
  const scripted = pluginRegions({ status: "ok", projectId: null, sessionId: null, items: [
    item({ id: "s1", html: "<p>x</p>", script: "/plugin/abc/def/main.js" }), item({ id: "s2", html: "<p>x</p>", script: "https://evil.example/x.js" }),
    item({ id: "s3", html: "<p>x</p>", script: "/plugin/abc/def/../x.js" }), item({ id: "s4", text: "no fragment", script: "/plugin/abc/def/main.js" }),
    item({ id: "s5", html: "<p>x</p>", scriptProblem: "The script of the content could not be found." })] }, null, null)!;
  assert.deepEqual(scripted.map(value => [value.id, value.script, value.scriptProblem]), [["s1", "/plugin/abc/def/main.js", null], ["s2", null, null], ["s3", null, null], ["s4", null, null],
    ["s5", null, "The script of the content could not be found."]]);
  assert.equal(samePluginRegions(scripted.slice(0, 1), [{ ...scripted[0], script: "/plugin/abc/other/main.js" }]), false, "a reloaded plugin is a change");
  assert.equal(pluginRegions(reply, "project", "session"), null);
  assert.equal(pluginRegions({ ...reply, status: "stale" }, "project", null), null);
  assert.ok(samePluginRegions(items!, pluginRegions(reply, "project", null)!));
  assert.equal(samePluginRegions(items!, items!.slice(1)), false);
  assert.equal(samePluginRegions(items!, items!.map(value => value.id === "c" ? { ...value, text: "changed" } : value)), false);
});

test("a command runs only where its needs are met", () => {
  assert.ok(pluginCommandAvailable(command(), pane({ projectId: null, sessionId: null })));
  assert.equal(pluginCommandAvailable(command({ needsProject: true }), pane({ projectId: null })), false);
  assert.equal(pluginCommandAvailable(command({ needsSession: true }), pane({ sessionId: null })), false);
  assert.equal(pluginCommandAvailable(command({ needsIdle: true }), pane({ busy: true })), false);
  assert.equal(pluginCommandAvailable(command({ needsBusy: true }), pane()), false);
  assert.ok(pluginCommandAvailable(command({ needsProject: true, needsSession: true, needsIdle: true }), pane()));
});

test("a fragment names a command of its own plugin first", () => {
  const commands = [command({ id: "a", pluginKey: "source:Other" }), command({ id: "b" }), command({ id: "c", name: "bye" })];
  assert.equal(findPluginCommand(commands, "HELLO", "source:Sample")?.id, "b");
  assert.equal(findPluginCommand(commands, "hello", null)?.id, "a");
  assert.equal(findPluginCommand(commands, "bye", "source:Other")?.id, "c");
  assert.equal(findPluginCommand(commands, "missing", null), null);
});

test("the palette finds plugin commands by name, label, plugin and description", () => {
  const commands = [command(), command({ id: "b", name: "report", label: "Weekly report", description: "Says hello to the team.", search: "summary" }),
    command({ id: "c", name: "hidden", palette: false })];
  assert.deepEqual(searchPluginCommands("", commands).map(item => item.id), ["source:Sample/command:hello", "b"]);
  assert.deepEqual(searchPluginCommands("/hello", commands).map(item => item.id), ["source:Sample/command:hello", "b"]);
  assert.deepEqual(searchPluginCommands("summary", commands).map(item => item.id), ["b"]);
  assert.deepEqual(searchPluginCommands("sample rep", commands).map(item => item.id), ["b"]);
  assert.deepEqual(searchPluginCommands("hidden", commands), []);
});

test("plugin shortcuts apply to keys the window does not take", () => {
  const first = command({ id: "f9", keys: "F9" }), chord = command({ id: "chord", keys: "Ctrl+G Ctrl+Y" }), arrow = command({ id: "arrow", keys: "Alt+Up" });
  const keymap = pluginKeymap([first, command({ id: "late", keys: "F9" }), chord, arrow, command({ id: "plain", keys: "Shift+F2" })]);
  assert.equal(resolvePluginKey(key("F9"), false, "prompt", keymap)?.id, "f9");
  assert.equal(resolvePluginKey(key("ArrowUp", { altKey: true }), false, "text", keymap)?.id, "arrow");
  assert.equal(resolvePluginKey(key("ArrowUp"), false, "none", keymap), null);
  assert.equal(resolvePluginKey(key("F2", { shiftKey: true }), false, "prompt", keymap)?.id, "plain");
  assert.equal(resolvePluginKey(key("y", { ctrlKey: true }), true, "prompt", keymap)?.id, "chord");
  assert.equal(resolvePluginKey(key("y"), true, "prompt", keymap)?.id, "chord");
  assert.equal(resolvePluginKey(key("y", { ctrlKey: true }), false, "prompt", keymap), null);
  assert.equal(resolvePluginKey(key("F9", { repeat: true }), false, "none", keymap), null);
  assert.equal(resolvePluginKey(key("F9", { metaKey: true }), false, "none", keymap), null);
  assert.equal(resolvePluginKey(key("Control", { ctrlKey: true }), true, "none", keymap), null);
});

test("a picker opens on its trigger at a word start and replaces the token", () => {
  assert.deepEqual(activePluginReference("!", "!bu", 3), { start: 0, end: 3, query: "bu" });
  assert.deepEqual(activePluginReference("!", "see (!bug-1, then", 8), { start: 5, end: 11, query: "bu" });
  assert.deepEqual(activePluginReference("!", "see !", 5), { start: 4, end: 5, query: "" });
  assert.equal(activePluginReference("!", "wow!bu", 6), null, "inside a word");
  assert.equal(activePluginReference("!", "!a b", 4), null, "the token ended");
  assert.equal(activePluginReference("!", "!!", 1), null, "a trigger follows the caret");
  assert.equal(activePluginReference("!", "nothing", 7), null);
  assert.deepEqual(insertPluginReference("see !bu now", 4, 7, "BUG-12 "), { text: "see BUG-12  now", caret: 11 });
  assert.equal(insertPluginReference("x", 0, 1, ""), null);
  assert.equal(insertPluginReference("x", 0, 1, "y".repeat(4097)), null);
});

test("the named fields of a fragment are returned as text", () => {
  const elements = [
    { nodeName: "INPUT", name: "title", type: "text", value: "Hello" },
    { nodeName: "INPUT", name: "urgent", type: "checkbox", checked: true },
    { nodeName: "INPUT", name: "quiet", type: "checkbox", checked: false },
    { nodeName: "INPUT", name: "size", type: "radio", value: "small", checked: false },
    { nodeName: "INPUT", name: "size", type: "radio", value: "large", checked: true },
    { nodeName: "INPUT", name: "size", type: "radio", value: "huge", checked: false },
    { nodeName: "SELECT", name: "labels", multiple: true, selectedOptions: [{ value: "bug" }, { value: "ui" }] },
    { nodeName: "SELECT", name: "kind", value: "task" },
    { nodeName: "TEXTAREA", name: "body", value: "Text" },
    { nodeName: "INPUT", name: "off", type: "text", value: "x", disabled: true },
    { nodeName: "INPUT", name: "", type: "text", value: "x" },
  ];
  assert.deepEqual(collectPluginFields({ querySelectorAll: () => elements }),
    { title: "Hello", urgent: "true", quiet: "false", size: "large", labels: "bug,ui", kind: "task", body: "Text" });
});

test("the Markdown of a fragment is read without the indentation and the blank lines of the fragment around it", () => {
  // As a plugin writes it inside its HTML: every line carries the indentation of the element it is in.
  const written = "\n      ## Report\n\n      | a | b |\n      |---|---|\n\n      ```csharp\n      if (x)\n          y();\n      ```\n    ";
  assert.equal(pluginMarkdownSource(written), "## Report\n\n| a | b |\n|---|---|\n\n```csharp\nif (x)\n    y();\n```");
  // What a helper wrote has no indentation to remove, and the lines of a file of Windows are lines.
  assert.equal(pluginMarkdownSource("# One\r\n\r\n- a\r\n  - b"), "# One\n\n- a\n  - b");
  // A line that is less indented than the others decides: nothing is cut from the middle of a line.
  assert.equal(pluginMarkdownSource("    a\n  b\n\tc"), "    a\n  b\n\tc");
  assert.equal(pluginMarkdownSource("\t\tx\n\t\t\ty"), "x\n\ty");
  assert.equal(pluginMarkdownSource(""), "");
  assert.equal(pluginMarkdownSource(" \n\t\n"), "");
});
