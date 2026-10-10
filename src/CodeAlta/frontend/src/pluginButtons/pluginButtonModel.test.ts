import assert from "node:assert/strict";
import test from "node:test";
import { badgeText, buttonName, buttonTooltip, createHiddenButtons, foldButtons, hiddenButtonsStorageKey, hiddenKey, readHiddenButtons, readPluginButtons, samePluginButtons, shownButtons } from "./pluginButtonModel";

const wire = (over: Record<string, unknown> = {}) => ({
  id: "builtin:fixture/a", pluginKey: "builtin:fixture", pluginId: "fixture", plugin: "Fixture", buttonId: "a", place: "TitleBar", icon: "chart-column", iconData: null, label: "A",
  commandId: "command-1", canvas: null, canvasScope: null, badge: "none", count: 0, tone: "Info", hidden: false, disabled: false, tooltip: null, ...over,
});
const read = (...buttons: Record<string, unknown>[]) => readPluginButtons({ status: "ok", place: null, buttons })!;

test("a list of buttons is read only when the host said ok, and a button that is not well formed is left out", () => {
  assert.equal(readPluginButtons(null), null);
  assert.equal(readPluginButtons({ status: "stale_epoch", buttons: [] }), null);
  assert.equal(readPluginButtons({ status: "ok", buttons: "x" }), null);
  assert.deepEqual(read(), []);
  const good = read(wire(), wire({ id: "x/b", buttonId: "b", commandId: null, canvas: "board", canvasScope: "Project", badge: "count", count: 4, tone: "Warning" }));
  assert.deepEqual(good.map(button => [button.buttonId, button.commandId, button.canvas, button.canvasScope, button.badge, button.count, button.tone]),
    [["a", "command-1", null, null, "none", 0, "Info"], ["b", null, "board", "Project", "count", 4, "Warning"]]);
  assert.deepEqual(read(
    wire({ id: "" }), wire({ buttonId: "has space" }), wire({ place: "Basement" }), wire({ icon: "" }), wire({ label: "" }), wire({ label: "bad\nlabel" }),
    wire({ commandId: null }), wire({ canvas: "board", canvasScope: "Application" }), wire({ commandId: null, canvas: "board", canvasScope: "Galaxy" }),
    wire({ id: "dup" }), wire({ id: "dup" }), null as never, 3 as never).map(button => button.id), ["dup"], "no command or canvas, both, a bad scope, a duplicate and junk are left out");
  assert.equal(read(wire({ badge: "sparkle", tone: "Rainbow", count: -2 }))[0].badge, "none");
  assert.equal(read(wire({ badge: "sparkle", tone: "Rainbow" }))[0].tone, "Info");
  assert.equal(read(wire({ badge: "count", count: -2 }))[0].count, 0);
  assert.equal(read(wire({ iconData: "javascript:alert(1)" }))[0].iconData, null, "only a clean SVG data URL is taken");
  assert.equal(read(wire({ iconData: "data:image/svg+xml;base64,PHN2Zz4=" }))[0].iconData, "data:image/svg+xml;base64,PHN2Zz4=");
});

test("the name and the tooltip of a button say its number, and the tooltip the plugin gave replaces them", () => {
  const withCount = (label: string, count: string) => `${label}: ${count}`;
  const [plain, counted, over, dotted] = read(wire(), wire({ id: "2", buttonId: "b", badge: "count", count: 3 }), wire({ id: "3", buttonId: "c", badge: "count", count: 250, tooltip: "3 steps left" }),
    wire({ id: "4", buttonId: "d", badge: "dot" }));
  assert.equal(buttonName(plain, withCount), "A");
  assert.equal(buttonName(counted, withCount), "A: 3");
  assert.equal(badgeText(over), "99+");
  assert.equal(buttonTooltip(over, withCount), "3 steps left");
  assert.equal(buttonTooltip(counted, withCount), "A: 3");
  assert.equal(badgeText(dotted), null);
});

test("two reads show the same thing when nothing a person sees changed", () => {
  assert.equal(samePluginButtons(read(wire(), wire({ id: "b", buttonId: "b" })), read(wire(), wire({ id: "b", buttonId: "b" }))), true);
  assert.equal(samePluginButtons(read(wire()), read(wire({ badge: "dot" }))), false);
  assert.equal(samePluginButtons(read(wire()), read(wire({ count: 1, badge: "count" }))), false);
  assert.equal(samePluginButtons(read(wire()), read(wire({ disabled: true }))), false);
  assert.equal(samePluginButtons(read(wire()), read(wire(), wire({ id: "b", buttonId: "b" }))), false);
});

test("hidden buttons are kept under the plugin and the button, not under a space, and survive a bad value", () => {
  const storage = new Map<string, string>();
  const backing = { getItem: (key: string) => storage.get(key) ?? null, setItem: (key: string, value: string) => { storage.set(key, value); } };
  const store = createHiddenButtons(backing);
  const heard: number[] = [];
  const stop = store.subscribe(() => heard.push(store.getSnapshot().size));
  const [button] = read(wire());
  assert.equal(store.isHidden(button), false);
  store.set(button, true);
  store.set(button, true);
  assert.equal(store.isHidden(button), true);
  assert.deepEqual(JSON.parse(storage.get(hiddenButtonsStorageKey)!), ["builtin:fixture\na"]);
  assert.deepEqual(heard, [1], "a choice that is already made says nothing");
  // Another window of the same profile, started later, has the choice.
  assert.equal(createHiddenButtons(backing).isHidden(button), true);
  assert.deepEqual(shownButtons(read(wire(), wire({ id: "b", buttonId: "b" }), wire({ id: "c", buttonId: "c", hidden: true })), store.getSnapshot()).map(value => value.buttonId), ["b"]);
  store.set(button, false);
  assert.deepEqual(JSON.parse(storage.get(hiddenButtonsStorageKey)!), []);
  stop();
  store.set(button, true);
  assert.deepEqual(heard, [1, 0]);
  assert.equal(readHiddenButtons(() => "not json").size, 0);
  assert.equal(readHiddenButtons(() => "{\"a\":1}").size, 0);
  assert.deepEqual([...readHiddenButtons(() => JSON.stringify(["a\nb", 3, "no separator"]))], ["a\nb"]);
  assert.equal(hiddenKey({ pluginKey: "p", buttonId: "b" }), "p\nb");
  // Storage that fails keeps the choice until the window closes.
  const broken = createHiddenButtons({ getItem: () => { throw new Error("no"); }, setItem: () => { throw new Error("no"); } });
  broken.set(button, true);
  assert.equal(broken.isHidden(button), true);
  assert.equal(createHiddenButtons(null).isHidden(button), false);
});

test("buttons fold into a menu when the window is narrow, and the rail folds the ones past its limit", () => {
  const buttons = read(wire(), wire({ id: "b", buttonId: "b" }), wire({ id: "c", buttonId: "c" }));
  assert.deepEqual(foldButtons(buttons, Number.POSITIVE_INFINITY, false).folded, []);
  assert.deepEqual(foldButtons(buttons, 2, false).inline.map(button => button.buttonId), ["a", "b"]);
  assert.deepEqual(foldButtons(buttons, 2, false).folded.map(button => button.buttonId), ["c"]);
  assert.deepEqual(foldButtons(buttons, Number.POSITIVE_INFINITY, true), { inline: [], folded: buttons });
});
