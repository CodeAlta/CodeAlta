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

test("mounted About settings and palette inspect only current boot identity", { skip: !edge, timeout: 60_000 }, async () => {
  const app = readFileSync(fileURLToPath(new URL("./main.tsx", import.meta.url)), "utf8");
  assert.match(app, /<AboutSettingsEntry onOpen=\{onOpenAbout\}/);
  assert.match(app, /openAboutPaletteAction\(action, paletteOrigin\.current, openAbout\)/);
  assert.match(app, /<AboutDialog status=\{status\} bootError=\{!!error\} demo=\{demoMode\} onClose=\{closeAbout\}/);
  const root = await mkdtemp(join(tmpdir(), "codealta-about-"));
  let browser: ReturnType<typeof spawn> | undefined;
  let socket: WebSocket | undefined;
  try {
    await build({ entryPoints: [fileURLToPath(new URL("./about.mount.tsx", import.meta.url))],
      outfile: join(root, "fixture.js"), bundle: true, platform: "browser", format: "iife" });
    await writeFile(join(root, "style.css"), readFileSync(fileURLToPath(new URL("./style.css", import.meta.url))));
    const page = join(root, "fixture.html");
    await writeFile(page, '<!doctype html><html><head><link rel="stylesheet" href="style.css"></head><body><div id="app"></div><script src="fixture.js"></script></body></html>');
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
    await command("Emulation.setFocusEmulationEnabled", { enabled: true });
    await command("Page.navigate", { url: pathToFileURL(page).href });
    const evaluate = async (expression: string) => (await command("Runtime.evaluate", { expression, returnByValue: true, awaitPromise: true })).result?.value;
    const wait = (condition: string) => evaluate(`new Promise(resolve=>{const end=Date.now()+7000;function check(){if(${condition})resolve(true);
      else if(Date.now()>end)resolve(document.body.innerText.slice(0,350));else setTimeout(check,20)}check()})`);
    const click = (selector: string) => evaluate(`document.querySelector(${JSON.stringify(selector)}).click()`);
    const press = async (key: string, code: string, virtual: number, modifiers = 0) => {
      await command("Input.dispatchKeyEvent", { type: "keyDown", key, code, windowsVirtualKeyCode: virtual, modifiers });
      await command("Input.dispatchKeyEvent", { type: "keyUp", key, code, windowsVirtualKeyCode: virtual, modifiers });
    };
    assert.equal(await wait("!!window.aboutFixture && !!document.querySelector('.settings-card button')"), true);
    await evaluate(`(() => { window.fixtureWrites=0;window.fixtureNetwork=0;window.fixtureClipboard=0;
      Storage.prototype.setItem=()=>{window.fixtureWrites++};window.fetch=()=>{window.fixtureNetwork++;throw Error('no network')};
      Object.defineProperty(navigator,'clipboard',{configurable:true,value:{writeText:()=>{window.fixtureClipboard++}}}); })()`);
    const open = async () => { await click(".settings-card button"); assert.equal(await wait("!!document.querySelector('.about-dialog[open]')"), true); };
    await evaluate("document.querySelector('.settings-card button').focus()");
    await open();
    assert.equal(await evaluate("document.activeElement.getAttribute('aria-label')"), "Close About");
    assert.equal(await evaluate("[...document.querySelectorAll('.about-dialog dd')].map(x=>x.textContent).join('|')"), "Fixture Desktop|2.7.9+build.123|build.123");
    assert.equal(await evaluate("document.querySelector('.about-dialog').textContent.includes('Update checks, downloads and installation are not supported')"), true);
    await click("#commands");
    assert.equal(await evaluate("!!document.querySelector('.command-palette')"), false);
    await evaluate("document.querySelector('.about-dialog header button').dispatchEvent(new KeyboardEvent('keydown',{key:'Escape',isComposing:true,bubbles:true,cancelable:true}))");
    assert.equal(await evaluate("!!document.querySelector('.about-dialog[open]')"), true);
    await press("Tab", "Tab", 9);
    assert.equal(await evaluate("document.querySelector('.about-dialog').contains(document.activeElement)"), true);
    await press("Escape", "Escape", 27);
    assert.equal(await wait("!document.querySelector('.about-dialog')"), true);
    assert.equal(await wait("document.activeElement?.textContent==='Open About'"), true);
    await press(" ", "Space", 32);
    assert.equal(await wait("!!document.querySelector('.about-dialog[open]')"), true);
    // Hold the scheduled close restoration so a newer, explicit focus move can
    // occur before it. The callback must not steal that focus or the palette origin.
    await evaluate(`(() => {window.heldFocusFrames=[];const original=requestAnimationFrame;
      window.requestAnimationFrame=callback=>{window.heldFocusFrames.push(callback);return -window.heldFocusFrames.length};
      window.releaseFocusFrames=()=>{window.requestAnimationFrame=original;const frames=window.heldFocusFrames.splice(0);for(const frame of frames)frame(performance.now())};})()`);
    await press("Escape", "Escape", 27);
    assert.equal(await wait("!document.querySelector('.about-dialog')"), true);
    assert.equal(await evaluate("window.heldFocusFrames.length"), 1);
    const restored = await evaluate(`(() => {document.querySelector('#commands').focus();const before=document.activeElement.id;
      window.releaseFocusFrames();return {before,after:document.activeElement.id}})()`) as {before:string;after:string};
    assert.equal(restored.before, "commands");
    assert.equal(restored.after, "commands", JSON.stringify(restored));
    await press("p", "KeyP", 80, 2);
    assert.equal(await evaluate("window.aboutFixture.paletteOrigin()?.id"), "commands");
    assert.equal(await wait("!!document.querySelector('.command-palette[open]')"), true);
    await command("Input.insertText", { text: "About" });
    assert.equal(await wait("document.querySelectorAll('#palette-results [role=option]').length===1"), true);
    await press("Enter", "Enter", 13);
    assert.equal(await wait("!!document.querySelector('.about-dialog[open]') && !document.querySelector('.command-palette')"), true);
    await click(".about-dialog footer button");
    assert.equal(await wait("document.activeElement?.id==='commands'"), true);
    // A close callback that runs after the next palette opens cannot move focus
    // out of that modal, even when its original trigger remains connected.
    await open();
    await evaluate(`(() => {window.heldFocusFrames=[];const original=requestAnimationFrame;
      window.requestAnimationFrame=callback=>{window.heldFocusFrames.push(callback);return -window.heldFocusFrames.length};
      window.releaseFocusFrames=()=>{window.requestAnimationFrame=original;const frames=window.heldFocusFrames.splice(0);for(const frame of frames)frame(performance.now())};})()`);
    await press("Escape", "Escape", 27);
    assert.equal(await wait("!document.querySelector('.about-dialog')"), true);
    assert.equal(await evaluate("window.heldFocusFrames.length"), 1);
    await evaluate("document.querySelector('#commands').focus()");
    await press("p", "KeyP", 80, 2);
    assert.equal(await wait("!!document.querySelector('.command-palette[open]')"), true);
    assert.equal(await evaluate("window.aboutFixture.paletteOrigin()?.id"), "commands");
    const paletteFocus = await evaluate("document.activeElement?.outerHTML");
    assert.equal(await evaluate("document.querySelector('.command-palette').contains(document.activeElement)"), true);
    await evaluate("window.releaseFocusFrames()");
    assert.equal(await evaluate("document.activeElement?.outerHTML"), paletteFocus);
    await press("Escape", "Escape", 27);
    assert.equal(await wait("document.activeElement?.id==='commands'"), true);
    await click(".settings-card button");
    await evaluate("window.aboutFixture.setStatus({...window.aboutFixture.product,productName:'Next host',version:'3.0.0+fresh',hostEpoch:'epoch-two'})");
    assert.equal(await wait("document.querySelector('.about-dialog').textContent.includes('Next host')"), true);
    assert.equal(await evaluate("document.querySelector('.about-dialog').textContent.includes('Fixture Desktop')"), false);
    await evaluate("window.aboutFixture.setStatus(window.aboutFixture.catalog)");
    assert.equal(await wait("document.querySelector('.about-dialog').textContent.includes('Catalog-only desktop host')"), true);
    await evaluate("window.aboutFixture.setStatus({...window.aboutFixture.product,hostEpoch:null})");
    assert.equal(await wait("document.querySelector('.about-dialog').textContent.includes('Host mode unverified')"), true);
    await evaluate("window.aboutFixture.setStatus(undefined)");
    assert.equal(await wait("document.querySelector('.about-dialog').textContent.includes('Waiting for the desktop boot response')"), true);
    await evaluate("window.aboutFixture.setError(true)");
    assert.equal(await wait("document.querySelector('.about-dialog').textContent.includes('Desktop boot unavailable')"), true);
    await evaluate("window.aboutFixture.setStatus(window.aboutFixture.product);window.aboutFixture.setError(false);window.aboutFixture.setDemo(true)");
    assert.equal(await wait("document.querySelector('.about-dialog').textContent.includes('Browser demo')"), true);
    assert.equal(await evaluate("document.querySelector('.about-dialog').textContent.includes('Fixture Desktop')"), false);
    await evaluate("window.aboutFixture.setDemo(false);window.aboutFixture.setStatus({...window.aboutFixture.product,state:'demo',productName:'False preview',version:'local preview'})");
    assert.equal(await wait("document.querySelector('.about-dialog').textContent.includes('Browser demo')"), true);
    assert.equal(await evaluate("document.querySelector('.about-dialog').textContent.includes('False preview')"), false);
    await evaluate("window.aboutFixture.setDemo(false);window.aboutFixture.setStatus({...window.aboutFixture.product,productName:'X'.repeat(130),version:'9'.repeat(260)})");
    assert.equal(await wait("[...document.querySelectorAll('.about-dialog dd')].every(x=>x.textContent.includes('Not available'))"), true);
    await evaluate("window.aboutFixture.setStatus({...window.aboutFixture.product,productName:'Bad\\u0000name',version:'\\u202E1.0'})");
    assert.equal(await wait("[...document.querySelectorAll('.about-dialog dd')].every(x=>x.textContent.includes('Not available'))"), true);
    await evaluate("window.aboutFixture.setStatus({...window.aboutFixture.product,version:'custom+unknown'})");
    assert.equal(await wait("document.querySelector('.about-dialog dd:nth-of-type(1)')?.textContent==='Fixture Desktop'"), true);
    assert.equal(await evaluate("[...document.querySelectorAll('.about-dialog dt')].some(x=>x.textContent==='Build metadata')"), false);
    await evaluate("window.aboutFixture.setView('workspace')");
    await click(".about-dialog footer button");
    assert.equal(await wait("!document.querySelector('.about-dialog')"), true);
    assert.equal(await evaluate("document.activeElement?.textContent==='Open About'"), false);
    await click("#settings");
    await evaluate("window.aboutFixture.setStatus(window.aboutFixture.product)");
    for (const width of [390, 1120]) {
      await command("Emulation.setDeviceMetricsOverride", { width, height: 720, deviceScaleFactor: 1, mobile: false });
      const backgrounds: string[] = [];
      for (const theme of ["dark", "light"]) {
        await evaluate(`document.documentElement.dataset.theme=${JSON.stringify(theme)}`);
        await open();
        const layout = await evaluate(`({right:document.querySelector('.about-dialog').getBoundingClientRect().right,
          bottom:document.querySelector('.about-dialog footer').getBoundingClientRect().bottom,
          overflow:document.documentElement.scrollWidth,background:getComputedStyle(document.querySelector('.about-dialog')).backgroundColor})`) as {right:number;bottom:number;overflow:number;background:string};
        assert.ok(layout.right <= width+2 && layout.bottom <= 722 && layout.overflow <= width+2 && layout.background !== "rgba(0, 0, 0, 0)", `${width} ${theme}: ${JSON.stringify(layout)}`);
        backgrounds.push(layout.background);
        await press("Escape", "Escape", 27);
        assert.equal(await wait("!document.querySelector('.about-dialog')"), true);
      }
      assert.notEqual(backgrounds[0], backgrounds[1]);
    }
    assert.equal(await evaluate("window.fixtureWrites"), 0);
    assert.equal(await evaluate("window.fixtureNetwork"), 0);
    assert.equal(await evaluate("window.fixtureClipboard"), 0);
  } finally {
    socket?.close(); browser?.kill(); await rm(root, { recursive: true, force: true, maxRetries: 8, retryDelay: 100 });
  }
});
