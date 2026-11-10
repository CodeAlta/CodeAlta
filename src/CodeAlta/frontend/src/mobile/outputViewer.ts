import type { LinkedFilePage, ToolOutputPlace } from "./contract";
import { sourceBlocks } from "../markdownSourceLines";
import { el } from "./dom";
import { createFindBar } from "./findBar";
import { timelineIcon } from "./timelineIcons";

/** How much of a text "Search the whole file" holds at most, in characters. */
export const searchedTextLimit = 1_000_000;

/**
 * The output of a tool call, full screen and one page at a time: a long output is never read at once. It opens on the
 * part that says the most (the end of what a command printed, the start of anything else), and buttons read what comes
 * before and after. Its close button, Escape and Back close it: it has an entry of its own in the history, as the images do.
 * Its magnifier finds in what it holds (findBar.ts), and reads the rest of the text on request, up to a limit. A Markdown
 * file is shown drawn or as it is written, and a button of its bar switches between them, at the same place.
 */
export function createOutputViewer(options: Readonly<{ window: Window & typeof globalThis }>) {
  const { window } = options, document = window.document;
  let shown: { leave: () => void } | null = null;

  function moreButton(className: string): HTMLButtonElement {
    const item = el(document, "button", `more ${className}`);
    item.type = "button";
    return item;
  }

  /**
   * Shows a text: `caption` says what it is, `read` gives its pages, and `fromEnd` says to start at its end. `label` names
   * the view; `drawn` draws what is read so far instead of showing it as it is written (a Markdown file), with the lines
   * of its blocks, and calls `after` after each drawing. A drawn text can also be shown as it is written, and back: it
   * opens so when `asText` says so. A page may say which part of the text to mark: the view opens on it. `file` says that
   * the text is a file, for the words of its search.
   */
  function open(caption: readonly Node[], read: (place: ToolOutputPlace) => Promise<LinkedFilePage>, fromEnd: boolean,
    how: Readonly<{ label?: string; file?: boolean; asText?: boolean;
      drawn?: Readonly<{ prepare: (element: HTMLElement) => void; draw: (element: HTMLElement, text: string, after: () => void) => void }> }> = {}) {
    shown?.leave();
    const view = el(document, "div", "output-viewer");
    view.setAttribute("role", "dialog");
    view.setAttribute("aria-modal", "true");
    view.setAttribute("aria-label", how.label ?? "Output of the tool call");
    const bar = el(document, "div", "output-viewer-bar");
    const title = el(document, "div", "output-viewer-title");
    title.append(...caption);
    const close = el(document, "button", "icon-btn output-viewer-close");
    close.type = "button";
    close.setAttribute("aria-label", "Close");
    close.title = "Close";
    close.append(timelineIcon(document, "close"));
    const findButton = el(document, "button", "icon-btn output-viewer-find");
    findButton.type = "button";
    findButton.setAttribute("aria-label", how.file ? "Find in this file" : "Find in this output");
    findButton.title = "Find";
    findButton.append(timelineIcon(document, "search"));
    const switchButton = how.drawn ? el(document, "button", "icon-btn output-viewer-switch") : null;
    if (switchButton) switchButton.type = "button";
    bar.append(title, ...(switchButton ? [switchButton] : []), findButton, close);
    const stage = el(document, "div", "output-viewer-stage");
    const earlier = moreButton("output-viewer-earlier"), later = moreButton("output-viewer-later");
    // The text as it is written, and drawn when it can be: one of them is shown, made again from `whole` at each switch.
    const plain = el(document, "pre", "output-viewer-text");
    const rich = how.drawn ? el(document, "div", "output-viewer-drawn markdown-content") : null;
    if (rich) how.drawn!.prepare(rich);
    let text: HTMLElement = rich && !how.asText ? rich : plain;
    // What the view holds of the text, when it can be drawn.
    let whole = "";
    const note = el(document, "p", "output-viewer-note", "Loading…");
    stage.append(earlier, text, note, later);

    // What the page holds of the output, in characters; null until a page came.
    let range: { start: number; end: number; length: number } | null = null;
    let reading = false;
    // The part of the text to mark, in characters: lines that may reach over several pages.
    let mark: { start: number; end: number } | null = null;

    const pieces = (part: string, start: number): Node[] => {
      if (!mark || start >= mark.end || start + part.length <= mark.start) return [document.createTextNode(part)];
      const from = Math.max(0, mark.start - start), to = Math.min(part.length, mark.end - start);
      return [document.createTextNode(part.slice(0, from)), el(document, "mark", "output-viewer-mark", part.slice(from, to)), document.createTextNode(part.slice(to))];
    };

    const update = () => {
      earlier.hidden = !range || range.start <= 0;
      later.hidden = reading && !range || !!range && range.end >= range.length;
      earlier.disabled = later.disabled = reading;
      if (switchButton) switchButton.disabled = reading;
      if (range) {
        earlier.textContent = `Read before (${range.start.toLocaleString()} characters)`;
        later.textContent = `Read after (${(range.length - range.end).toLocaleString()} characters)`;
      } else later.textContent = "Try again";
    };

    const load = async (place: ToolOutputPlace) => {
      if (reading) return;
      reading = true;
      note.textContent = "Loading…";
      update();
      let failure = "", textEnd: number | null = null;
      try {
        const page = await read(place);
        if (!view.isConnected) return;
        if (place.before !== undefined) textEnd = text.getBoundingClientRect().bottom;
        if (page.error) failure = page.error;
        else add(page, place);
      } catch (error) {
        if (!view.isConnected) return;
        failure = error instanceof Error ? error.message : String(error);
      }
      reading = false;
      note.textContent = failure || (range?.length === 0 ? "This is empty." : "");
      update();
      find.changed();
      // What comes before stays out of sight: the text being read does not move, also when the button above it goes away.
      if (textEnd !== null) stage.scrollTop += text.getBoundingClientRect().bottom - textEnd;
    };

    const add = (page: LinkedFilePage, place: ToolOutputPlace) => {
      const part = page.output ?? "", start = page.outputOffset ?? 0, length = page.outputLength ?? part.length;
      const earlierPart = !!range && place.before !== undefined;
      if (page.markStart !== undefined && page.markEnd !== undefined) mark = { start: page.markStart, end: page.markEnd };
      range = earlierPart ? { start, end: range!.end, length } : { start: range?.start ?? start, end: start + part.length, length };
      if (rich) whole = earlierPart ? part + whole : whole + part;
      // A page may end inside a block: the text is drawn again whole, and that block with it.
      if (text === rich) draw();
      else if (earlierPart) plain.prepend(...pieces(part, start));
      else plain.append(...pieces(part, start));
    };

    // The lines of what the view holds, 1-based as the blocks drawn from it count them.
    const lineAt = (offset: number) => {
      let line = 1;
      for (let at = whole.indexOf("\n"); at >= 0 && at < offset; at = whole.indexOf("\n", at + 1)) line++;
      return line;
    };
    const lineStart = (line: number) => {
      let at = 0;
      for (let count = 1; count < line; count++) { const next = whole.indexOf("\n", at); if (next < 0) break; at = next + 1; }
      return at;
    };
    const draw = () => how.drawn!.draw(rich!, whole, markBlocks);
    // The blocks drawn from the marked lines are marked too: the innermost ones, such as an item rather than its list.
    const markBlocks = () => {
      if (!mark || !range || !rich) return;
      const from = Math.max(mark.start, range.start) - range.start, to = Math.min(mark.end, range.end) - range.start;
      if (from >= to) return;
      const firstLine = lineAt(from), lastLine = lineAt(to - 1);
      const blocks = sourceBlocks(rich).filter(block => block.start <= lastLine && block.end >= firstLine);
      for (const { element } of blocks) if (!blocks.some(other => other.element !== element && element.contains(other.element))) element.classList.add("output-viewer-block-mark");
    };

    // The text nodes of the text as it is written, with the place of each in what the view holds.
    const textNodes = function* () {
      const walker = document.createTreeWalker(plain, window.NodeFilter.SHOW_TEXT);
      for (let node = walker.nextNode() as Text | null, at = 0; node; at += node.length, node = walker.nextNode() as Text | null) yield { node, at };
    };
    // The line of the text as it is written at the top of the stage; null when the browser cannot tell.
    const plainTopLine = (): number | null => {
      const box = plain.getBoundingClientRect(), x = box.left + 1, y = Math.max(box.top, stage.getBoundingClientRect().top) + 1;
      // Some browsers of phones have only `caretRangeFromPoint`.
      const caret = document.caretPositionFromPoint?.(x, y), hit = caret ? null : document.caretRangeFromPoint?.(x, y);
      const node = caret?.offsetNode ?? hit?.startContainer, offset = caret?.offset ?? hit?.startOffset ?? 0;
      for (const { node: each, at } of textNodes()) if (each === node) return lineAt(at + offset);
      return null;
    };
    const showPlainLine = (line: number) => {
      const offset = lineStart(line);
      for (const { node, at } of textNodes()) {
        if (offset >= at + node.length) continue;
        const span = document.createRange();
        span.setStart(node, offset - at);
        span.setEnd(node, offset - at + 1);
        const box = span.getClientRects()[0] ?? span.getBoundingClientRect();
        stage.scrollTop += box.top - stage.getBoundingClientRect().top;
        return;
      }
    };
    // The line of the drawn text at the top of the stage: in the innermost block there, as far into its lines as into its height.
    const richTopLine = (): number | null => {
      const top = stage.getBoundingClientRect().top;
      let shownBlock: ReturnType<typeof sourceBlocks>[number] | null = null;
      for (const block of sourceBlocks(rich!)) {
        if (block.element.getBoundingClientRect().bottom <= top + 1) continue;
        if (shownBlock && !shownBlock.element.contains(block.element)) break;
        shownBlock = block;
      }
      if (!shownBlock) return null;
      const box = shownBlock.element.getBoundingClientRect(), into = box.height > 0 ? Math.min(1, Math.max(0, (top - box.top) / box.height)) : 0;
      return Math.min(shownBlock.end, shownBlock.start + Math.floor(into * (shownBlock.end - shownBlock.start + 1)));
    };
    // Shows a line of the drawn text at the top: the innermost block that holds it, or the first one after it.
    const showRichLine = (line: number) => {
      const blocks = sourceBlocks(rich!);
      const holding = blocks.filter(block => block.start <= line && line <= block.end).at(-1), block = holding ?? blocks.find(each => each.start > line);
      if (!block) return;
      const box = block.element.getBoundingClientRect(), into = holding ? (line - holding.start) / (holding.end - holding.start + 1) : 0;
      stage.scrollTop += box.top + into * box.height - stage.getBoundingClientRect().top;
    };
    const inSight = (element: Element | null) => {
      if (!element) return false;
      const box = element.getBoundingClientRect(), seen = stage.getBoundingClientRect();
      return box.bottom > seen.top && box.top < seen.bottom;
    };
    // What is marked stands a third of the way down, with what comes before it in sight.
    const toThird = (element: Element) => { stage.scrollTop += element.getBoundingClientRect().top - stage.getBoundingClientRect().top - stage.clientHeight / 3; };

    const showSwitch = () => {
      if (!switchButton) return;
      const label = text === rich ? "Show as text" : "Show as Markdown";
      switchButton.setAttribute("aria-label", label);
      switchButton.title = label;
      switchButton.replaceChildren(timelineIcon(document, text === rich ? "code" : "eye"));
    };
    // Drawn or as it is written, at the same place: the marked lines a third of the way down when they are in sight,
    // else the line at the top. Nothing is read again.
    const switchView = () => {
      if (!rich || reading) return;
      const toRich = text !== rich, marked = toRich && inSight(plain.querySelector(".output-viewer-mark"));
      const line = toRich ? plainTopLine() : richTopLine();
      const leaving = text;
      text = toRich ? rich : plain;
      leaving.replaceWith(text);
      leaving.replaceChildren();
      if (toRich) draw();
      else plain.append(...pieces(whole, range?.start ?? 0));
      showSwitch();
      const markedBlock = marked ? rich.querySelector(".output-viewer-block-mark") : null;
      if (markedBlock) toThird(markedBlock);
      else if (line === null) stage.scrollTop = 0;
      else if (toRich) showRichLine(line);
      else showPlainLine(line);
    };

    const first = async () => {
      await load({});
      if (!view.isConnected) return;
      const marked = text.querySelector(".output-viewer-mark, .output-viewer-block-mark");
      if (marked) toThird(marked);
      else if (fromEnd) stage.scrollTop = stage.scrollHeight;
    };
    // What the view holds, and whether more of the text can be read to search it.
    const held = () => range ? range.end - range.start : 0;
    const partial = () => !!range && (range.start > 0 || range.end < range.length);
    const readAll = async () => {
      // What comes before stays out of sight as it is read: the place shown does not move.
      while (view.isConnected && range && range.start > 0 && held() < searchedTextLimit) {
        const at = range.start;
        await load({ before: at });
        if (!range || range.start === at) return;
      }
      while (view.isConnected && range && range.end < range.length && held() < searchedTextLimit) {
        const at = range.end;
        await load({ offset: at });
        if (!range || range.end === at) return;
      }
    };
    const find = createFindBar({
      window, label: how.file ? "Find in this file" : "Find in this output", historyKey: "outputFind", className: "output-find",
      page: () => text, scroller: () => stage,
      seen: () => {
        const box = stage.getBoundingClientRect(), viewport = window.visualViewport;
        return { top: box.top, bottom: Math.min(box.bottom, viewport ? viewport.offsetTop + viewport.height : box.bottom) };
      },
      partial,
      more: { label: how.file ? "Search the whole file" : "Search the whole output", busy: "Reading…", available: () => partial() && held() < searchedTextLimit, run: readAll },
      onOpen: () => { view.dataset.finding = "true"; },
      onClose: () => { delete view.dataset.finding; if (view.isConnected) findButton.focus(); },
    });
    view.append(bar, find.element, stage);
    findButton.addEventListener("click", () => find.open());
    switchButton?.addEventListener("click", switchView);
    showSwitch();
    earlier.addEventListener("click", () => { if (range) void load({ before: range.start }); });
    later.addEventListener("click", () => { if (range) void load({ offset: range.end }); else void first(); });

    const before = document.activeElement as HTMLElement | null;
    document.body.append(view);
    // The entry keeps those under it: a search of the timeline under the view stays open.
    window.history.pushState({ ...(window.history.state as object | null ?? {}), outputViewer: true }, "");
    type Entry = { outputViewer?: boolean; outputFind?: boolean } | null;
    const gone = () => {
      find.dismiss();
      view.remove();
      window.removeEventListener("popstate", popped);
      window.removeEventListener("keydown", keys);
      if (shown?.leave === leave) shown = null;
      before?.focus?.();
    };
    const keys = (event: KeyboardEvent) => { if (event.key === "Escape") { event.preventDefault(); leave(); } };
    // Back from the search of the view leaves the view open.
    const popped = () => { if (!(window.history.state as Entry)?.outputViewer) gone(); };
    const leave = () => {
      const entry = window.history.state as Entry;
      if (entry?.outputViewer) window.history.go(entry.outputFind ? -2 : -1); else gone();
    };
    window.addEventListener("popstate", popped);
    window.addEventListener("keydown", keys);
    close.addEventListener("click", leave);
    shown = { leave };
    void first();
    close.focus();
  }

  return { open };
}

/** The full-screen output of a tool call. */
export type OutputViewer = ReturnType<typeof createOutputViewer>;
