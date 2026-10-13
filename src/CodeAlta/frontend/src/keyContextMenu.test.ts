import assert from "node:assert/strict";
import test, { mock } from "node:test";
import { keyContextMenuDelay, takeKeyContextMenu } from "./keyContextMenu";

/** What the page listens to, with the listeners it has now. */
function page() {
  const listeners = new Map<string, Set<EventListener>>();
  return {
    addEventListener(type: string, listener: EventListener) { (listeners.get(type) ?? listeners.set(type, new Set()).get(type)!).add(listener); },
    removeEventListener(type: string, listener: EventListener) { listeners.get(type)?.delete(listener); },
    /** Sends an event and says whether a listener took it. */
    send(type: string) {
      let taken = false;
      const event = { preventDefault() { taken = true; }, stopPropagation() { } } as unknown as Event;
      for (const listener of [...(listeners.get(type) ?? [])]) listener(event);
      return taken;
    },
    count: () => [...listeners.values()].reduce((sum, set) => sum + set.size, 0),
  };
}

test("the request for a context menu that follows its key is taken once, also a moment after the key went up", () => {
  mock.timers.enable({ apis: ["setTimeout"] });
  try {
    // Linux asks while the key is down.
    let target = page();
    takeKeyContextMenu(target as never);
    assert.equal(target.send("contextmenu"), true, "the request of the key");
    assert.equal(target.count(), 0, "nothing is listened to any more");
    assert.equal(target.send("contextmenu"), false, "the next request is another one");

    // Windows asks a moment after the key went up.
    target = page();
    takeKeyContextMenu(target as never);
    target.send("keyup");
    mock.timers.tick(keyContextMenuDelay - 1);
    assert.equal(target.send("contextmenu"), true, "the request that follows the key going up");
    assert.equal(target.count(), 0);
  } finally { mock.timers.reset(); }
});

test("a request that does not follow the key is left alone", () => {
  mock.timers.enable({ apis: ["setTimeout"] });
  try {
    // macOS makes no request: a later one is of the pointer.
    let target = page();
    takeKeyContextMenu(target as never);
    target.send("keyup");
    mock.timers.tick(keyContextMenuDelay);
    assert.equal(target.count(), 0, "the wait ended");
    assert.equal(target.send("contextmenu"), false);

    // A press of the pointer asks for a menu of its own.
    target = page();
    takeKeyContextMenu(target as never);
    target.send("pointerdown");
    assert.equal(target.send("contextmenu"), false);
    assert.equal(target.count(), 0);

    // What stops the wait is given back.
    target = page();
    takeKeyContextMenu(target as never)();
    assert.equal(target.count(), 0);
  } finally { mock.timers.reset(); }
});
