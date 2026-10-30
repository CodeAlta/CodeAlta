import createDOMPurify, { type DOMPurify } from "dompurify";

/** How diagrams are drawn: what makes the same text draw differently, named by `key`. */
export type DiagramAppearance = Readonly<{ key: string; fontFamily?: string; maxWidth?: number }>;

/** A longer text is left as a code block. */
export const maximumDiagramLength = 20_000;

/** A text that the renderer refused, with the first line of what it said: shown under the block, which stays code. */
export type DiagramRefusal = Readonly<{ refused: string }>;

// What a refusal shows: its first line, short. A parse error goes on with the line and an arrow under it.
const maximumRefusalLength = 160;

/** The first line of what refused a text, for the reader; never empty. */
export function refusalOf(error: unknown): string {
  const message = error instanceof Error ? error.message : String(error ?? "");
  const line = message.split(/\r?\n/).map(value => value.trim()).find(value => value.length > 0)?.replace(/:$/, "") ?? "";
  if (!line) return "Not a diagram that can be read";
  return line.length > maximumRefusalLength ? `${line.slice(0, maximumRefusalLength - 1)}…` : line;
}

/**
 * Keeps the diagrams drawn from fenced `mermaid` blocks, by text and appearance: Markdown is rendered
 * synchronously, so a diagram is drawn once on the side and found here by every later render.
 */
export function createDiagramCache(draw: (text: string, appearance: DiagramAppearance) => Promise<string | DiagramRefusal | null>, capacity = 48) {
  const drawn = new Map<string, string | DiagramRefusal | null>();
  const drawing = new Map<string, Promise<void>>();
  const identity = (text: string, appearance: DiagramAppearance) => `${appearance.key}\n${text}`;
  const found = (text: string, appearance: DiagramAppearance) => {
    if (text.length > maximumDiagramLength || !text.trim()) return null;
    const key = identity(text, appearance);
    const value = drawn.get(key);
    if (value !== undefined) { drawn.delete(key); drawn.set(key, value); } // Most recently used last.
    return value;
  };
  return {
    /** The SVG of a drawn diagram; null when the text is not a diagram; undefined when it has not been drawn. */
    get(text: string, appearance: DiagramAppearance): string | null | undefined {
      const value = found(text, appearance);
      return typeof value === "object" && value !== null ? null : value;
    },
    /** Why the renderer refused a text that it was asked to draw; null when it did not, or did not say. */
    refusal(text: string, appearance: DiagramAppearance): string | null {
      const value = found(text, appearance);
      return typeof value === "object" && value !== null ? value.refused : null;
    },
    /** Draws a diagram unless it is drawn or being drawn; never rejects. */
    draw(text: string, appearance: DiagramAppearance): Promise<void> {
      const key = identity(text, appearance);
      if (drawn.has(key) || text.length > maximumDiagramLength || !text.trim()) return Promise.resolve();
      let work = drawing.get(key);
      if (!work) {
        work = Promise.resolve().then(() => draw(text, appearance)).catch(() => null).then(svg => {
          drawing.delete(key);
          drawn.set(key, svg);
          while (drawn.size > capacity) drawn.delete(drawn.keys().next().value!);
        });
        drawing.set(key, work);
      }
      return work;
    },
  };
}

/** The size of the text of a diagram, in pixels. */
const diagramFontSize = 14;

/**
 * How the window draws diagrams now: in its font, and no wider than `maxWidth` pixels when it is given (a wider
 * flowchart is laid out top to bottom). The colors are not part of it: a diagram takes them from the `--pele-*`
 * custom properties of the stylesheet, and follows the theme without being drawn again.
 */
export function diagramAppearance(maxWidth?: number): DiagramAppearance {
  const fontFamily = getComputedStyle(document.documentElement).fontFamily;
  const width = maxWidth !== undefined && maxWidth > 0 ? Math.floor(maxWidth) : undefined;
  return { key: `${fontFamily}|${width ?? ""}`, fontFamily, maxWidth: width };
}

let library: Promise<typeof import("pele/lazy")> | undefined;
let sequence = 0;

// Pele loads the code of a kind of diagram the first time one is drawn. It escapes the text of the labels and
// ignores click callbacks; links and images are off, so a diagram makes no request. The SVG it returns is
// sanitized again (SVG only, no script, no handler) before the window shows it.
async function drawWithPele(text: string, look: DiagramAppearance): Promise<string | DiagramRefusal | null> {
  const { renderAsync } = await (library ??= import("pele/lazy"));
  let svg: string;
  // A text that does not parse is said with the first line of the error, as the reader would fix it.
  try {
    svg = (await renderAsync(text, { fontFamily: look.fontFamily, fontSize: diagramFontSize, maxWidth: look.maxWidth,
      idPrefix: `codealta-diagram-${++sequence}-`, links: false, images: false, limit: maximumDiagramLength })).svg;
  } catch (error) { return { refused: refusalOf(error) }; }
  return (purifier ??= createDOMPurify(window)).sanitize(svg, { USE_PROFILES: { svg: true, svgFilters: true } });
}

let purifier: DOMPurify | undefined;

/** The diagrams of the window. */
export const diagrams = createDiagramCache(drawWithPele);
