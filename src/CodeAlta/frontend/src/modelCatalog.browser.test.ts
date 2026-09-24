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

// Mounts the production ModelCatalogPanel; responses are literal test-owned inventory, never provider network/auth.
test("mounted catalog selects providers, filters models and rejects errors, stale reads and catalog-only defaults", { skip: !edge, timeout: 60_000 }, async () => {
  const app = readFileSync(fileURLToPath(new URL("./main.tsx", import.meta.url)), "utf8");
  assert.match(app, /action === "models"\) navigate\("models"\)/, "the production shortcut dispatcher must navigate to the distinct catalog");
  assert.match(app, /readProviders=\{modelCatalog\.providers\} readModels=\{modelCatalog\.models\}/, "the production app uses host RPC reads");
  assert.match(app, /applyCatalogNextSend\(target/, "the production screen uses the same scoped choice owner as the mounted fixture");
  assert.match(app, /selections=\{nextSendSelections\}/, "the production composer receives the shared instance-owned selection");
  const root = await mkdtemp(join(tmpdir(), "codealta-model-catalog-"));
  let browser: ReturnType<typeof spawn> | undefined;
  let socket: WebSocket | undefined;
  try {
    await build({ entryPoints: [fileURLToPath(new URL("./modelCatalog.mount.tsx", import.meta.url))],
      outfile: join(root, "fixture.js"), bundle: true, platform: "browser", format: "iife",
      plugins: [{ name: "isolated-catalog-choices", setup(build) {
        build.onResolve({ filter: /^#neoastra$/ }, () => ({ path: fileURLToPath(new URL("./modelCatalog.neoastra.mount.ts", import.meta.url)) }));
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
    const evaluate = async (expression: string) => (await command("Runtime.evaluate", { expression, returnByValue: true,
      awaitPromise: true })).result?.value;
    const wait = async (condition: string) => evaluate(`new Promise(resolve => { const end = Date.now() + 7000; const check = () => {
      if (${condition}) resolve('ready'); else if (Date.now() > end) resolve(document.body.innerText.slice(0, 400));
      else setTimeout(check, 20); }; check(); })`);
    const snapshot = async (): Promise<{ text: string; providers: number; models: string[]; buttons: string[] }> => JSON.parse((await evaluate(`JSON.stringify({ text: document.body.innerText,
      providers: window.catalogFixture.providerReads.length, models: window.catalogFixture.modelReads.map(read => read.providerId),
      buttons: Array.from(document.querySelectorAll('.model-catalog-list button')).map(button => button.innerText) })`))!);
    assert.equal(await wait("window.catalogFixture?.providerReads.length === 1"), "ready");
    assert.match((await snapshot()).text, /Loading providers/);
    assert.equal((await snapshot()).models.length, 0, "no provider probing on initial navigation");
    await evaluate(`window.catalogFixture.providerReads[0].resolve({status:'ok',epoch:'epoch-1',truncated:false,providers:[
      {id:'alpha',name:'Alpha',availability:'Unknown',enabled:true},{id:'beta',name:'Beta',availability:'Unknown',enabled:true}]})`);
    assert.equal(await wait("document.querySelectorAll('.model-catalog-providers button').length === 2"), "ready");
    assert.match((await snapshot()).text, /Choose a provider/);
    await evaluate(`document.querySelectorAll('.model-catalog-providers button')[0].click()`);
    assert.equal(await wait("window.catalogFixture.modelReads.length === 1"), "ready");
    assert.match((await snapshot()).text, /Loading models for alpha/);
    await evaluate(`document.querySelectorAll('.model-catalog-providers button')[1].click()`);
    assert.equal(await wait("window.catalogFixture.modelReads.length === 2"), "ready");
    await evaluate(`window.catalogFixture.modelReads[0].resolve({status:'ok',epoch:'epoch-1',providerId:'alpha',availability:'Ready',truncated:false,
      models:[{id:'stale-model',name:'Stale model',description:null,efforts:[],defaultEffort:null,contextTokens:null,inputTokens:null,
        outputTokens:null,reasoning:null,tools:null,structuredOutput:null,imageInput:null}]})`);
    await evaluate(`window.catalogFixture.modelReads[1].resolve({status:'ok',epoch:'epoch-1',providerId:'beta',availability:'Ready',truncated:true,
      models:[{id:'beta-image',name:'Image model',description:'Literal image',efforts:['Low','Medium'],defaultEffort:'Medium',contextTokens:32000,
        inputTokens:null,outputTokens:4000,reasoning:true,tools:false,structuredOutput:null,imageInput:true},
      {id:'beta-text',name:'Text model',description:null,efforts:[],defaultEffort:null,contextTokens:null,inputTokens:null,
        outputTokens:null,reasoning:null,tools:null,structuredOutput:null,imageInput:null}]})`);
    assert.equal(await wait("document.querySelectorAll('.model-catalog-list button').length === 2"), "ready");
    assert.match((await snapshot()).text, /Showing 2 reported models; others are omitted/);
    assert.doesNotMatch((await snapshot()).text, /Stale model/);
    await evaluate(`document.querySelector('.model-catalog-list button').click()`);
    assert.equal(await wait("document.querySelector('.model-catalog-detail')?.innerText.includes('32,000') || document.querySelector('.model-catalog-detail')?.innerText.includes('32000')"), "ready");
    assert.match((await snapshot()).text, /Pricing\s+Unknown \(not exposed by the host model inventory\)/);
    assert.match((await snapshot()).text, /Supported efforts\s+Low, Medium/);
    await evaluate(`(() => { const input = document.querySelector('input[type=search]');
      Object.getOwnPropertyDescriptor(HTMLInputElement.prototype,'value').set.call(input,'text');
      input.dispatchEvent(new Event('input',{bubbles:true})); })()`);
    assert.equal(await wait("document.querySelectorAll('.model-catalog-list button').length === 1"), "ready");
    assert.deepEqual((await snapshot()).buttons, ["Text model\nbeta-text"]);
    assert.doesNotMatch((await snapshot()).text, /Pricing/, "a filtered-out selection must not retain stale details");
    await evaluate(`document.querySelector('.model-catalog-list button').click()`);
    assert.match((await snapshot()).text, /Context tokens\s+Unknown/);
    await evaluate(`document.querySelectorAll('.model-catalog-providers button')[0].click()`);
    assert.equal(await wait("window.catalogFixture.modelReads.length === 3"), "ready");
    await evaluate(`window.catalogFixture.modelReads[2].reject(new Error('private provider error'))`);
    assert.equal(await wait("document.querySelector('.model-catalog-results [role=alert]')"), "ready");
    assert.match((await snapshot()).text, /Model inventory could not be read/);
    assert.doesNotMatch((await snapshot()).text, /private provider error|Text model/);
    await evaluate(`window.catalogFixture.show('epoch-2')`);
    assert.equal(await wait("window.catalogFixture.providerReads.length === 2"), "ready");
    await evaluate(`window.catalogFixture.providerReads[1].resolve({status:'ok',epoch:'epoch-2',truncated:false,providers:[{id:'alpha',name:'Alpha',availability:'Unknown',enabled:true}]})`);
    assert.equal(await wait("document.querySelectorAll('.model-catalog-providers button').length === 1"), "ready");
    await evaluate(`document.querySelector('.model-catalog-providers button').click()`);
    assert.equal(await wait("window.catalogFixture.modelReads.length === 4"), "ready");
    await evaluate(`window.catalogFixture.show('epoch-3')`);
    assert.equal(await wait("window.catalogFixture.providerReads.length === 3"), "ready");
    await evaluate(`window.catalogFixture.modelReads[3].resolve({status:'ok',epoch:'epoch-2',providerId:'alpha',availability:'Ready',truncated:false,
      models:[{id:'old-epoch',name:'Old epoch model',description:null,efforts:[],defaultEffort:null,contextTokens:null,inputTokens:null,
        outputTokens:null,reasoning:null,tools:null,structuredOutput:null,imageInput:null}]})`);
    assert.doesNotMatch((await snapshot()).text, /Old epoch model/);
    await evaluate(`window.catalogFixture.providerReads[2].resolve({status:'ok',epoch:'epoch-3',truncated:false,providers:[{id:'alpha',name:'Alpha',availability:'Unknown',enabled:true}]})`);
    assert.equal(await wait("document.querySelectorAll('.model-catalog-providers button').length === 1"), "ready");
    await evaluate(`document.querySelector('.model-catalog-providers button').click()`);
    assert.equal(await wait("window.catalogFixture.modelReads.length === 5"), "ready");
    await evaluate(`window.catalogFixture.modelReads[4].resolve({status:'stale_epoch',epoch:'epoch-4',providerId:'alpha',availability:'Unknown',models:[],truncated:false})`);
    assert.equal(await wait("document.querySelector('.model-catalog-results [role=alert]')?.innerText.includes('Reload required')"), "ready");
    await evaluate(`window.catalogFixture.show('epoch-4')`);
    assert.equal(await wait("window.catalogFixture.providerReads.length === 4"), "ready");
    await evaluate(`window.catalogFixture.providerReads[3].reject(new Error('private configuration details'))`);
    assert.equal(await wait("document.querySelector('.model-catalog-providers [role=alert]')"), "ready");
    assert.doesNotMatch((await snapshot()).text, /private configuration details/);
    await evaluate(`window.catalogFixture.show(null)`);
    assert.equal(await wait("document.body.innerText.includes('Catalog-only mode has no owned model inventory')"), "ready");
    assert.equal((await snapshot()).models.length, 5);

    // Real production panel -> production composer -> frozen captured Send, with only test-owned RPC replies.
    await evaluate(`window.catalogFixture.session('one'); window.catalogFixture.show('epoch-1')`);
    assert.equal(await wait("window.catalogFixture.providerReads.length === 5"), "ready");
    await evaluate(`window.catalogFixture.providerReads[4].resolve({status:'ok',epoch:'epoch-1',truncated:false,providers:[
      {id:'alpha',name:'Alpha',availability:'Unknown',enabled:true},{id:'beta',name:'Beta',availability:'Unknown',enabled:true}]})`);
    assert.equal(await wait("document.querySelectorAll('.model-catalog-providers button').length === 2"), "ready");
    await evaluate(`document.querySelectorAll('.model-catalog-providers button')[1].click()`);
    assert.equal(await wait("window.catalogFixture.modelReads.length === 6"), "ready");
    const betaResponse = `{status:'ok',epoch:'epoch-1',providerId:'beta',availability:'Ready',truncated:false,
      models:[{id:'beta-image',name:'Image model',description:null,efforts:['Low','Medium'],defaultEffort:null,contextTokens:32000,
        inputTokens:null,outputTokens:null,reasoning:true,tools:null,structuredOutput:null,imageInput:true},
      {id:'beta-text',name:'Text model',description:null,efforts:[],defaultEffort:null,contextTokens:null,
        inputTokens:null,outputTokens:null,reasoning:null,tools:null,structuredOutput:null,imageInput:null}]}`;
    await evaluate(`window.catalogFixture.modelReads[5].resolve(${betaResponse})`);
    await evaluate(`(() => { const input = document.querySelector('input[type=search]');
      Object.getOwnPropertyDescriptor(HTMLInputElement.prototype,'value').set.call(input,'');
      input.dispatchEvent(new Event('input',{bubbles:true})); })()`);
    assert.equal(await wait("document.querySelectorAll('.model-catalog-list button').length === 2"), "ready");
    await evaluate(`document.querySelector('.model-catalog-list button').click()`);
    assert.equal(await wait("document.querySelector('.model-catalog-next')?.innerText.includes('Session-recorded model: beta-text')"), "ready");
    assert.match((await snapshot()).text, /Next Send: beta-text · effort High · prompt plan/);
    await evaluate(`document.querySelector('.model-catalog-next select').value='Medium';
      document.querySelector('.model-catalog-next select').dispatchEvent(new Event('change',{bubbles:true}))`);
    // React's controlled select dispatches an actual change event, not a direct storage write.
    await evaluate(`document.querySelector('.model-catalog-next button').click()`);
    assert.equal(await wait("document.querySelector('select[aria-label=Model]')?.value === 'beta-image'"), "ready");
    assert.equal(await evaluate(`document.querySelector('select[aria-label="Agent prompt"]').value`), "plan");
    assert.equal(await evaluate(`document.querySelector('select[aria-label="Reasoning"]').value`), "Medium");
    await evaluate(`(() => { const input = document.querySelector('#session-prompt');
      Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype,'value').set.call(input,'Literal next Send');
      input.dispatchEvent(new Event('input',{bubbles:true})); })()`);
    assert.equal(await wait("document.querySelector('#session-prompt')?.value === 'Literal next Send'"), "ready");
    await evaluate(`document.querySelector('.send-button').click()`);
    assert.equal(await wait("window.catalogFixture.sent.length === 1"), "ready");
    const captured = JSON.parse((await evaluate(`JSON.stringify(window.catalogFixture.sent[0])`))!);
    assert.deepEqual(captured.selection, { providerKey: "beta", agentPromptId: "plan", modelId: "beta-image", reasoningEffort: "Medium" });
    assert.equal(captured.sessionId, "one");
    await evaluate(`window.catalogFixture.openModels()`);
    assert.equal(await wait("window.catalogFixture.providerReads.length === 6"), "ready");
    await evaluate(`window.catalogFixture.providerReads[5].resolve({status:'ok',epoch:'epoch-1',truncated:false,providers:[
      {id:'alpha',name:'Alpha',availability:'Unknown',enabled:true},{id:'beta',name:'Beta',availability:'Unknown',enabled:true}]})`);
    assert.equal(await wait("document.querySelectorAll('.model-catalog-providers button').length === 2"), "ready");
    await evaluate(`document.querySelectorAll('.model-catalog-providers button')[1].click()`);
    assert.equal(await wait("window.catalogFixture.modelReads.length === 7"), "ready");
    await evaluate(`window.catalogFixture.modelReads[6].resolve(${betaResponse})`);
    assert.equal(await wait("document.querySelectorAll('.model-catalog-list button').length === 2"), "ready");
    await evaluate(`document.querySelectorAll('.model-catalog-list button')[1].click()`);
    assert.equal(await wait("document.querySelector('.model-catalog-next')?.innerText.includes('Retained exact request')"), "ready");
    assert.equal(await evaluate(`document.querySelector('.model-catalog-next button').disabled`), true);
    assert.equal((await snapshot()).text.includes("Retained exact request (beta-image)"), true);
    assert.deepEqual(JSON.parse((await evaluate(`JSON.stringify(window.catalogFixture.sent[0])`))!).selection, captured.selection);

    await evaluate(`window.catalogFixture.session('two')`);
    assert.equal(await wait("document.querySelector('.model-catalog-next')?.innerText.includes('Session: two')"), "ready");
    assert.equal(await wait("!document.querySelector('.model-catalog-next')?.innerText.includes('Loading session choices')"), "ready");
    await evaluate(`window.catalogFixture.holdChoices=true; document.querySelector('.model-catalog-next button').click()`);
    assert.equal(await wait("!!window.catalogFixture.releaseChoices"), "ready");
    await evaluate(`window.catalogFixture.session('three'); window.catalogFixture.holdChoices=false; window.catalogFixture.releaseChoices()`);
    assert.equal(await wait("document.querySelector('.model-catalog-next')?.innerText.includes('Session: three')"), "ready");
    assert.equal(await evaluate(`localStorage.getItem('codealta.desktop.selection.two')`), null,
      "a switched session cannot receive the stale in-flight selection");
    await evaluate(`document.querySelectorAll('.model-catalog-providers button')[0].click()`);
    assert.equal(await wait("window.catalogFixture.modelReads.length === 8"), "ready");
    await evaluate(`window.catalogFixture.modelReads[7].resolve({status:'ok',epoch:'epoch-1',providerId:'alpha',availability:'Ready',truncated:false,
      models:[{id:'alpha-real',name:'Alpha real',description:null,efforts:[],defaultEffort:null,contextTokens:null,
        inputTokens:null,outputTokens:null,reasoning:null,tools:null,structuredOutput:null,imageInput:null}]})`);
    assert.equal(await wait("document.querySelector('.model-catalog-list button')?.innerText.includes('Alpha real')"), "ready");
    await evaluate(`document.querySelector('.model-catalog-list button').click()`);
    assert.equal(await wait("document.querySelector('.model-catalog-next')?.innerText.includes('not the selected session')"), "ready");
    assert.equal(await evaluate(`document.querySelector('.model-catalog-next button').disabled`), true);
    assert.equal(await evaluate(`localStorage.getItem('codealta.desktop.selection.three')`), null);

    await evaluate(`document.querySelectorAll('.model-catalog-providers button')[1].click()`);
    assert.equal(await wait("window.catalogFixture.modelReads.length === 9"), "ready");
    await evaluate(`window.catalogFixture.modelReads[8].resolve(${betaResponse})`);
    assert.equal(await wait("document.querySelectorAll('.model-catalog-list button').length === 2"), "ready");
    await evaluate(`document.querySelector('.model-catalog-list button').click()`);
    assert.equal(await wait("document.querySelector('.model-catalog-next button:not(:disabled)')"), "ready");
    await evaluate(`window.catalogFixture.holdChoices=true; document.querySelector('.model-catalog-next button').click()`);
    assert.equal(await wait("!!window.catalogFixture.releaseChoices"), "ready");
    await evaluate(`window.catalogFixture.show('epoch-2'); window.catalogFixture.holdChoices=false; window.catalogFixture.releaseChoices()`);
    assert.equal(await wait("document.body.innerText.includes('Loading providers')"), "ready");
    assert.equal(await evaluate(`localStorage.getItem('codealta.desktop.selection.three')`), null,
      "the old host cannot write a new next-Send selection");

    await evaluate(`window.catalogFixture.show('epoch-1')`);
    assert.equal(await wait("window.catalogFixture.providerReads.length === 8"), "ready");
    await evaluate(`window.catalogFixture.providerReads[7].resolve({status:'ok',epoch:'epoch-1',truncated:false,providers:[
      {id:'beta',name:'Beta',availability:'Ready',enabled:true}]})`);
    assert.equal(await wait("document.querySelectorAll('.model-catalog-providers button').length === 1"), "ready");
    await evaluate(`document.querySelector('.model-catalog-providers button').click()`);
    assert.equal(await wait("window.catalogFixture.modelReads.length === 10"), "ready");
    await evaluate(`window.catalogFixture.modelReads[9].resolve(${betaResponse})`);
    assert.equal(await wait("document.querySelectorAll('.model-catalog-list button').length === 2"), "ready");
    await evaluate(`document.querySelector('.model-catalog-list button').click()`);
    assert.equal(await wait("document.querySelector('.model-catalog-next button:not(:disabled)')"), "ready");
    await evaluate(`window.catalogFixture.holdChoices=true; document.querySelector('.model-catalog-next button').click()`);
    assert.equal(await wait("!!window.catalogFixture.releaseChoices"), "ready");
    await evaluate(`window.catalogFixture.holdChoices=false; window.catalogFixture.leaveCatalog(); window.catalogFixture.releaseChoices()`);
    assert.equal(await wait("document.querySelector('select[aria-label=Model]')?.value === 'beta-text'"), "ready");
    assert.equal(await evaluate(`localStorage.getItem('codealta.desktop.selection.three')`), null,
      "a canceled catalog action cannot write even if its session is still selected");
  } finally {
    socket?.close(); browser?.kill();
    await rm(root, { recursive: true, force: true, maxRetries: 6, retryDelay: 100 });
  }
});
