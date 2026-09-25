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

test("mounted workspace dispatches reminders, session info and implemented palette actions with exact guards", { skip: !edge, timeout: 60_000 }, async () => {
  const root = await mkdtemp(join(tmpdir(), "codealta-reminder-nav-"));
  let browser: ReturnType<typeof spawn> | undefined;
  let socket: WebSocket | undefined;
  try {
    await build({ entryPoints: [fileURLToPath(new URL("./reminderNavigation.mount.tsx", import.meta.url))],
      outfile: join(root, "fixture.js"), bundle: true, platform: "browser", format: "iife",
      plugins: [{ name: "isolated-choices", setup(build) {
        build.onResolve({ filter: /^#neoastra$/ }, () => ({ path: fileURLToPath(new URL("./composer.neoastra.mount.ts", import.meta.url)) }));
      } }] });
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
      socket!.addEventListener("error", () => reject(new Error("browser unavailable")), { once: true });
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
    const evaluate = async (expression: string) => (await command("Runtime.evaluate", { expression, returnByValue: true,
      awaitPromise: true })).result?.value;
    const wait = async (condition: string) => evaluate(`new Promise(resolve => { const end = Date.now() + 7000; const check = () => {
      if (${condition}) resolve('ready'); else if (Date.now() > end) resolve(document.body.innerText.slice(0, 400));
      else setTimeout(check, 20); }; check(); })`);
    const chord = `(selector='#session-prompt') => { const target = document.querySelector(selector);
      target.focus(); target.dispatchEvent(new KeyboardEvent('keydown', {key:'g', ctrlKey:true, bubbles:true, cancelable:true}));
      target.dispatchEvent(new KeyboardEvent('keydown', {key:'d', ctrlKey:true, bubbles:true, cancelable:true})); }`;
    assert.equal(await wait("document.querySelector('#session-prompt') && document.querySelector('[aria-label=\"Reminders for selected session\"]')"), "ready");
    await evaluate(`(() => { const input = document.querySelector('#session-prompt');
      Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype,'value').set.call(input,'retained draft');
      input.dispatchEvent(new Event('input',{bubbles:true})); })()`);
    await evaluate(`(${chord})()`);
    assert.equal(await wait("document.querySelector('.reminder-page')?.textContent.includes('Session: one')"), "ready");
    assert.equal(await evaluate("document.querySelector('#reminder-delay').type"), "text");
    await evaluate(`(() => { const set = (selector, text) => { const input = document.querySelector(selector);
      const type = selector === '#reminder-content' ? HTMLTextAreaElement : HTMLInputElement;
      Object.getOwnPropertyDescriptor(type.prototype,'value').set.call(input,text);
      input.dispatchEvent(new Event('input',{bubbles:true})); };
      set('#reminder-content','check this session'); set('#reminder-delay','00:05:00'); })()`);
    assert.equal(await wait("![...document.querySelectorAll('button')].find(b=>b.textContent==='Create reminder')?.disabled"), "ready");
    await evaluate("[...document.querySelectorAll('button')].find(b=>b.textContent==='Create reminder').click()");
    assert.equal(await wait("window.reminderNavigationFixture.writes.length === 1"), "ready");
    assert.equal(await evaluate("JSON.stringify(window.reminderNavigationFixture.writes[0])"),
      JSON.stringify({ expectedEpoch: "e1", sessionId: "one", content: "check this session", delaySeconds: 300, repeatCount: 1 }));
    await evaluate("window.reminderNavigationFixture.workspace()");
    assert.equal(await wait("document.querySelector('#session-prompt')?.value === 'retained draft'"), "ready");
    await evaluate(`(() => { const t=document.querySelector('#session-prompt');
      t.dispatchEvent(new KeyboardEvent('keydown',{key:'g',ctrlKey:true,isComposing:true,bubbles:true,cancelable:true}));
      t.dispatchEvent(new KeyboardEvent('keydown',{key:'d',ctrlKey:true,bubbles:true,cancelable:true})); })()`);
    assert.equal(await evaluate("!!document.querySelector('.reminder-page')"), false);
    await evaluate("window.reminderNavigationFixture.modal(true)");
    assert.equal(await wait("document.querySelector('[role=dialog] input')"), "ready");
    await evaluate(`(${chord})('[role=dialog] input')`);
    assert.equal(await evaluate("!!document.querySelector('.reminder-page')"), false);
    await evaluate("document.querySelector('[aria-label=\"Reminders for selected session\"]').click()");
    assert.equal(await evaluate("!!document.querySelector('.reminder-page')"), false);
    await evaluate(`document.querySelector('[role=dialog] input').dispatchEvent(new KeyboardEvent('keydown',{key:'Escape',bubbles:true,cancelable:true}))`);
    assert.equal(await wait("!document.querySelector('[role=dialog]')"), "ready");
    await evaluate(`(() => { const t=document.querySelector('#session-prompt'); t.focus();
      t.dispatchEvent(new KeyboardEvent('keydown',{key:'g',ctrlKey:true,bubbles:true,cancelable:true})); })()`);
    await evaluate("window.reminderNavigationFixture.session('two')");
    assert.equal(await wait("document.querySelector('#session-prompt') && !document.querySelector('.reminder-page')"), "ready");
    await evaluate(`document.querySelector('#session-prompt').dispatchEvent(new KeyboardEvent('keydown',{key:'d',ctrlKey:true,bubbles:true,cancelable:true}))`);
    assert.equal(await evaluate("!!document.querySelector('.reminder-page')"), false);
    await evaluate(`(${chord})()`);
    assert.equal(await wait("document.querySelector('.reminder-page')?.textContent.includes('Session: two')"), "ready");
    await evaluate("window.reminderNavigationFixture.workspace()");
    assert.equal(await wait("!!document.querySelector('#session-prompt')"), "ready");
    await evaluate(`(() => { const t=document.querySelector('#session-prompt'); t.focus();
      t.dispatchEvent(new KeyboardEvent('keydown',{key:'g',ctrlKey:true,bubbles:true,cancelable:true})); })()`);
    await evaluate("window.reminderNavigationFixture.setProject('other')");
    assert.equal(await wait("!!document.querySelector('[aria-label=\"Reminders for selected session\"]')"), "ready");
    await evaluate(`document.querySelector('#session-prompt').dispatchEvent(new KeyboardEvent('keydown',{key:'d',ctrlKey:true,bubbles:true,cancelable:true}))`);
    assert.equal(await evaluate("!!document.querySelector('.reminder-page')"), false);
    await evaluate("document.querySelector('[aria-label=\"Reminders for selected session\"]').click()");
    assert.equal(await wait("document.querySelector('.reminder-page')?.textContent.includes('Session: two')"), "ready");
    await evaluate("window.reminderNavigationFixture.workspace()");
    assert.equal(await wait("!!document.querySelector('#session-prompt')"), "ready");
    await evaluate(`(() => { const t=document.querySelector('#session-prompt'); t.focus();
      t.dispatchEvent(new KeyboardEvent('keydown',{key:'g',ctrlKey:true,bubbles:true,cancelable:true})); })()`);
    await evaluate("window.reminderNavigationFixture.host('e2')");
    assert.equal(await wait("!document.querySelector('[aria-label=\"Reminders for selected session\"]')"), "ready");
    await evaluate(`document.querySelector('#workspace-shell').dispatchEvent(new KeyboardEvent('keydown',{key:'d',ctrlKey:true,bubbles:true,cancelable:true}))`);
    assert.equal(await evaluate("!!document.querySelector('.reminder-page')"), false);
    assert.equal(await evaluate("window.reminderNavigationFixture.writes.length"), 1);

    // The same dispatcher and production dialog must work with no owned host or reminder trigger.
    await evaluate("window.reminderNavigationFixture.setProject('project')");
    const infoChord = `(selector='#catalog-prompt') => { const t=document.querySelector(selector); t.focus();
      t.dispatchEvent(new KeyboardEvent('keydown',{key:'g',ctrlKey:true,bubbles:true,cancelable:true}));
      return !t.dispatchEvent(new KeyboardEvent('keydown',{key:'t',ctrlKey:true,bubbles:true,cancelable:true})); }`;
    assert.equal(await wait("!!document.querySelector('#catalog-prompt')"), "ready");
    assert.equal(await evaluate(`(${infoChord})()`), true);
    assert.equal(await wait("document.querySelector('dialog[open] .session-info-fields code')?.textContent === 'two'"), "ready");
    assert.equal(await evaluate("document.querySelector('dialog[open] .session-info-fields').textContent.includes('Title two')"), true);
    assert.equal(await evaluate(`(${infoChord})()`), false);
    assert.equal(await evaluate("document.querySelectorAll('dialog[open]').length"), 1);
    await evaluate("document.querySelector('[aria-label=\"Close session info\"]').click()");
    assert.equal(await wait("!document.querySelector('dialog[open]')"), "ready");
    assert.equal(await evaluate(`(() => { const t=document.querySelector('#catalog-prompt'); t.focus();
      t.dispatchEvent(new KeyboardEvent('keydown',{key:'g',ctrlKey:true,bubbles:true,cancelable:true}));
      return t.dispatchEvent(new KeyboardEvent('keydown',{key:'d',ctrlKey:true,bubbles:true,cancelable:true})); })()`), true);
    assert.equal(await evaluate("!!document.querySelector('.reminder-page')"), false);
    await evaluate("window.reminderNavigationFixture.modal(true)");
    assert.equal(await wait("!!document.querySelector('[role=dialog] input')"), "ready");
    assert.equal(await evaluate(`(${infoChord})()`), false);
    assert.equal(await evaluate("!!document.querySelector('dialog[open]')"), false);
    await evaluate("window.reminderNavigationFixture.modal(false)");
    assert.equal(await wait("!document.querySelector('[role=dialog]')"), "ready");
    assert.equal(await evaluate(`(() => { const t=document.querySelector('#catalog-prompt'); t.focus();
      t.dispatchEvent(new KeyboardEvent('keydown',{key:'g',ctrlKey:true,bubbles:true,cancelable:true,isComposing:true}));
      return t.dispatchEvent(new KeyboardEvent('keydown',{key:'t',ctrlKey:true,bubbles:true,cancelable:true})); })()`), true);
    assert.equal(await evaluate("!!document.querySelector('dialog[open]')"), false);
    assert.equal(await evaluate(`(() => { const t=document.querySelector('#catalog-prompt'); t.focus();
      t.dispatchEvent(new KeyboardEvent('keydown',{key:'g',ctrlKey:true,bubbles:true,cancelable:true}));
      return t.dispatchEvent(new KeyboardEvent('keydown',{key:'t',ctrlKey:true,bubbles:true,cancelable:true,keyCode:229})); })()`), true);
    assert.equal(await evaluate("!!document.querySelector('dialog[open]')"), false);
    await evaluate("document.querySelector('[aria-expanded][type=button]').disabled = true");
    assert.equal(await evaluate(`(${infoChord})()`), false);
    await evaluate("document.querySelector('[aria-expanded][type=button]').disabled = false");
    assert.equal(await evaluate("!!document.querySelector('dialog[open]')"), false);
    await evaluate("window.reminderNavigationFixture.ambiguous(true)");
    assert.equal(await wait("document.querySelector('#workspace-shell')?.dataset.ambiguous === 'true'"), "ready");
    assert.equal(await evaluate(`(${infoChord})()`), false);
    assert.equal(await evaluate("!!document.querySelector('dialog[open]')"), false);
    await evaluate("window.reminderNavigationFixture.ambiguous(false); window.reminderNavigationFixture.setProject('other')");
    assert.equal(await wait("document.querySelector('#workspace-shell')?.dataset.project === 'other' && document.querySelector('#workspace-shell')?.dataset.ambiguous === 'false'"), "ready");
    assert.equal(await evaluate(`(${infoChord})()`), false);
    await evaluate("window.reminderNavigationFixture.setProject('project'); window.reminderNavigationFixture.session('missing')");
    assert.equal(await wait("document.querySelector('#workspace-shell')?.dataset.session === 'missing' && !document.querySelector('[aria-expanded][type=button]')"), "ready");
    assert.equal(await evaluate(`(${infoChord})()`), false);
    await evaluate("window.reminderNavigationFixture.session('one')");
    assert.equal(await wait("document.querySelector('#workspace-shell')?.dataset.session === 'one'"), "ready");
    await evaluate(`(() => { const t=document.querySelector('#catalog-prompt'); t.focus();
      t.dispatchEvent(new KeyboardEvent('keydown',{key:'g',ctrlKey:true,bubbles:true,cancelable:true})); })()`);
    await evaluate("window.reminderNavigationFixture.session('two')");
    assert.equal(await wait("document.querySelector('#workspace-shell')?.dataset.session === 'two'"), "ready");
    assert.equal(await evaluate(`document.querySelector('#catalog-prompt').dispatchEvent(new KeyboardEvent('keydown',
      {key:'t',ctrlKey:true,bubbles:true,cancelable:true}))`), true);
    assert.equal(await evaluate("!!document.querySelector('dialog[open]')"), false);
    await evaluate("window.reminderNavigationFixture.host('e1')");
    assert.equal(await wait("!!document.querySelector('#session-prompt')"), "ready");
    assert.equal(await evaluate(`(${infoChord})('#session-prompt')`), true);
    assert.equal(await wait("document.querySelector('dialog[open] .session-info-fields code')?.textContent === 'two'"), "ready");
    await evaluate("document.querySelector('[aria-label=\"Close session info\"]').click()");
    assert.equal(await wait("!document.querySelector('dialog[open]')"), "ready");
    const paletteKey = `(options={}) => { const button=document.querySelector('[aria-label="Open command palette"]');
      button.focus(); return !button.dispatchEvent(new KeyboardEvent('keydown',
        {key:'p',ctrlKey:true,bubbles:true,cancelable:true,...options})); }`;
    const searchPalette = `(value) => { const input=document.querySelector('#palette-search');
      Object.getOwnPropertyDescriptor(HTMLInputElement.prototype,'value').set.call(input,value);
      input.dispatchEvent(new Event('input',{bubbles:true})); }`;
    assert.equal(await evaluate(`(${paletteKey})({isComposing:true})`), false);
    assert.equal(await evaluate("!!document.querySelector('.command-palette')"), false);
    assert.equal(await evaluate(`(${paletteKey})()`), true);
    assert.equal(await wait("!!document.querySelector('.command-palette[open] #palette-search')"), "ready");
    assert.equal(await evaluate("document.querySelector('.command-palette').textContent.includes('Skills')"), false);
    assert.equal(await evaluate("document.querySelector('.command-palette').textContent.includes('Reminders')"), true);
    await evaluate(`(${searchPalette})('not an implemented command')`);
    assert.equal(await wait("document.querySelector('.command-palette [role=status]')?.textContent.includes('No available')"), "ready");
    await evaluate(`(${searchPalette})('MCP')`);
    assert.equal(await wait("document.querySelectorAll('.command-palette [role=option]').length === 1"), "ready");
    assert.equal(await evaluate("document.querySelector('.command-palette [role=option]').textContent"), "MCP Servers");
    await evaluate("document.querySelector('#palette-search').focus()");
    await command("Input.dispatchKeyEvent", { type: "keyDown", key: "Enter", code: "Enter", windowsVirtualKeyCode: 13, text: "\r" });
    await command("Input.dispatchKeyEvent", { type: "keyUp", key: "Enter", code: "Enter", windowsVirtualKeyCode: 13 });
    assert.equal(await wait("document.querySelector('main[aria-label=\"MCP Servers\"] h1')?.textContent === 'MCP Servers'"), "ready");
    assert.equal(await evaluate("!!document.querySelector('.command-palette')"), false);
    assert.equal(await evaluate("window.reminderNavigationFixture.writes.length"), 1);
    await evaluate("document.querySelector('[aria-label=\"Open command palette\"]').click()");
    assert.equal(await wait("!!document.querySelector('.command-palette[open]')"), "ready");
    assert.equal(await evaluate("document.querySelector('.command-palette').textContent.includes('Session Info')"), false);
    await evaluate("window.reminderNavigationFixture.workspace()");
    await evaluate("document.querySelector('[aria-label=\"Close command palette\"]').click()");
    assert.equal(await wait("!document.querySelector('.command-palette')"), "ready");
    await evaluate("window.reminderNavigationFixture.host('e2')");
    assert.equal(await wait("!!document.querySelector('#catalog-prompt')"), "ready");
    await evaluate("document.querySelector('[aria-label=\"Open command palette\"]').click()");
    assert.equal(await wait("!!document.querySelector('.command-palette[open]')"), "ready");
    assert.equal(await evaluate("document.querySelector('.command-palette').textContent.includes('Reminders')"), false);
    await evaluate(`(${searchPalette})('Session Info')`);
    assert.equal(await wait("document.querySelector('.command-palette [role=option]')?.textContent === 'Session Info'"), "ready");
    await evaluate("document.querySelector('.command-palette [role=option]').click()");
    assert.equal(await wait("document.querySelector('dialog[open] .session-info-fields code')?.textContent === 'two'"), "ready");
    assert.equal(await evaluate("!!document.querySelector('.command-palette')"), false);
    assert.equal(await evaluate(`(${paletteKey})()`), false);
    assert.equal(await evaluate("document.querySelectorAll('dialog[open]').length"), 1);
    await evaluate("document.querySelector('[aria-label=\"Close session info\"]').click()");
    assert.equal(await wait("!document.querySelector('dialog[open]')"), "ready");
    await evaluate("document.querySelector('[aria-label=\"Open command palette\"]').click()");
    assert.equal(await wait("!!document.querySelector('.command-palette[open]')"), "ready");
    await evaluate("window.reminderNavigationFixture.session('one')");
    assert.equal(await wait("document.querySelector('#workspace-shell')?.dataset.session === 'one'"), "ready");
    await evaluate(`(${searchPalette})('Session Info')`);
    assert.equal(await wait("!!document.querySelector('.command-palette [role=status]')"), "ready");
    await evaluate("document.querySelector('#palette-search').dispatchEvent(new KeyboardEvent('keydown',{key:'Enter',bubbles:true,cancelable:true}))");
    assert.equal(await evaluate("!!document.querySelector('.session-info-fields')"), false);
    await evaluate("document.querySelector('#palette-search').dispatchEvent(new KeyboardEvent('keydown',{key:'Escape',bubbles:true,cancelable:true}))");
    assert.equal(await wait("!document.querySelector('.command-palette')"), "ready");
    await evaluate("window.reminderNavigationFixture.host('e1')");
    assert.equal(await wait("!!document.querySelector('#session-prompt')"), "ready");
    await evaluate(`(() => { const button=document.querySelector('[aria-label="Open command palette"]'); button.focus(); button.click(); })()`);
    assert.equal(await wait("!!document.querySelector('.command-palette[open]')"), "ready");
    await evaluate(`(${searchPalette})('Reminders')`);
    assert.equal(await wait("document.querySelector('.command-palette [role=option]')?.textContent === 'Reminders'"), "ready");
    await evaluate("window.reminderNavigationFixture.host('e2')");
    assert.equal(await wait("!!document.querySelector('.command-palette [role=status]')"), "ready");
    assert.equal(await evaluate("!!document.querySelector('.reminder-page')"), false);
    await evaluate("window.reminderNavigationFixture.host('e1')");
    assert.equal(await wait("!!document.querySelector('.command-palette [role=option]')"), "ready");
    await evaluate("window.reminderNavigationFixture.ambiguous(true)");
    assert.equal(await wait("!!document.querySelector('.command-palette [role=status]')"), "ready");
    await evaluate("window.reminderNavigationFixture.ambiguous(false)");
    assert.equal(await wait("!!document.querySelector('.command-palette [role=option]')"), "ready");
    await evaluate("window.reminderNavigationFixture.setProject('other')");
    assert.equal(await wait("!!document.querySelector('.command-palette [role=status]')"), "ready");
    await evaluate("window.reminderNavigationFixture.setProject('project')");
    assert.equal(await wait("!!document.querySelector('.command-palette [role=option]')"), "ready");
    await evaluate("document.querySelector('[aria-label=\"Close command palette\"]').click()");
    assert.equal(await wait("!document.querySelector('.command-palette')"), "ready");
    assert.equal(await wait("document.activeElement?.getAttribute('aria-label') === 'Open command palette'"), "ready");
    await evaluate("document.querySelector('[aria-label=\"Open command palette\"]').click()");
    assert.equal(await wait("!!document.querySelector('.command-palette[open]')"), "ready");
    await evaluate(`(${searchPalette})('Reminders')`);
    assert.equal(await wait("!!document.querySelector('.command-palette [role=option]')"), "ready");
    await evaluate("document.querySelector('.command-palette [role=option]').click()");
    assert.equal(await wait("document.querySelector('.reminder-page')?.textContent.includes('Session: one')"), "ready");
    assert.equal(await evaluate("window.reminderNavigationFixture.writes.length"), 1);
    await evaluate("window.reminderNavigationFixture.workspace()");
    assert.equal(await wait("!!document.querySelector('#session-prompt')"), "ready");
    assert.equal(await evaluate("document.querySelector('#session-prompt').value"), "retained draft");
    await evaluate("document.querySelector('[aria-label=\"Open command palette\"]').click()");
    assert.equal(await wait("!!document.querySelector('.command-palette[open]')"), "ready");
    await evaluate(`(${searchPalette})('Focus prompt')`);
    assert.equal(await wait("document.querySelector('.command-palette [role=option]')?.textContent === 'Focus prompt'"), "ready");
    await evaluate("document.querySelector('.command-palette [role=option]').click()");
    assert.equal(await wait("document.activeElement?.id === 'session-prompt'"), "ready");
    await evaluate("window.reminderNavigationFixture.host('e1')");
    assert.equal(await wait("!!document.querySelector('#session-prompt')"), "ready");
    assert.equal(await evaluate(`(() => { const t=document.querySelector('#session-prompt'); t.focus();
      return t.dispatchEvent(new KeyboardEvent('keydown',{key:'p',ctrlKey:true,bubbles:true,cancelable:true})); })()`), true);
    assert.equal(await evaluate("!!document.querySelector('.command-palette')"), false);
    await evaluate("window.reminderNavigationFixture.modal(true)");
    assert.equal(await wait("!!document.querySelector('[role=dialog] input')"), "ready");
    assert.equal(await evaluate(`(${paletteKey})()`), false);
    await evaluate("document.querySelector('[aria-label=\"Open command palette\"]').click()");
    assert.equal(await evaluate("!!document.querySelector('.command-palette')"), false);
    await evaluate("window.reminderNavigationFixture.modal(false)");
    assert.equal(await wait("!document.querySelector('[role=dialog]')"), "ready");
    await evaluate("document.querySelector('[aria-label=\"Open command palette\"]').click()");
    assert.equal(await wait("!!document.querySelector('.command-palette[open]')"), "ready");
    await evaluate("document.querySelector('#palette-search').dispatchEvent(new KeyboardEvent('keydown',{key:'ArrowDown',bubbles:true,cancelable:true}))");
    assert.equal(await evaluate("document.querySelector('.command-palette [aria-selected=true]').textContent"), "About");
    await evaluate("document.querySelector('#palette-search').dispatchEvent(new KeyboardEvent('keydown',{key:'Escape',isComposing:true,bubbles:true,cancelable:true}))");
    assert.equal(await evaluate("!!document.querySelector('.command-palette')"), true);
    const paletteColors: string[] = [];
    const activeVisible = `(() => { const list=document.querySelector('#palette-results');
      const active=document.getElementById(document.querySelector('#palette-search').getAttribute('aria-activedescendant'));
      const bounds=list.getBoundingClientRect(), option=active?.getBoundingClientRect();
      return !!option && option.top >= bounds.top-1 && option.bottom <= bounds.bottom+1 &&
        option.top >= 0 && option.bottom <= innerHeight; })()`;
    const scrollOutsideResults = `JSON.stringify([document.scrollingElement.scrollTop,
      document.querySelector('.command-palette').scrollTop, document.querySelector('#workspace-shell').scrollTop])`;
    for (const theme of ["dark", "light"]) {
      await command("Emulation.setDeviceMetricsOverride", { width: 390, height: 300, deviceScaleFactor: 1, mobile: false });
      await evaluate(`document.documentElement.dataset.theme='${theme}'`);
      assert.equal(await evaluate(`(() => { const box=document.querySelector('.command-palette').getBoundingClientRect();
        const input=document.querySelector('#palette-search').getBoundingClientRect();
        const trigger=document.querySelector('[aria-label="Open command palette"]').getBoundingClientRect();
        return box.left>=0 && box.right<=innerWidth && box.top>=0 && box.bottom<=innerHeight &&
          input.right<=box.right && input.width>100 &&
          trigger.left>=0 && trigger.right<=innerWidth &&
          getComputedStyle(document.querySelector('.command-palette')).backgroundColor !== 'rgba(0, 0, 0, 0)'; })()`), true);
      paletteColors.push(String(await evaluate("getComputedStyle(document.querySelector('.command-palette')).backgroundColor")));
      await evaluate(`(${searchPalette})('MCP')`);
      assert.equal(await wait("document.querySelectorAll('#palette-results [role=option]').length === 1"), "ready");
      await evaluate(`(${searchPalette})('')`);
      assert.equal(await wait("document.querySelectorAll('#palette-results [role=option]').length >= 7"), "ready");
      assert.equal(await evaluate("document.querySelector('#palette-results').scrollHeight > document.querySelector('#palette-results').clientHeight + 20"), true);
      const ids = JSON.parse(String(await evaluate("JSON.stringify([...document.querySelectorAll('#palette-results [role=option]')].map(option => option.id))"))) as string[];
      await evaluate("document.querySelector('#palette-search').focus()");
      assert.equal(await evaluate("document.activeElement?.id"), "palette-search");
      const outerScroll = await evaluate(scrollOutsideResults);
      const dialogBounds = await evaluate(`(() => { const box=document.querySelector('.command-palette').getBoundingClientRect();
        return JSON.stringify([box.top, box.bottom]); })()`);
      let currentIndex = 0;
      for (const key of [...Array(ids.length + 2).fill("ArrowDown"), ...Array(ids.length + 2).fill("ArrowUp")]) {
        await evaluate(`document.querySelector('#palette-search').dispatchEvent(new KeyboardEvent('keydown',{key:'${key}',bubbles:true,cancelable:true}))`);
        currentIndex = (currentIndex + (key === "ArrowDown" ? 1 : ids.length - 1)) % ids.length;
        assert.equal(await wait(`document.querySelector('#palette-search')?.getAttribute('aria-activedescendant') === '${ids[currentIndex]}'`), "ready");
        assert.equal(await evaluate(activeVisible), true, `${theme}: ${key} ${ids[currentIndex]}`);
        assert.equal(await evaluate(scrollOutsideResults), outerScroll);
        assert.equal(await evaluate(`(() => { const box=document.querySelector('.command-palette').getBoundingClientRect();
          return JSON.stringify([box.top, box.bottom]); })()`), dialogBounds);
        assert.equal(await evaluate("document.activeElement?.id"), "palette-search");
      }
      await evaluate(`(${searchPalette})('MCP')`);
      assert.equal(await wait("document.querySelectorAll('#palette-results [role=option]').length === 1"), "ready");
      assert.equal(await evaluate(activeVisible), true);
      assert.equal(await evaluate(scrollOutsideResults), outerScroll);
      assert.equal(await evaluate("document.activeElement?.id"), "palette-search");
      await evaluate(`(${searchPalette})('')`);
      assert.equal(await wait("document.querySelectorAll('#palette-results [role=option]').length === 10"), "ready");
      await evaluate("document.querySelector('#palette-search').dispatchEvent(new KeyboardEvent('keydown',{key:'ArrowUp',bubbles:true,cancelable:true}))");
      assert.equal(await wait("document.querySelector('#palette-search')?.getAttribute('aria-activedescendant') === 'palette-option-focusPrompt'"), "ready");
      assert.equal(await evaluate(activeVisible), true);
      await evaluate("window.reminderNavigationFixture.host('e2')");
      assert.equal(await wait("document.querySelectorAll('#palette-results [role=option]').length === 8"), "ready");
      assert.equal(await evaluate(activeVisible), true);
      assert.equal(await evaluate(scrollOutsideResults), outerScroll);
      assert.equal(await evaluate("!!document.querySelector('.reminder-page')"), false);
      await evaluate("window.reminderNavigationFixture.host('e1')");
      assert.equal(await wait("document.querySelectorAll('#palette-results [role=option]').length === 10"), "ready");
      assert.equal(await evaluate(activeVisible), true);
      assert.equal(await evaluate(scrollOutsideResults), outerScroll);
      assert.equal(await evaluate("document.activeElement?.id"), "palette-search");
    }
    assert.notEqual(paletteColors[0], paletteColors[1]);
    await evaluate(`(${searchPalette})('MCP')`);
    assert.equal(await wait("document.querySelectorAll('#palette-results [role=option]').length === 1"), "ready");
    await evaluate(`(${searchPalette})('')`);
    assert.equal(await wait("document.querySelectorAll('#palette-results [role=option]').length >= 7"), "ready");
    for (let index = 0; index < 7; index++)
      await evaluate("document.querySelector('#palette-search').dispatchEvent(new KeyboardEvent('keydown',{key:'ArrowDown',bubbles:true,cancelable:true}))");
    assert.equal(await wait("document.querySelector('#palette-search')?.getAttribute('aria-activedescendant') === 'palette-option-reminders'"), "ready");
    assert.equal(await evaluate(activeVisible), true);
    await command("Input.dispatchKeyEvent", { type: "keyDown", key: "Enter", code: "Enter", windowsVirtualKeyCode: 13, text: "\r" });
    await command("Input.dispatchKeyEvent", { type: "keyUp", key: "Enter", code: "Enter", windowsVirtualKeyCode: 13 });
    assert.equal(await wait("!document.querySelector('.command-palette')"), "ready");
    assert.equal(await wait("document.querySelector('.reminder-page')?.textContent.includes('Session: one')"), "ready");
    assert.equal(await evaluate("window.reminderNavigationFixture.writes.length"), 1);
  } finally {
    socket?.close(); browser?.kill(); await rm(root, { recursive: true, force: true, maxRetries: 8, retryDelay: 100 });
  }
});
