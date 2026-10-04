const declaration = /--bp-palette-([a-z0-9-]+)\s*:\s*(#[0-9a-fA-F]{6})\s*;/g;
// A palette declaration (kept as it is), a hexadecimal color in a value, or an rgb()/rgba() color with literal channels.
const color = /(--bp-palette-[a-z0-9-]+\s*:\s*#[0-9a-fA-F]{6})|([: ,(])#([0-9a-fA-F]{8}|[0-9a-fA-F]{6}|[0-9a-fA-F]{3})(?![0-9a-zA-Z_-])|rgba?\(\s*(\d{1,3})\s*,\s*(\d{1,3})\s*,\s*(\d{1,3})\s*(?:,\s*([0-9.]+%?)\s*)?\)/g;

/**
 * Rewrites Blueprint's stylesheet so that every color literal equal to one of its palette colors refers to the
 * palette variable instead (`#2f343c` becomes `var(--bp-palette-dark-gray-3)`, `rgba(17, 20, 24, 0.3)` becomes
 * `rgb(from var(--bp-palette-black) r g b / 0.3)`). Blueprint compiles most component colors to literals, so
 * without this a color scheme that redefines the palette variables would reach only part of the components.
 * The stylesheet renders the same as before while the palette variables keep Blueprint's values.
 */
export function blueprintPaletteVariables(css: string): string {
  const names = new Map<string, string>();
  for (const match of css.matchAll(declaration)) {
    const hex = match[2].toLowerCase();
    if (!names.has(hex)) names.set(hex, `var(--bp-palette-${match[1]})`);
  }
  if (names.size === 0) return css;
  const alpha = (variable: string, value: string) => `rgb(from ${variable} r g b / ${value})`;
  return css.replace(color, (text, kept: string | undefined, before: string | undefined, hex: string | undefined,
    red: string | undefined, green: string | undefined, blue: string | undefined, opacity: string | undefined) => {
    if (kept) return text;
    if (hex !== undefined) {
      const digits = hex.length === 3 ? [...hex].map(digit => digit + digit).join("") : hex;
      const variable = names.get(`#${digits.slice(0, 6).toLowerCase()}`);
      if (!variable) return text;
      return before + (digits.length === 8 ? alpha(variable, (parseInt(digits.slice(6), 16) / 255).toFixed(3).replace(/\.?0+$/, "")) : variable);
    }
    const channels = [red, green, blue].map(Number);
    if (channels.some(channel => channel > 255)) return text;
    const variable = names.get(`#${channels.map(channel => channel.toString(16).padStart(2, "0")).join("")}`);
    return !variable ? text : opacity === undefined ? variable : alpha(variable, opacity);
  });
}
