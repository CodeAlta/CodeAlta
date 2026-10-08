import assert from "node:assert/strict";
import test from "node:test";
import { modalDialogOpen } from "./modalDialogs";

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
