// The pointer of the browser tests: a press goes where the element is once it holds still, as a person's does.

/** A command of the DevTools connection of a browser test. */
export type DevToolsCommand = (method: string, params?: object) => Promise<Record<string, any>>;

/** How an element is pressed. */
export type PressOptions = Readonly<{
  /** Presses one after the other: two make a double click. */
  times?: number;
  button?: "left" | "right";
  /** How the element is scrolled into view before it is measured. */
  block?: ScrollLogicalPosition;
  /** How long the element may take to appear, hold still and be reachable. */
  timeoutMs?: number;
}>;

/**
 * A page expression that waits, frame after frame, until the element that `element` (a page expression) gives keeps
 * the same rectangle over two frames and is not inside a Blueprint transition that is still entering, and with
 * `reachable` until it is also what the pointer reaches at its middle. It gives that point, or after `timeoutMs` why
 * there is none.
 *
 * A popover or a dialog grows into place, and a row can give part of its width to its buttons under the pointer:
 * a point measured during such a change is beside the element once the press arrives.
 */
export function steadyPoint(element: string, block: ScrollLogicalPosition = "center", timeoutMs = 5000, reachable = true): string {
  return `(async () => {
    const find = () => (${element}) ?? null;
    const describe = node => node ? node.tagName.toLowerCase() + (node.id ? '#' + node.id : '') + (typeof node.className === 'string' && node.className.trim() ? '.' + node.className.trim().split(/\\s+/).join('.') : '') : 'nothing';
    const entering = node => { for (let at = node; at; at = at.parentElement) if ([...at.classList].some(name => /^bp6-.+-(enter|appear)(-active)?$/.test(name))) return at; return null; };
    const frame = () => new Promise(resolve => { const timer = setTimeout(resolve, 100); requestAnimationFrame(() => { clearTimeout(timer); resolve(); }); });
    const end = performance.now() + ${timeoutMs};
    let scrolled = null, last = '', why = 'nothing matches';
    for (;;) {
      const target = find();
      if (!target) { scrolled = null; last = ''; why = 'nothing matches'; }
      else {
        if (scrolled !== target) { target.scrollIntoView({ block: ${JSON.stringify(block)} }); scrolled = target; }
        const box = target.getBoundingClientRect(), shape = box.x + ',' + box.y + ' ' + box.width + 'x' + box.height, growing = entering(target);
        if (box.width <= 0 || box.height <= 0) why = describe(target) + ' has no size';
        else if (growing) why = describe(target) + ' is still entering in ' + describe(growing);
        else if (shape !== last) why = describe(target) + ' still moves (' + (last || 'first measure') + ' then ' + shape + ')';
        else {
          const x = box.x + box.width / 2, y = box.y + box.height / 2, hit = ${reachable ? "document.elementFromPoint(x, y)" : "target"};
          // The label of a control that it hides under its own drawing passes the press on to it.
          if (hit && (target.contains(hit) || [...(target.labels ?? [])].some(label => label.contains(hit)))) return { x, y };
          why = 'the middle of ' + describe(target) + ' (' + Math.round(x) + ',' + Math.round(y) + ') reaches ' + describe(hit);
        }
        last = shape;
      }
      if (performance.now() > end) return { problem: why };
      await frame();
    }
  })()`;
}

/**
 * Moves the pointer onto the element that `element` (a page expression) gives and presses it there, once it holds
 * still and is reachable (see {@link steadyPoint}); `what` names the element when it never is.
 * @throws Error when the element is missing, moving or covered after the deadline, or when the page cannot be evaluated.
 */
export async function pressElement(command: DevToolsCommand, element: string, what: string, options: PressOptions = {}): Promise<void> {
  const { times = 1, button = "left", block = "center", timeoutMs = 5000 } = options;
  const point = async (reachable: boolean) => {
    const reply = await command("Runtime.evaluate", { expression: steadyPoint(element, block, timeoutMs, reachable), returnByValue: true, awaitPromise: true });
    if (reply.exceptionDetails) throw new Error(`pressing ${what}: ${JSON.stringify(reply.exceptionDetails).slice(0, 600)}`);
    const value = reply.result?.value as { x: number; y: number } | { problem: string } | undefined;
    if (!value || "problem" in value) throw new Error(`pressing ${what}: ${value?.problem ?? "no answer from the page"}`);
    return value;
  };
  // The pointer goes onto the element once it holds still; what is drawn or takes the pointer only under it (the
  // buttons of a row) is reachable from then on, and the press goes where the element then is.
  let at = await point(false);
  await command("Input.dispatchMouseEvent", { type: "mouseMoved", x: at.x, y: at.y });
  const settled = await point(true);
  if (settled.x !== at.x || settled.y !== at.y) { at = settled; await command("Input.dispatchMouseEvent", { type: "mouseMoved", x: at.x, y: at.y }); }
  for (let count = 1; count <= times; count++) {
    await command("Input.dispatchMouseEvent", { type: "mousePressed", x: at.x, y: at.y, button, clickCount: count });
    await command("Input.dispatchMouseEvent", { type: "mouseReleased", x: at.x, y: at.y, button, clickCount: count });
  }
}
