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

test("mounted reminders list, create, switch fencing, uncertain hold and confirmed deletion", { skip: !edge, timeout: 60_000 }, async () => {
  const app = readFileSync(fileURLToPath(new URL("./main.tsx", import.meta.url)), "utf8");
  assert.match(app, /view === "reminders" \? <ReminderPanel/);
  assert.match(app, /read=\{readReminders\}/);
  const root = await mkdtemp(join(tmpdir(), "codealta-reminder-mounted-"));
  let browser: ReturnType<typeof spawn> | undefined;
  let socket: WebSocket | undefined;
  try {
    await build({ entryPoints: [fileURLToPath(new URL("./reminder.mount.tsx", import.meta.url))],
      outfile: join(root, "fixture.js"), bundle: true, platform: "browser", format: "iife" });
    const page = join(root, "fixture.html");
    await writeFile(page, '<!doctype html><html><body><div id="app"></div><script src="fixture.js"></script></body></html>');
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
    const evaluate = async (expression: string) => (await command("Runtime.evaluate", { expression, returnByValue: true,
      awaitPromise: true })).result?.value;
    const wait = async (condition: string) => evaluate(`new Promise(resolve => { const end = Date.now() + 7000; const check = () => {
      if (${condition}) resolve('ready'); else if (Date.now() > end) resolve(document.body.innerText.slice(0, 500));
      else setTimeout(check, 20); }; check(); })`);
    const list = (sessionId: string, reminders: string) => `({status:'ok',epoch:'e1',sessionId:'${sessionId}',reminders:[${reminders}],activeCount:${reminders ? 1 : 0},completedCount:0})`;
    const row = "{id:'reminder-1',state:'active',preview:'original',delaySeconds:60,repeatCount:1,firedCount:0,dueAt:null,lastExitCode:null,lastError:null}";
    assert.equal(await wait("window.reminderFixture?.reads.length === 1"), "ready");
    await evaluate("window.reminderFixture.session('two')");
    assert.equal(await wait("window.reminderFixture.reads.length === 2"), "ready");
    await evaluate(`window.reminderFixture.reads[0].resolve(${list("one", row)})`);
    await evaluate(`window.reminderFixture.reads[1].resolve(${list("two", "")})`);
    assert.equal(await wait("document.body.innerText.includes('0 active, 0 completed')"), "ready");
    assert.doesNotMatch((await evaluate("document.body.innerText"))!, /original/);
    await evaluate("window.reminderFixture.session('one')");
    assert.equal(await wait("window.reminderFixture.reads.length === 3"), "ready");
    await evaluate("window.reminderFixture.reads[2].reject(new Error('private file path'))");
    assert.equal(await wait("document.body.innerText.includes('could not be read')"), "ready");
    assert.doesNotMatch((await evaluate("document.body.innerText"))!, /private file path/);
    await evaluate("document.querySelector('.reminder-page > button').click()");
    assert.equal(await wait("window.reminderFixture.reads.length === 4"), "ready");
    await evaluate(`window.reminderFixture.reads[3].resolve(${list("one", row)})`);
    assert.equal(await wait("document.querySelectorAll('[aria-label=\"Reminder list\"] button').length === 1"), "ready");
    await evaluate(`(() => { const input=document.querySelector('#reminder-content');
      Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype,'value').set.call(input,'check session');
      input.dispatchEvent(new Event('input',{bubbles:true})); })()`);
    assert.equal(await wait("!document.querySelector('button:last-child')?.disabled && document.querySelector('#reminder-content')?.value === 'check session'"), "ready");
    await evaluate(`[...document.querySelectorAll('button')].find(b=>b.textContent==='Create reminder').click()`);
    assert.equal(await wait("window.reminderFixture.writes.length === 1"), "ready");
    assert.equal(await evaluate("window.reminderFixture.writes[0].request.sessionId"), "one");
    await evaluate("window.reminderFixture.writes[0].resolve({status:'ok',epoch:'e1',sessionId:'one',reminderId:'reminder-2'})");
    assert.equal(await wait("window.reminderFixture.reads.length === 5"), "ready");
    await evaluate(`window.reminderFixture.reads[4].resolve(${list("one", row)})`);
    assert.equal(await wait("document.body.innerText.includes('created (reminder-2)')"), "ready");
    await evaluate(`document.querySelector('[aria-label="Reminder list"] button').click()`);
    await evaluate(`(() => { const input=document.querySelector('#reminder-confirm');
      Object.getOwnPropertyDescriptor(HTMLInputElement.prototype,'value').set.call(input,'reminder-1');
      input.dispatchEvent(new Event('input',{bubbles:true})); })()`);
    assert.equal(await wait("![...document.querySelectorAll('button')].find(b=>b.textContent==='Delete confirmed reminder').disabled"), "ready");
    await evaluate(`[...document.querySelectorAll('button')].find(b=>b.textContent==='Delete confirmed reminder').click()`);
    assert.equal(await wait("window.reminderFixture.writes.length === 2"), "ready");
    assert.equal(await evaluate("window.reminderFixture.writes[1].request.confirmation"), "reminder-1");
    await evaluate("window.reminderFixture.writes[1].resolve({status:'ok',epoch:'e1',sessionId:'one',reminderId:'reminder-1'})");
    assert.equal(await wait("window.reminderFixture.reads.length === 6"), "ready");
    await evaluate(`window.reminderFixture.reads[5].resolve(${list("one", "")})`);
    assert.equal(await wait("document.body.innerText.includes('deleted (reminder-1)')"), "ready");
    await evaluate(`[...document.querySelectorAll('button')].find(b=>b.textContent==='Create reminder').click()`);
    assert.equal(await wait("window.reminderFixture.writes.length === 3"), "ready");
    await evaluate("window.reminderFixture.session('two')");
    assert.equal(await wait("window.reminderFixture.reads.length === 7"), "ready");
    await evaluate(`window.reminderFixture.reads[6].resolve(${list("two", "")})`);
    assert.equal(await evaluate("document.querySelector('#reminder-content').value"), "");
    await evaluate("window.reminderFixture.writes[2].reject(new Error('transport lost'))");
    await evaluate("window.reminderFixture.session('one')");
    assert.equal(await wait("window.reminderFixture.reads.length === 8"), "ready");
    await evaluate(`window.reminderFixture.reads[7].resolve(${list("one", row)})`);
    assert.equal(await wait("document.body.innerText.includes('no automatic retry')"), "ready");
    assert.equal(await evaluate("[...document.querySelectorAll('button')].find(b=>b.textContent==='Create reminder').disabled"), true);
    assert.equal(await evaluate("window.reminderFixture.writes.length"), 3);
    await evaluate("window.reminderFixture.host('e2')");
    assert.equal(await wait("window.reminderFixture.reads.length === 9"), "ready");
    await evaluate(`window.reminderFixture.reads[8].resolve(${list("one", row)})`);
    assert.equal(await wait("document.body.innerText.includes('Reload required')"), "ready");
    await evaluate("window.reminderFixture.host(null)");
    assert.equal(await wait("document.body.innerText.includes('Select an owned session')"), "ready");
  } finally {
    socket?.close(); browser?.kill(); await rm(root, { recursive: true, force: true, maxRetries: 8, retryDelay: 100 });
  }
});
