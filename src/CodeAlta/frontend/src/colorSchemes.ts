import { adjustPalette, contrast, darkerPalette, parseColor, schemeColorNames, type SchemeColorName, type SchemeColors } from "./colorPalette";
import { blueprintPalette, generatedColorSchemes, type Palette, type PaletteColor } from "./colorSchemes.gen";

/** A built-in color scheme of the window: the palettes of its dark and light themes. */
export type ColorScheme = Readonly<{ id: string; name: string; dark: Palette; light: Palette }>;

/** The theme a palette is for: the light one, the dark one, or the dark one with deeper surfaces. */
export type ColorVariant = "light" | "dark" | "darker";
export const colorVariants: readonly ColorVariant[] = ["light", "dark", "darker"];

/**
 * A color scheme of the user: a built-in scheme (`base`) with some of its colors chosen. `light` and `dark`
 * choose colors of the two themes; `darker` those of the darker theme, which otherwise follows the dark one.
 * The id is the name of the scheme's file; a scheme that has not been saved yet has none.
 */
export type CustomColorScheme = Readonly<{ id: string; name: string; base: string } & Record<ColorVariant, SchemeColors>>;

export const defaultColorScheme = "blueprint";
export const colorSchemeStorageKey = "codealta.desktop.colorScheme.v1";
/** Where the window keeps the custom scheme it shows, so that the next start has it before the host answers. */
export const customSchemeStorageKey = "codealta.desktop.colorScheme.custom.v1";
/** The longest name of a custom scheme, in UTF-16 units; the host has the same limit. */
export const maximumSchemeNameLength = 64;

/** Blueprint's own palette first, then the generated schemes in the order of their generator. */
export const colorSchemes: readonly ColorScheme[] = [
  { id: defaultColorScheme, name: "Blueprint", dark: blueprintPalette, light: blueprintPalette },
  ...generatedColorSchemes,
];
export const colorSchemeIds: readonly string[] = colorSchemes.map(scheme => scheme.id);

/** The scheme with an id; Blueprint's own for an id that names none. */
export const colorSchemeOf = (id: string): ColorScheme => colorSchemes.find(scheme => scheme.id === id) ?? colorSchemes[0];

// A custom scheme is selected under this prefix: its id is a file name and may be that of a built-in scheme.
const customPrefix = "custom:";
const customId = /^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$/;

/** The selection that stands for a custom scheme. */
export const customSchemeSelection = (id: string): string => customPrefix + id;
/** The id of the custom scheme a selection stands for; null for a built-in scheme or anything else. */
export function customSchemeIdOf(selection: string): string | null {
  const id = selection.startsWith(customPrefix) ? selection.slice(customPrefix.length) : "";
  return customId.test(id) ? id : null;
}

function chosenColors(value: unknown): SchemeColors {
  const colors: Partial<Record<SchemeColorName, string>> = {};
  if (value && typeof value === "object") for (const name of schemeColorNames) {
    const color = parseColor((value as Record<string, unknown>)[name]);
    if (color) colors[name] = color;
  }
  return colors;
}

/**
 * Reads a custom scheme from what the host answered or the window stored; null when it is not one.
 * A value that is not a color is left out: the scheme then shows its base there.
 */
export function parseCustomScheme(value: unknown): CustomColorScheme | null {
  if (!value || typeof value !== "object") return null;
  const record = value as Record<string, unknown>;
  if (typeof record.id !== "string" || !customId.test(record.id)) return null;
  const name = typeof record.name === "string" ? record.name.trim().slice(0, maximumSchemeNameLength) : "";
  return { id: record.id, name: name || record.id, base: typeof record.base === "string" ? record.base : defaultColorScheme,
    light: chosenColors(record.light), dark: chosenColors(record.dark), darker: chosenColors(record.darker) };
}

/**
 * The palette a scheme shows in a theme. The darker theme has no palette of its own: it is the dark one
 * made darker, and then what a custom scheme chose for it.
 */
export function schemePalette(scheme: ColorScheme | CustomColorScheme, variant: ColorVariant): Palette {
  if (!("base" in scheme)) return variant === "light" ? scheme.light : variant === "dark" ? scheme.dark : darkerPalette(scheme.dark);
  // A scheme whose base is not a built-in scheme of this version starts from Blueprint's.
  const base = colorSchemeOf(scheme.base);
  if (variant === "light") return adjustPalette(base.light, "light", scheme.light);
  const dark = adjustPalette(base.dark, "dark", scheme.dark);
  return variant === "dark" ? dark : adjustPalette(darkerPalette(dark), "dark", scheme.darker);
}

/** The colors that stand for a palette in a picker. */
export type ColorSchemeSwatch = Readonly<{ background: string; tint: string; foreground: string; accent: string }>;
export function schemeSwatch(palette: Palette, variant: ColorVariant): ColorSchemeSwatch {
  return variant === "light"
    ? { background: palette["light-gray-5"], tint: palette["gray-4"], foreground: palette["dark-gray-1"], accent: palette["blue-3"] }
    : { background: palette["dark-gray-1"], tint: palette["gray-1"], foreground: palette["light-gray-5"], accent: palette["blue-4"] };
}

/** What the window shows: its theme, the selected scheme and the palette on screen. */
export type ShownAppearance = Readonly<{ theme: "dark" | "light"; scheme: string; palette: Palette }>;

// The text Blueprint puts on a filled button or tag of each intent, and the palette step that fills it.
const intents = [["primary", "blue-3", "white"], ["success", "green-3", "white"], ["warning", "orange-3", "black"], ["danger", "red-3", "white"]] as const;

/**
 * The custom properties that show a palette: Blueprint's palette variables for the colors that differ from
 * Blueprint's own, and the text of a filled button where a chosen accent leaves the usual one unreadable.
 */
export function paletteVariables(palette: Palette): Readonly<Record<string, string>> {
  const variables: Record<string, string> = {};
  for (const name of Object.keys(blueprintPalette) as PaletteColor[])
    if (palette[name] !== blueprintPalette[name]) variables[`--bp-palette-${name}`] = palette[name];
  for (const [intent, fill, usual] of intents) {
    const other = usual === "white" ? "black" : "white";
    if (contrast(palette[fill], palette[usual]) < 3 && contrast(palette[fill], palette[other]) > contrast(palette[fill], palette[usual]))
      variables[`--bp-intent-${intent}-foreground`] = `var(--bp-palette-${other})`;
  }
  return variables;
}

// The custom properties a palette may set on the root: its own, never those of the start-up screen.
const owned = (property: string) => property.startsWith("--bp-palette-") || /^--bp-intent-[a-z]+-foreground$/.test(property);

function signature(text: string): string {
  let hash = 0x811c9dc5;
  for (let index = 0; index < text.length; index++) hash = Math.imul(hash ^ text.charCodeAt(index), 0x01000193);
  return (hash >>> 0).toString(16).padStart(8, "0");
}

/**
 * Shows a palette: sets the custom properties it needs on the root and removes those of the palette before.
 * The root's `data-palette` attribute then names what is set (absent for Blueprint's own palette), so that
 * what draws with the window's colors outside CSS (the code editor, diagrams) sees them change.
 */
export function applyPalette(root: Pick<HTMLElement, "style" | "dataset">, palette: Palette): void {
  const variables = paletteVariables(palette), style = root.style;
  const stale: string[] = [];
  for (let index = 0; index < style.length; index++) {
    const property = style.item(index);
    if (owned(property) && !(property in variables)) stale.push(property);
  }
  for (const property of stale) style.removeProperty(property);
  const entries = Object.entries(variables);
  for (const [property, value] of entries) if (style.getPropertyValue(property) !== value) style.setProperty(property, value);
  if (entries.length === 0) delete root.dataset.palette;
  else root.dataset.palette = signature(entries.map(([property, value]) => `${property}:${value}`).join(";"));
}

/** Shows an appearance on the document root. `darkClass` is the class that turns Blueprint's components dark. */
export function showAppearance(root: Pick<HTMLElement, "style" | "dataset" | "classList">, darkClass: string, appearance: ShownAppearance): void {
  root.dataset.theme = appearance.theme;
  root.classList.toggle(darkClass, appearance.theme === "dark");
  if (appearance.scheme === defaultColorScheme) delete root.dataset.colorScheme;
  else root.dataset.colorScheme = appearance.scheme;
  applyPalette(root, appearance.palette);
}
