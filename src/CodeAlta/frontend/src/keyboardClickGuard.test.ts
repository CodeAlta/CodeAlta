import assert from "node:assert/strict";
import test from "node:test";
import { createKeyboardClickGuard, installKeyboardClickGuard } from "./keyboardClickGuard";

test("a release only presses the control that received the key-down", () => {
  const guard = createKeyboardClickGuard();
  const palette = new EventTarget(), closeButton = new EventTarget();
  guard.down("Enter", palette);
  assert.equal(guard.strayRelease("Enter", closeButton), true, "focus moved into the window the key opened");
  guard.down("Enter", closeButton);
  assert.equal(guard.strayRelease("Enter", closeButton), false);
  assert.equal(guard.strayRelease("Enter", closeButton), true, "a second release has no key-down of its own");
  guard.down(" ", closeButton);
  assert.equal(guard.strayRelease("Enter", closeButton), true, "another key was pressed");
  guard.down("a", palette);
  assert.equal(guard.strayRelease("a", closeButton), false, "keys that do not activate controls are left alone");
});

test("the installed guard stops a stray release before other handlers and lets a real press through", () => {
  type Listener = (event: KeyboardEvent) => void;
  const listeners: Record<string, Listener> = {};
  installKeyboardClickGuard({ addEventListener: (type: string, listener: Listener) => { listeners[type] = listener; } } as unknown as Window);
  const first = new EventTarget(), second = new EventTarget();
  const key = (target: EventTarget) => {
    const state = { stopped: false, prevented: false };
    return { state, event: { key: "Enter", target, stopPropagation: () => { state.stopped = true; }, preventDefault: () => { state.prevented = true; } } as unknown as KeyboardEvent };
  };
  listeners.keydown(key(first).event);
  const stray = key(second);
  listeners.keyup(stray.event);
  assert.deepEqual(stray.state, { stopped: true, prevented: true });
  listeners.keydown(key(second).event);
  const press = key(second);
  listeners.keyup(press.event);
  assert.deepEqual(press.state, { stopped: false, prevented: false });
});
