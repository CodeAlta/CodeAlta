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

test("production composer info icon retains read-only scope and guarded focus", { skip: !edge, timeout: 90_000 }, async () => {
  const root = await mkdtemp(join(tmpdir(), "codealta-composer-info-"));
  let browser: ReturnType<typeof spawn> | undefined;
  let socket: WebSocket | undefined;
  try {
    await build({ entryPoints: [fileURLToPath(new URL("./main.tsx", import.meta.url))], outfile: join(root, "fixture.js"),
      bundle: true, platform: "browser", format: "iife", loader: { ".css": "empty" },
      define: { "import.meta.env.VITE_DEMO_MODE": '"false"' }, plugins: [{ name: "isolated-bridge", setup(bundle) {
        bundle.onResolve({ filter: /^#neoastra$/ }, () => ({ path: fileURLToPath(new URL("./settingsShell.neoastra.mount.ts", import.meta.url)) }));
        // Expose only the actual catalog publisher for a same-key replacement; dialog/selection
        // remain production App state, without adding a production refresh or fetch path.
        bundle.onLoad({ filter: /[/\\]main\.tsx$/ }, async args => ({ loader: "tsx", contents:
          (await readFile(args.path, "utf8")).replace('const [projectId, writeProjectId]',
            'Object.assign(window, { publishInfoFixtureSnapshot: (snapshot: WorkspaceSnapshot) => publishWorkspaceState({ kind: "ready", snapshot }) }); const [projectId, writeProjectId]') }));
      } }] });
    await writeFile(join(root, "style.css"), readFileSync(fileURLToPath(new URL("./style.css", import.meta.url))));
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
    await command("Page.navigate", { url: pathToFileURL(page).href });
    const evaluate = async (expression: string) => (await command("Runtime.evaluate", { expression, returnByValue: true, awaitPromise: true })).result?.value;
    const wait = (condition: string) => evaluate(`new Promise(resolve=>{const end=Date.now()+7000;const tick=()=>{
      if(${condition}) resolve(true); else if(Date.now()>end) resolve(document.body.innerText.slice(-1000)); else setTimeout(tick,20);};tick();})`);
    const trigger = '.session-info-trigger';
    const modal = '.session-info-dialog';
    const key = async (value: string, code: string, windowsVirtualKeyCode: number, modifiers = 0) => {
      await command("Input.dispatchKeyEvent", { type: "keyDown", key: value, code, windowsVirtualKeyCode, modifiers });
      await command("Input.dispatchKeyEvent", { type: "keyUp", key: value, code, windowsVirtualKeyCode, modifiers });
    };
    const chord = async () => { await key("g", "KeyG", 71, 2); await key("t", "KeyT", 84, 2); };
    const close = async () => {
      await evaluate(`document.querySelector('${modal} [aria-label="Close session info"]').click()`);
      assert.equal(await wait(`!document.querySelector('${modal}')`), true);
    };
    assert.equal(await wait("!!document.querySelector('#catalog-prompt')"), true);
    const readCounts = () => evaluate(`JSON.stringify({snapshots:window.settingsShellFixture.snapshotCalls.length,
      history:window.settingsShellFixture.historyCalls.length, usage:window.settingsShellFixture.usageReads.length,
      probes:window.settingsShellFixture.probes.length, choices:window.settingsShellFixture.choiceReads.length})`);
    const initialReads = await readCounts();
    assert.equal(await evaluate(`!!document.querySelector('.catalog-composer .history-controls ${trigger}') && !document.querySelector('.session-header ${trigger}')`), true,
      "one catalog composer info icon replaces the header text control");
    assert.equal(await evaluate(`document.querySelectorAll('${trigger}').length===1 &&
      document.querySelector('${trigger}').getAttribute('aria-label')==='Session info' &&
      !!document.querySelector('${trigger} svg[aria-hidden="true"]')`), true);
    await evaluate(`window.catalogPrompt=document.querySelector('#catalog-prompt'); window.catalogPrompt.focus(); document.querySelector('${trigger}').click()`);
    assert.equal(await wait(`document.querySelector('${modal}')?.matches(':modal')`), true);
    assert.equal(await evaluate(`document.querySelector('${modal}').textContent.includes('Recorded creation time') &&
      [...document.querySelectorAll('${modal} time')].some(time=>time.textContent==='2026-01-02T03:04:05.1234567+14:00')`), true,
      "actual App displays the supplied recorded creation time without converting its offset");
    await evaluate(`window.copiedInfo=[]; Object.defineProperty(navigator,'clipboard',{configurable:true,value:{writeText:async text=>{window.copiedInfo.push(text)}}});
      document.querySelector('${modal} button:nth-last-child(2)').click()`);
    assert.equal(await wait(`document.querySelector('${modal}')?.textContent.includes('Session ID copied.')`), true);
    assert.deepEqual(await evaluate("window.copiedInfo"), ["one"], "Copy remains session ID only");
    assert.equal(await readCounts(), initialReads, "display and Copy perform no additional reads or provider probes");
    await evaluate(`window.keptInfoDialog=document.querySelector('${modal}');
      window.publishInfoFixtureSnapshot({...window.settingsShellFixture.catalog, sessions:window.settingsShellFixture.catalog.sessions.map(row=>({...row,createdAt:'2025-04-03T02:01:00-07:00'}))})`);
    assert.equal(await wait(`document.querySelector('${modal}')?.textContent.includes('2025-04-03T02:01:00-07:00')`), true);
    assert.equal(await evaluate(`window.keptInfoDialog===document.querySelector('${modal}')`), true, "same-key publication updates the mounted dialog");
    await evaluate(`window.publishInfoFixtureSnapshot({...window.settingsShellFixture.catalog, sessions:window.settingsShellFixture.catalog.sessions.map(({createdAt,...row})=>row)})`);
    assert.equal(await wait(`document.querySelector('${modal}')?.textContent.includes('Not recorded or unavailable') && !document.querySelector('${modal}')?.textContent.includes('2025-04-03')`), true);
    assert.equal(await readCounts(), initialReads, "same-key metadata publication adds no acquisition");
    assert.equal(await evaluate(`document.querySelector('${modal}').textContent.includes('Project: Project') &&
      document.querySelector('${modal}').textContent.includes('Saved catalog metadata, not live runtime status.') &&
      !document.querySelector('${modal}').textContent.includes('Current tokens') &&
      window.catalogPrompt===document.querySelector('#catalog-prompt') && window.settingsShellFixture.sends.length===0`), true);
    await evaluate("Object.defineProperty(navigator,'clipboard',{configurable:true,value:undefined}); document.querySelector('.session-info-dialog button:nth-last-child(2)').click()");
    assert.equal(await wait(`document.querySelector('${modal}')?.textContent.includes('Clipboard unavailable; nothing copied.')`), true);
    await evaluate("Object.defineProperty(navigator,'clipboard',{configurable:true,value:{writeText:()=>Promise.reject(new Error('private clipboard error'))}}); document.querySelector('.session-info-dialog button:nth-last-child(2)').click()");
    assert.equal(await wait(`document.querySelector('${modal}')?.textContent.includes('Could not copy session ID.')`), true);
    assert.equal(await evaluate(`!document.querySelector('${modal}').textContent.includes('private clipboard error')`), true);
    await evaluate(`document.querySelector('${modal} button').dispatchEvent(new KeyboardEvent('keydown',{key:'Escape',isComposing:true,bubbles:true,cancelable:true}))`);
    assert.equal(await evaluate(`document.querySelector('${modal}')?.open`), true);
    await chord();
    assert.equal(await evaluate(`document.querySelectorAll('${modal}').length===1`), true, "modal keyboard cannot re-open info");
    await key("Escape", "Escape", 27);
    assert.equal(await wait(`!document.querySelector('${modal}') && document.activeElement===document.querySelector('${trigger}')`), true);

    await evaluate(`document.querySelector('${trigger}').click()`);
    assert.equal(await wait(`document.querySelector('${modal}')?.open`), true);
    await evaluate(`window.originalFrame=requestAnimationFrame; window.oldInfoFrames=[];
      window.requestAnimationFrame=callback=>{window.oldInfoFrames.push(callback); return 123456;};
      document.querySelector('${modal} [aria-label="Close session info"]').click()`);
    assert.equal(await wait(`!document.querySelector('${modal}')`), true);
    assert.equal(await evaluate(`(() => { window.requestAnimationFrame=window.originalFrame;
      window.catalogPrompt.focus(); for(const callback of window.oldInfoFrames) callback(performance.now());
      return document.activeElement===window.catalogPrompt; })()`), true, "deferred info close cannot steal newer composer focus");
    await evaluate(`document.querySelector('${trigger}').click()`);
    assert.equal(await wait(`document.querySelector('${modal}')?.open`), true);
    await evaluate(`window.originalFrame=requestAnimationFrame; window.oldInfoFrames=[];
      window.requestAnimationFrame=callback=>{window.oldInfoFrames.push(callback); return 123456;};
      document.querySelector('${modal} [aria-label="Close session info"]').click()`);
    assert.equal(await wait(`!document.querySelector('${modal}')`), true);
    await evaluate(`window.requestAnimationFrame=window.originalFrame; document.querySelector('${trigger}').click()`);
    assert.equal(await wait(`document.querySelector('${modal}')?.open`), true);
    assert.equal(await evaluate(`(() => { for(const callback of window.oldInfoFrames) callback(performance.now());
      return document.activeElement?.closest('${modal}')!==null; })()`), true, "reopen cancels old close focus");
    await close();

    await evaluate("document.querySelector('#catalog-prompt').focus()");
    await chord();
    assert.equal(await wait(`document.querySelector('${modal}')?.open`), true, "catalog-only prompt chord retains read-only info access");
    await close();
    await evaluate(`document.querySelector('#catalog-prompt').focus();
      [...document.querySelectorAll('button')].find(button=>button.textContent.includes('Commands') && button.textContent.includes('Ctrl+P')).click()`);
    assert.equal(await wait("document.querySelector('.command-palette')?.open && !!document.querySelector('#palette-option-sessionInfo')"), true);
    await evaluate("document.querySelector('#palette-option-sessionInfo').click()");
    assert.equal(await wait(`document.querySelector('${modal}')?.open && !document.querySelector('.command-palette')`), true,
      "palette selection preserves catalog-only session info availability");
    await close();
    for (const width of [390, 1120]) for (const theme of ["dark", "light"]) {
      await command("Emulation.setDeviceMetricsOverride", { width, height: 800, deviceScaleFactor: 1, mobile: false });
      await evaluate(`document.documentElement.dataset.theme='${theme}'`);
      assert.equal(await evaluate(`(() => {
        const button=document.querySelector('.catalog-composer .history-controls ${trigger}');
        const area=button.closest('.history-controls').getBoundingClientRect();
        const rect=button.getBoundingClientRect();
        return rect.width>=28 && rect.left>=0 && rect.right<=innerWidth+1 && area.right<=innerWidth+1 &&
          getComputedStyle(button).display!=='none'; })()`), true,
        `${width}px ${theme} catalog info and Send-unavailable controls fit the viewport`);
    }
    await command("Emulation.setDeviceMetricsOverride", { width: 1120, height: 800, deviceScaleFactor: 1, mobile: false });

    await evaluate(`document.querySelector('${trigger}').click()`);
    assert.equal(await wait(`document.querySelector('${modal}')?.open`), true);
    await evaluate("document.querySelector('.session-row:nth-child(2) > button:first-child').click()");
    assert.equal(await wait(`document.querySelector('.session-header h1')?.textContent==='two' && !document.querySelector('${modal}')`), true,
      "a replacement selection tears down the original keyed info dialog");
    await evaluate("document.querySelector('.session-row:nth-child(1) > button:first-child').click()");
    assert.equal(await wait("document.querySelector('.session-header h1')?.textContent==='one'"), true);
    await evaluate(`window.oldInfoTrigger=document.querySelector('${trigger}'); document.querySelector('${trigger}').click()`);
    assert.equal(await wait(`document.querySelector('${modal}')?.open`), true);
    await evaluate(`window.originalFrame=requestAnimationFrame; window.oldInfoFrames=[];
      window.requestAnimationFrame=callback=>{window.oldInfoFrames.push(callback); return 123456;};
      document.querySelector('${modal} [aria-label="Close session info"]').click()`);
    assert.equal(await wait(`!document.querySelector('${modal}')`), true);
    await evaluate("window.requestAnimationFrame=window.originalFrame; document.querySelector('.session-row:nth-child(2) > button:first-child').click()");
    assert.equal(await wait("document.querySelector('.session-header h1')?.textContent==='two' && !window.oldInfoTrigger.isConnected"), true);
    assert.equal(await evaluate(`(() => { const editor=document.querySelector('#catalog-prompt'); editor.focus();
      for(const callback of window.oldInfoFrames) callback(performance.now()); return document.activeElement===editor; })()`), true,
      "old info close cannot steal focus from the replacement session composer");

    await evaluate(`document.querySelector('${trigger}').click()`);
    assert.equal(await wait(`document.querySelector('${modal}')?.open`), true);
    await evaluate(`window.originalFrame=requestAnimationFrame; window.oldInfoFrames=[];
      window.requestAnimationFrame=callback=>{window.oldInfoFrames.push(callback); return 123456;};
      document.querySelector('${modal} [aria-label="Close session info"]').click()`);
    assert.equal(await wait(`!document.querySelector('${modal}')`), true);
    await evaluate("window.requestAnimationFrame=window.originalFrame; document.querySelector('.project-rail .icon-label-button').click()");
    assert.equal(await wait("document.querySelector('.settings-dialog')?.open"), true);
    assert.equal(await evaluate(`(() => { for(const callback of window.oldInfoFrames) callback(performance.now());
      return document.activeElement?.closest('.settings-dialog')!==null; })()`), true,
      "old info close cannot steal focus from newer Settings");
    await evaluate("document.querySelector('[aria-label=\"Close settings\"]').click()");
    assert.equal(await wait("!document.querySelector('.settings-dialog')"), true);

    await evaluate("localStorage.setItem('usageFixtureArchived','true')");
    await command("Page.reload");
    assert.equal(await wait("document.querySelector('#catalog-draft-status')?.textContent.includes('Archived') && !!document.querySelector('.catalog-composer .history-controls .session-info-trigger')"), true);
    await evaluate(`document.querySelector('${trigger}').click()`);
    assert.equal(await wait(`document.querySelector('${modal}')?.open && document.querySelector('${modal}')?.textContent.includes('Project: Project')`), true,
      "archived draft-only composer retains read-only snapshot info");
    assert.equal(await evaluate(`document.querySelector('${modal}').textContent.includes('2026-01-02T03:04:05.1234567+14:00')`), true);
    await close();
    await evaluate("document.querySelector('#catalog-prompt').focus()");
    await chord();
    assert.equal(await wait(`document.querySelector('${modal}')?.open`), true);
    await close();
    assert.equal(await evaluate("window.settingsShellFixture.sends.length===0"), true);

    await evaluate("localStorage.removeItem('usageFixtureArchived'); localStorage.setItem('infoFixtureAmbiguous','true')");
    await command("Page.reload");
    assert.equal(await wait(`!!document.querySelector('.catalog-composer ${trigger}')`), true);
    await evaluate(`document.querySelector('${trigger}').click()`);
    assert.equal(await wait(`document.querySelector('${modal}')?.textContent.includes('Unverified: ambiguous session identity')`), true);
    assert.equal(await evaluate(`document.querySelector('${modal} button:nth-last-child(2)').disabled &&
      !document.querySelector('${modal}').textContent.includes('/fixture/project')`), true, "ambiguous ID disables Copy and suppresses unverified fields");
    await close();
    await evaluate("document.querySelector('#catalog-prompt').focus()");
    await chord();
    assert.equal(await evaluate(`!document.querySelector('${modal}')`), true, "ambiguous selection cannot enter via chord");
    await evaluate("[...document.querySelectorAll('button')].find(button=>button.textContent.includes('Commands') && button.textContent.includes('Ctrl+P')).click()");
    assert.equal(await wait("document.querySelector('.command-palette')?.open"), true);
    assert.equal(await evaluate("!document.querySelector('#palette-option-sessionInfo')"), true);
    await evaluate("document.querySelector('[aria-label=\"Close command palette\"]').click()");
    assert.equal(await wait("!document.querySelector('.command-palette')"), true);

    await evaluate("localStorage.removeItem('infoFixtureAmbiguous'); localStorage.setItem('infoFixtureUnknown','true')");
    await command("Page.reload");
    assert.equal(await wait(`!!document.querySelector('.catalog-composer ${trigger}')`), true);
    await evaluate(`document.querySelector('${trigger}').click()`);
    assert.equal(await wait(`document.querySelector('${modal}')?.textContent.includes('Unverified / unmatched scope')`), true);
    assert.equal(await evaluate(`!document.querySelector('${modal} button:nth-last-child(2)').disabled`), true,
      "unknown scope stays disclosed without inventing missing identity or restricting a unique recorded ID");
    await close();

    await evaluate("localStorage.removeItem('infoFixtureUnknown'); localStorage.setItem('infoFixtureMismatched','true')");
    await command("Page.reload");
    assert.equal(await wait("!!document.querySelector('.project-root-list button')"), true);
    await evaluate("document.querySelector('.project-root-list button').click()");
    assert.equal(await wait(`!!document.querySelector('.catalog-composer ${trigger}') && document.querySelector('.session-header h1')?.textContent==='one'`), true);
    await evaluate(`document.querySelector('${trigger}').click()`);
    assert.equal(await wait(`document.querySelector('${modal}')?.textContent.includes('Unverified / unmatched scope')`), true,
      "wrong project identity is disclosed as unmatched, not inferred from a matching path");
    await close();

    await evaluate("localStorage.removeItem('infoFixtureMismatched'); localStorage.setItem('settingsFixtureOwned','true')");
    await command("Page.reload");
    assert.equal(await wait("!!document.querySelector('#session-prompt') && !!document.querySelector('.owned-session .history-controls .session-info-trigger')"), true);
    assert.equal(await evaluate(`document.querySelectorAll('${trigger}').length===1 && !document.querySelector('.session-header ${trigger}') &&
      !!document.querySelector('.owned-session .history-controls [aria-label="Inspect last-observed session usage"]')`), true,
      "owned composer keeps one info icon alongside other existing controls");
    await evaluate("document.querySelector('#session-prompt').focus(); window.keptPrompt=document.querySelector('#session-prompt'); void 0");
    await command("Input.insertText", { text: "Info draft retained" });
    assert.equal(await wait("document.querySelector('#session-prompt').value==='Info draft retained' && localStorage.getItem('codealta.desktop.prompt.one')==='Info draft retained'"), true);
    await evaluate(`document.querySelector('${trigger}').click()`);
    assert.equal(await wait(`document.querySelector('${modal}')?.open && document.querySelector('${modal}')?.textContent.includes('Project: Project')`), true);
    assert.equal(await evaluate(`document.querySelector('${modal}').textContent.includes('2026-01-02T03:04:05.1234567+14:00')`), true, "owned uses the same recorded value");
    await close();
    assert.equal(await wait(`document.activeElement===document.querySelector('${trigger}')`), true);
    assert.equal(await evaluate("window.keptPrompt===document.querySelector('#session-prompt') && document.querySelector('#session-prompt').value==='Info draft retained' && localStorage.getItem('codealta.desktop.prompt.one')==='Info draft retained' && window.settingsShellFixture.sends.length===0 && window.settingsShellFixture.usageReads.length===0"), true,
      "info is read-only: no draft loss, Send, or usage read");
    await evaluate("document.querySelector('#session-prompt').focus()");
    await chord();
    assert.equal(await wait(`document.querySelector('${modal}')?.open`), true, "owned prompt chord targets same icon");
    await close();

    for (const width of [390, 1120]) for (const theme of ["dark", "light"]) {
      await command("Emulation.setDeviceMetricsOverride", { width, height: 800, deviceScaleFactor: 1, mobile: false });
      await evaluate(`document.documentElement.dataset.theme='${theme}'`);
      assert.equal(await evaluate(`(() => {
        const button=document.querySelector('.owned-session .history-controls ${trigger}');
        const toolbar=button.closest('.history-controls');
        const rect=button.getBoundingClientRect(), area=toolbar.getBoundingClientRect();
        return rect.width>=28 && rect.left>=0 && rect.right<=innerWidth+1 && area.right<=innerWidth+1 &&
          getComputedStyle(button).display!=='none'; })()`), true,
        `${width}px ${theme} info icon and controls fit within the viewport`);
      await evaluate(`document.querySelector('${trigger}').click()`);
      assert.equal(await wait(`document.querySelector('${modal}')?.matches(':modal')`), true);
      await close();
    }
    assert.equal(await evaluate("document.querySelector('#session-prompt').value==='Info draft retained' && window.settingsShellFixture.sends.length===0"), true);
    await evaluate("document.querySelector('.owned-session .send-button').click()");
    assert.equal(await wait("window.settingsShellFixture.sends.length===1 && document.querySelector('.owned-session .send-button').disabled"), true,
      "fixture holds an explicitly initiated exact Send intent for the preservation check");
    await evaluate("window.retainedInfoIntent=JSON.stringify(window.settingsShellFixture.sends[0])");
    await evaluate(`document.querySelector('${trigger}').click()`);
    assert.equal(await wait(`document.querySelector('${modal}')?.open`), true);
    await close();
    assert.equal(await evaluate("window.settingsShellFixture.sends.length===1 && JSON.stringify(window.settingsShellFixture.sends[0])===window.retainedInfoIntent && document.querySelector('.owned-session .send-button').disabled && document.querySelector('#session-prompt').value==='Info draft retained'"), true,
      "info activation does not retry, replace, or clear an in-flight retained action/draft");
  } finally {
    socket?.close(); browser?.kill();
    await rm(root, { recursive: true, force: true, maxRetries: 8, retryDelay: 100 });
  }
});
