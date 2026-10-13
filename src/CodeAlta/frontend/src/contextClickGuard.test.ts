import assert from "node:assert/strict";
import test from "node:test";
import { createContextClickGuard, installContextClickGuard } from "./contextClickGuard";

test("the click of the press that asked for a context menu is not a click", () => {
  const guard = createContextClickGuard();
  assert.equal(guard.strayClick(0, true, 1), false, "a Ctrl+click that asked for no menu (Windows, Linux)");
  guard.down();
  guard.contextMenu(0, true);
  assert.equal(guard.strayClick(0, true, 1), true, "WebKit on macOS: the click follows the menu");
  assert.equal(guard.strayClick(0, true, 2), true, "and so does the double click of a second such press");
  assert.equal(guard.strayClick(0, true, 0), false, "Ctrl+Enter on an entry of the menu is a click of the keyboard");
  assert.equal(guard.strayClick(0, false, 1), false);
  guard.down();
  assert.equal(guard.strayClick(0, true, 1), false, "the next press is a press of its own");
  guard.down();
  guard.contextMenu(2, false);
  assert.equal(guard.strayClick(0, true, 1), false, "a right click asks with its own button, and has no click");
});

test("the installed guard stops the click after a Ctrl+click menu before other handlers", () => {
  type Listener = (event: MouseEvent) => void;
  const listeners: Record<string, Listener> = {}, captured: Record<string, unknown> = {};
  installContextClickGuard({ addEventListener: (type: string, listener: Listener, capture: unknown) => { listeners[type] = listener; captured[type] = capture; } } as unknown as Window);
  assert.deepEqual(captured, { mousedown: true, contextmenu: true, click: true, dblclick: true });
  const mouse = (ctrlKey: boolean, detail = 1) => {
    const state = { stopped: false, prevented: false };
    return { state, event: { button: 0, ctrlKey, detail, stopPropagation: () => { state.stopped = true; }, preventDefault: () => { state.prevented = true; } } as unknown as MouseEvent };
  };
  // What WebKit sends for a Ctrl+click whose menu the page shows itself.
  listeners.mousedown(mouse(true).event);
  const asking = mouse(true, 0);
  listeners.contextmenu(asking.event);
  assert.deepEqual(asking.state, { stopped: false, prevented: false }, "the menu itself is left to the row");
  const stray = mouse(true);
  listeners.click(stray.event);
  assert.deepEqual(stray.state, { stopped: true, prevented: true });
  const twice = mouse(true, 2);
  listeners.dblclick(twice.event);
  assert.deepEqual(twice.state, { stopped: true, prevented: true });
  // A plain click, then a Ctrl+click where no menu was asked.
  for (const ctrlKey of [false, true]) {
    listeners.mousedown(mouse(ctrlKey).event);
    const press = mouse(ctrlKey);
    listeners.click(press.event);
    assert.deepEqual(press.state, { stopped: false, prevented: false });
  }
});
