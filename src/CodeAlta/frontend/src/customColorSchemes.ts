import type { ColorSchemeColors, ColorSchemeSaveRequest } from "#neoastra";
import { parseColor, schemeColorNames, schemeColors, type SchemeColorName, type SchemeColors } from "./colorPalette";
import { colorSchemes, colorVariants, maximumSchemeNameLength, schemePalette, type ColorScheme, type ColorVariant, type CustomColorScheme } from "./colorSchemes";
import type { MessageKey } from "./localization";

/** The colors a scheme shows in a theme, whether it chose them or follows its base. */
export function shownColors(scheme: ColorScheme | CustomColorScheme, variant: ColorVariant): Readonly<Record<SchemeColorName, string>> {
  return schemeColors(schemePalette(scheme, variant), variant === "light" ? "light" : "dark");
}

/**
 * An unsaved scheme to edit, made from another: a built-in scheme becomes its base, a custom scheme is
 * copied with the colors it chose.
 */
export function draftScheme(source: ColorScheme | CustomColorScheme, name: string): CustomColorScheme {
  return "base" in source ? { ...source, id: "", name } : { id: "", name, base: source.id, light: {}, dark: {}, darker: {} };
}

/**
 * A scheme with one color of a theme chosen, or (`null`) left again to what it follows. Choosing the color the
 * scheme would show anyway chooses nothing, so that a scheme keeps only what differs from its base.
 */
export function withColor(scheme: CustomColorScheme, variant: ColorVariant, name: SchemeColorName, color: string | null): CustomColorScheme {
  const rest: Partial<Record<SchemeColorName, string>> = { ...scheme[variant] };
  delete rest[name];
  const without = { ...scheme, [variant]: rest };
  const chosen = color === null ? null : parseColor(color);
  return chosen === null || chosen === shownColors(without, variant)[name] ? without : { ...scheme, [variant]: { ...rest, [name]: chosen } };
}

const colorsInOrder = (colors: SchemeColors): SchemeColors => Object.fromEntries(schemeColorNames.filter(name => colors[name]).map(name => [name, colors[name]]));

/** Whether two schemes are the same scheme with the same name, base and chosen colors. */
export function sameScheme(left: CustomColorScheme, right: CustomColorScheme): boolean {
  const plain = (scheme: CustomColorScheme) => JSON.stringify([scheme.id, scheme.name.trim(), scheme.base, ...colorVariants.map(variant => colorsInOrder(scheme[variant]))]);
  return plain(left) === plain(right);
}

const sameName = (left: string, right: string) => left.trim().localeCompare(right.trim(), undefined, { sensitivity: "accent" }) === 0;

/** Why a scheme cannot be saved under its name; null when it can. Names are compared without regard to case. */
export function schemeNameProblem(scheme: Pick<CustomColorScheme, "id" | "name">, custom: readonly CustomColorScheme[]): MessageKey | null {
  const name = scheme.name.trim();
  if (name === "") return "Enter a name for the color scheme.";
  if (name.length > maximumSchemeNameLength) return "The name of a color scheme has at most 64 characters.";
  return colorSchemes.some(other => sameName(other.name, name)) || custom.some(other => other.id !== scheme.id && sameName(other.name, name))
    ? "A color scheme with this name already exists." : null;
}

/**
 * A name for a copy of a scheme that no scheme has yet: "Plum copy", then "Plum copy 2". `copy` words the
 * first one in the language of the window. The copy of a copy is the next copy of the same scheme.
 */
export function copyName(name: string, copy: (name: string) => string, custom: readonly CustomColorScheme[]): string {
  const taken = (candidate: string) => colorSchemes.some(scheme => sameName(scheme.name, candidate)) || custom.some(scheme => sameName(scheme.name, candidate));
  const [before, after] = copy("\u0000").split("\u0000");
  const unnumbered = name.trim().replace(/ \d+$/, "");
  const copied = unnumbered.length > before.length + after.length && unnumbered.startsWith(before) && unnumbered.endsWith(after);
  const source = copied ? unnumbered.slice(before.length, unnumbered.length - after.length) : name.trim();
  // The name of the source gives way when the whole does not fit, with room left for a number.
  const room = maximumSchemeNameLength - before.length - after.length - 4;
  const base = copy(source.slice(0, Math.max(1, room)).trim());
  for (let number = 1; number < 1000; number++) {
    const candidate = number === 1 ? base : `${base} ${number}`;
    if (!taken(candidate)) return candidate;
  }
  return base;
}

const wireColors = (colors: SchemeColors): ColorSchemeColors => ({ background: colors.background ?? null, text: colors.text ?? null, muted: colors.muted ?? null,
  accent: colors.accent ?? null, success: colors.success ?? null, warning: colors.warning ?? null, danger: colors.danger ?? null });

/** The request that saves a scheme; a scheme without an id is a new one. */
export function schemeSaveRequest(scheme: CustomColorScheme): ColorSchemeSaveRequest {
  return { id: scheme.id || null, name: scheme.name.trim(), base: scheme.base,
    light: wireColors(scheme.light), dark: wireColors(scheme.dark), darker: wireColors(scheme.darker) };
}
