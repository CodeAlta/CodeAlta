import assert from "node:assert/strict";
import { inventoryLanguages, inventoryNarrow } from "./inventoryLocalizationChecks";
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
    await writeFile(join(root, "style.css"), readFileSync(fileURLToPath(new URL("./style.css", import.meta.url))));
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
    const evaluate = async (expression: string) => (await command("Runtime.evaluate", { expression, returnByValue: true,
      awaitPromise: true })).result?.value;
    const wait = async (condition: string) => evaluate(`new Promise(resolve => { const end = Date.now() + 7000; const check = () => {
      if (${condition}) resolve('ready'); else if (Date.now() > end) resolve(document.body.innerText.slice(0, 500));
      else setTimeout(check, 20); }; check(); })`);
    const prompt = (epoch: string, sessionId: string) => `({status:'ok',epoch:'${epoch}',sessionId:'${sessionId}',truncated:true,prompts:[
      {id:'default',name:'Default',description:null,scope:'BuiltIn',builtIn:true,appended:false,body:'built in',bodyTruncated:false},
      {id:'plan',name:'Plan',description:'No description supplied.',scope:'Project',builtIn:false,appended:true,body:'Settings\\nUnknown\\n<tools> literal source'.padEnd(2048,'x'),bodyTruncated:true}]})`;
    assert.equal(await wait("window.promptFixture?.reads.length === 1"), "ready");
    const languages = () => inventoryLanguages(evaluate,
      "[promptFixture.reads.length,promptFixture.choicesReads,promptFixture.sent,JSON.stringify(localStorage)]", "Agent prompts");
    await languages();
    assert.match((await evaluate("document.body.innerText"))!, /Loading prompts/);
    await evaluate(`window.promptFixture.session('two')`);
    assert.equal(await wait("window.promptFixture.reads.length === 2"), "ready");
    await evaluate(`window.promptFixture.reads[0].resolve(${prompt("e1", "one")})`);
    await evaluate(`window.promptFixture.reads[1].resolve({status:'stale_epoch',epoch:'other',sessionId:'two',prompts:[],truncated:false})`);
    assert.equal(await wait("document.body.innerText.includes('Reload required')"), "ready");
    await languages();
    assert.equal(await evaluate("document.querySelector('.prompt-catalog-body')"), null);
    await evaluate(`window.promptFixture.session('one')`);
    assert.equal(await wait("window.promptFixture.reads.length === 3"), "ready");
    await evaluate(`window.promptFixture.reads[2].reject(new Error('private catalog failure'))`);
    assert.equal(await wait("document.body.innerText.includes('could not be read')"), "ready");
    await languages();
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
    assert.equal(await evaluate("document.querySelector('.prompt-catalog-body').textContent.length"), 2048);
    assert.equal(await evaluate("document.querySelector('.prompt-catalog-body tools')"), null, "literal source never becomes HTML");
    assert.match((await evaluate("document.body.innerText"))!, /read-only here|Read-only here/);
    assert.equal(await wait("document.body.innerText.includes('Session-recorded prompt: default')"), "ready");
    assert.match((await evaluate("document.body.innerText"))!, /Next Send: default · model model · effort High/);
    await inventoryLanguages(evaluate, "[promptFixture.reads.length,promptFixture.choicesReads]", "Agent prompts", "Content truncated to 2,048 characters. This is not the full prompt.", ".model-catalog-detail > p:nth-of-type(2)");
    await inventoryNarrow(evaluate, command);
    await languages();
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
    await languages();
    assert.equal(await evaluate("document.querySelector('.model-catalog-next button').disabled"), true);
    await evaluate(`window.promptFixture.session('two')`);
    assert.equal(await wait("window.promptFixture.reads.length === 7"), "ready");
    await evaluate(`window.promptFixture.reads[6].resolve(${prompt("e1", "two")})`);
    await evaluate(`document.querySelectorAll('[aria-label="Prompt inventory"] button')[1].click()`);
    assert.equal(await wait("document.querySelector('.model-catalog-next button:not(:disabled)')"), "ready");
    await evaluate(`window.promptFixture.hold=true; document.querySelector('.model-catalog-next button').click()`);
    assert.equal(await wait("!!window.promptFixture.release"), "ready");
    await languages();
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
    await languages();
    await evaluate("promptFixture.session('four')");
    assert.equal(await wait("promptFixture.reads.length===11"), "ready");
    await evaluate(`promptFixture.reads[10].resolve({...${prompt("e1", "four")},prompts:[{id:'Settings',name:'Settings',description:null,scope:'Project',builtIn:false,appended:false,body:'x'.repeat(2049),bodyTruncated:false}]})`);
    assert.equal(await wait("document.body.innerText.includes('Invalid prompt inventory')"), "ready");
    await languages();
    assert.equal(await evaluate("document.querySelector('.prompt-catalog-body')"), null);
    // Cached prompt editing follows every original catalog scenario.
    await evaluate("promptFixture.choices.prompts=[{id:'default',name:'Default'},{id:'plan',name:'Plan'}];promptFixture.session('chooser');promptFixture.leave()");
    assert.equal(await wait("document.querySelector('select[aria-label=\"Agent prompt\"]')?.value==='default'"), "ready");
    assert.equal(await evaluate("!!document.querySelector('#next-send-prompt-chooser:not(:disabled)')"), true,
      "cached owned choices expose a deliberate prompt chooser without opening Settings");
    await evaluate("window.promptReads=promptFixture.choicesReads;document.querySelector('#next-send-prompt-chooser').focus();document.querySelector('#next-send-prompt-chooser').click()");
    assert.equal(await wait("document.querySelector('.prompt-chooser')?.open"), "ready");
    assert.equal(await evaluate("document.activeElement===document.querySelector('.prompt-chooser input')"), true);
    await evaluate("{const i=document.querySelector('.prompt-chooser input');Object.getOwnPropertyDescriptor(HTMLInputElement.prototype,'value').set.call(i,'pLaN');i.dispatchEvent(new Event('input',{bubbles:true}))}");
    assert.equal(await wait("document.querySelectorAll('.prompt-chooser .model-chooser-list button').length===1"), "ready");
    await evaluate("{const i=document.querySelector('.prompt-chooser input');Object.getOwnPropertyDescriptor(HTMLInputElement.prototype,'value').set.call(i,'.*');i.dispatchEvent(new Event('input',{bubbles:true}))}");
    assert.equal(await wait("document.querySelector('.prompt-chooser').textContent.includes('No agent prompts match this search.')"), "ready");
    await evaluate("{const i=document.querySelector('.prompt-chooser input');Object.getOwnPropertyDescriptor(HTMLInputElement.prototype,'value').set.call(i,'Plan');i.dispatchEvent(new Event('input',{bubbles:true}))}");
    assert.equal(await wait("document.querySelectorAll('.prompt-chooser .model-chooser-list button').length===1"), "ready");
    await evaluate("document.querySelector('.prompt-chooser .model-chooser-list button').click()");
    assert.equal(await evaluate("document.querySelector('select[aria-label=\"Agent prompt\"]').value"), "default");
    await evaluate("document.querySelector('.prompt-chooser header button').click()");
    assert.equal(await wait("!document.querySelector('.prompt-chooser') && document.activeElement.id==='next-send-prompt-chooser'"), "ready");
    for (const change of ["same-object", "newer-selection", "observed-current", "input", "modal", "own-modal"]) {
      await evaluate("promptFixture.publish('default')");
      await evaluate("document.querySelector('#next-send-prompt-chooser').click()");
      assert.equal(await wait("document.querySelector('.prompt-chooser')?.open"), "ready");
      await evaluate("document.querySelectorAll('.prompt-chooser .model-chooser-list button')[1].click();void(window.promptApply=document.querySelector('.prompt-chooser-apply'))");
      if (change === "same-object") await evaluate("promptFixture.repeatSelection();promptFixture.repeatSelection()");
      if (change === "newer-selection") await evaluate("promptFixture.publish('plan')");
      if (change === "observed-current") await evaluate("promptFixture.lastChoices.current={...promptFixture.lastChoices.current,agentPromptId:'plan'}");
      if (change === "input") await evaluate("{const i=document.querySelector('#session-prompt');Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype,'value').set.call(i,'New draft');i.dispatchEvent(new Event('input',{bubbles:true}))}");
      if (change === "modal") await evaluate("{const d=document.createElement('dialog');document.body.append(d);d.showModal();d.close();d.remove()}");
      if (change === "own-modal") await evaluate("{const d=document.querySelector('.prompt-chooser');d.close();d.showModal();promptApply.click()}");
      await evaluate("promptApply.click()");
      if (change !== "own-modal") assert.equal(await wait("!!document.querySelector('.prompt-chooser [role=alert]')"), "ready", change);
      assert.equal(await evaluate("JSON.parse(localStorage.getItem('codealta.desktop.selection.chooser')).agentPromptId"), change === "newer-selection" ? "plan" : "default", change);
      await evaluate("document.querySelector('.prompt-chooser header button')?.click();promptFixture.lastChoices.current={...promptFixture.lastChoices.current,agentPromptId:'default'}");
      assert.equal(await wait("!document.querySelector('.prompt-chooser')"), "ready");
    }
    await evaluate("document.querySelector('#next-send-prompt-chooser').click()");
    assert.equal(await wait("document.querySelector('.prompt-chooser')?.open"), "ready");
    await evaluate("document.querySelectorAll('.prompt-chooser .model-chooser-list button')[1].click()");
    await evaluate("document.querySelector('.prompt-chooser-apply').click()");
    assert.equal(await wait("!document.querySelector('.prompt-chooser') && document.querySelector('select[aria-label=\"Agent prompt\"]').value==='plan'"), "ready");
    assert.equal(await evaluate("document.querySelector('.prompt-selection-observation').textContent.includes('default') && document.querySelector('.prompt-selection-observation').textContent.includes('plan')"), true);
    assert.deepEqual(JSON.parse((await evaluate("localStorage.getItem('codealta.desktop.selection.chooser')"))!),
      { providerKey: "beta", agentPromptId: "plan", modelId: "model", reasoningEffort: "High" });
    assert.equal(await evaluate("promptFixture.choicesReads===promptReads"), true);
    await evaluate("document.querySelector('.send-button').click()");
    assert.equal(await wait("promptFixture.sent.length===2 && !document.querySelector('.send-button').disabled"), "ready");
    assert.equal(await evaluate("document.querySelector('#next-send-prompt-chooser').disabled"), true, "uncertainty does not unlock prompt editing");
    assert.deepEqual(JSON.parse((await evaluate("JSON.stringify(promptFixture.sent[1].selection)"))!),
      { providerKey: "beta", agentPromptId: "plan", modelId: "model", reasoningEffort: "High" });
    assert.equal(await evaluate("promptFixture.sent[1].text"), "New draft");
    // A refreshed catalog omitting the stored model must not let prompt-only
    // editing replace that model/effort with observed defaults.
    await evaluate("promptFixture.session('removed-model')");
    assert.equal(await wait("!!document.querySelector('#next-send-prompt-chooser:not(:disabled)')"), "ready");
    await evaluate("promptFixture.selectRemovedModel()");
    assert.equal(await wait("document.querySelector('select[aria-label=Model]')?.value==='removed'"), "ready");
    await evaluate("[...document.querySelectorAll('.owned-session button')].find(b=>b.textContent.trim()==='Refresh choices').click()");
    assert.equal(await wait("document.querySelector('select[aria-label=Model]')?.value==='model' && document.querySelector('#next-send-prompt-chooser').disabled"), "ready");
    await evaluate("document.querySelector('#next-send-prompt-chooser').click()");
    assert.equal(await evaluate("!document.querySelector('.prompt-chooser') && JSON.parse(localStorage.getItem('codealta.desktop.selection.removed-model')).modelId==='removed' && JSON.parse(localStorage.getItem('codealta.desktop.selection.removed-model')).reasoningEffort==='Low'"), true);
    await evaluate("{const model=document.querySelector('select[aria-label=Model]');model.value='model';model.dispatchEvent(new Event('change',{bubbles:true}))}");
    assert.equal(await wait("!!document.querySelector('#next-send-prompt-chooser:not(:disabled)')"), "ready");
    // Optional prompt-search limits must not change the shared composer contract.
    await evaluate("promptFixture.choices.prompts=[{id:'default',name:'Default'},...Array.from({length:128},(_,i)=>({id:'prompt-'+i,name:'Prompt '+i}))];promptFixture.choices.models=[{id:'model',name:'Model',efforts:['High','Low'],imageInput:true},{id:'other',name:'Other',efforts:['Low'],imageInput:true}];promptFixture.session('large-catalog')");
    assert.equal(await wait("promptFixture.lastChoices.sessionId==='large-catalog'"), "ready");
    await evaluate("new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(()=>resolve(true))))");
    assert.equal(await evaluate("document.querySelector('#next-send-prompt-chooser').disabled"), true);
    assert.equal(await evaluate("[...document.querySelectorAll('.prompt-options select')].length===3 && [...document.querySelectorAll('.prompt-options select')].every(s=>!s.disabled) && !document.querySelector('#next-send-model-chooser').disabled"), true,
      "129 valid prompts refuse only prompt search, not quick selectors or the accepted model chooser");
    assert.equal(await evaluate("document.querySelector('select[aria-label=\"Agent prompt\"]').options.length"), 129);
    await evaluate("{const s=document.querySelector('select[aria-label=\"Agent prompt\"]');s.value='prompt-127';s.dispatchEvent(new Event('change',{bubbles:true}))}");
    await evaluate("{const s=document.querySelector('select[aria-label=Model]');s.value='other';s.dispatchEvent(new Event('change',{bubbles:true}))}");
    await evaluate("{const s=document.querySelector('select[aria-label=Reasoning]');s.value='Low';s.dispatchEvent(new Event('change',{bubbles:true}))}");
    assert.deepEqual(JSON.parse((await evaluate("localStorage.getItem('codealta.desktop.selection.large-catalog')"))!),
      { providerKey: "beta", agentPromptId: "prompt-127", modelId: "other", reasoningEffort: "Low" });
    await evaluate("window.largeReads=promptFixture.choicesReads;document.querySelector('#next-send-model-chooser').click()");
    assert.equal(await wait("document.querySelector('.model-chooser')?.open"), "ready");
    await evaluate("document.querySelector('.model-chooser-list button').click()");
    await evaluate("{const s=document.querySelector('.model-chooser select');s.value='High';s.dispatchEvent(new Event('change',{bubbles:true}))}");
    await evaluate("document.querySelector('.model-chooser-apply').click()");
    assert.equal(await wait("!document.querySelector('.model-chooser') && document.querySelector('select[aria-label=Model]').value==='model'"), "ready");
    await evaluate("{const c=document.createElement('canvas');c.width=1;c.height=1;window.largeImage={title:'Large catalog image',mediaType:'image/png',base64:c.toDataURL('image/png').split(',')[1]};const owner=promptFixture.imageOwner;const key=JSON.stringify(['e1','large-catalog',null,null]);owner.replace(key,owner.get(key),[largeImage]);const i=document.querySelector('#session-prompt');Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype,'value').set.call(i,'Large catalog exact Send');i.dispatchEvent(new Event('input',{bubbles:true}))}");
    assert.equal(await wait("!!document.querySelector('.prompt-image-attachments input') && !document.querySelector('.send-button').disabled"), "ready");
    assert.equal(await evaluate("promptFixture.choicesReads===largeReads && document.querySelector('#next-send-prompt-chooser').disabled"), true);
    await evaluate("document.querySelector('.send-button').click()");
    assert.equal(await wait("promptFixture.sent.length===3"), "ready");
    assert.deepEqual(JSON.parse((await evaluate("JSON.stringify(promptFixture.sent[2].selection)"))!),
      { providerKey: "beta", agentPromptId: "prompt-127", modelId: "model", reasoningEffort: "High" });
    assert.equal(await evaluate("promptFixture.sent[2].expectedEpoch==='e1' && promptFixture.sent[2].sessionId==='large-catalog' && promptFixture.sent[2].text==='Large catalog exact Send' && JSON.stringify(promptFixture.sent[2].images)===JSON.stringify([largeImage])"), true);
    await evaluate("promptFixture.choices.prompts=[{id:'default',name:'Default'},{id:'plan',name:'Plan'}];promptFixture.session('missing-current')");
    assert.equal(await wait("!!document.querySelector('#next-send-prompt-chooser:not(:disabled)')"), "ready");
    await evaluate("promptFixture.choices.prompts=[{id:'plan',name:'Plan'}];[...document.querySelectorAll('.owned-session button')].find(b=>b.textContent.trim()==='Refresh choices').click()");
    assert.equal(await wait("promptFixture.lastChoices.sessionId==='missing-current' && promptFixture.lastChoices.prompts.length===1 && document.querySelector('#next-send-prompt-chooser').disabled"), "ready");
    assert.equal(await evaluate("[...document.querySelectorAll('.prompt-options select')].every(s=>!s.disabled) && !document.querySelector('#next-send-model-chooser').disabled && document.querySelector('select[aria-label=\"Agent prompt\"]').value==='default'"), true,
      "an observed prompt absent from refreshed inventory stays visible and permits explicit quick recovery");
    await evaluate("{const s=document.querySelector('select[aria-label=\"Agent prompt\"]');s.value='plan';s.dispatchEvent(new Event('change',{bubbles:true}))}");
    assert.equal(await wait("document.querySelector('select[aria-label=\"Agent prompt\"]').value==='plan'"), "ready");
    assert.deepEqual(JSON.parse((await evaluate("localStorage.getItem('codealta.desktop.selection.missing-current')"))!),
      { providerKey: "beta", agentPromptId: "plan", modelId: "model", reasoningEffort: "High" });
    assert.equal(await evaluate("document.querySelector('#next-send-prompt-chooser').disabled && document.querySelector('.prompt-selection-observation').textContent.includes('default') && document.querySelector('.prompt-selection-observation').textContent.includes('plan')"), true);
  } finally {
    socket?.close(); browser?.kill(); await rm(root, { recursive: true, force: true, maxRetries: 8, retryDelay: 100 });
  }
});
