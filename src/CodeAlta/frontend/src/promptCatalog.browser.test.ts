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

test("mounted prompt inventory to composer to captured Send rejects stale, pending, unavailable and canceled selection", { skip: !edge, timeout: 60_000 }, async () => {
  const app = readFileSync(fileURLToPath(new URL("./main.tsx", import.meta.url)), "utf8");
  assert.match(app, /action === "prompts"\) navigate\("prompts"\)/);
  assert.match(app, /readPrompts=\{promptCatalog\.list\}/);
  assert.match(app, /applyPromptNextSend\(target/);
  assert.match(app, /selections=\{nextSendSelections\}/);
  const root = await mkdtemp(join(tmpdir(), "codealta-prompt-catalog-"));
  let browser: ReturnType<typeof spawn> | undefined;
  let socket: WebSocket | undefined;
  try {
    await build({ entryPoints: [fileURLToPath(new URL("./promptCatalog.mount.tsx", import.meta.url))],
      outfile: join(root, "fixture.js"), bundle: true, platform: "browser", format: "iife",
      plugins: [{ name: "test-only-types", setup(build) {
        build.onResolve({ filter: /^#neoastra$/ }, () => ({ path: fileURLToPath(new URL("./promptCatalog.neoastra.mount.ts", import.meta.url)) }));
      } }] });
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
    const prompt = (epoch: string, sessionId: string) => `({status:'ok',epoch:'${epoch}',sessionId:'${sessionId}',truncated:true,prompts:[
      {id:'default',name:'Default',description:null,scope:'BuiltIn',builtIn:true,appended:false,body:'built in',bodyTruncated:false},
      {id:'plan',name:'Plan',description:'planning mode',scope:'Project',builtIn:false,appended:true,body:'effective content',bodyTruncated:true}]})`;
    assert.equal(await wait("window.promptFixture?.reads.length === 1"), "ready");
    assert.match((await evaluate("document.body.innerText"))!, /Loading prompts/);
    await evaluate(`window.promptFixture.session('two')`);
    assert.equal(await wait("window.promptFixture.reads.length === 2"), "ready");
    await evaluate(`window.promptFixture.reads[0].resolve(${prompt("e1", "one")})`);
    await evaluate(`window.promptFixture.reads[1].resolve({status:'stale_epoch',epoch:'other',sessionId:'two',prompts:[],truncated:false})`);
    assert.equal(await wait("document.body.innerText.includes('Reload required')"), "ready");
    assert.equal(await evaluate("document.querySelector('.prompt-catalog-body')"), null);
    await evaluate(`window.promptFixture.session('one')`);
    assert.equal(await wait("window.promptFixture.reads.length === 3"), "ready");
    await evaluate(`window.promptFixture.reads[2].reject(new Error('private catalog failure'))`);
    assert.equal(await wait("document.body.innerText.includes('could not be read')"), "ready");
    assert.doesNotMatch((await evaluate("document.body.innerText"))!, /private catalog failure/);
    await evaluate(`window.promptFixture.session('two')`);
    assert.equal(await wait("window.promptFixture.reads.length === 4"), "ready");
    await evaluate(`window.promptFixture.session('one')`);
    assert.equal(await wait("window.promptFixture.reads.length === 5"), "ready");
    await evaluate(`window.promptFixture.reads[3].resolve(${prompt("e1", "two")})`);
    await evaluate(`window.promptFixture.reads[4].resolve(${prompt("e1", "one")})`);
    assert.equal(await wait(`document.querySelectorAll('[aria-label="Prompt inventory"] button').length === 2`), "ready");
    await evaluate(`document.querySelectorAll('[aria-label="Prompt inventory"] button')[1].click()`);
    assert.equal(await wait("document.body.innerText.includes('Content truncated')"), "ready");
    assert.match((await evaluate("document.body.innerText"))!, /read-only here|Read-only here/);
    assert.equal(await wait("document.body.innerText.includes('Session-recorded prompt: default')"), "ready");
    assert.match((await evaluate("document.body.innerText"))!, /Next Send: default · model model · effort High/);
    await evaluate(`document.querySelector('.model-catalog-next button').click()`);
    assert.equal(await wait(`document.querySelector('select[aria-label="Agent prompt"]')?.value === 'plan'`), "ready");
    assert.equal(await evaluate(`document.querySelector('select[aria-label="Model"]').value`), "model");
    assert.equal(await evaluate(`document.querySelector('select[aria-label="Reasoning"]').value`), "High");
    await evaluate(`(() => { const input = document.querySelector('#session-prompt');
      Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype,'value').set.call(input,'literal next send');
      input.dispatchEvent(new Event('input',{bubbles:true})); })()`);
    await evaluate(`document.querySelector('.send-button').click()`);
    assert.equal(await wait("window.promptFixture.sent.length === 1"), "ready");
    const sent = JSON.parse((await evaluate("JSON.stringify(window.promptFixture.sent[0])"))!);
    assert.equal(sent.sessionId, "one");
    assert.deepEqual(sent.selection, { providerKey: "beta", agentPromptId: "plan", modelId: "model", reasoningEffort: "High" });
    await evaluate(`window.promptFixture.open()`);
    assert.equal(await wait("window.promptFixture.reads.length === 6"), "ready");
    await evaluate(`window.promptFixture.reads[5].resolve(${prompt("e1", "one")})`);
    await evaluate(`document.querySelectorAll('[aria-label="Prompt inventory"] button')[0].click()`);
    assert.equal(await wait("document.body.innerText.includes('Retained exact request')"), "ready");
    assert.equal(await evaluate("document.querySelector('.model-catalog-next button').disabled"), true);
    await evaluate(`window.promptFixture.session('two')`);
    assert.equal(await wait("window.promptFixture.reads.length === 7"), "ready");
    await evaluate(`window.promptFixture.reads[6].resolve(${prompt("e1", "two")})`);
    await evaluate(`document.querySelectorAll('[aria-label="Prompt inventory"] button')[1].click()`);
    assert.equal(await wait("document.querySelector('.model-catalog-next button:not(:disabled)')"), "ready");
    await evaluate(`window.promptFixture.hold=true; document.querySelector('.model-catalog-next button').click()`);
    assert.equal(await wait("!!window.promptFixture.release"), "ready");
    await evaluate(`window.promptFixture.leave(); window.promptFixture.hold=false; window.promptFixture.release()`);
    assert.equal(await wait("document.querySelector('#session-prompt')"), "ready");
    assert.equal(await evaluate("localStorage.getItem('codealta.desktop.selection.two')"), null);
    assert.deepEqual(JSON.parse((await evaluate("JSON.stringify(window.promptFixture.sent[0])"))!).selection, sent.selection);
    await evaluate(`window.promptFixture.open()`);
    assert.equal(await wait("window.promptFixture.reads.length === 8"), "ready");
    await evaluate(`window.promptFixture.reads[7].resolve(${prompt("e1", "two")})`);
    await evaluate(`window.promptFixture.choices.prompts=[{id:'default',name:'Default'}]`);
    await evaluate(`document.querySelectorAll('[aria-label="Prompt inventory"] button')[1].click()`);
    assert.equal(await wait("document.body.innerText.includes('unavailable for this session')"), "ready");
    assert.equal(await evaluate("document.querySelector('.model-catalog-next button').disabled"), true);
    await evaluate(`window.promptFixture.show('e2')`);
    assert.equal(await wait("window.promptFixture.reads.length === 9"), "ready");
    await evaluate(`window.promptFixture.reads[8].resolve(${prompt("e1", "two")})`);
    assert.equal(await wait("document.body.innerText.includes('Reload required')"), "ready");
    assert.equal(await evaluate("localStorage.getItem('codealta.desktop.selection.two')"), null);
    await evaluate(`window.promptFixture.show(null)`);
    assert.equal(await wait("document.body.innerText.includes('Catalog-only mode has no owned prompt inventory')"), "ready");
    assert.equal(await evaluate("window.promptFixture.reads.length"), 9);
    await evaluate(`window.promptFixture.show('e1'); window.promptFixture.session(null)`);
    assert.equal(await wait("document.body.innerText.includes('Select an owned session')"), "ready");
    assert.equal(await evaluate("window.promptFixture.reads.length"), 9);
    await evaluate(`window.promptFixture.session('three')`);
    assert.equal(await wait("window.promptFixture.reads.length === 10"), "ready");
    await evaluate(`window.promptFixture.reads[9].resolve({status:'ok',epoch:'e1',sessionId:'three',prompts:[],truncated:false})`);
    assert.equal(await wait("document.body.innerText.includes('No effective prompts were discovered')"), "ready");
  } finally {
    socket?.close(); browser?.kill(); await rm(root, { recursive: true, force: true, maxRetries: 8, retryDelay: 100 });
  }
});
