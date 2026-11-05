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

/** The colors of a drawn diagram as the page shows them: the `--pele-*` custom properties, resolved. */
export type DiagramColors = Readonly<{ dark: boolean; variables: Readonly<Record<string, string>> }>;

// The custom properties that color a diagram (see the `svg.pele` rule of the stylesheet).
const diagramProperties = ["--pele-bg", "--pele-fg", "--pele-muted", "--pele-line", "--pele-surface", "--pele-surface-alt", "--pele-border",
  "--pele-accent", "--pele-critical", ...Array.from({ length: 8 }, (_, index) => `--pele-series-${index + 1}`)];

/** The colors a drawn diagram has in the page: what a picture or a page of its own needs, where the stylesheet of the page is not. */
export function diagramColors(drawing: Element): DiagramColors {
  const style = drawing.ownerDocument.defaultView!.getComputedStyle(drawing);
  const variables = Object.fromEntries(diagramProperties.map(name => [name, style.getPropertyValue(name).trim()]).filter(([, value]) => value));
  return { dark: drawing.ownerDocument.documentElement.dataset.theme !== "light", variables };
}

/**
 * A drawn diagram as a picture (an `svg+xml` data address), in the colors it has in the page: a picture has no
 * stylesheet, so its root carries the resolved `--pele-*` properties. It can be zoomed without being drawn again.
 */
export function diagramPicture(drawing: SVGSVGElement): string {
  const copy = drawing.cloneNode(true) as SVGSVGElement;
  for (const [name, value] of Object.entries(diagramColors(drawing).variables)) copy.style.setProperty(name, value);
  // A diagram that says no size is shown at the size it has in the page, which is no smaller than its box.
  if (!copy.getAttribute("width") || !copy.getAttribute("height")) {
    const box = drawing.viewBox?.baseVal;
    const width = box?.width || drawing.getBoundingClientRect().width, height = box?.height || drawing.getBoundingClientRect().height;
    if (width > 0 && height > 0) { copy.setAttribute("width", String(width)); copy.setAttribute("height", String(height)); }
  }
  const text = new (drawing.ownerDocument.defaultView ?? window).XMLSerializer().serializeToString(copy);
  return `data:image/svg+xml;charset=utf-8,${encodeURIComponent(text)}`;
}

/**
 * A diagram as PNG, from its picture: `scale` times its size, so that it stays sharp where it is pasted, with a margin, on
 * `background` (the color behind it in the page), so that its text reads on a page of another color.
 */
export async function diagramPng(picture: string, background: string, scale = 2, margin = 16): Promise<Blob> {
  const image = new Image();
  image.src = picture;
  await image.decode();
  const width = Math.max(1, Math.round((image.naturalWidth + 2 * margin) * scale)), height = Math.max(1, Math.round((image.naturalHeight + 2 * margin) * scale));
  const canvas = document.createElement("canvas");
  canvas.width = width; canvas.height = height;
  const context = canvas.getContext("2d");
  if (!context) throw new Error("The diagram could not be drawn.");
  if (background) { context.fillStyle = background; context.fillRect(0, 0, width, height); }
  context.drawImage(image, margin * scale, margin * scale, image.naturalWidth * scale, image.naturalHeight * scale);
  return new Promise((resolve, reject) => canvas.toBlob(blob => blob ? resolve(blob) : reject(new Error("The diagram could not be drawn.")), "image/png"));
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
