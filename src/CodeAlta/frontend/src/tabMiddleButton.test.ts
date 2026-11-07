import assert from "node:assert/strict";
import test from "node:test";
import { keepMiddleButtonFromTab } from "./tabMiddleButton";

function mouse(button: number, onTab: boolean) {
  let prevented = false;
  const target = { closest: (selector: string) => onTab && selector === ".tab" ? {} : null } as unknown as EventTarget;
  keepMiddleButtonFromTab({ button, target, preventDefault: () => { prevented = true; } }, ".tab");
  return prevented;
}

test("the middle button on a tab has no default action: Linux pastes nothing into the prompt when it closes the tab", () => {
  assert.equal(mouse(1, true), true);
});

test("other buttons, and the middle button off the tabs, keep their default actions", () => {
  assert.equal(mouse(0, true), false);
  assert.equal(mouse(2, true), false);
  assert.equal(mouse(1, false), false, "A middle click in the prompt still pastes there.");
  let prevented = false;
  keepMiddleButtonFromTab({ button: 1, target: null, preventDefault: () => { prevented = true; } }, ".tab");
  assert.equal(prevented, false);
});
