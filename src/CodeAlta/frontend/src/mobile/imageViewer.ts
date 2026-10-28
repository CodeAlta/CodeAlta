import { el } from "./dom";
import { steppedIndex, type ImageItem } from "./imageModel";
import { timelineIcon } from "./timelineIcons";

// How far a finger goes for a swipe: sideways to the next image, down to close.
const swipeAside = 60, swipeDown = 90;

/**
 * The images of the timeline, full screen: the image whole, which the browser pinches and pans; a swipe sideways goes
 * to the next image of the same group, a swipe down closes it, as do its close button, Escape and Back (it has an
 * entry of its own in the history, as the diagrams do). A swipe is a swipe only while the page is not zoomed.
 */
export function createImageViewer(options: Readonly<{ window: Window & typeof globalThis }>) {
  const { window } = options, document = window.document;
  let shown: { view: HTMLElement; leave: () => void } | null = null;

  function button(className: string, label: string, name: Parameters<typeof timelineIcon>[1]): HTMLButtonElement {
    const item = el(document, "button", `icon-btn ${className}`);
    item.type = "button";
    item.setAttribute("aria-label", label);
    item.title = label;
    item.append(timelineIcon(document, name));
    return item;
  }

  /** Shows an image of a group, from which a swipe goes to the others. */
  function open(items: readonly ImageItem[], start: number) {
    if (!items.length) return;
    shown?.leave();
    let index = Math.min(Math.max(0, start), items.length - 1);
    const view = el(document, "div", "img-viewer");
    view.setAttribute("role", "dialog");
    view.setAttribute("aria-modal", "true");
    const bar = el(document, "div", "img-viewer-bar");
    const caption = el(document, "div", "img-viewer-caption");
    const title = el(document, "span", "img-viewer-title"), count = el(document, "span", "img-viewer-count");
    caption.append(title, count);
    const close = button("img-viewer-close", "Close", "close");
    bar.append(caption, close);
    const stage = el(document, "div", "img-viewer-stage");
    const image = el(document, "img");
    image.decoding = "async";
    const failed = el(document, "p", "img-viewer-failed", "The image could not be read.");
    failed.hidden = true;
    image.addEventListener("error", () => { failed.hidden = false; image.hidden = true; });
    image.addEventListener("load", () => { failed.hidden = true; image.hidden = false; });
    stage.append(image, failed);
    view.append(bar, stage);
    const previous = button("img-viewer-step img-viewer-prev", "Previous image", "left");
    const next = button("img-viewer-step img-viewer-next", "Next image", "right");
    if (items.length > 1) view.append(previous, next);

    const show = () => {
      const item = items[index];
      view.setAttribute("aria-label", item.title || "Image");
      title.textContent = item.title || "Image";
      count.textContent = items.length > 1 ? `${index + 1} / ${items.length}` : "";
      image.alt = item.title || "Image";
      failed.hidden = true;
      image.hidden = false;
      image.src = item.url;
      stage.scrollTo?.(0, 0);
    };
    const step = (by: number) => { if (items.length > 1) { index = steppedIndex(index, items.length, by); show(); } };
    previous.addEventListener("click", () => step(-1));
    next.addEventListener("click", () => step(1));

    // A swipe of one finger while the page is at its own scale; a pinch or a pan of a zoomed image is the browser's.
    let touch: { x: number; y: number } | null = null;
    const zoomed = () => (window.visualViewport?.scale ?? 1) > 1.01;
    stage.addEventListener("touchstart", event => { touch = event.touches.length === 1 && !zoomed() ? { x: event.touches[0].clientX, y: event.touches[0].clientY } : null; }, { passive: true });
    stage.addEventListener("touchmove", event => { if (event.touches.length !== 1) touch = null; }, { passive: true });
    stage.addEventListener("touchend", event => {
      const from = touch, end = event.changedTouches[0];
      touch = null;
      if (!from || !end || zoomed()) return;
      const dx = end.clientX - from.x, dy = end.clientY - from.y;
      if (Math.abs(dx) > swipeAside && Math.abs(dx) > Math.abs(dy) * 1.5) step(dx < 0 ? 1 : -1);
      else if (dy > swipeDown && dy > Math.abs(dx) * 1.5) leave();
    });

    const before = document.activeElement as HTMLElement | null;
    document.body.append(view);
    window.history.pushState({ imageViewer: true }, "");
    const gone = () => {
      view.remove();
      window.removeEventListener("popstate", gone);
      window.removeEventListener("keydown", keys);
      if (shown?.view === view) shown = null;
      before?.focus?.();
    };
    const keys = (event: KeyboardEvent) => {
      if (event.key === "Escape") { event.preventDefault(); leave(); }
      else if (event.key === "ArrowRight") step(1);
      else if (event.key === "ArrowLeft") step(-1);
    };
    const leave = () => { if ((window.history.state as { imageViewer?: boolean } | null)?.imageViewer) window.history.back(); else gone(); };
    window.addEventListener("popstate", gone);
    window.addEventListener("keydown", keys);
    close.addEventListener("click", leave);
    shown = { view, leave };
    show();
    close.focus();
  }

  return { open, /** Whether an image is shown. */ isOpen: (): boolean => shown !== null };
}

/** The full-screen images of the page. */
export type ImageViewer = ReturnType<typeof createImageViewer>;
