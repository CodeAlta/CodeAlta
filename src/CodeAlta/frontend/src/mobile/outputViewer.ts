import type { LinkedFilePage, ToolOutputPlace } from "./contract";
import { el } from "./dom";
import { timelineIcon } from "./timelineIcons";

/**
 * The output of a tool call, full screen and one page at a time: a long output is never read at once. It opens on the
 * part that says the most (the end of what a command printed, the start of anything else), and buttons read what comes
 * before and after. Its close button, Escape and Back close it: it has an entry of its own in the history, as the images do.
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
   * the view; `drawn` draws what is read so far instead of showing it as it is written (a Markdown file). A page may say
   * which part of the text to mark: the view opens on it.
   */
  function open(caption: readonly Node[], read: (place: ToolOutputPlace) => Promise<LinkedFilePage>, fromEnd: boolean,
    how: Readonly<{ label?: string; drawn?: Readonly<{ prepare: (element: HTMLElement) => void; draw: (element: HTMLElement, text: string) => void }> }> = {}) {
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
    bar.append(title, close);
    const stage = el(document, "div", "output-viewer-stage");
    const earlier = moreButton("output-viewer-earlier"), later = moreButton("output-viewer-later");
    const text = how.drawn ? el(document, "div", "output-viewer-drawn markdown-content") : el(document, "pre", "output-viewer-text");
    how.drawn?.prepare(text);
    let whole = "";
    const note = el(document, "p", "output-viewer-note", "Loading…");
    stage.append(earlier, text, note, later);
    view.append(bar, stage);

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
      // What comes before stays out of sight: the text being read does not move, also when the button above it goes away.
      if (textEnd !== null) stage.scrollTop += text.getBoundingClientRect().bottom - textEnd;
    };

    const add = (page: LinkedFilePage, place: ToolOutputPlace) => {
      const part = page.output ?? "", start = page.outputOffset ?? 0, length = page.outputLength ?? part.length;
      const earlierPart = !!range && place.before !== undefined;
      if (page.markStart !== undefined && page.markEnd !== undefined) mark = { start: page.markStart, end: page.markEnd };
      if (how.drawn) {
        // A page may end inside a block: the text is drawn again whole, and that block with it.
        whole = earlierPart ? part + whole : whole + part;
        how.drawn.draw(text, whole);
      } else if (earlierPart) text.prepend(...pieces(part, start));
      else text.append(...pieces(part, start));
      range = earlierPart ? { start, end: range!.end, length } : { start: range?.start ?? start, end: start + part.length, length };
    };

    const first = async () => {
      await load({});
      if (!view.isConnected) return;
      // What is marked stands a third of the way down, with what comes before it in sight.
      const marked = text.querySelector(".output-viewer-mark");
      if (marked) stage.scrollTop += marked.getBoundingClientRect().top - stage.getBoundingClientRect().top - stage.clientHeight / 3;
      else if (fromEnd) stage.scrollTop = stage.scrollHeight;
    };
    earlier.addEventListener("click", () => { if (range) void load({ before: range.start }); });
    later.addEventListener("click", () => { if (range) void load({ offset: range.end }); else void first(); });

    const before = document.activeElement as HTMLElement | null;
    document.body.append(view);
    window.history.pushState({ outputViewer: true }, "");
    const gone = () => {
      view.remove();
      window.removeEventListener("popstate", gone);
      window.removeEventListener("keydown", keys);
      if (shown?.leave === leave) shown = null;
      before?.focus?.();
    };
    const keys = (event: KeyboardEvent) => { if (event.key === "Escape") { event.preventDefault(); leave(); } };
    const leave = () => { if ((window.history.state as { outputViewer?: boolean } | null)?.outputViewer) window.history.back(); else gone(); };
    window.addEventListener("popstate", gone);
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
