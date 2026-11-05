import type { ToolOutputPage, ToolOutputPlace } from "./contract";
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

  /** Shows an output: `caption` says of which call, `read` gives its pages, and `fromEnd` says to start at its end. */
  function open(caption: readonly Node[], read: (place: ToolOutputPlace) => Promise<ToolOutputPage>, fromEnd: boolean) {
    shown?.leave();
    const view = el(document, "div", "output-viewer");
    view.setAttribute("role", "dialog");
    view.setAttribute("aria-modal", "true");
    view.setAttribute("aria-label", "Output of the tool call");
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
    const text = el(document, "pre", "output-viewer-text");
    const note = el(document, "p", "output-viewer-note", "Loading…");
    stage.append(earlier, text, note, later);
    view.append(bar, stage);

    // What the page holds of the output, in characters; null until a page came.
    let range: { start: number; end: number; length: number } | null = null;
    let reading = false;

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
      let failure = "";
      try {
        const page = await read(place);
        if (!view.isConnected) return;
        if (page.error) failure = page.error;
        else add(page, place);
      } catch (error) {
        if (!view.isConnected) return;
        failure = error instanceof Error ? error.message : String(error);
      }
      reading = false;
      note.textContent = failure;
      update();
    };

    const add = (page: ToolOutputPage, place: ToolOutputPlace) => {
      const part = page.output ?? "", start = page.outputOffset ?? 0, length = page.outputLength ?? part.length;
      const node = document.createTextNode(part);
      if (range && place.before !== undefined) {
        // What comes before stays out of sight: the text being read does not move.
        const height = stage.scrollHeight;
        text.prepend(node);
        stage.scrollTop += stage.scrollHeight - height;
        range = { start, end: range.end, length };
      } else {
        text.append(node);
        range = { start: range?.start ?? start, end: start + part.length, length };
      }
    };

    const first = async () => {
      await load({});
      if (fromEnd && view.isConnected) stage.scrollTop = stage.scrollHeight;
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
