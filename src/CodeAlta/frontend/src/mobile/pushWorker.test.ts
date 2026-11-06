import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";
import { runInNewContext } from "node:vm";

function worker() {
  const handlers = new Map<string, (event: unknown) => void>();
  const notifications: { title: string; options: { data: { sessionId: string }; tag: string; body: string } }[] = [];
  const opened: string[] = [], navigated: string[] = [];
  let focused = 0, closed = 0;
  const state = { windows: [] as { url: string; navigate: (url: string) => Promise<unknown> }[] };
  const client = { url: "https://desk.example/", navigate: async (url: string) => { navigated.push(url); return { focus: async () => { focused++; } }; } };
  const self = {
    location: { origin: "https://desk.example" },
    addEventListener: (name: string, handler: (event: unknown) => void) => handlers.set(name, handler),
    registration: { showNotification: async (title: string, options: typeof notifications[number]["options"]) => { notifications.push({ title, options }); } },
    clients: { matchAll: async () => state.windows, openWindow: async (url: string) => { opened.push(url); } },
  };
  runInNewContext(readFileSync(new URL("../../public/mobile/sw.js", import.meta.url), "utf8"), { self, URL });
  async function fire(name: string, data: object) {
    let done: Promise<unknown> | undefined;
    handlers.get(name)!({ ...data, waitUntil: (promise: Promise<unknown>) => { done = promise; } });
    await done;
  }
  return { notifications, opened, navigated, state, client, focused: () => focused, closed: () => closed,
    push: (data: unknown) => fire("push", { data: { json: () => data } }),
    click: (sessionId: string) => fire("notificationclick", { notification: { data: { sessionId }, close: () => { closed++; } } }) };
}

test("push wakes the worker without a page and shows completion, failure, question/review and permission notifications", async () => {
  const w = worker();
  for (const kind of ["completed", "failed", "answer", "approval"]) await w.push({ sessionId: "s-1", kind });
  assert.deepEqual(w.notifications.map(n => n.title), ["Turn complete", "Turn failed", "Your answer is needed", "Your approval is needed"]);
  assert.ok(w.notifications.every(n => n.options.data.sessionId === "s-1" && n.options.tag === "alta-session-s-1"));
  await w.push({ sessionId: "https://evil.example/", kind: "completed" });
  await w.push({ sessionId: "s-1", kind: "constructor" });
  await w.push(null);
  assert.equal(w.notifications.length, 4, "Ignore invalid payloads rather than follow arbitrary links.");
});

test("tapping a notification reuses the app window and opens the exact session, not the current session", async () => {
  const w = worker();
  w.state.windows = [w.client];
  await w.click("s-2");
  assert.deepEqual(w.navigated, ["https://desk.example/#/session/s-2"]);
  assert.equal(w.focused(), 1);
  assert.equal(w.closed(), 1);
  assert.deepEqual(w.opened, []);
});

test("tapping opens a new app window when none exists, or when an existing window closes during navigation", async () => {
  const w = worker();
  await w.click("s-1");
  w.state.windows = [{ url: "https://other.example/", navigate: async () => assert.fail("A different origin is never navigated.") },
    { url: "https://desk.example/", navigate: async () => { throw new Error("closed"); } }];
  await w.click("s-2");
  await w.click("//evil.example");
  assert.deepEqual(w.opened, ["https://desk.example/#/session/s-1", "https://desk.example/#/session/s-2"]);
});
