import assert from "node:assert/strict";
import test from "node:test";
import { startSplitterDrag, type SplitterPress } from "./splitterPress";

function press(change: Partial<Omit<SplitterPress, "currentTarget" | "preventDefault">> = {}, capture?: () => void) {
  const seen = { prevented: false, captured: [] as number[] };
  const event: SplitterPress = {
    isPrimary: true, button: 0, ctrlKey: false, metaKey: false, pointerId: 7, ...change,
    currentTarget: { setPointerCapture: id => { capture?.(); seen.captured.push(id); } },
    preventDefault: () => { seen.prevented = true; },
  };
  return { started: startSplitterDrag(event), ...seen };
}

test("a press on a splitter prevents its default so that WebKit starts no text selection, and captures the pointer", () => {
  assert.deepEqual(press(), { started: true, prevented: true, captured: [7] });
});

test("a press a splitter does not drag with is left to the page", () => {
  for (const change of [{ button: 2 }, { button: 1 }, { isPrimary: false }, { ctrlKey: true }, { metaKey: true }])
    assert.deepEqual(press(change), { started: false, prevented: false, captured: [] }, JSON.stringify(change));
});

test("a pointer that cannot be captured still starts the drag without a selection", () => {
  const result = press({}, () => { throw new DOMException("gone", "NotFoundError"); });
  assert.deepEqual(result, { started: true, prevented: true, captured: [] });
});
