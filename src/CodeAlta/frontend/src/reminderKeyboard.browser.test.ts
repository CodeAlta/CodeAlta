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

test("mounted reminder shortcuts stay panel-scoped, guarded and never bypass confirmed Delete", { skip: !edge, timeout: 60_000 }, async () => {
  const root = await mkdtemp(join(tmpdir(), "codealta-reminder-keys-"));
  let browser: ReturnType<typeof spawn> | undefined;
  let socket: WebSocket | undefined;
  try {
    await build({ entryPoints: [fileURLToPath(new URL("./reminder.mount.tsx", import.meta.url))],
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
    const wait = async (condition: string) => evaluate(`new Promise(resolve => { const end=Date.now()+7000; const check=()=>{
      if (${condition}) resolve('ready'); else if (Date.now()>end) resolve(document.body.innerText.slice(0,500));
      else setTimeout(check,20); }; check(); })`);
    const press = async (key: string, code: string, virtual: number, ctrl = false) => {
      await command("Input.dispatchKeyEvent", { type: "keyDown", key, code, windowsVirtualKeyCode: virtual, modifiers: ctrl ? 2 : 0 });
      await command("Input.dispatchKeyEvent", { type: "keyUp", key, code, windowsVirtualKeyCode: virtual, modifiers: ctrl ? 2 : 0 });
    };
    const edit = async (selector: string, content: string) => evaluate(`(() => { const input=document.querySelector('${selector}');
      const type=input instanceof HTMLTextAreaElement ? HTMLTextAreaElement : HTMLInputElement;
      Object.getOwnPropertyDescriptor(type.prototype,'value').set.call(input,${JSON.stringify(content)});
      input.dispatchEvent(new Event('input',{bubbles:true})); })()`);
    const synthetic = async (selector: string, key: string, extras = "") => evaluate(`(() => {
      const event=new KeyboardEvent('keydown',{key:${JSON.stringify(key)},code:'Key${key.toUpperCase()}',ctrlKey:true,bubbles:true,cancelable:true,${extras}});
      document.querySelector('${selector}').dispatchEvent(event); return event.defaultPrevented; })()`);
    const createKey = async (selector: string, extras = "") => evaluate(`(() => {
      const source=document.querySelector(${JSON.stringify(selector)}); source.focus();
      const event=new KeyboardEvent('keydown',{key:'Enter',code:'Enter',ctrlKey:true,bubbles:true,cancelable:true,${extras}});
      source.dispatchEvent(event); return event.defaultPrevented; })()`);
    const row = "{id:'reminder-1',state:'active',preview:'original',delaySeconds:60,repeatCount:1,firedCount:0,dueAt:null,lastExitCode:null,lastError:null}";
    const list = (session: string, epoch = "e1") => `({status:'ok',epoch:'${epoch}',sessionId:'${session}',reminders:[${row}],activeCount:1,completedCount:0})`;
    await evaluate("window.shellEvents=0; window.addEventListener('keydown',()=>window.shellEvents++)");
    assert.equal(await wait("window.reminderFixture.reads.length === 1"), "ready");
    await evaluate(`window.reminderFixture.reads[0].resolve(${list("one")})`);
    assert.equal(await wait("!!document.querySelector('[aria-label=\"Reminder list\"] button')"), "ready");
    await evaluate("document.querySelector('[aria-label=\"Reminder list\"] button').click()");
    assert.equal(await wait("window.reminderFixture.details.length === 1"), "ready");
    await evaluate("window.reminderFixture.details[0].resolve({status:'ok',epoch:'e1',sessionId:'one',reminderId:'reminder-1',content:'original',delaySeconds:60,repeatCount:1,editRevision:'0'})");
    assert.equal(await wait("!!document.querySelector('#reminder-edit')"), "ready");
    await evaluate("document.querySelector('[aria-label=\"Reminder list\"] button').focus()");
    await press("e", "KeyE", 69, true);
    assert.equal(await evaluate("document.activeElement?.id"), "reminder-edit");
    assert.equal(await evaluate("window.shellEvents"), 0);
    await press("s", "KeyS", 83, true);
    assert.equal(await evaluate("window.reminderFixture.writes.length"), 0);
    assert.equal(await evaluate("window.shellEvents"), 1); // Unchanged edit is not a handled Save.
    assert.equal(await synthetic("#reminder-edit", "r"), false); // Refresh does not steal editor keys.
    assert.equal(await evaluate("window.reminderFixture.reads.length"), 1);
    await evaluate("document.querySelector('.reminder-page > button').focus()");
    await press("r", "KeyR", 82, true);
    assert.equal(await wait("window.reminderFixture.reads.length === 2"), "ready");
    assert.equal(await evaluate("window.shellEvents"), 2);
    await evaluate(`window.reminderFixture.reads[1].resolve(${list("one")})`);
    assert.equal(await wait("window.reminderFixture.details.length === 2"), "ready");
    await evaluate("window.reminderFixture.details[1].resolve({status:'ok',epoch:'e1',sessionId:'one',reminderId:'reminder-1',content:'original',delaySeconds:60,repeatCount:1,editRevision:'0'})");
    assert.equal(await wait("!!document.querySelector('#reminder-edit')"), "ready");
    await evaluate("document.querySelector('[aria-label=\"Reminder list\"] button').focus()");
    const beforeDelete = await evaluate("window.shellEvents");
    await press("Delete", "Delete", 46);
    assert.equal(await evaluate("document.activeElement?.id"), "reminder-confirm");
    assert.equal(await evaluate("window.shellEvents"), beforeDelete);
    assert.equal(await evaluate("window.reminderFixture.writes.length"), 0);
    await edit("#reminder-confirm", "reminder-1");
    await press("Delete", "Delete", 46);
    assert.equal(await evaluate("document.activeElement?.id"), "reminder-confirm");
    assert.equal(await evaluate("window.reminderFixture.writes.length"), 0);
    await edit("#reminder-edit", "new full message");
    assert.equal(await wait("document.querySelector('#reminder-edit')?.value === 'new full message'"), "ready");
    await edit("#reminder-content", "local Create draft");
    await edit("#reminder-delay", "0");
    assert.equal(await wait("[...document.querySelectorAll('button')].find(b=>b.textContent==='Create reminder').disabled"), "ready");
    for (const locale of locales) {
      await evaluate(`reminderFixture.language(${JSON.stringify(locale)})`);
      assert.equal(await wait(`document.querySelector('.reminder-page h1').textContent===${JSON.stringify(translate(locale, "Reminders"))}`), "ready");
      assert.equal(await evaluate(`document.body.textContent.includes(${JSON.stringify(translate(locale, "Enter 1–86400 whole seconds or HH:mm:ss (00–23 hours), optionally prefixed with d. (e.g. 1.00:00:00). Fractions are not accepted."))})`), true);
      assert.equal(await evaluate("document.querySelector('#reminder-delay').value"), "0");
      assert.equal(await createKey("#reminder-content"), false, `${locale}: invalid duration cannot create`);
      assert.equal(await synthetic("#reminder-edit", "s", "isComposing:true"), false, `${locale}: IME cannot Save`);
      assert.equal(await evaluate("JSON.stringify([reminderFixture.reads.length,reminderFixture.details.length,reminderFixture.writes.length])"), "[2,2,0]");
      if (locale === "de" || locale === "ja") {
        await evaluate("document.querySelector('#reminder-edit').focus()");
        await press("Tab", "Tab", 9);
        assert.equal(await evaluate("document.activeElement.textContent"), translate(locale, "Save message"));
      }
    }
    await evaluate("reminderFixture.language('en')");
    assert.equal(await wait("document.querySelector('.reminder-page h1').textContent==='Reminders'"), "ready");
    assert.equal(await synthetic("#reminder-content", "s"), false);
    assert.equal(await createKey("#reminder-content"), false); // Disabled Create is not handled.
    await evaluate("[...document.querySelectorAll('button')].find(b=>b.textContent==='Use as new reminder').click()");
    assert.equal(await wait("document.body.innerText.includes('The Create form has edits')"), "ready");
    assert.equal(await synthetic("[aria-label=\"Reminder list\"] button", "s"), false);
    assert.equal(await evaluate("window.reminderFixture.writes.length"), 0);
    await evaluate("[...document.querySelectorAll('button')].find(b=>b.textContent==='Keep draft').click()");
    await evaluate("document.querySelector('#reminder-edit').focus()");
    assert.equal(await synthetic("#reminder-edit", "s", "repeat:true"), false);
    assert.equal(await synthetic("#reminder-edit", "s", "altKey:true"), false);
    assert.equal(await synthetic("#reminder-edit", "s", "shiftKey:true"), false);
    assert.equal(await synthetic("#reminder-edit", "s", "metaKey:true"), false);
    assert.equal(await synthetic("#reminder-edit", "s", "isComposing:true"), false);
    assert.equal(await evaluate(`(() => { const event=new KeyboardEvent('keydown',{key:'s',ctrlKey:true,bubbles:true,cancelable:true});
      Object.defineProperty(event,'keyCode',{value:229}); document.querySelector('#reminder-edit').dispatchEvent(event);
      return event.defaultPrevented; })()`), false);
    assert.equal(await evaluate(`(() => { const event=new KeyboardEvent('keydown',{key:'s',ctrlKey:true,bubbles:true,cancelable:true});
      event.preventDefault(); document.querySelector('#reminder-edit').dispatchEvent(event); return event.defaultPrevented; })()`), true);
    assert.equal(await evaluate("window.reminderFixture.writes.length"), 0);
    await evaluate("window.reminderFixture.allowed=false");
    assert.equal(await synthetic("#reminder-edit", "s"), false);
    await evaluate("window.reminderFixture.allowed=true");
    await evaluate("document.body.insertAdjacentHTML('beforeend','<div role=\"dialog\" aria-modal=\"true\">Modal</div>')");
    assert.equal(await synthetic("#reminder-edit", "s"), false);
    await evaluate("document.querySelector('[role=\"dialog\"]').remove()");
    await evaluate(`(() => { const button=[...document.querySelectorAll('button')].find(b=>b.textContent==='Save message');
      window.savedButtonParent=button.parentNode; window.savedButtonNext=button.nextSibling; button.remove(); window.savedButton=button; })()`);
    assert.equal(await synthetic("#reminder-edit", "s"), false);
    await evaluate("(() => { window.savedButtonParent.insertBefore(window.savedButton,window.savedButtonNext); return true; })()");
    assert.equal(await evaluate("window.reminderFixture.writes.length"), 0);
    const beforeSave = await evaluate("window.shellEvents");
    await press("s", "KeyS", 83, true);
    assert.equal(await wait("window.reminderFixture.writes.length === 1"), "ready");
    assert.equal(await evaluate("window.shellEvents"), beforeSave);
    await edit("#reminder-delay", "60");
    assert.equal(await createKey("#reminder-content"), false, "pending Save blocks Create");
    assert.equal(await evaluate("window.reminderFixture.writes.length"), 1);
    assert.equal(await evaluate("JSON.stringify(window.reminderFixture.writes[0].request)"),
      JSON.stringify({ expectedEpoch: "e1", sessionId: "one", reminderId: "reminder-1", editRevision: "0", content: "new full message" }));
    await evaluate("document.querySelector('[aria-label=\"Reminder list\"] button').focus()");
    assert.equal(await synthetic("[aria-label=\"Reminder list\"] button", "s"), false);
    assert.equal(await synthetic("[aria-label=\"Reminder list\"] button", "e"), false);
    assert.equal(await evaluate("window.reminderFixture.writes.length"), 1);
    await evaluate("document.querySelector('.reminder-page > button').focus()");
    await press("r", "KeyR", 82, true); // Read-only refresh while Save is pending cannot replay it.
    assert.equal(await wait("window.reminderFixture.reads.length === 3"), "ready");
    await evaluate("window.reminderFixture.reads[2].reject(new Error('private pending read failure'))");
    assert.equal(await wait("document.body.innerText.includes('Reminder list could not be read')"), "ready");
    assert.equal(await evaluate("window.reminderFixture.writes.length"), 1);
    await evaluate("window.reminderFixture.session('two')");
    assert.equal(await wait("window.reminderFixture.reads.length === 4"), "ready");
    assert.equal(await evaluate("!!document.querySelector('[aria-label=\"Retained reminder Save\"]')"), false);
    assert.equal(await synthetic(".reminder-page > button", "s"), false);
    assert.equal(await evaluate("window.reminderFixture.writes.length"), 1);
    await evaluate("window.reminderFixture.writes[0].reject(new Error('lost response'))");
    await evaluate("window.reminderFixture.session('one')");
    assert.equal(await wait("window.reminderFixture.reads.length === 5"), "ready");
    await evaluate("window.reminderFixture.reads[4].reject(new Error('read failed'))");
    assert.equal(await wait("!!document.querySelector('[aria-label=\"Retained reminder Save\"]')"), "ready");
    assert.equal(await evaluate("document.querySelector('[aria-label=\"Retained reminder Save\"] pre')?.textContent"), "new full message");
    assert.equal(await synthetic("[aria-label=\"Retained reminder Save\"] pre", "s"), false);
    assert.equal(await evaluate("!!document.querySelector('#reminder-content')"), false, "list failure hides the Create form while Save is uncertain");
    assert.equal(await evaluate("window.reminderFixture.writes.length"), 1);
    await evaluate("document.querySelector('.reminder-page > button').focus()");
    await press("r", "KeyR", 82, true);
    assert.equal(await wait("window.reminderFixture.reads.length === 6"), "ready");
    assert.equal(await evaluate("window.reminderFixture.writes.length"), 1);
    await evaluate("window.reminderFixture.reads[5].reject(new Error('still unreadable'))");
    await evaluate("window.reminderFixture.host('e2')");
    assert.equal(await wait("window.reminderFixture.reads.length === 7"), "ready");
    assert.equal(await evaluate("!!document.querySelector('[aria-label=\"Retained reminder Save\"]')"), false);
    assert.doesNotMatch((await evaluate("document.body.innerText"))!, /new full message/);
    assert.equal(await synthetic(".reminder-page > button", "s"), false);
    const outside = await evaluate("window.shellEvents");
    assert.equal(await synthetic("body", "s"), false);
    assert.equal(await evaluate("window.shellEvents"), Number(outside) + 1);
    assert.equal(await evaluate("window.reminderFixture.writes.length"), 1);
    // The next host has independent admission authority. Only the four current Create controls own Ctrl+Enter.
    await evaluate(`window.reminderFixture.reads[6].resolve(${list("one", "e2")})`);
    assert.equal(await wait("!!document.querySelector('[aria-label=\"Reminder list\"] button')"), "ready");
    await evaluate("document.querySelector('[aria-label=\"Reminder list\"] button').click()");
    assert.equal(await wait("window.reminderFixture.details.length === 3"), "ready");
    await evaluate("window.reminderFixture.details[2].resolve({status:'ok',epoch:'e2',sessionId:'one',reminderId:'reminder-1',content:'original',delaySeconds:60,repeatCount:1,editRevision:'0'})");
    assert.equal(await wait("!!document.querySelector('#reminder-edit')"), "ready");
    await edit("#reminder-content", "first line\nsecond line");
    assert.equal(await wait("document.querySelector('#reminder-content')?.value === 'first line\\nsecond line'"), "ready");
    await edit("#reminder-delay", "bad");
    assert.equal(await createKey("#reminder-content"), false);
    await edit("#reminder-delay", "00:01:00");
    await edit("#reminder-repeat", "0");
    assert.equal(await createKey("#reminder-repeat"), false);
    await edit("#reminder-repeat", "2");
    await edit("#reminder-content", "   ");
    assert.equal(await createKey("#reminder-delay"), false);
    await edit("#reminder-content", "first line\nsecond line");
    const createButton = "[...document.querySelectorAll('button')].find(b=>b.textContent==='Create reminder')";
    assert.equal(await createKey('[aria-label="Reminder list"] button'), false);
    assert.equal(await createKey('.reminder-page > button'), false);
    assert.equal(await createKey('#reminder-confirm'), false);
    await edit("#reminder-edit", "separate Save draft");
    assert.equal(await wait("![...document.querySelectorAll('button')].find(b=>b.textContent==='Save message').disabled"), "ready");
    assert.equal(await createKey('#reminder-edit'), false);
    assert.equal(await createKey('body'), false);
    await evaluate("[...document.querySelectorAll('button')].find(b=>b.textContent==='Use as new reminder').click()");
    assert.equal(await wait("document.body.innerText.includes('The Create form has edits')"), "ready");
    assert.equal(await createKey('#reminder-content'), false, "inline confirmation owns the panel");
    await evaluate("[...document.querySelectorAll('button')].find(b=>b.textContent==='Keep draft').click()");
    for (const guard of ["repeat:true", "isComposing:true", "altKey:true", "shiftKey:true", "metaKey:true", "ctrlKey:false"]) {
      assert.equal(await createKey("#reminder-content", guard), false, guard);
    }
    assert.equal(await evaluate(`(() => { const e=new KeyboardEvent('keydown',{key:'Enter',ctrlKey:true,bubbles:true,cancelable:true});
      Object.defineProperty(e,'keyCode',{value:229}); document.querySelector('#reminder-content').dispatchEvent(e); return e.defaultPrevented; })()`), false);
    assert.equal(await evaluate(`(() => { const e=new KeyboardEvent('keydown',{key:'Enter',ctrlKey:true,bubbles:true,cancelable:true});
      e.preventDefault(); document.querySelector('#reminder-content').dispatchEvent(e); return e.defaultPrevented; })()`), true);
    await evaluate("window.reminderFixture.allowed=false");
    assert.equal(await createKey("#reminder-content"), false);
    await evaluate("window.reminderFixture.allowed=true; document.body.insertAdjacentHTML('beforeend','<div role=dialog aria-modal=true>Modal</div>')");
    assert.equal(await createKey("#reminder-content"), false);
    await evaluate("document.querySelector('[role=dialog]').remove(); window.reminderFixture.archive(true)");
    assert.equal(await wait("document.body.innerText.includes('Archived project is read-only')"), "ready");
    assert.equal(await createKey("#reminder-content"), false);
    await evaluate("window.reminderFixture.archive(false)");
    assert.equal(await wait("!document.body.innerText.includes('Archived project is read-only')"), "ready");
    await evaluate(`${createButton}.dataset.epoch='wrong'`);
    assert.equal(await createKey("#reminder-content"), false);
    await evaluate(`${createButton}.dataset.epoch='e2'; ${createButton}.dataset.sessionId='two'`);
    assert.equal(await createKey("#reminder-delay"), false);
    await evaluate(`${createButton}.dataset.sessionId='one'; ${createButton}.disabled=true`);
    assert.equal(await createKey("#reminder-repeat"), false);
    await evaluate(`${createButton}.disabled=false; window.createButton=${createButton}; window.createParent=window.createButton.parentNode;
      window.createNext=window.createButton.nextSibling; window.createButton.remove()`);
    assert.equal(await createKey("#reminder-content"), false);
    assert.equal(await evaluate("JSON.stringify([!!window.createParent,!!window.createButton,window.createButton?.isConnected,window.createNext?.parentNode===window.createParent])"),
      JSON.stringify([true, true, false, true]));
    await evaluate("window.createParent.insertBefore(window.createButton,window.createNext); true");
    await evaluate("document.querySelector('#reminder-content').focus(); document.querySelector('#reminder-content').setSelectionRange(22,22)");
    await command("Input.dispatchKeyEvent", { type: "keyDown", key: "Enter", code: "Enter", windowsVirtualKeyCode: 13, text: "\r" });
    await command("Input.dispatchKeyEvent", { type: "keyUp", key: "Enter", code: "Enter", windowsVirtualKeyCode: 13 });
    assert.equal(await evaluate("document.querySelector('#reminder-content').value"), "first line\nsecond line\n");
    assert.equal(await evaluate("window.reminderFixture.writes.length"), 1, "ordinary Enter only edits the message");
    await edit("#reminder-content", "first line\nsecond line");
    await command("Emulation.setDeviceMetricsOverride", { width: 375, height: 700, deviceScaleFactor: 1, mobile: false });
    for (const theme of ["light", "dark"]) {
      await evaluate(`document.documentElement.dataset.theme='${theme}'`);
      assert.equal(await evaluate(`(() => { const hint=[...document.querySelectorAll('.reminder-page p')]
        .find(p=>p.textContent.includes('Ctrl+Enter creates only')); const box=hint?.getBoundingClientRect();
        return !!box && box.width>100 && box.left>=0 && box.right<=innerWidth && document.documentElement.scrollWidth<=innerWidth; })()`),
      true, `${theme} narrow Create hint stays within the panel`);
    }
    assert.equal(await evaluate("window.reminderFixture.writes.length"), 1);
    const beforeCreate = await evaluate("window.shellEvents");
    assert.equal(await createKey("#reminder-content"), true);
    assert.equal(await wait("window.reminderFixture.writes.length === 2"), "ready");
    assert.equal(await evaluate("window.shellEvents"), beforeCreate);
    assert.equal(await evaluate("document.activeElement?.id"), "reminder-content");
    assert.equal(await evaluate("document.querySelector('#reminder-content').value"), "first line\nsecond line");
    assert.equal(await evaluate("JSON.stringify(window.reminderFixture.writes[1].request)"),
      JSON.stringify({ expectedEpoch: "e2", sessionId: "one", content: "first line\nsecond line", delaySeconds: 60, repeatCount: 2 }));
    assert.equal(await createKey("#reminder-delay"), false, "pending Create is not replayed");
    await evaluate("document.querySelector('.reminder-page > button').click()");
    assert.equal(await wait("window.reminderFixture.reads.length === 8"), "ready");
    await evaluate(`window.reminderFixture.reads[7].resolve(${list("one", "e2")})`);
    await evaluate("window.reminderFixture.leave()");
    assert.equal(await wait("document.body.innerText.includes('Other screen')"), "ready");
    await evaluate("window.reminderFixture.open()");
    assert.equal(await wait("window.reminderFixture.reads.length === 9"), "ready");
    await evaluate(`window.reminderFixture.reads[8].resolve(${list("one", "e2")})`);
    assert.equal(await wait("!!document.querySelector('#reminder-content')"), "ready");
    await edit("#reminder-content", "not retried");
    assert.equal(await createKey("#reminder-content"), false);
    await evaluate("window.reminderFixture.writes[1].reject(new Error('lost Create reply'))");
    assert.equal(await wait("document.body.innerText.includes('Admission response was lost')"), "ready");
    assert.equal(await createKey("#reminder-content"), false, "uncertain Create stays locked");
    assert.equal(await createKey('[aria-label="Reminder list"] button'), false);
    assert.equal(await evaluate("window.reminderFixture.writes.length"), 2);
    await evaluate("window.oldCreate=window.createButton; window.reminderFixture.host('e3')");
    assert.equal(await wait("window.reminderFixture.reads.length === 10"), "ready");
    await evaluate(`window.reminderFixture.reads[9].resolve(${list("one", "e3")})`);
    assert.equal(await wait("!!document.querySelector('[aria-label=\"Reminder list\"] button')"), "ready");
    assert.equal(await evaluate("window.oldCreate.isConnected"), false);
    assert.equal(await evaluate("(() => { const e=new KeyboardEvent('keydown',{key:'Enter',ctrlKey:true,bubbles:true,cancelable:true}); window.oldCreate.dispatchEvent(e); return e.defaultPrevented; })()"), false);
    await edit("#reminder-content", "next target");
    assert.equal(await wait("document.querySelector('#reminder-content').value === 'next target'"), "ready");
    await evaluate("document.querySelector('#reminder-delay').focus()");
    await press("Enter", "Enter", 13, true);
    assert.equal(await wait("window.reminderFixture.writes.length === 3"), "ready");
    assert.equal(await evaluate("document.activeElement?.id"), "reminder-delay");
    assert.equal(await evaluate("JSON.stringify(window.reminderFixture.writes[2].request)"),
      JSON.stringify({ expectedEpoch: "e3", sessionId: "one", content: "next target", delaySeconds: 300, repeatCount: 1 }));
    await evaluate("window.reminderFixture.writes[2].resolve({status:'invalid_request',epoch:'e3',sessionId:'one',reminderId:null})");
    assert.equal(await wait("document.body.innerText.includes('create was not accepted')"), "ready");
    await evaluate("document.querySelector('#reminder-repeat').focus()");
    await press("Enter", "Enter", 13, true);
    assert.equal(await wait("window.reminderFixture.writes.length === 4"), "ready");
    await evaluate("window.reminderFixture.writes[3].resolve({status:'invalid_request',epoch:'e3',sessionId:'one',reminderId:null})");
    assert.equal(await wait("!document.body.innerText.includes('create admission pending')"), "ready");
    await evaluate("[...document.querySelectorAll('button')].find(b=>b.textContent==='Create reminder').focus()");
    await press("Enter", "Enter", 13, true);
    assert.equal(await wait("window.reminderFixture.writes.length === 5"), "ready", "Create button Enter shares the owner latch");
    await evaluate("window.reminderFixture.writes[4].resolve({status:'invalid_request',epoch:'e3',sessionId:'one',reminderId:null})");
    assert.equal(await wait("!document.body.innerText.includes('create admission pending')"), "ready");
    await evaluate("document.querySelector('[aria-label=\"Reminder list\"] button').click()");
    await edit("#reminder-confirm", "reminder-1");
    await evaluate("[...document.querySelectorAll('button')].find(b=>b.textContent==='Delete confirmed reminder').click()");
    assert.equal(await wait("window.reminderFixture.writes.length === 6"), "ready");
    assert.equal(await createKey("#reminder-content"), false, "pending Delete blocks Create");
    await evaluate("document.querySelector('.reminder-page > button').click()");
    assert.equal(await wait("window.reminderFixture.reads.length === 11"), "ready");
    await evaluate(`window.reminderFixture.reads[10].resolve(${list("one", "e3")})`);
    await evaluate("window.reminderFixture.leave()");
    assert.equal(await wait("document.body.innerText.includes('Other screen')"), "ready");
    await evaluate("window.reminderFixture.open()");
    assert.equal(await wait("window.reminderFixture.reads.length === 12"), "ready");
    await evaluate(`window.reminderFixture.reads[11].resolve(${list("one", "e3")})`);
    assert.equal(await wait("!!document.querySelector('#reminder-content')"), "ready");
    await edit("#reminder-content", "still blocked");
    assert.equal(await createKey("#reminder-content"), false);
    await evaluate("window.reminderFixture.writes[5].reject(new Error('lost Delete reply'))");
    assert.equal(await wait("document.body.innerText.includes('Admission response was lost')"), "ready");
    assert.equal(await createKey("#reminder-content"), false, "uncertain Delete cannot create");
    assert.equal(await evaluate("window.reminderFixture.writes.length"), 6);
  } finally {
    socket?.close(); browser?.kill(); await rm(root, { recursive: true, force: true, maxRetries: 8, retryDelay: 100 });
  }
});
