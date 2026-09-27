import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { existsSync, readFileSync } from "node:fs";
import { mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { fileURLToPath, pathToFileURL } from "node:url";
import { build } from "esbuild";
import { locales, translate } from "./localization";

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
    const languages = async () => {
      await evaluate(`new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(()=>resolve(true))))`);
      await evaluate(`void (window.languageKeep={calls:JSON.stringify([askFixture.requests.length,askFixture.answers,askFixture.cancellations,askFixture.observations]),entries:askFixture.retained().map(e=>e.request),
        inputs:[...document.querySelectorAll('input,textarea')].map(e=>({node:e,value:e.value,checked:e.checked,disabled:e.disabled})),focus:document.activeElement,
        wording:JSON.stringify([...document.querySelectorAll('[data-ask-question] h4,[data-ask-question] > p,[data-ask-question] label')].map(e=>e.textContent))})`);
      for (const locale of locales) {
        await evaluate(`askFixture.language(${JSON.stringify(locale)})`);
        assert.equal(await waitFor(`!!document.querySelector('[aria-label=${JSON.stringify(translate(locale, "Owned asks"))}]') || document.querySelector('.ask-refresh')?.textContent===${JSON.stringify(translate(locale, "Check asks"))}`), "ready");
        assert.equal(await evaluate(`languageKeep.calls===JSON.stringify([askFixture.requests.length,askFixture.answers,askFixture.cancellations,askFixture.observations]) &&
          languageKeep.entries.every((r,i)=>r===askFixture.retained()[i].request) && languageKeep.inputs.every(i=>i.node.isConnected && i.node.value===i.value && i.node.checked===i.checked && i.node.disabled===i.disabled) && document.activeElement===languageKeep.focus`), true,
          `${locale}: literal answers/choices, mounted controls/focus, original requests and RPC counts retained`);
        assert.equal(await evaluate(`languageKeep.wording===JSON.stringify([...document.querySelectorAll('[data-ask-question] h4,[data-ask-question] > p,[data-ask-question] label')].map(e=>e.textContent))`), true, `${locale}: caller wording stays literal`);
      }
      await evaluate(`askFixture.language('en')`);
      assert.equal(await waitFor(`!!document.querySelector('[aria-label="Owned asks"]') || document.querySelector('.ask-refresh')?.textContent==='Check asks'`), "ready");
    };
    await languages(); // Initial list is still pending.
    const chordProbe = (selector: string, key: string, options: Record<string, unknown> = {}, prevent = false, focus = true) =>
      evaluate(`(() => { const target=document.querySelector(${JSON.stringify(selector)});
        if (${focus}) target.focus();
        const event=new KeyboardEvent('keydown',{key:${JSON.stringify(key)},ctrlKey:true,bubbles:true,cancelable:true,...${JSON.stringify(options)}});
        if (${options.keyCode === 229}) Object.defineProperty(event,'keyCode',{value:229});
        if (${prevent}) event.preventDefault();
        target.dispatchEvent(event); return event.defaultPrevented; })()`);
    const handle = {operationId:'11111111-1111-4111-8111-111111111111', runtimeInstanceId:'22222222-2222-4222-8222-222222222222',
      attachmentGeneration:'12', providerId:'fixture-provider', sessionId:'session-one', runId:'run-one',
      askId:'33333333-3333-4333-8333-333333333333', responseGeneration:'0'};
    const question = {title:'Choice and text',question:'Which option?',description:'Detailed ask',
      choices:[{title:'First',description:null},{title:'Second',description:'second detail'}],
      freeform:{title:'Notes',placeholder:'Type exact answer'}};
    const head = {handle,request:{questions:[question]},state:'pending'};
    await evaluate(`window.askFixture.page(${JSON.stringify(head)})`);
    assert.equal(await waitFor(`!!document.querySelector('[aria-label="Owned asks"] textarea')`), 'ready');
    assert.equal(await evaluate(`!!document.querySelector('[aria-label="Ask question navigation"]')`), false,
      'single-question ask keeps its original simple editor');
    assert.equal(await chordProbe('[aria-label="Owned asks"] textarea', 'n'), false,
      'single-question input does not consume browser Ctrl+N');
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
    await languages();
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
    await languages(); // Dirty draft during an explicit pending read.
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
    assert.equal(await evaluate(`document.querySelector('[aria-label="Owned asks"]').textContent.includes('Ask refresh pending')`), false,
      'a settled failed read must not be labeled pending, although its old page remains unusable');
    assert.equal(await evaluate(`document.querySelectorAll('.ask-draft-recovery')[2].querySelector('pre').textContent`), 'Third draft');
    await click('Discard local draft…');
    await languages(); // Explicit discard review is presentation state, not a locale effect.
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
    await languages();
    assert.equal(await evaluate(`JSON.stringify(window.askFixture.answers[0].action.answers)`),
      JSON.stringify([{questionIndex:0,selectedChoiceIndexes:[0],freeformText:'Only fresh answer should submit'}]));
    assert.equal(await evaluate(`[...document.querySelectorAll('[aria-label="Owned asks"] textarea, [aria-label="Owned asks"] pre')]
      .some(el=>(el.value ?? el.textContent).includes('Only fresh answer should submit'))`), true,
      'the exact submitted answer must remain inspectable while its original waiter is pending');
    const captured = '[aria-label="Captured original ask answer"]';
    assert.equal(await evaluate(`JSON.stringify((() => {const card=document.querySelector(${JSON.stringify(captured)});
      return {text:card.querySelector('pre').textContent, indexes:card.textContent.includes('Question index 0 · selected choice indexes: 0'),
        host:card.textContent.includes(window.askFixture.epoch),session:card.textContent.includes('session-one'),
        operation:card.textContent.includes(${JSON.stringify(handle.operationId)}),
        runtime:card.textContent.includes(${JSON.stringify(handle.runtimeInstanceId)}),
        attachment:card.textContent.includes('attachment 12'),provider:card.textContent.includes('fixture-provider'),
        run:card.textContent.includes('run-one'),ask:card.textContent.includes(${JSON.stringify(handle.askId)}),
        generation:card.textContent.includes('generation 0'),action:card.textContent.includes(window.askFixture.answers[0].action.actionId),
        inventedWording:card.textContent.includes('Which option?')};})())`), JSON.stringify({
      text:'Only fresh answer should submit',indexes:true,host:true,session:true,operation:true,runtime:true,
      attachment:true,provider:true,run:true,ask:true,generation:true,action:true,inventedWording:false
    }), 'only immutable action fields, not refreshed question wording, supply captured owner evidence');
    await click('Refresh asks');
    assert.equal(await waitFor(`window.askFixture.requests.length===10`), 'ready');
    assert.equal(await evaluate(`document.querySelector(${JSON.stringify(captured)}+' pre').textContent`), 'Only fresh answer should submit');
    await evaluate(`window.askFixture.page(${JSON.stringify(head)})`);
    assert.equal(await waitFor(`document.querySelector(${JSON.stringify(captured)}+' pre')?.textContent==='Only fresh answer should submit'`), 'ready');
    await click('Answer original ask'); await click('Cancel original ask');
    assert.equal(await evaluate(`window.askFixture.answers.length+window.askFixture.cancellations.length`), 1,
      'pending owner excludes a competing answer/cancel');
    await evaluate(`window.askFixture.failAnswer()`);
    assert.equal(await waitFor(`document.querySelector('[aria-label="Owned asks"]')?.textContent.includes('uncertain')`), 'ready');
    assert.equal(await evaluate(`document.querySelector(${JSON.stringify(captured)}+' pre').textContent`), 'Only fresh answer should submit');
    assert.equal(await evaluate(`document.querySelector(${JSON.stringify(captured)}).textContent.includes('Transport uncertain')`), true);
    await languages();
    await click('Answer original ask'); await click('Cancel original ask');
    assert.equal(await evaluate(`window.askFixture.answers.length+window.askFixture.cancellations.length`), 1,
      'uncertain original answer is not retried by a refreshed editor');
    await click('Refresh asks');
    assert.equal(await waitFor(`window.askFixture.requests.length===11`), 'ready');
    await evaluate(`window.askFixture.page(${JSON.stringify(changed)})`);
    assert.equal(await waitFor(`document.querySelector('[aria-label="Owned asks"] legend')?.textContent.includes('33333333')`), 'ready');
    assert.equal(await evaluate(`document.querySelector(${JSON.stringify(captured)}+' pre').textContent`), 'Only fresh answer should submit');
    assert.equal(await evaluate(`document.querySelector(${JSON.stringify(captured)}).textContent.includes('Changed immutable question shape')`), false,
      'replacement question wording cannot be invented in the original action evidence');
    await click('Refresh asks');
    assert.equal(await waitFor(`window.askFixture.requests.length===12`), 'ready');
    await evaluate(`window.askFixture.page(null)`);
    assert.equal(await waitFor(`!document.querySelector('[aria-label="Owned asks"] fieldset')`), 'ready');
    assert.equal(await evaluate(`document.querySelector(${JSON.stringify(captured)}+' pre').textContent`), 'Only fresh answer should submit');
    assert.equal(await evaluate(`document.querySelector('[aria-label="Owned asks"]').textContent.includes('Ask refresh pending')`), false);
    await click('Refresh asks');
    assert.equal(await waitFor(`window.askFixture.requests.length===13`), 'ready');
    await evaluate(`window.askFixture.fail()`);
    assert.equal(await waitFor(`document.querySelector('[aria-label="Owned asks"]')?.textContent.includes('Ask read failed')`), 'ready');
    assert.equal(await evaluate(`document.querySelector(${JSON.stringify(captured)}+' pre').textContent`), 'Only fresh answer should submit');
    assert.equal(await evaluate(`document.querySelector('[aria-label="Owned asks"]').textContent.includes('Ask refresh pending')`), false);
    assert.equal(await evaluate(`window.askFixture.answers.length+window.askFixture.cancellations.length`), 1,
      'read/disclosure never readmits a captured answer');
    const cancelHead = {...head,handle:{...handle,askId:'44444444-4444-4444-8444-444444444444'}};
    await click('Refresh asks');
    assert.equal(await waitFor(`window.askFixture.requests.length===14`), 'ready');
    await evaluate(`window.askFixture.page(${JSON.stringify(cancelHead)})`);
    assert.equal(await waitFor(`document.querySelector('[aria-label="Owned asks"] legend')?.textContent.includes('44444444')`), 'ready');
    await write('Local draft before session replacement');
    await evaluate(`window.askFixture.scope('session-other')`);
    assert.equal(await waitFor(`window.askFixture.requests.length===15`), 'ready');
    await languages();
    assert.equal(await evaluate(`document.querySelectorAll('.ask-draft-recovery').length`), 0,
      'local recovery stays private to the original session');
    assert.equal(await evaluate(`!!document.querySelector(${JSON.stringify(captured)})`), false,
      'original answer is not exposed in another session');
    await evaluate(`window.askFixture.page(null);window.askFixture.scope('session-one')`);
    assert.equal(await waitFor(`window.askFixture.requests.length===16`), 'ready');
    await evaluate(`window.askFixture.page(${JSON.stringify(cancelHead)})`);
    assert.equal(await waitFor(`[...document.querySelectorAll('.ask-draft-recovery pre')]
      .some(el=>el.textContent==='Local draft before session replacement')`), 'ready');
    assert.equal(await evaluate(`document.querySelector(${JSON.stringify(captured)}+' pre').textContent`), 'Only fresh answer should submit',
      'app-owned action survives panel scope changes independently of the local draft');
    assert.equal(await evaluate(`document.querySelector('[aria-label="Owned asks"] textarea').value`), '',
      'scope round-trip does not silently reactivate the detached local draft');
    await languages();
    await write('Unsent when cancel was captured');
    await click('Cancel original ask');
    assert.equal(await waitFor(`window.askFixture.cancellations.length===1`), 'ready');
    await languages();
    assert.equal(await evaluate(`[...document.querySelectorAll('.ask-draft-recovery pre')].some(el=>el.textContent==='Unsent when cancel was captured')`), true);
    await click('Cancel original ask'); await click('Answer original ask');
    assert.equal(await evaluate(`window.askFixture.answers.length+window.askFixture.cancellations.length`), 2);
    await evaluate(`window.askFixture.failCancel()`);
    assert.equal(await waitFor(`document.querySelector('[aria-label="Owned asks"]')?.textContent.includes('uncertain')`), 'ready');
    assert.equal(await waitFor(`askFixture.retained().some(e=>e.kind==='cancel' && e.transport==='uncertain')`), 'ready');
    await languages();
    await evaluate(`(() => {const row=[...document.querySelectorAll('.ask-draft-recovery')]
      .find(el=>el.querySelector('pre')?.textContent==='Unsent when cancel was captured');row.querySelector('button').click();})()`);
    await click('Confirm discard local draft');
    assert.equal(await waitFor(`![...document.querySelectorAll('.ask-draft-recovery pre')]
      .some(el=>el.textContent==='Unsent when cancel was captured')`), 'ready');
    await click('Cancel original ask');
    assert.equal(await evaluate(`window.askFixture.cancellations.length`), 1);
    assert.equal(await evaluate(`document.querySelector('[aria-label="Owned asks"]').textContent.includes('Original cancel')`), true,
      'discarding the local draft cannot acknowledge an uncertain owner action');
    assert.equal(await evaluate(`document.querySelector(${JSON.stringify(captured)}+' pre').textContent`), 'Only fresh answer should submit',
      'local discard cannot erase separate immutable answer evidence');
    const malformedHead = {...head, handle:{...handle,askId:'55555555-5555-4555-8555-555555555555'}};
    await click('Refresh asks');
    assert.equal(await waitFor(`window.askFixture.requests.length===17`), 'ready');
    await evaluate(`window.askFixture.page(${JSON.stringify(malformedHead)})`);
    assert.equal(await waitFor(`document.querySelector('[aria-label="Owned asks"] legend')?.textContent.includes('55555555')`), 'ready');
    await write('Retained after malformed read');
    await click('Refresh asks');
    assert.equal(await waitFor(`window.askFixture.requests.length===18`), 'ready');
    await evaluate(`window.askFixture.page(${JSON.stringify({...malformedHead, request:{questions:[{...question,choices:[],freeform:null}]}})})`);
    assert.equal(await waitFor(`[...document.querySelectorAll('.ask-draft-recovery pre')]
      .some(el=>el.textContent==='Retained after malformed read')`), 'ready');
    assert.equal(await evaluate(`!document.querySelector('[aria-label="Owned asks"] textarea')`), true);
    assert.equal(await evaluate(`document.querySelector('[aria-label="Owned asks"]').textContent.includes('Ask refresh pending')`), false,
      'a settled malformed read blocks mutations but is not pending');
    assert.equal(await evaluate(`document.querySelector(${JSON.stringify(captured)}+' pre').textContent`), 'Only fresh answer should submit');
    await click('Refresh asks');
    assert.equal(await waitFor(`window.askFixture.requests.length===19`), 'ready');
    await evaluate(`window.askFixture.page(null,{epoch:'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb'})`);
    assert.equal(await waitFor(`document.querySelector('[aria-label="Owned asks"]')?.textContent.includes('Host identity changed')`), 'ready');
    await languages();
    for (const locale of ["de", "ja"] as const) for (const theme of ["light", "dark"]) {
      await command("Emulation.setDeviceMetricsOverride", { width: 390, height: 500, deviceScaleFactor: 1, mobile: false });
      await evaluate(`askFixture.language('${locale}');document.documentElement.dataset.theme='${theme}'`);
      assert.equal(await waitFor(`document.querySelector('h3')?.textContent===${JSON.stringify(translate(locale, "Pending asks"))}`), "ready");
      assert.equal(await evaluate(`document.documentElement.scrollWidth<=innerWidth+2 && [...document.querySelectorAll('.ask-draft-recovery,.ask-captured-answer')].every(e=>e.scrollWidth<=e.clientWidth+2)`), true, `${locale}/${theme}: short narrow retained ask evidence fits`);
    }
    await evaluate(`askFixture.language('en')`);
    assert.equal(await waitFor(`!!document.querySelector('[aria-label="Owned asks"]')`), "ready");
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
    assert.equal(await waitFor(`window.askFixture.requests.length===20`), 'ready');
    assert.equal(await evaluate(`document.querySelectorAll('.ask-draft-recovery').length`), 0,
      'no old-session text in new scope');
    assert.equal(await evaluate(`!!document.querySelector(${JSON.stringify(captured)})`), false);
    await evaluate(`window.askFixture.page(null)`);
    await evaluate(`window.askFixture.scope('session-one','bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb')`);
    assert.equal(await waitFor(`window.askFixture.requests.length===21`), 'ready');
    assert.equal(await evaluate(`document.querySelectorAll('.ask-draft-recovery').length`), 0, 'no old-epoch text in replacement host');
    assert.equal(await evaluate(`!!document.querySelector(${JSON.stringify(captured)})`), false, 'no old-epoch action text');
    await evaluate(`window.askFixture.archive(true)`);
    assert.equal(await waitFor(`!!document.querySelector('#archived-ask')`), 'ready');
    assert.equal(await evaluate(`!!document.querySelector('[aria-label="Owned asks"]')`), false);
    assert.equal(await evaluate(`!!document.querySelector('#owned-ask-gate')`), false);
    assert.equal(await evaluate(`window.askFixture.requests.length`), 21, 'actual archived composer gate cannot start another ask read');
    await evaluate(`window.askFixture.scope('session-one',window.askFixture.epoch);window.askFixture.archive(false)`);
    assert.equal(await waitFor(`window.askFixture.requests.length===22`), 'ready');
    assert.equal(await waitFor(`document.querySelector(${JSON.stringify(captured)}+' pre')?.textContent==='Only fresh answer should submit'`), 'ready',
      'remounted owned panel projects the app-owned original even without a completed new list read');
    // A fresh isolated app owner lets the same production panel exercise multi-question navigation.
    await command('Page.navigate', { url: pathToFileURL(page).href });
    assert.equal(await waitFor(`window.askFixture?.requests.length===1`), 'ready');
    const multi = {...head, request:{questions:[question,
      {title:'Options only',question:'Pick one or more',description:null,
        choices:[{title:'Red',description:null},{title:'Blue',description:null}],freeform:null},
      {title:'Text only',question:'Explain',description:null,choices:[],freeform:{title:'Explanation',placeholder:null}}]}};
    await evaluate(`window.askFixture.page(${JSON.stringify(multi)})`);
    assert.equal(await waitFor(`document.querySelector('[aria-label="Ask question position"]')?.textContent.includes('Question 1 of 3: Choice and text')`), 'ready');
    const position = () => evaluate(`document.querySelector('[aria-label="Ask question position"]')?.textContent`);
    const nav = (label: string) => evaluate(`[...document.querySelectorAll('[aria-label="Ask question navigation"] button')]
      .find(el=>el.textContent===${JSON.stringify(label)})?.click()`);
    const textarea = '[data-ask-question="0"] textarea';
    assert.equal(await chordProbe(textarea, 'p'), false, 'boundary leaves Ctrl+P to the browser');
    assert.equal(await chordProbe('[aria-label="Owned asks"] button', 'n'), false, 'Refresh does not own ask chords');
    assert.equal(await chordProbe('[aria-label="Owned asks"] fieldset > button:nth-last-child(2)', 'n'), false,
      'Answer does not own ask chords');
    assert.equal(await chordProbe('[aria-label="Owned asks"] fieldset > button:last-child', 'n'), false,
      'Cancel does not own ask chords');
    await evaluate(`(() => {const other=document.createElement('textarea');other.id='other-composer';document.body.append(other);})()`);
    assert.equal(await chordProbe('#other-composer', 'n'), false, 'ordinary composer retains browser default');
    for (const options of [{repeat:true},{isComposing:true},{keyCode:229},{shiftKey:true},{altKey:true},{metaKey:true},{ctrlKey:false}]) {
      assert.equal(await chordProbe(textarea, 'n', options), false, `unsupported chord ${JSON.stringify(options)} retains default`);
    }
    assert.equal(await chordProbe(textarea, 'n', {}, true), true, 'an already prevented event cannot navigate');
    await evaluate(`(() => {const dialog=document.createElement('div');dialog.setAttribute('role','dialog');
      dialog.setAttribute('aria-modal','true');dialog.id='ask-test-modal';document.body.append(dialog);})()`);
    assert.equal(await chordProbe(textarea, 'n'), false, 'top-layer modal retains keyboard ownership');
    await evaluate(`document.querySelector('#ask-test-modal').remove()`);
    await evaluate(`document.querySelector(${JSON.stringify(textarea)}).focus()`);
    assert.equal(await chordProbe('[data-ask-direction="1"]', 'n', {}, false, false), false,
      'dispatching on an unfocused navigation button cannot steal input focus');
    assert.equal(await position(), 'Question 1 of 3: Choice and text');
    assert.equal(await evaluate(`JSON.stringify([...document.querySelectorAll('[aria-label="Ask question navigation"] button')]
      .map(el=>el.disabled))`), '[true,false]');
    assert.equal(await evaluate(`document.querySelectorAll('[aria-label="Owned asks"] fieldset h4').length`), 1);
    await nav('Previous question');
    assert.equal(await position(), 'Question 1 of 3: Choice and text', 'previous clamps without wrapping');
    for (const locale of locales) {
      await evaluate(`askFixture.language(${JSON.stringify(locale)})`);
      assert.equal(await waitFor(`document.querySelector('[data-ask-direction="1"]').textContent===${JSON.stringify(translate(locale, "Next question"))}`), "ready");
      assert.equal(await chordProbe('[data-ask-direction="1"]', 'n', { isComposing: true }), true, `${locale}: navigation button suppresses native activation while composing`);
      assert.equal(await evaluate(`!!document.querySelector('[data-ask-question="0"]')`), true, `${locale}: composition cannot navigate`);
      assert.equal(await chordProbe('[data-ask-direction="1"]', 'n'), true, `${locale}: navigation-button dispatch cannot depend on English ARIA`);
      assert.equal(await chordProbe('[data-ask-direction="-1"]', 'p'), true);
      assert.equal(await evaluate(`!!document.querySelector('[data-ask-question="0"]')`), true);
    }
    await evaluate(`askFixture.language('en')`);
    assert.equal(await waitFor(`document.querySelector('[aria-label="Ask question position"]')?.textContent.includes('Question 1 of 3')`), "ready");
    await write('First answer 😀');
    await evaluate(`document.querySelector('[aria-label="Owned asks"] input[type=checkbox]').click()`);
    await evaluate(`document.querySelector('[data-ask-question="0"] textarea').focus()`);
    await command('Page.bringToFront');
    await command('Input.dispatchKeyEvent',{type:'keyDown',key:'n',code:'KeyN',windowsVirtualKeyCode:78,modifiers:2});
    await command('Input.dispatchKeyEvent',{type:'keyUp',key:'n',code:'KeyN',windowsVirtualKeyCode:78,modifiers:2});
    assert.equal(await position(), 'Question 2 of 3: Options only', 'local Ctrl+N advances from the actual focused ask input');
    assert.equal(await waitFor(`document.activeElement?.matches('[data-ask-question="1"] input')`), 'ready');
    await command('Input.dispatchKeyEvent',{type:'keyDown',key:'p',code:'KeyP',windowsVirtualKeyCode:80,modifiers:2});
    await command('Input.dispatchKeyEvent',{type:'keyUp',key:'p',code:'KeyP',windowsVirtualKeyCode:80,modifiers:2});
    assert.equal(await position(), 'Question 1 of 3: Choice and text');
    await evaluate(`document.querySelector('[data-ask-direction="1"]').focus()`);
    await command('Input.dispatchKeyEvent',{type:'keyDown',key:'n',code:'KeyN',windowsVirtualKeyCode:78,modifiers:2});
    await command('Input.dispatchKeyEvent',{type:'keyUp',key:'n',code:'KeyN',windowsVirtualKeyCode:78,modifiers:2});
    assert.equal(await position(), 'Question 2 of 3: Options only', 'focused Next button also owns Ctrl+N');
    assert.equal(await evaluate(`document.activeElement?.matches('[data-ask-question="1"] input')`), true);
    await evaluate(`document.querySelector('[data-ask-direction="-1"]').focus()`);
    await command('Input.dispatchKeyEvent',{type:'keyDown',key:'p',code:'KeyP',windowsVirtualKeyCode:80,modifiers:2});
    await command('Input.dispatchKeyEvent',{type:'keyUp',key:'p',code:'KeyP',windowsVirtualKeyCode:80,modifiers:2});
    assert.equal(await position(), 'Question 1 of 3: Choice and text', 'focused Previous button also owns Ctrl+P');
    await evaluate(`[...document.querySelectorAll('[aria-label="Ask question navigation"] button')]
      .find(el=>el.textContent==='Next question').focus()`);
    await evaluate(`(() => {const button=document.activeElement;
      button.dispatchEvent(new KeyboardEvent('keydown',{key:'Enter',isComposing:true,bubbles:true,cancelable:true}));
      button.dispatchEvent(new KeyboardEvent('keydown',{key:'Enter',repeat:true,bubbles:true,cancelable:true}));})()`);
    assert.equal(await position(), 'Question 1 of 3: Choice and text', 'IME/repeat cannot navigate');
    await command('Page.bringToFront');
    await command('Input.dispatchKeyEvent',{type:'keyDown',key:'Enter',code:'Enter',text:'\r',windowsVirtualKeyCode:13,nativeVirtualKeyCode:13});
    await command('Input.dispatchKeyEvent',{type:'keyUp',key:'Enter',code:'Enter',windowsVirtualKeyCode:13});
    assert.equal(await waitFor(`document.querySelector('[aria-label="Ask question position"]')?.textContent.includes('Question 2 of 3')`), 'ready');
    assert.equal(await waitFor(`document.activeElement?.matches('[data-ask-question="1"] input')`), 'ready',
      'keyboard Next focuses a connected input only after explicit navigation');
    assert.equal(await evaluate(`document.querySelectorAll('[aria-label="Owned asks"] fieldset h4').length`), 1);
    assert.equal(await evaluate(`!!document.querySelector('[aria-label="Owned asks"] fieldset textarea')`), false);
    await evaluate(`document.querySelectorAll('[data-ask-question="1"] input[type=checkbox]')[1].click()`);
    await evaluate(`[...document.querySelectorAll('[aria-label="Ask question navigation"] button')]
      .find(el=>el.textContent==='Next question').focus()`);
    await nav('Next question');
    assert.equal(await waitFor(`document.activeElement?.matches('[data-ask-question="2"] textarea')`), 'ready');
    assert.equal(await position(), 'Question 3 of 3: Text only');
    assert.equal(await evaluate(`JSON.stringify([...document.querySelectorAll('[aria-label="Ask question navigation"] button')]
      .map(el=>el.disabled))`), '[false,true]');
    await nav('Next question');
    assert.equal(await position(), 'Question 3 of 3: Text only', 'next clamps without wrapping');
    await write('Third answer\nline two');
    assert.equal(await chordProbe('[data-ask-question="2"] textarea', 'n'), false,
      'last-question boundary preserves browser Ctrl+N');
    assert.equal(await evaluate(`window.askFixture.answers.length+window.askFixture.cancellations.length+window.askFixture.requests.length`), 1,
      'navigation is local: no submission, cancel, observation or read');
    await click('Refresh asks');
    assert.equal(await waitFor(`window.askFixture.requests.length===2`), 'ready');
    assert.equal(await chordProbe('[data-ask-question="2"] textarea', 'p'), true,
      'same-head in-flight read still permits local keyboard navigation');
    assert.equal(await position(), 'Question 2 of 3: Options only');
    assert.equal(await evaluate(`document.querySelectorAll('[data-ask-question="1"] input[type=checkbox]')[1].checked`), true);
    assert.equal(await chordProbe('[data-ask-question="1"] input[type=checkbox]', 'p'), true,
      'the actual current choice input owns the local chord');
    assert.equal(await evaluate(`document.querySelector('[data-ask-question="0"] textarea').value`), 'First answer 😀');
    await write('Edited while same-head read pending');
    await click('Answer original ask');
    assert.equal(await evaluate(`window.askFixture.answers.length`), 0, 'in-flight read still fences admission');
    await evaluate(`window.askFixture.page(${JSON.stringify(multi)})`);
    assert.equal(await waitFor(`document.querySelector('[data-ask-question="0"] textarea')?.value==='Edited while same-head read pending'`), 'ready');
    assert.equal(await position(), 'Question 1 of 3: Choice and text');
    await nav('Next question'); await nav('Next question');
    assert.equal(await evaluate(`document.querySelector('[data-ask-question="2"] textarea').value`), 'Third answer\nline two');
    assert.equal(await evaluate(`!!document.querySelector('[data-ask-question="2"] input')`), false);
    const revisedMulti = {...multi, request:{questions:[multi.request.questions[0],
      {...multi.request.questions[1],description:'Different immutable question shape'},multi.request.questions[2]]}};
    await evaluate(`window.__oldAskInput=document.querySelector('[data-ask-question="2"] textarea');true`);
    await click('Refresh asks');
    assert.equal(await waitFor(`window.askFixture.requests.length===3`), 'ready');
    await evaluate(`window.askFixture.page(${JSON.stringify(revisedMulti)})`);
    assert.equal(await waitFor(`document.querySelectorAll('.ask-draft-recovery').length===1`), 'ready');
    assert.equal(await position(), 'Question 1 of 3: Choice and text', 'changed question shape resets selection');
    assert.equal(await evaluate(`document.querySelector('.ask-draft-recovery').textContent.includes('Third answer')`), true);
    assert.equal(await evaluate(`(() => {const event=new KeyboardEvent('keydown',{key:'p',ctrlKey:true,bubbles:true,cancelable:true});
      window.__oldAskInput.dispatchEvent(event);return event.defaultPrevented;})()`), false,
    'a detached old-shape input cannot navigate or consume browser Ctrl+P');
    assert.equal(await chordProbe('.ask-draft-recovery button', 'n'), false,
      'recovery controls do not own ask chords');
    await nav('Next question');
    assert.equal(await evaluate(`document.querySelector('[data-ask-question="1"] input').checked`), false);
    await click('Refresh asks');
    assert.equal(await waitFor(`window.askFixture.requests.length===4`), 'ready');
    await evaluate(`window.askFixture.page(${JSON.stringify(multi)})`);
    assert.equal(await waitFor(`document.querySelector('[aria-label="Ask question position"]')?.textContent.includes('Question 1 of 3')`), 'ready',
      'A-B-A cannot revive a detached selection or answers');
    assert.equal(await evaluate(`document.querySelector('[data-ask-question="0"] textarea').value`), '');
    await nav('Next question');
    assert.equal(await evaluate(`document.querySelector('[data-ask-question="1"] input').checked`), false);
    await nav('Next question');
    await write('Recoverable third question');
    await click('Refresh asks');
    assert.equal(await waitFor(`window.askFixture.requests.length===5`), 'ready');
    await evaluate(`window.askFixture.page(null)`);
    assert.equal(await waitFor(`!document.querySelector('[aria-label="Ask question position"]')`), 'ready');
    assert.equal(await evaluate(`[...document.querySelectorAll('.ask-draft-recovery')]
      .some(el=>el.textContent.includes('Recoverable third question'))`), true);
    await click('Refresh asks');
    assert.equal(await waitFor(`window.askFixture.requests.length===6`), 'ready');
    await evaluate(`window.askFixture.fail()`);
    assert.equal(await waitFor(`document.querySelector('[aria-label="Owned asks"]')?.textContent.includes('Ask read failed')`), 'ready');
    assert.equal(await evaluate(`window.askFixture.answers.length`), 0);
    await click('Refresh asks');
    assert.equal(await waitFor(`window.askFixture.requests.length===7`), 'ready');
    await evaluate(`window.askFixture.page(${JSON.stringify({...multi,request:{questions:[multi.request.questions[0],
      {...multi.request.questions[1],question:''},multi.request.questions[2]]}})})`);
    assert.equal(await waitFor(`document.querySelector('[aria-label="Owned asks"]')?.textContent.includes('Ask read failed')`), 'ready');
    assert.equal(await evaluate(`!!document.querySelector('[aria-label="Ask question position"]')`), false,
      'malformed page cannot authorize navigation or submission');
    await evaluate(`window.askFixture.scope('session-other')`);
    assert.equal(await waitFor(`window.askFixture.requests.length===8`), 'ready');
    assert.equal(await evaluate(`document.querySelectorAll('.ask-draft-recovery').length`), 0);
    await evaluate(`window.askFixture.page(null);window.askFixture.scope('session-one','bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb')`);
    assert.equal(await waitFor(`window.askFixture.requests.length===9`), 'ready');
    assert.equal(await evaluate(`document.querySelectorAll('.ask-draft-recovery').length`), 0);
    await evaluate(`window.askFixture.page(null);window.askFixture.scope('session-one',window.askFixture.epoch)`);
    assert.equal(await waitFor(`window.askFixture.requests.length===10`), 'ready');
    await evaluate(`window.askFixture.page(${JSON.stringify(multi)})`);
    assert.equal(await waitFor(`document.querySelector('[aria-label="Ask question position"]')?.textContent.includes('Question 1 of 3')`), 'ready');
    assert.equal(await evaluate(`document.querySelector('[data-ask-question="0"] textarea').value`), '');
    assert.equal(await evaluate(`[...document.querySelectorAll('.ask-draft-recovery')]
      .some(el=>el.textContent.includes('Recoverable third question'))`), true,
      'restored scope retains separate read-only recovery without reactivating it');
    for (const theme of ['dark','light']) for (const width of [390,1120]) {
      await evaluate(`document.documentElement.dataset.theme=${JSON.stringify(theme)}`);
      await command('Emulation.setDeviceMetricsOverride',{width,height:680,deviceScaleFactor:1,mobile:false});
      assert.equal(await evaluate(`document.querySelector('[aria-label="Ask question navigation"]').getBoundingClientRect().right<=innerWidth
        && document.documentElement.scrollWidth<=innerWidth`), true, `${theme}/${width} navigation stays within viewport`);
      assert.equal(await evaluate(`getComputedStyle(document.querySelector('[aria-label="Ask question navigation"]')).display`), 'flex');
    }
    await write('Final first answer');
    await evaluate(`document.querySelector('[data-ask-question="0"] input[type=checkbox]').click()`);
    await evaluate(`(() => { const button=[...document.querySelectorAll('[aria-label="Ask question navigation"] button')]
      .find(el=>el.textContent==='Next question'); button.focus(); button.click();
      document.querySelector('[aria-label="Owned asks"] > button').focus(); })()`);
    assert.equal(await waitFor(`document.querySelector('[aria-label="Ask question position"]')?.textContent.includes('Question 2 of 3')`), 'ready');
    assert.equal(await evaluate(`document.activeElement?.textContent`), 'Refresh asks',
      'explicit question focus never steals a newer user focus');
    await evaluate(`document.querySelectorAll('[data-ask-question="1"] input[type=checkbox]')[1].click()`);
    await nav('Next question');
    await write('Final third answer\n😀');
    await nav('Previous question');
    assert.equal(await evaluate(`document.querySelectorAll('[data-ask-question="1"] input[type=checkbox]')[1].checked`), true);
    await nav('Previous question');
    assert.equal(await evaluate(`document.querySelector('[data-ask-question="0"] textarea').value`), 'Final first answer');
    await click('Answer original ask');
    assert.equal(await waitFor(`window.askFixture.answers.length===1`), 'ready');
    assert.equal(await chordProbe('[data-ask-question="0"] textarea', 'n'), false,
      'admitted action blocks further keyboard navigation');
    assert.equal(await evaluate(`JSON.stringify(window.askFixture.answers[0].action.answers)`), JSON.stringify([
      {questionIndex:0,selectedChoiceIndexes:[0],freeformText:'Final first answer'},
      {questionIndex:1,selectedChoiceIndexes:[1],freeformText:null},
      {questionIndex:2,selectedChoiceIndexes:[],freeformText:'Final third answer\n😀'}
    ]), 'one explicit Answer captures every question, including a hidden text answer');
    assert.equal(await evaluate(`document.querySelectorAll('[aria-label="Captured original ask answer"] [aria-label^="Captured answer"]').length`), 2);
    await click('Answer original ask'); await click('Cancel original ask');
    assert.equal(await evaluate(`window.askFixture.answers.length+window.askFixture.cancellations.length`), 1);
    await evaluate(`window.askFixture.failAnswer()`);
    assert.equal(await waitFor(`document.querySelector('[aria-label="Captured original ask answer"]')?.textContent.includes('Transport uncertain')`), 'ready');
    assert.equal(await chordProbe('[data-ask-question="0"] textarea', 'n'), false,
      'uncertain action cannot reopen question navigation');
    await click('Discard local draft…'); await click('Confirm discard local draft');
    assert.equal(await evaluate(`document.querySelector('[aria-label="Captured original ask answer"]').textContent.includes('Final first answer')`), true,
      'draft discard does not erase independent submitted evidence');
    await evaluate(`window.askFixture.scope('session-other')`);
    assert.equal(await waitFor(`window.askFixture.requests.length===11`), 'ready');
    assert.equal(await evaluate(`!!document.querySelector('[aria-label="Captured original ask answer"]')`), false);
    await evaluate(`window.askFixture.page(null);window.askFixture.scope('session-one','bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb')`);
    assert.equal(await waitFor(`window.askFixture.requests.length===12`), 'ready');
    assert.equal(await evaluate(`!!document.querySelector('[aria-label="Captured original ask answer"]')`), false);
    await evaluate(`window.askFixture.page(null);window.askFixture.scope('session-one',window.askFixture.epoch)`);
    assert.equal(await waitFor(`window.askFixture.requests.length===13`), 'ready');
    assert.equal(await waitFor(`document.querySelector('[aria-label="Captured original ask answer"]')?.textContent.includes('Final third answer')`), 'ready');
    await evaluate(`window.askFixture.page(null)`);
    assert.equal(await waitFor(`document.querySelector('[aria-label="Owned asks"]')?.textContent.includes('No pending head reported')`), 'ready');
    const nextMulti = {...multi,handle:{...handle,askId:'99999999-9999-4999-8999-999999999999'}};
    await click('Refresh asks');
    assert.equal(await waitFor(`window.askFixture.requests.length===14`), 'ready');
    await evaluate(`window.askFixture.page(${JSON.stringify(nextMulti)})`);
    assert.equal(await waitFor(`document.querySelector('[aria-label="Ask question position"]')?.textContent.includes('Question 1 of 3')`), 'ready');
    assert.equal(await evaluate(`document.querySelector('[data-ask-question="0"] textarea').value`), '');
    await click('Answer original ask');
    assert.equal(await waitFor(`window.askFixture.answers.length===2`), 'ready');
    assert.equal(await evaluate(`JSON.stringify(window.askFixture.answers[1].action.answers)`), JSON.stringify([
      {questionIndex:0,selectedChoiceIndexes:[],freeformText:null},
      {questionIndex:1,selectedChoiceIndexes:[],freeformText:null},
      {questionIndex:2,selectedChoiceIndexes:[],freeformText:null}
    ]), 'unvisited questions are still represented exactly under existing validation');
    await evaluate(`window.__oldAskInput=document.querySelector('[data-ask-question="0"] textarea');window.askFixture.scope('session-other')`);
    assert.equal(await waitFor(`window.askFixture.requests.length===15`), 'ready');
    assert.equal(await evaluate(`(() => {const event=new KeyboardEvent('keydown',{key:'n',ctrlKey:true,bubbles:true,cancelable:true});
      window.__oldAskInput.dispatchEvent(event);return event.defaultPrevented;})()`), false,
    'old-session editor cannot consume Ctrl+N');
    await evaluate(`window.askFixture.page(null);window.askFixture.scope('session-one','bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb')`);
    assert.equal(await waitFor(`window.askFixture.requests.length===16`), 'ready');
    assert.equal(await evaluate(`(() => {const event=new KeyboardEvent('keydown',{key:'p',ctrlKey:true,bubbles:true,cancelable:true});
      window.__oldAskInput.dispatchEvent(event);return event.defaultPrevented;})()`), false,
    'old-epoch editor cannot consume Ctrl+P');
    await command('Page.navigate', { url: pathToFileURL(page).href });
    assert.equal(await waitFor(`window.askFixture?.requests.length===1`), 'ready');
    await evaluate(`askFixture.page(${JSON.stringify({ ...head, request: { questions: [{ title: "Answer", question: "Reminders", description: "Settings",
      choices: [{ title: "None", description: "Refresh asks" }], freeform: { title: "Answer", placeholder: "Keep draft" } }] } })})`);
    assert.equal(await waitFor(`document.querySelector('[data-ask-question] h4')?.textContent==='Answer'`), 'ready');
    await write('  Answer\nNone 日本語  ');
    await evaluate(`document.querySelector('[data-ask-question] input').click();document.querySelector('[data-ask-question] textarea').focus()`);
    await languages();
    assert.equal(await evaluate(`document.querySelector('[data-ask-question] textarea').placeholder`), 'Keep draft');
    for (const locale of ['de', 'ja'] as const) for (const theme of ['light', 'dark']) {
      await command('Emulation.setDeviceMetricsOverride', { width: 390, height: 500, deviceScaleFactor: 1, mobile: false });
      await evaluate(`askFixture.language('${locale}');document.documentElement.dataset.theme='${theme}'`);
      assert.equal(await waitFor(`document.querySelector('h3')?.textContent===${JSON.stringify(translate(locale, 'Pending asks'))}`), 'ready');
      assert.equal(await evaluate(`document.documentElement.scrollWidth<=innerWidth+2 && document.querySelector('fieldset').scrollWidth<=document.querySelector('fieldset').clientWidth+2`), true, `${locale}/${theme}: short narrow caller editor fits`);
    }
    await evaluate(`askFixture.language('en')`);
    assert.equal(await waitFor(`!!document.querySelector('[aria-label="Owned asks"]')`), 'ready');
    await click('Answer original ask');
    assert.equal(await waitFor(`askFixture.answers.length===1`), 'ready');
    await languages();
    assert.equal(await evaluate(`askFixture.answers[0].action.answers[0].freeformText`), '  Answer\nNone 日本語  ');
    assert.equal(await evaluate(`JSON.stringify(askFixture.answers[0].action.answers[0].selectedChoiceIndexes)`), '[0]');
  } finally {
    socket?.close(); browser?.kill(); await rm(root, { recursive: true, force: true, maxRetries: 8, retryDelay: 100 });
  }
});
