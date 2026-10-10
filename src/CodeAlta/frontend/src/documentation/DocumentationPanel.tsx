import { useCallback, useContext, useEffect, useId, useLayoutEffect, useMemo, useRef, useState, useSyncExternalStore, type KeyboardEvent as ReactKeyboardEvent } from "react";
import { Button, InputGroup, NonIdealState, PopoverNext, SegmentedControl, TextArea } from "@blueprintjs/core";
import type { DocumentationBlock, DocumentationMenuItem, DocumentationSearchHit } from "#neoastra";
import { ActivitySpinner } from "../ActivitySpinner";
import { AppIcon } from "../AppIcon";
import { MarkdownContent, MarkdownLinksContext } from "../MarkdownContent";
import { markdownHrefKind } from "../markdownLinks";
import { useShellLanguage } from "../shellLanguage";
import type { MessageKey } from "../localization";
import { headingAnchors, highlightParts, menuIcon, menuNeighbors, menuNodes, menuOwner, menuTrail, resolveDocumentationLink } from "./documentation";
import type { DocumentationHub, DocumentationImageState } from "./documentationHub";

type OutlineEntry = Readonly<{ anchor: string; text: string; level: 2 | 3 }>;
const maximumQuestionLength = 2000;

/**
 * The Documentation tab: the user guide that ships with the application. The navigation of the guide on the left,
 * the page in the middle, the headings of the page on the right, a search in the text of the guide, and a question
 * for an agent.
 *
 * A page is texts of Markdown, each drawn through the sanitizing boundary of the window, and pictures the tab draws
 * itself. A link to another page of the guide is followed here; a page of the web goes to the system browser; any
 * other link is not followed. The headings get their addresses from their text once the page is drawn.
 */
export function DocumentationPanel({ hub, visible, onActivate, onOpenSession, onProviders, onNotice }: {
  hub: DocumentationHub;
  visible: boolean;
  onActivate: () => void;
  /** Shows the chat a question created. */
  onOpenSession: (sessionId: string) => void;
  /** Opens the settings of the model providers; null where they cannot be opened. */
  onProviders: (() => void) | null;
  /** Says something to the user, in a line. */
  onNotice: (message: string) => void;
}) {
  const { t } = useShellLanguage();
  const view = useSyncExternalStore(hub.subscribe, hub.getSnapshot);
  const openWeb = useContext(MarkdownLinksContext);
  const root = useRef<HTMLDivElement>(null), scroller = useRef<HTMLDivElement>(null), article = useRef<HTMLElement>(null), searchInput = useRef<HTMLInputElement>(null);
  const [query, setQuery] = useState("");
  const [hits, setHits] = useState<readonly DocumentationSearchHit[] | null>(null);
  const [searching, setSearching] = useState(false);
  const [navOpen, setNavOpen] = useState(false);
  const [outline, setOutline] = useState<readonly OutlineEntry[]>([]);
  const [active, setActive] = useState<string | null>(null);
  const [toggled, setToggled] = useState<ReadonlyMap<string, boolean>>(new Map());
  const [zoom, setZoom] = useState<Readonly<{ url: string; alt: string }> | null>(null);
  // The picture that was enlarged takes the keyboard back when the enlarged one is closed.
  const zoomOpener = useRef<HTMLElement | null>(null);
  const openZoom = useCallback((picture: Readonly<{ url: string; alt: string }>) => {
    zoomOpener.current = document.activeElement instanceof HTMLElement ? document.activeElement : null;
    setZoom(picture);
  }, []);
  function closeZoom() {
    setZoom(null);
    const opener = zoomOpener.current;
    zoomOpener.current = null;
    if (opener?.isConnected) opener.focus({ preventScroll: true });
  }
  const location = view.location, page = view.page;
  const current = page?.status === "ready" ? page.path : location?.page ?? null;

  // The guide is asked for when the tab comes to the screen: a hidden tab reads nothing.
  useEffect(() => { if (visible) hub.show(); }, [hub, visible]);

  const pages = useRef(view.pages); pages.current = view.pages;
  const notice = useRef(onNotice); notice.current = onNotice;
  const outside = t("This link leads outside the guide.");
  const followLink = useCallback((address: string) => {
    if (markdownHrefKind(address) === "web") { openWeb?.(address, null); return; }
    const target = resolveDocumentationLink(address, pages.current);
    if (target) hub.go(target.page, target.anchor); else notice.current(outside);
  }, [hub, openWeb, outside]);

  // The headings of the page get their addresses, and the outline its entries, each time the page is drawn: the
  // Markdown boundary draws again when the window changes its colors, and keeps no attribute of the page.
  useLayoutEffect(() => {
    const host = article.current;
    if (!host) { setOutline([]); return; }
    const tag = () => {
      const headings = Array.from(host.querySelectorAll<HTMLElement>(".documentation-text :is(h1, h2, h3, h4, h5, h6)"));
      const anchors = headingAnchors(headings.map(heading => heading.textContent ?? ""));
      const entries: OutlineEntry[] = [];
      headings.forEach((heading, index) => {
        if (!anchors[index]) return;
        if (heading.getAttribute("data-doc-anchor") !== anchors[index]) heading.setAttribute("data-doc-anchor", anchors[index]);
        if (heading.tagName === "H2" || heading.tagName === "H3") entries.push({ anchor: anchors[index], text: (heading.textContent ?? "").trim(), level: heading.tagName === "H2" ? 2 : 3 });
      });
      setOutline(previous => previous.length === entries.length && previous.every((entry, index) => entry.anchor === entries[index].anchor && entry.text === entries[index].text) ? previous : entries);
    };
    tag();
    const observer = new MutationObserver(tag);
    observer.observe(host, { childList: true, subtree: true });
    return () => observer.disconnect();
  }, [page]);

  const headingTop = useCallback((anchor: string) => {
    const host = article.current, view = scroller.current;
    const heading = host?.querySelector<HTMLElement>(`[data-doc-anchor="${CSS.escape(anchor)}"]`);
    return heading && view ? heading.getBoundingClientRect().top - view.getBoundingClientRect().top + view.scrollTop : null;
  }, []);

  // Where the page is read from: the heading that was asked for, where the reader was (going back, or opening the
  // tab again), or the top.
  const serial = location?.serial ?? 0, anchor = location?.anchor ?? null, comingBack = location?.restore ?? false;
  const readyPath = page?.status === "ready" ? page.path : null;
  // The page that is drawn is the one the tab went to: a place is shown once its own page is there.
  const arrived = readyPath !== null && location !== null && readyPath.toLowerCase() === location.page.toLowerCase();
  const placed = useRef<number | null>(null);
  // The place the page is to be shown at, until it is shown there. The window draws the content of a tab before the
  // tab is in its pane, and a hidden tab has no size either: a page without a size cannot be scrolled, and no
  // heading of it has a position. The place waits for the page to have a size.
  const waiting = useRef<Readonly<{ path: string; anchor: string | null; restore: boolean }> | null>(null);
  const place = useCallback(() => {
    const view = scroller.current, wanted = waiting.current;
    if (!view || !wanted || view.clientHeight === 0) return;
    waiting.current = null;
    const top = wanted.anchor ? headingTop(wanted.anchor) : null;
    view.scrollTop = wanted.restore && hub.scroll.has(wanted.path) ? hub.scroll.get(wanted.path) : top !== null ? Math.max(0, top - 12) : 0;
  }, [hub, headingTop]);
  useLayoutEffect(() => {
    const view = scroller.current;
    if (!view || !readyPath || !arrived || placed.current === serial) return;
    const reopened = hub.scroll.shown() === serial;
    placed.current = serial;
    hub.scroll.show(serial);
    waiting.current = { path: readyPath, anchor, restore: reopened || comingBack };
    place();
    if (!reopened) setActive(anchor);
    // A link of the page that was followed is gone with the page: the keys go on scrolling the new one.
    const focused = document.activeElement;
    if (!reopened && (!focused || focused === document.body || article.current?.contains(focused))) view.focus({ preventScroll: true });
  }, [hub, readyPath, arrived, serial, anchor, comingBack, place]);
  // The page gets its size when its tab is put in its pane, or shown again: the place that waited is shown then, before the page is painted.
  useEffect(() => {
    const view = scroller.current;
    if (!view || typeof ResizeObserver === "undefined") return;
    const observer = new ResizeObserver(() => place());
    observer.observe(view);
    return () => observer.disconnect();
  }, [place]);

  // The entry of the outline the reader is at, and how far the page was scrolled.
  const frame = useRef(0);
  const follow = () => {
    if (frame.current) return;
    frame.current = requestAnimationFrame(() => {
      frame.current = 0;
      const view = scroller.current;
      // A page that is not placed yet, or has no size, was not scrolled by its reader: where it was read stays as it was kept.
      if (!view || !readyPath || waiting.current || view.clientHeight === 0) return;
      hub.scroll.set(readyPath, view.scrollTop);
      let at: string | null = null;
      for (const entry of outline) {
        const top = headingTop(entry.anchor);
        if (top === null || top - view.scrollTop > 40) break;
        at = entry.anchor;
      }
      setActive(at);
    });
  };
  useEffect(() => () => { if (frame.current) cancelAnimationFrame(frame.current); }, []);

  // The search waits for the typing to pause, and shows only the answer to what is typed now.
  useEffect(() => {
    const text = query.trim();
    if (text.length < 2) { setHits(null); setSearching(false); return; }
    setSearching(true);
    let latest = true;
    const timer = setTimeout(() => {
      void hub.search(text).then(found => { if (latest && found) { setHits(found); setSearching(false); } });
    }, 220);
    return () => { latest = false; clearTimeout(timer); };
  }, [hub, query]);

  const nodes = useMemo(() => menuNodes(view.items), [view.items]);
  const owner = menuOwner(view.items, current);
  const trail = menuTrail(view.items, current);
  const neighbors = menuNeighbors(view.items, current);
  const expanded = (path: string) => toggled.get(path) ?? owner === path;
  // The entry the Tab key stops at: the page that is shown when the navigation lists it, else its folder, else the first entry.
  const listed = nodes.flatMap(node => [node.item.path, ...(node.children.length > 0 && expanded(node.item.path) ? node.children.map(child => child.path) : [])]);
  const tabStop = current && listed.includes(current) ? current : owner && listed.includes(owner) ? owner : listed[0] ?? null;
  const titleHits = useMemo(() => {
    const text = query.trim().toLowerCase();
    return text.length < 2 ? [] : view.pages.filter(known => known.title.toLowerCase().includes(text)).slice(0, 8);
  }, [query, view.pages]);

  function go(path: string, heading: string | null = null) {
    setNavOpen(false);
    hub.go(path, heading);
  }
  function openHit(hit: DocumentationSearchHit) {
    // The heading of a place is named by its text: its address is the one the page gives it once it is drawn.
    go(hit.path, hit.heading ? headingAnchors([hit.heading])[0] || null : null);
  }
  function toOutline(entry: OutlineEntry) {
    if (current) hub.go(current, entry.anchor);
  }

  // The arrows move among the entries of the navigation, which is one stop of the Tab key.
  function navKeys(event: ReactKeyboardEvent<HTMLElement>) {
    if (event.altKey || event.ctrlKey || event.metaKey) return;
    const items = Array.from(event.currentTarget.querySelectorAll<HTMLElement>("[data-doc-nav]"));
    const at = items.indexOf(document.activeElement as HTMLElement);
    if (at < 0) return;
    const folder = items[at].getAttribute("data-doc-folder");
    let next = at;
    switch (event.key) {
      case "ArrowDown": next = Math.min(items.length - 1, at + 1); break;
      case "ArrowUp": next = Math.max(0, at - 1); break;
      case "Home": next = 0; break;
      case "End": next = items.length - 1; break;
      case "ArrowRight": if (folder && !expanded(folder)) setToggled(value => new Map(value).set(folder, true)); else return; break;
      case "ArrowLeft": if (folder && expanded(folder)) setToggled(value => new Map(value).set(folder, false)); else return; break;
      default: return;
    }
    event.preventDefault();
    if (next !== at) items[next].focus();
  }

  function keys(event: ReactKeyboardEvent<HTMLDivElement>) {
    if (event.defaultPrevented) return;
    if (event.key === "Escape") {
      if (zoom) { event.preventDefault(); closeZoom(); }
      else if (navOpen) { event.preventDefault(); setNavOpen(false); }
      else if (query && event.target === searchInput.current) { event.preventDefault(); setQuery(""); }
      return;
    }
    if (event.altKey && !event.ctrlKey && !event.metaKey && !event.shiftKey && (event.key === "ArrowLeft" || event.key === "ArrowRight")) {
      event.preventDefault();
      if (event.key === "ArrowLeft") hub.back(); else hub.forward();
      return;
    }
    const target = event.target as HTMLElement;
    if (event.key === "/" && !event.altKey && !event.ctrlKey && !event.metaKey && !target.closest("input, textarea, select, [contenteditable]")) {
      event.preventDefault();
      setNavOpen(true);
      requestAnimationFrame(() => { searchInput.current?.focus(); searchInput.current?.select(); });
    }
  }

  const navigation = <nav className="documentation-menu" aria-label={t("Pages of the guide")} onKeyDown={navKeys}>
    <ul>
      {nodes.map(node => {
        const open = node.children.length > 0 && expanded(node.item.path);
        const focusable = tabStop === node.item.path;
        return <li key={node.item.path}>
          <div className="documentation-menu-row" data-current={current === node.item.path || undefined} data-within={owner === node.item.path && current !== node.item.path || undefined}>
            <button type="button" data-doc-nav data-doc-folder={node.children.length > 0 ? node.item.path : undefined} tabIndex={focusable ? 0 : -1}
              aria-current={current === node.item.path ? "page" : undefined} aria-expanded={node.children.length > 0 ? open : undefined}
              onClick={() => { if (node.children.length > 0) setToggled(value => new Map(value).set(node.item.path, true)); go(node.item.path); }}>
              <AppIcon name={menuIcon(node.item.icon)} size={15} /><span>{node.item.title}</span>
            </button>
            {node.children.length > 0 && <Button variant="minimal" size="small" className="documentation-menu-toggle" tabIndex={-1} icon={<AppIcon name={open ? "chevronDown" : "chevronRight"} size={14} />}
              aria-label={t(open ? "Hide the pages of {title}" : "Show the pages of {title}", { title: node.item.title })}
              onClick={() => setToggled(value => new Map(value).set(node.item.path, !open))} />}
          </div>
          {open && <ul>
            {node.children.map(child => <li key={child.path}>
              <div className="documentation-menu-row" data-current={current === child.path || undefined}>
                <button type="button" data-doc-nav tabIndex={tabStop === child.path ? 0 : -1} aria-current={current === child.path ? "page" : undefined} onClick={() => go(child.path)}>
                  <span>{child.title}</span>
                </button>
              </div>
            </li>)}
          </ul>}
        </li>;
      })}
    </ul>
  </nav>;

  const text = query.trim();
  const results = text.length >= 2 && <div className="documentation-results" role="region" aria-label={t("Search results")} aria-busy={searching || undefined}>
    {titleHits.length > 0 && <ul>
      {titleHits.map(known => <li key={`page:${known.path}`}>
        <button type="button" className="documentation-result" onClick={() => go(known.path)}>
          <strong><Highlight line={known.title} query={text} /></strong>
        </button>
      </li>)}
    </ul>}
    {hits && hits.length > 0 && <ul>
      {hits.map((hit, index) => <li key={`${hit.path}:${index}`}>
        <button type="button" className="documentation-result" onClick={() => openHit(hit)}>
          <strong>{hit.title}{hit.heading && hit.heading !== hit.title && <span> › {hit.heading}</span>}</strong>
          <span><Highlight line={hit.text} query={text} /></span>
        </button>
      </li>)}
    </ul>}
    {searching && !hits && <p className="documentation-results-state"><ActivitySpinner size={13} label={t("Searching…")} /></p>}
    {!searching && hits && hits.length === 0 && titleHits.length === 0 && <p className="documentation-results-state" role="status">{t("No result for “{text}”.", { text })}</p>}
  </div>;

  let body;
  if (view.status === "unavailable") body = <Problem icon="documentation" title={t("This application ships no user guide.")} />;
  else if (view.status === "failed") body = <Problem icon="warning" title={t("The guide could not be read.")} action={t("Try again")} onAction={() => hub.retry()} />;
  else if (!page || page.status === "loading" || view.status !== "ready") body = <div className="documentation-loading"><ActivitySpinner size={18} label={t("Loading…")} /></div>;
  else if (page.status === "not_found") body = <Problem icon="fileText" title={t("This page is not in the guide.")} action={view.home ? t("Open the first page") : undefined} onAction={() => { if (view.home) go(view.home); }} />;
  else if (page.status === "failed") body = <Problem icon="warning" title={t("The page could not be read.")} action={t("Try again")} onAction={() => hub.retry()} />;
  else body = <>
    <article className="documentation-article" ref={article} key={page.path}>
      {page.blocks.map((block, index) => block.kind === "figure"
        ? <DocumentationFigure key={index} hub={hub} block={block} onOpenLink={followLink} onZoom={openZoom} />
        : <div className="documentation-text" key={index}><MarkdownContent source={block.markdown ?? ""} document onOpenLink={followLink} /></div>)}
      {(neighbors.previous || neighbors.next) && <footer className="documentation-pager">
        {neighbors.previous ? <PagerLink item={neighbors.previous} label={t("Previous page")} direction="previous" onGo={go} /> : <span />}
        {neighbors.next ? <PagerLink item={neighbors.next} label={t("Next page")} direction="next" onGo={go} /> : <span />}
      </footer>}
    </article>
    {outline.length > 1 && <aside className="documentation-outline" aria-label={t("On this page")}>
      <h2>{t("On this page")}</h2>
      <ul>
        {outline.map(entry => <li key={entry.anchor} data-level={entry.level} data-active={active === entry.anchor || undefined}>
          <button type="button" aria-current={active === entry.anchor ? "location" : undefined} onClick={() => toOutline(entry)}>{entry.text}</button>
        </li>)}
      </ul>
    </aside>}
  </>;

  return <div className="documentation" ref={root} data-visible={visible || undefined} data-nav-open={navOpen || undefined} onPointerDown={onActivate} onKeyDown={keys}>
    <div className="documentation-frame">
    <aside className="documentation-nav">
      <InputGroup className="documentation-search" inputRef={searchInput} type="search" size="small" leftIcon="search" value={query} maxLength={100} autoComplete="off" spellCheck={false}
        placeholder={t("Search the guide")} aria-label={t("Search the guide")} onChange={event => setQuery(event.target.value)}
        rightElement={query ? <Button variant="minimal" size="small" icon="cross" aria-label={t("Clear the search")} onClick={() => { setQuery(""); searchInput.current?.focus(); }} /> : undefined} />
      {results || navigation}
    </aside>
    {navOpen && <div className="documentation-nav-scrim" aria-hidden="true" onClick={() => setNavOpen(false)} />}
    <section className="documentation-main">
      <header className="documentation-bar">
        <Button variant="minimal" size="small" className="documentation-nav-toggle" icon={<AppIcon name="sidebar" size={16} />} aria-label={t("Pages")} title={t("Pages")}
          aria-expanded={navOpen} onClick={() => setNavOpen(value => !value)} />
        <Button variant="minimal" size="small" icon={<AppIcon name="arrowLeft" size={16} />} disabled={!view.canBack} aria-label={t("Go back")} title={`${t("Go back")} (Alt+←)`} onClick={() => hub.back()} />
        <Button variant="minimal" size="small" icon={<AppIcon name="arrowRight" size={16} />} disabled={!view.canForward} aria-label={t("Go forward")} title={`${t("Go forward")} (Alt+→)`} onClick={() => hub.forward()} />
        <ol className="documentation-trail" aria-label={t("Documentation")}>
          {trail.length === 0 && page?.title && <li aria-current="page">{page.title}</li>}
          {trail.map((item, index) => index === trail.length - 1
            ? <li key={item.path} aria-current="page">{item.title}</li>
            : <li key={item.path}><button type="button" onClick={() => go(item.path)}>{item.title}</button></li>)}
        </ol>
        {view.canAsk && <AskAgent hub={hub} page={readyPath} onOpenSession={onOpenSession} onProviders={onProviders} />}
      </header>
      <div className="documentation-scroll" ref={scroller} tabIndex={-1} onScroll={follow}>{body}</div>
    </section>
    </div>
    {zoom && <div className="documentation-zoom" role="dialog" aria-modal="true" aria-label={zoom.alt} tabIndex={-1} ref={node => node?.focus()} onClick={closeZoom}>
      <img src={zoom.url} alt={zoom.alt} />
      <Button className="documentation-zoom-close" variant="minimal" icon="cross" aria-label={t("Close")} onClick={closeZoom} />
    </div>}
  </div>;
}

function Problem({ icon, title, action, onAction }: { icon: "documentation" | "warning" | "fileText"; title: string; action?: string; onAction?: () => void }) {
  return <NonIdealState className="documentation-problem" icon={<AppIcon name={icon} size={36} />} title={title}
    action={action && onAction ? <Button onClick={onAction}>{action}</Button> : undefined} />;
}

function Highlight({ line, query }: { line: string; query: string }) {
  return <>{highlightParts(line, query).map((part, index) => part.match ? <mark key={index}>{part.text}</mark> : part.text)}</>;
}

function PagerLink({ item, label, direction, onGo }: { item: DocumentationMenuItem; label: string; direction: "previous" | "next"; onGo: (path: string) => void }) {
  return <button type="button" className="documentation-pager-link" data-direction={direction} onClick={() => onGo(item.path)}>
    <AppIcon name={direction === "previous" ? "arrowLeft" : "arrowRight"} size={15} />
    <span><small>{label}</small><strong>{item.title}</strong></span>
  </button>;
}

// The shape of a drawing, from its viewBox: the place of the picture is kept before it is drawn.
function drawingRatio(svg: string): string | undefined {
  const box = /\bviewBox\s*=\s*"\s*[-\d.]+[\s,]+[-\d.]+[\s,]+([\d.]+)[\s,]+([\d.]+)\s*"/i.exec(svg);
  const width = box ? Number(box[1]) : 0, height = box ? Number(box[2]) : 0;
  return width > 0 && height > 0 ? `${width} / ${height}` : undefined;
}

/**
 * A picture of a page. The file of a picture is read when the figure comes near the screen; a drawing of the page
 * is shown as an image, never as markup of the window, so nothing in it runs or loads anything.
 */
function DocumentationFigure({ hub, block, onOpenLink, onZoom }: {
  hub: DocumentationHub; block: DocumentationBlock; onOpenLink: (address: string) => void; onZoom: (picture: { url: string; alt: string }) => void;
}) {
  const { t } = useShellLanguage();
  const name = block.image;
  const [state, setState] = useState<DocumentationImageState | undefined>(() => name ? hub.images.peek(name) : undefined);
  const frame = useRef<HTMLDivElement>(null);
  useEffect(() => {
    if (!name) return;
    const known = hub.images.peek(name);
    setState(known);
    if (known) return;
    let latest = true;
    const load = () => { void hub.images.load(name).then(result => { if (latest) setState(result); }); };
    const node = frame.current;
    if (!node || typeof IntersectionObserver === "undefined") { load(); return () => { latest = false; }; }
    // The page scrolls in its own box: the margin is around that box, so a picture is read a little before it is seen.
    const observer = new IntersectionObserver(entries => {
      if (entries.some(entry => entry.isIntersecting)) { observer.disconnect(); load(); }
    }, { root: node.closest(".documentation-scroll"), rootMargin: "800px 0px" });
    observer.observe(node);
    return () => { latest = false; observer.disconnect(); };
  }, [hub, name]);
  const drawing = !name && block.svg ? block.svg : null;
  const url = useMemo(() => drawing ? `data:image/svg+xml;charset=utf-8,${encodeURIComponent(drawing)}` : null, [drawing]);
  const shown = name ? state?.status === "ready" ? state.url : null : url;
  const alt = block.alt ?? "";
  const ratio = block.width && block.height ? `${block.width} / ${block.height}` : drawing ? drawingRatio(drawing) : undefined;
  return <figure className="documentation-figure" data-state={shown ? "ready" : state?.status === "failed" ? "failed" : "loading"}>
    <div className="documentation-figure-frame" ref={frame} style={{ aspectRatio: ratio, maxWidth: block.width ? `${block.width}px` : undefined }}>
      {shown
        ? <button type="button" className="documentation-figure-zoom" aria-label={t("Enlarge the picture")} title={t("Enlarge the picture")} onClick={() => onZoom({ url: shown, alt })}>
            <img src={shown} alt={alt} width={block.width ?? undefined} height={block.height ?? undefined} decoding="async" draggable={false} />
          </button>
        : state?.status === "failed" && <span className="documentation-figure-missing" role="img" aria-label={alt}><AppIcon name="imageOff" size={18} />{t("Picture unavailable")}</span>}
    </div>
    {block.caption && <figcaption><MarkdownContent source={block.caption} document onOpenLink={onOpenLink} /></figcaption>}
  </figure>;
}

const askProblems: Readonly<Record<string, MessageKey>> = Object.freeze({
  no_provider: "No model provider is enabled.",
  busy: "CodeAlta is busy. Try again in a moment.",
  closing: "CodeAlta is closing.",
  not_sent: "The chat was created, but the question was not sent.",
});

/**
 * A question for an agent about the page the tab shows, or about the whole guide. The host creates a chat and sends
 * it the question; the window then shows that chat. Nothing is asked until the user sends the question.
 */
function AskAgent({ hub, page, onOpenSession, onProviders }: {
  hub: DocumentationHub; page: string | null; onOpenSession: (sessionId: string) => void; onProviders: (() => void) | null;
}) {
  const { t } = useShellLanguage();
  const id = useId();
  const [open, setOpen] = useState(false);
  const [question, setQuestion] = useState("");
  const [scope, setScope] = useState<"page" | "guide">("page");
  const [busy, setBusy] = useState(false);
  const [problem, setProblem] = useState<Readonly<{ status: string; detail: string | null }> | null>(null);
  const field = useRef<HTMLTextAreaElement>(null);
  const mounted = useRef(true);
  useEffect(() => { mounted.current = true; return () => { mounted.current = false; }; }, []);
  useEffect(() => {
    if (!open) return;
    const timer = setTimeout(() => field.current?.focus());
    return () => clearTimeout(timer);
  }, [open]);
  const about = page && scope === "page" ? page : null;
  const text = question.trim();

  async function send() {
    if (!text || busy) return;
    setBusy(true);
    setProblem(null);
    const result = await hub.ask(text, about);
    // The chat exists: it is shown, whatever became of this form meanwhile.
    if (result.sessionId) onOpenSession(result.sessionId);
    if (!mounted.current) return;
    setBusy(false);
    if (result.status === "ok") { setQuestion(""); setOpen(false); }
    else setProblem({ status: result.status, detail: result.status === "failed" ? result.problem : null });
  }

  const form = <form className="documentation-ask" onSubmit={event => { event.preventDefault(); void send(); }}>
    <TextArea id={id} inputRef={field} fill rows={3} maxLength={maximumQuestionLength} value={question} disabled={busy} spellCheck
      aria-label={t("Question")} placeholder={t(about ? "Ask about this page…" : "Ask about the guide…")}
      onChange={event => { setQuestion(event.target.value); if (problem) setProblem(null); }}
      onKeyDown={event => {
        // Enter sends; Shift+Enter starts a line. A text being composed is left to its input method.
        if (event.key === "Enter" && !event.shiftKey && !event.altKey && !event.nativeEvent.isComposing) { event.preventDefault(); void send(); }
      }} />
    {problem && <p className="documentation-ask-problem" role="alert">
      <span>{problem.detail ?? t(askProblems[problem.status] ?? "The question could not be asked.")}</span>
      {problem.status === "no_provider" && onProviders && <Button variant="minimal" size="small" intent="primary" onClick={() => { setOpen(false); onProviders(); }}>{t("Open model providers")}</Button>}
    </p>}
    <div className="documentation-ask-actions">
      {page && <SegmentedControl size="small" aria-label={t("What the question is about")} value={scope} disabled={busy} onValueChange={value => setScope(value as "page" | "guide")}
        options={[{ label: t("This page"), value: "page" }, { label: t("Whole guide"), value: "guide" }]} />}
      <Button type="submit" intent="primary" size="small" icon={<AppIcon name="send" size={14} />} text={t("Send")} loading={busy} disabled={!text} />
    </div>
  </form>;
  return <PopoverNext isOpen={open} content={form} placement="bottom-end" popoverClassName="documentation-ask-popover" canEscapeKeyClose={!busy} autoFocus={false} enforceFocus={false}
    onInteraction={next => { if (next) setOpen(true); else if (!busy) setOpen(false); }}>
    <Button className="documentation-ask-button" size="small" intent="primary" variant="outlined" icon={<AppIcon name="ask" size={15} />} text={t("Ask an agent")} aria-haspopup="dialog" aria-expanded={open} />
  </PopoverNext>;
}
