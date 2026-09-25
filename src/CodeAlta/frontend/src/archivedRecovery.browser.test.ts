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

test("production archived composer/reminder gates retain exact owner evidence without new writes", { skip: !edge, timeout: 60_000 }, async () => {
  const source = readFileSync(fileURLToPath(new URL("./main.tsx", import.meta.url)), "utf8");
  assert.match(source, /<SessionComposerGate snapshot=\{snapshot\}/);
  assert.match(source, /readOnly=\{<ReadOnlyComposer/);
  assert.match(source, /recovery=\{ownedHost \? <ArchivedActionRecovery/);
  assert.match(source, /action === "compact"[\s\S]*?currentProjectWritable\(\)/);
  assert.match(source, /view === "reminders" \? <ReminderScopeGate/);
  const root = await mkdtemp(join(tmpdir(), "codealta-archive-recovery-"));
  let browser: ReturnType<typeof spawn> | undefined;
  let socket: WebSocket | undefined;
  try {
    await build({ entryPoints: [fileURLToPath(new URL("./archivedRecovery.mount.tsx", import.meta.url))],
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
    const evaluate = async (expression: string) => (await command("Runtime.evaluate", { expression, returnByValue: true, awaitPromise: true })).result?.value;
    const wait = (condition: string) => evaluate(`new Promise(resolve => { const end=Date.now()+7000; const check=()=>{
      if (${condition}) resolve('ready'); else if (Date.now()>end) resolve(document.body.innerText.slice(0,500));
      else setTimeout(check,20); }; check(); })`);
    assert.equal(await wait("!!document.querySelector('#session-prompt')"), "ready");
    await evaluate(`(() => { const input=document.querySelector('#session-prompt'); Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype,'value').set.call(input,'Original Send text'); input.dispatchEvent(new Event('input',{bubbles:true})); })()`);
    await evaluate("document.querySelector('.send-button').click()");
    assert.equal(await wait("window.archivedRecoveryFixture.calls.some(x=>x.kind==='send')"), "ready");
    await evaluate("window.archivedRecoveryFixture.save('one'); window.archivedRecoveryFixture.steer('one'); window.archivedRecoveryFixture.queue('one'); window.archivedRecoveryFixture.compact('one'); window.archivedRecoveryFixture.cancel('one'); window.archivedRecoveryFixture.abort('one'); window.archivedRecoveryFixture.cancelQueue('one'); window.archivedRecoveryFixture.save('two'); window.archivedRecoveryFixture.steer('two'); true");
    assert.equal(await wait("window.archivedRecoveryFixture.calls.length===10"), "ready");
    assert.deepEqual(await evaluate("window.archivedRecoveryFixture.calls.map(x=>x.kind).join(',')"), "send,save,steer,queue,compact,cancel,abort,cancelQueue,save,steer");
    await evaluate("void window.archivedRecoveryFixture.ask('one','answer'); void window.archivedRecoveryFixture.ask('two','cancel'); window.archivedRecoveryFixture.input('one','resolve'); window.archivedRecoveryFixture.permission(); true");
    assert.equal(await wait("window.archivedRecoveryFixture.calls.length===14"), "ready");
    assert.equal(await evaluate("[...window.archivedRecoveryFixture.reads].sort().join(',')"), "input:one,permission:one");
    await evaluate("window.archivedRecoveryFixture.archive(true)");
    assert.equal(await wait("!!document.querySelector('.catalog-composer #catalog-prompt')"), "ready");
    assert.equal(await evaluate("!!document.querySelector('[data-reminder-count]')"), false,
      "archived read-only composer never exposes an owned reminder observation");
    assert.equal(await evaluate("document.querySelector('.catalog-composer').innerText.includes('Archived project; this session is read-only.')"), true);
    assert.equal(await evaluate("document.querySelector('.catalog-composer').innerText.includes('Recorded by session')"), false);
    assert.equal(await evaluate("document.querySelector('.catalog-composer .send-button')?.disabled"), true);
    assert.equal(await evaluate("!!document.querySelector('.composer-toolbar [aria-label=\"Steer current composer to observed run\"]')"), false,
      "archived read-only gate must not mount a steering composer action");
    assert.equal(await evaluate("!!document.querySelector('.composer-toolbar [aria-label=\"Queue current composer in this host\"]')"), false,
      "archived read-only gate must not mount host-only queueing");
    assert.equal(await evaluate("document.querySelector('.catalog-composer .prompt-input')?.rows"), 1);
    assert.equal(await evaluate("document.querySelector('.catalog-composer .composer-toolbar').compareDocumentPosition(document.querySelector('#catalog-prompt')) & Node.DOCUMENT_POSITION_PRECEDING"), 2);
    assert.equal(await wait("document.body.innerText.includes('Original Send text')"), "ready");
    assert.equal(await wait("document.querySelector('[aria-label=\"Archived interaction recovery\"]')?.innerText.includes('Original one answer')"), "ready");
    assert.equal(await evaluate("document.querySelector('[aria-label=\"Archived interaction recovery\"]')?.innerText.includes('Original two answer')"), false);
    assert.equal(await evaluate("document.querySelector('[aria-label=\"Archived interaction recovery\"]')?.innerText.includes('Original one input')"), true);
    assert.equal(await evaluate("document.querySelector('[aria-label=\"Archived interaction recovery\"]')?.innerText.includes('original command')"), true);
    assert.equal(await evaluate("document.querySelector('[aria-label=\"Archived interaction recovery\"]')?.innerText.includes('allow_once')"), true);
    assert.equal(await evaluate("document.querySelector('[aria-label=\"Archived interaction recovery\"]')?.innerText.includes('input-one')"), true);
    assert.equal(await evaluate("document.querySelector('[aria-label=\"Archived interaction recovery\"]')?.innerText.includes('88888888-8888-8888-8888-888888888888')"), true);
    for (const width of [390, 1120]) {
      await command("Emulation.setDeviceMetricsOverride", { width, height: 800, deviceScaleFactor: 1, mobile: false });
      for (const theme of ["dark", "light"]) {
        await evaluate(`document.documentElement.dataset.theme='${theme}'`);
        assert.equal(await evaluate(`(() => { const input=document.querySelector('#catalog-prompt'); const toolbar=document.querySelector('.catalog-composer .composer-toolbar');
          const button=document.querySelector('#open-provider-configuration'); const status=document.querySelector('#catalog-draft-status');
          if (!input || !toolbar || !button || !status) return false;
          const rect=input.getBoundingClientRect(), bar=toolbar.getBoundingClientRect(), icon=button.getBoundingClientRect();
          const color=s=>getComputedStyle(s).color.match(/\\d+/g).slice(0,3).map(Number);
          const bg=getComputedStyle(button).backgroundColor.match(/\\d+/g).slice(0,3).map(Number);
          const lum=v=>{const x=v/255;return x<=.04045?x/12.92:((x+.055)/1.055)**2.4};
          const l=a=>.2126*lum(a[0])+.7152*lum(a[1])+.0722*lum(a[2]);
          const ratio=(Math.max(l(color(button)),l(bg))+.05)/(Math.min(l(color(button)),l(bg))+.05);
          return rect.width<=${width} && rect.height>=50 && rect.height<80 && input.scrollHeight<=input.clientHeight+2
            && bar.top>=rect.bottom && icon.width>=30 && icon.height>=30 && status.getBoundingClientRect().width>0
            && ratio>=4.5 && document.documentElement.scrollWidth<=${width}; })()`), true, `${width}px ${theme} compact layout and icon contrast`);
      }
    }
    await evaluate(`(() => { const input=document.querySelector('#catalog-prompt'); input.focus();
      Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype,'value').set.call(input,'line one\\nline two');
      input.dispatchEvent(new Event('input',{bubbles:true})); })()`);
    assert.equal(await wait("document.querySelector('#catalog-prompt').value==='line one\\nline two'"), "ready");
    assert.equal(await evaluate("(() => {const input=document.querySelector('#catalog-prompt');return input.getBoundingClientRect().height>50 && input.scrollHeight<=input.clientHeight+2})()"), true);
    await evaluate("(() => { const input=document.querySelector('#catalog-prompt'); input.setSelectionRange(input.value.length,input.value.length); input.focus(); })()");
    await command("Input.dispatchKeyEvent", { type: "keyDown", key: "Enter", code: "Enter", windowsVirtualKeyCode: 13, text: "\r", unmodifiedText: "\r" });
    await command("Input.dispatchKeyEvent", { type: "keyUp", key: "Enter", code: "Enter", windowsVirtualKeyCode: 13 });
    assert.equal(await wait("document.querySelector('#catalog-prompt').value==='line one\\nline two\\n'"), "ready");
    await command("Input.dispatchKeyEvent", { type: "keyDown", key: "Enter", code: "Enter", windowsVirtualKeyCode: 13, modifiers: 2 });
    await command("Input.dispatchKeyEvent", { type: "keyUp", key: "Enter", code: "Enter", windowsVirtualKeyCode: 13, modifiers: 2 });
    assert.equal(await evaluate("window.archivedRecoveryFixture.calls.length"), 14);
    await command("Input.dispatchKeyEvent", { type: "keyDown", key: "Tab", code: "Tab", windowsVirtualKeyCode: 9 });
    await command("Input.dispatchKeyEvent", { type: "keyUp", key: "Tab", code: "Tab", windowsVirtualKeyCode: 9 });
    assert.equal(await evaluate("document.activeElement?.id==='open-provider-configuration' && getComputedStyle(document.activeElement).outlineWidth==='2px'"), true);
    await command("Input.dispatchKeyEvent", { type: "keyDown", key: "g", code: "KeyG", windowsVirtualKeyCode: 71, modifiers: 2 });
    await command("Input.dispatchKeyEvent", { type: "keyUp", key: "g", code: "KeyG", windowsVirtualKeyCode: 71, modifiers: 2 });
    await command("Input.dispatchKeyEvent", { type: "keyDown", key: "u", code: "KeyU", windowsVirtualKeyCode: 85, modifiers: 2 });
    await command("Input.dispatchKeyEvent", { type: "keyUp", key: "u", code: "KeyU", windowsVirtualKeyCode: 85, modifiers: 2 });
    assert.equal(await evaluate("window.archivedRecoveryFixture.configurationOpens()"), 1);
    await evaluate(`(() => { const input=document.querySelector('#catalog-prompt');
      Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype,'value').set.call(input,Array(50).fill('draft line').join('\\n'));
      input.dispatchEvent(new Event('input',{bubbles:true})); })()`);
    assert.equal(await wait("document.querySelector('#catalog-prompt').value.split('\\n').length===50"), "ready");
    assert.equal(await evaluate("(() => {const input=document.querySelector('#catalog-prompt');return input.getBoundingClientRect().height<=240 && input.scrollHeight>input.clientHeight+2})()"), true);
    assert.equal(await evaluate("document.querySelector('#catalog-prompt').dispatchEvent(new KeyboardEvent('keydown',{key:'Enter',bubbles:true,cancelable:true}))"), true);
    assert.equal(await evaluate("document.querySelector('#catalog-prompt').dispatchEvent(new KeyboardEvent('keydown',{key:'Enter',ctrlKey:true,repeat:true,isComposing:true,bubbles:true,cancelable:true}))"), true);
    assert.equal(await evaluate("document.querySelector('[aria-label=\"Retained Steer text\"]')?.textContent"), "Steer exact one");
    assert.equal(await evaluate("document.querySelector('[aria-label=\"Retained Queue text\"]')?.textContent"), "Queue exact one");
    assert.equal(await evaluate("document.body.innerText.includes('compact-one') && document.body.innerText.includes('cancel-one') && document.body.innerText.includes('abort-one') && document.body.innerText.includes('cancel-queue-one') && !document.body.innerText.includes('Steer exact two')"), true);
    const countAtArchive = await evaluate("window.archivedRecoveryFixture.calls.length");
    await evaluate(`(() => { const input=document.querySelector('#catalog-prompt'); Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype,'value').set.call(input,'Private local draft'); input.dispatchEvent(new Event('input',{bubbles:true})); input.focus(); })()`);
    assert.equal(await wait("document.querySelector('#catalog-prompt').value==='Private local draft'"), "ready");
    await evaluate("document.querySelector('.catalog-composer .send-button').click()");
    await command("Input.dispatchKeyEvent", { type: "keyDown", key: "Enter", code: "Enter", windowsVirtualKeyCode: 13 });
    await command("Input.dispatchKeyEvent", { type: "keyUp", key: "Enter", code: "Enter", windowsVirtualKeyCode: 13 });
    await command("Input.dispatchKeyEvent", { type: "keyDown", key: "F11", code: "F11", windowsVirtualKeyCode: 122, modifiers: 2 });
    await command("Input.dispatchKeyEvent", { type: "keyUp", key: "F11", code: "F11", windowsVirtualKeyCode: 122, modifiers: 2 });
    assert.equal(await evaluate("window.archivedRecoveryFixture.calls.length"), countAtArchive);
    assert.equal(await evaluate("[...window.archivedRecoveryFixture.reads].sort().join(',')"), "input:one,permission:one");
    await evaluate(`(() => { const call=window.archivedRecoveryFixture.calls.find(x=>x.kind==='cancelAsk');
      call.resolve({status:'ok',hostEpoch:'12345678-1234-1234-1234-123456789abc',disposition:{actionId:call.request.action.actionId,
        handle:call.request.action.handle,status:'cancelled',runId:null}}); })()`);
    await evaluate(`(() => { const calls=window.archivedRecoveryFixture.calls;
      calls.find(x=>x.kind==='answerAsk').reject(new Error('lost ask'));
      calls.find(x=>x.kind==='resolveInput').resolve({status:'resolved',hostEpoch:'12345678-1234-1234-1234-123456789abc',handle:calls.find(x=>x.kind==='resolveInput').request.handle});
      calls.find(x=>x.kind==='permission').resolve({status:'rejected',hostEpoch:'12345678-1234-1234-1234-123456789abc',handle:calls.find(x=>x.kind==='permission').request.handle});
    })()`);
    assert.equal(await wait("document.querySelector('[aria-label=\"Archived interaction recovery\"]')?.innerText.includes('uncertain') && document.querySelector('[aria-label=\"Archived interaction recovery\"]')?.innerText.includes('resolved') && document.querySelector('[aria-label=\"Archived interaction recovery\"]')?.innerText.includes('rejected')"), "ready");
    assert.equal(await evaluate("window.archivedRecoveryFixture.calls.length"), countAtArchive);
    await evaluate("window.archivedRecoveryFixture.calls.find(x=>x.kind==='send').reject(new Error('lost send response'))");
    assert.equal(await wait("document.body.innerText.includes('Send · Outcome uncertain; original waiter settled')"), "ready");
    assert.equal(await evaluate("document.querySelector('[aria-label=\"Retained Send text\"]')?.textContent"), "Original Send text");
    await evaluate("window.archivedRecoveryFixture.calls.find(x=>x.kind==='compact').resolve({status:'busy',epoch:'12345678-1234-1234-1234-123456789abc',receipt:null})");
    assert.equal(await wait("!document.body.innerText.includes('compact-one')"), "ready");
    assert.equal(await evaluate("document.body.innerText.includes('Original Send text') && document.querySelector('.catalog-composer .send-button')?.disabled"), true);
    await evaluate("window.archivedRecoveryFixture.session('two')");
    assert.equal(await wait("document.body.innerText.includes('Steer exact two')"), "ready");
    assert.equal(await evaluate("!document.body.innerText.includes('Original Send text') && !document.body.innerText.includes('Steer exact one')"), true);
    assert.equal(await evaluate("document.querySelector('[aria-label=\"Archived interaction recovery\"]')?.innerText.includes('Original one answer')"), false);
    assert.equal(await evaluate("document.querySelector('[aria-label=\"Archived interaction recovery\"]')?.innerText.includes('99999999-9999-9999-9999-999999999999')"), true);
    assert.equal(await evaluate("document.querySelector('[aria-label=\"Archived interaction recovery\"]')?.innerText.includes('Ask cancel · settled')"), true);
    await evaluate("window.archivedRecoveryFixture.session('one')");
    assert.equal(await wait("document.body.innerText.includes('Original Send text')"), "ready");
    assert.equal(await evaluate("document.querySelector('[aria-label=\"Archived interaction recovery\"]')?.innerText.includes('Original one input')"), true);
    await evaluate("window.archivedRecoveryFixture.host(null)");
    assert.equal(await wait("!document.querySelector('[aria-label=\"Archived interaction recovery\"]')"), "ready");
    await evaluate("window.archivedRecoveryFixture.host('12345678-1234-1234-1234-123456789abc')");
    assert.equal(await wait("document.querySelector('[aria-label=\"Archived interaction recovery\"]')?.innerText.includes('Original one input')"), "ready");
    assert.equal(await evaluate("[...window.archivedRecoveryFixture.reads].sort().join(',')"), "input:one,permission:one");
    assert.equal(await evaluate("document.querySelector('#catalog-prompt').value"), "Private local draft");
    await evaluate("window.archivedRecoveryFixture.view('reminders')");
    assert.equal(await wait("!!document.querySelector('[aria-label=\"Retained reminder Save\"]')"), "ready");
    assert.equal(await evaluate("document.querySelector('[aria-label=\"Retained Save full message\"]')?.textContent"), "Saved exact message for one");
    assert.equal(await evaluate("document.querySelector('button:is([type=button])') !== null && [...document.querySelectorAll('button')].find(x=>x.textContent==='Create reminder')?.disabled"), true);
    await evaluate("window.archivedRecoveryFixture.calls.find(x=>x.kind==='save').reject(new Error('lost'))");
    assert.equal(await wait("document.body.innerText.includes('Uncertain reminder Save')"), "ready");
    const before = await evaluate("window.archivedRecoveryFixture.calls.length");
    await evaluate("[...document.querySelectorAll('button')].find(x=>x.textContent==='Create reminder').click()");
    await command("Input.dispatchKeyEvent", { type: "keyDown", key: "s", code: "KeyS", windowsVirtualKeyCode: 83, modifiers: 2 });
    await command("Input.dispatchKeyEvent", { type: "keyUp", key: "s", code: "KeyS", windowsVirtualKeyCode: 83, modifiers: 2 });
    assert.equal(await evaluate("window.archivedRecoveryFixture.calls.length"), before);
    await evaluate("window.archivedRecoveryFixture.session('two')");
    assert.equal(await wait("document.body.innerText.includes('Saved exact message for two') && !document.body.innerText.includes('Saved exact message for one')"), "ready");
    await evaluate("window.archivedRecoveryFixture.host(null)");
    assert.equal(await wait("document.body.innerText.includes('Select an owned session')"), "ready");
    await evaluate("window.archivedRecoveryFixture.view('workspace'); window.archivedRecoveryFixture.archive(false); window.archivedRecoveryFixture.session('one')");
    assert.equal(await wait("!!document.querySelector('#catalog-prompt')"), "ready");
    assert.equal(await evaluate("!!document.querySelector('.composer-toolbar [aria-label=\"Steer current composer to observed run\"]')"), false,
      "catalog-only gate does not expose owned steering after host loss");
    assert.equal(await evaluate("!!document.querySelector('.composer-toolbar [aria-label=\"Queue current composer in this host\"]')"), false,
      "catalog-only gate does not expose queueing after host loss");
    assert.equal(await evaluate("document.querySelector('#catalog-draft-status')?.textContent.includes('Draft only')"), true);
    assert.equal(await evaluate("document.querySelector('#catalog-draft-status')?.textContent.includes('archived')"), false);
    assert.equal(await evaluate("document.querySelector('#catalog-prompt').value"), "Private local draft");
    const opens = await evaluate("window.archivedRecoveryFixture.configurationOpens()");
    await evaluate("document.querySelector('#open-provider-configuration').click(); document.querySelector('#catalog-prompt').focus()");
    await command("Input.dispatchKeyEvent", { type: "keyDown", key: "g", code: "KeyG", windowsVirtualKeyCode: 71, modifiers: 2 });
    await command("Input.dispatchKeyEvent", { type: "keyUp", key: "g", code: "KeyG", windowsVirtualKeyCode: 71, modifiers: 2 });
    await command("Input.dispatchKeyEvent", { type: "keyDown", key: "u", code: "KeyU", windowsVirtualKeyCode: 85, modifiers: 2 });
    await command("Input.dispatchKeyEvent", { type: "keyUp", key: "u", code: "KeyU", windowsVirtualKeyCode: 85, modifiers: 2 });
    assert.equal(await evaluate("window.archivedRecoveryFixture.configurationOpens()"), Number(opens) + 2);
    assert.equal(await evaluate("window.archivedRecoveryFixture.calls.length"), countAtArchive);
    await evaluate("window.archivedRecoveryFixture.session('two')");
    assert.equal(await wait("document.querySelector('#catalog-prompt')?.value===''"), "ready");
    await evaluate(`(() => { window.fixtureSetItem=Storage.prototype.setItem; Storage.prototype.setItem=()=>{throw Error('storage unavailable')};
      const input=document.querySelector('#catalog-prompt'); Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype,'value').set.call(input,'Unsaved local two');
      input.dispatchEvent(new Event('input',{bubbles:true})); })()`);
    assert.equal(await wait("document.querySelector('#catalog-prompt').value==='Unsaved local two'"), "ready");
    assert.equal(await wait("window.archivedRecoveryFixture.draftVisible('two','two')"), "ready");
    await evaluate("new Promise(resolve=>setTimeout(resolve,40))");
    assert.equal(await evaluate("localStorage.getItem('codealta.desktop.prompt.two')===null"), true);
    await evaluate("Storage.prototype.setItem=window.fixtureSetItem");
    await evaluate("window.archivedRecoveryFixture.session('one')");
    assert.equal(await wait("document.querySelector('#catalog-prompt')?.value==='Private local draft'"), "ready");
    assert.equal(await evaluate("window.archivedRecoveryFixture.draftVisible('two','one')"), false);
    await evaluate("window.archivedRecoveryFixture.session('two')");
    assert.equal(await wait("document.querySelector('#catalog-prompt')?.value===''"), "ready");
    assert.equal(await evaluate("window.archivedRecoveryFixture.calls.length"), countAtArchive);
  } finally {
    socket?.close(); browser?.kill(); await rm(root, { recursive: true, force: true, maxRetries: 8, retryDelay: 100 });
  }
});
