import type { Palette, PaletteColor } from "./colorSchemes.gen";

/**
 * Palette arithmetic for the color schemes. A palette redefines Blueprint's palette variables; everything
 * here keeps the structure Blueprint designed its components for (the steps of a family stay in order and
 * keep their distances) and moves it as a whole. Colors are handled in the Oklab space, where equal steps
 * of lightness look equal.
 */

/** A color in the Oklab space: perceptual lightness (0 black to 1 white) and the two chroma axes. */
type Lab = readonly [lightness: number, a: number, b: number];

/** How much darker the surfaces of the darker theme are than those of the dark theme, in Oklab lightness. */
export const darkerShift = 0.06;

const steps = (family: string): PaletteColor[] => [1, 2, 3, 4, 5].map(step => `${family}-${step}` as PaletteColor);
// What a dark theme is laid on: the inset color, the window background, the panels and what is raised above them.
const darkSurfaces: readonly PaletteColor[] = ["black", ...steps("dark-gray")];

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

/** The perceptual lightness of a color, from 0 (black) to 1 (white). */
export const lightness = (hex: string): number => toLab(hex)[0];

/**
 * The palette of the darker theme: the dark palette with its surfaces (the window background, the panels
 * and what is raised above them) one notch down, each keeping its tint. Text and accents are the same,
 * so the theme is deeper without everything in it being dimmer.
 */
export function darkerPalette(dark: Palette): Palette {
  const next: Record<PaletteColor, string> = { ...dark };
  for (const name of darkSurfaces) {
    const [light, a, b] = toLab(dark[name]);
    const lower = Math.max(0, light - darkerShift), kept = light > 0 ? lower / light : 0;
    next[name] = toHex([lower, a * kept, b * kept]);
  }
  return next;
}
