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

test("mounted local preferences share live settings and rail owners across remounts", { skip: !edge, timeout: 90_000 }, async () => {
  const app = readFileSync(fileURLToPath(new URL("./main.tsx", import.meta.url)), "utf8");
  assert.match(app, /useWindowPreferences\(\)/);
  assert.match(app, /setSort: setProjectSort, desktopCollapsed: railState\.desktopCollapsed, setDesktopCollapsed/);
  assert.match(app, /<HTMLSelect id="project-sort" aria-label=\{t\("Sort projects"\)\} value=\{projectSort\} onChange=\{event => setProjectSort/);
  assert.match(app, /setRailState|toggleRail\(narrow\)/);
  const root = await mkdtemp(join(tmpdir(), "codealta-general-settings-"));
  let browser: ReturnType<typeof spawn> | undefined;
  let socket: WebSocket | undefined;
  try {
    await build({ entryPoints: [fileURLToPath(new URL("./generalSettings.mount.tsx", import.meta.url))],
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
    await command("Emulation.setDeviceMetricsOverride", { width: 1120, height: 800, deviceScaleFactor: 1, mobile: false });
    await command("Page.navigate", { url: pathToFileURL(page).href });
    const evaluate = async (expression: string) => {
      const response = await command("Runtime.evaluate", { expression, returnByValue: true, awaitPromise: true });
      return response.result?.value;
    };
    const wait = (condition: string) => evaluate(`new Promise(resolve => { const end=Date.now()+7000; const tick=()=>{
      if (${condition}) resolve(true); else if (Date.now()>end) resolve(document.body.innerText.slice(0,500));
      else setTimeout(tick,20); }; tick(); })`);
    const click = (selector: string) => evaluate(`document.querySelector(${JSON.stringify(selector)}).click()`);
    const change = (selector: string, value: string) => evaluate(`(() => {const el=document.querySelector(${JSON.stringify(selector)});
      Object.getOwnPropertyDescriptor(HTMLSelectElement.prototype,'value').set.call(el,${JSON.stringify(value)});
      el.dispatchEvent(new Event('change',{bubbles:true}));})()`);
    const state = () => evaluate(`({theme:document.documentElement.dataset.theme,
      sort:document.querySelector('#settings-project-sort')?.value ?? document.querySelector('#project-sort')?.value,
      collapsed:document.querySelector('.settings-checkbox input')?.checked,
      expanded:document.querySelector('button[aria-controls="project-rail"]')?.getAttribute('aria-expanded'),
      notice:[...document.querySelectorAll('[role="status"]')].map(x=>x.textContent).join(' ')})`);
    assert.equal(await wait("!!document.querySelector('#settings-project-sort')"), true);
    assert.deepEqual(await state(), { theme: "dark", sort: "name", collapsed: false, expanded: "true", notice: "" });
    await change("#settings-recent-count", "1");
    assert.equal(await evaluate("localStorage.getItem('codealta.desktop.recent-session-count.v1')"), "1");
    await evaluate("window.remountPreferences()");
    assert.equal(await wait("document.querySelector('#settings-recent-count')?.value==='1'"), true);
    await change("#settings-recent-count", "50");
    assert.equal(await evaluate("localStorage.getItem('codealta.desktop.recent-session-count.v1')"), "50");
    await evaluate("localStorage.setItem('codealta.desktop.recent-session-count.v1','51');window.remountPreferences()");
    assert.equal(await wait("document.querySelector('#settings-recent-count')?.value==='20' && document.querySelector('[role=status]')?.textContent.includes('Not overwritten')"), true);
    await change("#settings-recent-count", "20");
    await click('button[aria-pressed="false"]');
    assert.equal(await wait("document.documentElement.dataset.theme==='light'"), true);
    await change("#settings-project-sort", "recent");
    await click(".settings-checkbox input");
    assert.equal(await wait("document.querySelector('button[aria-controls=project-rail]').getAttribute('aria-expanded')==='false'"), true);
    await click("header button:first-child");
    assert.equal(await wait("!!document.querySelector('#current-selection')"), true);
    assert.equal(await evaluate("document.querySelector('#project-sort').value"), "recent");
    await click('button[aria-controls="project-rail"]');
    assert.equal(await wait("document.querySelector('#project-rail').hidden===false"), true);
    assert.equal(await evaluate("[...document.querySelectorAll('#project-list li')].map(x=>x.textContent).join(',')"), "Zeta,Alpha");
    await change("#project-sort", "name");
    assert.equal(await wait("[...document.querySelectorAll('#project-list li')].map(x=>x.textContent).join(',')==='Alpha,Zeta'"), true);
    await click("header button:first-child");
    assert.equal(await wait("document.querySelector('#settings-project-sort')?.value==='name'"), true);
    assert.deepEqual(await state(), { theme: "light", sort: "name", collapsed: false, expanded: "true", notice: "" });
    await click(".settings-checkbox input");
    await evaluate("window.remountPreferences()");
    assert.equal(await wait("document.querySelector('.settings-checkbox input')?.checked===true"), true);
    assert.deepEqual(await state(), { theme: "light", sort: "name", collapsed: true, expanded: "false", notice: "" });
    await command("Emulation.setDeviceMetricsOverride", { width: 390, height: 800, deviceScaleFactor: 1, mobile: false });
    await evaluate("new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(resolve)))");
    await click('button[aria-controls="project-rail"]');
    assert.equal(await wait("document.querySelector('button[aria-controls=project-rail]').getAttribute('aria-expanded')==='true'"), true);
    assert.equal(await evaluate("document.querySelector('.settings-checkbox input').checked"), true);
    await evaluate("window.remountPreferences()");
    assert.equal(await wait("document.querySelector('button[aria-controls=project-rail]').getAttribute('aria-expanded')==='false'"), true);
    await command("Emulation.setDeviceMetricsOverride", { width: 1120, height: 800, deviceScaleFactor: 1, mobile: false });
    await evaluate("new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(resolve)))");
    assert.equal(await wait("document.querySelector('.settings-checkbox input').checked===true"), true);
    assert.equal(await evaluate("document.querySelector('button[aria-controls=project-rail]').getAttribute('aria-expanded')"), "false");
    await evaluate("localStorage.setItem('codealta.desktop.theme.v1','purple');localStorage.setItem('codealta.desktop.projectSort.v1','bad');localStorage.setItem('codealta.desktop.projectRail.v1','???');window.remountPreferences()");
    assert.equal(await wait("document.querySelectorAll('[role=status]').length===3"), true);
    assert.deepEqual(await state(), { theme: "dark", sort: "name", collapsed: false, expanded: "true",
      notice: "Theme: invalid saved preference; using dark. Not overwritten. Project sort: invalid saved preference; using name. Not overwritten. Desktop projects: invalid saved preference; using expanded. Not overwritten." });
    assert.equal(await evaluate("localStorage.getItem('codealta.desktop.theme.v1')"), "purple");
    await evaluate(`(() => {const original=Storage.prototype.setItem; Storage.prototype.setItem=function(){throw Error('quota')}; window.failedStorageRestore=()=>{Storage.prototype.setItem=original};})()`);
    await click('button[aria-pressed="false"]');
    assert.equal(await wait("document.querySelector('[role=status]')?.textContent.includes('could not save')"), true);
    await change("#settings-project-sort", "recent"); await click(".settings-checkbox input");
    assert.equal(await wait("document.querySelectorAll('[role=status]').length===3"), true);
    assert.match(String((await state() as { notice: string }).notice), /Project sort: applied in this window, but local storage could not save/);
    await change("#settings-recent-count", "1");
    assert.equal(await wait("document.querySelector('#settings-recent-count').value==='1' && document.querySelectorAll('[role=status]').length===4"), true);
    assert.match(String((await state() as { notice: string }).notice), /Recent session count: applied in this window, but local storage could not save/);
    await evaluate("window.failedStorageRestore()");
    await evaluate(`(() => {const original=Storage.prototype.getItem; Storage.prototype.getItem=function(){throw Error('unavailable')};
      window.restoreReads=()=>{Storage.prototype.getItem=original};window.remountPreferences();})()`);
    assert.equal(await wait("document.querySelectorAll('[role=status]').length===4"), true);
    assert.match(String((await state() as { notice: string }).notice), /local storage unavailable; using dark\. Not saved/);
    await evaluate("window.restoreReads()");
    await command("Emulation.setDeviceMetricsOverride", { width: 390, height: 800, deviceScaleFactor: 1, mobile: false });
    assert.equal(await wait("document.querySelector('button[aria-controls=project-rail]').getAttribute('aria-expanded')==='false'"), true);
    await click('button[aria-controls="project-rail"]');
    assert.equal(await wait("document.querySelector('button[aria-controls=project-rail]').getAttribute('aria-expanded')==='true'"), true);
    assert.equal(await evaluate("document.querySelector('.settings-checkbox input').checked"), false);
    await evaluate("window.remountPreferences()");
    assert.equal(await wait("document.querySelector('button[aria-controls=project-rail]').getAttribute('aria-expanded')==='false'"), true);
    await command("Emulation.setDeviceMetricsOverride", { width: 1120, height: 800, deviceScaleFactor: 1, mobile: false });
    assert.equal(await wait("document.querySelector('button[aria-controls=project-rail]').getAttribute('aria-expanded')==='true'"), true);
    const press = async (key: string, code: string, virtual: number) => {
      await command("Input.dispatchKeyEvent", { type: "keyDown", key, code, windowsVirtualKeyCode: virtual });
      await command("Input.dispatchKeyEvent", { type: "keyUp", key, code, windowsVirtualKeyCode: virtual });
    };
    for (const width of [390, 1120]) {
      await command("Emulation.setDeviceMetricsOverride", { width, height: 800, deviceScaleFactor: 1, mobile: false });
      assert.equal(await wait(`window.innerWidth===${width}`), true);
      for (const theme of ["dark", "light"]) {
        if (await evaluate("document.documentElement.dataset.theme") !== theme) await click(`button[aria-pressed="false"]`);
        assert.equal(await wait(`document.documentElement.dataset.theme==='${theme}'`), true);
        const layout = await evaluate(`({page:document.documentElement.scrollWidth,inner:innerWidth,select:document.querySelector('#settings-project-sort').getBoundingClientRect().right,
          labelled:document.querySelector('label[for="settings-project-sort"]')?.textContent,
          focusable:[...document.querySelectorAll('button,select,input')].every(x=>!x.disabled && x.tabIndex>=0)})`) as {page:number;inner:number;select:number;labelled:string;focusable:boolean};
        assert.ok(layout.page <= width + 2 && layout.select <= width && layout.focusable, `${width} ${theme}: ${JSON.stringify(layout)}`);
        assert.equal(layout.labelled, "Sort projects");
        await evaluate("document.querySelector('#settings-project-sort').focus()");
        assert.equal(await evaluate("document.activeElement.id"), "settings-project-sort");
        await press("Tab", "Tab", 9);
        assert.equal(await evaluate("document.activeElement.id"), "settings-recent-count");
        const count = Number(await evaluate("document.activeElement.value"));
        await press("ArrowDown", "ArrowDown", 40);
        assert.equal(await wait(`document.querySelector('#settings-recent-count').value==='${count + 1}'`), true);
        assert.equal(await evaluate("document.querySelector('#settings-recent-count').getBoundingClientRect().right<=innerWidth"), true);
        await press("Tab", "Tab", 9);
        assert.equal(await evaluate("document.activeElement.matches('.settings-checkbox input')"), true);
        const before = await evaluate("document.activeElement.checked");
        await press(" ", "Space", 32);
        assert.equal(await wait(`document.querySelector('.settings-checkbox input').checked===${!before}`), true);
      }
    }
  } finally {
    socket?.close(); browser?.kill();
    await rm(root, { recursive: true, force: true, maxRetries: 5, retryDelay: 100 });
  }
});
