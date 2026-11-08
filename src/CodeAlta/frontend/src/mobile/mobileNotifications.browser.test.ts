import assert from "node:assert/strict";
import test from "node:test";
import { browserExecutable } from "../browserTarget";
import { session } from "./fixtureServer";
import { openPhone, tapMenuItem } from "./phoneBrowser";

test("the installed phone app offers a bell, enables and restores push, and fits both themes and narrow screens", { skip: !browserExecutable, timeout: 90_000 }, async () => {
  const phone = await openPhone(server => { server.state.sessions = [session("s1", "Fix the parser", "p1", 0)]; }, process.env.CODEALTA_SHOTS ?? "");
  try {
    await phone.signIn();
    assert.equal(await phone.evaluate("document.getElementById('notifications').hidden"), true, "A browser tab never offers notifications.");
    // Simulate an installed app and its OS permission/push service, not a real subscription or a prompt of this machine.
    // The page, its service worker registration, UI, authenticated session reads and local storage are real.
    await phone.command("Page.addScriptToEvaluateOnNewDocument", { source: `
      Object.defineProperty(navigator, 'standalone', { value: true });
      Object.defineProperty(Notification, 'permission', { get: () => 'granted' });
      window.pushRequests = []; window.permissionRequests = 0;
      Notification.requestPermission = async () => { window.permissionRequests++; return 'granted'; };
      let subscription = null;
      PushManager.prototype.getSubscription = async () => subscription;
      PushManager.prototype.subscribe = async options => subscription = {
        options: { applicationServerKey: options.applicationServerKey.buffer },
        toJSON: () => ({ endpoint: 'https://fcm.googleapis.com/fixture', keys: { p256dh: 'fixture', auth: 'fixture' } }),
        unsubscribe: async () => { subscription = null; return true; },
      };
      const originalFetch = window.fetch.bind(window);
      window.fetch = (input, init) => {
        if (typeof input !== 'string' || !input.startsWith('/api/push')) return originalFetch(input, init);
        window.pushRequests.push(input);
        return Promise.resolve(input === '/api/push'
          ? new Response(JSON.stringify({ publicKey: '${Buffer.from([4, ...new Array<number>(64).fill(1)]).toString("base64url")}' }), { status: 200 })
          : new Response(null, { status: 204 }));
      };
    ` });
    await phone.command("Page.reload");
    await phone.waitFor("document.getElementById('notifications')?.hidden === false", "the bell in the installed app");
    assert.equal(await phone.evaluate("window.permissionRequests"), 0);
    // The bell is an item of the menu of "⋯", checked while notifications are on; a tap on it closes the menu.
    await phone.evaluate(tapMenuItem("#notifications"));
    try { await phone.waitFor("document.getElementById('notifications').getAttribute('aria-checked') === 'true'", "push enabled"); }
    catch (error) {
      assert.fail(`${error}\n${JSON.stringify(await phone.evaluate(`(async () => ({ requests: window.pushRequests, permissionRequests: window.permissionRequests,
        permission: Notification.permission, disabled: document.getElementById('notifications').disabled, enabled: localStorage.getItem('alta.push.enabled'),
        toast: document.getElementById('toast').textContent, failures: ${JSON.stringify(phone.failures)}, worker: (await navigator.serviceWorker.getRegistration())?.active?.state }))()`))}`);
    }
    assert.deepEqual(await phone.evaluate("window.pushRequests"), ["/api/push", "/api/push/subscription"]);
    assert.equal(await phone.evaluate("window.permissionRequests"), 1);
    await phone.shot("mobile-push-dark.png");
    await phone.waitFor("document.getElementById('bar-more').getAttribute('aria-expanded') === 'false'", "the menu closed by the tap");
    await phone.evaluate(tapMenuItem("[data-theme-choice=light]"));
    await phone.evaluate("document.getElementById('bar-more').click()");
    await phone.shot("mobile-push-light.png");
    for (const width of [320, 390, 900]) {
      await phone.command("Emulation.setDeviceMetricsOverride", { width, height: 650, deviceScaleFactor: 1, mobile: true });
      assert.equal(await phone.evaluate("document.documentElement.scrollWidth <= innerWidth"), true, `The menu with the bell does not cause overflow at ${width}px.`);
      assert.equal(await phone.evaluate("(() => { const box = document.getElementById('notifications').getBoundingClientRect(); return box.height >= 44 && box.right <= innerWidth; })()"), true);
    }
    await phone.evaluate("document.getElementById('bar-more').click()");
    await phone.waitFor("document.getElementById('bar-menu').hidden", "the menu closed");
    await phone.command("Page.reload");
    await phone.waitFor("window.permissionRequests === 0 && window.pushRequests?.includes('/api/push/subscription')", "subscription restored after reopening");
    assert.equal(await phone.evaluate("window.permissionRequests"), 0, "An opted-in app never prompts again on load.");
    await phone.waitFor("!document.getElementById('notifications').disabled", "restore finished");
    await phone.command("Page.bringToFront");
    await phone.evaluate("document.getElementById('bar-more').click()");
    await phone.evaluate("document.getElementById('notifications').focus()");
    assert.equal(await phone.evaluate("document.activeElement?.id"), "notifications");
    await phone.command("Input.dispatchKeyEvent", { type: "keyDown", key: " ", code: "Space", windowsVirtualKeyCode: 32 });
    await phone.command("Input.dispatchKeyEvent", { type: "keyUp", key: " ", code: "Space", windowsVirtualKeyCode: 32 });
    await phone.waitFor("document.getElementById('notifications').getAttribute('aria-checked') === 'false'", "notifications disabled by keyboard");
    assert.equal(await phone.evaluate("window.pushRequests.at(-1)"), "/api/push/unsubscribe");
    assert.deepEqual(phone.failures, []);
  } finally { await phone.close(); }
});
