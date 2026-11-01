/** How long a setting glows once it is shown, in milliseconds: the length of the `settings-glow` animation. */
export const glowMilliseconds = 2400;

/** How long a page that reads its values first is waited for before the section or the heading glows instead. */
export const glowWaitMilliseconds = 3000;

type Frames = Readonly<{ request(callback: () => void): unknown; cancel(handle: unknown): void; now(): number; later(callback: () => void, milliseconds: number): unknown }>;

const browserFrames: Frames = {
  request: callback => requestAnimationFrame(callback),
  cancel: handle => cancelAnimationFrame(handle as number),
  now: () => performance.now(),
  later: (callback, milliseconds) => setTimeout(callback, milliseconds),
};

/** The element of a setting in a part of the page: its `data-setting` names it (a row can stand for several settings, separated by spaces). */
export function settingElement(root: ParentNode, id: string): HTMLElement | null {
  return root.querySelector<HTMLElement>(`[data-setting~="${CSS.escape(id)}"]`);
}

/** Makes an element glow: the attribute starts the animation, and goes away when it ends. */
export function glow(element: HTMLElement, frames: Frames = browserFrames) {
  element.removeAttribute("data-glow");
  // A second glow of the same element starts the animation again.
  void element.offsetWidth;
  element.setAttribute("data-glow", "");
  frames.later(() => element.removeAttribute("data-glow"), glowMilliseconds);
}

/**
 * Waits for a setting to be drawn in `root` (a page reads its values before it draws them), brings it into view and
 * makes it glow. When it is not drawn in time (a setting this host does not have), `fallback` glows instead: the
 * section or the heading of the page. Returns what cancels the wait.
 */
export function glowSetting(root: () => ParentNode | null, id: string, fallback: () => HTMLElement | null, frames: Frames = browserFrames): () => void {
  const start = frames.now();
  let handle: unknown = null;
  let done = false;
  const step = () => {
    if (done) return;
    const parent = root();
    const element = parent ? settingElement(parent, id) : null;
    const target = element ?? (frames.now() - start >= glowWaitMilliseconds ? fallback() : null);
    if (target) {
      done = true;
      // "nearest" scrolls the content of Settings alone; "start" would also scroll the dialog itself.
      target.scrollIntoView({ block: "nearest", inline: "nearest" });
      glow(target, frames);
      return;
    }
    if (frames.now() - start < glowWaitMilliseconds + 100) handle = frames.request(step);
  };
  handle = frames.request(step);
  return () => { done = true; if (handle !== null) frames.cancel(handle); };
}
