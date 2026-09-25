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
    assert.equal(await wait("document.body.innerText.includes('Original Send text')"), "ready");
    assert.equal(await wait("document.querySelector('[aria-label=\"Archived interaction recovery\"]')?.innerText.includes('Original one answer')"), "ready");
    assert.equal(await evaluate("document.querySelector('[aria-label=\"Archived interaction recovery\"]')?.innerText.includes('Original two answer')"), false);
    assert.equal(await evaluate("document.querySelector('[aria-label=\"Archived interaction recovery\"]')?.innerText.includes('Original one input')"), true);
    assert.equal(await evaluate("document.querySelector('[aria-label=\"Archived interaction recovery\"]')?.innerText.includes('original command')"), true);
    assert.equal(await evaluate("document.querySelector('[aria-label=\"Archived interaction recovery\"]')?.innerText.includes('allow_once')"), true);
    assert.equal(await evaluate("document.querySelector('[aria-label=\"Archived interaction recovery\"]')?.innerText.includes('input-one')"), true);
    assert.equal(await evaluate("document.querySelector('[aria-label=\"Archived interaction recovery\"]')?.innerText.includes('88888888-8888-8888-8888-888888888888')"), true);
    assert.equal(await evaluate("document.querySelector('[aria-label=\"Retained Steer text\"]')?.textContent"), "Steer exact one");
    assert.equal(await evaluate("document.querySelector('[aria-label=\"Retained Queue text\"]')?.textContent"), "Queue exact one");
    assert.equal(await evaluate("document.body.innerText.includes('compact-one') && document.body.innerText.includes('cancel-one') && document.body.innerText.includes('abort-one') && document.body.innerText.includes('cancel-queue-one') && !document.body.innerText.includes('Steer exact two')"), true);
    const countAtArchive = await evaluate("window.archivedRecoveryFixture.calls.length");
    await evaluate(`(() => { const input=document.querySelector('#catalog-prompt'); Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype,'value').set.call(input,'Private local draft'); input.dispatchEvent(new Event('input',{bubbles:true})); input.focus(); })()`);
    assert.equal(await wait("document.querySelector('#catalog-prompt').value==='Private local draft'"), "ready");
    await evaluate("document.querySelector('.catalog-composer .composer-footer button').click()");
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
    assert.equal(await evaluate("document.body.innerText.includes('Original Send text') && !document.querySelector('.send-button')"), true);
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
  } finally {
    socket?.close(); browser?.kill(); await rm(root, { recursive: true, force: true, maxRetries: 8, retryDelay: 100 });
  }
});
