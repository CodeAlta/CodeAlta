import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";
import { colorSchemeAttribute, colorSchemeIds, colorSchemes, defaultColorScheme } from "./colorSchemes";

test("Blueprint is the default scheme and needs no attribute", () => {
  assert.equal(colorSchemes[0].id, defaultColorScheme);
  assert.equal(colorSchemeAttribute(defaultColorScheme), undefined);
  assert.equal(colorSchemeAttribute("elderberry"), "elderberry");
  assert.equal(colorSchemeAttribute("unknown"), undefined);
  assert.equal(new Set(colorSchemeIds).size, colorSchemeIds.length);
});

test("every generated scheme has a dark and a light rule in the generated stylesheet", () => {
  const css = readFileSync(new URL("./colorSchemes.gen.css", import.meta.url), "utf8");
  for (const scheme of colorSchemes.slice(1)) {
    assert.match(scheme.id, /^[a-z][a-z0-9-]*$/);
    assert.ok(css.includes(`:root[data-color-scheme="${scheme.id}"] {`), scheme.id);
    assert.ok(css.includes(`:root[data-color-scheme="${scheme.id}"][data-theme="light"] {`), scheme.id);
    for (const swatch of [scheme.dark, scheme.light]) for (const color of Object.values(swatch)) assert.match(color, /^#[0-9a-f]{6}$/);
  }
});

test("a scheme's text keeps a readable contrast against its background", () => {
  const luminance = (hex: string) => {
    const channel = (index: number) => { const value = parseInt(hex.slice(index, index + 2), 16) / 255; return value <= 0.04045 ? value / 12.92 : ((value + 0.055) / 1.055) ** 2.4; };
    return 0.2126 * channel(1) + 0.7152 * channel(3) + 0.0722 * channel(5);
  };
  const contrast = (left: string, right: string) => { const [low, high] = [luminance(left), luminance(right)].sort((a, b) => a - b); return (high + 0.05) / (low + 0.05); };
  for (const scheme of colorSchemes) for (const swatch of [scheme.dark, scheme.light]) {
    assert.ok(contrast(swatch.background, swatch.foreground) >= 7, `${scheme.id} text`);
    assert.ok(contrast(swatch.background, swatch.accent) >= 3, `${scheme.id} accent`);
  }
});
