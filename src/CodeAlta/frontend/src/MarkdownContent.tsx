import { createContext, useContext, useEffect, useLayoutEffect, useMemo, useRef, useState, useSyncExternalStore, type KeyboardEvent } from "react";
import { showToast } from "./appToaster";
import { diagramAppearance, diagrams } from "./diagrams";
import { translate } from "./localization";
import { createMarkdownRenderer, equationCopy, followFootnote, localImageClass, markdownPending, toggleRenderedSource } from "./markdownBoundary";
import { loadMath } from "./math";
import type { MarkdownImage, MarkdownImages } from "./markdownImages";
import { markdownHrefKind, markdownLinkActivation, maximumMarkdownHrefLength, tooLongMarkdownHref, type MarkdownLinkScope } from "./markdownLinks";
import { appearanceKey, subscribeAppearance } from "./shellColors";
import { useShellLanguage } from "./shellLanguage";
import { inModalDialog, modalDialogOpen } from "./modalDialogs";
import { TimelineImageViewer } from "./TimelineImageStrip";
import { inlineImageSource } from "./timelineImages";
import type { WindowSize } from "./windowGeometry";

/**
 * Opens the links of the Markdown that the window shows: a page of the web in the system browser, a file of this
 * computer in the code editor. Null where nothing opens them (a window without a host): every link stays inert.
 */
export const MarkdownLinksContext = createContext<((address: string, scope: MarkdownLinkScope | null) => void) | null>(null);
/** Where the relative links of the texts below start from; null where a relative path names nothing. */
export const MarkdownLinkScopeContext = createContext<MarkdownLinkScope | null>(null);
/**
 * Reads the pictures of the disk that the Markdown names, for the window to draw them in the text. Null where
 * nothing reads them (a window without a host): such a picture stays a link to its file.
 */
export const MarkdownImagesContext = createContext<MarkdownImages | null>(null);

// A drawn picture opens its window by a click or Enter alone; with a modifier, or by the middle button, its link opens the file.
const opensPicture = (event: MouseEvent | globalThis.KeyboardEvent) => !event.ctrlKey && !event.metaKey && !event.shiftKey
  && (event.type === "click" || event.type === "keydown");

type ViewedPicture = Readonly<{ address: string; title: string; image: MarkdownImage; size: WindowSize | null; origin: HTMLElement }>;

/**
 * A text of Markdown, rendered through the sanitizing boundary. A message breaks its lines where its text does;
 * a `document` (a file, the instructions of a skill) is wrapped in its source, and its lines follow each other.
 */
export function MarkdownContent({ source, timelineCodeBlocks = false, document: asDocument = false, sourceLines = false, onOpenLink }: {
  source: string; timelineCodeBlocks?: boolean; document?: boolean;
  /** Each block says which lines of `source` it comes from, in `data-source-lines` (read with `sourceBlocks` of `markdownSourceLines`). */
  sourceLines?: boolean;
  /** What opens a link, instead of the opener of the window ({@link MarkdownLinksContext}); null leaves every link inert. */
  onOpenLink?: ((address: string) => void) | null;
}) {
  const opener = useContext(MarkdownLinksContext);
  const scope = useContext(MarkdownLinkScopeContext);
  const openLink = onOpenLink !== undefined ? onOpenLink : opener && ((address: string) => opener(address, scope));
  // The renderer is made once for a language: the titles of the alerts are in it.
  const { locale } = useShellLanguage();
  const render = useMemo(() => createMarkdownRenderer(window, { note: translate(locale, "Note"), tip: translate(locale, "Tip"),
    important: translate(locale, "Important"), warning: translate(locale, "Warning"), caution: translate(locale, "Caution"), copy: translate(locale, "Copy"),
    notDrawn: translate(locale, "Not drawn as a diagram:"), copyLatex: translate(locale, "Copy LaTeX"), copyMathMl: translate(locale, "Copy MathML"),
    footnotes: translate(locale, "Footnotes"), footnote: translate(locale, "Footnote"), backToReference: translate(locale, "Back to reference"),
    source: translate(locale, "Source"), preview: translate(locale, "Preview"), showSource: translate(locale, "Show the source or the drawing") }), [locale]);
  // Diagrams are drawn on the side, in the window's font, then found by the next render; they take the colors of the theme from the stylesheet.
  const appearance = useSyncExternalStore(subscribeAppearance, appearanceKey);
  const [drawn, setDrawn] = useState(0);
  const rendered = useMemo(() => {
    const pending = markdownPending();
    return { html: render(source, timelineCodeBlocks, pending, { document: asDocument, sourceLines }), pending };
  }, [render, source, timelineCodeBlocks, asDocument, sourceLines, appearance, drawn]);
  const html = rendered.html;
  useEffect(() => {
    const { diagrams: undrawn, math } = rendered.pending;
    if (!undrawn.length && !math) return;
    let current = true;
    // Formulas wait only for KaTeX, which loads once for the whole window.
    if (math) void loadMath().then(() => { if (current) setDrawn(value => value + 1); }, () => { /* Not loaded: the formulas stay as written. */ });
    // Text still being written changes before this fires, so only settled text is drawn.
    const timer = undrawn.length ? setTimeout(() => {
      const look = diagramAppearance();
      void Promise.all(undrawn.map(text => diagrams.draw(text, look))).then(() => { if (current) setDrawn(value => value + 1); });
    }, 200) : undefined;
    return () => { current = false; clearTimeout(timer); };
  }, [rendered]);
  // React compares this prop by identity. Equivalent persisted refreshes must not
  // replace focused code DOM, its selection or its inner scroll position.
  const codeMarkup = useMemo(() => ({ __html: html }), [html]);
  // The pictures of the disk are drawn in the links the renderer made for them, once read; a text rendered again
  // draws the ones already read before it is painted. A picture that cannot be read stays a link to its file, and
  // a text whose links are inert (the preview of a folded row) draws none.
  const windowImages = useContext(MarkdownImagesContext);
  const images = onOpenLink === null ? null : windowImages;
  const content = useRef<HTMLDivElement>(null);
  const drawnPictures = useRef(new WeakMap<HTMLImageElement, MarkdownImage>());
  const [viewing, setViewing] = useState<ViewedPicture | null>(null);
  useLayoutEffect(() => {
    const element = content.current;
    if (!images || !element) return;
    let current = true;
    for (const picture of Array.from(element.querySelectorAll<HTMLImageElement>(`a[href] > img.${localImageClass}`))) {
      const address = picture.parentElement!.getAttribute("href")!;
      if (markdownHrefKind(address) !== "file") continue;
      const draw = (image: MarkdownImage | null) => {
        if (!current || !image || !picture.isConnected) return;
        drawnPictures.current.set(picture, image);
        // An image the browser cannot decode leaves its link as it was.
        picture.onerror = () => { picture.removeAttribute("src"); drawnPictures.current.delete(picture); };
        picture.src = image.url;
      };
      const known = images.peek(address, scope);
      if (known !== undefined) draw(known);
      else void images.load(address, scope).then(draw, () => { /* Not drawn: the link stays. */ });
    }
    return () => { current = false; };
  }, [codeMarkup, images, scope]);
  function closeViewer() {
    const origin = viewing?.origin;
    setViewing(null);
    if (origin) requestAnimationFrame(() => { if (origin.isConnected && !origin.closest("[inert], [hidden]")) origin.focus(); });
  }

  function scrollCode(event: KeyboardEvent<HTMLDivElement>) {
    if (event.defaultPrevented || event.altKey || event.ctrlKey || event.metaKey || event.shiftKey) return;
    const code = (event.target as Element).closest<HTMLElement>("pre.timeline-code");
    if (!code || !event.currentTarget.contains(code)) return;
    const line = parseFloat(getComputedStyle(code).lineHeight);
    const page = Math.max(line, code.clientHeight - line);
    const maximum = code.scrollHeight - code.clientHeight;
    let top = code.scrollTop;
    switch (event.key) {
      case "ArrowUp": top -= line; break;
      case "ArrowDown": top += line; break;
      case "ArrowLeft": case "ArrowRight": break; // Wrapped code has no horizontal viewport to move.
      case "PageUp": top -= page; break;
      case "PageDown": case " ": top += page; break;
      case "Home": top = 0; break;
      case "End": top = maximum; break;
      default: return;
    }
    // Keyboard navigation belongs to the focused code region even at its boundary.
    // Tab and selection/Copy modifiers retain their native behavior.
    event.preventDefault();
    code.scrollTop = Math.max(0, Math.min(maximum, top));
  }

  function activateLink(event: { target: EventTarget; currentTarget: HTMLDivElement; nativeEvent: MouseEvent | globalThis.KeyboardEvent;
    preventDefault(): void; defaultPrevented: boolean }) {
    const target = event.target instanceof Element ? event.target : null;
    // Enter on the renderer's Copy button must still produce its native click, even inside an authored anchor.
    if (event.nativeEvent.type === "keydown" && target?.closest("button.markdown-copy")) return;
    const link = target?.closest("a");
    if (!link || !event.currentTarget.contains(link)) return;
    const activate = !event.defaultPrevented && markdownLinkActivation(event.nativeEvent);
    event.preventDefault(); // Never navigate the WebView, including ungranted and unsupported gestures.
    // A link of the page under a modal dialog is not followed; one of the dialog is.
    if (!activate || !link.isConnected || link.closest('[inert], [hidden]')
      || target?.closest("button") || modalDialogOpen() && !inModalDialog(link)) return;
    const address = link.getAttribute("href"); // Read the sanitized literal, not the browser-resolved property.
    if (!address) return;
    if (!markdownHrefKind(address)) {
      const length = openLink ? tooLongMarkdownHref(address) : null;
      if (length !== null) showToast({ intent: "warning", icon: "warning-sign", timeout: 12_000,
        message: translate(locale, "This link is too long to open: it has {length} characters, and the limit is {limit}.", { length, limit: maximumMarkdownHrefLength }),
        action: { text: translate(locale, "Copy address"), onClick: () => void navigator.clipboard?.writeText(address).catch(() => { /* The clipboard is unavailable: nothing was copied. */ }) } }, "markdown-link-too-long");
      return;
    }
    const picture = link.querySelector<HTMLImageElement>(`:scope > img.${localImageClass}[src]`);
    const image = picture && drawnPictures.current.get(picture);
    if (picture && image && opensPicture(event.nativeEvent)) {
      setViewing({ address, title: picture.alt || image.path, image, origin: link,
        size: picture.naturalWidth ? { width: picture.naturalWidth, height: picture.naturalHeight } : null });
      return;
    }
    openLink?.(address);
  }
  // The button of a code block copies the text of that block, as it is written, and says so for a moment.
  // The buttons of a displayed equation copy its LaTeX or its MathML.
  function copyCode(event: { target: EventTarget }) {
    const equation = (event.target as Element).closest<HTMLElement>("button.markdown-math-copy");
    const button = equation ?? (event.target as Element).closest<HTMLElement>("button.markdown-copy");
    const code = equation ? equationCopy(equation) : button?.parentElement?.querySelector(":scope > code")?.textContent?.replace(/\n$/, "");
    if (!button || code === null || code === undefined) return;
    void navigator.clipboard?.writeText(code).then(() => {
      button.setAttribute("data-copied", "true");
      window.setTimeout(() => button.removeAttribute("data-copied"), 1400);
    }, () => { /* The clipboard is unavailable: nothing was copied and nothing changes. */ });
  }
  return <><div className="markdown-content" ref={content} onClick={event => {
    const handled = event.defaultPrevented;
    activateLink(event);
    if (!handled && event.target instanceof Element && (followFootnote(event.target) || toggleRenderedSource(event.target))) return;
    if (!handled) copyCode(event); // Our navigation suppression must not swallow Copy inside an anchor.
  }} onAuxClick={activateLink}
    onKeyDown={event => {
      if (event.key === "Enter") activateLink(event);
      if (timelineCodeBlocks) scrollCode(event);
    }} dangerouslySetInnerHTML={codeMarkup} />
    {viewing && <TimelineImageViewer images={[{ index: 0, title: viewing.title, mediaType: null }]}
      source={inlineImageSource(viewing.image.path, [viewing.image])} index={0} initialSize={viewing.size} onIndex={() => { /* One picture. */ }}
      onClose={closeViewer} file={{ path: viewing.image.path, platform: images?.platform ?? null,
        onReveal: images ? () => images.reveal(viewing.address, scope) : undefined }} />}
  </>;
}
