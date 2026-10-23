// Install as an app, after kzu/evangelio.grok.me (src/lib/pwa-install.ts, src/components/pwa-install-prompt.tsx):
// standalone detection by display-mode and navigator.standalone; Chromium's beforeinstallprompt kept for an Install
// button (and menu instructions where it never fires, as Edge on Android); on iOS Safari a three-step "Add to Home
// Screen" guide pointing at the Share button; elsewhere on iOS "open it in Safari". A bar under the top bar offers it
// once, "Not now" hides it for 7 days; the download button of the top bar always opens it again.
(function () {
  const DISMISS = "alta.pwa.installDismissed", WEEK = 7 * 864e5;
  let deferred = null;
  const standalone = () => ["standalone", "minimal-ui", "window-controls-overlay", "fullscreen"].some(m => matchMedia(`(display-mode: ${m})`).matches) || !!navigator.standalone;
  const ua = navigator.userAgent;
  const iosDevice = /iPad|iPhone|iPod/.test(ua) || (navigator.platform === "MacIntel" && navigator.maxTouchPoints > 1);
  const ipad = /iPad/.test(ua) || (navigator.platform === "MacIntel" && navigator.maxTouchPoints > 1 && !/iPhone/.test(ua));
  const iosSafari = iosDevice && /WebKit/.test(ua) && !/CriOS|FxiOS|EdgiOS|OPiOS|EdgA/i.test(ua) && !/Instagram|FBAN|FBAV|WhatsApp|GSA\//i.test(ua);
  const phone = /Android|iPhone|iPad|iPod/i.test(ua) || matchMedia("(max-width: 768px), (pointer: coarse)").matches;
  const dismissed = () => { try { const at = +localStorage.getItem(DISMISS); return at && Date.now() - at < WEEK; } catch { return false; } };
  const $ = id => document.getElementById(id);

  if ("serviceWorker" in navigator) navigator.serviceWorker.register("/sw.js").catch(() => { });
  addEventListener("beforeinstallprompt", e => { e.preventDefault(); deferred = e; sync(); });
  addEventListener("appinstalled", () => { deferred = null; close(); sync(); });
  matchMedia("(display-mode: standalone)").addEventListener?.("change", sync);

  function sync() {
    const show = !standalone() && phone;
    $("install").hidden = !show;
    $("install-bar").hidden = !show || dismissed();
  }
  function close() { $("install-sheet").hidden = true; }
  function notNow() { try { localStorage.setItem(DISMISS, String(Date.now())); } catch { } close(); sync(); }
  async function install() {
    if (!iosDevice && deferred) {
      const e = deferred; deferred = null;
      try { await e.prompt(); const c = await e.userChoice; if (c.outcome === "accepted") { $("install-bar").hidden = true; } } catch { }
      return;
    }
    // A guide: iOS Safari's three steps, another iOS browser's "open in Safari", or the browser menu.
    const sheet = $("install-sheet");
    $("install-ios").hidden = !iosSafari; $("install-safari").hidden = !iosDevice || iosSafari; $("install-menu").hidden = iosDevice;
    $("install-share-where").textContent = ipad ? "Top right" : "At the bottom: the square with the arrow";
    sheet.dataset.ipad = ipad; sheet.hidden = false;
  }
  $("install").addEventListener("click", install);
  $("install-go").addEventListener("click", install);
  $("install-later").addEventListener("click", notNow);
  $("install-close").addEventListener("click", close);
  $("install-not-now").addEventListener("click", notNow);
  $("install-sheet").addEventListener("click", e => { if (e.target.id === "install-sheet") close(); });
  sync();
})();
