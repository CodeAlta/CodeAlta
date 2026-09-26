import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { existsSync, readFileSync } from "node:fs";
import { mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { fileURLToPath, pathToFileURL } from "node:url";
import { build } from "esbuild";

const edge = ["C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe",
  "C:/Program Files/Microsoft/Edge/Application/msedge.exe"].find(existsSync);

test("production workspace composer separator resizes without replacing draft, pending intent or timeline follow", { skip: !edge, timeout: 90_000 }, async () => {
  const root = await mkdtemp(join(tmpdir(), "codealta-composer-resize-"));
  let browser: ReturnType<typeof spawn> | undefined;
  let socket: WebSocket | undefined;
  try {
    await build({ entryPoints: [fileURLToPath(new URL("./main.tsx", import.meta.url))], outfile: join(root, "fixture.js"),
      bundle: true, platform: "browser", format: "iife", loader: { ".css": "empty" },
      define: { "import.meta.env.VITE_DEMO_MODE": '"false"' }, plugins: [{ name: "isolated-bridge", setup(bundle) {
        bundle.onResolve({ filter: /^#neoastra$/ }, () => ({ path: fileURLToPath(new URL("./settingsShell.neoastra.mount.ts", import.meta.url)) }));
      } }] });
    await writeFile(join(root, "style.css"), readFileSync(new URL("../node_modules/flexlayout-react/style/light.css", import.meta.url), "utf8") +
      "\n" + readFileSync(new URL("./style.css", import.meta.url), "utf8"));
    const page = join(root, "fixture.html");
    await writeFile(page, '<!doctype html><html><head><link rel="stylesheet" href="style.css"></head><body><div id="root"></div><script src="fixture.js"></script></body></html>');
    browser = spawn(edge!, ["--headless=new", "--disable-gpu", "--no-first-run", "--disable-background-networking", "--disable-extensions",
      `--user-data-dir=${join(root, "profile")}`, "--remote-debugging-port=0", "about:blank"], { stdio: "ignore", windowsHide: true });
    let port = "";
    for (let attempt = 0; attempt < 100 && !port; attempt++) {
      try { port = (await readFile(join(root, "profile", "DevToolsActivePort"), "utf8")).split(/\r?\n/)[0]; }
      catch { await new Promise(resolve => setTimeout(resolve, 50)); }
    }
    assert.match(port, /^\d+$/);
    const pages: { type?: string; url?: string; webSocketDebuggerUrl?: string }[] =
      await (await fetch(`http://127.0.0.1:${port}/json/list`, { signal: AbortSignal.timeout(10_000) })).json();
    const tab = pages.find(value => value.type === "page" && value.url === "about:blank");
    assert.ok(tab?.webSocketDebuggerUrl);
    socket = new WebSocket(tab.webSocketDebuggerUrl);
    await new Promise<void>((resolve, reject) => { socket!.addEventListener("open", () => resolve(), { once: true });
      socket!.addEventListener("error", () => reject(new Error("test browser unavailable")), { once: true }); });
    let sequence = 0;
    const command = (method: string, params: object = {}) => new Promise<{ result?: { value?: unknown } }>((resolve, reject) => {
      const id = ++sequence;
      const timer = setTimeout(() => reject(new Error(`browser ${method} timed out`)), 12_000);
      const reply = (event: MessageEvent) => {
        const message = JSON.parse(String(event.data)) as { id?: number; result?: { result?: { value?: unknown } }; error?: object };
        if (message.id !== id) return;
        socket!.removeEventListener("message", reply); clearTimeout(timer);
        if (message.error) reject(new Error(`browser ${method} failed: ${JSON.stringify(message.error)}`)); else resolve(message.result ?? {});
      };
      socket!.addEventListener("message", reply);
      socket!.send(JSON.stringify({ id, method, params }));
    });
    await command("Page.enable");
    await command("Emulation.setDeviceMetricsOverride", { width: 1120, height: 800, deviceScaleFactor: 1, mobile: false });
    await command("Page.navigate", { url: pathToFileURL(page).href });
    const evaluate = async (expression: string) => (await command("Runtime.evaluate", { expression, returnByValue: true, awaitPromise: true })).result?.value;
    const wait = (condition: string) => evaluate(`new Promise(resolve=>{const end=Date.now()+7000;const tick=()=>{
      if(${condition}) resolve(true); else if(Date.now()>end) resolve(document.body.innerText.slice(-1000)); else setTimeout(tick,20);};tick();})`);
    const key = async (value: string, options: { repeat?: boolean; modifiers?: number } = {}) => {
      const params = { key: value, code: value, windowsVirtualKeyCode: value === "Home" ? 36 : value === "ArrowUp" ? 38 : 40,
        modifiers: options.modifiers ?? 0 };
      await command("Input.dispatchKeyEvent", { type: "keyDown", ...params, autoRepeat: options.repeat ?? false });
      await command("Input.dispatchKeyEvent", { type: "keyUp", ...params });
    };
    // Reboot the isolated fixture as an owned host. This is temp browser profile state only.
    await evaluate("localStorage.setItem('settingsFixtureOwned','true')");
    await command("Page.reload");
    assert.equal(await wait("!!document.querySelector('#session-prompt')"), true);
    assert.equal(await wait("document.querySelector('.composer-splitter')?.getAttribute('aria-valuenow')!=='0'"), true);
    await evaluate("window.resizeFixture = { input: document.querySelector('#session-prompt'), timeline: document.querySelector('.timeline-scroll') }; document.querySelector('.composer-splitter').focus()");
    const automatic = await evaluate("document.querySelector('.composer-region').getBoundingClientRect().height");
    const automaticEditor = await evaluate("document.querySelector('#session-prompt').clientHeight");
    assert.equal(await evaluate("(() => {const r=document.querySelector('.composer-region');return r.scrollHeight<=r.clientHeight+1 && r.querySelector('textarea').clientHeight>=48})()"), true,
      "ordinary idle composer fits without a second scrolling pane");
    assert.equal(await evaluate("document.querySelector('.composer-splitter').getAttribute('aria-orientation')"), "horizontal");
    await key("ArrowUp");
    assert.equal(await wait("!!document.querySelector('.composer-region.resized')"), true);
    assert.equal(await wait("document.querySelector('.composer-region').getBoundingClientRect().height > " + automatic), true);
    assert.equal(await wait("document.querySelector('#session-prompt').clientHeight > " + automaticEditor), true,
      "reserved room is available to the prompt editor, not just an empty panel");
    assert.equal(await evaluate("window.resizeFixture.input===document.querySelector('#session-prompt') && window.resizeFixture.timeline===document.querySelector('.timeline-scroll')"), true);
    const first = await evaluate("document.querySelector('.composer-region').getBoundingClientRect().height");
    await key("ArrowDown", { repeat: true });
    await key("ArrowUp", { modifiers: 2 });
    await evaluate("document.querySelector('.composer-splitter').dispatchEvent(new KeyboardEvent('keydown',{key:'ArrowUp',isComposing:true,bubbles:true,cancelable:true}))");
    assert.equal(await evaluate("document.querySelector('.composer-region').getBoundingClientRect().height"), first);
    await key("Home");
    assert.equal(await wait("!document.querySelector('.composer-region.resized')"), true);
    await evaluate("{const s=document.querySelector('.composer-splitter');s.dispatchEvent(new KeyboardEvent('keydown',{key:'ArrowUp',bubbles:true,cancelable:true}));s.dispatchEvent(new KeyboardEvent('keydown',{key:'ArrowUp',bubbles:true,cancelable:true}))}");
    assert.equal(await wait("document.querySelector('.composer-region').getBoundingClientRect().height>" + ((automatic as number) + 24)), true,
      "rapid keyboard increments do not lose a step before React renders");
    await evaluate("document.querySelector('.composer-splitter').dispatchEvent(new KeyboardEvent('keydown',{key:'Home',bubbles:true,cancelable:true}))");
    assert.equal(await wait("!document.querySelector('.composer-region.resized')"), true);
    // Pointer capture uses real CDP events, and release/cancel must not keep dragging.
    const handle = await evaluate("(() => {const r=document.querySelector('.composer-splitter').getBoundingClientRect();return {x:r.x+r.width/2,y:r.y+r.height/2}})()");
    const point = handle as { x: number; y: number };
    await command("Input.dispatchMouseEvent", { type: "mousePressed", x: point.x, y: point.y, button: "left", clickCount: 1 });
    await command("Input.dispatchMouseEvent", { type: "mouseMoved", x: point.x, y: point.y - 35, button: "left", buttons: 1 });
    await command("Input.dispatchMouseEvent", { type: "mouseMoved", x: point.x, y: point.y - 95, button: "left", buttons: 1 });
    await command("Input.dispatchMouseEvent", { type: "mouseReleased", x: point.x, y: point.y - 95, button: "left", clickCount: 1 });
    assert.equal(await wait("document.querySelector('.composer-region.resized')?.getBoundingClientRect().height > " + automatic), true);
    await evaluate("new Promise(resolve=>setTimeout(resolve,80))");
    const afterPointer = await evaluate("document.querySelector('.composer-region').getBoundingClientRect().height");
    assert.ok((afterPointer as number) > (automatic as number) + 40, "successive pointer moves accumulate their deltas");
    await command("Input.dispatchMouseEvent", { type: "mouseMoved", x: point.x, y: point.y - 155, button: "none", buttons: 0 });
    assert.equal(await evaluate("document.querySelector('.composer-region').getBoundingClientRect().height"), afterPointer);
    const newPoint = await evaluate("(() => {const r=document.querySelector('.composer-splitter').getBoundingClientRect();return {x:r.x+r.width/2,y:r.y+r.height/2}})()") as { x: number; y: number };
    await command("Input.dispatchMouseEvent", { type: "mousePressed", x: newPoint.x, y: newPoint.y, button: "left", clickCount: 1 });
    await evaluate("window.dispatchEvent(new Event('blur'))");
    await command("Input.dispatchMouseEvent", { type: "mouseMoved", x: newPoint.x, y: newPoint.y - 45, button: "left", buttons: 1 });
    await command("Input.dispatchMouseEvent", { type: "mouseReleased", x: newPoint.x, y: newPoint.y - 45, button: "left", clickCount: 1 });
    assert.equal(await evaluate("document.querySelector('.composer-region').getBoundingClientRect().height"), afterPointer,
      "window disconnect releases pointer drag");
    const cancelPoint = await evaluate("(() => {const r=document.querySelector('.composer-splitter').getBoundingClientRect();return {x:r.x+r.width/2,y:r.y+r.height/2}})()") as { x: number; y: number };
    await evaluate("window.resizeCaptureId=null; document.querySelector('.composer-splitter').addEventListener('gotpointercapture',e=>window.resizeCaptureId=e.pointerId,{once:true})");
    await command("Input.dispatchMouseEvent", { type: "mousePressed", x: cancelPoint.x, y: cancelPoint.y, button: "left", clickCount: 1 });
    await command("Input.dispatchMouseEvent", { type: "mouseMoved", x: cancelPoint.x, y: cancelPoint.y, button: "left", buttons: 1 });
    assert.equal(await wait("window.resizeCaptureId!==null"), true);
    await evaluate("document.querySelector('.composer-splitter').dispatchEvent(new PointerEvent('pointercancel',{pointerId:window.resizeCaptureId,bubbles:true}))");
    await command("Input.dispatchMouseEvent", { type: "mouseMoved", x: cancelPoint.x, y: cancelPoint.y - 25, button: "left", buttons: 1 });
    await command("Input.dispatchMouseEvent", { type: "mouseReleased", x: cancelPoint.x, y: cancelPoint.y - 25, button: "left", clickCount: 1 });
    assert.equal(await evaluate("document.querySelector('.composer-region').getBoundingClientRect().height"), afterPointer,
      "pointer cancellation ends the drag");
    await evaluate("document.querySelector('.composer-splitter').focus()");
    for (let i = 0; i < 60; i++) await key("ArrowUp");
    assert.equal(await evaluate("document.querySelector('.timeline-scroll').clientHeight>=28 && document.querySelector('.composer-region').scrollHeight>=document.querySelector('.composer-region').clientHeight"), true);
    await command("Emulation.setDeviceMetricsOverride", { width: 390, height: 500, deviceScaleFactor: 1, mobile: false });
    assert.equal(await wait("document.querySelector('.composer-splitter').getAttribute('aria-valuemax')<400"), true);
    assert.equal(await evaluate("document.querySelector('.timeline-scroll').clientHeight>=28 && document.querySelector('.composer-region').getBoundingClientRect().bottom<=document.querySelector('.session-workspace').getBoundingClientRect().bottom"), true);
    await evaluate("document.documentElement.dataset.theme='light'");
    assert.equal(await evaluate("getComputedStyle(document.documentElement).colorScheme==='light' && document.querySelector('.composer-splitter').getBoundingClientRect().width>100"), true);
    const small = await evaluate("document.querySelector('.composer-region').getBoundingClientRect().height");
    await command("Emulation.setDeviceMetricsOverride", { width: 1120, height: 800, deviceScaleFactor: 1, mobile: false });
    assert.equal(await wait("document.querySelector('.composer-region').getBoundingClientRect().height>" + small), true, "preferred height survives a short viewport");
    await evaluate("document.querySelector('.composer-splitter').focus()");
    assert.equal(await evaluate("getComputedStyle(document.querySelector('.composer-splitter')).cursor"), "row-resize");
    await evaluate("document.documentElement.dataset.theme='dark'; document.querySelector('[aria-label=\"Reset composer size to automatic\"]').click()");
    assert.equal(await wait("!document.querySelector('.composer-region.resized')"), true);
    assert.equal(await evaluate("document.activeElement===document.querySelector('.composer-splitter')"), true,
      "reset button restores focus to the still-mounted separator");
    // Paste a draft and keep the same DOM while Settings is open, closed and resized.
    await evaluate("document.querySelector('#session-prompt').focus()");
    await command("Input.insertText", { text: "keep pending draft" });
    assert.equal(await wait("document.querySelector('#session-prompt').value==='keep pending draft'"), true);
    const editorHeight = await evaluate("document.querySelector('#session-prompt').clientHeight");
    await command("Input.insertText", { text: "\nsecond line\nthird line" });
    assert.equal(await wait("document.querySelector('#session-prompt').clientHeight>" + editorHeight), true,
      "ordinary prompt still grows automatically with its text");
    assert.equal(await evaluate("document.querySelector('.composer-region').scrollHeight<=document.querySelector('.composer-region').clientHeight+1"), true);
    await evaluate("document.querySelector('.composer-splitter').focus()");
    await key("ArrowUp");
    await evaluate("[...document.querySelectorAll('button')].find(b=>b.textContent.includes('Settings & extensions')).click()");
    assert.equal(await wait("!!document.querySelector('.settings-dialog')"), true);
    assert.equal(await evaluate("window.resizeFixture.input===document.querySelector('#session-prompt') && document.querySelector('#session-prompt').value.startsWith('keep pending draft')"), true);
    await evaluate("document.querySelector('[aria-label=\"Close settings\"]').click()");
    assert.equal(await wait("!document.querySelector('.settings-dialog')"), true);
    // Per-session preferences are retained within this instance, but the other session starts automatic.
    const preferred = await evaluate("document.querySelector('.composer-region').getBoundingClientRect().height");
    await evaluate("[...document.querySelectorAll('.session-row button')].find(b=>b.textContent.includes('two')).click()");
    assert.equal(await wait("document.querySelector('.session-header h1')?.textContent==='two'"), true);
    assert.equal(await evaluate("!!document.querySelector('.composer-region.resized')"), false);
    await evaluate("[...document.querySelectorAll('.session-row button')].find(b=>b.textContent.includes('one')).click()");
    assert.equal(await wait("document.querySelector('.session-header h1')?.textContent==='one'"), true);
    assert.equal(await evaluate("Math.abs(document.querySelector('.composer-region').getBoundingClientRect().height-" + preferred + ")<=1"), true);
    assert.equal(await evaluate("document.querySelector('#session-prompt').value"), "keep pending draft\nsecond line\nthird line");
    // Follow layout changes only for a reader who is following; unfollow remains reading.
    await evaluate("{const timeline=document.querySelector('.timeline-scroll');const filler=document.createElement('div');filler.style.height='2400px';timeline.appendChild(filler)}");
    assert.equal(await wait("(() => {const e=document.querySelector('.timeline-scroll');return e.scrollHeight-e.clientHeight-e.scrollTop<=2})()"), true);
    await evaluate("document.querySelector('.composer-splitter').focus()");
    await key("ArrowDown");
    assert.equal(await wait("(() => {const e=document.querySelector('.timeline-scroll');return e.scrollHeight-e.clientHeight-e.scrollTop<=2})()"), true);
    await evaluate("{const e=document.querySelector('.timeline-scroll');e.dispatchEvent(new WheelEvent('wheel',{deltaY:-250,bubbles:true}));e.scrollTop=100;e.dispatchEvent(new Event('scroll',{bubbles:true}))}");
    assert.equal(await wait("!!document.querySelector('.timeline-bottom-button')"), true);
    await evaluate("document.querySelector('.composer-splitter').focus()");
    await key("ArrowUp");
    assert.equal(await evaluate("!!document.querySelector('.timeline-bottom-button') && document.querySelector('.timeline-scroll').scrollTop<500"), true);
    await evaluate("document.querySelector('.timeline-bottom-button').click()");
    assert.equal(await wait("(() => {const e=document.querySelector('.timeline-scroll');return e.scrollHeight-e.clientHeight-e.scrollTop<=2})()"), true);
    assert.equal(await evaluate("window.settingsShellFixture.sends.length"), 0, "separator never submits or steers");
    // A real pending Send keeps its exact request and disabled input throughout resize and selection.
    await evaluate("document.querySelector('.owned-session .send-button').click()");
    assert.equal(await wait("window.settingsShellFixture.sends.length===1 && document.querySelector('#session-prompt').disabled"), true);
    const pendingText = await evaluate("document.querySelector('#session-prompt').value");
    await evaluate("window.resizeFixture.pendingInput=document.querySelector('#session-prompt'); document.querySelector('.composer-splitter').focus()");
    await key("ArrowUp");
    assert.equal(await evaluate("document.querySelector('#session-prompt')===window.resizeFixture.pendingInput && document.querySelector('#session-prompt').value===" + JSON.stringify(pendingText) + " && window.settingsShellFixture.sends.length===1"), true);
    await evaluate("[...document.querySelectorAll('button')].find(b=>b.textContent.includes('Settings & extensions')).click()");
    assert.equal(await wait("!!document.querySelector('.settings-dialog')"), true);
    await evaluate("document.querySelector('[aria-label=\"Close settings\"]').click()");
    assert.equal(await evaluate("document.querySelector('#session-prompt')===window.resizeFixture.pendingInput && document.querySelector('#session-prompt').disabled"), true);
    await evaluate("[...document.querySelectorAll('.session-row button')].find(b=>b.textContent.includes('two')).click()");
    assert.equal(await wait("document.querySelector('.session-header h1')?.textContent==='two'"), true);
    await evaluate("[...document.querySelectorAll('.session-row button')].find(b=>b.textContent.includes('one')).click()");
    assert.equal(await wait("document.querySelector('.session-header h1')?.textContent==='one'"), true);
    assert.equal(await wait("document.querySelector('#session-prompt')?.disabled && document.querySelector('#session-prompt').value===" + JSON.stringify(pendingText)), true);
    assert.equal(await evaluate("window.settingsShellFixture.sends.length"), 1);
    // The same control is available in regular catalog and archived composer gates.
    await evaluate("localStorage.removeItem('settingsFixtureOwned')");
    await command("Page.reload");
    assert.equal(await wait("!!document.querySelector('#catalog-prompt')"), true);
    await evaluate("window.catalogInput=document.querySelector('#catalog-prompt'); document.querySelector('.composer-splitter').focus()");
    await key("ArrowUp");
    assert.equal(await evaluate("!!document.querySelector('.composer-region.resized') && window.catalogInput===document.querySelector('#catalog-prompt')"), true);
    await evaluate("localStorage.setItem('settingsFixtureOwned','true');localStorage.setItem('usageFixtureArchived','true')");
    await command("Page.reload");
    assert.equal(await wait("document.querySelector('#catalog-draft-status')?.textContent.includes('Archived')"), true);
    await evaluate("document.querySelector('.composer-splitter').focus()");
    await key("ArrowUp");
    assert.equal(await evaluate("!!document.querySelector('.composer-region.resized') && !!document.querySelector('#catalog-prompt')"), true);
  } finally {
    socket?.close(); browser?.kill(); await rm(root, { recursive: true, force: true, maxRetries: 8, retryDelay: 100 });
  }
});
