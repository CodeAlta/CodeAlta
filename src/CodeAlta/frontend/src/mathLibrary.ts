import createDOMPurify from "dompurify";
import katex from "katex";
// `\ce{…}` and `\pu{…}` for chemistry, and the LaTeX of the formulas in a copied selection.
import "katex/contrib/mhchem";
import "katex/contrib/copy-tex";

const r = String.raw;
// Commands that models write and KaTeX does not have. `\label` is read and shows nothing: KaTeX does not number equations.
const builtInMacros: Readonly<Record<string, string>> = Object.freeze({
  [r`\DeclareMathOperator`]: r`\gdef#1{\operatorname{#2}}`,
  [r`\label`]: r`\vphantom{#1}`,
  [r`\abs`]: r`\left\lvert #1 \right\rvert`,
  [r`\norm`]: r`\left\lVert #1 \right\rVert`,
  [r`\dv`]: r`\frac{\mathrm{d}#1}{\mathrm{d}#2}`,
  [r`\pdv`]: r`\frac{\partial #1}{\partial #2}`,
});

/** The formulas of one text, drawn in order: a macro that one of them defines serves the next ones. */
export type MathFormulas = Readonly<{ render(tex: string, display: boolean): string }>;

/** KaTeX, loaded: it draws the formulas of a text as sanitized HTML (with their MathML, which holds their LaTeX). */
export type MathLibrary = Readonly<{ formulas(): MathFormulas }>;

/** Creates the library for a window: its sanitizer belongs to that window. */
export function createMathLibrary(view: Window & typeof globalThis): MathLibrary {
  const purifier = createDOMPurify(view);
  // KaTeX writes classes, inline styles, SVG for its stretchy signs and MathML; `semantics` and `annotation` keep the
  // LaTeX of the formula, which copying reads. A formula is untrusted (`trust: false`): this is a second barrier.
  const sanitize = (html: string) => purifier.sanitize(html, { USE_PROFILES: { html: true, svg: true, mathMl: true },
    ADD_TAGS: ["semantics", "annotation"], ADD_ATTR: ["encoding"] });
  return {
    formulas() {
      const macros: Record<string, string> = { ...builtInMacros };
      const options = (display: boolean) => ({ displayMode: display, output: "htmlAndMathml" as const, trust: false, strict: "ignore" as const,
        maxExpand: 1000, maxSize: 50, macros, globalGroup: true });
      return {
        render(tex, display) {
          // A `\newcommand` of a name KaTeX already has (`\R`) redefines it, as models mean it to.
          let source = tex;
          for (let attempt = 0; attempt < 8; attempt++) {
            try { return sanitize(katex.renderToString(source, { ...options(display), throwOnError: true })); }
            catch (error) {
              const name = /\\newcommand\{(\\[a-zA-Z@]+)\} attempting to redefine/.exec(error instanceof Error ? error.message : "")?.[1];
              const redefined = name ? source.replace(`\\newcommand{${name}}`, `\\renewcommand{${name}}`) : source;
              if (redefined === source) break;
              source = redefined;
            }
          }
          // A formula that cannot be read shows its source, with what KaTeX says of it as its title.
          return sanitize(katex.renderToString(tex, { ...options(display), throwOnError: false }));
        },
      };
    },
  };
}
