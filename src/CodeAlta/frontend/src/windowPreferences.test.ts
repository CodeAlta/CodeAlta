import assert from "node:assert/strict";
import test from "node:test";
import { colorSchemeOf, defaultColorScheme, type CustomColorScheme } from "./colorSchemes";
import { colorVariant, effectiveTheme, nextTheme, reconciledSelection, selectedScheme, themeLabel, themes } from "./windowPreferences";

test("the theme switch goes through dark, light and the system's theme", () => {
  assert.deepEqual(themes, ["dark", "light", "system"]);
  assert.equal(nextTheme("dark"), "light");
  assert.equal(nextTheme("light"), "system");
  assert.equal(nextTheme("system"), "dark");
  assert.deepEqual(themes.map(themeLabel), ["Dark", "Light", "Auto"]);
  assert.equal(effectiveTheme("system", true), "dark");
  assert.equal(effectiveTheme("system", false), "light");
  assert.equal(effectiveTheme("light", true), "light");
});

test("the darker option applies to the dark theme only", () => {
  assert.equal(colorVariant("dark", false), "dark");
  assert.equal(colorVariant("dark", true), "darker");
  assert.equal(colorVariant("light", false), "light");
  assert.equal(colorVariant("light", true), "light");
});

const night: CustomColorScheme = { id: "night", name: "Night", base: "plum", light: {}, dark: { background: "#101418" }, darker: {} };

test("a selection stands for a built-in scheme, a listed custom one, or the copy kept of it", () => {
  assert.equal(selectedScheme("kiwi", null, null), colorSchemeOf("kiwi"));
  assert.equal(selectedScheme("unknown", [], null), colorSchemeOf(defaultColorScheme));
  // Before the host has listed the schemes, the window shows the copy it kept.
  assert.equal(selectedScheme("custom:night", null, night), night);
  // What the host lists is what counts: the file may have changed since.
  const edited = { ...night, dark: { background: "#000000" } };
  assert.equal(selectedScheme("custom:night", [edited], night), edited);
  assert.equal(selectedScheme("custom:night", null, { ...night, id: "other" }), colorSchemeOf(defaultColorScheme));
  assert.equal(selectedScheme("custom:night", null, null), colorSchemeOf(defaultColorScheme));
});

test("a custom scheme that is not listed stays selected while the window has a copy of it", () => {
  assert.equal(reconciledSelection("kiwi", [], null), "kiwi");
  assert.equal(reconciledSelection("custom:night", [night], night), "custom:night");
  // Its file is missing or cannot be read for now: the copy is shown, and the selection is not rewritten.
  assert.equal(reconciledSelection("custom:night", [], night), "custom:night");
  assert.equal(selectedScheme("custom:night", [], night), night);
  // Nothing to show: the default scheme.
  assert.equal(reconciledSelection("custom:night", [{ ...night, id: "other" }], { ...night, id: "other" }), defaultColorScheme);
  assert.equal(reconciledSelection("custom:night", [], null), defaultColorScheme);
});
