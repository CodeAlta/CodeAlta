import assert from "node:assert/strict";
import test from "node:test";
import { dismissDialogsOnOutsidePress, keptOnOutsidePress, modalDialogOpen, outsideDialog } from "./modalDialogs";

// A page as the question sees it: what a search finds, how often it is searched, and the changes an
// observer is told of, either later (its callback) or when it is asked (takeRecords).
function page() {
  const state = { open: false, searches: 0, pending: 0, observers: [] as { callback: () => void; options: MutationObserverInit | undefined }[] };
  class Observer {
    constructor(private readonly callback: () => void) { }
    observe(_target: unknown, options?: MutationObserverInit) { state.observers.push({ callback: this.callback, options }); }
    takeRecords() { const records = Array.from({ length: state.pending }, () => ({})); state.pending = 0; return records; }
  }
  const document = { querySelector: (selector: string) => { state.searches++; assert.match(selector, /dialog\[open\]/); return state.open ? {} : null; } };
  return { state, document, Observer };
}

function withPage(run: (state: ReturnType<typeof page>["state"]) => void, observer = true) {
  const { state, document, Observer } = page();
  const globals = globalThis as { document?: unknown; MutationObserver?: unknown };
  const saved = { document: globals.document, observer: globals.MutationObserver };
  globals.document = document;
  globals.MutationObserver = observer ? Observer : undefined;
  try { run(state); } finally { globals.document = saved.document; globals.MutationObserver = saved.observer; }
}

test("the page is searched for a modal dialog once, and again only after it changed", () => withPage(state => {
  assert.equal(modalDialogOpen(), false);
  for (let asked = 0; asked < 8; asked++) assert.equal(modalDialogOpen(), false);
  assert.equal(state.searches, 1);
  // One observer of the whole page: elements added or removed, and the attributes that make a modal dialog.
  assert.equal(state.observers.length, 1);
  assert.deepEqual(state.observers[0].options, { subtree: true, childList: true, attributes: true, attributeFilter: ["open", "role", "aria-modal"] });
  // A dialog opened in this very task is seen at once: its change is taken from the observer.
  state.open = true; state.pending = 1;
  assert.equal(modalDialogOpen(), true);
  assert.equal(modalDialogOpen(), true);
  assert.equal(state.searches, 2);
  // A change the observer was told of later is seen too.
  state.open = false; state.observers[0].callback();
  assert.equal(modalDialogOpen(), false);
  assert.equal(state.searches, 3);
}));

test("without an observer every question searches the page", () => withPage(state => {
  assert.equal(modalDialogOpen(), false);
  state.open = true;
  assert.equal(modalDialogOpen(), true);
  assert.equal(state.searches, 2);
  assert.equal(state.observers.length, 0);
}, false));

type Box = { left: number; top: number; right: number; bottom: number };
type Shape = { box: Box; open?: boolean; kept?: boolean; popover?: boolean; surface?: Box; refuses?: boolean };
const centered: Box = { left: 100, top: 100, right: 300, bottom: 300 };
const viewport: Box = { left: 0, top: 0, right: 800, bottom: 600 };

// A dialog as a press sees it: where it is, what it holds, and what it does with the request to close.
class Dialog {
  cancels = 0; closes = 0;
  constructor(readonly shape: Shape) { }
  matches(selector: string) {
    if (selector === "dialog[open]") return this.shape.open !== false;
    assert.equal(selector, `[data-outside-press="${keptOnOutsidePress["data-outside-press"]}"]`);
    return !!this.shape.kept;
  }
  querySelector(selector: string) {
    if (selector === ":scope > .app-window") return this.shape.surface ? { getBoundingClientRect: () => this.shape.surface } : null;
    assert.match(selector, /\.bp6-overlay-open \.bp6-popover:not\(\.bp6-tooltip\)/);
    return this.shape.popover ? {} : null;
  }
  getBoundingClientRect() { return this.shape.box; }
  dispatchEvent(event: Event) { assert.equal(event.type, "cancel"); assert.equal(event.cancelable, true); this.cancels++; return !this.shape.refuses; }
  close() { this.closes++; }
}

// The listeners of a page, and the presses a test makes in it.
function pressing(run: (press: (target: unknown, x: number, y: number, button?: number) => void, click: (target: unknown, x: number, y: number) => void, remove: () => void, listeners: Map<string, unknown>) => void) {
  const globals = globalThis as { Element?: unknown };
  const saved = globals.Element;
  globals.Element = Dialog;
  const listeners = new Map<string, (event: unknown) => void>();
  const root = { addEventListener: (type: string, listener: (event: unknown) => void, capture: boolean) => { assert.equal(capture, true); listeners.set(type, listener); },
    removeEventListener: (type: string, listener: unknown, capture: boolean) => { assert.equal(capture, true); assert.equal(listeners.get(type), listener); listeners.delete(type); } };
  try {
    const remove = dismissDialogsOnOutsidePress(root as unknown as Document);
    run((target, x, y, button = 0) => listeners.get("pointerdown")!({ target, clientX: x, clientY: y, button }),
      (target, x, y) => listeners.get("click")!({ target, clientX: x, clientY: y, button: 0 }), remove, listeners);
  } finally { globals.Element = saved; }
}

test("a press is outside a dialog on its backdrop, or beside the window a covering dialog holds", () => {
  const outside = (dialog: Dialog, target: unknown, x: number, y: number) => outsideDialog(dialog as unknown as Element, target as EventTarget, x, y);
  const dialog = new Dialog({ box: centered });
  assert.equal(outside(dialog, dialog, 50, 50), true);
  assert.equal(outside(dialog, dialog, 350, 200), true);
  // The padding of a dialog is the dialog: a press there is not outside it.
  assert.equal(outside(dialog, dialog, 101, 101), false);
  assert.equal(outside(dialog, {}, 50, 50), false, "A press on something the dialog shows has another target.");
  const layer = new Dialog({ box: viewport, surface: centered });
  assert.equal(outside(layer, layer, 50, 50), true);
  assert.equal(outside(layer, layer, 200, 200), false);
});

test("a press outside a dialog dismisses it as Escape does, when it began and ended there", () => pressing((press, click, remove, listeners) => {
  const dialog = new Dialog({ box: centered });
  press(dialog, 20, 20); click(dialog, 20, 20);
  assert.deepEqual([dialog.cancels, dialog.closes], [1, 1]);
  // A selection or a window dragged out of the dialog ends outside it without having begun there.
  press({}, 200, 200); click(dialog, 20, 20);
  press(dialog, 200, 200); click(dialog, 20, 20);
  // A press that began outside and ended on the dialog, or with another button, is not one either.
  press(dialog, 20, 20); click(dialog, 200, 200);
  press(dialog, 20, 20, 2); click(dialog, 20, 20);
  assert.deepEqual([dialog.cancels, dialog.closes], [1, 1]);
  // One press dismisses one dialog: the release alone is nothing.
  click(dialog, 20, 20);
  assert.deepEqual([dialog.cancels, dialog.closes], [1, 1]);
  // A dialog that handles the request itself, as its Escape does, is not closed a second time.
  const handled = new Dialog({ box: viewport, surface: centered, refuses: true });
  press(handled, 20, 20); click(handled, 20, 20);
  assert.deepEqual([handled.cancels, handled.closes], [1, 0]);
  remove();
  assert.equal(listeners.size, 0);
}));

test("a dialog that asks something stays, and an open menu or popover is closed alone", () => pressing((press, click) => {
  const asking = new Dialog({ box: centered, kept: true });
  press(asking, 20, 20); click(asking, 20, 20);
  assert.deepEqual([asking.cancels, asking.closes], [0, 0]);
  const shape: Shape = { box: viewport, surface: centered, popover: true };
  const menu = new Dialog(shape);
  press(menu, 20, 20); click(menu, 20, 20);
  assert.deepEqual([menu.cancels, menu.closes], [0, 0]);
  // The menu is gone: the next press is for the dialog.
  shape.popover = false;
  press(menu, 20, 20); click(menu, 20, 20);
  assert.deepEqual([menu.cancels, menu.closes], [1, 1]);
  const closed = new Dialog({ box: centered, open: false });
  press(closed, 20, 20); click(closed, 20, 20);
  assert.equal(closed.cancels, 0);
}));
