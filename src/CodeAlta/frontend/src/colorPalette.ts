import type { Palette, PaletteColor } from "./colorSchemes.gen";

/**
 * Palette arithmetic for the color schemes. A palette redefines Blueprint's palette variables; everything
 * here keeps the structure Blueprint designed its components for (the steps of a family stay in order and
 * keep their distances) and moves it as a whole. Colors are handled in the Oklab space, where equal steps
 * of lightness look equal.
 */

/** A color in the Oklab space: perceptual lightness (0 black to 1 white) and the two chroma axes. */
type Lab = readonly [lightness: number, a: number, b: number];

/** The two kinds of palette: dark surfaces under light text, or light surfaces under dark text. */
export type PaletteMode = "dark" | "light";

/** The colors of a scheme that a custom scheme chooses; everything else follows from them. */
export const schemeColorNames = ["background", "text", "muted", "accent", "success", "warning", "danger"] as const;
export type SchemeColorName = typeof schemeColorNames[number];
export type SchemeColors = Readonly<Partial<Record<SchemeColorName, string>>>;

/** How much darker the surfaces of the darker theme are than those of the dark theme, in Oklab lightness. */
export const darkerShift = 0.06;

// Below this chroma a color is a gray: its hue means nothing.
const neutral = 0.002;
// The families of an accent scale their chroma with it up to this factor; what is beyond is added instead.
const strongestTint = 3;

const steps = (family: string): PaletteColor[] => [1, 2, 3, 4, 5].map(step => `${family}-${step}` as PaletteColor);
const grays = steps("gray"), darkGrays = steps("dark-gray"), lightGrays = steps("light-gray");
const accentFamilies = { accent: ["blue", "indigo"], success: ["green", "forest", "lime"], warning: ["orange", "gold", "sepia"], danger: ["red", "vermilion", "rose"] } as const;

// Where each chosen color lives in a palette, and the steps that move with it. The anchors are the steps the
// shell itself shows: its window background, text, muted text, accent, and the status colors.
const layouts = {
  dark: { surfaces: ["black", ...darkGrays] as PaletteColor[], texts: lightGrays, surfaceEdge: "dark-gray-5", textEdge: "gray-5",
    anchors: { background: "dark-gray-1", text: "light-gray-5", muted: "gray-4", accent: "blue-4", success: "green-4", warning: "orange-4", danger: "red-4" } },
  light: { surfaces: ["white", ...lightGrays] as PaletteColor[], texts: darkGrays, surfaceEdge: "light-gray-1", textEdge: "gray-1",
    anchors: { background: "light-gray-5", text: "dark-gray-1", muted: "gray-1", accent: "blue-3", success: "green-3", warning: "orange-3", danger: "red-3" } },
} as const satisfies Record<PaletteMode, { surfaces: PaletteColor[]; texts: PaletteColor[]; surfaceEdge: PaletteColor; textEdge: PaletteColor; anchors: Record<SchemeColorName, PaletteColor> }>;

/** Normalizes a color written as #rgb or #rrggbb to lowercase #rrggbb; null for anything else. */
export function parseColor(value: unknown): string | null {
  if (typeof value !== "string") return null;
  const match = /^#(?:([0-9a-f]{3})|([0-9a-f]{6}))$/i.exec(value.trim());
  if (!match) return null;
  return `#${(match[1] ? [...match[1]].map(digit => digit + digit).join("") : match[2]).toLowerCase()}`;
}

function toLab(hex: string): Lab {
  const [red, green, blue] = [1, 3, 5].map(index => {
    const value = parseInt(hex.slice(index, index + 2), 16) / 255;
    return value <= 0.04045 ? value / 12.92 : ((value + 0.055) / 1.055) ** 2.4;
  });
  const l = Math.cbrt(0.4122214708 * red + 0.5363325363 * green + 0.0514459929 * blue);
  const m = Math.cbrt(0.2119034982 * red + 0.6806995451 * green + 0.1073969566 * blue);
  const s = Math.cbrt(0.0883024619 * red + 0.2817188376 * green + 0.6299787005 * blue);
  return [0.2104542553 * l + 0.7936177850 * m - 0.0040720468 * s, 1.9779984951 * l - 2.4285922050 * m + 0.4505937099 * s,
    0.0259040371 * l + 0.7827717662 * m - 0.8086757660 * s];
}

function linear(lightness: number, a: number, b: number): [number, number, number] {
  const l = (lightness + 0.3963377774 * a + 0.2158037573 * b) ** 3;
  const m = (lightness - 0.1055613458 * a - 0.0638541728 * b) ** 3;
  const s = (lightness - 0.0894841775 * a - 1.2914855480 * b) ** 3;
  return [4.0767416621 * l - 3.3077115913 * m + 0.2309699292 * s, -1.2684380046 * l + 2.6097574011 * m - 0.3413193965 * s,
    -0.0041960863 * l - 0.7034186147 * m + 1.7076147010 * s];
}

// A color outside sRGB keeps its lightness and hue and loses chroma until it fits.
function toHex([lightness, a, b]: Lab): string {
  const light = Math.min(1, Math.max(0, lightness));
  const fits = (channels: number[]) => channels.every(channel => channel >= -0.0005 && channel <= 1.0005);
  let channels = linear(light, a, b);
  if (!fits(channels)) {
    let low = 0, high = 1;
    for (let step = 0; step < 24; step++) {
      const middle = (low + high) / 2;
      if (fits(linear(light, a * middle, b * middle))) low = middle; else high = middle;
    }
    channels = linear(light, a * low, b * low);
  }
  return `#${channels.map(channel => {
    const value = Math.min(1, Math.max(0, channel));
    const encoded = value <= 0.0031308 ? value * 12.92 : 1.055 * value ** (1 / 2.4) - 0.055;
    return Math.round(Math.min(1, Math.max(0, encoded)) * 255).toString(16).padStart(2, "0");
  }).join("")}`;
}

const chroma = (color: Lab) => Math.hypot(color[1], color[2]);
const hue = (color: Lab) => Math.atan2(color[2], color[1]);
const polar = (lightness: number, amount: number, angle: number): Lab => [lightness, amount * Math.cos(angle), amount * Math.sin(angle)];

/** The contrast ratio of two colors (1 to 21), as WCAG defines it. */
export function contrast(left: string, right: string): number {
  const luminance = (hex: string) => {
    const [red, green, blue] = [1, 3, 5].map(index => {
      const value = parseInt(hex.slice(index, index + 2), 16) / 255;
      return value <= 0.04045 ? value / 12.92 : ((value + 0.055) / 1.055) ** 2.4;
    });
    return 0.2126 * red + 0.7152 * green + 0.0722 * blue;
  };
  const [low, high] = [luminance(left), luminance(right)].sort((a, b) => a - b);
  return (high + 0.05) / (low + 0.05);
}

/** The perceptual lightness of a color, from 0 (black) to 1 (white). */
export const lightness = (hex: string): number => toLab(hex)[0];

/** The colors a scheme can choose, as a palette shows them. */
export function schemeColors(palette: Palette, mode: PaletteMode): Readonly<Record<SchemeColorName, string>> {
  const anchors = layouts[mode].anchors;
  return Object.fromEntries(schemeColorNames.map(name => [name, palette[anchors[name]]])) as Record<SchemeColorName, string>;
}

/**
 * The palette of the darker theme: the dark palette with its surfaces (the window background, the panels
 * and what is raised above them) one notch down, each keeping its tint. Text and accents are the same,
 * so the theme is deeper without everything in it being dimmer.
 */
export function darkerPalette(dark: Palette): Palette {
  const next: Record<PaletteColor, string> = { ...dark };
  for (const name of layouts.dark.surfaces) {
    const [light, a, b] = toLab(dark[name]);
    const lower = Math.max(0, light - darkerShift), kept = light > 0 ? lower / light : 0;
    next[name] = toHex([lower, a * kept, b * kept]);
  }
  return next;
}

// How a group of steps takes the tint of its anchor's new color. `strength` is what is compared: chroma, or
// chroma per lightness for dark surfaces. A weaker tint scales every step down with the anchor; a stronger one
// scales them up to `most` times and adds the rest, so that a step that was almost a gray does not explode.
function tinting(anchor: Lab, target: Lab, strength: (color: Lab) => number, most: number) {
  const from = strength(anchor), goal = strength(target);
  const scale = from > neutral ? Math.min(goal / from, most) : 0;
  const extra = Math.max(0, goal - scale * from);
  const tinted = chroma(anchor) > neutral && chroma(target) > neutral;
  const turn = tinted ? hue(target) - hue(anchor) : 0;
  return (color: Lab): [amount: number, angle: number] =>
    [strength(color) * scale + extra, chroma(color) > neutral && tinted ? hue(color) + turn : hue(target)];
}

// Lightness as the display encodes it. Near black, Oklab lightness falls much faster than the encoded value:
// dark surfaces keep their distances in the encoded value, or a black background would swallow the panels.
const encoded = (light: number) => { const linearLight = light ** 3; return linearLight <= 0.0031308 ? linearLight * 12.92 : 1.055 * linearLight ** (1 / 2.4) - 0.055; };
const decoded = (value: number) => Math.cbrt(value <= 0.04045 ? value / 12.92 : ((value + 0.055) / 1.055) ** 2.4);

// The window background: every surface moves by as much as the background did and takes its tint.
function setBackground(palette: Record<PaletteColor, string>, mode: PaletteMode, target: string) {
  const layout = layouts[mode], anchor = toLab(palette[layout.anchors.background]), to = toLab(target), dark = mode === "dark";
  // Dark surfaces are compared by saturation: the same tint has less chroma the darker it is.
  const tint = tinting(anchor, to, color => !dark ? chroma(color) : color[0] > 0.02 ? chroma(color) / color[0] : 0, 1);
  for (const name of layout.surfaces) {
    const color = toLab(palette[name]);
    const level = Math.min(1, Math.max(0, dark ? decoded(Math.max(0, encoded(color[0]) + encoded(to[0]) - encoded(anchor[0]))) : color[0] + to[0] - anchor[0]));
    const [amount, angle] = tint(color);
    palette[name] = toHex(polar(level, dark ? amount * level : amount, angle));
  }
  palette[layout.anchors.background] = target;
  // A background with a clear tint also turns the grays and the text steps to its hue, so that one color
  // makes a coherent scheme. One that is almost a gray says nothing reliable about a hue and leaves them.
  const clear = Math.min(1, Math.max(0, (chroma(to) - 0.006) / 0.014)), weight = clear * clear * (3 - 2 * clear);
  if (weight === 0) return;
  for (const name of [...grays, ...layout.texts]) {
    const [light, a, b] = toLab(palette[name]), amount = Math.hypot(a, b);
    const angle = chroma(anchor) > neutral ? Math.atan2(b, a) + hue(to) - hue(anchor) : hue(to);
    palette[name] = toHex([light, a + (amount * Math.cos(angle) - a) * weight, b + (amount * Math.sin(angle) - b) * weight]);
  }
}

// A group that lies between a fixed edge and its anchor: the anchor goes to the target and the other steps
// keep their relative place between the edge and it.
function stretch(palette: Record<PaletteColor, string>, names: readonly PaletteColor[], anchorName: PaletteColor, edgeName: PaletteColor, target: string,
  recolor: (anchor: Lab, to: Lab) => (color: Lab) => readonly [a: number, b: number]) {
  const anchor = toLab(palette[anchorName]), to = toLab(target), edge = toLab(palette[edgeName])[0];
  // A target at the edge or beyond it leaves no room: the group keeps a tenth of its extent and moves as one.
  const ratio = Math.max(0.1, Math.abs(anchor[0] - edge) > 0.01 ? (to[0] - edge) / (anchor[0] - edge) : 1);
  const origin = to[0] - (anchor[0] - edge) * ratio, colored = recolor(anchor, to);
  for (const name of names) {
    const color = toLab(palette[name]);
    palette[name] = toHex([Math.min(1, Math.max(0, origin + (color[0] - edge) * ratio)), ...colored(color)]);
  }
  palette[anchorName] = target;
}

// Muted text: the mid grays (muted text, icons, borders) follow it in lightness and take its tint.
function setMuted(palette: Record<PaletteColor, string>, mode: PaletteMode, target: string) {
  const layout = layouts[mode];
  stretch(palette, grays, layout.anchors.muted, layout.surfaceEdge, target, (anchor, to) => {
    const tint = tinting(anchor, to, chroma, 1);
    return color => { const [amount, angle] = tint(color); return [amount * Math.cos(angle), amount * Math.sin(angle)]; };
  });
}

// Text: the steps near it follow it in lightness. They are almost grays, so its tint is added, not scaled.
function setText(palette: Record<PaletteColor, string>, mode: PaletteMode, target: string) {
  const layout = layouts[mode];
  stretch(palette, layout.texts, layout.anchors.text, layout.textEdge, target,
    (anchor, to) => color => [color[1] + to[1] - anchor[1], color[2] + to[2] - anchor[2]]);
}

// An accent: its color families take its hue, scale to its chroma and move to its lightness. Steps that would
// leave the usable range of lightness move closer together instead. `step` is the anchor's place in its family.
function setFamilies(palette: Record<PaletteColor, string>, anchorName: PaletteColor, families: readonly string[], target: string) {
  const anchor = toLab(palette[anchorName]), to = toLab(target), step = anchorName.slice(anchorName.lastIndexOf("-"));
  const lightnesses = families.flatMap(steps).map(name => toLab(palette[name])[0]);
  const above = Math.max(...lightnesses) - anchor[0], below = anchor[0] - Math.min(...lightnesses);
  const up = above > 0.01 ? Math.min(1, Math.max(0, 0.97 - to[0]) / above) : 1;
  const down = below > 0.01 ? Math.min(1, Math.max(0, to[0] - 0.12) / below) : 1;
  for (const family of families) {
    // Each family turns from its own hue, so that all of them end on the hue of the accent.
    const own = toLab(palette[`${family}${step}` as PaletteColor]);
    const tint = tinting([anchor[0], chroma(anchor) * Math.cos(hue(own)), chroma(anchor) * Math.sin(hue(own))], to, chroma, strongestTint);
    for (const name of steps(family)) {
      const color = toLab(palette[name]), offset = color[0] - anchor[0];
      const [amount, angle] = tint(color);
      palette[name] = toHex(polar(to[0] + offset * (offset > 0 ? up : down), amount, angle));
    }
  }
  palette[anchorName] = target;
}

/**
 * A palette with some of its colors chosen. Each chosen color is shown exactly as given, and the steps
 * around it follow: the surfaces follow the background, the mid grays the muted text, the text steps the
 * text, and each accent its color families. A color that is absent or not a color changes nothing.
 */
export function adjustPalette(palette: Palette, mode: PaletteMode, colors: SchemeColors): Palette {
  const next: Record<PaletteColor, string> = { ...palette };
  const anchors = layouts[mode].anchors;
  // In this order: the mid grays are placed from the surfaces, and the text steps from the mid grays.
  for (const name of ["background", "muted", "text", "accent", "success", "warning", "danger"] as const) {
    const target = parseColor(colors[name]);
    if (!target || target === next[anchors[name]]) continue;
    if (name === "background") setBackground(next, mode, target);
    else if (name === "muted") setMuted(next, mode, target);
    else if (name === "text") setText(next, mode, target);
    else setFamilies(next, anchors[name], accentFamilies[name], target);
  }
  return next;
}
