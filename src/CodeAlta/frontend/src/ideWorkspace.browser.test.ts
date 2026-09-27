import assert from "node:assert/strict";
import test from "node:test";
import { spawn } from "node:child_process";
import { existsSync } from "node:fs";
import { mkdtemp, readFile, writeFile, mkdir } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";
import { build } from "esbuild";

const edge = ["C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe", "C:/Program Files/Microsoft/Edge/Application/msedge.exe"].find(existsSync);
test("actual IDE workspace geometry and width changes retain the mounted composer and calls", { skip: !edge, timeout: 180_000 }, async t => {
  const root = await mkdtemp(join(tmpdir(), "codealta-ide-shell-"));
  const evidence = resolve("../../../tmp/ide-ux-20260927"); await mkdir(evidence, { recursive: true });
  const stamp = Date.now();
  let browser: ReturnType<typeof spawn> | undefined; let socket: WebSocket | undefined;
  let failureEvidence: (() => Promise<unknown>) | undefined;
  try {
    await build({ entryPoints: [fileURLToPath(new URL("./main.tsx", import.meta.url))], outfile: join(root, "fixture.js"),
      bundle: true, platform: "browser", format: "iife", loader: { ".css": "empty" }, define: { "import.meta.env.VITE_DEMO_MODE": '"false"' },
      plugins: [{ name: "isolated-ide", setup(bundle) {
        bundle.onResolve({ filter: /^#neoastra$/ }, () => ({ path: fileURLToPath(new URL("./settingsShell.neoastra.mount.ts", import.meta.url)) }));
        bundle.onLoad({ filter: /[/\\]timelineScroll\.ts$/ }, async args => ({ loader: "ts", contents: (await readFile(args.path, "utf8"))
          .replace('following: () => following,', 'following: () => following, diagnostic: () => ({following,restoring,pendingFinish,suppressedTop,pausedTop}),')
          .replace('return { elementRef, following, settled,', `Object.assign(window,{ideScrollProbe:()=>({mode:selection.diagnostic(),intent:scrollIntent.current,
            layoutUntil:layoutUntil.current,now:performance.now(),messageAnchor:messageAnchor.current?{top:messageAnchor.current.top,connected:messageAnchor.current.row.isConnected}:null,
            prependPending:!!prependMetrics.current})}); return { elementRef, following, settled,`) }));
        bundle.onLoad({ filter: /[/\\]main\.tsx$/ }, async args => ({ loader: "tsx", contents: (await readFile(args.path, "utf8"))
          .replace('const language = useLanguagePreference();', 'const language = useLanguagePreference(); Object.assign(window, { workflowLanguage: language.setLanguage });') }));
      } }] });
    await writeFile(join(root, "style.css"), await readFile("node_modules/flexlayout-react/style/light.css", "utf8") + await readFile("src/style.css", "utf8"));
    const page = join(root, "fixture.html");
    await writeFile(page, '<!doctype html><html><head><link rel="stylesheet" href="style.css"></head><body><div id="root"></div><script src="fixture.js"></script></body></html>');
    const profile = join(root, "profile");
    browser = spawn(edge!, ["--headless=new", "--disable-gpu", "--no-first-run", "--disable-background-networking", "--disable-extensions", `--user-data-dir=${profile}`, "--remote-debugging-port=0", "about:blank"], { stdio: "ignore", windowsHide: true });
    let port = "";
    for (let i = 0; i < 100 && !port; i++) { try { port = (await readFile(join(profile, "DevToolsActivePort"), "utf8")).split(/\r?\n/)[0]; } catch { await new Promise(r => setTimeout(r, 50)); } }
    assert.match(port, /^\d+$/);
    const pages = await (await fetch(`http://127.0.0.1:${port}/json/list`)).json() as { type?: string; url?: string; webSocketDebuggerUrl?: string }[];
    const tab = pages.find(value => value.type === "page" && value.url === "about:blank");
    assert.ok(tab?.webSocketDebuggerUrl);
    socket = new WebSocket(tab.webSocketDebuggerUrl);
    await new Promise<void>((done, fail) => { socket!.addEventListener("open", () => done(), { once: true }); socket!.addEventListener("error", fail, { once: true }); });
    const runtimeEvidence: unknown[] = [];
    socket.addEventListener("message", event => {
      const value = JSON.parse(String(event.data));
      if (["Runtime.exceptionThrown", "Runtime.consoleAPICalled", "Log.entryAdded"].includes(value.method) && runtimeEvidence.length < 20)
        runtimeEvidence.push(JSON.stringify(value).slice(0, 6000));
    });
    let sequence = 0;
    const command = (method: string, params: object = {}) => new Promise<Record<string, any>>((done, fail) => {
      const id = ++sequence; const timer = setTimeout(() => fail(new Error(method + " timeout")), 12000);
      const reply = (event: MessageEvent) => { const value = JSON.parse(String(event.data)); if (value.id !== id) return;
        socket!.removeEventListener("message", reply); clearTimeout(timer); value.error ? fail(new Error(JSON.stringify(value.error))) : done(value.result); };
      socket!.addEventListener("message", reply); socket!.send(JSON.stringify({ id, method, params }));
    });
    const evaluate = async (expression: string) => { const value = await command("Runtime.evaluate", { expression, returnByValue: true, awaitPromise: true }); assert.equal(value.exceptionDetails, undefined); return value.result?.value; };
    const wait = (expression: string) => evaluate(`new Promise(resolve=>{const end=Date.now()+7000;const tick=()=>{if(${expression})resolve(true);else if(Date.now()>end)resolve(false);else setTimeout(tick,20)};tick()})`);
    const frames = () => evaluate("new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(()=>resolve(true))))");
    failureEvidence = async () => ({ runtimeEvidence, state: await evaluate(`(()=>{
      const measure=n=>{const s=getComputedStyle(n);return {tag:n.tagName,className:n.className,rect:n.getBoundingClientRect().toJSON(),
        client:[n.clientWidth,n.clientHeight],scroll:[n.scrollWidth,n.scrollHeight,n.scrollTop],hidden:n.hidden,
        style:Object.fromEntries(['display','position','width','height','minWidth','minHeight','maxHeight','overflow','gridTemplateColumns','gridTemplateRows'].map(k=>[k,s[k]]))}};
      return {url:location.href,ready:document.readyState,viewport:[innerWidth,innerHeight],
        nodes:[...document.querySelectorAll('.workspace-shell,.project-rail,.session-rail,.content,.session-workspace,.timeline-scroll,.composer-region,.owned-session,.owned-session > *')].slice(0,45).map(measure),
        body:document.body.innerText.slice(0,10000),fixture:typeof settingsShellFixture,scrollEvidence:window.scrollEvidence,
        calls:typeof settingsShellFixture==='undefined'?null:settingsShellFixture.rpcCalls.slice(0,80)};
    })()`) });
    await command("Page.enable"); await command("Runtime.enable"); await command("Log.enable");
    await command("Page.navigate", { url: pathToFileURL(page).href });
    try {
      assert.equal(await wait("!!document.querySelector('#catalog-prompt')"), true);
    } catch (error) {
      const state = await evaluate(`({url:location.href,ready:document.readyState,viewport:[innerWidth,innerHeight],
        root:document.querySelector('#root')?.innerHTML.slice(0,12000),body:document.body.innerText.slice(0,8000),
        fixture:typeof settingsShellFixture, calls:typeof settingsShellFixture==='undefined'?null:settingsShellFixture.rpcCalls.slice(0,60)})`);
      const diagnostic = { runtimeEvidence, state };
      t.diagnostic(JSON.stringify(diagnostic));
      await writeFile(join(evidence, 'setup-diagnostic-'+stamp+'.json'), JSON.stringify(diagnostic, null, 2));
      throw error;
    }
    await evaluate("localStorage.clear();localStorage.setItem('settingsFixtureOwned','true');localStorage.setItem('retainedQueueFixture','true');localStorage.setItem('navigationFixture','tabs');localStorage.setItem('ideFixtureLongLabels','true')");
    await command("Page.reload"); assert.equal(await wait("typeof settingsShellFixture !== 'undefined' && settingsShellFixture.currentReads.length>0 && !!document.querySelector('#session-prompt')"), true);
    await evaluate("settingsShellFixture.releaseCurrent()"); await frames();
    await evaluate(`(()=>{const input=document.querySelector('#session-prompt');
      Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype,'value').set.call(input,'Literal draft 日本語 — keep me');
      input.dispatchEvent(new Event('input',{bubbles:true}));input.setSelectionRange(3,9);})()`); await frames();
    for (const [width, height] of [[1440, 900], [1120, 750], [750, 485], [390, 500]]) for (const theme of ["light", "dark"]) for (const locale of ["en", "de", "ja"]) {
      await command("Emulation.setDeviceMetricsOverride", { width, height, deviceScaleFactor: 1, mobile: false });
      await evaluate(`workflowLanguage('${locale}');document.documentElement.dataset.theme='${theme}'`); await frames();
      const geometry = await evaluate(`(()=>{const rect=s=>document.querySelector(s).getBoundingClientRect().toJSON();return {width:innerWidth,height:innerHeight,scroll:document.documentElement.scrollWidth,workspace:rect('.session-workspace'),timeline:rect('.timeline-scroll'),composer:rect('.composer-region'),content:rect('.content'),projects:rect('.project-rail'),sessions:rect('.session-rail')}})()`);
      const screenshot = await command("Page.captureScreenshot", { format: "png" });
      await writeFile(join(evidence, `visual-${stamp}-${width}-${height}-${theme}-${locale}.png`), Buffer.from(screenshot.data, "base64"));
      t.diagnostic(JSON.stringify({ theme, locale, ...geometry }));
      assert.ok(geometry.scroll <= width, "no page horizontal scroll");
      if (width === 1120) { assert.ok(geometry.composer.height <= 132, "idle composer <=132px"); assert.ok(geometry.timeline.height >= geometry.workspace.height * .55, "timeline >=55% workspace"); }
      assert.ok(geometry.timeline.height > 0, "timeline remains reachable");
      assert.ok(geometry.composer.bottom <= height, `composer bottom ${geometry.composer.bottom} <= viewport ${height}`);
      await evaluate(`window.matrixKept={input:document.querySelector('#session-prompt'),timeline:document.querySelector('.timeline-scroll'),
        draft:document.querySelector('#session-prompt').value,selection:[document.querySelector('#session-prompt').selectionStart,document.querySelector('#session-prompt').selectionEnd],
        calls:JSON.stringify(settingsShellFixture.rpcCalls),streams:JSON.stringify(settingsShellFixture.displayEvidence())};
        document.querySelector('.timeline-width-toggle').click()`); await frames();
      const expanded = await evaluate(`({timeline:document.querySelector('.timeline-scroll').getBoundingClientRect().toJSON(),
        content:document.querySelector('.content').getBoundingClientRect().toJSON(),scroll:document.documentElement.scrollWidth})`);
      assert.equal(expanded.timeline.width, expanded.content.width, `full bounds ${width}/${theme}/${locale}: ${JSON.stringify(expanded)}`);
      assert.ok(expanded.scroll <= width, `full width page overflow: ${JSON.stringify(expanded)}`);
      await evaluate("document.querySelector('.timeline-width-toggle').click()"); await frames();
      const retained = await evaluate(`({input:matrixKept.input===document.querySelector('#session-prompt'),timeline:matrixKept.timeline===document.querySelector('.timeline-scroll'),
        draft:matrixKept.draft===document.querySelector('#session-prompt').value,selection:JSON.stringify(matrixKept.selection)===JSON.stringify([document.querySelector('#session-prompt').selectionStart,document.querySelector('#session-prompt').selectionEnd]),
        calls:matrixKept.calls===JSON.stringify(settingsShellFixture.rpcCalls),streams:matrixKept.streams===JSON.stringify(settingsShellFixture.displayEvidence()),
        restoredWidth:document.querySelector('.timeline-scroll').getBoundingClientRect().width})`);
      assert.deepEqual(retained, {input:true,timeline:true,draft:true,selection:true,calls:true,streams:true,restoredWidth:geometry.timeline.width}, `restore lifetime ${width}/${theme}/${locale}: ${JSON.stringify(retained)}`);
      if (width < 875) {
        await evaluate("document.querySelector('.activity-rail button').click()"); await frames();
        const explorer = await evaluate(`(()=>{const rail=document.querySelector('.session-rail'),slot=document.querySelector('.session-content-rail-slot');
          return {rail:rail.getBoundingClientRect().toJSON(),slot:slot.getBoundingClientRect().toJSON(),page:document.documentElement.scrollWidth,scrollHeight:document.documentElement.scrollHeight}})()`);
        assert.ok(explorer.rail.bottom<=height && explorer.rail.height>0 && explorer.rail.width===320, `open short Explorer ${width}/${height}: ${JSON.stringify(explorer)}`);
        assert.ok(explorer.page<=width && explorer.scrollHeight<=height, `Explorer page containment: ${JSON.stringify(explorer)}`);
        await evaluate("document.querySelector('.project-sort-controls > summary').focus()");
        await command("Input.dispatchKeyEvent", {type:"keyDown",key:"Enter",text:"\r"});
        await command("Input.dispatchKeyEvent", {type:"keyUp",key:"Enter"}); await frames();
        assert.equal(await evaluate("document.querySelector('.project-sort-controls').open"), true, "project overflow opens with keyboard");
        const actionBounds = await evaluate(`(()=>{const action=document.querySelector('.project-sort-controls button:last-child');action.scrollIntoView({block:'nearest'});
          const rect=action.getBoundingClientRect(),rail=document.querySelector('.project-rail').getBoundingClientRect();return {top:rect.top,bottom:rect.bottom,railTop:rail.top,railBottom:rail.bottom,disabled:action.disabled}})()`);
        assert.ok(actionBounds.top>=actionBounds.railTop && actionBounds.bottom<=actionBounds.railBottom, `project action scroll reachable: ${JSON.stringify(actionBounds)}`);
        const openShot = await command("Page.captureScreenshot", {format:"png"});
        await writeFile(join(evidence, `explorer-${stamp}-${width}-${height}-${theme}-${locale}.png`),Buffer.from(openShot.data,"base64"));
        await evaluate("document.querySelector('.project-sort-controls > summary').click();document.querySelector('.activity-rail button').click()"); await frames();
      }
    }
    await evaluate(`window.restoreEvidence=[];window.captureRestore=(stage)=>{const shell=document.querySelector('.workspace-shell');
      const timeline=document.querySelector('.timeline-scroll');restoreEvidence.push({stage,viewport:[innerWidth,innerHeight],
        savedWidth:window.ideKept?.width,currentWidth:timeline.getBoundingClientRect().width,sameTimeline:window.ideKept?.timeline===timeline,
        shell:shell.className,columns:getComputedStyle(shell).gridTemplateColumns,railHidden:document.querySelector('#project-rail').hidden,
        full:document.querySelector('.timeline-width-toggle').getAttribute('aria-pressed')})}`);
    await command("Emulation.setDeviceMetricsOverride", { width: 1120, height: 750, deviceScaleFactor: 1, mobile: false });
    await evaluate("workflowLanguage('en')"); await frames();
    await evaluate("window.ideKept={input:document.querySelector('#session-prompt'),calls:JSON.stringify(settingsShellFixture.rpcCalls),timeline:document.querySelector('.timeline-scroll'),width:document.querySelector('.timeline-scroll').getBoundingClientRect().width};captureRestore('capture');captureRestore('before-click');document.querySelector('.timeline-width-toggle').click()"); await frames();
    assert.equal(await evaluate("ideKept.input===document.querySelector('#session-prompt') && ideKept.calls===JSON.stringify(settingsShellFixture.rpcCalls) && document.querySelector('.session-content-rail-slot').hidden && document.querySelector('#project-rail').hidden"), true);
    const fullBounds = await evaluate("({timeline:document.querySelector('.timeline-scroll').getBoundingClientRect().toJSON(),content:document.querySelector('.content').getBoundingClientRect().toJSON()})");
    assert.equal(fullBounds.timeline.width, fullBounds.content.width, `full content bounds: ${JSON.stringify(fullBounds)}`);
    await evaluate("document.querySelector('.timeline-width-toggle').click()"); await frames();
    assert.equal(await evaluate("ideKept.input===document.querySelector('#session-prompt') && ideKept.calls===JSON.stringify(settingsShellFixture.rpcCalls)"), true);
    await evaluate("captureRestore('after-restore')");
    const restoreEvidence = await evaluate("restoreEvidence");
    t.diagnostic(JSON.stringify({ restoreEvidence }));
    await writeFile(join(evidence, `restore-diagnostic-${stamp}.json`), JSON.stringify(restoreEvidence, null, 2));
    assert.equal(await evaluate("ideKept.timeline===document.querySelector('.timeline-scroll') && ideKept.width===document.querySelector('.timeline-scroll').getBoundingClientRect().width"), true, "timeline identity and width restored");
    await evaluate(`window.scrollEvidence=[];window.captureScroll=(stage)=>{const el=document.querySelector('.timeline-scroll');const bounds=el.getBoundingClientRect();
      const row=[...el.querySelectorAll('[data-message-key],.timeline-message')].find(n=>n.getBoundingClientRect().bottom>bounds.top);
      scrollEvidence.push({stage,probe:window.ideScrollProbe?.(),scrollTop:el.scrollTop,scrollHeight:el.scrollHeight,clientHeight:el.clientHeight,
        anchor:row?{text:row.textContent.slice(0,80),offset:row.getBoundingClientRect().top-bounds.top}:null})};captureScroll('before-assignment')`);
    await evaluate("document.querySelector('.session-splitter').focus()");
    await command("Input.dispatchKeyEvent", {type:"keyDown",key:"F3",code:"F3",modifiers:2});
    await command("Input.dispatchKeyEvent", {type:"keyUp",key:"F3",code:"F3",modifiers:2}); await frames();
    assert.equal(await evaluate("ideScrollProbe().mode.following"), false, "Ctrl+F3 deliberately pauses at first retained message before scripted reading offset");
    await evaluate(`window.resizeKept={input:document.querySelector('#session-prompt'),timeline:document.querySelector('.timeline-scroll'),
      calls:JSON.stringify(settingsShellFixture.rpcCalls),streams:JSON.stringify(settingsShellFixture.displayEvidence())};
      document.querySelector('.timeline-scroll').scrollTop=300;document.querySelector('.session-splitter').focus()`); await frames();
    await evaluate("captureScroll('before-first-resize')");
    assert.deepEqual(await evaluate("({following:ideScrollProbe().mode.following,restoring:ideScrollProbe().mode.restoring,top:document.querySelector('.timeline-scroll').scrollTop})"), {following:false,restoring:false,top:300}, "paused reading is established before any resize");
    const key = async (key: string) => { await command("Input.dispatchKeyEvent", {type:"keyDown",key}); await command("Input.dispatchKeyEvent", {type:"keyUp",key}); await frames(); await evaluate(`captureScroll(${JSON.stringify(key)})`); };
    await key("ArrowRight");
    assert.equal(await evaluate("Number(document.querySelector('.session-splitter').getAttribute('aria-valuenow'))"), 288, "keyboard grows Explorer by 16px");
    const handle = await evaluate("document.querySelector('.session-splitter').getBoundingClientRect().toJSON()");
    await command("Input.dispatchMouseEvent", {type:"mousePressed",x:handle.x+4,y:handle.y+80,button:"left",clickCount:1});
    await command("Input.dispatchMouseEvent", {type:"mouseMoved",x:handle.x+600,y:handle.y+80,button:"left",buttons:1});
    await command("Input.dispatchMouseEvent", {type:"mouseReleased",x:handle.x+600,y:handle.y+80,button:"left",clickCount:1}); await frames();
    await evaluate("captureScroll('after-pointer-drag')");
    assert.equal(await evaluate("Number(document.querySelector('.session-splitter').getAttribute('aria-valuenow'))"), 360, "pointer drag clamps maximum");
    for(let index=0;index<10;index++) await key("ArrowLeft");
    assert.equal(await evaluate("Number(document.querySelector('.session-splitter').getAttribute('aria-valuenow'))"), 220, "keyboard clamps minimum");
    await key("Home");
    assert.deepEqual(await evaluate("JSON.parse(localStorage.getItem('codealta.desktop.ide-width.v1'))"), {width:272,full:false}, "reset persists original width");
    const resizeRetained = await evaluate(`({input:resizeKept.input===document.querySelector('#session-prompt'),timeline:resizeKept.timeline===document.querySelector('.timeline-scroll'),
      calls:resizeKept.calls===JSON.stringify(settingsShellFixture.rpcCalls),streams:resizeKept.streams===JSON.stringify(settingsShellFixture.displayEvidence()),
      scroll:document.querySelector('.timeline-scroll').scrollTop,draft:document.querySelector('#session-prompt').value})`);
    assert.deepEqual(resizeRetained, {input:true,timeline:true,calls:true,streams:true,scroll:300,draft:'Literal draft 日本語 — keep me'}, `resize lifetime: ${JSON.stringify(resizeRetained)}`);
    await evaluate("window.savedStorageSet=Storage.prototype.setItem;Storage.prototype.setItem=function(key,value){if(key==='codealta.desktop.ide-width.v1')throw new Error('fixture storage unavailable');return savedStorageSet.call(this,key,value)}");
    await key("ArrowRight");
    assert.equal(await evaluate("document.body.innerText.includes('Width preference could not be saved; current layout stays available.') && document.querySelector('.session-splitter').getAttribute('aria-valuenow')==='288'"), true, "storage failure preserves usable local layout and notice");
    await evaluate("Storage.prototype.setItem=savedStorageSet"); await key("Home");
    assert.equal(await evaluate("!document.body.innerText.includes('Width preference could not be saved; current layout stays available.') && JSON.parse(localStorage.getItem('codealta.desktop.ide-width.v1')).width===272"), true, "next explicit resize recovers preference persistence");
    await evaluate("document.querySelector('.timeline-bottom-button').click();document.querySelector('.session-splitter').focus()"); await frames();
    for (const action of ["ArrowRight", "ArrowLeft", "Home"]) {
      await key(action);
      const follow = await evaluate("({following:ideScrollProbe().mode.following,top:document.querySelector('.timeline-scroll').scrollTop,bottom:document.querySelector('.timeline-scroll').scrollHeight-document.querySelector('.timeline-scroll').clientHeight})");
      assert.equal(follow.following, true, `following mode after ${action}: ${JSON.stringify(follow)}`);
      assert.equal(follow.top, follow.bottom, `following bottom after ${action}: ${JSON.stringify(follow)}`);
    }
  } catch (error) {
    if (failureEvidence) {
      try { const diagnostic = await failureEvidence(); t.diagnostic(JSON.stringify(diagnostic));
        await writeFile(join(evidence, 'failure-diagnostic-'+stamp+'.json'), JSON.stringify(diagnostic, null, 2)); }
      catch (diagnosticError) { t.diagnostic(`Failure diagnostics unavailable: ${String(diagnosticError)}`); }
    }
    throw error;
  } finally { socket?.close(); browser?.kill(); }
});
