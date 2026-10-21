// The fonts of the window: the code font (code blocks, editors, file views, tool output) and the font of the prompt.
// The page styles them through two CSS variables of the root element, `--code-font` and `--prompt-font`
// (style.css); the user's choice, a preference of the window kept in its local storage, puts its fonts before
// the built-in one.

/** Which font the prompt is written in. */
export type PromptFont = "code" | "interface";
export type FontPreferences = Readonly<{
  /** The families of the code font, first choice first; empty for the built-in font alone. */
  code: readonly string[];
  prompt: PromptFont;
}>;

export const fontPreferencesKey = "codealta.desktop.fonts.v1";
export const promptFonts: readonly PromptFont[] = ["code", "interface"];
/** The family of the font the application brings: Cascadia Code with the icons of the Nerd Fonts. */
export const builtinCodeFont = "CaskaydiaCove Nerd Font";
export const defaultFontPreferences: FontPreferences = { code: [], prompt: "code" };
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
    };
  } catch { return defaultFontPreferences; }
}

export function persistFontPreferences(write: (value: string) => void, preferences: FontPreferences): boolean {
  try { write(JSON.stringify(preferences)); return true; }
  catch { return false; }
}

/**
 * The values of the font variables for the preferences; null leaves the variable to the stylesheet, which
 * gives the built-in code font and writes the prompt in it. The built-in font follows the user's fonts: it
 * draws what they lack, the icons of the Nerd Fonts among them.
 */
export function fontVariables(preferences: FontPreferences): Readonly<Record<"--code-font" | "--prompt-font", string | null>> {
  return {
    "--code-font": preferences.code.length ? `${fontFamilyList(preferences.code)}, var(--code-font-builtin)` : null,
    "--prompt-font": preferences.prompt === "interface" ? "var(--bp-typography-family-default)" : null,
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
