// The fonts of the window: the code font (code blocks, editors, file views, tool output), the font of the prompt,
// the size of the text of the code editors and that of the rest of the window. The page styles them through CSS
// variables of the root element, `--code-font`, `--prompt-font`, `--code-font-size` (style.css) and
// `--ui-text-scale` (textScale.ts); the user's choice, a preference of the window kept in its local storage, puts
// its fonts before the built-in one.

import { textScaleVariable } from "./textScale";

/** Which font the prompt is written in. */
export type PromptFont = "code" | "interface";
export type FontPreferences = Readonly<{
  /** The families of the code font, first choice first; empty for the built-in font alone. */
  code: readonly string[];
  prompt: PromptFont;
  /** The size of the text of the code editors, the diffs and the prompt, in CSS pixels. */
  codeSize: number;
  /** The size of the rest of the text of the window, in percent of the sizes of its stylesheets. */
  interfaceSize: number;
}>;

export const fontPreferencesKey = "codealta.desktop.fonts.v1";
export const promptFonts: readonly PromptFont[] = ["code", "interface"];
/** The family of the font the application brings: Cascadia Code with the icons of the Nerd Fonts. */
export const builtinCodeFont = "CaskaydiaCove Nerd Font";
export const defaultFontPreferences: FontPreferences = { code: [], prompt: "code", codeSize: 13, interfaceSize: 100 };
/** The sizes the code editors may have, and those Settings offers. */
export const minimumCodeFontSize = 9, maximumCodeFontSize = 24;
export const codeFontSizes: readonly number[] = [10, 11, 12, 13, 14, 15, 16, 18, 20];
/**
 * The interface text sizes the window may have, and those Settings offers. Larger, the text would outgrow the boxes
 * of fixed height around it: the zoom of the window enlarges the text and its boxes together.
 */
export const minimumInterfaceTextSize = 80, maximumInterfaceTextSize = 130;
export const interfaceTextSizes: readonly number[] = [80, 85, 90, 95, 100, 105, 110, 115, 120, 125, 130];

/** Whether a value is a size the code editors may have. */
export function validCodeFontSize(value: unknown): value is number {
  return typeof value === "number" && Number.isInteger(value) && value >= minimumCodeFontSize && value <= maximumCodeFontSize;
}

/** Whether a value is an interface text size the window may have. */
export function validInterfaceTextSize(value: unknown): value is number {
  return typeof value === "number" && Number.isInteger(value) && value >= minimumInterfaceTextSize && value <= maximumInterfaceTextSize;
}

/** The height of a line of the code editors for a size of their text: 20 pixels for the 13 of the default, in the same proportion. */
export function codeLineHeight(size: number): number {
  return Math.round(size * 20 / 13);
}
/** How many families a list keeps, and how long a name may be. */
export const maximumFontFamilies = 8, maximumFontFamilyLength = 64;

// The generic families of CSS are keywords: a quoted "monospace" would name a font of that name.
const genericFamilies: ReadonlySet<string> = new Set(["serif", "sans-serif", "monospace", "cursive", "fantasy", "system-ui", "math", "emoji", "fangsong",
  "ui-serif", "ui-sans-serif", "ui-monospace", "ui-rounded"]);

/**
 * The families a list the user wrote names, in its order: names separated by commas, quoted or not. A name that
 * a CSS string cannot hold as written (a quote, a backslash, a control character) or that is too long is left
 * out, as are the names after the first eight and the second time a name is given.
 */
export function fontFamilies(text: string): string[] {
  const families: string[] = [];
  for (const part of text.split(",")) {
    let name = part.trim();
    if (name.length >= 2 && (name[0] === "\"" || name[0] === "'") && name.at(-1) === name[0]) name = name.slice(1, -1).trim();
    name = name.replace(/\s+/g, " ");
    if (!name || name.length > maximumFontFamilyLength || /["'\\\u0000-\u001f\u007f]/.test(name)) continue;
    if (families.some(family => family.toLowerCase() === name.toLowerCase())) continue;
    families.push(name);
    if (families.length === maximumFontFamilies) break;
  }
  return families;
}

/** A list of families as the value of a CSS `font-family`: the names quoted, the generic families as keywords. */
export function fontFamilyList(families: readonly string[]): string {
  return families.map(family => genericFamilies.has(family.toLowerCase()) ? family.toLowerCase() : `"${family}"`).join(", ");
}

/** The preferences that were stored, with the default for anything that is missing or is not a value the window offers. */
export function restoreFontPreferences(read: () => string | null): FontPreferences {
  try {
    const raw = read();
    if (!raw || raw.length > 4096) return defaultFontPreferences;
    const stored: unknown = JSON.parse(raw);
    if (!stored || typeof stored !== "object") return defaultFontPreferences;
    const value = stored as Record<string, unknown>;
    return {
      code: Array.isArray(value.code) ? fontFamilies(value.code.filter(name => typeof name === "string").join(",")) : defaultFontPreferences.code,
      prompt: promptFonts.includes(value.prompt as PromptFont) ? value.prompt as PromptFont : defaultFontPreferences.prompt,
      codeSize: validCodeFontSize(value.codeSize) ? value.codeSize : defaultFontPreferences.codeSize,
      interfaceSize: validInterfaceTextSize(value.interfaceSize) ? value.interfaceSize : defaultFontPreferences.interfaceSize,
    };
  } catch { return defaultFontPreferences; }
}

export function persistFontPreferences(write: (value: string) => void, preferences: FontPreferences): boolean {
  try { write(JSON.stringify(preferences)); return true; }
  catch { return false; }
}

/** The variables of the root element the preferences set. */
export type FontVariable = "--code-font" | "--prompt-font" | "--code-font-size" | typeof textScaleVariable;

/**
 * The values of the font variables for the preferences; null leaves the variable to the stylesheet, which
 * gives the built-in code font, writes the prompt in it, and has the default sizes. The built-in font follows
 * the user's fonts: it draws what they lack, the icons of the Nerd Fonts among them.
 */
export function fontVariables(preferences: FontPreferences): Readonly<Record<FontVariable, string | null>> {
  return {
    "--code-font": preferences.code.length ? `${fontFamilyList(preferences.code)}, var(--code-font-builtin)` : null,
    "--prompt-font": preferences.prompt === "interface" ? "var(--bp-typography-family-default)" : null,
    "--code-font-size": preferences.codeSize !== defaultFontPreferences.codeSize ? `${preferences.codeSize}px` : null,
    [textScaleVariable]: preferences.interfaceSize !== defaultFontPreferences.interfaceSize ? String(preferences.interfaceSize / 100) : null,
  };
}

/** Sets the font variables on the style of the root element. */
export function applyFontPreferences(style: Pick<CSSStyleDeclaration, "setProperty" | "removeProperty">, preferences: FontPreferences): void {
  for (const [name, value] of Object.entries(fontVariables(preferences))) {
    if (value === null) style.removeProperty(name); else style.setProperty(name, value);
  }
}

/** One installed font, as the browser lists them. */
export type LocalFont = Readonly<{ family: string }>;

/**
 * The families of the installed fonts, each once, in alphabetical order, with the monospace ones first: the
 * fonts of code. `monospace` says whether a family is one, by the widths it gives two strings.
 */
export function installedFontFamilies(fonts: readonly LocalFont[], monospace: (family: string) => boolean, locale?: string): { monospace: string[]; other: string[] } {
  // A family a list could not keep as it is named is not offered.
  const families = [...new Set(fonts.map(font => font.family).filter(family => fontFamilies(family)[0] === family))]
    .sort((a, b) => a.localeCompare(b, locale, { sensitivity: "base" }));
  const result = { monospace: [] as string[], other: [] as string[] };
  for (const family of families) (monospace(family) ? result.monospace : result.other).push(family);
  return result;
}

/** The list once an installed font is chosen: it becomes the first family, in place of the first one. */
export function withFirstFamily(families: readonly string[], family: string): string[] {
  return fontFamilies([family, ...families.slice(1)].join(","));
}
