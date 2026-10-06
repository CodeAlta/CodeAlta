import assert from "node:assert/strict";
import test from "node:test";
import { adjustPalette, contrast, darkerPalette, darkerShift, lightness, parseColor, schemeColorNames, schemeColors } from "./colorPalette";
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

const dark = blueprintPalette, cherry = colorSchemes.find(scheme => scheme.id === "cherry")!;
const channels = (hex: string) => [1, 3, 5].map(index => parseInt(hex.slice(index, index + 2), 16));
const family = (name: string) => [1, 2, 3, 4, 5].map(step => `${name}-${step}` as PaletteColor);
const grays = family("gray"), lights = family("light-gray"), darks = family("dark-gray");

test("a color is #rgb or #rrggbb in either case, and nothing else", () => {
  assert.equal(parseColor("#1A2B3C"), "#1a2b3c");
  assert.equal(parseColor(" #abc "), "#aabbcc");
  for (const value of ["1a2b3c", "#1a2b3", "#1a2b3c4d", "#gggggg", "red", "", null, undefined, 12, {}]) assert.equal(parseColor(value), null);
  assert.equal(Math.round(contrast("#000000", "#ffffff")), 21);
  assert.equal(contrast("#777777", "#777777"), 1);
});

test("a chosen color is shown exactly as given", () => {
  const targets = ["#080808", "#000000", "#ffffff", "#ff8800", "#7c3aed", "#2b2118", "#fdf6e3", "#888888", "#00d084"];
  for (const scheme of [colorSchemes[0], cherry]) for (const mode of ["dark", "light"] as const) for (const name of schemeColorNames) for (const target of targets)
    assert.equal(schemeColors(adjustPalette(scheme[mode], mode, { [name]: target }), mode)[name], target, `${scheme.id} ${mode} ${name} ${target}`);
});

test("choosing the colors a palette shows, or none, changes nothing", () => {
  for (const scheme of colorSchemes) for (const mode of ["dark", "light"] as const) {
    assert.deepEqual(adjustPalette(scheme[mode], mode, schemeColors(scheme[mode], mode)), scheme[mode], `${scheme.id} ${mode}`);
    assert.deepEqual(adjustPalette(scheme[mode], mode, { background: "not a color", text: undefined }), scheme[mode]);
  }
});

test("a darker background lowers the panels with it and keeps them apart", () => {
  // The window at 8 of 255, with the panels at 17 to 27: the surfaces keep the distances they had.
  const deep = adjustPalette(dark, "dark", { background: "#080808" });
  assert.deepEqual(surfaces.map(name => deep[name]), ["#000000", "#080808", "#111111", "#1b1b1b", "#252525", "#2f2f2f"]);
  // Even a black background leaves every panel above it visible.
  const black = adjustPalette(dark, "dark", { background: "#000000" });
  assert.deepEqual(darks.map(name => black[name]), ["#000000", "#090909", "#131313", "#1d1d1d", "#272727"]);
  // Nothing else moved: a background that is a gray says nothing about the tint of the text.
  for (const name of [...grays, ...lights, "white", ...family("blue"), ...family("green")] as PaletteColor[]) assert.equal(deep[name], dark[name], name);
});

test("a tinted background tints the grays and the text with it", () => {
  const brown = adjustPalette(dark, "dark", { background: "#2b2118" });
  // Blueprint's grays are bluish; on a brown background they are warm, at the lightness they had.
  for (const name of [...surfaces.slice(1), ...grays]) {
    const [red, , blue] = channels(brown[name]);
    assert.ok(red > blue, `${name} ${brown[name]}`);
  }
  for (const name of [...grays, ...lights]) assert.ok(Math.abs(lightness(brown[name]) - lightness(dark[name])) < 0.01, name);
  // The same in the light theme: a cream background warms the dark text.
  const cream = adjustPalette(dark, "light", { background: "#fdf6e3" });
  const [red, , blue] = channels(cream["dark-gray-1"]);
  assert.ok(red > blue, cream["dark-gray-1"]);
  assert.ok(contrast(cream["light-gray-5"], cream["dark-gray-1"]) >= 7);
});

test("muted text moves the mid grays, and text the steps near it", () => {
  const muted = adjustPalette(dark, "dark", { muted: "#a0a0a0" });
  assert.deepEqual(grays.map(name => muted[name]), ["#646464", "#757575", "#8a8a8a", "#a0a0a0", "#b4b4b4"]);
  for (const name of [...surfaces, ...lights, "white"] as PaletteColor[]) assert.equal(muted[name], dark[name], name);

  const text = adjustPalette(dark, "dark", { text: "#e0e0e0" });
  assert.equal(text["light-gray-5"], "#e0e0e0");
  for (let index = 1; index < lights.length; index++) assert.ok(lightness(text[lights[index]]) > lightness(text[lights[index - 1]]), lights[index]);
  assert.ok(lightness(text["light-gray-1"]) > lightness(text["gray-5"]));
  for (const name of [...surfaces, ...grays, "white"] as PaletteColor[]) assert.equal(text[name], dark[name], name);

  // In the light theme the text is the darkest step and muted text the darkest gray.
  const light = adjustPalette(dark, "light", { text: "#000000", muted: "#555555" });
  assert.equal(light["dark-gray-1"], "#000000");
  assert.equal(light["gray-1"], "#555555");
  for (let index = 1; index < grays.length; index++) assert.ok(lightness(light[grays[index]]) > lightness(light[grays[index - 1]]), grays[index]);
});

test("an accent turns its color families to its hue and leaves the others", () => {
  const orange = adjustPalette(dark, "dark", { accent: "#ff8800" });
  for (const name of [...family("blue"), ...family("indigo")]) {
    const [red, green, blue] = channels(orange[name]);
    assert.ok(red > green && green > blue, `${name} ${orange[name]}`);
  }
  for (const name of [...family("green"), ...family("red"), ...family("orange"), ...family("violet"), ...grays]) assert.equal(orange[name], dark[name], name);
  // The status colors each have their families.
  const status = adjustPalette(dark, "dark", { success: "#00d084", warning: "#ffcc00", danger: "#ff3b30" });
  for (const [name, expected] of [["green-4", "#00d084"], ["orange-4", "#ffcc00"], ["red-4", "#ff3b30"]] as const) assert.equal(status[name], expected);
  for (const name of family("gold")) { const [red, green, blue] = channels(status[name]); assert.ok(red >= green && green > blue, `${name} ${status[name]}`); }
  for (const name of family("blue")) assert.equal(status[name], dark[name], name);
});

test("the steps of an accent stay in order, however light or dark it is", () => {
  for (const accent of ["#ffe45c", "#ffffff", "#101010", "#000000", "#7c3aed", "#00ffcc"]) for (const mode of ["dark", "light"] as const) {
    const palette = adjustPalette(dark, mode, { accent });
    const steps = family("blue").map(name => lightness(palette[name]));
    for (let index = 1; index < steps.length; index++) assert.ok(steps[index] >= steps[index - 1] - 0.001, `${accent} ${mode} blue-${index + 1}`);
  }
});
