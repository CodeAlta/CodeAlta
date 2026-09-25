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

test("mounted saved-project dialog preserves exact read-only navigation and isolated folder import", { skip: !edge, timeout: 60_000 }, async () => {
  const root = await mkdtemp(join(tmpdir(), "codealta-saved-project-"));
  let browser: ReturnType<typeof spawn> | undefined;
  let socket: WebSocket | undefined;
  try {
    await build({ entryPoints: [fileURLToPath(new URL("./openProject.mount.tsx", import.meta.url))],
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
    const command = (method: string, params: object = {}) => new Promise<{ result?: { value?: string } }>((resolve, reject) => {
      const id = ++sequence;
      const timer = setTimeout(() => reject(new Error(`browser ${method} timed out`)), 12_000);
      const reply = (event: MessageEvent) => {
        const message = JSON.parse(String(event.data)) as { id?: number; result?: { result?: { value?: string } }; error?: object };
        if (message.id !== id) return;
        socket!.removeEventListener("message", reply); clearTimeout(timer);
        if (message.error) reject(new Error(`browser ${method} failed`)); else resolve(message.result ?? {});
      };
      socket!.addEventListener("message", reply);
      socket!.send(JSON.stringify({ id, method, params }));
    });
    await command("Page.enable");
    const loaded = new Promise<void>((resolve, reject) => {
      const timer = setTimeout(() => reject(new Error("page did not load")), 10_000);
      const listener = (event: MessageEvent) => {
        if ((JSON.parse(String(event.data)) as { method?: string }).method !== "Page.loadEventFired") return;
        clearTimeout(timer); socket!.removeEventListener("message", listener); resolve();
      };
      socket!.addEventListener("message", listener);
    });
    await command("Page.navigate", { url: pathToFileURL(page).href }); await loaded;
    await command("Emulation.setFocusEmulationEnabled", { enabled: true });
    const evaluate = async (expression: string) => {
      try { return (await command("Runtime.evaluate", { expression, returnByValue: true, awaitPromise: true })).result?.value; }
      catch (error) { throw new Error(`Fixture expression failed: ${expression.slice(0, 100)}`, { cause: error }); }
    };
    const press = async (key: string, code: string, virtual: number, ctrl = false) => {
      await command("Input.dispatchKeyEvent", { type: "keyDown", key, code, windowsVirtualKeyCode: virtual, modifiers: ctrl ? 2 : 0 });
      await command("Input.dispatchKeyEvent", { type: "keyUp", key, code, windowsVirtualKeyCode: virtual, modifiers: ctrl ? 2 : 0 });
    };
    const wait = async (condition: string) => evaluate(`new Promise(resolve => { const end=Date.now()+7000; const check=()=>{
      if (${condition}) resolve('ready'); else if (Date.now()>end) resolve(document.body.innerText.slice(0,500));
      else setTimeout(check,20); }; check(); })`);
    const edit = (selector: string, value: string) => evaluate(`(() => { const input=document.querySelector('${selector}');
      Object.getOwnPropertyDescriptor(input instanceof HTMLTextAreaElement ? HTMLTextAreaElement.prototype : HTMLInputElement.prototype,'value')
        .set.call(input,${JSON.stringify(value)}); input.dispatchEvent(new Event('input',{bubbles:true})); return true; })()`);
    const open = async () => {
      await evaluate("document.querySelector('button').focus(); document.querySelector('button').click()");
      assert.equal(await wait("!!document.querySelector('#saved-project-filter')"), "ready");
    };
    await evaluate("window.shellEvents=0; window.addEventListener('keydown',()=>window.shellEvents++)");
    await open();
    assert.equal(await evaluate("document.activeElement?.id"), "saved-project-filter");
    assert.equal(await evaluate("window.shellEvents"), 0);
    assert.equal(await evaluate("[...document.querySelectorAll('#saved-project-results [role=option] strong')].map(x=>x.textContent).join(',')"), "Alpha,Beta,Legacy (archived, read-only)");
    await edit("#saved-project-filter", "C:/catalog/bet");
    assert.equal(await wait("document.querySelectorAll('#saved-project-results [role=option]').length===1"), "ready");
    assert.equal(await evaluate("document.querySelector('#saved-project-results strong').textContent"), "Beta");
    await edit("#saved-project-filter", "missing");
    assert.equal(await wait("document.body.innerText.includes('No saved projects match this name or path')"), "ready");
    await edit("#saved-project-filter", "");
    assert.equal(await wait("document.querySelectorAll('#saved-project-results [role=option]').length===3"), "ready");
    await evaluate(`(() => { const input=document.querySelector('#saved-project-filter');
      input.dispatchEvent(new KeyboardEvent('keydown',{key:'Enter',isComposing:true,bubbles:true,cancelable:true}));
      const e=new KeyboardEvent('keydown',{key:'Enter',bubbles:true,cancelable:true}); Object.defineProperty(e,'keyCode',{value:229});
      input.dispatchEvent(e); return true; })()`);
    await evaluate("document.querySelector('#saved-project-filter').dispatchEvent(new KeyboardEvent('keydown',{key:'Enter',repeat:true,bubbles:true,cancelable:true}))");
    assert.equal(await evaluate("!!document.querySelector('#saved-project-filter')"), true);
    assert.equal(await evaluate("window.openProjectFixture.calls.length"), 0);
    await press("ArrowDown", "ArrowDown", 40);
    assert.equal(await evaluate("document.querySelector('#saved-project-filter').getAttribute('aria-activedescendant')"), "saved-project-1");
    assert.equal(await evaluate("document.querySelector('[aria-selected=true]').textContent.includes('Beta')"), true);
    assert.equal(await evaluate("document.activeElement?.id"), "saved-project-filter");
    await press("Enter", "Enter", 13);
    assert.equal(await wait("!document.querySelector('#saved-project-filter')"), "ready");
    assert.equal(await wait("document.activeElement?.textContent==='Open project'"), "ready");
    assert.equal(await evaluate("document.querySelector('#selection').textContent"), "two / session-two");
    assert.equal(await evaluate("document.querySelector('[aria-label=\"Session draft\"]').value"), "preserved draft");
    assert.equal(await evaluate("window.openProjectFixture.calls.length"), 0);
    await open();
    await edit("#saved-project-filter", "lega");
    assert.equal(await wait("document.querySelectorAll('#saved-project-results [role=option]').length===1"), "ready");
    await evaluate("document.querySelector('#saved-project-results button').click()");
    assert.equal(await evaluate("document.querySelector('#selection').textContent"), "old / session-old");
    assert.equal(await evaluate("window.openProjectFixture.calls.length"), 0);
    await evaluate("window.openProjectFixture.host(false)");
    await open();
    assert.equal(await wait("!document.querySelector('#project-folder-path')"), "ready");
    await edit("#saved-project-filter", "C:/catalog/alpha");
    assert.equal(await wait("document.querySelectorAll('#saved-project-results [role=option]').length===1"), "ready");
    await evaluate("document.querySelector('#saved-project-results button').focus()");
    await press("Enter", "Enter", 13); // Native focused option activation.
    assert.equal(await wait("!document.querySelector('#saved-project-filter')"), "ready");
    assert.equal(await evaluate("document.querySelector('#selection').textContent"), "one / session-one");
    assert.equal(await evaluate("window.openProjectFixture.calls.length"), 0);
    await open();
    await evaluate("window.openProjectFixture.failRefresh=true; [...document.querySelectorAll('button')].find(x=>x.textContent==='Refresh projects').click()");
    assert.equal(await wait("document.body.innerText.includes('Saved selection is paused after a failed refresh')"), "ready");
    assert.equal(await evaluate("document.querySelector('#saved-project-results button').disabled"), true);
    assert.equal(await evaluate("document.querySelector('#selection').textContent"), "one / session-one");
    await evaluate("window.openProjectFixture.failRefresh=false; [...document.querySelectorAll('button')].find(x=>x.textContent==='Refresh projects').click()");
    assert.equal(await wait("!document.querySelector('#saved-project-results button').disabled"), "ready");
    await evaluate("window.openProjectFixture.stale({...window.openProjectFixture.current,projects:[{id:'other',name:'Alpha',path:'C:/catalog/alpha',archived:false}]})");
    await evaluate("document.querySelector('#saved-project-results button').click()");
    assert.equal(await evaluate("!!document.querySelector('#saved-project-filter')"), true);
    assert.equal(await evaluate("document.querySelector('#selection').textContent"), "one / session-one");
    assert.equal(await wait("document.body.innerText.includes('no longer verified')"), "ready");
    await evaluate("window.openProjectFixture.set({...window.openProjectFixture.current,projects:[{id:'one',name:'Alpha',path:'C:/catalog/alpha',archived:false},{id:'copy',name:'Copy',path:'C:/catalog/alpha',archived:false}]})");
    assert.equal(await wait("document.querySelectorAll('#saved-project-results [role=option]').length===2"), "ready");
    await evaluate("document.querySelector('#saved-project-results button').click()");
    assert.equal(await evaluate("!!document.querySelector('#saved-project-filter')"), true);
    await evaluate("window.openProjectFixture.set({...window.openProjectFixture.current,projects:[],projectsTruncated:true})");
    assert.equal(await wait("document.body.innerText.includes('snapshot is truncated')"), "ready");
    assert.equal(await evaluate("document.body.innerText.includes('No saved projects in this snapshot')"), true);
    await evaluate("window.openProjectFixture.set(undefined)");
    assert.equal(await wait("document.body.innerText.includes('saved project list is unavailable')"), "ready");
    await evaluate("window.openProjectFixture.failRefresh=true; [...document.querySelectorAll('button')].find(x=>x.textContent==='Refresh projects').click()");
    assert.equal(await wait("document.body.innerText.includes('Could not refresh the project list')"), "ready");
    assert.equal(await evaluate("window.openProjectFixture.calls.length"), 0);
    await evaluate("window.openProjectFixture.failRefresh=false");
    await evaluate("document.querySelector('.app-dialog header button').focus()");
    assert.equal(await evaluate(`(() => { const e=new KeyboardEvent('keydown',{key:'Tab',shiftKey:true,bubbles:true,cancelable:true});
      document.activeElement.dispatchEvent(e); return document.activeElement.textContent; })()`), "Cancel");
    assert.equal(await evaluate("window.shellEvents"), 0);
    await press("Escape", "Escape", 27);
    assert.equal(await wait("!document.querySelector('#saved-project-filter')"), "ready");
    assert.equal(await wait("document.activeElement?.textContent==='Open project'"), "ready");
    await evaluate("window.openProjectFixture.set({configured:true,projects:[{id:'one',name:'Alpha',path:'C:/catalog/alpha',archived:false}],sessions:[],projectsTruncated:false,sessionsTruncated:false,displayTextTruncated:false}); window.openProjectFixture.host(true)");
    await open();
    assert.equal(await evaluate(`(() => { const e=new KeyboardEvent('keydown',{key:'Escape',isComposing:true,bubbles:true,cancelable:true});
      document.querySelector('#saved-project-filter').dispatchEvent(e); return !!document.querySelector('#saved-project-filter'); })()`), true);
    await edit("#project-folder-path", "C:/different");
    await evaluate("[...document.querySelectorAll('button')].find(x=>x.textContent==='Check folder').click()");
    assert.equal(await wait("window.openProjectFixture.calls.length===1"), "ready");
    await evaluate("document.querySelector('#saved-project-results button').click()");
    assert.equal(await evaluate("document.querySelector('#selection').textContent"), "one / session-one");
    await evaluate("window.openProjectFixture.calls[0].resolve({status:'confirmation_required',hostEpoch:'12345678-1234-1234-1234-123456789abc',requestedPath:'C:/different',projectPath:'C:/normalized',projectId:null})");
    assert.equal(await wait("!!document.querySelector('.project-import input[type=checkbox]')"), "ready");
    await evaluate("document.querySelector('.project-import input[type=checkbox]').click()");
    await evaluate("[...document.querySelectorAll('button')].find(x=>x.textContent==='Import and open folder').click()");
    assert.equal(await wait("window.openProjectFixture.calls.length===2"), "ready");
    await evaluate("[...document.querySelectorAll('button')].find(x=>x.textContent==='Cancel').click()");
    assert.equal(await wait("!document.querySelector('#saved-project-filter')"), "ready");
    await open();
    assert.equal(await wait("document.body.innerText.includes('Captured import pending')"), "ready");
    assert.equal(await evaluate("document.body.innerText.includes('12345678-1234-1234-1234-123456789abc') && document.body.innerText.includes('C:/different') && document.body.innerText.includes('C:/normalized')"), true);
    assert.equal(await evaluate("document.querySelector('#saved-project-results button').disabled"), true);
    await evaluate("document.querySelector('#saved-project-results button').click()");
    await press("Enter", "Enter", 13);
    assert.equal(await evaluate("document.querySelector('#selection').textContent"), "one / session-one");
    assert.equal(await evaluate("window.openProjectFixture.calls.length"), 2);
    await evaluate("[...document.querySelectorAll('button')].find(x=>x.textContent==='Cancel').click()");
    assert.equal(await wait("!document.querySelector('#saved-project-filter')"), "ready");
    await evaluate("window.openProjectFixture.calls[1].reject(new Error('lost result'))");
    await open();
    assert.equal(await wait("document.body.innerText.includes('Captured import uncertain')"), "ready");
    assert.equal(await evaluate("document.body.innerText.includes('no navigation, new request or retry')"), true);
    assert.equal(await evaluate("document.querySelector('#saved-project-results button').disabled"), true);
    await evaluate("[...document.querySelectorAll('button')].find(x=>x.textContent==='Refresh projects').click()");
    assert.equal(await wait("document.body.innerText.includes('snapshot is truncated')"), "ready");
    assert.equal(await evaluate("document.body.innerText.includes('Captured import uncertain')"), true);
    assert.equal(await evaluate("window.openProjectFixture.calls.filter(x=>x.confirmed).length"), 1);
    await evaluate("[...document.querySelectorAll('button')].find(x=>x.textContent==='Cancel').click()");
    assert.equal(await wait("!document.querySelector('#saved-project-filter')"), "ready");
    const backgrounds: string[] = [];
    for (const theme of ["dark", "light"]) {
      await evaluate(`document.documentElement.dataset.theme=${JSON.stringify(theme)}`);
      await command("Emulation.setDeviceMetricsOverride", { width: 365, height: 650, deviceScaleFactor: 1, mobile: false });
      await open();
      assert.equal(await evaluate("document.querySelector('.app-dialog').getBoundingClientRect().right <= innerWidth && document.documentElement.scrollWidth <= innerWidth"), true);
      assert.equal(await evaluate("document.querySelector('.app-dialog footer').getBoundingClientRect().bottom <= innerHeight && document.querySelector('#saved-project-filter').getBoundingClientRect().width > 100"), true);
      backgrounds.push((await evaluate("getComputedStyle(document.querySelector('.app-dialog')).backgroundColor"))!);
      assert.notEqual(backgrounds.at(-1), "rgba(0, 0, 0, 0)");
      await press("Escape", "Escape", 27);
      assert.equal(await wait("!document.querySelector('#saved-project-filter')"), "ready");
    }
    assert.notEqual(backgrounds[0], backgrounds[1]);
    assert.equal(await evaluate("document.querySelector('[aria-label=\"Session draft\"]').value"), "preserved draft");
    assert.equal(await evaluate("window.openProjectFixture.calls.length"), 2);
    await command("Page.navigate", { url: pathToFileURL(page).href });
    assert.equal(await wait("window.openProjectFixture?.calls.length === 0 && !!document.querySelector('button')"), "ready");
    await open();
    await edit("#project-folder-path", "C:/late");
    await evaluate("[...document.querySelectorAll('button')].find(x=>x.textContent==='Check folder').click()");
    assert.equal(await wait("window.openProjectFixture.calls.length===1"), "ready");
    await evaluate("window.openProjectFixture.calls[0].resolve({status:'confirmation_required',hostEpoch:'12345678-1234-1234-1234-123456789abc',requestedPath:'C:/late',projectPath:'C:/late',projectId:null})");
    assert.equal(await wait("!!document.querySelector('.project-import input[type=checkbox]')"), "ready");
    await evaluate("document.querySelector('.project-import input[type=checkbox]').click()");
    await evaluate("[...document.querySelectorAll('button')].find(x=>x.textContent==='Import and open folder').click()");
    assert.equal(await wait("window.openProjectFixture.calls.length===2"), "ready");
    await evaluate("[...document.querySelectorAll('button')].find(x=>x.textContent==='Cancel').click()");
    await evaluate("window.openProjectFixture.calls[1].resolve({status:'ok',hostEpoch:'12345678-1234-1234-1234-123456789abc',requestedPath:'C:/late',projectPath:'C:/late',projectId:'late-id'})");
    await open();
    assert.equal(await wait("document.body.innerText.includes('Captured import imported')"), "ready");
    assert.equal(await evaluate("document.body.innerText.includes('C:/late') && document.body.innerText.includes('late-id')"), true);
    assert.equal(await evaluate("document.querySelector('#saved-project-results button').disabled"), true);
    await evaluate("document.querySelector('#saved-project-results button').click()");
    await press("Enter", "Enter", 13);
    assert.equal(await evaluate("document.querySelector('#selection').textContent"), "one / session-one");
    assert.equal(await evaluate("window.openProjectFixture.calls.length"), 2);
  } finally {
    socket?.close(); browser?.kill(); await rm(root, { recursive: true, force: true, maxRetries: 8, retryDelay: 100 });
  }
});
