/**
 * The search of a text in a rendered page (Ctrl+F, Cmd+F on macOS): the page of the Documentation tab, the Markdown
 * preview of the code editor, a plan or a file under review. The places of the page where the text is are marked
 * without changing the page, which the Markdown boundary of the window draws.
 */

/** The names of the marks of the page: every place found, and the place shown. */
export const findHighlight = "find-in-page", currentFindHighlight = "find-in-page-current";
/** More places than this are not looked for: the count says "1000". */
export const maximumFindMatches = 1000;

const blocks = "p, li, h1, h2, h3, h4, h5, h6, td, th, pre, blockquote, figcaption, dt, dd, summary";
// What is not a word of the page: the links to the pages before and after, the button that copies a code block, the
// gutter and the comment cards of a review.
const skipped = ".documentation-pager, .review-block-gutter, .review-block-host, button, script, style, [aria-hidden='true']";

/** How a text is looked for: as the search of the text editor, whatever the case unless asked, and as typed. */
export type FindOptions = Readonly<{ matchCase: boolean; wholeWord: boolean; regex: boolean }>;
export const defaultFindOptions: FindOptions = Object.freeze({ matchCase: false, wholeWord: false, regex: false });

// A text as it is written: its words, any run of spaces as any run of spaces (a line of the source is a space of the page).
function literal(text: string): string | null {
  const words = text.trim().split(/\s+/u).filter(Boolean);
  return words.length === 0 ? null : words.map(word => word.replace(/[.*+?^${}()|[\]\\]/gu, "\\$&")).join("\\s+");
}

/**
 * What a text is looked for with: its letters whatever their case unless `matchCase`, only as a whole word with
 * `wholeWord` (no letter, digit or `_` just before or after), and read as a regular expression with `regex`.
 * Null for a text of spaces only, and for a regular expression that is not valid (see {@link findPatternInvalid}).
 */
export function findPattern(text: string, options: FindOptions = defaultFindOptions): RegExp | null {
  const source = options.regex ? (text.length > 0 ? text : null) : literal(text);
  if (source === null) return null;
  const flags = options.matchCase ? "gu" : "giu";
  const whole = (value: string) => options.wholeWord ? `(?<![\\p{L}\\p{N}_])(?:${value})(?![\\p{L}\\p{N}_])` : value;
  try { return new RegExp(whole(source), flags); }
  catch {
    // A regular expression the Unicode mode refuses (`\-`, `\_`) is read as the text editor reads it.
    if (!options.regex) return null;
    try { return new RegExp(options.wholeWord ? `(?<!\\w)(?:${source})(?!\\w)` : source, flags.replace("u", "")); }
    catch { return null; }
  }
}

/** Whether a text asks for a regular expression that is not valid: nothing is found, and the box says so. */
export function findPatternInvalid(text: string, options: FindOptions): boolean {
  return options.regex && text.length > 0 && findPattern(text, options) === null;
}

// The places of a pattern in a line; a place of no letter (`a*` before a `b`) is no place.
function matchesOf(line: string, pattern: RegExp, limit: number): { start: number; end: number }[] {
  const found: { start: number; end: number }[] = [];
  for (const match of line.matchAll(pattern)) {
    if (found.length >= limit) break;
    if (match[0].length > 0) found.push({ start: match.index, end: match.index + match[0].length });
  }
  return found;
}

/** The places of a text in a line: where each starts and ends, one after the other. */
export function findMatches(line: string, text: string, limit = maximumFindMatches, options: FindOptions = defaultFindOptions): { start: number; end: number }[] {
  const pattern = findPattern(text, options);
  return pattern ? matchesOf(line, pattern, limit) : [];
}

/**
 * The places of a text in the words of an element, in the order they are read. A place is in one paragraph, item,
 * cell or heading, and can cross the parts of it that are written differently (a word in bold, a link).
 */
export function findRanges(root: HTMLElement, text: string, options: FindOptions = defaultFindOptions): Range[] {
  const pattern = findPattern(text, options);
  if (!pattern) return [];
  const ranges: Range[] = [];
  const walker = root.ownerDocument.createTreeWalker(root, NodeFilter.SHOW_TEXT, {
    acceptNode: node => {
      const outside = node.parentElement?.closest(skipped);
      return outside && root.contains(outside) ? NodeFilter.FILTER_REJECT : NodeFilter.FILTER_ACCEPT;
    },
  });
  let nodes: Text[] = [], owner: Element | null = null;
  const search = () => {
    if (nodes.length === 0) return;
    const starts: number[] = [];
    let line = "";
    for (const node of nodes) { starts.push(line.length); line += node.data; }
    let at = 0;
    for (const { start, end } of matchesOf(line, pattern, maximumFindMatches - ranges.length)) {
      while (at < nodes.length - 1 && starts[at + 1] <= start) at++;
      let last = at;
      while (last < nodes.length - 1 && starts[last + 1] < end) last++;
      const range = root.ownerDocument.createRange();
      range.setStart(nodes[at], start - starts[at]);
      range.setEnd(nodes[last], end - starts[last]);
      ranges.push(range);
    }
    nodes = [];
  };
  for (let node = walker.nextNode() as Text | null; node && ranges.length < maximumFindMatches; node = walker.nextNode() as Text | null) {
    const block = node.parentElement?.closest(blocks) ?? root;
    if (block !== owner) { search(); owner = block; }
    nodes.push(node);
  }
  search();
  return ranges;
}

/** Whether a key asks for the search in the page: Ctrl+F, and Cmd+F on macOS. */
export function isFindKey(event: Readonly<{ key: string; ctrlKey: boolean; metaKey: boolean; altKey: boolean; shiftKey: boolean }>, mac: boolean): boolean {
  return event.key.toLowerCase() === "f" && !event.altKey && !event.shiftKey && (mac ? event.metaKey && !event.ctrlKey : event.ctrlKey && !event.metaKey);
}

/**
 * Whether the window leaves Ctrl+F to the element that has the keyboard: a rendered page (`data-find-keys`) finds
 * in itself there, where Ctrl+F elsewhere searches the sessions.
 */
export function findKeyKept(event: Readonly<{ key: string; ctrlKey: boolean; metaKey: boolean; altKey: boolean; shiftKey: boolean }>, target: Element | null): boolean {
  return isFindKey(event, false) && !!target?.closest("[data-find-keys]");
}

// The marks of the window, a map of names to marks; the DOM library of the project types its map methods with the
// iterable DOM library, which it does not include.
function highlights(): Map<string, Highlight> | null {
  return typeof CSS !== "undefined" && "highlights" in CSS && typeof Highlight !== "undefined" ? CSS.highlights as unknown as Map<string, Highlight> : null;
}

/**
 * The places one search has marked. The marks belong to the window, and several pages can search at once (the
 * Documentation tab and a code editor, each in its tab): each search takes back only its own places.
 */
export type FoundMarks = { ranges: readonly Range[]; shown: Range | null };

/** The marks of a search that has marked nothing yet. */
export function noFoundMarks(): FoundMarks {
  return { ranges: [], shown: null };
}

// The places of a mark, which the engine keeps as a set; the DOM library of the project types none of its methods.
type RangeSet = Highlight & { add(range: AbstractRange): unknown; delete(range: AbstractRange): boolean; readonly size: number };

function replaceIn(marks: Map<string, Highlight>, name: string, removed: readonly Range[], added: readonly Range[], priority: number): void {
  const mark = (marks.get(name) ?? new Highlight()) as RangeSet;
  for (const range of removed) mark.delete(range);
  for (const range of added) mark.add(range);
  mark.priority = priority;
  if (mark.size === 0) marks.delete(name);
  else if (!marks.has(name)) marks.set(name, mark);
}

/**
 * Marks the places found, and the one shown, where the engine of the window can mark a page without changing it, in
 * place of what the same search marked before (`marked`, updated).
 */
export function markFound(marked: FoundMarks, ranges: readonly Range[], current: number): void {
  const marks = highlights();
  const shown = ranges[current] ?? null;
  if (marks) {
    replaceIn(marks, findHighlight, marked.ranges, ranges, 0);
    // The place shown is drawn over the mark of every place.
    replaceIn(marks, currentFindHighlight, marked.shown ? [marked.shown] : [], shown ? [shown] : [], 1);
  }
  marked.ranges = [...ranges];
  marked.shown = shown;
}

/** Takes the marks of the places a search found away. */
export function clearFound(marked: FoundMarks): void {
  markFound(marked, [], -1);
}

/**
 * Scrolls a place into the page: into a wide table or block of code that scrolls by itself, then into the scroll of
 * the page, a third of the way down, out of the way of the search box. A place that is seen already stays where it is.
 */
export function revealRange(range: Range, scroller: HTMLElement): void {
  const wide = range.startContainer.parentElement?.closest<HTMLElement>("pre, table");
  if (wide && wide.scrollWidth > wide.clientWidth) {
    const box = range.getBoundingClientRect(), frame = wide.getBoundingClientRect();
    if (box.left < frame.left || box.right > frame.right) wide.scrollLeft += box.left - frame.left - (frame.width - box.width) / 2;
  }
  const box = range.getBoundingClientRect(), view = scroller.getBoundingClientRect();
  if (box.top < view.top + 56 || box.bottom > view.bottom - 16) scroller.scrollTop += box.top - view.top - view.height / 3;
}

/** Whether a place is in the part of the page that is seen. */
export function rangeShown(range: Range, scroller: HTMLElement): boolean {
  const box = range.getBoundingClientRect(), view = scroller.getBoundingClientRect();
  return box.bottom > view.top && box.top < view.bottom;
}
