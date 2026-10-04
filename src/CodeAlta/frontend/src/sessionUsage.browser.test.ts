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

test("production composer usage inspector reads only on intent and fences late/foreign responses", { skip: !edge, timeout: 90_000 }, async () => {
  const root = await mkdtemp(join(tmpdir(), "codealta-usage-ui-"));
  let browser: ReturnType<typeof spawn> | undefined;
  let socket: WebSocket | undefined;
  try {
    await build({ entryPoints: [fileURLToPath(new URL("./main.tsx", import.meta.url))], outfile: join(root, "fixture.js"),
      bundle: true, platform: "browser", format: "iife", loader: { ".css": "empty", ".flf": "text", ".svg": "dataurl" },
      define: { "import.meta.env.VITE_DEMO_MODE": '"false"' }, plugins: [{ name: "isolated-bridge", setup(bundle) {
        bundle.onResolve({ filter: /^#neoastra$/ }, () => ({ path: fileURLToPath(new URL("./settingsShell.neoastra.mount.ts", import.meta.url)) }));
      } }] });
    await writeFile(join(root, "style.css"), readFileSync(new URL("../node_modules/flexlayout-react/style/light.css", import.meta.url), "utf8") +
      "\n" + readFileSync(new URL("./style.css", import.meta.url), "utf8"));
    const page = join(root, "fixture.html");
    await writeFile(page, '<!doctype html><html><head><link rel="stylesheet" href="style.css"></head><body><div id="root"></div><script src="fixture.js"></script></body></html>');
    browser = spawn(edge!, ["--headless=new", "--disable-gpu", "--no-first-run", "--disable-background-networking", "--disable-extensions",
      `--user-data-dir=${join(root, "profile")}`, "--remote-debugging-port=0", "about:blank"], { stdio: "ignore", windowsHide: true });
    let port = "";
    for (let attempt = 0; attempt < 100 && !port; attempt++) {
      try { port = (await readFile(join(root, "profile", "DevToolsActivePort"), "utf8")).split(/\r?\n/)[0]; }
      catch { await new Promise(resolve => setTimeout(resolve, 50)); }
    }
    assert.match(port, /^\d+$/);
    const pages: { type?: string; url?: string; webSocketDebuggerUrl?: string }[] =
      await (await fetch(`http://127.0.0.1:${port}/json/list`, { signal: AbortSignal.timeout(10_000) })).json();
    const tab = pages.find(value => value.type === "page" && value.url === "about:blank");
    assert.ok(tab?.webSocketDebuggerUrl);
    socket = new WebSocket(tab.webSocketDebuggerUrl);
    await new Promise<void>((resolve, reject) => { socket!.addEventListener("open", () => resolve(), { once: true });
      socket!.addEventListener("error", () => reject(new Error("test browser unavailable")), { once: true }); });
    let sequence = 0;
    const command = (method: string, params: object = {}) => new Promise<{ result?: { value?: unknown } }>((resolve, reject) => {
      const id = ++sequence;
      const timer = setTimeout(() => reject(new Error(`browser ${method} timed out`)), 12_000);
      const reply = (event: MessageEvent) => {
        const message = JSON.parse(String(event.data)) as { id?: number; result?: { result?: { value?: unknown } }; error?: object };
        if (message.id !== id) return;
        socket!.removeEventListener("message", reply); clearTimeout(timer);
        if (message.error) reject(new Error(`browser ${method} failed: ${JSON.stringify(message.error)}`)); else resolve(message.result ?? {});
      };
      socket!.addEventListener("message", reply);
      socket!.send(JSON.stringify({ id, method, params }));
    });
    await command("Page.enable");
    await command("Emulation.setDeviceMetricsOverride", { width: 1120, height: 800, deviceScaleFactor: 1, mobile: false });
    await command("Page.navigate", { url: pathToFileURL(page).href });
    const evaluate = async (expression: string) => (await command("Runtime.evaluate", { expression, returnByValue: true, awaitPromise: true })).result?.value;
    const wait = (condition: string) => evaluate(`new Promise(resolve=>{const end=Date.now()+7000;const tick=()=>{
      if(${condition}) resolve(true); else if(Date.now()>end) resolve(document.body.innerText.slice(-1000)); else setTimeout(tick,20);};tick();})`);
    const trigger = '[aria-label="Inspect last-observed session usage"]';
    const modal = '.session-usage-dialog';
    assert.equal(await wait("!!document.querySelector('#catalog-prompt')"), true);
    assert.equal(await evaluate(`!document.querySelector('${trigger}') && window.settingsShellFixture.usageReads.length===0`), true);
    await evaluate("localStorage.setItem('settingsFixtureOwned','true')");
    await command("Page.reload");
    assert.equal(await wait(`!!document.querySelector('${trigger}') && !!document.querySelector('#session-prompt')`), true);
    assert.equal(await evaluate("window.settingsShellFixture.usageReads.length"), 0);
    await evaluate(`(() => { const input=document.querySelector('#session-prompt'); window.keptComposer=input;
      window.keptTimeline=document.querySelector('.timeline-scroll'); window.keptScroll=window.keptTimeline.scrollTop;
      Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype,'value').set.call(input,'Usage draft survives');
      input.dispatchEvent(new Event('input',{bubbles:true})); document.querySelector('${trigger}').click(); })()`);
    assert.equal(await wait(`document.querySelector('${modal}')?.matches(':modal') && window.settingsShellFixture.usageReads.length===1`), true);
    assert.equal(await evaluate(`document.activeElement?.closest('${modal}')!==null`), true);
    assert.deepEqual(await evaluate("window.settingsShellFixture.usageReads[0].request"), {
      expectedHostEpoch: "12345678-1234-1234-1234-123456789abc", sessionId: "one", scope: "project", projectId: "project", expectedProjectPath: "/fixture/project" });
    const observed = `({ status:'ok', hostEpoch:'12345678-1234-1234-1234-123456789abc',sessionId:'one',runtimeInstanceId:'12345678-1234-1234-1234-123456789abe',
      attachmentGeneration:'9223372036854775807',omittedUsageEvents:'0',observation:{ sequence:'9223372036854775806',scope:'CurrentWindow',source:'CodexTokenCountEvent',
      sourceUpdatedAt:null,eventTimestamp:null,hadInvalidValues:true,hadOmittedData:true,window:{currentTokens:'0',tokenLimit:'9223372036854775807',messageCount:null},
      lastOperation:{inputTokens:null,outputTokens:'0',cacheReadTokens:null,cacheWriteTokens:null,cachedInputTokens:null,reasoningTokens:null,cost:'0.01',durationMs:null}}})`;
    await evaluate(`window.settingsShellFixture.usageReads[0].resolve(${observed})`);
    assert.equal(await wait(`document.querySelector('${modal}')?.textContent.includes('9223372036854775807') && document.querySelector('${modal}')?.textContent.includes('Unknown / 0')`), true);
    assert.equal(await evaluate(`window.keptComposer===document.querySelector('#session-prompt') && window.keptTimeline===document.querySelector('.timeline-scroll') && document.querySelector('#session-prompt').value==='Usage draft survives' && window.keptScroll===window.keptTimeline.scrollTop`), true);
    await command("Input.dispatchMouseEvent", { type: "mousePressed", x: 5, y: 5, button: "left", clickCount: 1 });
    await command("Input.dispatchMouseEvent", { type: "mouseReleased", x: 5, y: 5, button: "left", clickCount: 1 });
    await command("Input.dispatchKeyEvent", { type: "keyDown", key: "Enter", code: "Enter", windowsVirtualKeyCode: 13, modifiers: 2 });
    await command("Input.dispatchKeyEvent", { type: "keyUp", key: "Enter", code: "Enter", windowsVirtualKeyCode: 13, modifiers: 2 });
    assert.equal(await evaluate(`document.querySelector('${modal}')?.open && window.settingsShellFixture.sends.length===0`), true);
    await evaluate(`document.querySelector('${modal} button:nth-last-child(2)').click()`);
    assert.equal(await wait("window.settingsShellFixture.usageReads.length===2"), true);
    assert.equal(await evaluate(`!document.querySelector('${modal}')?.textContent.includes('9223372036854775807')`), true);
    await evaluate(`window.settingsShellFixture.usageReads[1].resolve({...${observed}, attachmentGeneration:'2', observation:{...${observed}.observation,sequence:'1'}})`);
    assert.equal(await wait(`document.querySelector('${modal}')?.textContent.includes('2 / 1')`), true);
    await evaluate(`document.querySelector('${modal} button:nth-last-child(2)').click()`);
    assert.equal(await wait("window.settingsShellFixture.usageReads.length===3"), true);
    await evaluate("window.settingsShellFixture.usageReads[2].reject(new Error('private path'))");
    assert.equal(await wait(`document.querySelector('${modal}')?.textContent.includes('Usage read failed') && !document.querySelector('${modal}')?.textContent.includes('2 / 1')`), true);
    assert.equal(await evaluate(`!document.querySelector('${modal}')?.textContent.includes('private path')`), true);
    await evaluate(`document.querySelector('${modal} button').dispatchEvent(new KeyboardEvent('keydown',{key:'Escape',isComposing:true,bubbles:true,cancelable:true}))`);
    assert.equal(await evaluate(`document.querySelector('${modal}')?.open`), true);
    await command("Input.dispatchKeyEvent", { type: "keyDown", key: "Escape", code: "Escape", windowsVirtualKeyCode: 27 });
    await command("Input.dispatchKeyEvent", { type: "keyUp", key: "Escape", code: "Escape", windowsVirtualKeyCode: 27 });
    assert.equal(await wait(`!document.querySelector('${modal}') && document.activeElement===document.querySelector('${trigger}')`), true);
    await evaluate(`document.querySelector('${trigger}').click()`);
    assert.equal(await wait("window.settingsShellFixture.usageReads.length===4"), true);
    await evaluate(`window.settingsShellFixture.usageReads[3].resolve({...${observed},sessionId:'two'})`);
    assert.equal(await wait(`document.querySelector('${modal}')?.textContent.includes('Invalid or foreign')`), true);
    await evaluate(`document.querySelector('${modal} button[aria-label="Close usage inspector"]').click()`);
    await evaluate(`document.querySelector('${trigger}').click()`);
    assert.equal(await wait("window.settingsShellFixture.usageReads.length===5"), true);
    await evaluate(`window.usageOriginalFrame=requestAnimationFrame; window.usageCloseFrames=[];
      window.requestAnimationFrame=callback=>{window.usageCloseFrames.push(callback); return 123456;};
      document.querySelector('${modal} button[aria-label="Close usage inspector"]').click(); window.settingsShellFixture.usageReads[4].resolve(${observed})`);
    assert.equal(await wait(`!document.querySelector('${modal}')`), true);
    assert.equal(await evaluate(`(() => { window.requestAnimationFrame=window.usageOriginalFrame;
      const editor=document.querySelector('#session-prompt'); editor.focus();
      for(const callback of window.usageCloseFrames) callback(performance.now());
      return document.activeElement===editor; })()`), true, "usage close must not steal a newer composer focus move");
    for (const width of [390, 1120]) for (const theme of ["dark", "light"]) {
      await command("Emulation.setDeviceMetricsOverride", { width, height: 800, deviceScaleFactor: 1, mobile: false });
      await evaluate(`document.documentElement.dataset.theme='${theme}'; document.querySelector('${trigger}').click()`);
      assert.equal(await wait(`document.querySelector('${modal}')?.open`), true);
      assert.equal(await evaluate(`(() => {const d=document.querySelector('${modal}'), r=d.getBoundingClientRect();
        return r.left>=0 && r.right<=${width} && r.height<=800 && getComputedStyle(d).color!=='rgba(0, 0, 0, 0)'})()`), true);
      await evaluate(`document.querySelector('${modal} button[aria-label="Close usage inspector"]').click()`);
    }
    await evaluate(`document.querySelector('${trigger}').click()`);
    assert.equal(await wait("window.settingsShellFixture.usageReads.length===10"), true);
    await evaluate(`document.querySelector('${modal} button[aria-label="Close usage inspector"]').click(); document.querySelector('.session-row:nth-child(2) > button:first-child').click()`);
    assert.equal(await wait(`!document.querySelector('${modal}') && document.querySelector('.session-header h1')?.textContent==='two'`), true);
    await evaluate(`window.settingsShellFixture.usageReads[9].resolve(${observed})`);
    assert.equal(await evaluate(`!document.querySelector('${modal}') && document.querySelector('${trigger}')?.getAttribute('aria-expanded')==='false'`), true);
    await evaluate(`document.querySelector('.session-row:first-child > button:first-child').click()`);
    assert.equal(await wait(`document.querySelector('${trigger}')?.getAttribute('aria-expanded')==='false'`), true);
    await evaluate(`document.querySelector('${trigger}').click()`);
    assert.equal(await wait("window.settingsShellFixture.usageReads.length===11"), true);
    await evaluate(`window.settingsShellFixture.usageReads[10].resolve({...${observed},status:'no_observation',observation:null})`);
    assert.equal(await wait(`document.querySelector('${modal}')?.textContent.includes('No usage observation')`), true);
    assert.equal(await evaluate(`!document.querySelector('${modal}')?.textContent.includes('Reported window tokens')`), true);
    await evaluate(`document.querySelector('${modal} button:nth-last-child(2)').click()`);
    assert.equal(await wait("window.settingsShellFixture.usageReads.length===12"), true);
    await evaluate("window.settingsShellFixture.usageReads[11].resolve({status:'metadata_missing',hostEpoch:'12345678-1234-1234-1234-123456789abc',sessionId:'one',runtimeInstanceId:null,attachmentGeneration:null,omittedUsageEvents:null,observation:null})");
    assert.equal(await wait(`document.querySelector('${modal}')?.textContent.includes('Persisted session metadata')`), true);
    await evaluate(`document.querySelector('${modal} button:nth-last-child(2)').click()`);
    assert.equal(await wait("window.settingsShellFixture.usageReads.length===13"), true);
    await evaluate("window.settingsShellFixture.usageReads[12].resolve({status:'stale_epoch',hostEpoch:'12345678-1234-1234-1234-123456789acd',sessionId:'one',runtimeInstanceId:null,attachmentGeneration:null,omittedUsageEvents:null,observation:null})");
    assert.equal(await wait(`!document.querySelector('${modal}') && document.querySelector('${trigger}')?.disabled`), true);
    await evaluate("localStorage.setItem('usageFixtureTruncated','true')");
    await command("Page.reload");
    assert.equal(await wait("!!document.querySelector('#session-prompt')"), true);
    assert.equal(await evaluate(`!document.querySelector('${trigger}') && window.settingsShellFixture.usageReads.length===0`), true);
    await evaluate("localStorage.removeItem('usageFixtureTruncated'); localStorage.setItem('usageFixtureArchived','true')");
    await command("Page.reload");
    assert.equal(await wait("!!document.querySelector('#catalog-prompt')"), true);
    assert.equal(await evaluate(`!document.querySelector('${trigger}') && window.settingsShellFixture.usageReads.length===0`), true);
    await evaluate("localStorage.removeItem('usageFixtureArchived')");
    await command("Page.reload");
    assert.equal(await wait(`!!document.querySelector('${trigger}')`), true);
    await evaluate(`document.querySelector('${trigger}').click()`);
    assert.equal(await wait(`document.querySelector('${modal}')?.open`), true);
    await evaluate(`window.usageOriginalFrame=requestAnimationFrame; window.usageCloseFrames=[];
      window.requestAnimationFrame=callback=>{window.usageCloseFrames.push(callback); return 123456;};
      document.querySelector('${modal} button[aria-label="Close usage inspector"]').click()`);
    assert.equal(await wait(`!document.querySelector('${modal}')`), true);
    await evaluate(`window.requestAnimationFrame=window.usageOriginalFrame; document.querySelector('${trigger}').click()`);
    assert.equal(await wait(`document.querySelector('${modal}')?.open`), true);
    assert.equal(await evaluate(`(() => { for(const callback of window.usageCloseFrames) callback(performance.now());
      return document.querySelector('${modal}')?.open && document.activeElement?.closest('${modal}')!==null; })()`), true,
      "a deferred close may not steal focus from a reopened usage dialog");
    await evaluate(`document.querySelector('${modal} button[aria-label="Close usage inspector"]').click()`);
    assert.equal(await wait(`!document.querySelector('${modal}') && document.activeElement===document.querySelector('${trigger}')`), true);

    await evaluate(`document.querySelector('${trigger}').click()`);
    assert.equal(await wait(`document.querySelector('${modal}')?.open`), true);
    await evaluate(`window.usageOriginalFrame=requestAnimationFrame; window.usageCloseFrames=[];
      window.requestAnimationFrame=callback=>{window.usageCloseFrames.push(callback); return 123456;};
      document.querySelector('${modal} button[aria-label="Close usage inspector"]').click()`);
    assert.equal(await wait(`!document.querySelector('${modal}')`), true);
    await evaluate(`window.requestAnimationFrame=window.usageOriginalFrame;
      document.querySelector('.activity-rail button[aria-label="Open command palette"]').click()`);
    assert.equal(await wait("document.querySelector('.command-palette')?.open"), true);
    assert.equal(await evaluate("(() => { for(const callback of window.usageCloseFrames) callback(performance.now()); return document.querySelector('.command-palette')?.open && document.activeElement?.closest('.command-palette')!==null; })()"), true,
      "a deferred usage close may not steal focus from the command palette");
    await evaluate("document.querySelector('[aria-label=\"Close command palette\"]').click()");
    assert.equal(await wait("!document.querySelector('.command-palette')"), true);

    await evaluate(`document.querySelector('${trigger}').click()`);
    assert.equal(await wait(`document.querySelector('${modal}')?.open`), true);
    await evaluate(`window.oldUsageTrigger=document.querySelector('${trigger}'); window.usageOriginalFrame=requestAnimationFrame; window.usageCloseFrames=[];
      window.requestAnimationFrame=callback=>{window.usageCloseFrames.push(callback); return 123456;};
      document.querySelector('${modal} button[aria-label="Close usage inspector"]').click()`);
    assert.equal(await wait(`!document.querySelector('${modal}')`), true);
    await evaluate(`window.requestAnimationFrame=window.usageOriginalFrame; document.querySelector('.session-row:nth-child(2) > button:first-child').click()`);
    assert.equal(await wait("document.querySelector('.session-header h1')?.textContent==='two' && !window.oldUsageTrigger.isConnected"), true);
    assert.equal(await evaluate(`(() => { const editor=document.querySelector('#session-prompt'); editor.focus();
      for(const callback of window.usageCloseFrames) callback(performance.now());
      return document.activeElement===editor; })()`), true,
      "a deferred usage close may not steal focus from a replacement session composer");
  } finally {
    socket?.close(); browser?.kill();
    await rm(root, { recursive: true, force: true, maxRetries: 8, retryDelay: 100 });
  }
});
