import type { createApi, KeyValueStore } from "./api";
import { installedApp } from "./install";

const enabledKey = "alta.push.enabled";

/** Decodes the uncompressed P-256 application-server key returned by the host. */
export function pushKeyBytes(key: string): Uint8Array<ArrayBuffer> {
  const binary = atob(key.replace(/-/g, "+").replace(/_/g, "/"));
  return Uint8Array.from(binary, char => char.charCodeAt(0));
}

/** Reuses a subscription, or replaces it when the host's VAPID identity changed. */
export async function pushSubscription(manager: PushManager, publicKey: string): Promise<PushSubscription> {
  const key = pushKeyBytes(publicKey);
  let subscription = await manager.getSubscription();
  const old = subscription?.options.applicationServerKey;
  if (subscription && (!old || new Uint8Array(old).length !== key.length || !new Uint8Array(old).every((byte, index) => byte === key[index]))) {
    await subscription.unsubscribe();
    subscription = null;
  }
  return subscription ?? manager.subscribe({ userVisibleOnly: true, applicationServerKey: key });
}

/** Offers push only in an installed app. Permission is requested directly from the person's tap, including on iOS. */
export function setUpNotifications(window: Window & typeof globalThis, store: KeyValueStore,
  api: Pick<ReturnType<typeof createApi>, "pushKey" | "subscribePush" | "unsubscribePush">,
  worker: Promise<ServiceWorkerRegistration | undefined> | undefined, note: (text: string) => void) {
  const button = window.document.getElementById("notifications") as HTMLButtonElement;
  let busy = false;
  const supported = () => installedApp(window) && "Notification" in window && "PushManager" in window && !!worker;
  const enabled = () => store.get(enabledKey) === "true";
  function sync() {
    button.hidden = !supported();
    button.disabled = busy;
    const active = enabled() && window.Notification?.permission === "granted";
    // An item of the menu of the top bar, checked while notifications are on.
    button.setAttribute("aria-checked", String(active));
    button.setAttribute("aria-label", active ? "Turn off notifications" : "Enable notifications");
    button.title = button.getAttribute("aria-label")!;
  }
  async function register() {
    if (!await worker) throw new Error("The app could not register notifications. Reload and try again.");
    const registration = await window.navigator.serviceWorker.ready;
    const { publicKey } = await api.pushKey();
    await api.subscribePush((await pushSubscription(registration.pushManager, publicKey)).toJSON());
  }
  async function restore() {
    sync();
    if (!supported() || !enabled() || window.Notification.permission !== "granted" || busy) return;
    busy = true; sync();
    try { await register(); }
    catch { note("Notifications could not reconnect. Check the connection and reopen the app."); }
    finally { busy = false; sync(); }
  }
  button.addEventListener("click", async () => {
    if (!supported() || busy) return;
    busy = true;
    sync();
    try {
      // This call must precede every await: Safari requires the user activation of the tap.
      const permission = !enabled() ? window.Notification.requestPermission() : null;
      if (enabled()) {
        // Remove the server's copy first, so a failed unsubscribe cannot keep sending notifications.
        await api.unsubscribePush();
        store.remove(enabledKey);
        const registration = await worker;
        await (await registration?.pushManager.getSubscription())?.unsubscribe();
        note("Notifications are off.");
      } else if (await permission === "granted") {
        await register();
        store.set(enabledKey, "true");
        note("Notifications are on for completed turns and requests that need you.");
      } else {
        note("Notifications are blocked. Allow them in this app's notification settings, then try again.");
      }
    } catch { note("Notifications could not be updated. Check the connection and try again."); }
    finally { busy = false; sync(); }
  });
  window.document.addEventListener("visibilitychange", () => { if (!window.document.hidden) void restore(); });
  window.addEventListener("appinstalled", () => { void restore(); });
  window.matchMedia("(display-mode: standalone)").addEventListener?.("change", () => { void restore(); });
  void restore();
}
