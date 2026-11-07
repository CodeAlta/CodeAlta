import { clearFound, defaultFindOptions, findPattern, findPatternInvalid, findRanges, markFound, nearestPlace, noFoundMarks, placeKept, revealRange, samePlace, type FindOptions, type FindScope, type SeenBand } from "../findInPage/findInPage";
import { el } from "./dom";
import { timelineIcon, type TimelineIconName } from "./timelineIcons";

// How long typing is waited for before the page is searched.
const typingDelay = 150;
// How many frames a place just gone to is kept in view: what comes into view (a fold that opened) can move it.
const revealFrames = 2;

/** What the count of a search says: "3 of 12", "3 of 12+" while the page holds only a part of what can be searched, nothing before a text is typed. */
export function findCount(index: number, count: number, partial: boolean, typed: boolean): string {
  return typed ? `${index + 1} of ${count}${partial ? "+" : ""}` : "";
}

/** What a find bar searches, and how the page shows a place. */
export type FindBarOptions = Readonly<{
  window: Window & typeof globalThis;
  /** What the bar is called: the name of its box, and what the empty box says. */
  label: string;
  /** The key of its entry in the history, where it keeps the entries under it: Back closes the bar. */
  historyKey: string;
  className?: string;
  /** The element whose text is searched. */
  page: () => HTMLElement | null;
  /** The element that scrolls a place into view. */
  scroller: () => HTMLElement;
  /** The part of the window where a place is seen: under the bar, above the keyboard. */
  seen: (bar: HTMLElement) => SeenBand;
  /** What a place can be in, and what is not looked at. */
  scope?: FindScope;
  /** With no place after the reader, the nearest one before them is shown, not the first: a timeline is read from its end. */
  endFirst?: boolean;
  /** How long a page that keeps changing is left alone, in milliseconds, before it is searched again. */
  refreshDelay?: number;
  /** Whether the page holds only a part of what can be searched: the count says "n+". */
  partial?: () => boolean;
  /** The button that reads more of what can be searched, while the page holds only a part of it and more can be read. */
  more?: Readonly<{ label: string; busy: string; available: () => boolean; run: () => Promise<void> }>;
  /** What is looked for changed, before the page is searched with it: null once nothing is. */
  onPattern?: (pattern: RegExp | null, text: string) => void;
  onOpen?: () => void;
  onClose?: () => void;
}>;

/**
 * The search of a text in what a page of the phone shows, as the desktop finds in a rendered page and with its engine: the
 * places found are marked without changing the page, the count says where the place shown is, the arrows go to the next
 * and the previous place (after the last, the first) without opening the keyboard, and the Search key of the keyboard goes
 * to the place nearest to the reader and closes the keyboard. Match case, Whole word and Regex are in the menu of "Aa".
 * The page is searched again, keeping the place shown, while it changes. Back closes the bar: it has an entry of its own in
 * the history, as the viewers do.
 */
export function createFindBar(options: FindBarOptions) {
  const { window, historyKey } = options, document = window.document;
  const bar = el(document, "div", `find-bar${options.className ? ` ${options.className}` : ""}`);
  bar.setAttribute("role", "search");
  bar.setAttribute("aria-label", options.label);
  bar.hidden = true;
  const row = el(document, "div", "find-bar-row");
  const input = el(document, "input", "find-bar-query");
  input.type = "search";
  input.enterKeyHint = "search";
  input.placeholder = options.label;
  input.maxLength = 200;
  input.autocomplete = "off";
  input.spellcheck = false;
  input.setAttribute("autocapitalize", "off");
  input.setAttribute("autocorrect", "off");
  input.setAttribute("aria-label", options.label);
  const count = el(document, "span", "find-bar-count");
  count.setAttribute("role", "status");
  count.setAttribute("aria-live", "polite");
  const button = (className: string, label: string, name?: TimelineIconName) => {
    const item = el(document, "button", `icon-btn ${className}`);
    item.type = "button";
    item.setAttribute("aria-label", label);
    item.title = label;
    if (name) item.append(timelineIcon(document, name));
    return item;
  };
  const optionsButton = button("find-bar-options", "Search options");
  optionsButton.append(el(document, "span", null, "Aa"));
  optionsButton.firstElementChild!.setAttribute("aria-hidden", "true");
  optionsButton.setAttribute("aria-haspopup", "menu");
  optionsButton.setAttribute("aria-expanded", "false");
  const previous = button("find-bar-previous", "Previous match", "up"), next = button("find-bar-next", "Next match", "down");
  const close = button("find-bar-close", "Close the search", "close");
  const menu = el(document, "div", "find-bar-menu");
  menu.setAttribute("role", "menu");
  menu.setAttribute("aria-label", "Search options");
  menu.hidden = true;
  const toggles: readonly (readonly [keyof FindOptions, string])[] = [["matchCase", "Match case"], ["wholeWord", "Match whole word"], ["regex", "Use regular expression"]];
  const items = toggles.map(([option, label]) => {
    const item = el(document, "button", "find-bar-toggle");
    item.type = "button";
    item.setAttribute("role", "menuitemcheckbox");
    item.setAttribute("aria-checked", "false");
    item.dataset.option = option;
    item.append(timelineIcon(document, "check"), el(document, "span", null, label));
    menu.append(item);
    return item;
  });
  row.append(input, count, optionsButton, previous, next, close, menu);
  const more = el(document, "button", "find-bar-more");
  more.type = "button";
  more.hidden = true;
  bar.append(row, more);

  let open = false, text = "", findOptions: FindOptions = defaultFindOptions, composing = false, reading = false;
  let ranges: Range[] = [], shown = -1, looked: string | null = null;
  let typingTimer = 0, refreshTimer = 0, frame = 0, revealing = 0;
  let watched: HTMLElement | null = null;
  const marks = noFoundMarks();
  const observer = new window.MutationObserver(() => {
    if ((options.refreshDelay ?? 0) <= 0) again();
    else refreshTimer ||= window.setTimeout(() => { refreshTimer = 0; again(); }, options.refreshDelay);
  });
  const again = () => { frame ||= window.requestAnimationFrame(() => { frame = 0; if (open) search(false); }); };
  const bottomOf = (range: Range) => range.getBoundingClientRect().bottom;

  function show() {
    const typed = text.trim().length > 0, partial = options.partial?.() ?? false;
    count.textContent = findCount(shown, ranges.length, partial, typed);
    if (typed && ranges.length === 0) count.dataset.empty = "true"; else delete count.dataset.empty;
    if (findPatternInvalid(text, findOptions)) input.setAttribute("aria-invalid", "true"); else input.removeAttribute("aria-invalid");
    previous.disabled = next.disabled = ranges.length === 0;
    more.hidden = !options.more || !typed || !partial || !options.more.available();
    more.disabled = reading;
    if (options.more) more.textContent = reading ? options.more.busy : options.more.label;
  }

  // Searches the page: `going` goes to the place nearest to the reader (a text typed), else the place shown stays.
  function search(going: boolean) {
    const pattern = findPattern(text, findOptions);
    const key = pattern ? `${pattern.flags}/${pattern.source}` : null;
    if (key !== looked) { looked = key; options.onPattern?.(pattern, text); }
    const page = options.page();
    if (page !== watched) {
      observer.disconnect();
      watched = page;
      if (page) observer.observe(page, { childList: true, subtree: true, characterData: true });
    }
    const before = ranges[shown];
    const found = page && pattern ? findRanges(page, text, findOptions, options.scope) : [];
    // A page that had nothing and now has a place (older messages read, a fold that opened) goes to it too.
    const go = found.length > 0 && (going || !before);
    const index = go ? nearestPlace(found, options.seen(bar).top, !!options.endFirst, bottomOf) : placeKept(found, before, shown, samePlace);
    ranges = found;
    shown = index;
    markFound(marks, found, index);
    show();
    if (go) reveal(found[index]);
    // What the page drew for this search (the folds it opened) is not a change to search again for.
    observer.takeRecords();
  }

  function reveal(range: Range) {
    window.cancelAnimationFrame(revealing);
    let frames = revealFrames;
    const place = () => {
      if (ranges[shown] !== range || !range.startContainer.isConnected) return;
      revealRange(range, options.scroller(), options.seen(bar));
      if (--frames > 0) revealing = window.requestAnimationFrame(place);
    };
    place();
  }

  function step(by: 1 | -1) {
    if (ranges.length === 0) return;
    shown = (shown + by + ranges.length) % ranges.length;
    markFound(marks, ranges, shown);
    show();
    reveal(ranges[shown]);
  }

  // What is typed is searched once typing pauses; a text an input method is composing, once it is composed.
  function typed() {
    window.clearTimeout(typingTimer);
    typingTimer = window.setTimeout(take, typingDelay);
  }
  function take(): boolean {
    window.clearTimeout(typingTimer);
    typingTimer = 0;
    if (input.value === text) return false;
    text = input.value;
    search(true);
    return true;
  }
  input.addEventListener("compositionstart", () => { composing = true; window.clearTimeout(typingTimer); });
  input.addEventListener("compositionend", () => { composing = false; typed(); });
  input.addEventListener("input", event => { if (!composing && !(event as InputEvent).isComposing) typed(); });
  input.addEventListener("keydown", event => {
    // The Search key of the keyboard: the nearest place, and the keyboard closes. Enter that ends a composition is the input method's.
    if (event.key !== "Enter" || composing || event.isComposing || event.keyCode === 229) return;
    event.preventDefault();
    if (!take() && ranges.length > 0) {
      shown = nearestPlace(ranges, options.seen(bar).top, !!options.endFirst, bottomOf);
      markFound(marks, ranges, shown);
      show();
      reveal(ranges[shown]);
    }
    input.blur();
  });

  // A tap on a button of the bar leaves the keyboard as it is: open while typing, closed once searched.
  bar.addEventListener("mousedown", event => { if ((event.target as Element | null)?.closest("button")) event.preventDefault(); });
  previous.addEventListener("click", () => step(-1));
  next.addEventListener("click", () => step(1));
  close.addEventListener("click", () => leave());
  // A tap outside the menu closes it: listened to only while it shows.
  const outside = (event: PointerEvent) => {
    if (!menu.contains(event.target as Node) && !optionsButton.contains(event.target as Node)) showMenu(false);
  };
  const showMenu = (shown: boolean) => {
    menu.hidden = !shown;
    optionsButton.setAttribute("aria-expanded", String(shown));
    if (shown) document.addEventListener("pointerdown", outside, true);
    else document.removeEventListener("pointerdown", outside, true);
  };
  optionsButton.addEventListener("click", () => showMenu(menu.hidden));
  for (const item of items) item.addEventListener("click", () => {
    const option = item.dataset.option as keyof FindOptions;
    findOptions = { ...findOptions, [option]: !findOptions[option] };
    item.setAttribute("aria-checked", String(findOptions[option]));
    if (toggles.some(([key]) => findOptions[key])) optionsButton.dataset.on = "true"; else delete optionsButton.dataset.on;
    if (!take()) search(true);
  });
  bar.addEventListener("keydown", event => {
    if (event.key !== "Escape") return;
    event.preventDefault();
    event.stopPropagation();
    if (!menu.hidden) showMenu(false); else leave();
  });
  more.addEventListener("click", async () => {
    if (reading || !options.more) return;
    reading = true;
    show();
    try { await options.more.run(); } finally { reading = false; }
    if (open) search(false);
  });

  const ownEntry = () => !!(window.history.state as Record<string, unknown> | null)?.[historyKey];
  const popped = () => { if (!ownEntry()) end(); };

  /** Opens the bar on the text looked for last, with the keyboard: called from a tap, as the keyboard opens only then. */
  function start() {
    if (open) { input.focus(); input.select(); return; }
    open = true;
    bar.hidden = false;
    window.history.pushState({ ...(window.history.state as object | null ?? {}), [historyKey]: true }, "");
    window.addEventListener("popstate", popped);
    options.onOpen?.();
    input.value = text;
    input.focus();
    input.select();
    if (text) search(true); else show();
  }
  // Closes the bar by its entry in the history, when it is the one shown.
  function leave() {
    if (!open) return;
    if (ownEntry()) window.history.back(); else end();
  }
  /** Closes the bar at once; its entry in the history, if it is still there, closes nothing more. */
  function end() {
    if (!open) return;
    open = false;
    window.removeEventListener("popstate", popped);
    window.clearTimeout(typingTimer);
    window.clearTimeout(refreshTimer);
    window.cancelAnimationFrame(frame);
    window.cancelAnimationFrame(revealing);
    typingTimer = refreshTimer = frame = 0;
    observer.disconnect();
    watched = null;
    showMenu(false);
    if (document.activeElement === input) input.blur();
    bar.hidden = true;
    ranges = [];
    shown = -1;
    clearFound(marks);
    if (looked !== null) { looked = null; options.onPattern?.(null, ""); }
    options.onClose?.();
  }

  return {
    element: bar,
    open: start,
    close: leave,
    dismiss: end,
    isOpen: (): boolean => open,
    /** Searches the page again, keeping the place shown. */
    refresh: () => { if (open) search(false); },
    /** Shows again what the bar says of the page: whether it holds all of what can be searched. */
    changed: () => { if (open) show(); },
  };
}

/** A find bar of the phone. */
export type FindBar = ReturnType<typeof createFindBar>;
