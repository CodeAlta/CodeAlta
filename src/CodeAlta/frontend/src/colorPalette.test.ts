import assert from "node:assert/strict";
import test from "node:test";
import { darkerPalette, darkerShift, lightness } from "./colorPalette";
import { colorSchemes } from "./colorSchemes";
import { blueprintPalette, type PaletteColor } from "./colorSchemes.gen";

const surfaces: PaletteColor[] = ["black", "dark-gray-1", "dark-gray-2", "dark-gray-3", "dark-gray-4", "dark-gray-5"];

test("the darker palette lowers the surfaces of the dark one and leaves the rest", () => {
  const darker = darkerPalette(blueprintPalette);
  assert.deepEqual(Object.fromEntries(surfaces.map(name => [name, darker[name]])), {
    "black": "#06070a", "dark-gray-1": "#101317", "dark-gray-2": "#181c21", "dark-gray-3": "#21252b", "dark-gray-4": "#2a2f36", "dark-gray-5": "#323842" });
  for (const name of Object.keys(blueprintPalette) as PaletteColor[])
    if (!surfaces.includes(name)) assert.equal(darker[name], blueprintPalette[name], name);
});

test("every scheme's darker theme is clearly darker than its dark one without being black", () => {
  for (const scheme of colorSchemes) {
    const darker = darkerPalette(scheme.dark);
    for (const name of surfaces) {
      const moved = lightness(scheme.dark[name]) - lightness(darker[name]);
      // One notch, within what rounding to eight bits moves a dark color.
      assert.ok(Math.abs(moved - darkerShift) < 0.006, `${scheme.id} ${name} moved by ${moved}`);
    }
    // The window background stays a visible dark gray: #080808 has a lightness of 0.13.
    assert.ok(lightness(darker["dark-gray-1"]) > 0.17, scheme.id);
    assert.ok(lightness(darker["black"]) > 0.1, scheme.id);
    // What is raised stays raised.
    for (let index = 1; index < surfaces.length; index++)
      assert.ok(lightness(darker[surfaces[index]]) > lightness(darker[surfaces[index - 1]]), `${scheme.id} ${surfaces[index]}`);
  }
});

test("a surface keeps its tint when it gets darker", () => {
  const channels = (hex: string) => [1, 3, 5].map(index => parseInt(hex.slice(index, index + 2), 16));
  // Cherry's surfaces are red: red stays their strongest channel and blue stays above green.
  const cherry = darkerPalette(colorSchemes.find(scheme => scheme.id === "cherry")!.dark);
  for (const name of surfaces.slice(1)) {
    const [red, green, blue] = channels(cherry[name]);
    assert.ok(red > blue && blue > green, `${name} ${cherry[name]}`);
  }
});
