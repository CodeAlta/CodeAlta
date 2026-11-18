import assert from "node:assert/strict";
import test from "node:test";
import { createKeptPanes, inHiddenPane } from "./keptPanes";

// What the panes read and write of the document, without a document: the pane of the dock and the content of a tab in it.
function tab(width = 800, height = 600) {
  const attributes = new Map<string, string>();
  const animation = { playState: "running", pause() { this.playState = "paused"; }, play() { this.playState = "running"; } };
  let focused: { blurred: boolean; blur: () => void } | null = null;
  const text = {}, selection = { anchorNode: null as object | null, removeAllRanges() { this.anchorNode = null; } };
  const pane = { dataset: {} as Record<string, string | undefined>, style: { display: "" }, isConnected: true,
    setAttribute: (name: string, value: string) => { attributes.set(name, value); }, removeAttribute: (name: string) => { attributes.delete(name); },
    contains: (element: unknown) => element === focused || element === text, getAnimations: () => [animation] };
  const content = { closest: () => pane, style: { width: "", height: "" }, offsetWidth: width, offsetHeight: height,
    ownerDocument: { get activeElement() { return focused; }, getSelection: () => selection } };
  return { pane, content: content as unknown as HTMLElement, attributes, animation, selection, select() { selection.anchorNode = text; },
    focus() { focused = { blurred: false, blur() { this.blurred = true; } }; return focused; },
    hide() { pane.style.display = "none"; }, show() { pane.style.display = ""; } };
}

test("a pane that was shown is kept when hidden: its size is held and what has the keyboard in it gives it up", () => {
  const kept = createKeptPanes(3, 60_000), one = tab();
  kept.hidden("one", one.content);
  assert.equal(one.pane.dataset.keptPane, undefined, "a pane never shown is not kept");
  assert.equal(one.content.style.width, "");

  kept.shown("one", one.content);
  assert.equal(one.pane.dataset.keptPane, "");
  const prompt = one.focus();
  one.select();
  one.hide();
  kept.hidden("one", one.content);
  assert.deepEqual([one.content.style.width, one.content.style.height], ["800px", "600px"]);
  assert.equal(prompt.blurred, true);
  assert.equal(one.selection.anchorNode, null, "the text selected in it is no longer selected");
  assert.equal(inHiddenPane({ closest: () => one.pane } as unknown as Element), true);
  // Nothing more until the pane has stayed hidden a moment: going back and forth pays for none of it.
  assert.equal(one.attributes.get("aria-hidden"), undefined);
  assert.equal(one.animation.playState, "running");
  kept.dispose();
});

test("a pane that stays hidden is hidden from assistive technology and its animations pause, until it is shown", () => {
  const kept = createKeptPanes(3, 60_000), one = tab();
  kept.shown("one", one.content);
  one.hide(); kept.hidden("one", one.content);
  kept.settle();
  assert.equal(one.attributes.get("aria-hidden"), "true");
  assert.equal(one.animation.playState, "paused");

  one.show(); kept.shown("one", one.content);
  assert.equal(one.attributes.has("aria-hidden"), false);
  assert.equal(one.animation.playState, "running");
  assert.deepEqual([one.content.style.width, one.content.style.height], ["", ""]);
  assert.equal(inHiddenPane({ closest: () => one.pane } as unknown as Element), false);
  kept.dispose();
});

test("only the panes hidden last are kept: the others are hidden as the dock hides them", () => {
  const kept = createKeptPanes(2, 60_000);
  const tabs = ["a", "b", "c", "d"].map(id => ({ id, ...tab() }));
  for (const value of tabs) { kept.shown(value.id, value.content); value.hide(); kept.hidden(value.id, value.content); }
  // Until the panes have stayed hidden a moment, all are still laid out.
  assert.deepEqual(tabs.map(value => value.pane.dataset.keptPane), ["", "", "", ""]);
  kept.settle();
  assert.deepEqual(tabs.map(value => value.pane.dataset.keptPane), [undefined, undefined, "", ""]);
  assert.deepEqual(tabs.map(value => value.content.style.width), ["", "", "800px", "800px"]);
  assert.deepEqual(tabs.map(value => value.attributes.get("aria-hidden")), [undefined, undefined, "true", "true"]);

  // Shown again, a pane that was let go is kept again at its next turn.
  tabs[0].show(); kept.shown("a", tabs[0].content);
  tabs[0].hide(); kept.hidden("a", tabs[0].content);
  kept.settle();
  assert.deepEqual(tabs.map(value => value.pane.dataset.keptPane), ["", undefined, undefined, ""]);

  // A closed tab leaves its place.
  kept.gone("d");
  tabs[1].show(); kept.shown("b", tabs[1].content);
  tabs[1].hide(); kept.hidden("b", tabs[1].content);
  kept.settle();
  assert.deepEqual([tabs[0].pane.dataset.keptPane, tabs[1].pane.dataset.keptPane], ["", ""]);
  kept.dispose();
});
