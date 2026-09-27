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
      window.canonicalInfoDetails=[...document.querySelectorAll('[data-info-copy]')].map(x=>x.innerText).join('\\n\\n');
      document.querySelector('${modal} footer button').click();void 0`);
    assert.equal(await wait("copiedInfo.length===1"), true);
    assert.equal(await evaluate("copiedInfo[0]===canonicalInfoDetails"), true, "canonical details preserve the original English DOM Copy payload exactly");
    await evaluate(`window.copiedInfo=[];
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
      document.querySelector('${modal}').textContent.includes('Saved metadata and separate point-in-time observations.') &&
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
      // FlexLayout positions its portal after ResizeObserver measurement, not synchronously
      // with the CDP viewport command. Require real final geometry before checking controls.
      assert.equal(await wait(`(() => {const panel=document.querySelector('.content').closest('.flexlayout__tab').getBoundingClientRect();
        const content=document.querySelector('.content').getBoundingClientRect();
        return panel.width>0 && panel.height>0 && panel.right<=innerWidth+1 && Math.abs(panel.width-content.width)<1 && Math.abs(panel.height-content.height)<1;})()`), true);
      assert.equal(await evaluate(`(() => {
        const button=document.querySelector('.catalog-composer .history-controls ${trigger}');
        const area=button.closest('.history-controls').getBoundingClientRect();
        const rect=button.getBoundingClientRect();
        return rect.width>=28 && rect.left>=0 && rect.right<=innerWidth+1 && area.right<=innerWidth+1 &&
          getComputedStyle(button).display!=='none'; })()`), true,
        `${width}px ${theme} catalog info and Send-unavailable controls fit the viewport: ${JSON.stringify(await evaluate(`({
          panel:document.querySelector('.content').closest('.flexlayout__tab').getBoundingClientRect().toJSON(),
          shell:document.querySelector('.workspace-shell').getBoundingClientRect().toJSON(),
          content:document.querySelector('.content').getBoundingClientRect().toJSON(),
          button:document.querySelector('.catalog-composer .history-controls ${trigger}').getBoundingClientRect().toJSON()})`))}`);
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
    assert.equal(await evaluate(`Array.from(document.querySelectorAll('${modal} button')).find(button=>button.textContent==='Refresh observed details').disabled && window.settingsShellFixture.runtimeReads.length===0`), true);
    await close();
    await evaluate("document.querySelector('#catalog-prompt').focus()");
    await chord();
    assert.equal(await wait(`document.querySelector('${modal}')?.open`), true);
    await close();
    assert.equal(await evaluate("window.settingsShellFixture.sends.length===0"), true);

    await evaluate("localStorage.removeItem('usageFixtureArchived'); localStorage.removeItem('codealta.desktop.sessionTabs.v1'); localStorage.setItem('infoFixtureAmbiguous','true')");
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

    await evaluate("localStorage.removeItem('infoFixtureAmbiguous'); localStorage.removeItem('codealta.desktop.sessionTabs.v1'); localStorage.setItem('infoFixtureUnknown','true')");
    await command("Page.reload");
    assert.equal(await wait(`!!document.querySelector('.catalog-composer ${trigger}')`), true);
    await evaluate(`document.querySelector('${trigger}').click()`);
    assert.equal(await wait(`document.querySelector('${modal}')?.textContent.includes('Unverified / unmatched scope')`), true);
    assert.equal(await evaluate(`!document.querySelector('${modal} button:nth-last-child(2)').disabled`), true,
      "unknown scope stays disclosed without inventing missing identity or restricting a unique recorded ID");
    await close();

    await evaluate("localStorage.removeItem('infoFixtureUnknown'); localStorage.removeItem('codealta.desktop.sessionTabs.v1'); localStorage.setItem('infoFixtureMismatched','true')");
    await command("Page.reload");
    assert.equal(await wait("!!document.querySelector('.project-root-list button')"), true);
    await evaluate("document.querySelector('.project-root-list button').click()");
    await evaluate("[...document.querySelectorAll('.session-row > button:first-child')].find(button=>button.textContent.includes('one'))?.click()");
    assert.equal(await wait(`!!document.querySelector('.catalog-composer ${trigger}') && document.querySelector('.session-header h1')?.textContent==='one'`), true);
    await evaluate(`document.querySelector('${trigger}').click()`);
    assert.equal(await wait(`document.querySelector('${modal}')?.textContent.includes('Unverified / unmatched scope')`), true,
      "wrong project identity is disclosed as unmatched, not inferred from a matching path");
    await close();

    await evaluate("localStorage.removeItem('infoFixtureMismatched'); localStorage.removeItem('codealta.desktop.sessionTabs.v1'); localStorage.setItem('settingsFixtureOwned','true')");
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
      assert.equal(await wait(`(() => {const panel=document.querySelector('.content').closest('.flexlayout__tab').getBoundingClientRect();
        const content=document.querySelector('.content').getBoundingClientRect();
        return panel.width>0 && panel.height>0 && panel.right<=innerWidth+1 && Math.abs(panel.width-content.width)<1 && Math.abs(panel.height-content.height)<1;})()`), true);
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
    // Real controls with a disposable context driver: settings cannot be interacted with
    // behind a native modal, so drive only this fixture's context to test pending lifetimes.
    await build({ entryPoints: [fileURLToPath(new URL("./inspectionLocalization.mount.tsx", import.meta.url))], outfile: join(root, "localized.js"),
      bundle: true, platform: "browser", format: "iife", plugins: [{ name: "isolated-bridge", setup(bundle) {
        bundle.onResolve({ filter: /^#neoastra$/ }, () => ({ path: fileURLToPath(new URL("./settingsShell.neoastra.mount.ts", import.meta.url)) }));
      } }] });
    await writeFile(join(root, "localized.html"), '<!doctype html><html><head><link rel="stylesheet" href="style.css"></head><body><div id="root"></div><script src="localized.js"></script></body></html>');
    await command("Page.navigate", { url: pathToFileURL(join(root, "localized.html")).href });
    assert.equal(await wait("document.querySelector('.session-info-dialog')?.open"), true);
    await evaluate("window.localeDialog=document.querySelector('.session-info-dialog');window.canonicalPayload=[...document.querySelectorAll('[data-info-copy]')].map(x=>x.innerText).join('\\n\\n');window.copies=[];Object.defineProperty(navigator,'clipboard',{configurable:true,value:{writeText:text=>{copies.push(text);return new Promise(resolve=>window.releaseLocaleCopy=resolve)}}});document.querySelector('.session-info-dialog footer button').click();document.querySelector('.session-info-dialog header button').focus();window.localeFocus=document.activeElement;void 0");
    for (const locale of locales) {
      await evaluate(`inspectionFixture.setLocale(${JSON.stringify(locale)})`);
      assert.equal(await wait(`document.querySelector('#session-info-title').textContent===${JSON.stringify(translate(locale, "Session info"))}`), true);
      assert.equal(await evaluate("localeDialog===document.querySelector('.session-info-dialog') && document.activeElement===localeFocus && copies.length===1 && copies[0]===canonicalPayload && settingsShellFixture.runtimeReads.length===0 && settingsShellFixture.usageReads.length===0 && inspectionFixture.canMutate()"), true, `${locale}: pending canonical Copy retains identity and permission without reads`);
      assert.equal(await evaluate("[...document.querySelectorAll('.session-info-fields dd')].some(x=>x.textContent==='Saved metadata') && document.querySelector('.session-info-fields code').textContent==='Unknown'"), true, "English-like title and ID stay literal");
      for (const theme of ["light", "dark"]) {
        await command("Emulation.setDeviceMetricsOverride", { width: 390, height: 500, deviceScaleFactor: 1, mobile: false });
        await evaluate(`document.documentElement.dataset.theme=${JSON.stringify(theme)}`);
        assert.equal(await evaluate("(()=>{const d=document.querySelector('.session-info-dialog');return d.getBoundingClientRect().width<=innerWidth && d.getBoundingClientRect().height<=innerHeight && d.scrollWidth<=d.clientWidth+1})()"), true, `${locale} ${theme}: Info fits short/narrow`);
      }
    }
    await evaluate("releaseLocaleCopy()");
    assert.equal(await wait(`document.querySelector('.session-info-dialog footer').textContent.includes(${JSON.stringify(translate("zh-CN", "Displayed details copied."))})`), true);
    await evaluate("document.querySelector('.session-info-dialog footer button').click()");
    assert.equal(await wait("copies.length===2 && copies[1]===canonicalPayload"), true, "a new Copy from translated presentation still uses the exact canonical payload");
    await evaluate("releaseLocaleCopy()");
    await evaluate("document.querySelector('.session-info-dialog > div > button').click();window.originalInfoRead=settingsShellFixture.runtimeReads[0]");
    assert.equal(await wait("settingsShellFixture.runtimeReads.length===1"), true);
    for (const locale of locales) {
      await evaluate(`inspectionFixture.setLocale(${JSON.stringify(locale)})`);
      assert.equal(await wait(`document.querySelector('.session-info-dialog [role=status]').textContent===${JSON.stringify(translate(locale, "Reading observed details…"))}`), true);
      assert.equal(await evaluate("settingsShellFixture.runtimeReads.length===1 && settingsShellFixture.runtimeReads[0]===originalInfoRead && settingsShellFixture.usageReads.length===0 && inspectionFixture.canMutate() && localeDialog===document.querySelector('.session-info-dialog')"), true);
    }
    await evaluate("originalInfoRead.reject(new Error('private error'))");
    assert.equal(await wait(`document.querySelector('.session-info-dialog').textContent.includes(${JSON.stringify(translate("zh-CN", "Error: read failed; no observation established."))})`), true);
    await evaluate("inspectionFixture.setView('usage')");
    assert.equal(await wait("!!document.querySelector('[aria-haspopup=dialog]') && !document.querySelector('.session-info-dialog')"), true);
    await evaluate("document.querySelector('[aria-haspopup=dialog]').click();void 0");
    assert.equal(await wait("document.querySelector('.session-usage-dialog')?.open && settingsShellFixture.usageReads.length===1"), true);
    await evaluate("window.localeUsage=document.querySelector('.session-usage-dialog');window.originalUsageRead=settingsShellFixture.usageReads[0]");
    for (const locale of locales) {
      await evaluate(`inspectionFixture.setLocale(${JSON.stringify(locale)})`);
      assert.equal(await wait(`document.querySelector('#session-usage-title').textContent===${JSON.stringify(translate(locale, "Last-observed usage"))}`), true);
      assert.equal(await evaluate("localeUsage===document.querySelector('.session-usage-dialog') && settingsShellFixture.usageReads.length===1 && settingsShellFixture.usageReads[0]===originalUsageRead && settingsShellFixture.runtimeReads.length===1 && inspectionFixture.canMutate()"), true);
      for (const theme of ["light", "dark"]) {
        await evaluate(`document.documentElement.dataset.theme=${JSON.stringify(theme)}`);
        assert.equal(await evaluate("(()=>{const d=document.querySelector('.session-usage-dialog');return d.getBoundingClientRect().width<=innerWidth && d.getBoundingClientRect().height<=innerHeight && d.scrollWidth<=d.clientWidth+1 && document.activeElement.closest('.session-usage-dialog')===d})()"), true, `${locale} ${theme}: pending usage focus and short/narrow bounds`);
      }
    }
    await evaluate("originalUsageRead.resolve({status:'no_observation',hostEpoch:'12345678-1234-1234-1234-123456789abc',sessionId:'Unknown',runtimeInstanceId:'12345678-1234-1234-1234-123456789abe',attachmentGeneration:'7',omittedUsageEvents:'3',observation:null})");
    assert.equal(await wait("!document.querySelector('.session-usage-dialog footer button').disabled"), true);
    for (const locale of locales) {
      await evaluate(`inspectionFixture.setLocale(${JSON.stringify(locale)})`);
      assert.equal(await wait(`document.querySelector('.session-usage-dialog').textContent.includes(${JSON.stringify(translate(locale, "Attachment {attachment}; no admitted usage event. Mismatched usage callbacks: {count}.", { attachment: "7", count: "3" }))})`), true);
      assert.equal(await evaluate("settingsShellFixture.usageReads.length===1 && inspectionFixture.canMutate()"), true);
    }
    await evaluate("document.querySelector('.session-usage-dialog footer button').click()");
    assert.equal(await wait("settingsShellFixture.usageReads.length===2"), true);
    await evaluate("settingsShellFixture.usageReads[1].reject(new Error('private usage error'))");
    for (const locale of locales) {
      await evaluate(`inspectionFixture.setLocale(${JSON.stringify(locale)})`);
      assert.equal(await wait(`document.querySelector('.session-usage-dialog [role=status]').textContent===${JSON.stringify(translate(locale, "Usage read failed; no observation established. Refresh explicitly if needed."))}`), true);
      assert.equal(await evaluate("settingsShellFixture.usageReads.length===2 && inspectionFixture.canMutate() && !document.body.textContent.includes('private usage error')"), true);
    }
    await command("Input.dispatchKeyEvent", { type: "keyDown", key: "Escape", code: "Escape", windowsVirtualKeyCode: 27 });
    await command("Input.dispatchKeyEvent", { type: "keyUp", key: "Escape", code: "Escape", windowsVirtualKeyCode: 27 });
    assert.equal(await wait("!document.querySelector('.session-usage-dialog') && document.activeElement===document.querySelector('[aria-haspopup=dialog]')"), true);
    await evaluate("inspectionFixture.setView('browser')");
    assert.equal(await wait("document.querySelector('.session-browser')?.open"), true);
    await evaluate("window.localeBrowser=document.querySelector('.session-browser');inspectionFixture.setStale(true);inspectionFixture.invalidate()");
    for (const locale of locales) {
      await evaluate(`inspectionFixture.setLocale(${JSON.stringify(locale)})`);
      assert.equal(await wait(`document.querySelector('#session-browser-title').textContent===${JSON.stringify(translate(locale, "Browse saved sessions"))}`), true);
      assert.equal(await evaluate("localeBrowser===document.querySelector('.session-browser') && document.activeElement===document.querySelector('.session-browser input') && document.querySelector('.session-browser-results strong').textContent==='Saved metadata' && document.querySelector('.session-browser-results [role=option]').disabled && settingsShellFixture.usageReads.length===2 && settingsShellFixture.runtimeReads.length===1 && !inspectionFixture.canMutate()"), true, `${locale}: stale browser retains literal title, disabled selection, revoked authority and no reads`);
      for (const theme of ["light", "dark"]) {
        await evaluate(`document.documentElement.dataset.theme=${JSON.stringify(theme)}`);
        assert.equal(await evaluate("(()=>{const d=document.querySelector('.session-browser');return d.getBoundingClientRect().width<=innerWidth && d.getBoundingClientRect().height<=innerHeight && d.scrollWidth<=d.clientWidth+1})()"), true, `${locale} ${theme}: browser short/narrow bounds`);
      }
    }
    await command("Input.dispatchKeyEvent", { type: "keyDown", key: "Escape", code: "Escape", windowsVirtualKeyCode: 27 });
    await command("Input.dispatchKeyEvent", { type: "keyUp", key: "Escape", code: "Escape", windowsVirtualKeyCode: 27 });
    assert.equal(await wait("!document.querySelector('.session-browser')"), true);
  } finally {
    socket?.close(); browser?.kill();
    await rm(root, { recursive: true, force: true, maxRetries: 8, retryDelay: 100 });
  }
});
