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

// The bundle entry is main.tsx, not a copied shell or reimplemented navigation fixture.
test("production shell settings overlay keeps the session workspace mounted and inert", { skip: !edge, timeout: 90_000 }, async () => {
  const root = await mkdtemp(join(tmpdir(), "codealta-settings-shell-"));
  let browser: ReturnType<typeof spawn> | undefined;
  let socket: WebSocket | undefined;
  try {
    await build({ entryPoints: [fileURLToPath(new URL("./main.tsx", import.meta.url))],
      outfile: join(root, "fixture.js"), bundle: true, platform: "browser", format: "iife", loader: { ".css": "empty" },
      define: { "import.meta.env.VITE_DEMO_MODE": '"false"' },
      plugins: [{ name: "isolated-bridge", setup(bundle) {
        bundle.onResolve({ filter: /^#neoastra$/ }, () => ({ path: fileURLToPath(new URL("./settingsShell.neoastra.mount.ts", import.meta.url)) }));
      } }] });
    await writeFile(join(root, "style.css"), readFileSync(fileURLToPath(new URL("./style.css", import.meta.url))));
    const page = join(root, "fixture.html");
    await writeFile(page, '<!doctype html><html><head><link rel="stylesheet" href="style.css"></head><body><div id="root"></div><script src="fixture.js"></script></body></html>');
    const profile = join(root, "profile");
    browser = spawn(edge!, ["--headless=new", "--disable-gpu", "--no-first-run", "--disable-background-networking", "--disable-extensions",
      `--user-data-dir=${profile}`, "--remote-debugging-port=0", "about:blank"], { stdio: "ignore", windowsHide: true });
    let port = "";
    for (let attempt = 0; attempt < 100 && !port; attempt++) {
      try { port = (await readFile(join(profile, "DevToolsActivePort"), "utf8")).split(/\r?\n/)[0]; }
      catch { await new Promise(resolve => setTimeout(resolve, 50)); }
    }
    assert.match(port, /^\d+$/);
    const pages: { type?: string; url?: string; webSocketDebuggerUrl?: string }[] =
      await (await fetch(`http://127.0.0.1:${port}/json/list`, { signal: AbortSignal.timeout(10_000) })).json();
    const tab = pages.find(value => value.type === "page" && value.url === "about:blank");
    assert.ok(tab?.webSocketDebuggerUrl);
    socket = new WebSocket(tab.webSocketDebuggerUrl);
    await new Promise<void>((resolve, reject) => {
      socket!.addEventListener("open", () => resolve(), { once: true });
      socket!.addEventListener("error", () => reject(new Error("test browser unavailable")), { once: true });
    });
    let sequence = 0;
    const command = (method: string, params: object = {}) => new Promise<{ result?: { value?: unknown } }>((resolve, reject) => {
      const id = ++sequence;
      const timer = setTimeout(() => reject(new Error(`browser ${method} timed out`)), 12_000);
      const reply = (event: MessageEvent) => {
        const message = JSON.parse(String(event.data)) as { id?: number; result?: { result?: { value?: unknown } }; error?: object };
        if (message.id !== id) return;
        socket!.removeEventListener("message", reply); clearTimeout(timer);
        if (message.error) reject(new Error(`browser ${method} failed`)); else resolve(message.result ?? {});
      };
      socket!.addEventListener("message", reply);
      socket!.send(JSON.stringify({ id, method, params }));
    });
    await command("Page.enable");
    await command("Emulation.setDeviceMetricsOverride", { width: 1120, height: 800, deviceScaleFactor: 1, mobile: false });
    await command("Page.navigate", { url: pathToFileURL(page).href });
    const evaluate = async (expression: string) => {
      const response = await command("Runtime.evaluate", { expression, returnByValue: true, awaitPromise: true });
      return response.result?.value;
    };
    const wait = (condition: string) => evaluate(`new Promise(resolve => { const end=Date.now()+7000; const tick=()=>{
      if (${condition}) resolve(true); else if (Date.now()>end) resolve(document.body.innerText.slice(-1200));
      else setTimeout(tick,20); }; tick(); })`);
    assert.equal(await wait("!!document.querySelector('#catalog-prompt') && !!document.querySelector('.project-rail .icon-label-button')"), true);
    assert.equal(await evaluate("!!document.querySelector('.topnav, #open-provider-configuration')"), false);
    await evaluate(`(() => { const input=document.querySelector('#catalog-prompt'); input.focus();
      Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype,'value').set.call(input,'Private local draft');
      input.dispatchEvent(new Event('input',{bubbles:true})); window.originalComposer=input;
      window.originalWorkspace=document.querySelector('.workspace-shell');
      window.originalTimeline=document.querySelector('.timeline-scroll'); })()`);
    assert.equal(await wait("document.querySelector('#catalog-prompt').value==='Private local draft'"), true);
    await evaluate("document.querySelector('.project-rail .icon-label-button').focus(); document.querySelector('.project-rail .icon-label-button').click()");
    assert.equal(await wait("document.querySelector('.settings-dialog')?.open"), true);
    assert.equal(await evaluate("document.querySelector('.settings-dialog').getBoundingClientRect().width"), 896);
    assert.equal(await evaluate("window.originalComposer===document.querySelector('#catalog-prompt') && window.originalWorkspace===document.querySelector('.workspace-shell') && window.originalTimeline===document.querySelector('.timeline-scroll')"), true);
    assert.equal(await evaluate("document.activeElement.closest('.settings-dialog')!==null"), true);
    assert.equal(await evaluate("document.querySelector('.settings-dialog').matches(':modal')"), true);
    await command("Input.dispatchMouseEvent", { type: "mousePressed", x: 5, y: 5, button: "left", clickCount: 1 });
    await command("Input.dispatchMouseEvent", { type: "mouseReleased", x: 5, y: 5, button: "left", clickCount: 1 });
    assert.equal(await evaluate("document.querySelector('.settings-dialog')?.open && document.querySelector('#catalog-prompt').value==='Private local draft'"), true);
    assert.equal(await evaluate("(() => {const d=document.querySelector('.settings-dialog'); d.dispatchEvent(new KeyboardEvent('keydown', {key:'Escape',repeat:true,bubbles:true,cancelable:true})); return d.open})()"), true);
    assert.equal(await evaluate("(() => {const d=document.querySelector('.settings-dialog'); d.dispatchEvent(new KeyboardEvent('keydown', {key:'Escape',isComposing:true,bubbles:true,cancelable:true})); return d.open})()"), true);
    await evaluate("[...document.querySelectorAll('.settings-dialog-navigation button')].find(x=>x.textContent==='Logs').click()");
    assert.equal(await wait("document.querySelector('.settings-dialog-navigation [aria-current=\"page\"]')?.textContent==='Logs'"), true);
    assert.equal(await evaluate("window.settingsShellFixture.calls.length"), 0);
    await evaluate("[...document.querySelectorAll('.application-logs button')].find(x=>x.textContent.includes('Refresh logs')).click()");
    assert.equal(await wait("window.settingsShellFixture.calls.length===1 && document.querySelector('.application-logs')?.textContent.includes('No files were read')"), true);
    assert.equal(await evaluate("window.originalComposer===document.querySelector('#catalog-prompt') && document.querySelector('#catalog-prompt').value==='Private local draft'"), true);
    await evaluate("document.querySelector('.settings-dialog-header .quiet-button').click()");
    assert.equal(await wait("document.querySelector('.settings-dialog-navigation [aria-current=\"page\"]')?.textContent==='Overview'"), true);
    await evaluate("[...document.querySelectorAll('.settings-dialog-navigation button')].find(x=>x.textContent==='About').click()");
    assert.equal(await wait("document.querySelector('.about-dialog')?.open"), true);
    await evaluate("document.querySelector('.about-dialog [aria-label=\"Close About\"]').click()");
    assert.equal(await wait("!document.querySelector('.about-dialog') && document.querySelector('.settings-dialog')?.open"), true);
    await command("Input.dispatchKeyEvent", { type: "keyDown", key: "Escape", code: "Escape", windowsVirtualKeyCode: 27 });
    await command("Input.dispatchKeyEvent", { type: "keyUp", key: "Escape", code: "Escape", windowsVirtualKeyCode: 27 });
    assert.equal(await wait("!document.querySelector('.settings-dialog')"), true);
    assert.equal(await wait("document.activeElement===document.querySelector('.project-rail .icon-label-button') && window.originalComposer===document.querySelector('#catalog-prompt')"), true);
    await command("Input.dispatchKeyEvent", { type: "keyDown", key: ",", code: "Comma", windowsVirtualKeyCode: 188, modifiers: 2 });
    await command("Input.dispatchKeyEvent", { type: "keyUp", key: ",", code: "Comma", windowsVirtualKeyCode: 188, modifiers: 2 });
    assert.equal(await wait("document.querySelector('.settings-dialog')?.open"), true);
    assert.equal(await evaluate("document.activeElement.closest('.settings-dialog')!==null"), true);
    await evaluate("document.querySelector('[aria-label=\"Close settings\"]').click()");
    assert.equal(await wait("!document.querySelector('.settings-dialog')"), true);
    await evaluate("[...document.querySelectorAll('button')].find(x=>x.textContent.includes('Commands') && x.textContent.includes('Ctrl+P')).click()");
    assert.equal(await wait("document.querySelector('.command-palette')?.open"), true);
    await evaluate("document.querySelector('#palette-option-models').click()");
    assert.equal(await wait("document.querySelector('.settings-dialog')?.open && document.querySelector('.settings-dialog-navigation [aria-current=\"page\"]')?.textContent==='Models'"), true);
    assert.equal(await evaluate("window.originalComposer===document.querySelector('#catalog-prompt') && document.querySelector('#catalog-prompt').value==='Private local draft'"), true);
    await evaluate("document.querySelector('[aria-label=\"Close settings\"]').click()");
    assert.equal(await wait("!document.querySelector('.settings-dialog')"), true);
    await evaluate("[...document.querySelectorAll('button')].find(x=>x.textContent.includes('Commands') && x.textContent.includes('Ctrl+P')).click()");
    assert.equal(await wait("document.querySelector('.command-palette')?.open"), true);
    await evaluate("document.querySelector('#palette-option-about').click()");
    assert.equal(await wait("document.querySelector('.settings-dialog')?.open && document.querySelector('.about-dialog')?.open"), true);
    await evaluate("document.querySelector('.about-dialog [aria-label=\"Close About\"]').click()");
    assert.equal(await wait("!document.querySelector('.about-dialog') && document.querySelector('.settings-dialog')?.open"), true);
    await evaluate("document.querySelector('[aria-label=\"Close settings\"]').click()");
    assert.equal(await wait("!document.querySelector('.settings-dialog') && window.originalComposer===document.querySelector('#catalog-prompt')"), true);
    await evaluate("document.querySelector('#catalog-prompt').focus()");
    for (const [key, code, value] of [["g", "KeyG", 71], ["u", "KeyU", 85]] as const) {
      await command("Input.dispatchKeyEvent", { type: "keyDown", key, code, windowsVirtualKeyCode: value, modifiers: 2 });
      await command("Input.dispatchKeyEvent", { type: "keyUp", key, code, windowsVirtualKeyCode: value, modifiers: 2 });
    }
    assert.equal(await wait("document.querySelector('.settings-dialog')?.open && document.querySelector('.settings-dialog-navigation [aria-current=\"page\"]')?.textContent==='Overview'"), true);
    await evaluate("document.querySelector('[aria-label=\"Close settings\"]').click()");
    assert.equal(await wait("!document.querySelector('.settings-dialog') && document.activeElement===document.querySelector('#catalog-prompt')"), true);
    for (const width of [390, 1120]) for (const theme of ["dark", "light"]) {
      await command("Emulation.setDeviceMetricsOverride", { width, height: 800, deviceScaleFactor: 1, mobile: false });
      await evaluate(`document.documentElement.dataset.theme='${theme}'; document.querySelector('.project-rail .icon-label-button').click()`);
      assert.equal(await wait("document.querySelector('.settings-dialog')?.open"), true);
      assert.equal(await evaluate(`(() => {const r=document.querySelector('.settings-dialog').getBoundingClientRect();
        return r.width<=${width} && r.left>=0 && r.right<=${width} && r.height<=800 && getComputedStyle(document.querySelector('.settings-dialog')).color!=='rgba(0, 0, 0, 0)'})()`), true);
      await evaluate("document.querySelector('[aria-label=\"Close settings\"]').click()");
      assert.equal(await wait("!document.querySelector('.settings-dialog')"), true);
    }
  } finally {
    socket?.close(); browser?.kill(); await rm(root, { recursive: true, force: true, maxRetries: 6, retryDelay: 100 });
  }
});
