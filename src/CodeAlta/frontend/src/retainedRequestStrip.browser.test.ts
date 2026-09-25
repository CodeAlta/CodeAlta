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

// Mounts the actual OwnedSessionPanel with production style.css, not an OS select popup or native WebView2.
test("mounted retained queue and steering strip is exact, copyable and read-only", { skip: !edge, timeout: 60_000 }, async () => {
  const app = readFileSync(fileURLToPath(new URL("./main.tsx", import.meta.url)), "utf8");
  assert.match(app, /action === "context"\)[\s\S]*?activateContextShortcut\(workspaceShell\.current\)/,
    "the mounted shortcut dispatcher must remain wired to the production app");
  assert.match(app, /compactTrigger: compactTrigger\.current/,
    "the mounted compaction shortcut trigger must remain wired to the production dispatcher");
  assert.match(app, /action === "compact"[\s\S]*?trigger\.dataset\.epoch === status\.hostEpoch[\s\S]*?trigger\.click\(\)/,
    "production shortcut dispatch must recheck the selected epoch and live trigger");
  const root = await mkdtemp(join(tmpdir(), "codealta-retained-strip-"));
  let browser: ReturnType<typeof spawn> | undefined;
  let socket: WebSocket | undefined;
  try {
    await build({ entryPoints: [fileURLToPath(new URL("./composer.mount.tsx", import.meta.url))],
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
      socket!.addEventListener("error", () => reject(new Error("test browser debugger unavailable")), { once: true });
    });
    let sequence = 0;
    const command = (method: string, params: object = {}) => new Promise<{ result?: { value?: string } }>((resolve, reject) => {
      const id = ++sequence;
      const timer = setTimeout(() => reject(new Error(`test browser ${method} timed out`)), 12_000);
      const reply = (event: MessageEvent) => {
        const message = JSON.parse(String(event.data)) as { id?: number; result?: { result?: { value?: string } }; error?: object };
        if (message.id !== id) return;
        socket!.removeEventListener("message", reply);
        clearTimeout(timer);
        if (message.error) reject(new Error(`test browser ${method} failed`));
        else resolve(message.result ?? {});
      };
      socket!.addEventListener("message", reply);
      socket!.send(JSON.stringify({ id, method, params }));
    });
    await command("Page.enable");
    const loaded = new Promise<void>((resolve, reject) => {
      const timer = setTimeout(() => reject(new Error("test page did not load")), 10_000);
      const listener = (event: MessageEvent) => {
        if ((JSON.parse(String(event.data)) as { method?: string }).method !== "Page.loadEventFired") return;
        clearTimeout(timer); socket!.removeEventListener("message", listener); resolve();
      };
      socket!.addEventListener("message", listener);
    });
    await command("Page.navigate", { url: pathToFileURL(page).href });
    await loaded;
    await command("Emulation.setFocusEmulationEnabled", { enabled: true });
    const evaluate = async (expression: string) => (await command("Runtime.evaluate", { expression, returnByValue: true,
      awaitPromise: true })).result?.value;
    const ready = await evaluate(`new Promise(resolve => { const end = Date.now() + 9000; const check = () => {
      if (document.querySelector('select[aria-label="Agent prompt"]:not(:disabled)')) resolve('ready');
      else if (Date.now() > end) resolve(document.body.innerText.slice(0, 300)); else setTimeout(check, 35); }; check(); })`);
    assert.equal(ready, "ready");
    const waitFor = (condition: string) => evaluate(`new Promise(resolve => { const end = Date.now() + 4000; const check = () => {
      if (${condition}) resolve('ready'); else if (Date.now() > end) resolve('timed out'); else setTimeout(check, 25); }; check(); })`);
    assert.equal(await evaluate("!!document.querySelector('.retained-intent-strip')"), false);
    await evaluate("window.fixture.observe(null,13);document.querySelector('#refresh-session-context').click()");
    assert.equal(await waitFor("!!document.querySelector('.composer-toolbar [aria-label=\"Queue current composer in this host\"]')"), "ready");
    await evaluate(`(() => {const el=document.querySelector('#session-prompt');Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype,'value').set.call(el,'Exact queue text');el.dispatchEvent(new Event('input',{bubbles:true}));})()`);
    await evaluate("document.querySelector('.composer-toolbar [aria-label=\"Queue current composer in this host\"]').click()");
    assert.equal(await waitFor("!!document.querySelector('.retained-intent-strip [data-kind=\"Queue\"]')"), "ready");
    const queue = '.retained-intent-row[data-kind="Queue"]';
    const steer = '.retained-intent-row[data-kind="Steer"]';
    const writePrompt = (text: string) => evaluate(`(() => {const el=document.querySelector('#session-prompt');
      Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype,'value').set.call(el,${JSON.stringify(text)});
      el.dispatchEvent(new Event('input',{bubbles:true}));})()`);
    assert.equal(await evaluate(`document.querySelector(${JSON.stringify(queue)}+' summary').textContent.includes('Exact-request waiter pending')`), true);
    const queueRequest = JSON.parse((await evaluate(`JSON.stringify(window.fixture.queueCalls[0])`))!);
    assert.equal(await evaluate(`document.querySelector(${JSON.stringify(queue)}+' details').open`), false);
    await evaluate(`(() => {const el=[...document.querySelectorAll('.context-actions label')]
      .find(x=>x.textContent.includes('Host-only queued text')).querySelector('textarea');
      Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype,'value').set.call(el,'Separate queue draft');
      el.dispatchEvent(new Event('input',{bubbles:true}));})()`);
    const counts = () => evaluate(`JSON.stringify([window.fixture.refreshes,window.fixture.queueCalls.length,window.fixture.steerCalls.length])`);
    const before = await counts();
    await evaluate(`document.querySelector(${JSON.stringify(queue)}+' summary').click()`);
    assert.equal(await evaluate(`document.querySelector(${JSON.stringify(queue)}+' details').open`), true);
    await evaluate(`document.querySelector(${JSON.stringify(queue)}+' summary').focus()`);
    await command('Page.bringToFront');
    await command('Input.dispatchKeyEvent', {type:'keyDown', key:'Enter', code:'Enter', text:'\r', windowsVirtualKeyCode:13, nativeVirtualKeyCode:13});
    await command('Input.dispatchKeyEvent', {type:'keyUp', key:'Enter', code:'Enter', windowsVirtualKeyCode:13});
    assert.equal(await waitFor(`!document.querySelector(${JSON.stringify(queue)}+' details').open`), 'ready', 'keyboard toggles disclosure');
    await evaluate(`document.querySelector(${JSON.stringify(queue)}+' summary').click()`);
    assert.equal(await evaluate(`document.querySelector(${JSON.stringify(queue)}+' pre').textContent`), "Exact queue text");
    assert.equal(await evaluate(`JSON.stringify([...document.querySelectorAll(${JSON.stringify(queue)}+' dd')].map(x=>x.textContent))`),
      JSON.stringify([queueRequest.expectedEpoch,queueRequest.sessionId,queueRequest.expectedRuntimeInstanceId,
        queueRequest.expectedAttachmentGeneration,queueRequest.clientRequestId]));
    assert.equal(await evaluate(`document.querySelector(${JSON.stringify(queue)}).textContent.includes('not host insertion, durability or execution')`), true);
    await evaluate(`Object.defineProperty(navigator,'clipboard',{configurable:true,value:{writeText: text => {
      (window.copyWrites ||= []).push(text);return Promise.resolve();}}})`);
    await evaluate(`document.querySelector(${JSON.stringify(queue)}+' button').click()`);
    assert.equal(await waitFor(`document.querySelector(${JSON.stringify(queue)}+' [role=status]')?.textContent.includes('copied')`), "ready");
    assert.equal(await evaluate(`JSON.stringify(window.copyWrites)`), '["Exact queue text"]');
    assert.equal(await counts(), before, "disclosure and Copy must not read, queue, or steer");
    await evaluate(`window.fixture.switchSession('fixture-other')`);
    assert.equal(await waitFor(`!!document.querySelector('#session-prompt') && !document.querySelector('.retained-intent-strip')`), "ready");
    await evaluate(`window.fixture.settleQueue('mismatched')`);
    await evaluate(`window.fixture.switchSession('fixture-session')`);
    assert.equal(await waitFor(`document.querySelector(${JSON.stringify(queue)}+' summary')?.textContent.includes('manual recovery')`), "ready");
    assert.equal(await evaluate(`document.querySelector('#session-prompt').value`), 'Exact queue text');
    assert.equal(await evaluate(`window.fixture.queueDraft()`), 'Separate queue draft', 'retained text must not replace the independent editor draft');
    await evaluate(`window.fixture.observe('run-one', 14);document.querySelector('#refresh-session-context').click()`);
    assert.equal(await waitFor(`!!document.querySelector('.composer-toolbar [aria-label="Steer current composer to observed run"]')`), "ready");
    await writePrompt('  Steer\nthis exact text 😀  ');
    await evaluate(`document.querySelector('.composer-toolbar [aria-label="Steer current composer to observed run"]').click()`);
    assert.equal(await waitFor(`document.querySelector(${JSON.stringify(steer)}+' summary')?.textContent.includes('Exact-request waiter pending')`), "ready");
    const steerRequest = JSON.parse((await evaluate(`JSON.stringify(window.fixture.steerCalls[0])`))!);
    assert.equal(await evaluate(`document.querySelector(${JSON.stringify(queue)}+' summary').textContent.includes('manual recovery')`), true);
    await evaluate(`document.querySelector(${JSON.stringify(steer)}+' summary').click()`);
    assert.equal(await evaluate(`document.querySelector(${JSON.stringify(steer)}+' pre').textContent`), steerRequest.text);
    assert.equal(await evaluate(`JSON.stringify([...document.querySelectorAll(${JSON.stringify(steer)}+' dd')].map(x=>x.textContent))`),
      JSON.stringify([steerRequest.expectedEpoch,steerRequest.sessionId,steerRequest.expectedRuntimeInstanceId,
        steerRequest.expectedAttachmentGeneration,steerRequest.expectedRunId,steerRequest.clientRequestId]));
    assert.equal(await evaluate(`document.querySelector(${JSON.stringify(steer)}).textContent.includes('not run completion')`), true);
    await evaluate(`window.fixture.failRuntimeRead(true);document.querySelector('#refresh-session-context').click()`);
    assert.equal(await waitFor(`document.querySelector('.composer-notice')?.textContent.includes('read_failed') ||
      [...document.querySelectorAll('.owned-session')].some(el=>el.textContent.includes('read_failed'))`), 'ready');
    assert.equal(await evaluate(`document.querySelector(${JSON.stringify(steer)}+' pre').textContent`), steerRequest.text,
      'a failed observation cannot erase retained steering evidence');
    assert.equal(await evaluate(`document.querySelector(${JSON.stringify(queue)}+' summary').textContent.includes('manual recovery')`), true);
    await evaluate(`window.fixture.failRuntimeRead(false)`);
    await writePrompt('Independent Send draft');
    const beforeSteerCopy = await counts();
    await evaluate(`Object.defineProperty(navigator,'clipboard',{configurable:true,value:{writeText:()=>Promise.reject(Error('denied'))}});
      document.querySelector(${JSON.stringify(steer)}+' button').click()`);
    assert.equal(await waitFor(`document.querySelector(${JSON.stringify(steer)}+' [role=status]')?.textContent.includes('Copy failed')`), 'ready');
    await evaluate(`Object.defineProperty(navigator,'clipboard',{configurable:true,value:undefined});
      document.querySelector(${JSON.stringify(steer)}+' button').click()`);
    assert.equal(await waitFor(`document.querySelector(${JSON.stringify(steer)}+' [role=status]')?.textContent.includes('unavailable')`), 'ready');
    assert.equal(await counts(), beforeSteerCopy);
    assert.equal(await evaluate(`document.querySelector('#session-prompt').value`), 'Independent Send draft');
    for (const width of [390, 1120]) for (const theme of ['light', 'dark']) {
      await command('Emulation.setDeviceMetricsOverride', {width, height: 800, deviceScaleFactor: 1, mobile: false});
      await evaluate(`document.documentElement.dataset.theme=${JSON.stringify(theme)}`);
      const layout = JSON.parse((await evaluate(`JSON.stringify((() => {const s=document.querySelector('.retained-intent-strip');
        const panel=document.querySelector('.owned-session');const b=s.querySelector('button');b.focus();
        return {page:document.documentElement.scrollWidth,view:innerWidth,strip:s.scrollWidth,stripWidth:s.clientWidth,
          panel:panel.scrollWidth,panelWidth:panel.clientWidth,outline:getComputedStyle(b).outlineStyle,
          copy:b.getAttribute('aria-label'),summary:s.querySelector('summary').textContent};})())`))!);
      assert.ok(layout.page <= layout.view + 2 && layout.strip <= layout.stripWidth + 2 &&
        layout.panel <= layout.panelWidth + 2 && layout.outline === 'solid' && layout.copy === 'Copy retained Queue text'
        && layout.summary.includes('manual recovery'), `${width}/${theme}: ${JSON.stringify(layout)}`);
    }
    await evaluate(`window.fixture.switchSession('fixture-other')`);
    assert.equal(await waitFor(`!document.querySelector('.retained-intent-strip')`), 'ready');
    await evaluate(`window.fixture.settleSteer()`);
    await evaluate(`window.fixture.switchSession('fixture-session')`);
    assert.equal(await waitFor(`document.querySelector(${JSON.stringify(steer)}+' summary')?.textContent.includes('manual recovery')
      && !!document.querySelector(${JSON.stringify(queue)})`), 'ready',
      'an aborted panel waiter cannot treat its late admission as a definite outcome');
    assert.equal(await evaluate(`document.querySelector('#session-prompt').value`), 'Independent Send draft');
    await evaluate(`window.fixture.steerMode('hold');[...document.querySelectorAll('.context-actions button')]
      .find(b=>b.textContent.includes('Retry exact steering request')).click()`);
    assert.equal(await waitFor(`document.querySelector(${JSON.stringify(steer)}+' summary')?.textContent.includes('Exact-request waiter pending')`), 'ready');
    assert.equal(await evaluate(`JSON.stringify([window.fixture.steerCalls[1].clientRequestId,window.fixture.steerCalls[1].text,
      window.fixture.steerCalls[1].expectedRunId])`),
      JSON.stringify([steerRequest.clientRequestId,steerRequest.text,steerRequest.expectedRunId]));
    await evaluate(`window.fixture.settleSteer()`);
    assert.equal(await waitFor(`!document.querySelector(${JSON.stringify(steer)}) && !!document.querySelector(${JSON.stringify(queue)})`), 'ready');
    await evaluate(`window.fixture.queueMode('hold');[...document.querySelectorAll('.context-actions button')]
      .find(b=>b.textContent.includes('Retry exact host-only queue request')).click()`);
    assert.equal(await waitFor(`document.querySelector(${JSON.stringify(queue)}+' summary')?.textContent.includes('Exact-request waiter pending')`), 'ready');
    assert.equal(await evaluate(`JSON.stringify([window.fixture.queueCalls[1].clientRequestId,window.fixture.queueCalls[1].text])`),
      JSON.stringify([queueRequest.clientRequestId,queueRequest.text]));
    await evaluate(`window.fixture.settleQueue()`);
    assert.equal(await waitFor(`!document.querySelector('.retained-intent-strip')`), 'ready');
    assert.equal(await evaluate(`document.querySelector('#session-prompt').value`), 'Independent Send draft');
    assert.equal(await evaluate(`[...document.querySelectorAll('.context-actions label')]
      .find(x=>x.textContent.includes('Host-only queued text')).querySelector('textarea').value`), 'Separate queue draft');
    await writePrompt('  New exact\nretained text 😀  ');
    await evaluate(`document.querySelector('.composer-toolbar [aria-label="Queue current composer in this host"]').click()`);
    assert.equal(await waitFor(`!!document.querySelector(${JSON.stringify(queue)})`), 'ready');
    const previousCounts = await counts();
    await evaluate(`Object.defineProperty(navigator,'clipboard',{configurable:true,value:{writeText: text => {
      (window.copyWrites ||= []).push(text);return new Promise(resolve => {window.releaseCopy = resolve});}}});
      document.querySelector(${JSON.stringify(queue)}+' button').click()`);
    assert.equal(await waitFor(`!!window.releaseCopy`), 'ready');
    assert.equal(await evaluate(`window.copyWrites.at(-1)`), '  New exact\nretained text 😀  ');
    assert.equal(await counts(), previousCounts);
    await evaluate(`window.fixture.switchSession('fixture-other')`);
    assert.equal(await waitFor(`!document.querySelector('.retained-intent-strip')`), 'ready');
    await evaluate(`window.fixture.switchSession('fixture-session')`);
    assert.equal(await waitFor(`!!document.querySelector(${JSON.stringify(queue)})`), 'ready');
    await evaluate(`document.querySelector('#session-prompt').focus();window.releaseCopy()`);
    assert.equal(await evaluate(`!!document.querySelector(${JSON.stringify(queue)}+' [role=status]')`), false,
      'an old copy completion must not report success on the remounted owner row');
    assert.equal(await evaluate(`document.activeElement?.id`), 'session-prompt', 'late Copy must not steal focus');
    await evaluate(`window.fixture.switchEpoch('replacement-epoch')`);
    assert.equal(await waitFor(`!document.querySelector('.retained-intent-strip')`), 'ready',
      'old-epoch retained requests are not visible under the replacement epoch');
  } finally {
    socket?.close(); browser?.kill(); await rm(root, { recursive: true, force: true, maxRetries: 8, retryDelay: 100 });
  }
});
