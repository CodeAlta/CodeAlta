import assert from "node:assert/strict";
import test from "node:test";
import { applyPalette, colorSchemeIds, colorSchemeOf, colorSchemes, defaultColorScheme, paletteVariables, schemePalette, schemeSwatch, showAppearance, type ColorVariant } from "./colorSchemes";
import { blueprintPalette } from "./colorSchemes.gen";

// The part of the document root that showing an appearance touches.
function fakeRoot() {
  const properties = new Map<string, string>([["--splash-background", "#101010"]]);
  const classes = new Set<string>();
  const style = { get length() { return properties.size; }, item: (index: number) => [...properties.keys()][index] ?? "",
    getPropertyValue: (name: string) => properties.get(name) ?? "", setProperty: (name: string, value: string) => { properties.set(name, value); },
    removeProperty: (name: string) => { properties.delete(name); return ""; } };
  const classList = { toggle: (name: string, on?: boolean) => { if (on) classes.add(name); else classes.delete(name); return !!on; } };
  const root = { style, dataset: {} as Record<string, string | undefined>, classList } as unknown as HTMLElement;
  return { root, properties, classes };
}

test("Blueprint is the default scheme and redefines nothing", () => {
  assert.equal(colorSchemes[0].id, defaultColorScheme);
  assert.equal(colorSchemeOf("elderberry").name, "Elderberry");
  assert.equal(colorSchemeOf("unknown"), colorSchemes[0]);
  assert.equal(new Set(colorSchemeIds).size, colorSchemeIds.length);
  assert.deepEqual(paletteVariables(colorSchemes[0].dark), {});
  assert.deepEqual(paletteVariables(colorSchemes[0].light), {});
});

test("every scheme has a whole palette for both themes", () => {
  const names = Object.keys(blueprintPalette).sort();
  assert.equal(names.length, 87);
  for (const scheme of colorSchemes) {
    assert.match(scheme.id, /^[a-z][a-z0-9-]*$/);
    for (const palette of [scheme.dark, scheme.light]) {
      assert.deepEqual(Object.keys(palette).sort(), names, scheme.id);
      for (const color of Object.values(palette)) assert.match(color, /^#[0-9a-f]{6}$/);
    }
  }
});

test("a scheme's text keeps a readable contrast against its background", () => {
  const luminance = (hex: string) => {
    const channel = (index: number) => { const value = parseInt(hex.slice(index, index + 2), 16) / 255; return value <= 0.04045 ? value / 12.92 : ((value + 0.055) / 1.055) ** 2.4; };
    return 0.2126 * channel(1) + 0.7152 * channel(3) + 0.0722 * channel(5);
  };
  const contrast = (left: string, right: string) => { const [low, high] = [luminance(left), luminance(right)].sort((a, b) => a - b); return (high + 0.05) / (low + 0.05); };
  for (const scheme of colorSchemes) for (const variant of ["dark", "darker", "light"] as ColorVariant[]) {
    const swatch = schemeSwatch(schemePalette(scheme, variant), variant);
    assert.ok(contrast(swatch.background, swatch.foreground) >= 7, `${scheme.id} ${variant} text`);
    assert.ok(contrast(swatch.background, swatch.accent) >= 3, `${scheme.id} ${variant} accent`);
  }
});

test("a scheme's themes are its two palettes, and the darker theme is made from the dark one", () => {
  const plum = colorSchemeOf("plum");
  assert.equal(schemePalette(plum, "light"), plum.light);
  assert.equal(schemePalette(plum, "dark"), plum.dark);
  const darker = schemePalette(plum, "darker");
  assert.notEqual(darker["dark-gray-1"], plum.dark["dark-gray-1"]);
  assert.equal(darker["light-gray-5"], plum.dark["light-gray-5"]);
  // On Blueprint's own palette the darker theme redefines the surfaces and nothing else.
  assert.deepEqual(Object.keys(paletteVariables(schemePalette(colorSchemes[0], "darker"))),
    ["--bp-palette-black", ...[1, 2, 3, 4, 5].map(step => `--bp-palette-dark-gray-${step}`)]);
});

test("a palette sets the palette variables it changes and removes those of the palette before", () => {
  const { root, properties } = fakeRoot();
  const cherry = colorSchemeOf("cherry"), kiwi = colorSchemeOf("kiwi");
  applyPalette(root, cherry.dark);
  assert.equal(properties.get("--bp-palette-dark-gray-1"), cherry.dark["dark-gray-1"]);
  assert.equal(properties.get("--bp-palette-blue-3"), cherry.dark["blue-3"]);
  // White is Blueprint's own in this scheme: it is not redefined.
  assert.equal(properties.has("--bp-palette-white"), false);
  const first = root.dataset.palette;
  assert.match(first ?? "", /^[0-9a-f]{8}$/);

  applyPalette(root, kiwi.light);
  assert.equal(properties.get("--bp-palette-dark-gray-1"), kiwi.light["dark-gray-1"]);
  assert.notEqual(root.dataset.palette, first);

  applyPalette(root, blueprintPalette);
  assert.deepEqual([...properties.keys()], ["--splash-background"]);
  assert.equal(root.dataset.palette, undefined);
});

test("showing an appearance sets the theme, the scheme and its palette on the root", () => {
  const { root, properties, classes } = fakeRoot();
  showAppearance(root, "dark-class", { theme: "dark", scheme: "plum", palette: colorSchemeOf("plum").dark });
  assert.equal(root.dataset.theme, "dark");
  assert.equal(root.dataset.colorScheme, "plum");
  assert.ok(classes.has("dark-class"));
  assert.equal(properties.get("--bp-palette-gray-1"), colorSchemeOf("plum").dark["gray-1"]);

  showAppearance(root, "dark-class", { theme: "light", scheme: defaultColorScheme, palette: blueprintPalette });
  assert.equal(root.dataset.theme, "light");
  assert.equal(root.dataset.colorScheme, undefined);
  assert.equal(classes.has("dark-class"), false);
  assert.equal(properties.has("--bp-palette-gray-1"), false);
});
