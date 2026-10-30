import { diagramAppearance, diagrams } from "../diagrams";
import { createMarkdownRenderer, equationCopy, followFootnote, markdownPending } from "../markdownBoundary";
import { loadMath } from "../math";
import { el, iconSvg } from "./dom";

/** What the page asks of the server for a diagram page: `POST /api/diagrams`. */
export type DiagramUpload = (body: Readonly<{ svg: string; background?: string; text?: string; dark?: boolean }>) => Promise<{ url?: string }>;

/** The colors of a drawn diagram as the page shows them: the `--pele-*` custom properties, resolved. */
export type DiagramColors = Readonly<{ dark: boolean; variables: Readonly<Record<string, string>> }>;

// The custom properties that color a diagram (see the `svg.pele` rule of the stylesheet).
const diagramProperties = ["--pele-bg", "--pele-fg", "--pele-muted", "--pele-line", "--pele-surface", "--pele-surface-alt", "--pele-border",
  "--pele-accent", "--pele-critical", ...Array.from({ length: 8 }, (_, index) => `--pele-series-${index + 1}`)];

/** The colors a drawn diagram has in the page: what its own page needs, where the stylesheet of the page is not. */
export function diagramColors(drawing: Element): DiagramColors {
  const style = drawing.ownerDocument.defaultView!.getComputedStyle(drawing);
  const variables = Object.fromEntries(diagramProperties.map(name => [name, style.getPropertyValue(name).trim()]).filter(([, value]) => value));
  return { dark: drawing.ownerDocument.documentElement.dataset.theme !== "light", variables };
}

/**
 * The SVG of a diagram as its own page shows it: its width becomes a value the page reads (`data-w` and `--w`), so the
 * diagram can be shown at its size and zoomed, and it carries its colors.
 */
export function diagramPageSvg(svg: string, variables: Readonly<Record<string, string>> = {}): string {
  const root = /^<svg\b[^>]*>/u.exec(svg)?.[0];
  if (!root) return svg;
  const width = parseFloat(/\swidth="([\d.]+)"/u.exec(root)?.[1] ?? "") || 0;
  const declarations = (width ? `--w:${width}px;` : "") + Object.entries(variables)
    .map(([name, value]) => `${name}:${value.replace(/[&"<>]/gu, "")};`).join("");
  if (!declarations) return svg;
  const dressed = / style="/u.test(root) ? root.replace(/ style="/u, ` style="${declarations}`) : root.replace(/\s*(\/?>)$/u, ` style="${declarations}"$1`);
  return (width ? dressed.replace(/^<svg\b/u, `<svg data-w="${width}"`) : dressed) + svg.slice(root.length);
}

/**
 * The pages of drawn diagrams, made on the server the first time a diagram is opened (never when it is drawn), once per
 * drawing and colors. A page that could not be made is asked for again at the next open.
 */
export function createDiagramPages(upload: DiagramUpload, capacity = 48) {
  const made = new Map<string, Promise<string | null>>();
  const ready = new Map<string, string>();
  const identity = (svg: string, colors: DiagramColors) => `${JSON.stringify(colors)}\n${svg}`;
  return {
    /** The address of a page already made, or undefined. */
    known: (svg: string, colors: DiagramColors) => ready.get(identity(svg, colors)),
    /** Makes the page of a diagram (once), and gives its address, or null when it could not be made. */
    open(svg: string, colors: DiagramColors, base: string): Promise<string | null> {
      const key = identity(svg, colors);
      let page = made.get(key);
      if (!page) {
        const { variables } = colors;
        page = upload({ svg: diagramPageSvg(svg, variables), background: variables["--pele-bg"] ?? "", text: variables["--pele-fg"] ?? "", dark: colors.dark })
          .then(answer => answer.url ? new URL(answer.url, base).href : null)
          .catch(() => null)
          .then(url => { if (url) ready.set(key, url); else made.delete(key); return url; });
        made.set(key, page);
        while (made.size > capacity) { const oldest = made.keys().next().value!; made.delete(oldest); ready.delete(oldest); }
      }
      return page;
    },
  };
}

/** What a tap on a link of a message does: a page of the web opens in a tab of its own; anything else (a file of the computer) does nothing here. */
export function linkTarget(href: string | null): "web" | "none" {
  return href !== null && /^(https?:|mailto:)/iu.test(href.trim()) ? "web" : "none";
}

/**
 * The messages of the timeline, rendered as the desktop renders them (`createMarkdownRenderer`: markdown-it,
 * DOMPurify, highlighted code, diagrams drawn on the side), with what a phone adds: a tap on a diagram shows
 * it full screen, "Open" shows it in a tab of its own (not in the installed app, where full screen is the way), and
 * the copy button of a code block copies it.
 */
export function createMarkdownView(options: Readonly<{ window: Window & typeof globalThis; upload: DiagramUpload; installed: () => boolean; note: (text: string) => void }>) {
  const { window } = options, document = window.document;
  // A diagram is laid out for the width it has in a message: the screen's, less the margins of the message and of the
  // diagram. A flowchart wider than that is laid out top to bottom.
  const look = () => diagramAppearance(window.innerWidth - 64);
  const render = createMarkdownRenderer(window, undefined, look);
  const pages = createDiagramPages(options.upload);

  /**
   * Renders a text into an element; `redraw` is called once a diagram of it is drawn, or once KaTeX is loaded for its
   * formulas. Says whether it has a diagram (drawn again when the theme changes).
   */
  function fill(container: HTMLElement, text: string, redraw: () => void): boolean {
    const pending = markdownPending();
    container.innerHTML = render(text, true, pending);
    for (const figure of Array.from(container.querySelectorAll<HTMLElement>(".markdown-diagram"))) dressDiagram(figure);
    const appearance = look(), undrawn = pending.diagrams;
    if (undrawn.length) void Promise.all(undrawn.map(source => diagrams.draw(source, appearance))).then(() => { if (container.isConnected) redraw(); });
    if (pending.math) void loadMath().then(() => { if (container.isConnected) redraw(); }, () => { /* Not loaded: the formulas stay as written. */ });
    return undrawn.length > 0 || !!container.querySelector(".markdown-diagram, pre[data-language='mermaid']");
  }

  function dressDiagram(figure: HTMLElement) {
    figure.tabIndex = 0;
    figure.setAttribute("role", "button");
    figure.setAttribute("aria-label", "Diagram; tap to enlarge");
    const wrap = el(document, "div", "md-diagram-wrap");
    figure.replaceWith(wrap);
    wrap.append(figure);
    if (options.installed()) return;
    const open = el(document, "a", "diagram-open");
    open.target = "_blank";
    open.rel = "noopener";
    open.setAttribute("role", "button");
    open.setAttribute("aria-label", "Open the diagram in a new tab");
    open.innerHTML = iconSvg("external");
    open.append(el(document, "span", null, "Open"));
    wrap.append(open);
  }

  // A tab opened in the tap itself (a browser lets no page open one after an await), then sent to the page once made.
  async function openDiagram(link: HTMLAnchorElement, drawing: SVGElement) {
    const svg = drawing.outerHTML, colors = diagramColors(drawing);
    const known = pages.known(svg, colors);
    if (known) { link.href = known; return; }
    const tab = window.open("about:blank", "_blank");
    if (tab) tab.opener = null;
    const url = await pages.open(svg, colors, window.location.href);
    if (!url) { tab?.close(); options.note("The diagram could not be opened."); return; }
    link.href = url;
    if (tab) tab.location.replace(url);
    else options.note("Tap Open again to see the diagram.");
  }

  // Full screen: the diagram at its own size, which the browser pinches and pans. Back closes it: it has an entry of its own in the history.
  function zoom(svg: string) {
    const view = el(document, "div", "diagram-zoom");
    view.setAttribute("role", "dialog");
    view.setAttribute("aria-modal", "true");
    view.setAttribute("aria-label", "Diagram");
    const close = el(document, "button", "icon-btn diagram-zoom-close");
    close.type = "button";
    close.setAttribute("aria-label", "Close");
    close.innerHTML = iconSvg("close");
    const stage = el(document, "div", "diagram-zoom-stage");
    stage.innerHTML = svg;
    const drawing = stage.querySelector("svg");
    if (drawing) {
      // Its style also holds its colors: only its size changes.
      const width = drawing.width?.baseVal?.value || drawing.viewBox?.baseVal?.width || 0;
      drawing.style.maxWidth = "none";
      if (width) drawing.style.width = `${Math.max(width, window.innerWidth - 24)}px`;
    }
    const back = el(document, "button", "diagram-zoom-back");
    back.type = "button";
    back.setAttribute("aria-label", "Back");
    back.innerHTML = iconSvg("back");
    const logo = el(document, "img");
    logo.src = "/mobile/icon-192.png"; logo.alt = ""; logo.width = logo.height = 28;
    back.append(logo);
    view.append(back, close, stage);
    const before = document.activeElement as HTMLElement | null;
    document.body.append(view);
    window.history.pushState({ diagramZoom: true }, "");
    const gone = () => { view.remove(); window.removeEventListener("popstate", gone); window.removeEventListener("keydown", escape); before?.focus?.(); };
    const escape = (event: KeyboardEvent) => { if (event.key === "Escape") leave(); };
    const leave = () => { if ((window.history.state as { diagramZoom?: boolean } | null)?.diagramZoom) window.history.back(); else gone(); };
    window.addEventListener("popstate", gone);
    window.addEventListener("keydown", escape);
    back.addEventListener("click", leave);
    close.addEventListener("click", leave);
    close.focus();
  }

  /** Handles the taps in the messages of an element: diagrams, their Open link, copy buttons and links. */
  function handle(root: HTMLElement) {
    root.addEventListener("click", event => {
      const target = event.target as Element | null;
      const open = target?.closest<HTMLAnchorElement>("a.diagram-open");
      if (open) {
        if (open.getAttribute("href")) return; // A page already made: the link opens it.
        event.preventDefault();
        const drawing = open.parentElement?.querySelector<SVGElement>(".markdown-diagram svg");
        if (drawing) void openDiagram(open, drawing);
        return;
      }
      if (target && followFootnote(target)) return;
      const figure = target?.closest<HTMLElement>(".markdown-diagram");
      if (figure) { const svg = figure.querySelector("svg")?.outerHTML; if (svg) zoom(svg); return; }
      const copy = target?.closest<HTMLButtonElement>("button.markdown-copy, button.markdown-math-copy");
      if (copy) {
        const code = copy.classList.contains("markdown-math-copy") ? equationCopy(copy) ?? "" : copy.parentElement?.querySelector("code")?.textContent ?? "";
        void window.navigator.clipboard?.writeText(code).then(() => {
          copy.dataset.copied = "";
          window.setTimeout(() => { delete copy.dataset.copied; }, 1500);
        }, () => options.note("The code could not be copied."));
        return;
      }
      const link = target?.closest<HTMLAnchorElement>(".markdown-content a[href]");
      if (link) {
        event.preventDefault();
        if (linkTarget(link.getAttribute("href")) === "web") window.open(link.href, "_blank", "noopener");
      }
    });
    root.addEventListener("keydown", event => {
      const figure = (event.target as Element | null)?.closest?.<HTMLElement>(".markdown-diagram");
      if (figure && (event.key === "Enter" || event.key === " ")) { event.preventDefault(); figure.click(); }
    });
  }

  return { fill, handle };
}

/** The messages of the page. */
export type MarkdownView = ReturnType<typeof createMarkdownView>;
