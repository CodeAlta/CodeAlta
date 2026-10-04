import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";
import { blueprintPaletteVariables } from "./blueprintPalette";

const palette = ":root{\n  --bp-palette-black:#111418;\n  --bp-palette-white:#ffffff;\n  --bp-palette-dark-gray-3:#2f343c;\n  --bp-palette-blue-3:#2d72d2;\n  --bp-intent-primary-rest:#2d72d2;\n}\n";

test("palette literals become palette variables and the palette itself stays", () => {
  const css = blueprintPaletteVariables(palette + ".a{background:#2F343C;color:#fff;border:1px solid rgba(17, 20, 24, 0.3);box-shadow:0 0 0 1px rgb(45,114,210), 0 1px #2d72d280}");
  assert.ok(css.includes("--bp-palette-dark-gray-3:#2f343c;"));
  assert.ok(css.includes("--bp-intent-primary-rest:var(--bp-palette-blue-3);"));
  assert.ok(css.includes(".a{background:var(--bp-palette-dark-gray-3);color:var(--bp-palette-white);border:1px solid rgb(from var(--bp-palette-black) r g b / 0.3);"));
  assert.ok(css.includes("box-shadow:0 0 0 1px var(--bp-palette-blue-3), 0 1px rgb(from var(--bp-palette-blue-3) r g b / 0.502)}"));
});

test("colors outside the palette, selectors and encoded colors are left alone", () => {
  const rules = "#fff .a{color:#123456;background:rgba(0, 0, 0, 0.2);fill:url(\"data:image/svg+xml,%3Csvg fill='%23111418'/%3E\")}";
  assert.equal(blueprintPaletteVariables(palette + rules), palette.replace("--bp-intent-primary-rest:#2d72d2", "--bp-intent-primary-rest:var(--bp-palette-blue-3)") + rules);
  assert.equal(blueprintPaletteVariables(".a{color:#111418}"), ".a{color:#111418}");
});

test("Blueprint's stylesheet keeps its palette and loses its palette literals", () => {
  const source = readFileSync(new URL("../node_modules/@blueprintjs/core/lib/css/blueprint.css", import.meta.url), "utf8");
  const css = blueprintPaletteVariables(source);
  const declared = (text: string) => [...text.matchAll(/--bp-palette-[a-z0-9-]+\s*:\s*#[0-9a-fA-F]{6}/g)].map(match => match[0]);
  assert.deepEqual(declared(css), declared(source));
  const literals = (text: string, hex: string) => text.split(hex).length - 1;
  // Each of these appears once in the palette and nowhere else afterwards.
  for (const hex of ["#2f343c", "#383e47", "#f6f7f9", "#2d72d2", "#1c2127"]) {
    assert.ok(literals(source, hex) > 2, hex);
    assert.equal(literals(css, hex), 1, hex);
  }
  assert.ok(!/rgba\(17, 20, 24,/.test(css));
});
