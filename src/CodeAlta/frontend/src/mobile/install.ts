import type { KeyValueStore } from "./api";
import { createSheet } from "./sheet";

// Install as an app, after kzu/evangelio.grok.me (src/lib/pwa-install.ts, src/components/pwa-install-prompt.tsx):
// standalone detection by display-mode and navigator.standalone; Chromium's beforeinstallprompt kept for an Install
// button (and menu instructions where it never fires, as Edge on Android); on iOS Safari a three-step "Add to Home
// Screen" guide pointing at the Share button; elsewhere on iOS "open it in Safari". A bar under the top bar offers it
// once, "Not now" hides it for 7 days; the download button of the top bar always opens it again.

const dismissKey = "alta.pwa.installDismissed", week = 7 * 864e5;

type InstallPrompt = Event & { prompt(): Promise<void>; userChoice: Promise<{ outcome: string }> };

/** Whether the page runs as an installed app. */
export function installedApp(window: Window): boolean {
  return ["standalone", "minimal-ui", "fullscreen", "window-controls-overlay"].some(mode => window.matchMedia(`(display-mode: ${mode})`).matches)
    || !!(window.navigator as Navigator & { standalone?: boolean }).standalone;
}

/** What the browser is, for the guide: an iOS device, an iPad, Safari on iOS, a phone. */
export function deviceOf(userAgent: string, platform: string, touchPoints: number, narrow: boolean) {
  const desktopIpad = platform === "MacIntel" && touchPoints > 1;
  const iosDevice = /iPad|iPhone|iPod/.test(userAgent) || desktopIpad;
  const ipad = /iPad/.test(userAgent) || (desktopIpad && !/iPhone/.test(userAgent));
  const iosSafari = iosDevice && /WebKit/.test(userAgent) && !/CriOS|FxiOS|EdgiOS|OPiOS|EdgA/i.test(userAgent) && !/Instagram|FBAN|FBAV|WhatsApp|GSA\//i.test(userAgent);
  const phone = /Android|iPhone|iPad|iPod/i.test(userAgent) || narrow;
  return { iosDevice, ipad, iosSafari, phone };
}

/** Sets up the Install button, the bar that offers it and the guide sheet. */
export function setUpInstall(window: Window & typeof globalThis, store: KeyValueStore) {
  const document = window.document;
  const $ = (id: string) => document.getElementById(id)!;
  const device = deviceOf(window.navigator.userAgent, window.navigator.platform, window.navigator.maxTouchPoints, window.matchMedia("(max-width: 768px), (pointer: coarse)").matches);
  const sheet = createSheet(document, $("install-sheet"));
  let deferred: InstallPrompt | null = null;
  const dismissed = () => { const at = Number(store.get(dismissKey)); return !!at && Date.now() - at < week; };

  if ("serviceWorker" in window.navigator) window.navigator.serviceWorker.register("/sw.js").catch(() => { /* no service worker: the page still runs */ });
  window.addEventListener("beforeinstallprompt", event => { event.preventDefault(); deferred = event as InstallPrompt; sync(); });
  window.addEventListener("appinstalled", () => { deferred = null; sheet.close(); sync(); });
  window.matchMedia("(display-mode: standalone)").addEventListener?.("change", sync);

  function sync() {
    const show = !installedApp(window) && device.phone;
    $("install").hidden = !show;
    $("install-bar").hidden = !show || dismissed();
  }
  function notNow() { store.set(dismissKey, String(Date.now())); sheet.close(); sync(); }
  async function install() {
    if (!device.iosDevice && deferred) {
      const prompt = deferred;
      deferred = null;
      try { await prompt.prompt(); if ((await prompt.userChoice).outcome === "accepted") $("install-bar").hidden = true; } catch { /* the browser refused to ask */ }
      return;
    }
    // A guide: iOS Safari's three steps, another iOS browser's "open in Safari", or the browser menu.
    $("install-ios").hidden = !device.iosSafari;
    $("install-safari").hidden = !device.iosDevice || device.iosSafari;
    $("install-menu").hidden = device.iosDevice;
    $("install-share-where").textContent = device.ipad ? "Top right" : "At the bottom: the square with the arrow";
    $("install-sheet").dataset.ipad = String(device.ipad);
    sheet.open($("install-close"));
  }
  $("install").addEventListener("click", install);
  $("install-go").addEventListener("click", install);
  $("install-later").addEventListener("click", notNow);
  $("install-close").addEventListener("click", () => sheet.close());
  $("install-not-now").addEventListener("click", notNow);
  sync();
}
