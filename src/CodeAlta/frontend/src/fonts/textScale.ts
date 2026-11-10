// The size of the text of the window, apart from the code editors: Settings → Appearance → Fonts → Interface text
// size. The stylesheets give their sizes in px and rem; the build (vite.config.ts) multiplies each of them by the
// variable `--ui-text-scale` of the root element, which the preference sets (codeFonts.ts) and which is 1 when it
// is not set. Only the sizes of the text change: the layout around it keeps its lengths.

/** The variable of the root element that scales the text of the window; absent, the text has the sizes the stylesheets give. */
export const textScaleVariable = "--ui-text-scale";

const scaled = (size: string) => `calc(${size} * var(${textScaleVariable}, 1))`;
// A size the scale applies to: a length in px or rem, or a variable that holds one (Blueprint's typography sizes).
// The code text size (`--code-font-size`) is a setting of its own: what is drawn as in a code editor keeps it.
const length = /^-?(?:\d+\.?\d*|\.\d+)(?:px|rem)$/i;
const variable = /^var\((?!--code-font-size\))--[\w-]+\)$/;
// A declaration of the size of the text, longhand or shorthand, up to the end of its value. A custom property
// (`--size: 12px`) is not one: the declaration that uses it is scaled, so a size is never scaled twice.
const declaration = /(^|[{;\s])(font-size|font)(\s*:\s*)([^;{}]*?)(\s*!important)?(?=\s*[;}]|\s*$)/gi;
// In the shorthand, the size comes before the family, and before the line height after a slash; a variable is the
// size only before that slash. Words in parentheses are the arguments of a function, never the size.
const shorthandSize = /(^|\s)((?:-?(?:\d+\.?\d*|\.\d+)(?:px|rem))(?=\s*\/|\s)|var\((?!--code-font-size\))--[\w-]+\)(?=\s*\/))/i;

function scaledShorthand(value: string): string {
  const found = shorthandSize.exec(value);
  if (!found) return value;
  const start = found.index + found[1].length;
  const before = value.slice(0, start);
  if ((before.match(/\(/g)?.length ?? 0) !== (before.match(/\)/g)?.length ?? 0)) return value;
  return before + scaled(found[2]) + value.slice(start + found[2].length);
}

/**
 * Rewrites a stylesheet so that the size of its text follows the interface text size: `font-size: 12px` becomes
 * `font-size: calc(12px * var(--ui-text-scale, 1))`, as does the size in a `font` shorthand and a size given by a
 * variable. Sizes relative to the parent (em, %, keywords) already follow it, and a size computed by a function
 * (`clamp()`, `calc()`) is left as it is. Unscaled, the stylesheet renders as before.
 */
export function scaledTextSizes(css: string): string {
  return css.replace(declaration, (text, before: string, property: string, colon: string, value: string, important: string | undefined) => {
    const trimmed = value.trim();
    const next = property.toLowerCase() === "font-size"
      ? length.test(trimmed) || variable.test(trimmed) ? scaled(trimmed) : value
      : scaledShorthand(value);
    return next === value ? text : before + property + colon + next + (important ?? "");
  });
}
