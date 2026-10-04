import { appearanceKey, mixColors, shellColor } from "./shellColors";

/** How diagrams are drawn for the window's current theme and color scheme. */
export type DiagramAppearance = Readonly<{ key: string; variables: Readonly<Record<string, string | boolean>> }>;

/** A longer text is left as a code block. */
export const maximumDiagramLength = 20_000;

/**
 * Keeps the diagrams drawn from fenced `mermaid` blocks, by text and appearance: Markdown is rendered
 * synchronously, so a diagram is drawn once on the side and found here by every later render.
 */
export function createDiagramCache(draw: (text: string, appearance: DiagramAppearance) => Promise<string | null>, capacity = 48) {
  const drawn = new Map<string, string | null>();
  const drawing = new Map<string, Promise<void>>();
  const identity = (text: string, appearance: DiagramAppearance) => `${appearance.key}\n${text}`;
  return {
    /** The SVG of a drawn diagram; null when the text is not a diagram; undefined when it has not been drawn. */
    get(text: string, appearance: DiagramAppearance): string | null | undefined {
      if (text.length > maximumDiagramLength || !text.trim()) return null;
      const key = identity(text, appearance);
      const value = drawn.get(key);
      if (value !== undefined) { drawn.delete(key); drawn.set(key, value); } // Most recently used last.
      return value;
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

let appearance: DiagramAppearance | undefined;

/** The diagram colors of the window as it looks now, taken from the shell's own colors. */
export function diagramAppearance(): DiagramAppearance {
  const key = appearanceKey();
  if (appearance?.key === key) return appearance;
  const root = document.documentElement;
  const dark = root.dataset.theme !== "light";
  const probe = root.appendChild(document.createElement("span"));
  const color = (property: string, fallback: string) => shellColor(probe, property) ?? fallback;
  const surface = color("--panel-2", dark ? "#2f343c" : "#f6f7f9");
  const text = color("--text", dark ? "#f6f7f9" : "#1c2127");
  const muted = color("--muted", dark ? "#abb3bf" : "#5f6b7c");
  const accent = color("--accent", dark ? "#4c90f0" : "#2d72d2");
  const user = color("--user-accent", dark ? "#9d3f9d" : "#7c327c");
  const green = color("--green", dark ? "#32a467" : "#238551");
  const yellow = color("--yellow", dark ? "#f0b726" : "#c87619");
  const red = color("--red", dark ? "#e76a6e" : "#cd4246");
  probe.remove();
  // Charts take the shell's own hues, strong first, then the same hues softened.
  const hues = [accent, user, green, yellow, red, muted];
  const slices = Object.fromEntries([...hues.map(hue => mixColors(surface, hue, 0.8)), ...hues.map(hue => mixColors(surface, hue, 0.45))]
    .map((value, index) => [`pie${index + 1}`, value]));
  const node = mixColors(surface, accent, dark ? 0.22 : 0.12);
  appearance = {
    key,
    variables: {
      darkMode: dark, background: surface, fontFamily: getComputedStyle(root).fontFamily, fontSize: "13px",
      primaryColor: node, primaryTextColor: text, primaryBorderColor: mixColors(surface, accent, 0.7),
      secondaryColor: mixColors(surface, user, dark ? 0.24 : 0.12), secondaryTextColor: text, secondaryBorderColor: mixColors(surface, user, 0.6),
      tertiaryColor: mixColors(surface, green, dark ? 0.2 : 0.1), tertiaryTextColor: text, tertiaryBorderColor: mixColors(surface, green, 0.6),
      lineColor: muted, textColor: text, mainBkg: node, nodeBorder: mixColors(surface, accent, 0.7),
      clusterBkg: mixColors(surface, text, 0.04), clusterBorder: mixColors(surface, text, 0.3), titleColor: text,
      edgeLabelBackground: surface, noteBkgColor: mixColors(surface, yellow, dark ? 0.25 : 0.3), noteTextColor: text,
      noteBorderColor: mixColors(surface, yellow, 0.6),
      ...slices, pieStrokeColor: surface, pieStrokeWidth: "2px", pieOuterStrokeColor: mixColors(surface, text, 0.3), pieOuterStrokeWidth: "1px",
      pieTitleTextColor: text, pieSectionTextColor: dark ? "#ffffff" : "#111418", pieLegendTextColor: text,
    },
  };
  return appearance;
}

let library: Promise<typeof import("mermaid")> | undefined;
let queue: Promise<unknown> = Promise.resolve();
let sequence = 0;

// Mermaid keeps one configuration: draw one diagram at a time, each under its own appearance. Its strict
// security level encodes markup in labels, disables click handlers and sanitizes the SVG it returns.
function drawWithMermaid(text: string, look: DiagramAppearance): Promise<string | null> {
  const work = queue.then(async () => {
    const { default: mermaid } = await (library ??= import("mermaid"));
    mermaid.initialize({ startOnLoad: false, securityLevel: "strict", suppressErrorRendering: true,
      theme: "base", themeVariables: look.variables, fontFamily: String(look.variables.fontFamily) });
    if (!await mermaid.parse(text, { suppressErrors: true })) return null;
    return (await mermaid.render(`codealta-diagram-${++sequence}`, text)).svg;
  });
  queue = work.catch(() => null);
  return work;
}

/** The diagrams of the window. */
export const diagrams = createDiagramCache(drawWithMermaid);
