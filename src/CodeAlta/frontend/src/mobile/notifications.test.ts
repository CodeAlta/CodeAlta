import assert from "node:assert/strict";
import test from "node:test";
import { memoryStore } from "./api";
import { pushKeyBytes, pushSubscription, setUpNotifications } from "./notifications";

const publicKey = Buffer.from([4, ...new Array<number>(64).fill(1)]).toString("base64url");

function fixture(installed = true, permission: NotificationPermission = "granted", enabled = false) {
  const store = memoryStore(enabled ? { "alta.push.enabled": "true" } : {});
  let click: () => Promise<void> = async () => {}, resume: () => void = () => {};
  const calls: string[] = [], notes: string[] = [];
  const attributes: Record<string, string> = {};
  const button = {
    hidden: true, disabled: false, title: "",
    setAttribute: (name: string, value: string) => { attributes[name] = value; }, getAttribute: (name: string) => attributes[name],
    addEventListener: (_: string, fn: () => Promise<void>) => { click = fn; },
  };
  let current: PushSubscription | null = null;
  const manager = {
    getSubscription: async () => current,
    subscribe: async (options: PushSubscriptionOptionsInit) => {
      calls.push("subscribe");
      current = {
        options, toJSON: () => ({ endpoint: "https://fcm.googleapis.com/example", keys: { p256dh: publicKey, auth: "secret" } }),
        unsubscribe: async () => { calls.push("unsubscribe"); current = null; return true; },
      } as unknown as PushSubscription;
      return current;
    },
  } as PushManager;
  const registration = { pushManager: manager } as ServiceWorkerRegistration;
  const window = {
    document: { hidden: false, getElementById: () => button, addEventListener: (_: string, fn: () => void) => { resume = fn; } },
    matchMedia: () => ({ matches: installed, addEventListener: () => {} }), addEventListener: () => {},
    navigator: { serviceWorker: { ready: Promise.resolve(registration) } },
    Notification: { permission, requestPermission: () => { calls.push("permission"); return Promise.resolve(permission); } },
    PushManager: {},
  } as unknown as Window & typeof globalThis;
  const api = {
    pushKey: async () => { calls.push("key"); return { publicKey }; },
    subscribePush: async (_: PushSubscriptionJSON) => { calls.push("save"); },
    unsubscribePush: async () => { calls.push("remove"); },
  };
  return { window, store, button, attributes, calls, notes, manager, api, registration,
    start: () => setUpNotifications(window, store, api, Promise.resolve(registration), text => notes.push(text)),
    click: () => click(), resume: () => resume() };
}

test("notifications are offered only in installed apps with Web Push, and never request permission on load", () => {
  const browser = fixture(false); browser.start();
  assert.equal(browser.button.hidden, true);
  assert.deepEqual(browser.calls, []);
  const app = fixture(); app.start();
  assert.equal(app.button.hidden, false);
  assert.deepEqual(app.calls, []);
  const unsupported = fixture();
  Reflect.deleteProperty(unsupported.window, "PushManager"); unsupported.start();
  assert.equal(unsupported.button.hidden, true);
});

test("a tap requests permission before awaiting work, subscribes, and a second tap disables only this phone", async () => {
  const f = fixture(); f.start();
  const click = f.click();
  assert.deepEqual(f.calls, ["permission"], "Permission is requested in the tap, before any network or service-worker await.");
  await click;
  assert.deepEqual(f.calls, ["permission", "key", "subscribe", "save"]);
  assert.equal(f.store.get("alta.push.enabled"), "true");
  assert.equal(f.attributes["aria-pressed"], "true");
  await f.click();
  assert.deepEqual(f.calls.slice(-2), ["remove", "unsubscribe"], "Remove the host subscription before the browser subscription.");
  assert.equal(f.store.get("alta.push.enabled"), null);
  assert.equal(f.attributes["aria-pressed"], "false");
});

test("denied permission or a failed subscription does not opt in or crash the app", async () => {
  const denied = fixture(true, "denied"); denied.start(); await denied.click();
  assert.deepEqual(denied.calls, ["permission"]);
  assert.equal(denied.store.get("alta.push.enabled"), null);
  assert.match(denied.notes[0], /blocked/);
  const failed = fixture();
  failed.api.subscribePush = async () => { throw new Error("offline"); };
  failed.start(); await failed.click();
  assert.equal(failed.store.get("alta.push.enabled"), null);
  assert.equal(failed.button.disabled, false);
  assert.match(failed.notes[0], /could not be updated/);
});

test("subscriptions are reused and registered again on resume; a new host key replaces the old browser subscription", async () => {
  const f = fixture(); f.start(); await f.click();
  f.calls.length = 0;
  assert.equal(await pushSubscription(f.manager, publicKey), await f.manager.getSubscription());
  assert.deepEqual(f.calls, []);
  const next = Buffer.from([4, ...new Array<number>(64).fill(2)]).toString("base64url");
  assert.equal(pushKeyBytes(next).length, 65);
  await pushSubscription(f.manager, next);
  assert.deepEqual(f.calls, ["unsubscribe", "subscribe"]);
  f.calls.length = 0;
  f.resume();
  // Restore awaits worker readiness, configuration and storage; join it by observing the final save.
  for (let i = 0; i < 20 && !f.calls.some(call => call === "save"); i++) await Promise.resolve();
  assert.deepEqual(f.calls, ["key", "unsubscribe", "subscribe", "save"]);
  assert.equal(f.store.get("alta.push.enabled"), "true");
});
