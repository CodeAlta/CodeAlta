import assert from "node:assert/strict";
import test from "node:test";
import type { TerminalItem } from "#neoastra";
import { applicationKey, folderName, terminalKeyAction, terminalTabLabel, terminalTheme, terminalsOf, type TerminalKey } from "./terminals";
import { defaultTerminalLook, maximumTerminalFontSize, minimumTerminalFontSize, persistTerminalLook, restoreTerminalLook } from "./terminalLook";

const key = (name: string, modifiers = ""): TerminalKey => ({ key: name, ctrlKey: modifiers.includes("c"), altKey: modifiers.includes("a"), shiftKey: modifiers.includes("s"), metaKey: modifiers.includes("m") });
const terminal = (id: string, projectId: string | null, more: Partial<TerminalItem> = {}): TerminalItem => ({
  id, projectId, sessionId: null, title: "C:\\code\\app", titled: false, folder: "C:\\code\\app", profile: "pwsh", profileName: "PowerShell", programTitle: null,
  running: true, exitCode: null, integrated: true, busy: false, command: null, lastExitCode: null, columns: 120, rows: 30, created: "2026-10-06T00:00:00Z",
  open: false, attention: false, agent: false, ...more });

test("a tab names its terminal by its title, or by the folder it is in", () => {
  assert.equal(folderName("C:\\code\\app"), "app");
  assert.equal(folderName("/home/me/src/"), "src");
  assert.equal(folderName("C:\\"), "C:");
  assert.equal(folderName("/"), "/");
  assert.equal(folderName("app"), "app");
  assert.equal(terminalTabLabel(terminal("a", "p"), "Terminal"), "Terminal app");
  assert.equal(terminalTabLabel(terminal("a", "p", { folder: "C:\\code\\app\\src" }), "Terminal"), "Terminal src");
  assert.equal(terminalTabLabel(terminal("a", "p", { title: "dev server", titled: true }), "Terminal"), "dev server");
});

test("the terminals of a project are listed in the order they were created; those of no project are their own list", () => {
  const list = [terminal("a", "p"), terminal("b", null), terminal("c", "q"), terminal("d", "p")];
  assert.deepEqual(terminalsOf(list, "p").map(item => item.id), ["a", "d"]);
  assert.deepEqual(terminalsOf(list, null).map(item => item.id), ["b"]);
  assert.deepEqual(terminalsOf(list, "none"), []);
});

test("a terminal takes the keyboard, but for the shortcuts that move around the application", () => {
  for (const [name, modifiers] of [["p", "c"], ["P", "c"], ["g", "c"], [",", "c"], ["`", "c"], ["PageUp", "c"], ["PageDown", "c"],
    ["T", "cs"], ["w", "cs"], ["N", "cs"], ["ArrowLeft", "ca"], ["ArrowRight", "ca"], ["b", "ca"]] as const) {
    assert.equal(applicationKey(key(name, modifiers)), true, `${modifiers}+${name}`);
  }
  // What a shell and its programs use: Ctrl with a letter, the function keys, Alt with an arrow.
  for (const [name, modifiers] of [["c", "c"], ["w", "c"], ["q", "c"], ["o", "c"], ["r", "c"], ["l", "c"], ["d", "c"], ["t", "c"], ["e", "c"], ["z", "c"],
    ["F1", ""], ["F5", ""], ["Escape", ""], ["Tab", ""], ["ArrowLeft", "a"], ["b", "a"], ["p", ""], ["p", "cm"], ["p", "ca"], ["g", "cs"], ["ArrowLeft", "cas"], ["c", "cs"]] as const) {
    assert.equal(applicationKey(key(name, modifiers)), false, `${modifiers}+${name}`);
  }
});

test("copy, paste, find and the ends of the text have their keys; everything else is for the program", () => {
  // Ctrl+C copies what is selected, and interrupts the program when nothing is.
  assert.equal(terminalKeyAction(key("c", "c"), true, false), "copy");
  assert.equal(terminalKeyAction(key("c", "c"), false, false), null);
  assert.equal(terminalKeyAction(key("C", "cs"), false, false), "copy");
  assert.equal(terminalKeyAction(key("v", "c"), false, false), "paste");
  assert.equal(terminalKeyAction(key("V", "cs"), false, false), "paste");
  assert.equal(terminalKeyAction(key("Insert", "s"), false, false), "paste");
  assert.equal(terminalKeyAction(key("Insert", "c"), false, false), "copy");
  assert.equal(terminalKeyAction(key("f", "c"), false, false), "find");
  assert.equal(terminalKeyAction(key("A", "cs"), false, false), "selectAll");
  assert.equal(terminalKeyAction(key("Home", "c"), false, false), "top");
  assert.equal(terminalKeyAction(key("End", "c"), false, false), "bottom");
  for (const [name, modifiers] of [["a", "c"], ["c", ""], ["v", "a"], ["c", "ca"], ["Home", ""], ["f", "m"], ["Insert", ""]] as const) {
    assert.equal(terminalKeyAction(key(name, modifiers), true, false), null, `${modifiers}+${name}`);
  }
  // On macOS the Command key does it, and Ctrl is all for the program.
  assert.equal(terminalKeyAction(key("c", "m"), true, true), "copy");
  assert.equal(terminalKeyAction(key("c", "m"), false, true), null);
  assert.equal(terminalKeyAction(key("v", "m"), false, true), "paste");
  assert.equal(terminalKeyAction(key("f", "m"), false, true), "find");
  assert.equal(terminalKeyAction(key("a", "m"), false, true), "selectAll");
  assert.equal(terminalKeyAction(key("ArrowUp", "m"), false, true), "top");
  assert.equal(terminalKeyAction(key("End", "m"), false, true), "bottom");
  for (const [name, modifiers] of [["c", "c"], ["v", "c"], ["f", "c"], ["c", "ms"], ["c", "ma"], ["c", "mc"]] as const) {
    assert.equal(terminalKeyAction(key(name, modifiers), true, true), null, `${modifiers}+${name}`);
  }
});

test("a terminal takes its colors from the window: its theme, its accent and its palette", () => {
  const window = new Map([["--bg", "#101010"], ["--text", "#eeeeee"], ["--accent", "#ff8800"], ["--bp-palette-red-4", "#ff1111"], ["--bp-palette-red-5", "#ff5555"],
    ["--bp-palette-red-2", "#990000"], ["--bp-palette-red-3", "#bb0000"]]);
  const dark = terminalTheme(name => window.get(name), true);
  assert.equal(dark.background, "#101010");
  assert.equal(dark.foreground, "#eeeeee");
  assert.equal(dark.cursor, "#ff8800");
  assert.equal(dark.cursorAccent, "#101010");
  assert.equal(dark.selectionBackground, "#ff880055");
  // A dark window takes the light steps of a family, a light one the dark steps.
  assert.deepEqual([dark.red, dark.brightRed], ["#ff1111", "#ff5555"]);
  const light = terminalTheme(name => window.get(name), false);
  assert.deepEqual([light.red, light.brightRed], ["#990000", "#bb0000"]);
  // Without a window to read, the colors of the application before any scheme.
  const plain = terminalTheme(() => undefined, true), day = terminalTheme(() => undefined, false);
  assert.equal(plain.background, "#1c2127");
  assert.equal(day.background, "#ffffff");
  for (const theme of [dark, light, plain, day]) {
    assert.equal(Object.keys(theme).length, 25);
    for (const [name, value] of Object.entries(theme)) assert.match(value, /^#[0-9a-f]{6}([0-9a-f]{2})?$/i, name);
    assert.notEqual(theme.foreground, theme.background);
  }
});

test("the look of the terminals is kept, and anything that is not a look restores the default", () => {
  let stored: string | null = null;
  const look = { ...defaultTerminalLook, fontSize: 16, cursorStyle: "block" as const, cursorBlink: false, copyOnSelect: true, scrollback: 100_000, shellIntegration: false, shell: "git-bash" };
  assert.equal(persistTerminalLook(value => { stored = value; }, look), true);
  assert.deepEqual(restoreTerminalLook(() => stored), look);
  assert.equal(persistTerminalLook(() => { throw new Error("denied"); }, look), false);
  assert.deepEqual(restoreTerminalLook(() => null), defaultTerminalLook);
  assert.deepEqual(restoreTerminalLook(() => { throw new Error("denied"); }), defaultTerminalLook);
  for (const value of ["", "{", "null", "[]", "7", "x".repeat(2000)]) assert.deepEqual(restoreTerminalLook(() => value), defaultTerminalLook, value.slice(0, 10));
  // Each value is taken alone: one that is not offered leaves the others as they were stored.
  const partial = restoreTerminalLook(() => JSON.stringify({ fontSize: 99, cursorStyle: "beam", cursorBlink: "yes", copyOnSelect: true, scrollback: 5, shellIntegration: 0 }));
  assert.deepEqual(partial, { ...defaultTerminalLook, copyOnSelect: true });
  assert.equal(restoreTerminalLook(() => JSON.stringify({ fontSize: minimumTerminalFontSize })).fontSize, minimumTerminalFontSize);
  assert.equal(restoreTerminalLook(() => JSON.stringify({ fontSize: maximumTerminalFontSize })).fontSize, maximumTerminalFontSize);
  for (const size of [minimumTerminalFontSize - 1, maximumTerminalFontSize + 1, 13.5, "13", null]) {
    assert.equal(restoreTerminalLook(() => JSON.stringify({ fontSize: size })).fontSize, defaultTerminalLook.fontSize, String(size));
  }
  assert.equal(defaultTerminalLook.shellIntegration, true);
  // The shell is a name of the host, or the default one.
  assert.equal(defaultTerminalLook.shell, null);
  assert.equal(restoreTerminalLook(() => JSON.stringify({ shell: "wsl:Ubuntu" })).shell, "wsl:Ubuntu");
  for (const shell of ["", 7, null, "x".repeat(129)]) assert.equal(restoreTerminalLook(() => JSON.stringify({ shell })).shell, null, String(shell).slice(0, 10));
});
