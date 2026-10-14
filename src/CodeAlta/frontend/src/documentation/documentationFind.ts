/**
 * The search of a text in the page the Documentation tab shows (Ctrl+F, Cmd+F on macOS): the places of the page
 * where the text is, marked without changing the page, which the Markdown boundary of the window draws.
 */

/** The names of the marks of the page: every place found, and the place shown. */
export const findHighlight = "documentation-find", currentFindHighlight = "documentation-find-current";
/** More places than this are not looked for: the count says "1000". */
export const maximumFindMatches = 1000;

const blocks = "p, li, h1, h2, h3, h4, h5, h6, td, th, pre, blockquote, figcaption, dt, dd, summary";
// What is not a word of the page: the links to the pages before and after, the button that copies a code block.
const skipped = ".documentation-pager, button, script, style, [aria-hidden='true']";

/**
 * What a text is looked for with: its letters whatever their case, and any run of spaces as any run of spaces (a
 * line of the source is a space of the page). Null for a text of spaces only.
 */
export function findPattern(text: string): RegExp | null {
  const words = text.trim().split(/\s+/u).filter(Boolean);
  if (words.length === 0) return null;
  return new RegExp(words.map(word => word.replace(/[.*+?^${}()|[\]\\]/gu, "\\$&")).join("\\s+"), "giu");
}

/** The places of a text in a line: where each starts and ends, one after the other. */
export function findMatches(line: string, text: string, limit = maximumFindMatches): { start: number; end: number }[] {
  const pattern = findPattern(text);
  if (!pattern) return [];
  const found: { start: number; end: number }[] = [];
  for (const match of line.matchAll(pattern)) {
    if (found.length >= limit) break;
    found.push({ start: match.index, end: match.index + match[0].length });
  }
  return found;
}

/**
 * The places of a text in the words of an element, in the order they are read. A place is in one paragraph, item,
 * cell or heading, and can cross the parts of it that are written differently (a word in bold, a link).
 */
export function findRanges(root: HTMLElement, text: string): Range[] {
  if (!findPattern(text)) return [];
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
    for (const { start, end } of findMatches(line, text, maximumFindMatches - ranges.length)) {
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
 * Whether the window leaves Ctrl+F to the element that has the keyboard: the Documentation tab finds in its page
 * there, where Ctrl+F elsewhere searches the sessions.
 */
export function findKeyKept(event: Readonly<{ key: string; ctrlKey: boolean; metaKey: boolean; altKey: boolean; shiftKey: boolean }>, target: Element | null): boolean {
  return isFindKey(event, false) && !!target?.closest("[data-find-keys]");
}

// The marks of the window, a map of names to marks; the DOM library of the project types its map methods with the
// iterable DOM library, which it does not include.
function highlights(): Map<string, Highlight> | null {
  return typeof CSS !== "undefined" && "highlights" in CSS && typeof Highlight !== "undefined" ? CSS.highlights as unknown as Map<string, Highlight> : null;
}

/** Marks the places found, and the one shown, where the engine of the window can mark a page without changing it. */
export function markFound(ranges: readonly Range[], current: number): void {
  const marks = highlights();
  if (!marks) return;
  if (ranges.length === 0) { clearFound(); return; }
  marks.set(findHighlight, new Highlight(...ranges));
  const shown = ranges[current];
  if (!shown) { marks.delete(currentFindHighlight); return; }
  // The place shown is drawn over the mark of every place.
  const mark = new Highlight(shown);
  mark.priority = 1;
  marks.set(currentFindHighlight, mark);
}

/** Takes the marks of the places found away. */
export function clearFound(): void {
  const marks = highlights();
  marks?.delete(findHighlight);
  marks?.delete(currentFindHighlight);
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
