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

// Mounts the production AskPanel with its real action owner and isolated list transport.
test("mounted production ask editor retains only exact validated drafts through explicit reads", { skip: !edge, timeout: 60_000 }, async () => {
  const app = readFileSync(fileURLToPath(new URL("./main.tsx", import.meta.url)), "utf8");
  assert.match(app, /\{ownedSession && status\?\.hostEpoch[\s\S]*?status\.ownedAsksEnabled && <AskPanel/,
    "production mounts this editor only in the owned, enabled session scope");
  const root = await mkdtemp(join(tmpdir(), "codealta-ask-draft-"));
  let browser: ReturnType<typeof spawn> | undefined;
  let socket: WebSocket | undefined;
  try {
    await build({ entryPoints: [fileURLToPath(new URL("./askDraft.mount.tsx", import.meta.url))],
      outfile: join(root, "fixture.js"), bundle: true, platform: "browser", format: "iife",
      plugins: [{ name: "isolated-choices", setup(build) {
        build.onResolve({ filter: /^#neoastra$/ }, () => ({ path: fileURLToPath(new URL("./askDraft.neoastra.mount.ts", import.meta.url)) }));
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
      if (window.askFixture?.requests.length) resolve('ready');
      else if (Date.now() > end) resolve(document.body.innerText.slice(0, 300)); else setTimeout(check, 35); }; check(); })`);
    assert.equal(ready, "ready");
    const waitFor = (condition: string) => evaluate(`new Promise(resolve => { const end = Date.now() + 4000; const check = () => {
      if (${condition}) resolve('ready'); else if (Date.now() > end) resolve('timed out'); else setTimeout(check, 25); }; check(); })`);
    const handle = {operationId:'11111111-1111-4111-8111-111111111111', runtimeInstanceId:'22222222-2222-4222-8222-222222222222',
      attachmentGeneration:'12', providerId:'fixture-provider', sessionId:'session-one', runId:'run-one',
      askId:'33333333-3333-4333-8333-333333333333', responseGeneration:'0'};
    const question = {title:'Choice and text',question:'Which option?',description:'Detailed ask',
      choices:[{title:'First',description:null},{title:'Second',description:'second detail'}],
      freeform:{title:'Notes',placeholder:'Type exact answer'}};
    const head = {handle,request:{questions:[question]},state:'pending'};
    await evaluate(`window.askFixture.page(${JSON.stringify(head)})`);
    assert.equal(await waitFor(`!!document.querySelector('[aria-label="Owned asks"] textarea')`), 'ready');
    await evaluate(`(() => {const el=document.querySelector('[aria-label="Owned asks"] textarea');
      Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype,'value').set.call(el,${JSON.stringify('Exact unsent text 😀\nline two')});
      el.dispatchEvent(new Event('input',{bubbles:true}));document.querySelector('[aria-label="Owned asks"] input[type=checkbox]').click();
      document.querySelector('[aria-label="Owned asks"] button').click();})()`);
    assert.equal(await waitFor(`window.askFixture.requests.length===2`), 'ready');
    await evaluate(`window.askFixture.page(${JSON.stringify(head)})`);
    assert.equal(await waitFor(`document.querySelector('[aria-label="Owned asks"] textarea')?.value===${JSON.stringify('Exact unsent text 😀\nline two')}`), 'ready',
      'same validated ask refresh preserves unsubmitted text');
    assert.equal(await evaluate(`document.querySelector('[aria-label="Owned asks"] input[type=checkbox]').checked`), true);
    assert.equal(await evaluate(`window.askFixture.answers.length+window.askFixture.cancellations.length`), 0);
    const write = (value: string) => evaluate(`(() => {const el=document.querySelector('[aria-label="Owned asks"] textarea');
      Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype,'value').set.call(el,${JSON.stringify(value)});
      el.dispatchEvent(new Event('input',{bubbles:true}));})()`);
    const click = (label: string) => evaluate(`[...document.querySelectorAll('[aria-label="Owned asks"] button')]
      .find(b=>b.textContent===${JSON.stringify(label)})?.click()`);
    await click('Refresh asks');
    assert.equal(await waitFor(`window.askFixture.requests.length===3`), 'ready');
    assert.equal(await evaluate(`[...document.querySelectorAll('[aria-label="Owned asks"] button')]
      .find(b=>b.textContent==='Answer original ask').disabled`), true, 'no stale page submit during an explicit read');
    await write('Changed while refresh pending\nwith emoji 😀');
    await evaluate(`document.querySelectorAll('[aria-label="Owned asks"] input[type=checkbox]')[1].click()`);
    await click('Answer original ask'); await click('Cancel original ask');
    assert.equal(await evaluate(`window.askFixture.answers.length+window.askFixture.cancellations.length`), 0);
    await evaluate(`window.askFixture.page(${JSON.stringify(head)})`);
    assert.equal(await waitFor(`document.querySelector('[aria-label="Owned asks"] textarea')?.value===
      ${JSON.stringify('Changed while refresh pending\nwith emoji 😀')}`), 'ready');
    assert.equal(await evaluate(`JSON.stringify([...document.querySelectorAll('[aria-label="Owned asks"] input[type=checkbox]')]
      .map(el=>el.checked))`), '[true,true]');
    const changed = { ...head, request:{questions:[{...question,description:'Changed immutable question shape'}]} };
    await click('Refresh asks');
    assert.equal(await waitFor(`window.askFixture.requests.length===4`), 'ready');
    await evaluate(`window.askFixture.page(${JSON.stringify(changed)})`);
    assert.equal(await waitFor(`!!document.querySelector('.ask-draft-recovery')`), 'ready');
    assert.equal(await evaluate(`document.querySelector('.ask-draft-recovery pre').textContent`), 'Changed while refresh pending\nwith emoji 😀');
    assert.equal(await evaluate(`document.querySelector('.ask-draft-recovery').textContent.includes('0: First, 1: Second')`), true);
    assert.equal(await evaluate(`document.querySelector('[aria-label="Owned asks"] textarea').value`), '', 'changed question cannot inherit old answer');
    // A -> B -> A must not silently revive the old A draft even with the same handle.
    await click('Refresh asks');
    assert.equal(await waitFor(`window.askFixture.requests.length===5`), 'ready');
    await evaluate(`window.askFixture.page(${JSON.stringify(head)})`);
    assert.equal(await waitFor(`document.querySelector('[aria-label="Owned asks"] textarea')?.value===''`), 'ready');
    assert.equal(await evaluate(`document.querySelector('.ask-draft-recovery pre').textContent`), 'Changed while refresh pending\nwith emoji 😀');
    await write('New draft for same-shaped ask');
    await click('Refresh asks');
    assert.equal(await waitFor(`window.askFixture.requests.length===6`), 'ready');
    await evaluate(`window.askFixture.page(${JSON.stringify({...head, handle:{...handle,responseGeneration:'1'}})})`);
    assert.equal(await waitFor(`document.querySelectorAll('.ask-draft-recovery').length===2`), 'ready');
    assert.equal(await evaluate(`document.querySelector('[aria-label="Owned asks"] textarea').value`), '');
    await write('Third draft');
    await click('Refresh asks');
    assert.equal(await waitFor(`window.askFixture.requests.length===7`), 'ready');
    await evaluate(`window.askFixture.fail()`);
    assert.equal(await waitFor(`document.querySelectorAll('.ask-draft-recovery').length===3 &&
      !document.querySelector('[aria-label="Owned asks"] textarea')`), 'ready', 'failed read retains old drafts only as read-only recovery');
    assert.equal(await evaluate(`document.querySelectorAll('.ask-draft-recovery')[2].querySelector('pre').textContent`), 'Third draft');
    await click('Discard local draft…');
    assert.equal(await evaluate(`document.querySelectorAll('.ask-draft-recovery').length`), 3, 'discard requires confirmation');
    await evaluate(`[...document.querySelectorAll('.ask-draft-recovery button')]
      .find(b=>b.textContent==='Confirm discard local draft').focus()`);
    await command('Page.bringToFront');
    await command('Input.dispatchKeyEvent',{type:'keyDown',key:'Enter',code:'Enter',text:'\r',windowsVirtualKeyCode:13,nativeVirtualKeyCode:13});
    await command('Input.dispatchKeyEvent',{type:'keyUp',key:'Enter',code:'Enter',windowsVirtualKeyCode:13});
    assert.equal(await waitFor(`document.querySelectorAll('.ask-draft-recovery').length===2`), 'ready');
    await click('Refresh asks');
    assert.equal(await waitFor(`window.askFixture.requests.length===8`), 'ready');
    await evaluate(`window.askFixture.page(null)`);
    assert.equal(await waitFor(`!document.querySelector('[aria-label="Owned asks"] textarea')`), 'ready');
    assert.equal(await evaluate(`document.querySelectorAll('.ask-draft-recovery').length`), 2);
    await click('Refresh asks');
    assert.equal(await waitFor(`window.askFixture.requests.length===9`), 'ready');
    await evaluate(`window.askFixture.page(${JSON.stringify(head)})`);
    assert.equal(await waitFor(`!!document.querySelector('[aria-label="Owned asks"] textarea')`), 'ready');
    assert.equal(await evaluate(`document.querySelector('[aria-label="Owned asks"] textarea').value`), '');
    await write('Only fresh answer should submit');
    await evaluate(`document.querySelector('[aria-label="Owned asks"] input[type=checkbox]').click()`);
    await click('Answer original ask');
    assert.equal(await waitFor(`window.askFixture.answers.length===1`), 'ready');
    assert.equal(await evaluate(`JSON.stringify(window.askFixture.answers[0].action.answers)`),
      JSON.stringify([{questionIndex:0,selectedChoiceIndexes:[0],freeformText:'Only fresh answer should submit'}]));
    await click('Answer original ask'); await click('Cancel original ask');
    assert.equal(await evaluate(`window.askFixture.answers.length+window.askFixture.cancellations.length`), 1,
      'pending owner excludes a competing answer/cancel');
    await evaluate(`window.askFixture.failAnswer()`);
    assert.equal(await waitFor(`document.querySelector('[aria-label="Owned asks"]')?.textContent.includes('uncertain')`), 'ready');
    await click('Answer original ask'); await click('Cancel original ask');
    assert.equal(await evaluate(`window.askFixture.answers.length+window.askFixture.cancellations.length`), 1,
      'uncertain original answer is not retried by a refreshed editor');
    const cancelHead = {...head,handle:{...handle,askId:'44444444-4444-4444-8444-444444444444'}};
    await click('Refresh asks');
    assert.equal(await waitFor(`window.askFixture.requests.length===10`), 'ready');
    await evaluate(`window.askFixture.page(${JSON.stringify(cancelHead)})`);
    assert.equal(await waitFor(`document.querySelector('[aria-label="Owned asks"] legend')?.textContent.includes('44444444')`), 'ready');
    await write('Local draft before session replacement');
    await evaluate(`window.askFixture.scope('session-other')`);
    assert.equal(await waitFor(`window.askFixture.requests.length===11`), 'ready');
    assert.equal(await evaluate(`document.querySelectorAll('.ask-draft-recovery').length`), 0,
      'local recovery stays private to the original session');
    await evaluate(`window.askFixture.page(null);window.askFixture.scope('session-one')`);
    assert.equal(await waitFor(`window.askFixture.requests.length===12`), 'ready');
    await evaluate(`window.askFixture.page(${JSON.stringify(cancelHead)})`);
    assert.equal(await waitFor(`[...document.querySelectorAll('.ask-draft-recovery pre')]
      .some(el=>el.textContent==='Local draft before session replacement')`), 'ready');
    assert.equal(await evaluate(`document.querySelector('[aria-label="Owned asks"] textarea').value`), '',
      'scope round-trip does not silently reactivate the detached local draft');
    await write('Unsent when cancel was captured');
    await click('Cancel original ask');
    assert.equal(await waitFor(`window.askFixture.cancellations.length===1`), 'ready');
    assert.equal(await evaluate(`[...document.querySelectorAll('.ask-draft-recovery pre')].some(el=>el.textContent==='Unsent when cancel was captured')`), true);
    await click('Cancel original ask'); await click('Answer original ask');
    assert.equal(await evaluate(`window.askFixture.answers.length+window.askFixture.cancellations.length`), 2);
    await evaluate(`window.askFixture.failCancel()`);
    assert.equal(await waitFor(`document.querySelector('[aria-label="Owned asks"]')?.textContent.includes('uncertain')`), 'ready');
    await evaluate(`(() => {const row=[...document.querySelectorAll('.ask-draft-recovery')]
      .find(el=>el.querySelector('pre')?.textContent==='Unsent when cancel was captured');row.querySelector('button').click();})()`);
    await click('Confirm discard local draft');
    assert.equal(await waitFor(`![...document.querySelectorAll('.ask-draft-recovery pre')]
      .some(el=>el.textContent==='Unsent when cancel was captured')`), 'ready');
    await click('Cancel original ask');
    assert.equal(await evaluate(`window.askFixture.cancellations.length`), 1);
    assert.equal(await evaluate(`document.querySelector('[aria-label="Owned asks"]').textContent.includes('Original cancel')`), true,
      'discarding the local draft cannot acknowledge an uncertain owner action');
    const malformedHead = {...head, handle:{...handle,askId:'55555555-5555-4555-8555-555555555555'}};
    await click('Refresh asks');
    assert.equal(await waitFor(`window.askFixture.requests.length===13`), 'ready');
    await evaluate(`window.askFixture.page(${JSON.stringify(malformedHead)})`);
    assert.equal(await waitFor(`document.querySelector('[aria-label="Owned asks"] legend')?.textContent.includes('55555555')`), 'ready');
    await write('Retained after malformed read');
    await click('Refresh asks');
    assert.equal(await waitFor(`window.askFixture.requests.length===14`), 'ready');
    await evaluate(`window.askFixture.page(${JSON.stringify({...malformedHead, request:{questions:[{...question,choices:[],freeform:null}]}})})`);
    assert.equal(await waitFor(`[...document.querySelectorAll('.ask-draft-recovery pre')]
      .some(el=>el.textContent==='Retained after malformed read')`), 'ready');
    assert.equal(await evaluate(`!document.querySelector('[aria-label="Owned asks"] textarea')`), true);
    await click('Refresh asks');
    assert.equal(await waitFor(`window.askFixture.requests.length===15`), 'ready');
    await evaluate(`window.askFixture.page(null,{epoch:'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb'})`);
    assert.equal(await waitFor(`document.querySelector('[aria-label="Owned asks"]')?.textContent.includes('Host identity changed')`), 'ready');
    assert.equal(await evaluate(`[...document.querySelectorAll('.ask-draft-recovery pre')]
      .some(el=>el.textContent==='Retained after malformed read')`), true, 'stale host cannot erase local recovery');
    for (const width of [390,1120]) for (const theme of ['dark','light']) {
      await command('Emulation.setDeviceMetricsOverride',{width,height:800,deviceScaleFactor:1,mobile:false});
      await evaluate(`document.documentElement.dataset.theme=${JSON.stringify(theme)}`);
      assert.equal(await evaluate(`document.documentElement.scrollWidth<=innerWidth+2 &&
        document.querySelector('.ask-draft-recovery').scrollWidth<=document.querySelector('.ask-draft-recovery').clientWidth+2`), true,
      `${width}/${theme} read-only recovery remains bounded`);
    }
    await evaluate(`window.askFixture.scope('session-other')`);
    assert.equal(await waitFor(`window.askFixture.requests.length===16`), 'ready');
    assert.equal(await evaluate(`document.querySelectorAll('.ask-draft-recovery').length`), 0,
      'no old-session text in new scope');
    await evaluate(`window.askFixture.page(null)`);
    await evaluate(`window.askFixture.scope('session-one','bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb')`);
    assert.equal(await waitFor(`window.askFixture.requests.length===17`), 'ready');
    assert.equal(await evaluate(`document.querySelectorAll('.ask-draft-recovery').length`), 0, 'no old-epoch text in replacement host');
    await evaluate(`window.askFixture.archive(true)`);
    assert.equal(await waitFor(`!!document.querySelector('#archived-ask')`), 'ready');
    assert.equal(await evaluate(`!!document.querySelector('[aria-label="Owned asks"]')`), false);
    assert.equal(await evaluate(`!!document.querySelector('#owned-ask-gate')`), false);
    assert.equal(await evaluate(`window.askFixture.requests.length`), 17, 'actual archived composer gate cannot start another ask read');
  } finally {
    socket?.close(); browser?.kill(); await rm(root, { recursive: true, force: true, maxRetries: 8, retryDelay: 100 });
  }
});
