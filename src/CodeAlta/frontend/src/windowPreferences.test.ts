import assert from "node:assert/strict";
import test from "node:test";
import { colorVariant, effectiveTheme, nextTheme, themeLabel, themes } from "./windowPreferences";

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
