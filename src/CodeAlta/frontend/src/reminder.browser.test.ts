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

test("mounted reminders show exact detail, load a guarded new Create, and fence stale/uncertain work", { skip: !edge, timeout: 60_000 }, async () => {
  const app = readFileSync(fileURLToPath(new URL("./main.tsx", import.meta.url)), "utf8");
  assert.match(app, /view === "reminders" \? <ReminderPanel/);
  assert.match(app, /read=\{readReminders\}/);
  assert.match(app, /readDetail=\{readReminderDetail\}/);
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
    await command("Emulation.setFocusEmulationEnabled", { enabled: true });
    const evaluate = async (expression: string) => (await command("Runtime.evaluate", { expression, returnByValue: true,
      awaitPromise: true })).result?.value;
    const wait = async (condition: string) => evaluate(`new Promise(resolve => { const end = Date.now() + 7000; const check = () => {
      if (${condition}) resolve('ready'); else if (Date.now() > end) resolve(document.body.innerText.slice(0, 500));
      else setTimeout(check, 20); }; check(); })`);
    const activateSelectedWithEnter = async () => {
      await evaluate(`(() => { const button = document.querySelector('[aria-label="Reminder list"] button[aria-pressed="true"]');
        button.focus(); button.dataset.activations = '0';
        button.addEventListener('click', () => button.dataset.activations = '1', { once: true }); })()`);
      assert.equal(await evaluate(`document.activeElement?.getAttribute('aria-pressed')`), "true");
      await command("Input.dispatchKeyEvent", { type: "keyDown", key: "Enter", code: "Enter", windowsVirtualKeyCode: 13, text: "\r" });
      await command("Input.dispatchKeyEvent", { type: "keyUp", key: "Enter", code: "Enter", windowsVirtualKeyCode: 13 });
      assert.equal(await wait(`document.querySelector('[aria-label="Reminder list"] button[aria-pressed="true"]')?.dataset.activations === '1'`), "ready");
    };
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
    await evaluate(`document.querySelector('[aria-label="Reminder list"] button').click()`);
    assert.equal(await wait("window.reminderFixture.details.length === 1"), "ready");
    assert.equal(await evaluate("window.reminderFixture.details[0].request.reminderId"), "reminder-1");
    await evaluate(`document.querySelector('[aria-label="Reminder list"] button').click()`);
    await activateSelectedWithEnter();
    assert.equal(await evaluate("window.reminderFixture.details.length"), 1);
    assert.equal(await evaluate("document.body.innerText.includes('Loading full reminder message.')"), true);
    await evaluate(`window.reminderFixture.details[0].resolve({status:'ok',epoch:'e1',sessionId:'one',reminderId:'reminder-1',
      content:'full 😀\\nsecond line',delaySeconds:60,repeatCount:1})`);
    assert.equal(await wait("document.querySelector('[aria-label=\"Full reminder message\"]')?.textContent === 'full 😀\\nsecond line'"), "ready");
    await evaluate(`document.querySelector('[aria-label="Reminder list"] button').click()`);
    await activateSelectedWithEnter();
    assert.equal(await evaluate("window.reminderFixture.details.length"), 1);
    assert.equal(await evaluate("document.querySelector('[aria-label=\"Full reminder message\"]')?.textContent"), "full 😀\nsecond line");
    assert.equal(await evaluate("document.body.innerText.includes('Loading full reminder message.')"), false);
    await evaluate(`[...document.querySelectorAll('button')].find(b=>b.textContent==='Use as new reminder').click()`);
    assert.equal(await wait("document.querySelector('#reminder-content')?.value === 'full 😀\\nsecond line'"), "ready");
    assert.equal(await evaluate("document.querySelector('#reminder-delay').value"), "60");
    assert.equal(await evaluate("document.querySelector('#reminder-repeat').value"), "1");
    assert.equal(await evaluate("window.reminderFixture.writes.length"), 0);
    await evaluate(`(() => { const input=document.querySelector('#reminder-content');
      Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype,'value').set.call(input,'check session');
      input.dispatchEvent(new Event('input',{bubbles:true})); })()`);
    await evaluate(`[...document.querySelectorAll('button')].find(b=>b.textContent==='Use as new reminder').click()`);
    assert.equal(await wait("!![...document.querySelectorAll('button')].find(b=>b.textContent==='Discard draft and use reminder')"), "ready");
    assert.equal(await evaluate("document.querySelector('#reminder-content').value"), "check session");
    await evaluate(`[...document.querySelectorAll('button')].find(b=>b.textContent==='Discard draft and use reminder').click()`);
    assert.equal(await wait("document.querySelector('#reminder-content').value === 'full 😀\\nsecond line'"), "ready");
    await evaluate(`[...document.querySelectorAll('button')].find(b=>b.textContent==='Create reminder').click()`);
    assert.equal(await wait("window.reminderFixture.writes.length === 1"), "ready");
    assert.equal(await evaluate("window.reminderFixture.writes[0].request.sessionId"), "one");
    assert.equal(await evaluate("window.reminderFixture.writes[0].request.content"), "full 😀\nsecond line");
    assert.equal(await evaluate("window.reminderFixture.writes[0].request.delaySeconds"), 60);
    assert.equal(await evaluate("window.reminderFixture.writes[0].request.repeatCount"), 1);
    await evaluate(`[...document.querySelectorAll('button')].find(b=>b.textContent==='Use as new reminder').click()`);
    assert.equal(await evaluate("document.querySelector('#reminder-content').value"), "full 😀\nsecond line");
    await evaluate(`(() => { const input=document.querySelector('#reminder-content');
      Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype,'value').set.call(input,'pending draft');
      input.dispatchEvent(new Event('input',{bubbles:true})); })()`);
    assert.equal(await wait("document.querySelector('#reminder-content').value === 'pending draft'"), "ready");
    assert.equal(await evaluate("[...document.querySelectorAll('button')].find(b=>b.textContent==='Use as new reminder').disabled"), true);
    assert.equal(await evaluate("document.querySelector('#reminder-content').value"), "pending draft");
    assert.equal(await evaluate("window.reminderFixture.writes[0].request.content"), "full 😀\nsecond line");
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
    const detailCount = Number(await evaluate("window.reminderFixture.details.length"));
    await evaluate(`document.querySelector('[aria-label="Reminder list"] button').click()`);
    assert.equal(await wait(`window.reminderFixture.details.length > ${detailCount}`), "ready");
    await evaluate(`window.reminderFixture.details.at(-1).resolve({status:'ok',epoch:'e1',sessionId:'one',reminderId:'reminder-1',
      content:'full message',delaySeconds:60,repeatCount:1})`);
    assert.equal(await wait("!!document.querySelector('[aria-label=\"Full reminder message\"]')"), "ready");
    assert.equal(await evaluate("[...document.querySelectorAll('button')].find(b=>b.textContent==='Use as new reminder').disabled"), true);
    assert.equal(await evaluate("window.reminderFixture.writes.length"), 3);
    await evaluate("window.reminderFixture.host('e2')");
    assert.equal(await wait("window.reminderFixture.reads.length === 9"), "ready");
    await evaluate(`window.reminderFixture.reads[8].resolve(${list("one", row)})`);
    assert.equal(await wait("document.body.innerText.includes('Reload required')"), "ready");
    await evaluate("window.reminderFixture.host(null)");
    assert.equal(await wait("document.body.innerText.includes('Select an owned session')"), "ready");
    const priorReads = Number(await evaluate("window.reminderFixture.reads.length"));
    const priorDetails = Number(await evaluate("window.reminderFixture.details.length"));
    await evaluate("window.reminderFixture.host('e1')");
    assert.equal(await wait(`window.reminderFixture.reads.length > ${priorReads}`), "ready");
    await evaluate(`window.reminderFixture.reads.at(-1).resolve(${list("one", row)})`);
    assert.equal(await wait("!!document.querySelector('[aria-label=\"Reminder list\"] button')"), "ready");
    await evaluate(`document.querySelector('[aria-label="Reminder list"] button').click()`);
    assert.equal(await wait(`window.reminderFixture.details.length > ${priorDetails}`), "ready");
    await evaluate("window.reminderFixture.host('e2')");
    assert.equal(await wait(`window.reminderFixture.reads.length > ${priorReads + 1}`), "ready");
    await evaluate(`window.reminderFixture.reads.at(-1).resolve(${list("one", row).replace("epoch:'e1'", "epoch:'e2'")})`);
    assert.equal(await wait("!!document.querySelector('#reminder-content')"), "ready");
    await evaluate(`window.reminderFixture.details[${priorDetails}].resolve({status:'ok',epoch:'e1',sessionId:'one',reminderId:'reminder-1',
      content:'stale private text',delaySeconds:60,repeatCount:1})`);
    assert.equal(await evaluate("document.querySelector('#reminder-content').value"), "");
    assert.equal(await evaluate("!!document.querySelector('[aria-label=\"Full reminder message\"]')"), false);
    assert.equal(await evaluate("window.reminderFixture.writes.length"), 3);
    const currentDetails = Number(await evaluate("window.reminderFixture.details.length"));
    await evaluate(`document.querySelector('[aria-label="Reminder list"] button').click()`);
    assert.equal(await wait(`window.reminderFixture.details.length > ${currentDetails}`), "ready");
    await evaluate(`window.reminderFixture.details.at(-1).resolve({status:'missing_reminder',epoch:'e2',sessionId:'one',
      reminderId:'reminder-1',content:null,delaySeconds:null,repeatCount:null})`);
    assert.equal(await wait("document.body.innerText.includes('unavailable or was deleted')"), "ready");
    const refreshReads = Number(await evaluate("window.reminderFixture.reads.length"));
    await evaluate("document.querySelector('.reminder-page > button').click()");
    assert.equal(await wait(`window.reminderFixture.reads.length > ${refreshReads}`), "ready");
    await evaluate(`window.reminderFixture.reads.at(-1).resolve(${list("one", row).replace("epoch:'e1'", "epoch:'e2'")})`);
    assert.equal(await wait(`window.reminderFixture.details.length > ${currentDetails + 1}`), "ready");
    await evaluate("window.reminderFixture.details.at(-1).reject(new Error('private path'))");
    assert.equal(await wait("document.body.innerText.includes('detail could not be read')"), "ready");
    assert.doesNotMatch((await evaluate("document.body.innerText"))!, /private path/);
    const failedDetailCount = Number(await evaluate("window.reminderFixture.details.length"));
    await evaluate(`document.querySelector('[aria-label="Reminder list"] button').click()`);
    await activateSelectedWithEnter();
    assert.equal(await evaluate("window.reminderFixture.details.length"), failedDetailCount);
    assert.equal(await evaluate("document.body.innerText.includes('detail could not be read')"), true);
    assert.equal(await evaluate("document.body.innerText.includes('Loading full reminder message.')"), false);
    const beforeRowSwitch = Number(await evaluate("window.reminderFixture.details.length"));
    await evaluate("document.querySelector('.reminder-page > button').click()");
    assert.equal(await wait(`window.reminderFixture.reads.length > ${refreshReads + 1}`), "ready");
    await evaluate(`window.reminderFixture.reads.at(-1).resolve({status:'ok',epoch:'e2',sessionId:'one',
      reminders:[${row},${row.replace("reminder-1", "reminder-2").replace("original", "second")}],activeCount:2,completedCount:0})`);
    assert.equal(await wait(`window.reminderFixture.details.length > ${beforeRowSwitch}`), "ready");
    await evaluate(`document.querySelectorAll('[aria-label="Reminder list"] button')[1].click()`);
    assert.equal(await wait(`window.reminderFixture.details.length > ${beforeRowSwitch + 1}`), "ready");
    await evaluate(`window.reminderFixture.details[${beforeRowSwitch}].resolve({status:'ok',epoch:'e2',sessionId:'one',
      reminderId:'reminder-1',content:'wrong row private text',delaySeconds:60,repeatCount:1})`);
    assert.equal(await evaluate("!!document.querySelector('[aria-label=\"Full reminder message\"]')"), false);
    await evaluate(`window.reminderFixture.details.at(-1).resolve({status:'ok',epoch:'e2',sessionId:'one',
      reminderId:'reminder-2',content:'selected row full text',delaySeconds:60,repeatCount:1})`);
    assert.equal(await wait("document.querySelector('[aria-label=\"Full reminder message\"]')?.textContent === 'selected row full text'"), "ready");
    assert.doesNotMatch((await evaluate("document.body.innerText"))!, /wrong row private text/);
    assert.equal(await evaluate("window.reminderFixture.writes.length"), 3);
  } finally {
    socket?.close(); browser?.kill(); await rm(root, { recursive: true, force: true, maxRetries: 8, retryDelay: 100 });
  }
});
