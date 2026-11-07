import { useEffect, useMemo, useRef, useState, type KeyboardEvent as ReactKeyboardEvent, type ReactNode, type RefObject } from "react";
import { Button, InputGroup } from "@blueprintjs/core";
import { AppIcon, type IconName } from "../AppIcon";
import type { MessageKey } from "../localization";
import { useShellLanguage } from "../shellLanguage";
import { clearFound, defaultFindOptions, findPattern, findPatternInvalid, findRanges, isFindKey, markFound, nearestPlace, noFoundMarks, placeKept, rangeShown, revealRange, samePlace, scrollbarTicks, type FindOptions, type FindTick } from "./findInPage";

const mac = typeof navigator !== "undefined" && navigator.platform.startsWith("Mac");
/** The key that finds in a page, as the window names it. */
export const findKeyLabel = mac ? "Cmd+F" : "Ctrl+F";

/** The search in a rendered page: whether it is open, what opens and closes it, its keys, and its box. */
export type FindInPage = Readonly<{
  finding: boolean;
  /** Whether the page can be searched now. */
  available: boolean;
  open: () => void;
  close: (refocus?: boolean) => void;
  /** Whether an element is in the box of the search. */
  inBar: (target: EventTarget | null) => boolean;
  /**
   * Takes Ctrl+F (Cmd+F on macOS) to open the search, and Escape outside its box to close it; true when it took the
   * key. The element that has these keys tells the window with `data-find-keys`, or Ctrl+F searches the sessions.
   */
  keyDown: (event: ReactKeyboardEvent) => boolean;
  /** The box of the search, rendered over the page by its host, which positions it. */
  bar: ReactNode;
  /** What is looked for while the search is open; null otherwise. A page opens by it what hides a place. */
  pattern: RegExp | null;
}>;

// How many frames a place just gone to is kept in view: the rows that come into view take their real height, and move it.
const revealFrames = 3;
// How long a page that changes is left alone before its places are measured along the scrollbar: the first measure
// lays out the rows not seen yet, which typing does not wait for.
const tickDelay = 120;

// The element that scrolls a place of the page into view: the page itself, or the first of its parents that scrolls.
function scrollerOf(element: HTMLElement): HTMLElement {
  for (let node: HTMLElement | null = element; node; node = node.parentElement) {
    const overflow = getComputedStyle(node).overflowY;
    if ((overflow === "auto" || overflow === "scroll") && node.scrollHeight > node.clientHeight) return node;
  }
  return element.ownerDocument.scrollingElement as HTMLElement ?? element;
}

type TickStrip = Readonly<{ left: number; top: number; width: number; height: number; ticks: readonly FindTick[] }>;

/**
 * Where the places of a page are, along its scrollbar. A row not seen yet has only an estimated height, so the places
 * are measured again when the page changes size and when scrolling lays out rows that change its height.
 */
function useScrollbarTicks(host: HTMLElement | null, places: RefObject<Range[]>, shown: RefObject<number>) {
  const [strip, setStrip] = useState<TickStrip | null>(null);
  const measureSoon = useRef(() => { });
  const measured = useRef<Readonly<{ tops: readonly number[]; contentHeight: number; track: Omit<TickStrip, "ticks"> | null }>>({ tops: [], contentHeight: 0, track: null });
  const drawn = useRef("");

  function draw() {
    const { tops, contentHeight, track } = measured.current;
    const ticks = track ? scrollbarTicks(tops, shown.current, contentHeight, track.height) : [];
    const next = track && ticks.length > 0 ? { ...track, ticks } : null;
    const key = JSON.stringify(next);
    if (key !== drawn.current) { drawn.current = key; setStrip(next); }
  }

  useEffect(() => {
    if (!host) { measured.current = { tops: [], contentHeight: 0, track: null }; draw(); return; }
    let timer: ReturnType<typeof setTimeout> | undefined, watched: HTMLElement | null = null, contentHeight = -1;
    const soon = () => { clearTimeout(timer); timer = setTimeout(measure, tickDelay); };
    const resized = new ResizeObserver(soon);
    function measure() {
      const view = scrollerOf(host!), frame = view.getBoundingClientRect();
      if (view !== watched) { if (watched) resized.unobserve(watched); resized.observe(view); watched = view; }
      contentHeight = view.scrollHeight;
      const origin = frame.top + view.clientTop - view.scrollTop;
      // A native scrollbar cannot be drawn on: the marks are laid over it, in the window.
      const width = view.offsetWidth - view.clientWidth - view.clientLeft * 2;
      measured.current = {
        tops: places.current.map(range => { const box = range.getBoundingClientRect(); return box.height > 0 ? box.top - origin : NaN; }),
        contentHeight,
        track: width > 0 && view.clientHeight > 0 ? { left: frame.left + view.clientLeft + view.clientWidth, top: frame.top + view.clientTop, width, height: view.clientHeight } : null,
      };
      draw();
    }
    const scrolled = (event: Event) => { if (event.target === watched && watched!.scrollHeight !== contentHeight) soon(); };
    const document = host.ownerDocument;
    resized.observe(host);
    document.addEventListener("scroll", scrolled, true);
    measureSoon.current = soon;
    return () => { clearTimeout(timer); resized.disconnect(); document.removeEventListener("scroll", scrolled, true); measureSoon.current = () => { }; };
  }, [host]);

  const element = strip ? <div className="find-in-page-ticks" aria-hidden="true" style={{ left: strip.left, top: strip.top, width: strip.width, height: strip.height }}>
    {strip.ticks.map(tick => <span key={tick.top} data-current={tick.current || undefined} style={{ top: tick.top }} />)}
  </div> : null;
  return { element, measureSoon: () => measureSoon.current(), shownChanged: draw };
}

/**
 * Finds a text in a rendered page, as it is typed: the first place from where the reader is is shown, Enter and
 * Shift+Enter go to the next and the previous one, Escape closes. The places are found again, without moving the
 * page, when the page is drawn again (another file, new colors, what a session writes), and the place shown stays.
 */
export function useFindInPage({ page, available = true, className, returnTo, label = "Find in this page", endFirst = false, refreshDelay = 0, partial = false, extra = null, onMoved }: {
  /** The element whose text is searched; it stays in the document while the search is open. */
  page: () => HTMLElement | null;
  /** Whether the page can be searched now: it is read and shown. The search closes when it no longer can. */
  available?: boolean;
  /** The class of the box, for its host to place it. */
  className?: string;
  /** What takes the keyboard when the search closes; else what had it when it opened, else the page. */
  returnTo?: () => HTMLElement | null;
  /** What the box is called. */
  label?: MessageKey;
  /** With no place after the reader, the nearest one before them is shown, not the first of the page: a timeline is read from its end. */
  endFirst?: boolean;
  /** How long a page that keeps changing is left alone, in milliseconds, before it is searched again. */
  refreshDelay?: number;
  /** Whether the page holds only part of what could be searched: the count says it. */
  partial?: boolean;
  /** What the box shows after the count. */
  extra?: ReactNode;
  /** The search moved the page to show a place. */
  onMoved?: () => void;
}): FindInPage {
  const { t } = useShellLanguage();
  const pageOf = useRef(page), returnOf = useRef(returnTo), movedOf = useRef(onMoved);
  pageOf.current = page;
  returnOf.current = returnTo;
  movedOf.current = onMoved;
  const input = useRef<HTMLInputElement>(null), box = useRef<HTMLDivElement>(null), opener = useRef<HTMLElement | null>(null), revealing = useRef(0);
  const [finding, setFinding] = useState(false);
  const [text, setText] = useState("");
  const [options, setOptions] = useState<FindOptions>(defaultFindOptions);
  const invalid = findPatternInvalid(text, options);
  const [found, setFound] = useState<Readonly<{ index: number; count: number }>>({ index: -1, count: 0 });
  const ranges = useRef<Range[]>([]), shown = useRef(-1), marks = useRef(noFoundMarks());
  const open = finding && available;
  // The page searched: another element (the page drawn again in another place) is searched again.
  const searched = open ? page() : null;
  const ticks = useScrollbarTicks(searched, ranges, shown);

  useEffect(() => {
    const host = searched;
    if (!host) { ranges.current = []; shown.current = -1; clearFound(marks.current); setFound({ index: -1, count: 0 }); return; }
    let frame = 0, timer: ReturnType<typeof setTimeout> | undefined;
    const run = (typed: boolean) => {
      const before = ranges.current[shown.current];
      const next = host.isConnected ? findRanges(host, text, options) : [];
      const view = scrollerOf(host);
      // Text typed, or a page that had nothing and now has a place (a fold that opened for it), goes to the nearest place.
      const going = next.length > 0 && (typed || !before);
      const index = next.length === 0 ? -1 : going ? nearest(next, view, endFirst) : placeKept(next, before, shown.current, samePlace);
      ranges.current = next;
      shown.current = index;
      markFound(marks.current, next, index);
      setFound({ index, count: next.length });
      if (going && !rangeShown(next[index], view)) reveal(next[index], view);
      ticks.measureSoon();
    };
    run(true);
    const again = () => { timer = undefined; frame ||= requestAnimationFrame(() => { frame = 0; run(false); }); };
    const observer = new MutationObserver(() => { if (refreshDelay <= 0) again(); else timer ??= setTimeout(again, refreshDelay); });
    observer.observe(host, { childList: true, subtree: true, characterData: true });
    return () => { observer.disconnect(); cancelAnimationFrame(frame); clearTimeout(timer); };
  }, [searched, text, options]);
  useEffect(() => () => { clearFound(marks.current); cancelAnimationFrame(revealing.current); }, []);
  useEffect(() => { if (!available) setFinding(false); }, [available]);

  function reveal(range: Range, view: HTMLElement) {
    cancelAnimationFrame(revealing.current);
    let frames = revealFrames;
    const place = () => {
      if (ranges.current[shown.current] !== range) return;
      const top = view.scrollTop;
      revealRange(range, view);
      if (view.scrollTop !== top) movedOf.current?.();
      if (--frames > 0) revealing.current = requestAnimationFrame(place);
    };
    place();
  }

  function start() {
    if (!available) return;
    // A few words selected in the page are what is looked for.
    const selection = window.getSelection(), selected = selection?.toString().trim() ?? "", host = pageOf.current();
    if (selected && selected.length <= 200 && !selected.includes("\n") && selection?.anchorNode && host?.contains(selection.anchorNode)) setText(selected);
    if (!box.current?.contains(document.activeElement)) opener.current = document.activeElement instanceof HTMLElement ? document.activeElement : null;
    setFinding(true);
    requestAnimationFrame(() => { input.current?.focus(); input.current?.select(); });
  }
  function close(refocus = true) {
    setFinding(false);
    // The keyboard goes back where it was, and the keys scroll the page again.
    const back = opener.current;
    opener.current = null;
    if (refocus) (returnOf.current?.() ?? (back?.isConnected ? back : pageOf.current()))?.focus({ preventScroll: true });
  }
  function step(by: 1 | -1) {
    const list = ranges.current, host = pageOf.current();
    if (list.length === 0) return;
    const index = (shown.current + by + list.length) % list.length;
    shown.current = index;
    markFound(marks.current, list, index);
    setFound({ index, count: list.length });
    ticks.shownChanged();
    if (host) reveal(list[index], scrollerOf(host));
  }
  const inBar = (target: EventTarget | null) => target instanceof Node && !!box.current?.contains(target);
  function keyDown(event: ReactKeyboardEvent) {
    if (event.defaultPrevented || event.nativeEvent.isComposing) return false;
    if (isFindKey(event, mac) && !event.repeat) {
      if (!available) return false;
      event.preventDefault();
      start();
      return true;
    }
    if (event.key === "Escape" && open && !event.ctrlKey && !event.altKey && !event.metaKey && !event.shiftKey) {
      event.preventDefault();
      close(inBar(event.target));
      return true;
    }
    return false;
  }

  // The options of the search, as the text editor shows them, with its keys in the box: Alt+C, Alt+W, Alt+R.
  const toggles: readonly (readonly [keyof FindOptions, IconName, "Match case" | "Match whole word" | "Use regular expression", string])[] = [
    ["matchCase", "caseSensitive", "Match case", "c"], ["wholeWord", "wholeWord", "Match whole word", "w"], ["regex", "regex", "Use regular expression", "r"]];
  const toggle = (option: keyof FindOptions) => setOptions(current => ({ ...current, [option]: !current[option] }));
  const bar = open ? <><div className={className ? `find-in-page ${className}` : "find-in-page"} role="search" aria-label={t(label)} ref={box}
    onKeyDown={event => {
      if (!event.altKey || event.ctrlKey || event.metaKey || event.shiftKey) return;
      const chosen = toggles.find(entry => entry[3] === event.key.toLowerCase());
      if (!chosen) return;
      event.preventDefault();
      toggle(chosen[0]);
    }}>
    <InputGroup inputRef={input} size="small" className="find-in-page-query" value={text} maxLength={200} autoComplete="off" spellCheck={false}
      placeholder={t(label)} aria-label={t(label)} aria-invalid={invalid || undefined} intent={invalid ? "danger" : undefined}
      title={invalid ? t("The regular expression is not valid.") : undefined} onChange={event => setText(event.target.value)}
      onKeyDown={event => {
        // Enter goes to the next place, Shift+Enter to the one before. A text being composed is left to its input method.
        if (event.key !== "Enter" || event.altKey || event.ctrlKey || event.metaKey || event.nativeEvent.isComposing) return;
        event.preventDefault();
        step(event.shiftKey ? -1 : 1);
      }}
      rightElement={<span className="find-in-page-options">
        {toggles.map(([option, icon, label, key]) => <Button key={option} variant="minimal" size="small" className="find-in-page-option" icon={<AppIcon name={icon} size={15} />}
          active={options[option]} aria-pressed={options[option]} aria-label={t(label)} title={`${t(label)} (Alt+${key.toUpperCase()})`}
          onClick={() => { toggle(option); input.current?.focus(); }} />)}
      </span>} />
    <span className="find-in-page-count" role="status" data-empty={text.trim() && found.count === 0 || undefined}>
      {text.trim() ? t("{index} of {count}", { index: found.index + 1, count: partial ? `${found.count}+` : found.count }) : ""}</span>
    {extra}
    <Button variant="minimal" size="small" icon={<AppIcon name="arrowUp" size={15} />} aria-label={t("Previous match")} title={`${t("Previous match")} (Shift+Enter)`}
      disabled={found.count === 0} onClick={() => step(-1)} />
    <Button variant="minimal" size="small" icon={<AppIcon name="arrowDown" size={15} />} aria-label={t("Next match")} title={`${t("Next match")} (Enter)`}
      disabled={found.count === 0} onClick={() => step(1)} />
    <Button variant="minimal" size="small" icon={<AppIcon name="close" size={15} />} aria-label={t("Close")} title={`${t("Close")} (Esc)`} onClick={() => close()} />
  </div>{ticks.element}</> : null;
  const looked = useMemo(() => findPattern(text, options), [text, options]);
  return { finding: open, available, open: start, close, inBar, keyDown, bar, pattern: open ? looked : null };
}

// The place to go to from where the reader is: the first that ends in view or after it.
function nearest(places: readonly Range[], view: HTMLElement, endFirst: boolean): number {
  return nearestPlace(places, view.getBoundingClientRect().top, endFirst, range => range.getBoundingClientRect().bottom);
}
