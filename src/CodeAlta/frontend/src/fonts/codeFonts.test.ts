import assert from "node:assert/strict";
import test from "node:test";
import { applyFontPreferences, codeFontSizes, codeLineHeight, defaultFontPreferences, fontFamilies, fontFamilyList, fontVariables, installedFontFamilies, interfaceTextSizes,
  maximumFontFamilies, persistFontPreferences, restoreFontPreferences, validCodeFontSize, validInterfaceTextSize, withFirstFamily } from "./codeFonts";

test("a list of fonts keeps the names the user wrote, quoted or not, each once", () => {
  assert.deepEqual(fontFamilies(` "Fira Code" , 'Sarasa Mono SC',Consolas,,  微软雅黑 , fira code`), ["Fira Code", "Sarasa Mono SC", "Consolas", "微软雅黑"]);
  assert.deepEqual(fontFamilies("JetBrains   Mono"), ["JetBrains Mono"]);
  assert.deepEqual(fontFamilies(""), []);
  assert.deepEqual(fontFamilies("  ,  , "), []);
});

test("a name a CSS string cannot hold, a name too long and the names past the limit are left out", () => {
  assert.deepEqual(fontFamilies(`a"b, c\\d, e\u0001f, ${"x".repeat(65)}, Good`), ["Good"]);
  assert.equal(fontFamilies(Array.from({ length: 12 }, (_, i) => `Font ${i}`).join(",")).length, maximumFontFamilies);
  // A quote that does not close is part of the name, which a CSS string cannot hold.
  assert.deepEqual(fontFamilies(`"Fira Code`), []);
});

test("a list of fonts becomes a CSS font-family with the generic families as keywords", () => {
  assert.equal(fontFamilyList(["Fira Code", "Monospace", "微软雅黑", "ui-monospace"]), `"Fira Code", monospace, "微软雅黑", ui-monospace`);
});

test("the font variables put the user's fonts before the built-in one and write the prompt in the chosen font", () => {
  assert.deepEqual(fontVariables(defaultFontPreferences), { "--code-font": null, "--prompt-font": null, "--code-font-size": null, "--ui-text-scale": null });
  assert.deepEqual(fontVariables({ ...defaultFontPreferences, code: ["Fira Code", "Sarasa Mono SC"], prompt: "interface" }),
    { "--code-font": `"Fira Code", "Sarasa Mono SC", var(--code-font-builtin)`, "--prompt-font": "var(--bp-typography-family-default)", "--code-font-size": null, "--ui-text-scale": null });
  const style = new Map<string, string>();
  const target = { setProperty: (name: string, value: string | null) => { style.set(name, value ?? ""); }, removeProperty: (name: string) => { style.delete(name); return ""; } };
  applyFontPreferences(target, { code: ["Fira Code"], prompt: "interface", codeSize: 12, interfaceSize: 90 });
  assert.equal(style.get("--code-font"), `"Fira Code", var(--code-font-builtin)`);
  assert.equal(style.get("--prompt-font"), "var(--bp-typography-family-default)");
  assert.equal(style.get("--code-font-size"), "12px");
  assert.equal(style.get("--ui-text-scale"), "0.9");
  applyFontPreferences(target, defaultFontPreferences);
  assert.equal(style.size, 0);
});

test("the stored preferences are read back, with the default for what is missing or invalid", () => {
  let stored = "";
  assert.ok(persistFontPreferences(value => { stored = value; }, { code: ["Fira Code"], prompt: "interface", codeSize: 12, interfaceSize: 110 }));
  assert.deepEqual(restoreFontPreferences(() => stored), { code: ["Fira Code"], prompt: "interface", codeSize: 12, interfaceSize: 110 });
  // The preferences of an earlier version have no sizes: the text keeps the sizes it had.
  assert.deepEqual(restoreFontPreferences(() => JSON.stringify({ code: ["Fira Code"], prompt: "code" })), { ...defaultFontPreferences, code: ["Fira Code"] });
  assert.deepEqual(restoreFontPreferences(() => JSON.stringify({ codeSize: 40, interfaceSize: 12.5 })), defaultFontPreferences);
  assert.deepEqual(restoreFontPreferences(() => null), defaultFontPreferences);
  assert.deepEqual(restoreFontPreferences(() => "{not json"), defaultFontPreferences);
  assert.deepEqual(restoreFontPreferences(() => JSON.stringify({ code: ["Fira Code", 3, "a\"b"], prompt: "serif" })), { ...defaultFontPreferences, code: ["Fira Code"] });
  assert.deepEqual(restoreFontPreferences(() => JSON.stringify({ code: "Fira Code" })), defaultFontPreferences);
  assert.deepEqual(restoreFontPreferences(() => { throw new Error("unavailable"); }), defaultFontPreferences);
  assert.equal(persistFontPreferences(() => { throw new Error("full"); }, defaultFontPreferences), false);
});

test("the installed fonts are offered once each, the monospace ones apart, and a chosen one replaces the first family", () => {
  const fonts = [{ family: "Segoe UI" }, { family: "Consolas" }, { family: "Consolas" }, { family: "Arial" }, { family: "Cascadia Mono" }, { family: "Bad\"Name" }];
  assert.deepEqual(installedFontFamilies(fonts, family => family === "Consolas" || family === "Cascadia Mono", "en"),
    { monospace: ["Cascadia Mono", "Consolas"], other: ["Arial", "Segoe UI"] });
  assert.deepEqual(withFirstFamily([], "Consolas"), ["Consolas"]);
  assert.deepEqual(withFirstFamily(["Fira Code", "Sarasa Mono SC"], "Consolas"), ["Consolas", "Sarasa Mono SC"]);
  assert.deepEqual(withFirstFamily(["Fira Code", "Consolas"], "Consolas"), ["Consolas"]);
});
test("the sizes offered are sizes the window may have, the default among them, and a line of the code editors keeps its proportion", () => {
  assert.ok(codeFontSizes.every(validCodeFontSize) && codeFontSizes.includes(defaultFontPreferences.codeSize));
  assert.ok(interfaceTextSizes.every(validInterfaceTextSize) && interfaceTextSizes.includes(defaultFontPreferences.interfaceSize));
  assert.deepEqual([8, 25, 12.5, "13", null].map(validCodeFontSize), [false, false, false, false, false]);
  assert.deepEqual([79, 131, 100.5, "100"].map(validInterfaceTextSize), [false, false, false, false]);
  assert.deepEqual([10, 12, 13, 14, 16, 20].map(codeLineHeight), [15, 18, 20, 22, 25, 31]);
});
