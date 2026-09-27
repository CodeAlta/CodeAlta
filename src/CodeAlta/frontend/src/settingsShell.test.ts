import assert from "node:assert/strict";
import { workflowLanguages, workflowNarrow } from "./workflowLocalizationChecks";
import { timelineGeometryProbe } from "./timelineGeometryProbe";
import { spawn } from "node:child_process";
import { existsSync, readFileSync } from "node:fs";
import { mkdtemp, readFile, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { fileURLToPath, pathToFileURL } from "node:url";
import { build } from "esbuild";
import { translate } from "./localization";

const edge = ["C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe",
  "C:/Program Files/Microsoft/Edge/Application/msedge.exe"].find(existsSync);

// The bundle entry is main.tsx, not a copied shell or reimplemented navigation fixture.
test("production shell settings overlay keeps the session workspace mounted and inert", { skip: !edge, timeout: 90_000 }, async t => {
  const root = await mkdtemp(join(tmpdir(), "codealta-session-content-shell-"));
  t.diagnostic(`Fake-host App evidence retained at ${root}`);
  const layoutObservations: unknown[] = [];
  let browser: ReturnType<typeof spawn> | undefined;
  let socket: WebSocket | undefined;
  try {
    await build({ entryPoints: [fileURLToPath(new URL("./main.tsx", import.meta.url))],
      outfile: join(root, "fixture.js"), bundle: true, platform: "browser", format: "iife", loader: { ".css": "empty" },
      define: { "import.meta.env.VITE_DEMO_MODE": '"false"' },
      plugins: [{ name: "isolated-bridge", setup(bundle) {
        bundle.onResolve({ filter: /^#neoastra$/ }, () => ({ path: fileURLToPath(new URL("./settingsShell.neoastra.mount.ts", import.meta.url)) }));
        // Expose teardown and existing publication boundaries, not replacement navigation/owners.
        bundle.onLoad({ filter: /[/\\]main\.tsx$/ }, async args => ({ loader: "tsx", contents:
          (await readFile(args.path, "utf8")).replace('createRoot(document.getElementById("root")!).render(',
            'const fixtureRoot = createRoot(document.getElementById("root")!); Object.assign(window, { unmountShellFixture: () => fixtureRoot.unmount() }); fixtureRoot.render(')
            .replace('const language = useLanguagePreference();', 'const language = useLanguagePreference(); Object.assign(window, { workflowLanguage: language.setLanguage });')
            .replace('const [projectId, writeProjectId]',
               'Object.assign(window, { readBatchCapability: () => mutation?.capability, publishLayoutState: publishWorkspaceState, publishLayoutCatalog: (snapshot: WorkspaceSnapshot) => publishWorkspaceState({ kind: "ready", snapshot }), cycleInfoSelection: () => { setSessionId("two"); setProjectId(null); setProjectId(projectId); setSessionId(sessionId); }, cycleInfoHost: () => { setStatus({ ...status!, hostAvailable: false, hostEpoch: "temporary-host" }); setStatus(status!); }, loseLayoutHost: () => setStatus({ ...status!, hostAvailable: false, hostEpoch: "different-host" }) }); const [projectId, writeProjectId]') }));
      } }] });
    // CSS imports are suppressed in the JS bundle; use the real packaged layout geometry too.
    await writeFile(join(root, "style.css"), readFileSync(new URL("../node_modules/flexlayout-react/style/light.css", import.meta.url), "utf8") +
      "\n" + readFileSync(new URL("./style.css", import.meta.url), "utf8"));
    const page = join(root, "fixture.html");
    await writeFile(page, '<!doctype html><html><head><link rel="stylesheet" href="style.css"></head><body><div id="root"></div><script src="fixture.js"></script></body></html>');
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
    const command = (method: string, params: object = {}) => new Promise<{ result?: { value?: unknown }; exceptionDetails?: unknown }>((resolve, reject) => {
      const id = ++sequence;
      const timer = setTimeout(() => reject(new Error(`browser ${method} timed out`)), 12_000);
      const reply = (event: MessageEvent) => {
        const message = JSON.parse(String(event.data)) as { id?: number; result?: { result?: { value?: unknown }; exceptionDetails?: unknown }; error?: object };
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
    const evaluate = async (expression: string) => {
      const response = await command("Runtime.evaluate", { expression, returnByValue: true, awaitPromise: true });
      assert.equal(response.exceptionDetails, undefined, `Browser evaluation failed: ${JSON.stringify(response.exceptionDetails)}`);
      return response.result?.value;
    };
    const wait = (condition: string) => evaluate(`new Promise(resolve => { const end=Date.now()+7000; const tick=()=>{
      if (${condition}) resolve(true); else if (Date.now()>end) resolve(document.body.innerText.slice(-1200));
      else setTimeout(tick,20); }; tick(); })`);
    assert.equal(await wait("!!document.querySelector('#catalog-prompt') && !!document.querySelector('.project-rail .icon-label-button')"), true);
    assert.equal(await evaluate(`document.querySelectorAll('.session-content-layout .flexlayout__tab').length===2 &&
      !!document.querySelector('.session-rail')?.closest('.flexlayout__tab') &&
      !!document.querySelector('.content')?.closest('.flexlayout__tab') &&
      !document.querySelector('.project-rail')?.closest('.flexlayout__tab')`), true,
      "actual App sessions/content share one fixed model, with projects outside");
    assert.equal(await evaluate(`(() => {const host=document.querySelector('.workspace-layout');
      const panel=host?.querySelector('.session-content-main-panel');
      return !!panel && panel.getBoundingClientRect().height>0 && host.getBoundingClientRect().width>0 &&
        document.querySelector('.workspace-shell').getBoundingClientRect().width>0 &&
        !host.querySelector('.flexlayout__splitter, .flexlayout__tab_button, .flexlayout__floating_window');})()`), true,
      "the actual App must mount measured content without package docking controls");
    assert.equal(await evaluate(`(() => {const projects=document.querySelector('.project-rail'); const sessions=document.querySelector('.session-rail');
      const filter=projects.querySelector('#project-filter'); const sort=projects.querySelector('#project-sort');
      return !!projects.querySelector('.panel-title') && !!sessions.querySelector('.session-rail-header h2') &&
        !projects.querySelector('.project-controls label, .project-controls p') && !sessions.querySelector('.session-rail-header .eyebrow') &&
        filter.getAttribute('aria-label')==='Filter projects by name or path' && sort.getAttribute('aria-label')==='Sort projects' &&
        projects.querySelector('.project-controls').getBoundingClientRect().height<92 &&
        sessions.querySelector('.session-rail-header').getBoundingClientRect().height<64 &&
        !!projects.querySelector('.rail-footer button') && !!sessions.querySelector('[aria-label="Create session"]');})()`), true,
      "rail headers and controls stay useful without redundant visible pre-list prose");
    assert.equal(await evaluate("document.querySelector('.session-rail')?.textContent.includes('Session creation requires an owned host.') && document.querySelector('#project-filter')?.getAttribute('aria-controls')==='project-list' && !!document.querySelector('#project-list')"), true,
      "catalog-only restrictions and list relationships remain visible/accessible");
    for (const width of [390, 1120]) for (const theme of ["light", "dark"]) {
      await command("Emulation.setDeviceMetricsOverride", { width, height: 800, deviceScaleFactor: 1, mobile: false });
      await evaluate(`document.documentElement.dataset.theme='${theme}'`);
      assert.equal(await wait(`window.innerWidth===${width} && document.querySelector('[aria-controls=project-rail]').getAttribute('aria-expanded')==='${width===390 ? "false" : "true"}'`), true);
      if (width===390)
        await evaluate("document.querySelector('[aria-controls=project-rail]').click()");
      assert.equal(await wait("document.querySelector('#project-rail').getBoundingClientRect().width>0"), true);
      assert.equal(await evaluate(`(() => {const rail=document.querySelector('#project-rail'); const controls=rail.querySelector('.project-controls');
        return controls.getBoundingClientRect().height<92 && controls.getBoundingClientRect().height>0 &&
          rail.querySelector('.nav-list').getBoundingClientRect().top>=controls.getBoundingClientRect().bottom &&
          rail.querySelector('.rail-footer button').getBoundingClientRect().right<=${width};})()`), true, `compact project rail at ${width}px ${theme}`);
      if (width===390) await evaluate("document.querySelector('[aria-controls=project-rail]').click()");
      assert.equal(await wait("document.querySelector('.session-rail').getBoundingClientRect().width>0"), true);
      assert.equal(await evaluate("document.querySelector('.session-rail-header').getBoundingClientRect().height<64 && document.querySelector('.session-rail-header h2')?.textContent==='Project' && document.querySelector('.session-rail .search input')?.getAttribute('aria-label')==='Search sessions'"), true,
        `compact session rail at ${width}px ${theme}`);
    }
    assert.equal(await evaluate("!!document.querySelector('.topnav, #open-provider-configuration')"), false);
    await evaluate(`(() => { const input=document.querySelector('#catalog-prompt'); input.focus();
      Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype,'value').set.call(input,'Private local draft');
      input.dispatchEvent(new Event('input',{bubbles:true})); window.originalComposer=input;
      window.originalWorkspace=document.querySelector('.workspace-shell');
      window.originalTimeline=document.querySelector('.timeline-scroll'); })()`);
    assert.equal(await wait("document.querySelector('#catalog-prompt').value==='Private local draft'"), true);
    await evaluate("document.querySelector('.project-rail .icon-label-button').focus(); document.querySelector('.project-rail .icon-label-button').click()");
    assert.equal(await wait("document.querySelector('.settings-dialog')?.open"), true);
    assert.equal(await evaluate("document.querySelector('.settings-dialog').getBoundingClientRect().width"), 896);
    assert.equal(await evaluate(`(() => { const dialog = document.querySelector('.settings-dialog');
      const nav = dialog.querySelector('.settings-dialog-navigation');
      const titles = [...nav.querySelectorAll('button')].map(button => button.textContent);
      const buttons = [...nav.querySelectorAll('button')];
      return !dialog.querySelector('.settings-search, .settings-navigation') &&
        !titles.some(title => title === 'Overview' || title === 'All settings' || title === 'All Settings') &&
        ['Appearance', 'Providers', 'Models', 'Agent prompts', 'Skills', 'Plugins & MCP', 'MCP Servers', 'Application Logs', 'About'].every(title => titles.includes(title)) &&
        buttons[0].getBoundingClientRect().top < buttons[1].getBoundingClientRect().top &&
        dialog.querySelector('.settings-dialog-content .page-heading h1')?.textContent === 'Appearance'; })()`), true,
      "Settings must offer only grouped actual pages in a vertical sidebar, not an aggregate/tab strip");
    assert.deepEqual(await evaluate("[...document.querySelectorAll('.settings-dialog-group h3')].map(x=>x.textContent)"),
      ["Personalization", "Agent & models", "Extensions", "Diagnostics"]);
    await evaluate("[...document.querySelectorAll('.settings-dialog-navigation button')].find(x=>x.textContent==='Skills').click()");
    assert.equal(await wait("document.querySelector('.settings-dialog-navigation [aria-current=page]')?.textContent==='Skills' && !!document.querySelector('.skills-inspection')"), true);
    assert.equal(await evaluate("settingsShellFixture.skillReads.length===0 && document.querySelector('.skills-inspection > button').disabled && document.querySelector('.skills-inspection').textContent.includes('Unavailable')"), true);
    assert.equal(await evaluate("!!document.querySelector('.settings-dialog-content #settings-project-sort')"), false);
    await evaluate("[...document.querySelectorAll('.settings-dialog-navigation button')].find(x=>x.textContent==='Plugins & MCP').focus()");
    assert.equal(await evaluate("document.activeElement?.textContent"), "Plugins & MCP");
    await command("Input.dispatchKeyEvent", { type: "keyDown", key: "Enter", code: "Enter", text: "\r", windowsVirtualKeyCode: 13 });
    await command("Input.dispatchKeyEvent", { type: "keyUp", key: "Enter", code: "Enter", windowsVirtualKeyCode: 13 });
    assert.equal(await wait("document.querySelector('.settings-dialog-navigation [aria-current=page]')?.textContent==='Plugins & MCP' && document.querySelector('.settings-dialog-content .settings-card h2')?.textContent==='Plugins & MCP'"), true);
    assert.equal(await evaluate("document.activeElement?.closest('.settings-dialog-navigation')!==null"), true);
    await evaluate("document.querySelector('.settings-dialog-navigation button').click()");
    assert.equal(await wait("document.querySelector('.settings-dialog-content #settings-project-sort')!==null"), true);
    assert.equal(await evaluate("window.originalComposer===document.querySelector('#catalog-prompt') && window.originalWorkspace===document.querySelector('.workspace-shell') && window.originalTimeline===document.querySelector('.timeline-scroll')"), true);
    assert.equal(await evaluate("document.activeElement.closest('.settings-dialog')!==null"), true);
    assert.equal(await evaluate("document.querySelector('.settings-dialog').matches(':modal')"), true);
    await command("Input.dispatchMouseEvent", { type: "mousePressed", x: 5, y: 5, button: "left", clickCount: 1 });
    await command("Input.dispatchMouseEvent", { type: "mouseReleased", x: 5, y: 5, button: "left", clickCount: 1 });
    assert.equal(await evaluate("document.querySelector('.settings-dialog')?.open && document.querySelector('#catalog-prompt').value==='Private local draft'"), true);
    assert.equal(await evaluate("(() => {const d=document.querySelector('.settings-dialog'); d.dispatchEvent(new KeyboardEvent('keydown', {key:'Escape',repeat:true,bubbles:true,cancelable:true})); return d.open})()"), true);
    assert.equal(await evaluate("(() => {const d=document.querySelector('.settings-dialog'); d.dispatchEvent(new KeyboardEvent('keydown', {key:'Escape',isComposing:true,bubbles:true,cancelable:true})); return d.open})()"), true);
    await evaluate("[...document.querySelectorAll('.settings-dialog-navigation button')].find(x=>x.textContent==='Application Logs').click()");
    assert.equal(await wait("document.querySelector('.settings-dialog-navigation [aria-current=\"page\"]')?.textContent==='Application Logs'"), true);
    assert.equal(await evaluate("window.settingsShellFixture.calls.length"), 0);
    await evaluate("[...document.querySelectorAll('.application-logs button')].find(x=>x.textContent.includes('Refresh logs')).click()");
    assert.equal(await wait("window.settingsShellFixture.calls.length===1 && document.querySelector('.application-logs')?.textContent.includes('No files were read')"), true);
    assert.equal(await evaluate("window.originalComposer===document.querySelector('#catalog-prompt') && document.querySelector('#catalog-prompt').value==='Private local draft'"), true);
    await evaluate("document.querySelector('.settings-dialog-navigation button').click()");
    assert.equal(await wait("document.querySelector('.settings-dialog-navigation [aria-current=\"page\"]')?.textContent==='Appearance'"), true);
    await evaluate("[...document.querySelectorAll('.settings-dialog-navigation button')].find(x=>x.textContent==='About').click()");
    assert.equal(await wait("document.querySelector('.settings-dialog-content .settings-card h2')?.textContent==='About' && !document.querySelector('.about-dialog')"), true);
    await evaluate("document.querySelector('.settings-dialog-content .settings-card button').click()");
    assert.equal(await wait("document.querySelector('.about-dialog')?.open"), true);
    await evaluate("document.querySelector('.about-dialog [aria-label=\"Close About\"]').click()");
    assert.equal(await wait("!document.querySelector('.about-dialog') && document.querySelector('.settings-dialog')?.open"), true);
    await command("Input.dispatchKeyEvent", { type: "keyDown", key: "Escape", code: "Escape", windowsVirtualKeyCode: 27 });
    await command("Input.dispatchKeyEvent", { type: "keyUp", key: "Escape", code: "Escape", windowsVirtualKeyCode: 27 });
    assert.equal(await wait("!document.querySelector('.settings-dialog')"), true);
    assert.equal(await wait("document.activeElement===document.querySelector('.project-rail .icon-label-button') && window.originalComposer===document.querySelector('#catalog-prompt')"), true);
    await command("Input.dispatchKeyEvent", { type: "keyDown", key: ",", code: "Comma", windowsVirtualKeyCode: 188, modifiers: 2 });
    await command("Input.dispatchKeyEvent", { type: "keyUp", key: ",", code: "Comma", windowsVirtualKeyCode: 188, modifiers: 2 });
    assert.equal(await wait("document.querySelector('.settings-dialog')?.open"), true);
    assert.equal(await evaluate("document.activeElement.closest('.settings-dialog')!==null"), true);
    await evaluate("document.querySelector('[aria-label=\"Close settings\"]').click()");
    assert.equal(await wait("!document.querySelector('.settings-dialog')"), true);
    await evaluate("[...document.querySelectorAll('button')].find(x=>x.textContent.includes('Commands') && x.textContent.includes('Ctrl+P')).click()");
    assert.equal(await wait("document.querySelector('.command-palette')?.open"), true);
    assert.equal(await evaluate("!document.querySelector('#palette-option-skills') && !document.querySelector('#palette-option-usage')"), true,
      "catalog-only palette cannot advertise owned inspection authority");
    await evaluate("document.querySelector('#palette-option-models').click()");
    assert.equal(await wait("document.querySelector('.settings-dialog')?.open && document.querySelector('.settings-dialog-navigation [aria-current=\"page\"]')?.textContent==='Models'"), true);
    assert.equal(await evaluate("window.originalComposer===document.querySelector('#catalog-prompt') && document.querySelector('#catalog-prompt').value==='Private local draft'"), true);
    await evaluate("document.querySelector('[aria-label=\"Close settings\"]').click()");
    assert.equal(await wait("!document.querySelector('.settings-dialog')"), true);
    await evaluate("[...document.querySelectorAll('button')].find(x=>x.textContent.includes('Commands') && x.textContent.includes('Ctrl+P')).click()");
    assert.equal(await wait("document.querySelector('.command-palette')?.open"), true);
    await evaluate("document.querySelector('#palette-option-about').click()");
    assert.equal(await wait("document.querySelector('.settings-dialog')?.open && document.querySelector('.about-dialog')?.open"), true);
    await evaluate("document.querySelector('.about-dialog [aria-label=\"Close About\"]').click()");
    assert.equal(await wait("!document.querySelector('.about-dialog') && document.querySelector('.settings-dialog')?.open"), true);
    await evaluate("document.querySelector('[aria-label=\"Close settings\"]').click()");
    assert.equal(await wait("!document.querySelector('.settings-dialog') && window.originalComposer===document.querySelector('#catalog-prompt')"), true);
    await evaluate("document.querySelector('#catalog-prompt').focus()");
    for (const [key, code, value] of [["g", "KeyG", 71], ["u", "KeyU", 85]] as const) {
      await command("Input.dispatchKeyEvent", { type: "keyDown", key, code, windowsVirtualKeyCode: value, modifiers: 2 });
      await command("Input.dispatchKeyEvent", { type: "keyUp", key, code, windowsVirtualKeyCode: value, modifiers: 2 });
    }
    assert.equal(await wait("document.querySelector('.settings-dialog')?.open && document.querySelector('.settings-dialog-navigation [aria-current=\"page\"]')?.textContent==='Appearance'"), true);
    await evaluate("document.querySelector('[aria-label=\"Close settings\"]').click()");
    assert.equal(await wait("!document.querySelector('.settings-dialog') && document.activeElement===document.querySelector('#catalog-prompt')"), true);
    for (const width of [390, 1120]) for (const theme of ["dark", "light"]) {
      await command("Emulation.setDeviceMetricsOverride", { width, height: 800, deviceScaleFactor: 1, mobile: false });
      await evaluate(`document.documentElement.dataset.theme='${theme}'; document.querySelector('.project-rail .icon-label-button').click()`);
      assert.equal(await wait("document.querySelector('.settings-dialog')?.open"), true);
      assert.equal(await evaluate(`(() => {const r=document.querySelector('.settings-dialog').getBoundingClientRect();
        return r.width<=${width} && r.left>=0 && r.right<=${width} && r.height<=800 && getComputedStyle(document.querySelector('.settings-dialog')).color!=='rgba(0, 0, 0, 0)'})()`), true);
      assert.equal(await evaluate(`(() => {const dialog=document.querySelector('.settings-dialog');
        const nav=dialog.querySelector('.settings-dialog-navigation'); const content=dialog.querySelector('.settings-dialog-content');
        const buttons=[...nav.querySelectorAll('button')]; return nav.getBoundingClientRect().right<=content.getBoundingClientRect().left+1 &&
          buttons.every((button,index)=>index===0 || button.getBoundingClientRect().top>buttons[index-1].getBoundingClientRect().top) &&
          dialog.scrollWidth<=dialog.clientWidth; })()`), true, `sidebar should remain vertical at ${width}px`);
      await evaluate("document.querySelector('[aria-label=\"Close settings\"]').click()");
      assert.equal(await wait("!document.querySelector('.settings-dialog')"), true);
    }
    // A second production-shell boot uses an isolated owned fake; the first boot proved catalog-only behavior.
    await evaluate("localStorage.setItem('settingsFixtureOwned','true')");
    await command("Page.reload");
    assert.equal(await wait("document.querySelector('#session-prompt') && document.querySelector('.owned-session [aria-label=\"Model\"]')?.value==='old'"), true);
    // Registry access routes through the actual mounted App and existing modal owners.
    const openCommands = async () => {
      await evaluate("document.querySelector('[aria-label=\"Open command palette\"]').click()");
      assert.equal(await wait("document.querySelector('.command-palette')?.open"), true);
    };
    const chord = async (key: string, options = "") => evaluate(`(() => {
      const target=document.activeElement;
      target.dispatchEvent(new KeyboardEvent('keydown',{key:'g',ctrlKey:true,bubbles:true,cancelable:true}));
      return target.dispatchEvent(new KeyboardEvent('keydown',{key:${JSON.stringify(key)},ctrlKey:true,bubbles:true,cancelable:true,${options}}));
    })()`);
    const usageBefore = Number(await evaluate("settingsShellFixture.usageReads.length"));
    const skillsBefore = Number(await evaluate("settingsShellFixture.skillReads.length"));
    await openCommands();
    assert.equal(await evaluate("['skills','usage','openProject','help'].every(id=>!!document.querySelector('#palette-option-'+id))"), true);
    await evaluate("document.querySelector('#palette-option-usage').dispatchEvent(new MouseEvent('mouseenter',{bubbles:true}))");
    assert.equal(await evaluate("settingsShellFixture.usageReads.length"), usageBefore, "search/hover is not an observation");
    await evaluate("document.querySelector('#palette-option-usage').click()");
    assert.equal(await wait("document.querySelector('.session-usage-dialog')?.open"), true);
    assert.equal(await evaluate("settingsShellFixture.usageReads.length"), usageBefore + 1);
    assert.deepEqual(await evaluate("settingsShellFixture.usageReads.at(-1).request"), {
      expectedHostEpoch: "12345678-1234-1234-1234-123456789abc", sessionId: "one", scope: "project", projectId: "project", expectedProjectPath: "/fixture/project",
    });
    await evaluate("document.querySelector('.session-usage-dialog header button').click()");
    assert.equal(await wait("!document.querySelector('.session-usage-dialog') && document.activeElement.id==='session-usage-trigger'"), true);
    await evaluate("settingsShellFixture.releaseInfoUsage()");
    assert.equal(await evaluate("!!document.querySelector('.session-usage-dialog')"), false, "held old reply cannot reopen");
    for (const cycle of ["cycleInfoSelection", "cycleInfoHost"]) {
      await openCommands();
      await evaluate(`(() => { const option=document.querySelector('#palette-option-usage'); window.${cycle}(); option.click(); })()`);
      assert.equal(await evaluate("settingsShellFixture.usageReads.length"), usageBefore + 1, "synchronous ABA cannot admit selected command");
      assert.equal(await wait("!document.querySelector('#palette-option-usage')"), true);
      await evaluate("document.querySelector('.command-palette header button').click()");
    }
    await openCommands();
    await evaluate(`(() => { const fence=event=>{if(event.target.matches?.('.command-palette') && event.newState==='closed') {
      document.removeEventListener('beforetoggle',fence,true); window.cycleInfoSelection(); }};
      document.addEventListener('beforetoggle',fence,true); document.querySelector('#palette-option-usage').click(); })()`);
    assert.equal(await wait("!document.querySelector('.command-palette')"), true);
    assert.equal(await evaluate("settingsShellFixture.usageReads.length"), usageBefore + 1, "deferred close dispatch rechecks original generation");
    await evaluate("document.querySelector('#session-usage-trigger').focus(); document.activeElement.dispatchEvent(new KeyboardEvent('keydown',{key:'g',ctrlKey:true,bubbles:true,cancelable:true})); window.cycleInfoSelection()");
    assert.equal(await evaluate("document.activeElement.dispatchEvent(new KeyboardEvent('keydown',{key:'k',ctrlKey:true,bubbles:true,cancelable:true}))"), true, "chord ABA stays unconsumed");
    assert.equal(await evaluate("!!document.querySelector('.settings-dialog')"), false);
    for (const [key, section] of [["k", "skills"], ["l", "logs"]]) {
      for (const options of ["repeat:true", "isComposing:true", "keyCode:229", "metaKey:true", "altKey:true", "shiftKey:true"]) {
        await evaluate("document.querySelector('#session-usage-trigger').focus()");
        assert.equal(await chord(key, options), true, "invalid suffix stays unconsumed");
        assert.equal(await evaluate("!!document.querySelector('.settings-dialog')"), false);
      }
      await evaluate("document.querySelector('#session-prompt').focus()");
      assert.equal(await chord(key), true, "editing does not activate the inspection chord");
      await evaluate("document.querySelector('#session-usage-trigger').focus()");
      assert.equal(await chord(key), false);
      assert.equal(await wait(`document.querySelector('.settings-dialog')?.open && document.querySelector('[data-settings-section=${section}]')?.getAttribute('aria-current')==='page'`), true);
      assert.equal(await evaluate("settingsShellFixture.skillReads.length"), skillsBefore, "opening Skills never scans");
      await evaluate("document.querySelector('.settings-dialog-header button').click()");
      assert.equal(await wait("!document.querySelector('.settings-dialog')"), true);
      for (const theme of ["light", "dark"]) {
        await command("Emulation.setDeviceMetricsOverride", { width: 390, height: 500, deviceScaleFactor: 1, mobile: false });
        await evaluate(`document.documentElement.dataset.theme='${theme}';document.querySelector('#session-usage-trigger').scrollIntoView({block:'center'});document.querySelector('#session-usage-trigger').focus()`);
        assert.equal(await chord(key), false);
        assert.equal(await wait("document.querySelector('.settings-dialog')?.open"), true);
        assert.equal(await evaluate(`(() => {const close=document.querySelector('.settings-dialog-header button');close.scrollIntoView({block:'center'});close.focus();const r=close.getBoundingClientRect();
          return document.activeElement===close && r.left>=0 && r.right<=innerWidth && r.top>=0 && r.bottom<=innerHeight;})()`), true, `${theme}: keyboard-opened ${section} has reachable Close`);
        await command("Input.dispatchKeyEvent", { type: "keyDown", key: "Escape", code: "Escape", windowsVirtualKeyCode: 27 });
        assert.equal(await wait("!document.querySelector('.settings-dialog') && document.activeElement.id==='session-usage-trigger'"), true);
      }
    }
    await command("Emulation.setDeviceMetricsOverride", { width: 1120, height: 800, deviceScaleFactor: 1, mobile: false });
    await openCommands();
    await evaluate("document.querySelector('#palette-option-help').click()");
    assert.equal(await wait("!!document.querySelector('.shortcut-dialog')"), true);
    assert.equal(await evaluate("document.querySelector('.shortcut-dialog').textContent.includes('Ctrl+G, Ctrl+K') && document.querySelector('.shortcut-dialog').textContent.includes('Ctrl+G, Ctrl+L')"), true);
    await evaluate("document.querySelector('.shortcut-dialog header button').click()");
    await openCommands();
    await evaluate("document.querySelector('#palette-option-openProject').click()");
    assert.equal(await wait("!!document.querySelector('#open-project-title')"), true);
    await evaluate("document.querySelector('[aria-labelledby=open-project-title] header button').click()");
    assert.equal(await wait("!document.querySelector('#open-project-title')"), true);
    await evaluate(`(() => {const input=document.querySelector('#session-prompt'); window.ownedComposer=input;
      Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype,'value').set.call(input,'Owned test draft');
      input.dispatchEvent(new Event('input',{bubbles:true})); })()`);
    assert.equal(await wait("document.querySelector('#session-prompt').value==='Owned test draft'"), true);
    await evaluate(`(async () => {
      const canvas=document.createElement('canvas'); canvas.width=1; canvas.height=1;
      const blob=await new Promise(resolve=>canvas.toBlob(resolve,'image/png'));
      window.imageFile=new File([blob],'fixture.png',{type:'image/png'});
      window.pasteFixtureImage=(selector, file=window.imageFile)=>{ const transfer=new DataTransfer(); transfer.items.add(file);
        document.querySelector(selector).dispatchEvent(new ClipboardEvent('paste',{bubbles:true,cancelable:true,clipboardData:transfer})); };
      window.pasteFixtureImage('#session-prompt');
    })()`);
    assert.equal(await wait("document.querySelector('.prompt-image-attachments')?.textContent.includes('Image paste unavailable')"), true);
    assert.equal(await evaluate("document.querySelectorAll('.prompt-image-attachments img').length"), 0);
    await evaluate("localStorage.setItem('settingsFixtureNewChoices','true'); document.querySelector('.project-rail .icon-label-button').click()");
    assert.equal(await wait("document.querySelector('.settings-dialog')?.matches(':modal')"), true);
    await command("Input.dispatchMouseEvent", { type: "mousePressed", x: 5, y: 5, button: "left", clickCount: 1 });
    await command("Input.dispatchMouseEvent", { type: "mouseReleased", x: 5, y: 5, button: "left", clickCount: 1 });
    await command("Input.dispatchKeyEvent", { type: "keyDown", key: "Enter", code: "Enter", windowsVirtualKeyCode: 13, modifiers: 2 });
    await command("Input.dispatchKeyEvent", { type: "keyUp", key: "Enter", code: "Enter", windowsVirtualKeyCode: 13, modifiers: 2 });
    assert.equal(await evaluate("document.querySelector('.settings-dialog')?.open && document.querySelector('#session-prompt').value==='Owned test draft' && window.settingsShellFixture.sends.length===0"), true);
    // Simulate an external selection change while the native modal makes pointer access to the workspace inert.
    await evaluate("[...document.querySelectorAll('.session-row > button:first-child')].find(x=>x.textContent.includes('two')).dispatchEvent(new MouseEvent('click',{bubbles:true}))");
    assert.equal(await wait("document.querySelector('.session-header h1')?.textContent==='two' && document.querySelector('.settings-dialog')?.open"), true);
    await evaluate("[...document.querySelectorAll('.settings-dialog-navigation button')].find(x=>x.textContent==='Providers').click()");
    assert.equal(await wait("document.querySelector('.settings-dialog-content .settings-card h2')?.textContent==='Providers'"), true);
    await evaluate("[...document.querySelectorAll('.session-row > button:first-child')].find(x=>x.textContent.includes('one')).dispatchEvent(new MouseEvent('click',{bubbles:true}))");
    assert.equal(await wait("document.querySelector('.session-header h1')?.textContent==='one' && document.querySelector('#session-prompt')?.value==='Owned test draft'"), true);
    await evaluate("window.ownedComposer=document.querySelector('#session-prompt'); true");
    assert.equal(await evaluate("window.settingsShellFixture.sends.length"), 0);
    await evaluate("[...document.querySelectorAll('.settings-dialog-navigation button')].find(x=>x.textContent==='Models').click()");
    assert.equal(await wait("!!document.querySelector('.model-catalog-providers button')"), true);
    await evaluate("document.querySelector('.model-catalog-providers button').click()");
    assert.equal(await wait("!!document.querySelector('.model-catalog-list button')"), true);
    await evaluate("document.querySelector('.model-catalog-list button').click()");
    assert.equal(await wait("!!document.querySelector('.model-catalog-next select option[value=\"High\"]')"), true);
    await evaluate(`(() => {const select=document.querySelector('.model-catalog-next select'); select.value='High'; select.dispatchEvent(new Event('change',{bubbles:true}));})()`);
    await evaluate("[...document.querySelectorAll('.model-catalog-next button')].find(x=>x.textContent.includes('Use model')).click()");
    const modelApplied = await wait("!document.querySelector('.settings-dialog') && document.querySelector('.owned-session [aria-label=\"Model\"]')?.value==='new'");
    assert.equal(modelApplied, true, JSON.stringify(await evaluate("({open:!!document.querySelector('.settings-dialog'), model:document.querySelector('.owned-session [aria-label=\"Model\"]')?.value, notice:document.querySelector('.model-catalog-next')?.innerText})")));
    assert.equal(await evaluate("window.ownedComposer===document.querySelector('#session-prompt') && document.querySelector('#session-prompt').value==='Owned test draft'"), true);
    await evaluate("window.pasteFixtureImage('#session-prompt',new File(['bad'],'invalid.png',{type:'image/png'}))");
    assert.equal(await wait("document.querySelector('.prompt-image-attachments')?.textContent.includes('Image paste refused')"), true);
    await evaluate(`(() => {const file=new File([window.imageFile],'late.png',{type:'image/png'});
      file.arrayBuffer=()=>new Promise(resolve=>window.releaseImage=async()=>resolve(await window.imageFile.arrayBuffer()));
      window.pasteFixtureImage('#session-prompt',file); document.querySelector('.project-rail .icon-label-button').click(); })()`);
    assert.equal(await wait("!!document.querySelector('[aria-label=\"Close settings\"]')"), true);
    await evaluate("document.querySelector('[aria-label=\"Close settings\"]').click()");
    assert.equal(await wait("!document.querySelector('.settings-dialog')"), true);
    await evaluate("window.releaseImage()");
    await new Promise(resolve => setTimeout(resolve, 100));
    assert.equal(await evaluate("document.querySelectorAll('.prompt-image-attachments img').length"), 0, "Settings ABA fences the original element's delayed read");
    await evaluate("document.querySelector('#expand-session-prompt').click()");
    assert.equal(await wait("!!document.querySelector('.expanded-prompt-dialog textarea')"), true);
    await evaluate(`(() => {const file=new File([window.imageFile],'late.png',{type:'image/png'});
      file.arrayBuffer=()=>new Promise(resolve=>window.releaseImage=async()=>resolve(await window.imageFile.arrayBuffer()));
      window.pasteFixtureImage('.expanded-prompt-dialog textarea',file);
      document.querySelector('.expanded-prompt-dialog header button').click(); })()`);
    await evaluate("window.releaseImage()");
    await new Promise(resolve => setTimeout(resolve, 100));
    assert.equal(await evaluate("document.querySelectorAll('.prompt-image-attachments img').length"), 0, "closing expanded editor fences its delayed paste");
    await evaluate("window.pasteFixtureImage('#session-prompt')");
    assert.equal(await wait("document.querySelectorAll('.prompt-image-attachments img').length===1"), true);
    await evaluate(`window.editImageTitle=value=>{const input=document.querySelector('.prompt-image-attachments input');Object.getOwnPropertyDescriptor(HTMLInputElement.prototype,'value').set.call(input,value);input.dispatchEvent(new Event('input',{bubbles:true}));};
      window.editImageText=value=>{const input=document.querySelector('#session-prompt');Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype,'value').set.call(input,value);input.dispatchEvent(new Event('input',{bubbles:true}));};
      window.editImageText('');`);
    assert.equal(await wait("!document.querySelector('.owned-session .send-button').disabled"), true, "valid retained PNG enables truly empty Send");
    await evaluate("window.editImageText('  ')");
    assert.equal(await wait("document.querySelector('.owned-session .send-button').disabled"), true, "whitespace is not image-only text");
    await evaluate("window.editImageText('');window.editImageTitle('Renamed / local')");
    assert.equal(await wait("document.querySelector('.prompt-image-attachments img').alt==='Renamed / local'"), true);
    await evaluate("window.editImageTitle('   ')");
    assert.equal(await evaluate("document.querySelector('.prompt-image-attachments input').value==='Renamed / local' && settingsShellFixture.sends.length===0"), true);
    await evaluate(`(() => {const file=new File([window.imageFile],'late.png',{type:'image/png'});
      file.arrayBuffer=()=>new Promise(resolve=>window.releaseImage=async()=>resolve(await window.imageFile.arrayBuffer()));window.pasteFixtureImage('#session-prompt',file);})()`);
    await evaluate("window.editImageTitle('Image 1');window.releaseImage()");
    await new Promise(resolve => setTimeout(resolve, 100));
    assert.equal(await evaluate("document.querySelectorAll('.prompt-image-attachments img').length"), 1, "rename fences delayed paste");
    await evaluate("document.querySelector('#expand-session-prompt').click()");
    assert.equal(await wait("!!document.querySelector('.expanded-prompt-dialog input')"), true);
    await evaluate("window.imageEditCalls=settingsShellFixture.rpcCalls.length;window.editImageTitle('画像');document.querySelector('.expanded-prompt-dialog input').dispatchEvent(new KeyboardEvent('keydown',{key:'Enter',isComposing:true,bubbles:true}));");
    assert.equal(await evaluate("document.querySelector('.expanded-prompt-dialog').open && document.querySelector('.expanded-prompt-dialog input').value==='画像' && settingsShellFixture.sends.length===0 && settingsShellFixture.rpcCalls.length===imageEditCalls"), true);
    await evaluate("document.querySelector('.expanded-prompt-dialog input').focus()");
    await command("Input.dispatchKeyEvent", { type: "keyDown", key: "Enter", code: "Enter", text: "\r", windowsVirtualKeyCode: 13 });
    await command("Input.dispatchKeyEvent", { type: "keyUp", key: "Enter", code: "Enter", windowsVirtualKeyCode: 13 });
    assert.equal(await evaluate("document.querySelector('.expanded-prompt-dialog').open && settingsShellFixture.sends.length===0"), true, "title Enter does not invoke editor close or Send");
    await evaluate("window.editImageTitle('Image 1');document.querySelector('.expanded-prompt-dialog header button').click()");
    for (const theme of ["light", "dark"]) {
      await command("Emulation.setDeviceMetricsOverride", { width: 390, height: 800, deviceScaleFactor: 1, mobile: false });
      await evaluate(`document.documentElement.dataset.theme='${theme}'`);
      assert.equal(await evaluate("document.querySelector('.prompt-image-attachments').scrollWidth<=document.querySelector('.prompt-image-attachments').clientWidth+1"), true);
    }
    await evaluate("document.querySelector('.prompt-image-attachments figure button').focus()");
    await command("Input.dispatchKeyEvent", { type: "keyDown", key: "Enter", code: "Enter", text: "\r", windowsVirtualKeyCode: 13 });
    await command("Input.dispatchKeyEvent", { type: "keyUp", key: "Enter", code: "Enter", windowsVirtualKeyCode: 13 });
    assert.equal(await wait("document.querySelectorAll('.prompt-image-attachments img').length===0"), true);
    assert.equal(await evaluate("document.querySelector('.owned-session .send-button').disabled"), true, "removing final PNG disables empty Send immediately");
    await evaluate("window.editImageText('Owned test draft')");
    await command("Emulation.setDeviceMetricsOverride", { width: 1120, height: 800, deviceScaleFactor: 1, mobile: false });
    await evaluate("window.pasteFixtureImage('#session-prompt')");
    assert.equal(await wait("document.querySelectorAll('.prompt-image-attachments img').length===1"), true);
    await evaluate("[...document.querySelectorAll('.session-row > button:first-child')].find(x=>x.textContent.includes('two')).click()");
    assert.equal(await wait("document.querySelector('.session-header h1')?.textContent==='two'"), true);
    assert.equal(await evaluate("document.querySelectorAll('.prompt-image-attachments img').length"), 0, "another session cannot see the draft image");
    await evaluate("[...document.querySelectorAll('.session-row > button:first-child')].find(x=>x.textContent.includes('one')).click()");
    assert.equal(await wait("document.querySelector('.session-header h1')?.textContent==='one' && document.querySelectorAll('.prompt-image-attachments img').length===1 && document.querySelector('.owned-session [aria-label=Model]')?.value==='new'"), true);
    await evaluate("window.shortImageEditor=document.querySelector('#session-prompt');window.shortImageDraft=shortImageEditor.value;shortImageEditor.focus();shortImageEditor.setSelectionRange(2,5);true");
    for (const [width, height] of [[750, 485], [1120, 800], [390, 500]]) {
      await command("Emulation.setDeviceMetricsOverride", { width, height, deviceScaleFactor: 1, mobile: false });
      assert.equal(await wait(`innerWidth===${width} && innerHeight===${height} && document.querySelector('.active-session-content').clientHeight${width < 875 ? "<350" : ">600"}
        && ${width < 875 ? `Math.abs(document.querySelector('.content').getBoundingClientRect().width-${width})<2` : "document.querySelector('.content').getBoundingClientRect().width<896"}`), true);
      await evaluate("shortImageEditor.scrollIntoView({block:'center',inline:'nearest'})");
      assert.equal(await evaluate("shortImageEditor===document.querySelector('#session-prompt') && document.activeElement===shortImageEditor && shortImageEditor.value===shortImageDraft && shortImageEditor.selectionStart===2 && shortImageEditor.selectionEnd===5 && document.querySelectorAll('.prompt-image-attachments img').length===1"), true,
        "short/wide transitions preserve the focused editor, native selection and original image draft");
    }
    await evaluate("window.editImageText('');window.editImageTitle('Submitted title');document.querySelector('.owned-session .send-button').focus()");
    assert.equal(await evaluate("(()=>{const b=document.activeElement,r=b.getBoundingClientRect();return b.matches('.send-button') && document.elementFromPoint(r.x+r.width/2,r.y+r.height/2)?.closest('button')===b})()"), true,
      "Send is keyboard-reachable and not clipped in the short narrow workspace");
    await command("Input.dispatchKeyEvent", { type: "keyDown", key: "Enter", code: "Enter", text: "\r", windowsVirtualKeyCode: 13 });
    await command("Input.dispatchKeyEvent", { type: "keyUp", key: "Enter", code: "Enter", windowsVirtualKeyCode: 13 });
    assert.equal(await wait("window.settingsShellFixture.sends.length===1"), true);
    assert.equal(await evaluate("settingsShellFixture.sends[0].text"), "", "actual App sends exactly empty text with typed images");
    assert.equal(await evaluate("settingsShellFixture.sends[0].images.length===1 && settingsShellFixture.sends[0].images[0].mediaType==='image/png' && settingsShellFixture.sends[0].images[0].title==='Submitted title'"), true);
    assert.equal(await evaluate("Object.isFrozen(settingsShellFixture.sends[0].images) && Object.isFrozen(settingsShellFixture.sends[0].images[0]) && document.querySelector('.prompt-image-attachments figure button').disabled"), true);
    await evaluate("window.editImageTitle('cannot mutate original')");
    assert.equal(await evaluate("document.querySelector('.prompt-image-attachments input').disabled && settingsShellFixture.sends[0].images[0].title==='Submitted title'"), true);
    await evaluate("window.pasteFixtureImage('#session-prompt')");
    assert.equal(await evaluate("settingsShellFixture.sends[0].images.length===1 && document.querySelectorAll('.prompt-image-attachments img').length===1"), true, "late paste cannot edit the admitted original");
    assert.deepEqual(await evaluate("window.settingsShellFixture.sends[0].selection"),
      { providerKey: "fixture", agentPromptId: "default", modelId: "new", reasoningEffort: "High" });
    await evaluate("document.querySelector('.project-rail .icon-label-button').click()");
    // Actual App raw Skills inspector; one explicit original, no scans on open/search/navigation.
    // Production localization while the typed image Send is still pending. Stable control identities,
    // not translated text, select pages; assertions verify actual translated names and visible labels.
    await evaluate("document.querySelector('[data-settings-section=appearance]').click(); window.languageWorkspace=document.querySelector('.workspace-shell'); window.languageComposer=document.querySelector('#session-prompt'); window.languageSend=settingsShellFixture.sends[0]; window.languageModal=document.querySelector('.settings-dialog'); true");
    const chooseLanguage = (locale: string) => evaluate(`(() => {const select=document.querySelector('#settings-language'); select.value=${JSON.stringify(locale)};select.dispatchEvent(new Event('change',{bubbles:true}));})()`);
    const workflowCalls = "[settingsShellFixture.rpcCalls,settingsShellFixture.creates,settingsShellFixture.renameRequests,settingsShellFixture.projectRenames,settingsShellFixture.archives,settingsShellFixture.sends,settingsShellFixture.promptCreates,readBatchCapability()?.canMutate()]";
    const verifyCreationLocaleRetention = async (message: string) => {
      await evaluate("window.localizedCreateOriginal=settingsShellFixture.promptCreates[0].request; window.localizedCreateAuthority=readBatchCapability().canMutate(); document.querySelector('[data-settings-section=appearance]').click(); window.creationLocaleReads=settingsShellFixture.rpcCalls.length");
      for (const [width, height, locale] of [[750, 485, "ja"], [390, 500, "es"], [1120, 800, "en"]] as const) {
        await command("Emulation.setDeviceMetricsOverride", { width, height, deviceScaleFactor: 1, mobile: false });
        await evaluate("document.querySelector('#settings-language').focus()");
        await chooseLanguage(locale);
        assert.equal(await wait(`innerWidth===${width} && innerHeight===${height} && document.documentElement.lang==='${locale}' && document.querySelector('.settings-dialog')?.open
          && document.querySelector('.settings-dialog').getBoundingClientRect().width<=${width} && document.querySelector('.settings-dialog').getBoundingClientRect().height<=${height}`), true);
        assert.equal(await evaluate("(()=>{document.querySelector('.session-tabs [role=tab]').focus();return document.activeElement===document.querySelector('#settings-language') && languageComposer===document.querySelector('#session-prompt') && document.querySelectorAll('.prompt-image-attachments img').length===1})()"), true,
          "short Settings and language transitions keep the original mounted image Send inert");
      }
      assert.equal(await evaluate("settingsShellFixture.rpcCalls.length===creationLocaleReads && settingsShellFixture.promptCreates[0].request===localizedCreateOriginal && readBatchCapability().canMutate()===localizedCreateAuthority && settingsShellFixture.sends[0]===languageSend"), true,
        "language changes retain original publication/Send and authority without a bridge call");
      await evaluate("document.querySelector('[data-settings-section=prompts]').click()");
      assert.equal(await wait(`document.querySelector('.prompt-creation')?.textContent.includes(${JSON.stringify(message)})`), true);
      assert.equal(await evaluate("document.querySelector('.prompt-creation textarea').value==='Create original body' && !document.querySelector('.prompt-creation button') && settingsShellFixture.promptCreates.length===1"), true);
    };
    for (const [locale, title, close, search, help] of [
      ["en", "Settings", "Close settings", "Search sessions", "Shortcuts"],
      ["es", "Configuración", "Cerrar configuración", "Buscar sesiones", "Atajos"],
      ["ja", "設定", "設定を閉じる", "セッションを検索", "ショートカット"],
      ["zh-CN", "设置", "关闭设置", "搜索会话", "快捷键"],
      ["de", "Einstellungen", "Einstellungen schließen", "Sitzungen suchen", "Tastenkombinationen"],
      ["fr", "Paramètres", "Fermer les paramètres", "Rechercher des sessions", "Raccourcis"],
    ]) {
      await evaluate("window.languageModal=document.querySelector('.settings-dialog'); window.languageRpcCount=settingsShellFixture.rpcCalls.length; window.languageScroll=document.querySelector('.timeline-scroll')?.scrollTop; document.querySelector('#settings-language').focus()");
      await chooseLanguage(locale);
      assert.equal(await wait(`document.documentElement.lang===${JSON.stringify(locale)}`), true);
      if (process.env.CODEALTA_TIMELINE_GEOMETRY) console.log("APP GEOMETRY", locale, JSON.stringify(await evaluate(timelineGeometryProbe)));
      assert.equal(await evaluate(`document.querySelector('#settings-title').textContent===${JSON.stringify(title)} && document.querySelector('.settings-dialog-header button').getAttribute('aria-label')===${JSON.stringify(close)} && document.querySelector('.session-rail .search input').getAttribute('aria-label')===${JSON.stringify(search)}`), true);
      assert.equal(await evaluate("document.activeElement.id==='settings-language' && languageModal===document.querySelector('.settings-dialog') && languageModal.open && languageWorkspace===document.querySelector('.workspace-shell') && languageComposer===document.querySelector('#session-prompt') && languageSend===settingsShellFixture.sends[0] && settingsShellFixture.sends.length===1 && languageSend.images.length===1 && document.querySelector('#session-prompt').value==='' && document.querySelector('.timeline-scroll')?.scrollTop===languageScroll && settingsShellFixture.rpcCalls.length===languageRpcCount"), true,
        `${locale}: live locale does not remount, lose focus/scroll/original image Send, or invoke any bridge method: ${await evaluate("JSON.stringify({focus:document.activeElement.id,modal:languageModal===document.querySelector('.settings-dialog')&&languageModal.open,workspace:languageWorkspace===document.querySelector('.workspace-shell'),composer:languageComposer===document.querySelector('#session-prompt'),send:languageSend===settingsShellFixture.sends[0],sends:settingsShellFixture.sends.length,images:languageSend.images.length,draft:document.querySelector('#session-prompt').value,scroll:document.querySelector('.timeline-scroll')?.scrollTop,beforeScroll:languageScroll,calls:settingsShellFixture.rpcCalls.length,beforeCalls:languageRpcCount})")}`);
      for (const width of [390, 1120]) for (const theme of ["light", "dark"]) {
        await command("Emulation.setDeviceMetricsOverride", { width, height: 800, deviceScaleFactor: 1, mobile: false });
        await evaluate(`document.documentElement.dataset.theme='${theme}'`);
        assert.equal(await evaluate("(()=>{const d=document.querySelector('.settings-dialog');const c=document.querySelector('.settings-dialog-content');return d.getBoundingClientRect().width<=innerWidth && d.getBoundingClientRect().height<=innerHeight && c.scrollWidth<=c.clientWidth+1})()"), true, `${locale} ${width} ${theme}: localized Settings fits`);
      }
      await evaluate("document.querySelector('#settings-language').dispatchEvent(new KeyboardEvent('keydown',{key:'Escape',isComposing:true,bubbles:true}));document.querySelector('.settings-dialog').dispatchEvent(new Event('cancel',{cancelable:true}));");
      assert.equal(await evaluate("document.querySelector('.settings-dialog').open"), true, "IME Escape retains the localized modal");
      await evaluate("document.querySelector('#settings-language').dispatchEvent(new CompositionEvent('compositionend',{bubbles:true}));document.querySelector('.settings-dialog-header button').click()");
      await evaluate("document.querySelector('.project-rail .icon-label-button').focus()");
      await command("Input.dispatchKeyEvent", { type: "keyDown", key: "F1", code: "F1", windowsVirtualKeyCode: 112 });
      assert.equal(await wait(`document.querySelector('#shortcut-title')?.textContent===${JSON.stringify(help)}`), true);
      assert.equal(await evaluate(`document.querySelector('.shortcut-dialog').textContent.includes('Ctrl+F11') && (document.querySelector('.shortcut-dialog dd').textContent==='Browse saved sessions (Ctrl+E remains reserved for TUI Edit File)')===${locale === "en"}`), true);
      await evaluate("document.querySelector('.shortcut-dialog header button').click()");
      assert.equal(await evaluate(`document.querySelector('.session-tabs [role=tab]').textContent===${JSON.stringify(translate(locale, "Prompt draft"))}`), true);
      assert.equal(await evaluate(`document.querySelector('.prompt-image-attachments input').getAttribute('aria-label')===${JSON.stringify(translate(locale, "Image title"))} && document.querySelector('.prompt-image-attachments figure button').textContent===${JSON.stringify(translate(locale, "Remove {title}", { title: "Submitted title" }))} && document.querySelector('.prompt-image-attachments details').textContent.includes(${JSON.stringify(translate(locale, "{count} image attached", { count: 1 }))})`), true);
      await evaluate(`document.querySelector('[aria-label=${JSON.stringify(translate(locale, "Open command palette"))}]').click()`);
      assert.equal(await wait(`document.querySelector('#palette-title')?.textContent===${JSON.stringify(translate(locale, "Command palette"))}`), true);
      for (const [id, label, alias] of [["skills", "Skills", "skill"], ["usage", "Inspect last-observed session usage", "context_usage"],
        ["openProject", "Open project", "open_project"], ["help", "Keyboard shortcuts", "help"]] as const) {
        for (const query of [translate(locale as Parameters<typeof translate>[0], label), alias]) {
          await evaluate(`(()=>{const input=document.querySelector('#palette-search');Object.getOwnPropertyDescriptor(HTMLInputElement.prototype,'value').set.call(input,${JSON.stringify(query)});input.dispatchEvent(new Event('input',{bubbles:true}))})()`);
          assert.equal(await wait(`!!document.querySelector('#palette-option-${id}')`), true, `${locale}: ${id} localized/canonical discovery`);
        }
      }
      for (const query of ["Focus session search", translate(locale, "Focus session search")]) {
        await evaluate(`(()=>{const input=document.querySelector('#palette-search');Object.getOwnPropertyDescriptor(HTMLInputElement.prototype,'value').set.call(input,${JSON.stringify(query)});input.dispatchEvent(new Event('input',{bubbles:true}))})()`);
        assert.equal(await wait("!!document.querySelector('#palette-option-focusSearch')"), true, `${locale}: canonical and translated search retain the action ID`);
      }
      await evaluate("document.querySelector('#palette-option-focusSearch').click()");
      assert.equal(await wait("!document.querySelector('.command-palette') && document.activeElement===document.querySelector('.session-rail .search input')"), true);
      assert.equal(await evaluate("languageComposer===document.querySelector('#session-prompt') && languageSend===settingsShellFixture.sends[0] && settingsShellFixture.sends.length===1 && settingsShellFixture.rpcCalls.length===languageRpcCount"), true, `${locale}: localized dispatch neither submits nor refreshes`);
      await evaluate("window.browseLocaleReads=JSON.stringify([settingsShellFixture.displayCalls.length,settingsShellFixture.probes.length,settingsShellFixture.choiceReads.length]);document.querySelector('.browse-sessions-button').click()");
      assert.equal(await wait(`document.querySelector('#session-browser-title')?.textContent===${JSON.stringify(translate(locale, "Browse saved sessions"))}`), true);
      assert.equal(await evaluate(`document.querySelector('.session-browser input').getAttribute('aria-label')===${JSON.stringify(translate(locale, "Find saved sessions"))}`), true);
      await evaluate("(()=>{const input=document.querySelector('.session-browser input');Object.getOwnPropertyDescriptor(HTMLInputElement.prototype,'value').set.call(input,'one');input.dispatchEvent(new Event('input',{bubbles:true}))})()");
      assert.equal(await wait("document.querySelectorAll('.session-browser-results [role=option]').length===1"), true);
      await evaluate("document.querySelector('.session-browser-results [role=option]').click()");
      assert.equal(await wait("!document.querySelector('.session-browser') && languageComposer===document.querySelector('#session-prompt')"), true);
      assert.equal(await evaluate("browseLocaleReads===JSON.stringify([settingsShellFixture.displayCalls.length,settingsShellFixture.probes.length,settingsShellFixture.choiceReads.length]) && settingsShellFixture.sends.length===1 && settingsShellFixture.sends[0]===languageSend"), true, `${locale}: translated browser searches/selects the existing session without attachment/provider work`);
      await evaluate("document.querySelector('.project-rail .icon-label-button').click()");
    }
    // Denied persistence applies only to this window and displays a translated notice.
    await evaluate("window.languageWrite=Storage.prototype.setItem;Storage.prototype.setItem=function(key,value){if(key==='codealta.desktop.language.v1')throw Error('denied');return languageWrite.call(this,key,value)}");
    await chooseLanguage("es");
    assert.equal(await wait("document.documentElement.lang==='es' && document.querySelector('[data-diagnostic=unsaved]')?.textContent.includes('no se pudo guardar')"), true);
    await evaluate("Storage.prototype.setItem=languageWrite");
    await chooseLanguage("en");
    assert.equal(await wait("document.documentElement.lang==='en' && !document.querySelector('[data-diagnostic=unsaved]')"), true);
    assert.equal(await evaluate("localStorage.getItem('codealta.desktop.language.v1')==='en' && document.querySelectorAll('#settings-language option').length===7"), true);
    await evaluate("window.skillsWorkspace=document.querySelector('.workspace-shell'); window.skillsComposer=document.querySelector('#session-prompt'); window.skillsSend=settingsShellFixture.sends[0]; true");
    await evaluate("[...document.querySelectorAll('.settings-dialog-navigation button')].find(x=>x.textContent==='Skills').click()");
    assert.equal(await wait("!!document.querySelector('.skills-inspection')"), true);
    assert.equal(await evaluate("settingsShellFixture.skillReads.length"), 0);
    const chooseSkillRoot = () => evaluate("(() => { const select=document.querySelector('[aria-label=\"Skill source root\"]'); select.value='project_alta'; select.dispatchEvent(new Event('change',{bubbles:true})); })()");
    await chooseSkillRoot();
    assert.equal(await wait("!document.querySelector('.skills-inspection > button').disabled"), true);
    await evaluate("document.querySelector('.skills-inspection > button').focus()");
    await command("Input.dispatchKeyEvent", { type: "keyDown", key: "Enter", code: "Enter", text: "\r", windowsVirtualKeyCode: 13 });
    await command("Input.dispatchKeyEvent", { type: "keyUp", key: "Enter", code: "Enter", windowsVirtualKeyCode: 13 });
    assert.equal(await wait("settingsShellFixture.skillReads.length===1"), true);
    await evaluate("settingsShellFixture.releaseSkills()");
    assert.equal(await wait("document.querySelectorAll('.skills-inspection li').length===2"), true);
    await evaluate("document.querySelector('.skills-inspection li button').click()");
    assert.equal(await wait("document.querySelector('[aria-label=\"Selected raw candidate metadata\"]')?.textContent.includes('Example raw metadata')"), true);
    for (const theme of ["light", "dark"]) {
      await command("Emulation.setDeviceMetricsOverride", { width: 390, height: 800, deviceScaleFactor: 1, mobile: false });
      await evaluate(`document.documentElement.dataset.theme='${theme}'`);
      assert.equal(await evaluate("(() => { const panel=document.querySelector('.skills-inspection'); return panel.scrollWidth<=panel.clientWidth+1 && document.querySelector('.settings-dialog').getBoundingClientRect().width<=390; })()"), true, `raw Skills narrow ${theme}`);
    }
    await command("Emulation.setDeviceMetricsOverride", { width: 1120, height: 800, deviceScaleFactor: 1, mobile: false });
    await evaluate("(() => {const input=document.querySelector('[aria-label=\"Search raw skill candidates\"]'); Object.getOwnPropertyDescriptor(HTMLInputElement.prototype,'value').set.call(input,'no-local-match'); input.dispatchEvent(new Event('input',{bubbles:true})); })()");
    assert.equal(await wait("document.querySelector('.skills-inspection')?.textContent.includes('No local search matches')"), true);
    assert.equal(await evaluate("settingsShellFixture.skillReads.length"), 1);
    // Close/open retains the original busy slot, but never its late metadata publication.
    await evaluate("document.querySelector('.skills-inspection > button').click()");
    assert.equal(await wait("settingsShellFixture.skillReads.length===2"), true);
    await evaluate("document.querySelector('[aria-label=\"Close settings\"]').click()");
    await evaluate("document.querySelector('.project-rail .icon-label-button').click()");
    await evaluate("[...document.querySelectorAll('.settings-dialog-navigation button')].find(x=>x.textContent==='Skills').click()");
    await chooseSkillRoot();
    assert.equal(await evaluate("document.querySelector('.skills-inspection > button').disabled && settingsShellFixture.skillReads.length===2"), true);
    await evaluate("settingsShellFixture.releaseSkills()");
    assert.equal(await wait("!document.querySelector('.skills-inspection > button').disabled && document.querySelectorAll('.skills-inspection li').length===0"), true);
    for (const cycle of ["cycleInfoSelection()", "cycleInfoHost()", "publishLayoutCatalog(settingsShellFixture.catalog)"]) {
      await evaluate("document.querySelector('.skills-inspection > button').click()");
      await evaluate(`window.${cycle}; settingsShellFixture.releaseSkills();`);
      assert.equal(await wait("!document.querySelector('.skills-inspection > button').disabled && document.querySelectorAll('.skills-inspection li').length===0"), true, cycle);
    }
    await evaluate("document.querySelector('.skills-inspection > button').click()");
    await evaluate("(() => { const select=document.querySelector('[aria-label=\"Skill source root\"]'); for(const value of ['user_alta','project_alta']) {select.value=value; select.dispatchEvent(new Event('change',{bubbles:true}));} settingsShellFixture.releaseSkills(); })()");
    assert.equal(await wait("!document.querySelector('.skills-inspection > button').disabled && document.querySelectorAll('.skills-inspection li').length===0"), true, "root ABA discards the original metadata");
    await evaluate("document.querySelector('.skills-inspection > button').click()");
    await evaluate("(() => { const input=document.querySelector('[aria-label=\"Search raw skill candidates\"]'); for(const value of ['changed','']) { Object.getOwnPropertyDescriptor(HTMLInputElement.prototype,'value').set.call(input,value); input.dispatchEvent(new Event('input',{bubbles:true})); } settingsShellFixture.releaseSkills(); })()");
    assert.equal(await wait("!document.querySelector('.skills-inspection > button').disabled && document.querySelectorAll('.skills-inspection li').length===0"), true, "input ABA discards the original metadata");
    for (const mode of ["empty", "unknown"]) {
      await evaluate("document.querySelector('.skills-inspection > button').click()");
      await evaluate(`settingsShellFixture.releaseSkills(undefined,'${mode}')`);
      assert.equal(await wait("!document.querySelector('.skills-inspection > button').disabled && document.querySelectorAll('.skills-inspection li').length===0"), true);
      assert.equal(await evaluate(`document.querySelector('.skills-inspection').textContent.includes('${mode === "empty" ? "zero rows never proves absent" : "metadata_unavailable"}')`), true);
    }
    await evaluate("document.querySelector('.skills-inspection > button').click(); settingsShellFixture.releaseSkills(undefined,'error')");
    assert.equal(await wait("document.querySelector('.skills-inspection')?.textContent.includes('outcome unknown') && document.querySelector('.skills-inspection > button').disabled"), true);
    assert.equal(await evaluate("window.skillsWorkspace===document.querySelector('.workspace-shell') && window.skillsComposer===document.querySelector('#session-prompt') && window.skillsSend===settingsShellFixture.sends[0] && settingsShellFixture.sends.length===1 && document.querySelector('#session-prompt').value===''"), true,
      "inspection leaves workspace, draft and immutable pending typed Send untouched");
    await evaluate("[...document.querySelectorAll('.settings-dialog-navigation button')].find(x=>x.textContent==='Models').click()");
    assert.equal(await wait("!!document.querySelector('.model-catalog-providers button')"), true);
    await evaluate("document.querySelector('.model-catalog-providers button').click()");
    assert.equal(await wait("!![...document.querySelectorAll('.model-catalog-results button')].find(x=>x.textContent.includes('Old'))"), true);
    await evaluate("[...document.querySelectorAll('.model-catalog-results button')].find(x=>x.textContent.includes('Old')).click()");
    assert.equal(await wait("!![...document.querySelectorAll('.model-catalog-detail button')].find(x=>x.textContent==='Use model for next Send')"), true);
    assert.equal(await evaluate("[...document.querySelectorAll('.model-catalog-detail button')].find(x=>x.textContent==='Use model for next Send').disabled && window.settingsShellFixture.sends.length===1"), true,
      "pending exact Send also excludes model Apply");
    await evaluate("[...document.querySelectorAll('.settings-dialog-navigation button')].find(x=>x.textContent==='Agent prompts').click()");
    assert.equal(await wait("!!document.querySelector('.prompt-catalog-page .model-catalog-providers button')"), true);
    await evaluate("document.querySelector('.prompt-catalog-page .model-catalog-providers button').click()");
    assert.equal(await wait("!!document.querySelector('.prompt-catalog-page .model-catalog-next button')"), true);
    assert.equal(await evaluate("document.querySelector('.prompt-catalog-page .model-catalog-next button').disabled"), true,
      "the pending exact Send cannot be replaced by a prompt Apply");
    // Actual App create-only authoring, with the existing typed image Send still retained.
    await evaluate("document.querySelector('.prompt-creation > button').click()");
    assert.equal(await wait("!!document.querySelector('.prompt-creation textarea')"), true);
    await evaluate(`(() => { const panel=document.querySelector('.prompt-creation');
      const inputs=panel.querySelectorAll('input:not([type=checkbox])');
      for(const [index,value] of ['example','Example','Description'].entries()) {
        Object.getOwnPropertyDescriptor(HTMLInputElement.prototype,'value').set.call(inputs[index],value); inputs[index].dispatchEvent(new Event('input',{bubbles:true})); }
      const body=panel.querySelector('textarea'); Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype,'value').set.call(body,'Create original body'); body.dispatchEvent(new Event('input',{bubbles:true}));
    })()`);
    await evaluate("document.querySelector('[aria-label=\"Close settings\"]').click()");
    await evaluate("document.querySelector('.project-rail .icon-label-button').click()");
    await evaluate("[...document.querySelectorAll('.settings-dialog-navigation button')].find(x=>x.textContent==='Agent prompts').click()");
    assert.equal(await wait("document.querySelector('.prompt-creation textarea')?.value==='Create original body'"), true);
    assert.equal(await evaluate("settingsShellFixture.promptCreates.length"), 0);
    await evaluate(`(() => { const selects=document.querySelectorAll('.prompt-creation select');
      for(const [index,value] of ['project_alta','append'].entries()) { selects[index].value=value; selects[index].dispatchEvent(new Event('change',{bubbles:true})); } })()`);
    await evaluate("document.querySelector('.prompt-creation input[type=checkbox]').click()");
    const clickPrompt = (label: string) => evaluate(`[...document.querySelectorAll('.prompt-creation button')].find(x=>x.textContent===${JSON.stringify(label)}).click()`);
    await clickPrompt("Review creation");
    await evaluate("window.cycleInfoSelection()");
    await clickPrompt("Confirm create only");
    assert.equal(await evaluate("settingsShellFixture.promptCreates.length"), 0, "same-value selected-session ABA cannot dispatch reviewed authoring");
    await clickPrompt("Back to draft"); await clickPrompt("Review creation");
    for (const theme of ["light", "dark"]) {
      await command("Emulation.setDeviceMetricsOverride", { width: 390, height: 800, deviceScaleFactor: 1, mobile: false });
      await evaluate(`document.documentElement.dataset.theme='${theme}'`);
      assert.equal(await evaluate("(()=>{const p=document.querySelector('.prompt-creation');return p.scrollWidth<=p.clientWidth+1})()"), true, `prompt authoring narrow ${theme}`);
    }
    await command("Emulation.setDeviceMetricsOverride", { width: 1120, height: 800, deviceScaleFactor: 1, mobile: false });
    await evaluate("[...document.querySelectorAll('.prompt-creation button')].find(x=>x.textContent==='Confirm create only').focus()");
    await command("Input.dispatchKeyEvent", { type: "keyDown", key: "Enter", code: "Enter", text: "\r", windowsVirtualKeyCode: 13 });
    await command("Input.dispatchKeyEvent", { type: "keyUp", key: "Enter", code: "Enter", windowsVirtualKeyCode: 13 });
    assert.equal(await wait("settingsShellFixture.promptCreates.length===1"), true);
    await verifyCreationLocaleRetention("Original creation pending");
    await evaluate("window.promptCreateSnapshots=settingsShellFixture.snapshotCalls.length; window.originalPromptCapability=readBatchCapability(); document.querySelector('[aria-label=\"Close settings\"]').click()");
    await evaluate("(() => {const {request,resolve}=settingsShellFixture.promptCreates[0];resolve({status:'created',hostEpoch:'22222222-2222-4222-8222-222222222222',request});})()");
    await evaluate("document.querySelector('.project-rail .icon-label-button').click()");
    await evaluate("[...document.querySelectorAll('.settings-dialog-navigation button')].find(x=>x.textContent==='Agent prompts').click()");
    assert.equal(await wait("document.querySelector('.prompt-creation')?.textContent.includes('Publication outcome uncertain')"), true);
    assert.equal(await evaluate("readBatchCapability()===originalPromptCapability && !originalPromptCapability.canMutate()"), true,
      "changed responding host disables unrelated old-host mutation authority without certifying publication");
    assert.equal(await evaluate("document.querySelector('.prompt-creation textarea').value==='Create original body' && !document.querySelector('.prompt-creation button') && settingsShellFixture.promptCreates.length===1 && settingsShellFixture.snapshotCalls.length===window.promptCreateSnapshots"), true);
    assert.equal(await evaluate("window.skillsWorkspace===document.querySelector('.workspace-shell') && window.skillsComposer===document.querySelector('#session-prompt') && window.skillsSend===settingsShellFixture.sends[0] && settingsShellFixture.sends.length===1 && settingsShellFixture.sends[0].images.length===1 && document.querySelector('#session-prompt').value===''"), true,
      "create uncertainty preserves workspace DOM, composer and original image Send without apply or refresh");
    await verifyCreationLocaleRetention("Publication outcome uncertain");
    // Inventory bodies inside the actual native App modal, while both original image Send
    // and uncertain prompt publication remain owned by their original callers.
    for (const locale of ["de", "ja"] as const) for (const theme of ["light", "dark"]) {
      await evaluate("document.querySelector('[data-settings-section=appearance]').click()");
      await chooseLanguage(locale);
      await command("Emulation.setDeviceMetricsOverride", { width: 390, height: 500, deviceScaleFactor: 1, mobile: false });
      await evaluate(`document.documentElement.dataset.theme='${theme}'`);
      for (const [section, title] of [["providers", "Provider management"], ["models", "Model catalog"], ["prompts", "Agent prompts"], ["mcp", "MCP Servers"]] as const) {
        await evaluate(`document.querySelector('[data-settings-section=${section}]').click()`);
        assert.equal(await wait(`document.querySelector('.settings-dialog main')?.getAttribute('aria-label')===${JSON.stringify(translate(locale, title))}`), true);
        await evaluate("new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(()=>resolve(true))))");
        await evaluate("window.inventoryAppCalls=settingsShellFixture.rpcCalls.length;window.inventoryAppModal=document.querySelector('.settings-dialog');document.querySelector('.settings-dialog-navigation button').focus()");
        assert.equal(await evaluate("(()=>{const d=document.querySelector('.settings-dialog'),c=document.querySelector('.settings-dialog-content'),r=d.getBoundingClientRect();return d.open&&r.width<=innerWidth&&r.height<=innerHeight&&c.scrollWidth<=c.clientWidth+1})()"), true, `${locale}/${theme}/${section}: modal inventory fits`);
        await command("Input.dispatchKeyEvent", { type: "keyDown", key: "Tab", code: "Tab", windowsVirtualKeyCode: 9 });
        await command("Input.dispatchKeyEvent", { type: "keyUp", key: "Tab", code: "Tab", windowsVirtualKeyCode: 9 });
        await evaluate("document.activeElement.dispatchEvent(new KeyboardEvent('keydown',{key:'Escape',isComposing:true,bubbles:true}));document.querySelector('.settings-dialog').dispatchEvent(new Event('cancel',{cancelable:true}));document.activeElement.dispatchEvent(new CompositionEvent('compositionend',{bubbles:true}))");
        assert.equal(await evaluate("inventoryAppModal===document.querySelector('.settings-dialog')&&inventoryAppModal.open&&inventoryAppModal.contains(document.activeElement)&&settingsShellFixture.rpcCalls.length===inventoryAppCalls&&languageWorkspace===document.querySelector('.workspace-shell')&&languageComposer===document.querySelector('#session-prompt')&&languageSend===settingsShellFixture.sends[0]&&settingsShellFixture.sends.length===1&&languageSend.images.length===1&&settingsShellFixture.promptCreates.length===1&&readBatchCapability()===originalPromptCapability&&!originalPromptCapability.canMutate()"), true,
          `${locale}/${theme}/${section}: focus/IME preserve original image Send and uncertain publication without calls`);
      }
    }
    await evaluate("document.querySelector('[data-settings-section=appearance]').click()");
    await chooseLanguage("en");
    await command("Emulation.setDeviceMetricsOverride", { width: 1120, height: 800, deviceScaleFactor: 1, mobile: false });
    await evaluate("document.querySelector('[aria-label=\"Close settings\"]').click()");
    await evaluate("localStorage.setItem('codealta.desktop.language.v1','malformed-locale')");
    await command("Page.reload"); // Fresh isolated fake instance; the previous retained Send was not retried.
    assert.equal(await wait("document.querySelector('.owned-session [aria-label=\"Model\"]')?.value==='new'"), true);
    await evaluate("window.promptComposer=document.querySelector('#session-prompt'); true");
    await evaluate("document.querySelector('.project-rail .icon-label-button').click()");
    assert.equal(await wait("document.querySelector('[data-diagnostic=invalid]')?.textContent.includes('Language: invalid saved preference')"), true);
    assert.equal(await evaluate("document.documentElement.lang==='en' && localStorage.getItem('codealta.desktop.language.v1')==='malformed-locale'"), true, "malformed saved language is not auto-overwritten");
    await chooseLanguage("en");
    await evaluate("[...document.querySelectorAll('.settings-dialog-navigation button')].find(x=>x.textContent==='Agent prompts').click()");
    assert.equal(await wait("!!document.querySelector('.prompt-catalog-page .model-catalog-providers button')"), true);
    await evaluate("document.querySelector('.prompt-creation > button').click()");
    await evaluate(`(() => { const p=document.querySelector('.prompt-creation'); const inputs=p.querySelectorAll('input:not([type=checkbox])');
      for(const [index,value] of ['created-example','Created example',''].entries()) { Object.getOwnPropertyDescriptor(HTMLInputElement.prototype,'value').set.call(inputs[index],value); inputs[index].dispatchEvent(new Event('input',{bubbles:true})); }
      const body=p.querySelector('textarea'); Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype,'value').set.call(body,'Complete created body'); body.dispatchEvent(new Event('input',{bubbles:true}));
      for(const [index,value] of ['user_alta','replace'].entries()) {const select=p.querySelectorAll('select')[index];select.value=value;select.dispatchEvent(new Event('change',{bubbles:true}));} })()`);
    await evaluate("document.querySelector('.prompt-creation input[type=checkbox]').click()");
    await clickPrompt("Review creation"); await clickPrompt("Confirm create only");
    assert.equal(await wait("settingsShellFixture.promptCreates.length===1"), true);
    await evaluate("window.beforePromptCreationReads=settingsShellFixture.promptReads.length; window.beforePromptCreationSnapshots=settingsShellFixture.snapshotCalls.length; document.querySelector('[aria-label=\"Close settings\"]').click()");
    await evaluate("(() => {const {request,resolve}=settingsShellFixture.promptCreates[0];resolve({status:'created',hostEpoch:request.expectedHostEpoch,request});})()");
    assert.equal(await evaluate("settingsShellFixture.promptReads.length===window.beforePromptCreationReads && settingsShellFixture.snapshotCalls.length===window.beforePromptCreationSnapshots && window.promptComposer===document.querySelector('#session-prompt') && settingsShellFixture.sends.length===0"), true,
      "late successful creation does not refresh, apply, send or replace the workspace");
    await evaluate("document.querySelector('.project-rail .icon-label-button').click()");
    await evaluate("[...document.querySelectorAll('.settings-dialog-navigation button')].find(x=>x.textContent==='Agent prompts').click()");
    assert.equal(await wait("document.querySelector('.prompt-creation')?.textContent.includes('Created at the original scope')"), true);
    assert.equal(await evaluate("document.querySelector('.prompt-creation textarea').value==='Complete created body' && settingsShellFixture.promptCreates.length===1"), true);
    assert.equal(await wait("!!document.querySelector('.prompt-catalog-page .model-catalog-providers button')"), true);
    await evaluate("document.querySelector('.prompt-catalog-page .model-catalog-providers button').click()");
    assert.equal(await wait("!!document.querySelector('.prompt-catalog-page .model-catalog-next button:not(:disabled)')"), true);
    await evaluate("document.querySelector('.prompt-catalog-page .model-catalog-next button').click()");
    assert.equal(await wait("!document.querySelector('.settings-dialog') && document.querySelector('.owned-session [aria-label=\"Agent prompt\"]')?.value==='plan'"), true);
    assert.equal(await evaluate("document.querySelector('.owned-session [aria-label=\"Model\"]')?.value==='new' && document.querySelector('.owned-session [aria-label=\"Reasoning\"]')?.value==='High'"), true);
    assert.equal(await evaluate("window.promptComposer===document.querySelector('#session-prompt')"), true);
    await evaluate(`(() => {const input=document.querySelector('#session-prompt');
      Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype,'value').set.call(input,'Prompt test draft');
      input.dispatchEvent(new Event('input',{bubbles:true})); })()`);
    assert.equal(await wait("document.querySelector('#session-prompt').value==='Prompt test draft'"), true);
    await evaluate("document.querySelector('.owned-session .send-button').click()");
    assert.equal(await wait("window.settingsShellFixture.sends.length===1"), true);
    assert.deepEqual(await evaluate("window.settingsShellFixture.sends[0].selection"),
      { providerKey: "fixture", agentPromptId: "plan", modelId: "new", reasoningEffort: "High" });
    await command("Page.reload");
    assert.equal(await wait("document.querySelector('.owned-session [aria-label=\"Reasoning\"]')?.value==='High'"), true);
    await evaluate("document.querySelector('.project-rail .icon-label-button').click()");
    await evaluate("[...document.querySelectorAll('.settings-dialog-navigation button')].find(x=>x.textContent==='Providers').click()");
    assert.equal(await wait("!!document.querySelector('.model-catalog-providers button')"), true);
    await evaluate("document.querySelector('.model-catalog-providers button').click()");
    assert.equal(await wait("!!document.querySelector('.model-catalog-detail button')"), true);
    await evaluate("document.querySelector('.model-catalog-detail button').click()");
    assert.equal(await wait("window.settingsShellFixture.probes.length===1"), true);
    await evaluate("[...document.querySelectorAll('.settings-dialog-navigation button')].find(x=>x.textContent==='Application Logs').click()");
    await evaluate("[...document.querySelectorAll('.settings-dialog-navigation button')].find(x=>x.textContent==='Providers').click()");
    assert.equal(await wait("!!document.querySelector('.model-catalog-providers button')"), true);
    await evaluate("document.querySelector('.model-catalog-providers button').click()");
    assert.equal(await wait("document.querySelector('.model-catalog-detail button')?.disabled"), true);
    await evaluate("document.querySelector('[aria-label=\"Close settings\"]').click()");
    await evaluate("document.querySelector('.project-rail .icon-label-button').click()");
    await evaluate("[...document.querySelectorAll('.settings-dialog-navigation button')].find(x=>x.textContent==='Providers').click()");
    assert.equal(await wait("!!document.querySelector('.model-catalog-providers button')"), true);
    await evaluate("document.querySelector('.model-catalog-providers button').click()");
    assert.equal(await wait("document.querySelector('.model-catalog-detail button')?.disabled && window.settingsShellFixture.probes.length===1"), true);
    await evaluate("localStorage.setItem('settingsFixtureLogsOk','true'); [...document.querySelectorAll('.settings-dialog-navigation button')].find(x=>x.textContent==='Application Logs').click()");
    await evaluate("document.querySelector('.logs-toolbar button').click()");
    assert.equal(await wait("!!document.querySelector('.logs-rows')"), true);
    await evaluate("[...document.querySelectorAll('.logs-toolbar button')].find(x=>x.textContent.includes('Clear captured')).click()");
    await evaluate(`(() => {const input=document.querySelector('.logs-confirm input');
      Object.getOwnPropertyDescriptor(HTMLInputElement.prototype,'value').set.call(input,'CLEAR CAPTURED LOGS');
      input.dispatchEvent(new Event('input',{bubbles:true})); })()`);
    assert.equal(await wait("document.querySelector('.logs-confirm button[type=submit]')?.disabled===false"), true);
    await evaluate("document.querySelector('.logs-confirm button[type=submit]').click()");
    assert.equal(await wait("window.settingsShellFixture.clearRequests.length===1"), true);
    await evaluate("[...document.querySelectorAll('.settings-dialog-navigation button')].find(x=>x.textContent==='Providers').click()");
    await evaluate("[...document.querySelectorAll('.settings-dialog-navigation button')].find(x=>x.textContent==='Application Logs').click()");
    assert.equal(await wait("document.querySelector('.logs-clear-status')?.textContent.includes('pending')"), true);
    assert.equal(await evaluate("window.settingsShellFixture.clearRequests.length"), 1);
    await evaluate("document.querySelector('[aria-label=\"Close settings\"]').click()");
    await evaluate("document.querySelector('.project-rail .icon-label-button').click()");
    await evaluate("[...document.querySelectorAll('.settings-dialog-navigation button')].find(x=>x.textContent==='Application Logs').click()");
    assert.equal(await wait("document.querySelector('.logs-clear-status')?.textContent.includes('pending') && window.settingsShellFixture.clearRequests.length===1"), true);
    // An in-flight Apply may not commit into a different Settings section, a closed overlay, or another session.
    await evaluate("[...document.querySelectorAll('.settings-dialog-navigation button')].find(x=>x.textContent==='Models').click()");
    const selectOld = "[...document.querySelectorAll('.model-catalog-results button')].find(x=>x.textContent.includes('Old')).click()";
    const applyModel = "[...document.querySelectorAll('.model-catalog-detail button')].find(x=>x.textContent==='Use model for next Send').click()";
    async function chooseOld() {
      await evaluate("document.querySelector('.model-catalog-providers button').click()");
      assert.equal(await wait("!![...document.querySelectorAll('.model-catalog-results button')].find(x=>x.textContent.includes('Old'))"), true);
      await evaluate(selectOld);
    }
    await chooseOld();
    assert.equal(await wait("!![...document.querySelectorAll('.model-catalog-detail button')].find(x=>x.textContent==='Use model for next Send' && !x.disabled)"), true);
    await evaluate("localStorage.setItem('settingsFixtureHoldChoices','true')");
    await evaluate(applyModel);
    assert.equal(await wait("window.settingsShellFixture.choiceReads.length===1"), true);
    await evaluate("[...document.querySelectorAll('.settings-dialog-navigation button')].find(x=>x.textContent==='Agent prompts').click()");
    await evaluate("window.settingsShellFixture.releaseChoices()");
    assert.equal(await evaluate("document.querySelector('.owned-session [aria-label=\"Model\"]').value"), "new");
    await evaluate("localStorage.removeItem('settingsFixtureHoldChoices'); [...document.querySelectorAll('.settings-dialog-navigation button')].find(x=>x.textContent==='Models').click()");
    await chooseOld();
    assert.equal(await wait("!![...document.querySelectorAll('.model-catalog-detail button')].find(x=>x.textContent==='Use model for next Send' && !x.disabled)"), true);
    await evaluate("localStorage.setItem('settingsFixtureHoldChoices','true')");
    await evaluate(applyModel);
    assert.equal(await wait("window.settingsShellFixture.choiceReads.length===1"), true);
    await evaluate("document.querySelector('[aria-label=\"Close settings\"]').click()");
    await evaluate("window.settingsShellFixture.releaseChoices()");
    assert.equal(await evaluate("document.querySelector('.owned-session [aria-label=\"Model\"]').value"), "new");
    await evaluate("localStorage.removeItem('settingsFixtureHoldChoices'); document.querySelector('.project-rail .icon-label-button').click()");
    await evaluate("[...document.querySelectorAll('.settings-dialog-navigation button')].find(x=>x.textContent==='Models').click()");
    await chooseOld();
    assert.equal(await wait("!![...document.querySelectorAll('.model-catalog-detail button')].find(x=>x.textContent==='Use model for next Send' && !x.disabled)"), true);
    await evaluate("localStorage.setItem('settingsFixtureHoldChoices','true')");
    await evaluate(applyModel);
    assert.equal(await wait("window.settingsShellFixture.choiceReads.length===1"), true);
    await evaluate("document.querySelector('[aria-label=\"Close settings\"]').click()");
    await evaluate("[...document.querySelectorAll('.session-row > button:first-child')].find(x=>x.textContent.includes('two')).click()");
    await evaluate("window.settingsShellFixture.releaseChoices()");
    assert.equal(await wait("document.querySelector('.owned-session [aria-label=\"Model\"]')?.value==='old'"), true);
    await evaluate("localStorage.removeItem('settingsFixtureHoldChoices'); [...document.querySelectorAll('.session-row > button:first-child')].find(x=>x.textContent.includes('one')).click()");
    assert.equal(await wait("document.querySelector('.owned-session [aria-label=\"Model\"]')?.value==='new'"), true);
    await evaluate("document.querySelector('.project-rail .icon-label-button').click()");
    await evaluate("[...document.querySelectorAll('.settings-dialog-navigation button')].find(x=>x.textContent==='Models').click()");
    await chooseOld();
    assert.equal(await wait("!![...document.querySelectorAll('.model-catalog-detail button')].find(x=>x.textContent==='Use model for next Send' && !x.disabled)"), true);
    await evaluate("localStorage.setItem('settingsFixtureHoldChoices','true')");
    await evaluate(applyModel);
    assert.equal(await wait("window.settingsShellFixture.choiceReads.length===1"), true);
    await evaluate("window.settingsShellFixture.releaseChoices('different')");
    assert.equal(await wait("document.querySelector('.model-catalog-detail [role=alert]')?.textContent.includes('changed')"), true);
    assert.equal(await evaluate("document.querySelector('.owned-session [aria-label=\"Model\"]').value"), "new");
    await evaluate("localStorage.removeItem('settingsFixtureHoldChoices'); document.querySelector('.model-catalog-providers button').click()");
    await chooseOld();
    assert.equal(await wait("!![...document.querySelectorAll('.model-catalog-detail button')].find(x=>x.textContent==='Use model for next Send' && !x.disabled)"), true);
    await evaluate("localStorage.setItem('settingsFixtureHoldChoices','true')");
    await evaluate(applyModel);
    assert.equal(await wait("window.settingsShellFixture.choiceReads.length===1"), true);
    await evaluate("window.settingsShellFixture.releaseChoices('stale')");
    assert.equal(await wait("document.querySelector('.model-catalog-detail [role=alert]')?.textContent.includes('changed')"), true);
    assert.equal(await evaluate("document.querySelector('.owned-session [aria-label=\"Model\"]').value"), "new");
    // A project selection is an explicit navigation out of Settings, unlike switching pages or sessions.
    await evaluate("localStorage.setItem('settingsFixtureSecondProject','true')");
    await command("Page.reload");
    assert.equal(await wait("document.querySelector('.session-header h1')?.textContent==='one' && !!document.querySelector('#project-list button[title=\"/fixture/other\"]')"), true);
    await evaluate("document.querySelector('.project-rail .icon-label-button').click()");
    assert.equal(await wait("document.querySelector('.settings-dialog')?.open"), true);
    await evaluate("document.querySelector('#project-list button[title=\"/fixture/other\"]').dispatchEvent(new MouseEvent('click',{bubbles:true}))");
    assert.equal(await wait("!document.querySelector('.settings-dialog') && document.querySelector('.session-header h1')?.textContent==='other-session'"), true);
    await evaluate("document.querySelector('.project-rail .icon-label-button').click()");
    assert.equal(await wait("document.querySelector('.settings-dialog')?.open && document.querySelector('.settings-dialog-navigation [aria-current=page]')?.textContent==='Appearance'"), true);
    await evaluate("[...document.querySelectorAll('.settings-dialog-navigation button')].find(x=>x.textContent==='Providers').click()");
    assert.equal(await wait("document.querySelector('.settings-dialog-content .settings-card')?.textContent.includes('Current session provider: other-provider') && document.querySelector('.settings-dialog-navigation [aria-current=page]')?.textContent==='Providers'"), true);
    assert.equal(await evaluate("window.settingsShellFixture.sends.length===0 && window.settingsShellFixture.probes.length===0"), true);
    await evaluate("document.querySelector('[aria-label=\"Close settings\"]').click()");
    assert.equal(await wait("!document.querySelector('.settings-dialog')"), true);
    // The row action entry is a single, right-aligned icon, not three competing buttons.
    assert.equal(await evaluate(`(() => {const row=document.querySelector('.session-row'); const trigger=row.querySelector('.session-actions-trigger');
      const title=row.querySelector(':scope > button:first-child'); const r=trigger.getBoundingClientRect();
      return row.querySelectorAll(':scope > button').length===2 && trigger.querySelector('svg') && !trigger.textContent.trim() &&
        trigger.getAttribute('aria-label')==='Actions for other-session (ID: other-session)' && r.left>=title.getBoundingClientRect().right-1 && r.width<=32;})()`), true);
    await evaluate("document.querySelector('.session-actions-trigger').focus()");
    assert.equal(await evaluate("document.activeElement?.classList.contains('session-actions-trigger')"), true);
    await command("Input.dispatchKeyEvent", { type: "keyDown", key: " ", code: "Space", text: " ", windowsVirtualKeyCode: 32 });
    await command("Input.dispatchKeyEvent", { type: "keyUp", key: " ", code: "Space", windowsVirtualKeyCode: 32 });
    assert.equal(await wait("document.querySelector('.session-actions-menu') && document.activeElement?.textContent==='Open session'"), true);
    assert.equal(await evaluate("document.querySelector('.session-header h1').textContent==='other-session' && window.settingsShellFixture.renameRequests.length===0 && window.settingsShellFixture.deleteRequests.length===0"), true);
    await command("Input.dispatchKeyEvent", { type: "keyDown", key: "ArrowDown", code: "ArrowDown", windowsVirtualKeyCode: 40 });
    await command("Input.dispatchKeyEvent", { type: "keyUp", key: "ArrowDown", code: "ArrowDown", windowsVirtualKeyCode: 40 });
    assert.equal(await evaluate("document.activeElement?.textContent==='Rename…'"), true);
    await command("Input.dispatchKeyEvent", { type: "keyDown", key: "End", code: "End", windowsVirtualKeyCode: 35 });
    await command("Input.dispatchKeyEvent", { type: "keyUp", key: "End", code: "End", windowsVirtualKeyCode: 35 });
    assert.equal(await evaluate("document.activeElement?.textContent.includes('Delete…')"), true);
    await command("Input.dispatchKeyEvent", { type: "keyDown", key: "Escape", code: "Escape", windowsVirtualKeyCode: 27 });
    await command("Input.dispatchKeyEvent", { type: "keyUp", key: "Escape", code: "Escape", windowsVirtualKeyCode: 27 });
    assert.equal(await wait("!document.querySelector('.session-actions-menu') && document.activeElement===document.querySelector('.session-actions-trigger')"), true);
    await evaluate("document.querySelector('.session-actions-trigger').click()");
    await command("Input.dispatchKeyEvent", { type: "keyDown", key: "End", code: "End", windowsVirtualKeyCode: 35 });
    await command("Input.dispatchKeyEvent", { type: "keyUp", key: "End", code: "End", windowsVirtualKeyCode: 35 });
    await command("Input.dispatchKeyEvent", { type: "keyDown", key: "Tab", code: "Tab", windowsVirtualKeyCode: 9 });
    await command("Input.dispatchKeyEvent", { type: "keyUp", key: "Tab", code: "Tab", windowsVirtualKeyCode: 9 });
    assert.equal(await wait("!document.querySelector('.session-actions-menu') && document.activeElement!==document.querySelector('.session-actions-trigger')"), true,
      "Tab leaves the menu using normal browser focus order without restoring its trigger");
    await evaluate("document.querySelector('.session-actions-trigger').click()");
    assert.equal(await wait("!!document.querySelector('.session-actions-menu')"), true);
    await evaluate("document.dispatchEvent(new PointerEvent('pointerdown',{bubbles:true}))");
    assert.equal(await wait("!document.querySelector('.session-actions-menu')"), true);
    for (const width of [390, 1120]) for (const theme of ["light", "dark"]) {
      await command("Emulation.setDeviceMetricsOverride", { width, height: 800, deviceScaleFactor: 1, mobile: false });
      await evaluate(`document.documentElement.dataset.theme='${theme}'; document.querySelector('.session-actions-trigger').click()`);
      assert.equal(await wait("!!document.querySelector('.session-actions-menu')"), true);
      assert.equal(await evaluate(`(() => {const row=document.querySelector('.session-row').getBoundingClientRect();
        const icon=document.querySelector('.session-actions-trigger').getBoundingClientRect();
        const menu=document.querySelector('.session-actions-menu').getBoundingClientRect();
        return icon.right<=row.right+1 && icon.width<=32 && menu.right<=${width} && menu.left>=0 && menu.width<=row.width && menu.bottom<=800;
      })()`), true);
      await evaluate("document.querySelector('.session-actions-trigger').click()");
      assert.equal(await wait("!document.querySelector('.session-actions-menu')"), true);
    }
    await command("Emulation.setDeviceMetricsOverride", { width: 1120, height: 800, deviceScaleFactor: 1, mobile: false });
    await evaluate("document.querySelector('#project-list button[title=\"/fixture/project\"]').click()");
    assert.equal(await wait("document.querySelector('.session-header h1')?.textContent==='one'"), true);
    await evaluate("[...document.querySelectorAll('.session-row')].find(row=>row.textContent.includes('two')).dispatchEvent(new MouseEvent('contextmenu',{bubbles:true,cancelable:true}))");
    assert.equal(await wait("document.querySelector('.session-actions-menu') && document.querySelector('.session-header h1')?.textContent==='one'"), true);
    await evaluate("document.querySelector('.session-actions-menu').dispatchEvent(new KeyboardEvent('keydown',{key:'Escape',bubbles:true,cancelable:true}))");
    assert.equal(await wait("!document.querySelector('.session-actions-menu')"), true);
    await evaluate("[...document.querySelectorAll('.session-row')].find(row=>row.textContent.includes('two')).querySelector('.session-actions-trigger').click()");
    assert.equal(await wait("document.querySelector('.session-actions-menu') && document.querySelector('.session-header h1')?.textContent==='one'"), true,
      "opening another row's menu must not change the selection");
    await evaluate("document.querySelector('.session-actions-menu [role=menuitem]').click()");
    assert.equal(await wait("document.querySelector('.session-header h1')?.textContent==='two' && !document.querySelector('.session-actions-menu')"), true);
    await evaluate("[...document.querySelectorAll('.session-row')].find(row=>row.textContent.includes('one')).querySelector('.session-actions-trigger').click()");
    assert.equal(await wait("document.querySelector('.session-actions-menu') && document.querySelector('.session-header h1')?.textContent==='two'"), true);
    await evaluate("document.querySelector('.session-row:nth-child(1) > button:first-child').click()");
    assert.equal(await wait("!document.querySelector('.session-actions-menu') && document.querySelector('.session-header h1')?.textContent==='one'"), true,
      "switching the selected session dismisses a different row's menu without admitting its actions");
    await evaluate("document.querySelector('.session-row:nth-child(2) > button:first-child').click()");
    assert.equal(await wait("document.querySelector('.session-header h1')?.textContent==='two'"), true);
    await evaluate("[...document.querySelectorAll('.session-row')].find(row=>row.textContent.includes('one')).querySelector('.session-actions-trigger').click()");
    await evaluate("[...document.querySelectorAll('.session-actions-menu button')].find(button=>button.textContent==='Rename…').click()");
    assert.equal(await wait("document.querySelector('.session-header h1')?.textContent==='one' && document.activeElement===document.querySelector('.session-rename input')"), true);
    await evaluate("document.querySelector('.session-rename button:last-child').click()");
    assert.equal(await wait("!document.querySelector('.session-rename') && window.settingsShellFixture.renameRequests.length===0"), true);
    await evaluate("document.querySelector('.session-actions-trigger').click()");
    await evaluate("[...document.querySelectorAll('.session-actions-menu button')].find(button=>button.textContent.includes('Delete…')).click()");
    assert.equal(await wait("!!document.querySelector('.session-delete input') && window.settingsShellFixture.deleteRequests.length===0"), true);
    assert.equal(await evaluate("document.querySelector('.session-delete button:not(:last-child)').disabled"), true);
    await evaluate("document.querySelector('.session-delete button:last-child').click()");
    assert.equal(await wait("!document.querySelector('.session-delete') && window.settingsShellFixture.deleteRequests.length===0"), true);
    await evaluate("document.querySelector('.session-actions-trigger').click()");
    await evaluate("[...document.querySelectorAll('.session-actions-menu button')].find(button=>button.textContent==='Rename…').click()");
    await evaluate(`(() => {const input=document.querySelector('.session-rename input');
      Object.getOwnPropertyDescriptor(HTMLInputElement.prototype,'value').set.call(input,'Updated title');
      input.dispatchEvent(new Event('input',{bubbles:true}));})()`);
    await evaluate("[...document.querySelectorAll('.session-rename button')].find(button=>button.textContent==='Save title').click()");
    assert.equal(await wait("window.settingsShellFixture.renameRequests.length===1"), true);
    await workflowLanguages(evaluate, workflowCalls, ".session-rename", ".session-rename button", "Save title", "code", "settingsShellFixture.renameRequests[0]");
    assert.deepEqual(await evaluate("window.settingsShellFixture.renameRequests[0] && ({sessionId:window.settingsShellFixture.renameRequests[0].sessionId,projectId:window.settingsShellFixture.renameRequests[0].projectId})"),
      { sessionId: "one", projectId: "project" });
    await evaluate("document.querySelector('.session-actions-trigger').click()");
    assert.equal(await wait("document.querySelector('.session-actions-menu button:nth-child(2)')?.disabled && document.querySelector('.session-actions-menu button:nth-child(3)')?.disabled"), true);
    await evaluate("window.settingsShellFixture.releaseMutation('rename')");
    assert.equal(await wait("document.querySelector('.session-rail')?.textContent.includes('unconfirmed') && document.querySelector('.session-actions-menu button:nth-child(2)')?.disabled"), true);
    assert.equal(await evaluate("window.settingsShellFixture.renameRequests.length===1 && window.settingsShellFixture.deleteRequests.length===0"), true);
    await evaluate("localStorage.setItem('settingsFixtureFreshSnapshot','true'); [...document.querySelectorAll('.session-rail button')].find(button=>button.textContent==='Refresh title').click()");
    assert.equal(await wait("!document.querySelector('.session-actions-menu') && document.querySelector('.session-rail')?.textContent.includes('unconfirmed')"), true,
      "a refreshed row revision dismisses the captured menu without unlocking uncertainty or stealing focus");
    assert.equal(await evaluate("window.settingsShellFixture.renameRequests.length"), 1);
    await evaluate("document.querySelector('.session-actions-trigger').click()");
    assert.equal(await wait("!!document.querySelector('.session-actions-menu')"), true);
    await evaluate("document.querySelector('#project-list button[title=\"/fixture/other\"]').click()");
    assert.equal(await wait("!document.querySelector('.session-actions-menu') && document.querySelector('.session-header h1')?.textContent==='other-session'"), true);
    await evaluate("document.querySelector('#project-list button[title=\"/fixture/project\"]').click()");
    assert.equal(await wait("document.querySelector('.session-header h1')?.textContent==='one' && document.querySelector('.session-rail')?.textContent.includes('unconfirmed')"), true,
      "switching scope cannot release the original unconfirmed rename");
    // Fresh isolated owner for the destructive path; the previous uncertain owner was never retried or released.
    await command("Page.reload");
    assert.equal(await wait("document.querySelector('.session-header h1')?.textContent==='one'"), true);
    await evaluate("document.querySelector('.session-actions-trigger').click()");
    await evaluate("[...document.querySelectorAll('.session-actions-menu button')].find(button=>button.textContent.includes('Delete…')).click()");
    assert.equal(await wait("document.activeElement===document.querySelector('.session-delete input') && window.settingsShellFixture.deleteRequests.length===0"), true);
    await evaluate(`(() => {const input=document.querySelector('.session-delete input');
      Object.getOwnPropertyDescriptor(HTMLInputElement.prototype,'value').set.call(input,'one');
      input.dispatchEvent(new Event('input',{bubbles:true}));})()`);
    assert.equal(await wait("document.querySelector('.session-delete button:not(:last-child)')?.disabled===false"), true);
    await evaluate("document.querySelector('.session-delete button:not(:last-child)').click()");
    assert.equal(await wait("window.settingsShellFixture.deleteRequests.length===1"), true);
    assert.deepEqual(await evaluate("window.settingsShellFixture.deleteRequests[0] && ({sessionId:window.settingsShellFixture.deleteRequests[0].sessionId,projectId:window.settingsShellFixture.deleteRequests[0].projectId,confirmedTitle:window.settingsShellFixture.deleteRequests[0].confirmedTitle})"),
      { sessionId: "one", projectId: "project", confirmedTitle: "one" });
    await evaluate("document.querySelector('.session-actions-trigger').click()");
    assert.equal(await wait("document.querySelector('.session-actions-menu button:nth-child(3)')?.disabled"), true);
    await evaluate("window.settingsShellFixture.releaseMutation('delete')");
    assert.equal(await wait("document.querySelector('.session-rail')?.textContent.includes('unconfirmed') && document.querySelector('.session-actions-menu button:nth-child(3)')?.disabled"), true);
    assert.equal(await evaluate("window.settingsShellFixture.deleteRequests.length===1"), true);
    await evaluate("localStorage.setItem('usageFixtureTruncated','true')");
    await command("Page.reload");
    assert.equal(await wait("!!document.querySelector('#project-sort')"), true);
    await evaluate(`(() => {const select=document.querySelector('#project-sort'); select.value='recent'; select.dispatchEvent(new Event('change',{bubbles:true}));})()`);
    assert.equal(await wait("document.querySelector('.project-evidence[role=status]')?.textContent.includes('Snapshot is truncated')"), true);
    await evaluate(`(() => {const input=document.querySelector('#project-filter');
      Object.getOwnPropertyDescriptor(HTMLInputElement.prototype,'value').set.call(input,'absent project');
      input.dispatchEvent(new Event('input',{bubbles:true}));})()`);
    assert.equal(await wait("document.querySelector('.project-rail .sidebar-empty[role=status]')?.textContent.includes('No matching projects')"), true);
    await evaluate("localStorage.setItem('settingsFixtureWorkspaceError','true')");
    await command("Page.reload");
    assert.equal(await wait("document.querySelector('.project-rail .sidebar-empty[role=alert]')?.textContent.includes('No alternate session scan was used')"), true);
    assert.equal(await evaluate("!!document.querySelector('.project-rail .panel-title') && !!document.querySelector('.session-rail-header h2') && !!document.querySelector('.rail-footer button') && !!document.querySelector('.project-rail [aria-label=\"Open project (Ctrl+O)\"]')"), true);
    // Actual App -> shared dispatcher -> registered command -> mixed History -> scroll hook.
    // No fixture-owned dispatcher or synthetic message DOM stands in for production wiring.
    for (const mode of ["live-only", "mixed", "empty", "error", "loading", "partial"]) {
      await evaluate(`localStorage.clear(); localStorage.setItem('settingsFixtureOwned','true');
        localStorage.setItem('navigationFixture','${mode}')`);
      await command("Page.reload");
      assert.equal(await wait("!!document.querySelector('[aria-label=\"Session info\"]') && window.settingsShellFixture.historyCalls.length>0"), true);
      if (mode !== "empty") assert.equal(await wait("document.querySelector('.history')?.textContent.includes('live-Assistant')"), true);
      if (["mixed", "live-only", "empty"].includes(mode))
        assert.equal(await wait("document.querySelector('.history')?.dataset.windowReady==='true'"), true);
      if (mode === "error") assert.equal(await wait("!!document.querySelector('.history [role=alert]')"), true);
      if (mode === "partial") assert.equal(await wait("window.settingsShellFixture.historyCalls.some(call=>call.cursor)"), true);
      await evaluate(`document.querySelector('[aria-label="Session info"]').focus()`);
      const key = (key: string, modifiers = {}) => evaluate(`(() => {const e=new KeyboardEvent('keydown',
        ${JSON.stringify({ key, bubbles: true, cancelable: true, ...modifiers })});
        document.activeElement.dispatchEvent(e); return e.defaultPrevented;})()`);
      const before = await evaluate(`JSON.stringify({ reads:window.settingsShellFixture.historyCalls,
        session:document.querySelector('.session-row > button[aria-pressed=true]')?.textContent,
        focus:document.activeElement.outerHTML, outer:[window.scrollX,window.scrollY,document.querySelector('.workspace-shell').scrollTop] })`);
      if (mode === "mixed") {
        assert.equal(await key("F3", { ctrlKey: true }), true);
        assert.equal(await wait("document.querySelector('.timeline-navigation-notice')?.textContent.includes('persisted-User-one')"), true);
        assert.equal(await key("F4"), true);
        assert.equal(await wait("document.querySelector('.timeline-navigation-notice')?.textContent.includes('persisted-Assistant-one')"), true);
        assert.equal(await key("F4"), true);
        assert.equal(await wait("document.querySelector('.timeline-navigation-notice')?.textContent.includes('Last retained message in this window')"), true,
          "next at the last persisted message must not select a live User/Assistant/Unknown row");
        assert.equal(await evaluate("!!document.querySelector('.timeline-bottom-button')"), true, "boundary remains unfollowed");
        assert.equal(await key("F3"), true);
        assert.equal(await wait("document.querySelector('.timeline-navigation-notice')?.textContent.includes('persisted-User-one')"), true);
        assert.equal(await key("F3"), true);
        assert.equal(await wait("document.querySelector('.timeline-navigation-notice')?.textContent.includes('First retained message in this window')"), true);
      } else {
        for (const [name, modifiers] of [["F3", {}], ["F4", {}], ["F3", { ctrlKey: true }]] as const)
          assert.equal(await key(name, modifiers), false, `${mode}: no eligible settled persisted row, leave native key unhandled`);
        assert.equal(await evaluate("!!document.querySelector('.timeline-navigation-notice')"), false);
      }
      assert.equal(await evaluate(`JSON.stringify({ reads:window.settingsShellFixture.historyCalls,
        session:document.querySelector('.session-row > button[aria-pressed=true]')?.textContent,
        focus:document.activeElement.outerHTML, outer:[window.scrollX,window.scrollY,document.querySelector('.workspace-shell').scrollTop] })`), before,
        `${mode}: navigation cannot read/page, change selection, transfer focus or scroll the outer workspace`);
      if (mode === "mixed") {
        assert.deepEqual(await evaluate("[...document.querySelectorAll('[data-persisted-message=true]')].map(row=>row.querySelector('.message-body p')?.textContent)"),
          ["persisted-User-one", "persisted-Assistant-one"], "only persisted user/assistant articles advertise eligibility");
        const notice = await evaluate("document.querySelector('.timeline-navigation-notice').textContent");
        const readsBeforeGuards = await evaluate("window.settingsShellFixture.historyCalls.length");
        for (const modifiers of [{ isComposing: true }, { keyCode: 229 }, { repeat: true },
          { altKey: true }, { shiftKey: true }, { metaKey: true }]) assert.equal(await key("F3", modifiers), false);
        await evaluate(`(() => {const e=new KeyboardEvent('keydown',{key:'F4',bubbles:true,cancelable:true});
          e.preventDefault(); document.activeElement.dispatchEvent(e);})()`);
        await evaluate("document.querySelector('#session-prompt').focus()");
        assert.equal(await key("F3", { ctrlKey: true }), false, "composer owns its input");
        await evaluate("document.querySelector('.session-rail .search input').focus()");
        assert.equal(await key("F4"), false, "other editors own their input");
        await evaluate("window.navigationWorkspace=document.querySelector('.session-workspace'); document.querySelector('.rail-footer .icon-label-button').click()");
        assert.equal(await wait("document.querySelector('.settings-dialog')?.open"), true);
        assert.equal(await key("F4"), false, "Settings retains modal ownership");
        assert.equal(await evaluate("window.navigationWorkspace===document.querySelector('.session-workspace')"), true);
        await evaluate("document.querySelector('[aria-label=\"Close settings\"]').click()");
        assert.equal(await evaluate("document.querySelector('.timeline-navigation-notice').textContent"), notice);
        assert.equal(await evaluate("window.settingsShellFixture.historyCalls.length"), readsBeforeGuards, "guards add no history reads");

        await evaluate("[...document.querySelectorAll('.session-row > button:first-child')].find(b=>b.textContent.includes('two')).click()");
        assert.equal(await wait("document.querySelector('.history')?.dataset.windowReady==='true' && document.querySelector('.history')?.textContent.includes('persisted-User-two')"), true);
        const readsAfterSelection = await evaluate("window.settingsShellFixture.historyCalls.length") as number;
        await evaluate("document.querySelector('[aria-label=\"Session info\"]').focus()");
        assert.equal(await key("F3", { ctrlKey: true }), true);
        assert.equal(await wait("document.querySelector('.timeline-navigation-notice')?.textContent.includes('persisted-User-two')"), true,
          "a remounted selected session cannot retain the prior row anchor");
        // Replace the retained rows through the existing explicit refresh, not navigation.
        await evaluate(`localStorage.setItem('navigationFixture','live-only'); document.querySelector('.history .section-heading button').click()`);
        assert.equal(await wait("document.querySelector('.history')?.dataset.windowReady==='true' && !document.querySelector('.history')?.textContent.includes('persisted-User-two')"), true);
        await evaluate("document.querySelector('[aria-label=\"Session info\"]').focus()");
        assert.equal(await key("F4"), false, "removed persisted anchors cannot redirect navigation onto retained live rows");
        assert.equal(await evaluate("!!document.querySelector('.timeline-navigation-notice')"), false, "ordinary refresh does not announce navigation");
        assert.equal(await evaluate("window.settingsShellFixture.historyCalls.length"), readsAfterSelection + 1, "only explicit refresh adds a read after selection");
      }
      // Release only the fixture's original held read, then drain the production History settlement.
      if (mode === "loading" || mode === "partial") {
        await evaluate("window.settingsShellFixture.releaseHistory()");
        assert.equal(await wait("document.querySelector('.history')?.dataset.windowReady==='true'"), true);
        assert.equal(await key("F3", { ctrlKey: true }), true, "settled original accumulation admits persisted navigation");
        assert.equal(await wait("document.querySelector('.timeline-navigation-notice')?.textContent.includes('persisted-User-one')"), true);
      }
    }
    // Production create handler and navigation wiring, with only transport replies deferred.
    for (const phase of ["reply", "snapshot"]) for (const change of ["session", "aba", "settings", "settings-aba", "scope", "scope-aba", "path", "project-id", "modal-aba", "input", "input-aba", "search", "host", "unmount", "valid", "missing", "error"]) {
      await evaluate(`localStorage.clear(); localStorage.setItem('settingsFixtureOwned','true'); localStorage.setItem('settingsFixtureSecondProject','true')`);
      if (change === "host") await evaluate(`localStorage.setItem('settingsFixtureHoldChoices','true')`);
      await command("Page.reload");
      assert.equal(await wait("!!document.querySelector('.session-row button[aria-pressed=true]') && !document.querySelector('[aria-label=\"Create session\"]').disabled"), true);
      const readsBefore = await evaluate("window.settingsShellFixture.snapshotCalls.length") as number;
      await evaluate(`document.querySelector('[aria-label="Create session"]').click()`);
      await evaluate(`(() => {const input=document.querySelector('.session-create input');
        Object.getOwnPropertyDescriptor(HTMLInputElement.prototype,'value').set.call(input,'Original title');
        input.dispatchEvent(new Event('input',{bubbles:true}));})()`);
      if (change === "valid") await workflowLanguages(evaluate, workflowCalls, ".session-create", ".session-create button", "Create and open");
      await evaluate(`document.querySelector('.session-create button').click()`);
      assert.equal(await wait("window.settingsShellFixture.creates.length===1"), true);
      if (change === "valid" || change === "host") await workflowLanguages(evaluate, workflowCalls, ".session-create", ".session-create button", "Create and open", "code", "settingsShellFixture.creates[0].request");
      assert.equal(await evaluate("document.querySelector('.session-create input').disabled && document.querySelector('[aria-label=\"Create session\"]').disabled"), true,
        "pending creation still disables ordinary title editing and form toggling");
      await evaluate(`localStorage.setItem('creationFixtureHoldSnapshot','true')`);
      if (phase === "snapshot") {
        await evaluate(`window.settingsShellFixture.releaseCreate()`);
        assert.equal(await wait("window.settingsShellFixture.snapshots.length===1"), true);
      }
      const selectSession = (title: string) => evaluate(`[...document.querySelectorAll('.session-row > button:first-child')].find(b=>b.textContent.includes('${title}')).click()`);
      if (change === "session" || change === "aba") { await selectSession("two"); if (change === "aba") await selectSession("one"); }
      if (change === "settings" || change === "settings-aba") {
        await evaluate(`document.querySelector('.rail-footer .icon-label-button').click()`);
        assert.equal(await wait("document.querySelector('.settings-dialog')?.open"), true);
        if (change === "settings-aba") await evaluate(`document.querySelector('[aria-label="Close settings"]').click()`);
      }
      if (change === "scope" || change === "scope-aba") {
        await evaluate(`[...document.querySelectorAll('#project-list button')].find(b=>b.textContent.includes('Other project')).click()`);
        if (change === "scope-aba") await evaluate(`document.querySelector('#project-list button[title="/fixture/project"]').click()`);
      }
      if (change === "path") await evaluate(`window.settingsShellFixture.catalog.projects[0].path='/fixture/changed'`);
      if (change === "project-id") await evaluate(`window.settingsShellFixture.catalog.projects[0].id='changed-project'`);
      if (change === "input" || change === "input-aba" || change === "search") {
        // The UI disables title editing while pending. Scripted input still must not mutate
        // the captured request or let an old completion clear a newer form lifetime.
        const selector = change === "search" ? '.session-rail .search input' : '.session-create input';
        await evaluate(`(() => {const input=document.querySelector('${selector}');
          Object.getOwnPropertyDescriptor(HTMLInputElement.prototype,'value').set.call(input,'Newer input');
          input.dispatchEvent(new Event('input',{bubbles:true}));})()`);
        if (change === "input-aba") await evaluate(`(() => {const input=document.querySelector('.session-create input');
          Object.getOwnPropertyDescriptor(HTMLInputElement.prototype,'value').set.call(input,'Original title');
          input.dispatchEvent(new Event('input',{bubbles:true}));})()`);
      }
      if (change === "host") {
        assert.equal(await wait("window.settingsShellFixture.choiceReads.length>0"), true);
        await evaluate(`window.settingsShellFixture.releaseChoices('stale')`);
      }
      if (change === "modal-aba") {
        await evaluate(`document.querySelector('[aria-label="Session info"]').click()`);
        assert.equal(await wait("!!document.querySelector('.session-info-dialog')"), true);
        await evaluate(`document.querySelector('[aria-label="Close session info"]').click()`);
      }
      if (change === "unmount") await evaluate(`window.unmountShellFixture()`);
      if (phase === "reply") {
        await evaluate(`window.settingsShellFixture.releaseCreate()`);
        if (["valid", "missing", "error"].includes(change)) assert.equal(await wait("window.settingsShellFixture.snapshots.length===1"), true);
      }
      if (phase === "snapshot" || ["valid", "missing", "error"].includes(change))
        await evaluate(`window.settingsShellFixture.releaseSnapshot('${change === "missing" || change === "error" ? change : "ok"}')`);
      assert.equal(await wait("!document.querySelector('.session-rail')?.textContent.includes('Creating session…') && !document.querySelector('.session-create button')?.disabled"), true);
      assert.equal(await evaluate("window.settingsShellFixture.creates.length"), 1, change);
      assert.deepEqual(await evaluate("window.settingsShellFixture.creates[0].request"), {
        expectedHostEpoch: "12345678-1234-1234-1234-123456789abc", scope: "project", projectId: "project",
        projectPath: "/fixture/project", title: "Original title",
      }, `${phase}/${change}: original request never changes or retries`);
      assert.equal(await evaluate("window.settingsShellFixture.snapshotCalls.length"),
        readsBefore + (phase === "snapshot" || ["valid", "missing", "error"].includes(change) ? 1 : 0), `${phase}/${change}: no extra read`);
      if (change === "unmount") {
        assert.equal(await evaluate("document.querySelector('#root').childElementCount"), 0);
      } else if (change === "valid") {
        assert.equal(await wait("document.querySelector('.session-row > button[aria-pressed=true]')?.textContent.includes('created')"), true);
        assert.equal(await evaluate("!!document.querySelector('.session-create')"), false);
      } else {
        assert.equal(await evaluate("document.querySelector('.session-row > button[aria-pressed=true]')?.textContent.includes('created') ?? false"), false, `${change}: late create must not select its session`);
        if (!["path", "project-id", "search"].includes(change))
          assert.equal(await evaluate("document.querySelector('.session-row > button[aria-pressed=true] .session-title')?.textContent"),
            change === "session" ? "two" : change === "scope" ? "other-session" : "one", `${phase}/${change}: preserve newer selection`);
        assert.equal(await evaluate("document.querySelector('.session-rail [role=alert]')?.textContent.includes('completed')"), true, `${change}: keep truthful completion evidence`);
        assert.equal(await evaluate("!![...document.querySelectorAll('.session-row .session-title')].find(x=>x.textContent==='created')"), false, `${change}: do not publish stale create refresh`);
        if (change === "scope" || change === "scope-aba") {
          assert.equal(await evaluate("!!document.querySelector('.session-create')"), false, "closed form must stay closed");
          await evaluate(`document.querySelector('[aria-label="Create session"]').click()`);
        }
        assert.equal(await evaluate("document.querySelector('.session-create input')?.value"), change === "input" ? "Newer input" : "Original title", `${change}: preserve form input`);
        if (change === "search") assert.equal(await evaluate("document.querySelector('.session-rail .search input').value"), "Newer input");
        if (change === "settings") assert.equal(await evaluate("document.querySelector('.settings-dialog')?.open"), true, "late completion must not close Settings");
      }
    }
    // Fixed Layout lifetime through actual App publications, not a copied component shell.
    await evaluate(`localStorage.clear(); localStorage.setItem('settingsFixtureOwned','true');
      localStorage.setItem('navigationFixture','mixed'); localStorage.setItem('layoutFixtureLive','true');
      localStorage.setItem('settingsFixtureSecondProject','true')`);
    await command("Page.reload");
    assert.equal(await wait("document.querySelector('#session-prompt') && document.querySelector('.timeline-scroll')?.textContent.includes('persisted-User-one') && document.querySelector('.timeline-scroll')?.textContent.includes('live-User') && settingsShellFixture.displayCalls.length-settingsShellFixture.displayCleanup.length===1"), true);
    await evaluate(`window.layoutOpens=0; window.open=()=>{window.layoutOpens++; return null;};
      window.layoutHost=document.querySelector('.workspace-layout'); window.layoutPanel=document.querySelector('.session-content-main-panel');
      window.layoutComposer=document.querySelector('#session-prompt'); window.layoutTimeline=document.querySelector('.timeline-scroll');
      window.layoutRail=document.querySelector('.session-rail'); window.layoutNotes=document.querySelector('.notes-pane');
      window.layoutReads=JSON.stringify([settingsShellFixture.historyCalls,settingsShellFixture.notesCalls,settingsShellFixture.displayCalls,settingsShellFixture.displayCleanup]);
      Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype,'value').set.call(window.layoutComposer,'Original layout draft');
      window.layoutComposer.dispatchEvent(new Event('input',{bubbles:true}))`);
    assert.equal(await wait("document.querySelector('#session-prompt').value==='Original layout draft'"), true);
    const frames = () => evaluate("new Promise(resolve=>{let n=6;const frame=()=>--n?requestAnimationFrame(frame):resolve(true);requestAnimationFrame(frame)})");
    const geometry = () => evaluate(`(() => {const rect=s=>{const r=document.querySelector(s).getBoundingClientRect();return {x:r.x,y:r.y,width:r.width,height:r.height,right:r.right,bottom:r.bottom}};
      return {sessions:rect('.session-rail'),content:rect('.content'),projects:rect('.project-rail'),shell:rect('.workspace-shell'),
        control:rect('.session-splitter'),aria:document.querySelector('.session-splitter').getAttribute('aria-valuenow'),
        preference:localStorage.getItem('codealta.desktop.panes.v1')};})()`);
    const captureGeometry = async (label: string) => {
      await frames(); const first = await geometry(); await frames(); const second = await geometry();
      layoutObservations.push({ label, first, second }); assert.deepEqual(first, second, `${label}: finite geometry/ARIA samples agree`);
    };
    const originalWidths = await evaluate("localStorage.getItem('codealta.desktop.panes.v1')");
    for (const [width,height] of [[1120,800],[876,601],[875,601],[875,600],[390,800],[390,600],[1120,800]]) {
      await evaluate("window.layoutComposer.focus()");
      await command("Emulation.setDeviceMetricsOverride", { width, height, deviceScaleFactor: 1, mobile: false });
      await captureGeometry(`${width}x${height}`);
      assert.equal(await evaluate(`window.layoutComposer===document.querySelector('#session-prompt') && document.activeElement===window.layoutComposer &&
        window.layoutTimeline===document.querySelector('.timeline-scroll') && window.layoutRail===document.querySelector('.session-rail') &&
        window.layoutNotes===document.querySelector('.notes-pane') && window.layoutComposer.value==='Original layout draft'`), true);
      assert.equal(await evaluate("localStorage.getItem('codealta.desktop.panes.v1')"), originalWidths, "projection never persists widths");
      assert.equal(await evaluate(`(() => {const s=window.layoutRail.getBoundingClientRect(),c=document.querySelector('.content').getBoundingClientRect(),
        bar=document.querySelector('.session-splitter'),b=bar.getBoundingClientRect();return ${width}>875
          ? s.width>=220 && s.width<=560 && c.width>=480 && Math.abs(c.left-s.right-8)<1 && b.width===8 && Number(bar.getAttribute('aria-valuenow'))===Math.round(s.width)
          : Math.abs(s.height-Math.max(${height}<=600?180:340,${height}*(${height}<=600?.4:.45)))<1 && Math.abs(c.top-s.bottom)<1 && s.width===${width} && c.width===${width} && b.width===0;})()`), true);
      if (width <= 875) {
        await evaluate("document.querySelector('[aria-controls=project-rail]').click()"); await captureGeometry(`projects-open-${width}x${height}`);
        assert.equal(await evaluate(`(() => {const p=document.querySelector('.project-rail').getBoundingClientRect(),c=document.querySelector('.content').getBoundingClientRect();
          const hidden=window.layoutRail.getBoundingClientRect();const input=window.layoutRail.querySelector('input'); input.focus();
          return hidden.width===0 && hidden.height===0 && window.layoutRail===document.querySelector('.session-rail') &&
            window.layoutNotes===document.querySelector('.notes-pane') && input!==document.activeElement &&
            Math.abs(c.top-p.bottom)<1 && c.width===${width} && c.height>0 && document.querySelector('.session-splitter').getClientRects().length===0;})()`), true);
        await evaluate("document.querySelector('[aria-controls=project-rail]').click()"); await captureGeometry(`projects-closed-${width}x${height}`);
      }
      assert.equal(await evaluate("window.layoutReads===JSON.stringify([settingsShellFixture.historyCalls,settingsShellFixture.notesCalls,settingsShellFixture.displayCalls,settingsShellFixture.displayCleanup])"), true);
    }
    // App-owned existing input surface must project live pixels, not library default key steps.
    await evaluate("document.querySelector('.session-splitter').focus()");
    await command("Input.dispatchKeyEvent", { type: "keyDown", key: "ArrowRight", code: "ArrowRight", windowsVirtualKeyCode: 39 });
    await command("Input.dispatchKeyEvent", { type: "keyUp", key: "ArrowRight", code: "ArrowRight", windowsVirtualKeyCode: 39 });
    await captureGeometry("arrow-plus16");
    assert.equal(await evaluate("Math.round(window.layoutRail.getBoundingClientRect().width)"), 326);
    await command("Input.dispatchKeyEvent", { type: "keyDown", key: "ArrowLeft", code: "ArrowLeft", windowsVirtualKeyCode: 37 });
    await command("Input.dispatchKeyEvent", { type: "keyUp", key: "ArrowLeft", code: "ArrowLeft", windowsVirtualKeyCode: 37 });
    await captureGeometry("arrow-minus16");
    assert.equal(await evaluate("Math.round(window.layoutRail.getBoundingClientRect().width)"), 310);
    assert.equal(await evaluate("localStorage.getItem('codealta.desktop.panes.v1')"), originalWidths);
    await evaluate("document.querySelector('.session-splitter').dispatchEvent(new KeyboardEvent('keydown',{key:'ArrowRight',bubbles:true,cancelable:true}))");
    await evaluate("document.querySelector('.session-splitter').dispatchEvent(new KeyboardEvent('keydown',{key:'Home',bubbles:true,cancelable:true}))");
    await captureGeometry("home-reset"); assert.equal(await evaluate("Math.round(window.layoutRail.getBoundingClientRect().width)"), 310);
    const bar = await evaluate("(() => {const r=document.querySelector('.session-splitter').getBoundingClientRect();return {x:r.x+4,y:r.y+40}})()") as { x: number; y: number };
    await command("Input.dispatchMouseEvent", { type: "mousePressed", ...bar, button: "left", buttons: 1, clickCount: 1 });
    await command("Input.dispatchMouseEvent", { type: "mouseMoved", x: bar.x + 24, y: bar.y, button: "left", buttons: 1 });
    await captureGeometry("live-drag-before-release"); assert.equal(await evaluate("Math.round(window.layoutRail.getBoundingClientRect().width)"), 334);
    await command("Input.dispatchMouseEvent", { type: "mouseReleased", x: bar.x + 24, y: bar.y, button: "left", buttons: 0, clickCount: 1 });
    await evaluate("document.querySelector('.session-splitter').dispatchEvent(new MouseEvent('dblclick',{bubbles:true}))");
    await captureGeometry("double-click-reset"); assert.equal(await evaluate("Math.round(window.layoutRail.getBoundingClientRect().width)"), 310);
    // Collapse projects, then resize only the parent: neither operation persists a constrained width.
    await evaluate("document.querySelector('[aria-controls=project-rail]').click(); document.querySelector('.workspace-shell').style.width='740px'");
    await captureGeometry("collapsed-parent-740");
    assert.equal(await evaluate("Math.round(window.layoutRail.getBoundingClientRect().width)===252 && document.querySelector('.content').getBoundingClientRect().width>=480"), true);
    assert.equal(await evaluate("localStorage.getItem('codealta.desktop.panes.v1')"), originalWidths);
    await evaluate("document.querySelector('.workspace-shell').style.width=''; document.querySelector('[aria-controls=project-rail]').click()");
    await captureGeometry("restored-parent"); assert.equal(await evaluate("Math.round(window.layoutRail.getBoundingClientRect().width)"), 310);
    for (const width of [390, 1120]) for (const theme of ["light", "dark"]) {
      await command("Emulation.setDeviceMetricsOverride", { width, height: 800, deviceScaleFactor: 1, mobile: false });
      await evaluate(`document.documentElement.dataset.theme='${theme}';
        window.publishLayoutCatalog({...settingsShellFixture.catalog,sessions:settingsShellFixture.catalog.sessions.map(row=>({...row,title:'fresh '+row.id}))});
        document.querySelector('.project-rail .icon-label-button').click()`);
      assert.equal(await wait("document.querySelector('.settings-dialog')?.open && document.querySelector('.session-header h1')?.textContent==='fresh one'"), true);
      await evaluate("document.querySelector('[aria-label=\"Close settings\"]').click()");
      assert.equal(await wait("!document.querySelector('.settings-dialog')"), true);
      assert.equal(await evaluate(`(() => {const panel=document.querySelector('.session-content-main-panel').getBoundingClientRect();
        const shell=document.querySelector('.workspace-shell').getBoundingClientRect();
        return panel.width>0 && panel.height>0 && shell.width>0 && panel.left>=0 && panel.right<=${width}+1 &&
          panel.bottom<=800 && document.documentElement.scrollWidth<=${width} &&
          window.layoutHost===document.querySelector('.workspace-layout') && window.layoutPanel===document.querySelector('.session-content-main-panel') &&
          window.layoutComposer===document.querySelector('#session-prompt') && window.layoutTimeline===document.querySelector('.timeline-scroll') &&
          window.layoutComposer.value==='Original layout draft' &&
          getComputedStyle(window.layoutHost).getPropertyValue('--flexlayout-color-text').trim()===getComputedStyle(document.documentElement).getPropertyValue('--text').trim();})()`), true,
        `${width}/${theme}: real measured panel, fresh props, stable children and bounded geometry`);
      assert.equal(await evaluate("window.layoutReads===JSON.stringify([settingsShellFixture.historyCalls,settingsShellFixture.notesCalls,settingsShellFixture.displayCalls,settingsShellFixture.displayCleanup])"), true,
        "unchanged target must not re-read history/notes or reattach live display");
    }
    // Layout key bindings and drag gestures have no session/provider or window authority.
    await evaluate(`window.layoutPanel.focus();
      for(const key of ['Delete','F2','Escape']) window.layoutPanel.dispatchEvent(new KeyboardEvent('keydown',{key,bubbles:true,cancelable:true}));
      for(const type of ['dragstart','dragover','drop']) window.layoutHost.dispatchEvent(new DragEvent(type,{bubbles:true,cancelable:true,dataTransfer:new DataTransfer()}))`);
    assert.equal(await evaluate(`window.layoutPanel===document.querySelector('.session-content-main-panel') && document.querySelectorAll('.flexlayout__tab').length===2 &&
      !window.layoutHost.querySelector('.flexlayout__splitter,.flexlayout__tab_button,.flexlayout__tabset_header,.flexlayout__floating_window') &&
      window.layoutOpens===0 && settingsShellFixture.sends.length===0 && settingsShellFixture.creates.length===0 &&
      settingsShellFixture.deleteRequests.length===0 && settingsShellFixture.probes.length===0`), true);
    await evaluate("document.querySelector('.owned-session .send-button').click()");
    assert.equal(await wait("settingsShellFixture.sends.length===1"), true);
    const originalSend = await evaluate("settingsShellFixture.sends[0]");
    // Reminders intentionally unmounts the workspace, unlike Settings. Pending
    // requests/drafts belong to App; display and child read effects must detach.
    const beforeReminders = await evaluate("settingsShellFixture.displayCalls.length") as number;
    await evaluate(`localStorage.setItem('layoutFixtureReminders','true'); document.querySelector('[data-reminder-count]').click()`);
    assert.equal(await wait("!!document.querySelector('.reminders-destination') && !document.querySelector('.workspace-shell') && settingsShellFixture.displayCalls.length===settingsShellFixture.displayCleanup.length && settingsShellFixture.reminderReads.length>0"), true);
    assert.equal(await evaluate("!window.layoutHost.isConnected && !window.layoutComposer.isConnected && !window.layoutTimeline.isConnected && !window.layoutNotes.isConnected"), true);
    assert.equal(await evaluate("settingsShellFixture.lateDisplayAttempts.includes('one')"), true, "exercise a real late fake-provider yield after abort");
    const staleReminderReads = await evaluate("settingsShellFixture.reminderReads.length") as number;
    assert.deepEqual(await evaluate("settingsShellFixture.sends[0]"), originalSend);
    assert.equal(await evaluate("settingsShellFixture.sends.length"), 1, "navigation cannot retry a pending Send");
    await evaluate("document.querySelector('.reminders-destination > button').click()");
    assert.equal(await wait("document.querySelector('#session-prompt')?.value==='Original layout draft' && settingsShellFixture.displayCalls.length-settingsShellFixture.displayCleanup.length===1 && document.querySelector('.timeline-scroll')?.textContent.includes('live-User')"), true);
    assert.equal(await evaluate("window.layoutHost!==document.querySelector('.workspace-layout') && window.layoutComposer!==document.querySelector('#session-prompt') && window.layoutTimeline!==document.querySelector('.timeline-scroll')"), true,
      "Back to session intentionally mounts new DOM, not a retained workspace");
    assert.equal(await evaluate("settingsShellFixture.displayCalls.length"), beforeReminders + 1, "one restored live display attachment");
    assert.equal(await evaluate(`settingsShellFixture.reminderReads.slice(0,${staleReminderReads}).every(read=>read.signal.aborted)`), true);
    await evaluate(`for(let i=0;i<${staleReminderReads};i++) settingsShellFixture.releaseReminder(i)`);
    await frames();
    assert.equal(await evaluate("!document.body.textContent.includes('stale-disposed-display') && !document.querySelector('.reminders-destination') && document.querySelector('[data-reminder-count]').getAttribute('aria-label').includes('unknown')"), true,
      "late disposed display/list results cannot publish into the restored owner");
    assert.deepEqual(await evaluate("settingsShellFixture.sends[0]"), originalSend);
    assert.equal(await evaluate("settingsShellFixture.sends.length"), 1);
    layoutObservations.push({ label: "reminders-unmount-remount", beforeReminders, staleReminderReads,
      after: await evaluate("({displays:settingsShellFixture.displayCalls,cleanup:settingsShellFixture.displayCleanup,lateAttempts:settingsShellFixture.lateDisplayAttempts})") });
    // Subsequent keyed-session/archive/epoch assertions now compare this genuine
    // replacement workspace, while the original request is still in flight.
    await evaluate("window.layoutHost=document.querySelector('.workspace-layout'); window.layoutPanel=document.querySelector('.session-content-main-panel'); window.layoutComposer=document.querySelector('#session-prompt'); window.layoutTimeline=document.querySelector('.timeline-scroll'); true");
    await evaluate("[...document.querySelectorAll('.session-row > button:first-child')].find(x=>x.textContent.includes('two')).click()");
    assert.equal(await wait("document.querySelector('.session-header h1')?.textContent==='fresh two'"), true);
    assert.equal(await evaluate("window.layoutHost===document.querySelector('.workspace-layout') && window.layoutComposer!==document.querySelector('#session-prompt') && window.layoutTimeline!==document.querySelector('.timeline-scroll')"), true,
      "session key remounts only the existing session workspace, not Layout");
    await evaluate(`const otherDraft=document.querySelector('#session-prompt');
      Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype,'value').set.call(otherDraft,'Other session draft');
      otherDraft.dispatchEvent(new Event('input',{bubbles:true}));`);
    await evaluate("settingsShellFixture.failSend()");
    assert.equal(await wait("document.querySelector('#session-prompt')?.value==='Other session draft' && !document.querySelector('.owned-session')?.textContent.includes('Original layout draft')"), true);
    await evaluate("[...document.querySelectorAll('.session-row > button:first-child')].find(x=>x.textContent.includes('one')).click()");
    assert.equal(await wait("document.querySelector('#session-prompt')?.value==='Original layout draft' && document.querySelector('.send-button')?.textContent==='Retry exact request' && !document.querySelector('.send-button').disabled"), true);
    await evaluate(`window.publishLayoutCatalog({...settingsShellFixture.catalog,projects:settingsShellFixture.catalog.projects.map(row=>({...row,archived:row.id==='project'}))})`);
    assert.equal(await wait("!!document.querySelector('#catalog-prompt') && document.querySelector('.archived-action-recovery')?.textContent.includes('Original layout draft')"), true,
      "archive recovery shows original uncertainty without modifying the other session's draft");
    assert.deepEqual(await evaluate("settingsShellFixture.sends[0]"), originalSend);
    assert.equal(await evaluate("settingsShellFixture.sends.length"), 1, "no Layout or archive retry");
    await evaluate("window.archivedTimeline=document.querySelector('.timeline-scroll'); document.querySelector('#project-list button[title=\"/fixture/other\"]').click()");
    assert.equal(await wait("document.querySelector('.session-header h1')?.textContent==='other-session' && !document.querySelector('.archived-action-recovery')"), true);
    assert.equal(await evaluate("window.layoutHost===document.querySelector('.workspace-layout') && window.archivedTimeline!==document.querySelector('.timeline-scroll')"), true,
      "project navigation retains Layout but changes the existing keyed workspace");
    await evaluate("document.querySelector('#project-list button[title=\"/fixture/project\"]').click()");
    assert.equal(await wait("document.querySelector('.archived-action-recovery')?.textContent.includes('Original layout draft')"), true);
    await evaluate("window.loseLayoutHost()");
    assert.equal(await wait("!document.querySelector('.archived-action-recovery')"), true,
      "host fencing hides another owner's retained original");
    assert.equal(await evaluate("window.layoutHost===document.querySelector('.workspace-layout') && settingsShellFixture.sends.length===1 && window.layoutOpens===0"), true);
    await evaluate("window.publishLayoutCatalog({...settingsShellFixture.catalog,sessions:[]})");
    assert.equal(await wait("document.querySelector('.session-header h1')?.textContent==='Prompt draft' && !!document.querySelector('#catalog-prompt')"), true);
    assert.equal(await evaluate("window.layoutHost===document.querySelector('.workspace-layout') && window.layoutPanel===document.querySelector('.session-content-main-panel')"), true,
      "empty content is a fresh factory child, not a new Layout model");
    await evaluate("window.unmountShellFixture()");
    assert.equal(await wait("document.querySelector('#root').childElementCount===0 && settingsShellFixture.displayCalls.length===settingsShellFixture.displayCleanup.length"), true);
    // Restore a non-default preference on a new actual App, including a load at
    // constrained desktop width. Projection and remounts must not save constraints.
    await evaluate(`localStorage.removeItem('layoutFixtureReminders'); localStorage.removeItem('codealta.desktop.sessionTabs.v1'); localStorage.setItem('codealta.desktop.panes.v1',JSON.stringify({projects:280,sessions:420}));
      localStorage.setItem('codealta.desktop.projectRail.v1','expanded')`);
    await command("Emulation.setDeviceMetricsOverride", { width: 876, height: 800, deviceScaleFactor: 1, mobile: false });
    await command("Page.reload");
    assert.equal(await wait("!!document.querySelector('#session-prompt') && settingsShellFixture.displayCalls.length-settingsShellFixture.displayCleanup.length===1"), true);
    const savedWidths = JSON.stringify({ projects: 280, sessions: 420 });
    for (const width of [876, 1400, 875, 1400]) {
      await command("Emulation.setDeviceMetricsOverride", { width, height: 800, deviceScaleFactor: 1, mobile: false });
      await captureGeometry(`persisted-${width}`);
      assert.equal(await evaluate("localStorage.getItem('codealta.desktop.panes.v1')"), savedWidths);
      if (width > 875) assert.equal(await evaluate(`Math.round(document.querySelector('.session-rail').getBoundingClientRect().width)===${width===876?220:420} &&
        Math.round(document.querySelector('.project-rail').getBoundingClientRect().width)===${width===876?160:280}`), true);
    }
    await command("Page.reload");
    assert.equal(await wait("Math.round(document.querySelector('.session-rail')?.getBoundingClientRect().width)===420 && !!document.querySelector('#session-prompt')"), true);
    await captureGeometry("persisted-second-App-mount");
    assert.equal(await evaluate("localStorage.getItem('codealta.desktop.panes.v1')"), savedWidths);
    // Actual App tab navigation: no hidden live SessionWorkspace instances.
    await evaluate(`localStorage.clear(); localStorage.setItem('settingsFixtureOwned','true');
      localStorage.setItem('settingsFixtureSecondProject','true'); localStorage.setItem('layoutFixtureLive','true'); localStorage.setItem('navigationFixture','tabs')`);
    await command("Page.reload");
    assert.equal(await wait("document.querySelector('#session-prompt') && document.querySelectorAll('.session-tabs [role=tab]').length===2 && document.querySelector('.timeline-scroll')?.textContent.includes('Retained paragraph')"), true);
    await frames();
    await evaluate(`const input=document.querySelector('#session-prompt'); Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype,'value').set.call(input,'Tab-owned pending draft');
      input.dispatchEvent(new Event('input',{bubbles:true}));`);
    await frames();
    assert.equal(await evaluate("document.querySelector('.session-tabs [aria-selected=true]').textContent.includes('Draft')"), true);
    await evaluate("document.querySelector('.send-button').click()");
    assert.equal(await wait("settingsShellFixture.sends.length===1"), true);
    const tabSend = await evaluate("settingsShellFixture.sends[0]");
    await evaluate("document.querySelector('.timeline-scroll').scrollTop=120; document.querySelector('.timeline-scroll').dispatchEvent(new Event('scroll')); true");
    await frames();
    const tabScroll = await evaluate("document.querySelector('.timeline-scroll').scrollTop");
    assert.ok(Number(tabScroll) > 0);
    await evaluate("[...document.querySelectorAll('.session-row > button:first-child')].find(x=>x.textContent.includes('two')).click()");
    assert.equal(await wait("document.querySelector('.session-header h1')?.textContent==='two' && document.querySelectorAll('.session-tabs [role=tab]').length===3 && settingsShellFixture.displayCalls.length-settingsShellFixture.displayCleanup.length===1"), true);
    assert.equal(await evaluate("document.querySelector('.session-tabs [role=tab]').textContent.includes('Draft')"), false, "pending Send is not relabeled as an unsent edited draft");
    const inactiveReads = await evaluate("JSON.stringify([settingsShellFixture.historyCalls,settingsShellFixture.displayCalls,settingsShellFixture.displayCleanup])");
    await evaluate("window.tabTwoInput=document.querySelector('#session-prompt'); document.querySelector('.close-session-tab').click()");
    await frames();
    assert.equal(await evaluate("window.tabTwoInput===document.querySelector('#session-prompt') && document.querySelectorAll('.session-tabs [role=tab]').length===2"), true);
    assert.equal(await evaluate("JSON.stringify([settingsShellFixture.historyCalls,settingsShellFixture.displayCalls,settingsShellFixture.displayCleanup])"), inactiveReads, "closing inactive tab does not read or detach active session");
    await evaluate("document.querySelector('.session-tabs > button').click()");
    assert.equal(await wait("document.querySelector('#session-prompt')?.value==='Tab-owned pending draft' && settingsShellFixture.displayCalls.length-settingsShellFixture.displayCleanup.length===1"), true);
    await frames();
    assert.equal(await evaluate("document.querySelector('.timeline-scroll').scrollTop"), tabScroll, "scroll and non-follow position survive tab close/reopen");
    assert.deepEqual(await evaluate("settingsShellFixture.sends[0]"), tabSend);
    assert.equal(await evaluate("settingsShellFixture.sends.length"), 1);
    // Editing/IME/repeat do not consume tab commands; normal tab keyboard does.
    await evaluate(`document.querySelector('.session-rail .search input').focus(); document.activeElement.dispatchEvent(new KeyboardEvent('keydown',{key:'w',ctrlKey:true,bubbles:true,cancelable:true}));
      const tab=document.querySelector('.session-tabs [aria-selected=true]'); tab.focus();
      tab.dispatchEvent(new KeyboardEvent('keydown',{key:'w',ctrlKey:true,isComposing:true,bubbles:true,cancelable:true}));
      tab.dispatchEvent(new KeyboardEvent('keydown',{key:'w',ctrlKey:true,repeat:true,bubbles:true,cancelable:true}));`);
    assert.equal(await evaluate("document.querySelectorAll('.session-tabs [role=tab]').length"), 3);
    await evaluate("document.activeElement.dispatchEvent(new KeyboardEvent('keydown',{key:'w',ctrlKey:true,bubbles:true,cancelable:true}))");
    assert.equal(await wait("document.querySelector('.session-header h1')?.textContent==='two' && document.querySelectorAll('.session-tabs [role=tab]').length===2"), true);
    await evaluate("document.querySelector('.session-tabs [role=tab]').focus(); document.activeElement.dispatchEvent(new KeyboardEvent('keydown',{key:'t',ctrlKey:true,shiftKey:true,bubbles:true,cancelable:true}))");
    assert.equal(await wait("document.querySelector('#session-prompt')?.value==='Tab-owned pending draft'"), true);
    await evaluate("document.querySelector('#project-list button[title=\"/fixture/other\"]').click()");
    assert.equal(await wait("document.querySelector('.session-header h1')?.textContent==='other-session'"), true);
    await evaluate("[...document.querySelectorAll('.session-tabs [role=tab]')].find(x=>x.textContent.startsWith('one')).click()");
    assert.equal(await wait("document.querySelector('#session-prompt')?.value==='Tab-owned pending draft' && document.querySelector('#project-list button[title=\"/fixture/project\"]').getAttribute('aria-pressed')==='true'"), true);
    await evaluate(`window.tabsCatalog={...settingsShellFixture.catalog,sessions:[...settingsShellFixture.catalog.sessions,{...settingsShellFixture.catalog.sessions[0],id:'global-tab',title:'global-tab',scopeKind:'global',projectId:null,workspacePath:null}]};
      window.publishLayoutCatalog(window.tabsCatalog);`);
    await frames();
    await evaluate("document.querySelector('.project-root-list button').click()");
    assert.equal(await wait("document.querySelector('.session-header h1')?.textContent==='global-tab'"), true);
    await evaluate("[...document.querySelectorAll('.session-tabs [role=tab]')].find(x=>x.textContent.startsWith('one')).click(); settingsShellFixture.failSend()");
    assert.equal(await wait("document.querySelector('.send-button')?.textContent==='Retry exact request' && document.querySelector('#session-prompt')?.value==='Tab-owned pending draft'"), true);
    assert.equal(await evaluate("settingsShellFixture.sends.length===1 && document.querySelectorAll('.session-workspace').length===1 && settingsShellFixture.displayCalls.length-settingsShellFixture.displayCleanup.length===1"), true);
    // Transient catalog error keeps presentation identities, disables navigation;
    // a verified publication restores, and a removed identity is pruned.
    await evaluate("window.publishLayoutState({kind:'error',message:'Fake catalog unavailable'})");
    assert.equal(await wait("[...document.querySelectorAll('.session-tabs [role=tab]')].slice(1).every(x=>x.disabled) && !document.querySelector('#session-prompt')"), true);
    await evaluate("window.publishLayoutCatalog(window.tabsCatalog)");
    assert.equal(await wait("document.querySelector('#session-prompt')?.value==='Tab-owned pending draft'"), true);
    await evaluate("window.publishLayoutCatalog({...window.tabsCatalog,sessions:window.tabsCatalog.sessions.filter(x=>x.id!=='one')})");
    assert.equal(await wait("document.querySelector('.session-header h1')?.textContent==='two' && ![...document.querySelectorAll('.session-tabs [role=tab]')].some(x=>x.textContent.startsWith('one'))"), true);
    await command("Page.reload");
    assert.equal(await wait("document.querySelector('.session-header h1')?.textContent==='two' && document.querySelectorAll('.session-tabs [role=tab]').length===3"), true, "restore validated open/active identities, prune absent global tab");
    await evaluate("document.querySelector('[aria-label=\"Open command palette\"]').click()");
    assert.equal(await wait("document.querySelector('.command-palette')?.open && !!document.querySelector('#palette-option-nextTab')"), true);
    await evaluate("document.querySelector('#palette-option-nextTab').click()");
    assert.equal(await wait("document.querySelector('.session-header h1')?.textContent==='other-session' && !document.querySelector('.command-palette')"), true);
    await evaluate("document.querySelector('.session-tabs [aria-selected=true]').focus(); document.activeElement.dispatchEvent(new KeyboardEvent('keydown',{key:'ArrowLeft',ctrlKey:true,altKey:true,bubbles:true,cancelable:true}))");
    assert.equal(await wait("document.querySelector('.session-header h1')?.textContent==='two' && document.activeElement.getAttribute('aria-selected')==='true'"), true);
    await evaluate("document.querySelector('.session-tabs [aria-selected=true]').focus(); document.activeElement.dispatchEvent(new KeyboardEvent('keydown',{key:'ArrowRight',bubbles:true,cancelable:true}))");
    assert.equal(await wait("document.querySelector('.session-header h1')?.textContent==='other-session' && document.activeElement.getAttribute('role')==='tab'"), true);
    await evaluate("window.publishLayoutCatalog({...settingsShellFixture.catalog,sessions:[...settingsShellFixture.catalog.sessions,settingsShellFixture.catalog.sessions.find(x=>x.id==='two')]})");
    assert.equal(await wait("document.querySelectorAll('.session-tabs [role=tab]').length===2 && document.querySelector('.session-header h1')?.textContent==='other-session'"), true, "ambiguous inactive ID is pruned without switching the active workspace");
    assert.equal(await evaluate("settingsShellFixture.creates.length===0 && settingsShellFixture.deleteRequests.length===0 && settingsShellFixture.probes.length===0 && settingsShellFixture.displayCalls.length-settingsShellFixture.displayCleanup.length===1"), true);
    layoutObservations.push({ label: "session-tabs", scrollRestored: tabScroll,
      state: await evaluate("JSON.parse(localStorage.getItem('codealta.desktop.sessionTabs.v1'))") });
    while (Number(await evaluate("document.querySelectorAll('.close-session-tab').length"))) {
      await evaluate("document.querySelector('.close-session-tab').click()"); await frames();
    }
    assert.equal(await wait("document.querySelector('.session-header h1')?.textContent==='Prompt draft' && settingsShellFixture.displayCalls.length===settingsShellFixture.displayCleanup.length"), true);
    await command("Page.reload");
    assert.equal(await wait("document.querySelector('.session-header h1')?.textContent==='Prompt draft' && document.querySelector('.session-tabs > button').disabled"), true, "closed last tab restores local draft; closed history is window-local");
    // Cross-project active-close/reconcile must invalidate project edits just as
    // clicking a project does, including a delayed preflight across A -> B -> A.
    for (const transition of ["close", "reconcile"]) {
      await evaluate(`window.unmountShellFixture(); localStorage.clear(); localStorage.setItem('settingsFixtureOwned','true'); localStorage.setItem('settingsFixtureSecondProject','true');
        localStorage.setItem('draftTabFixture','true'); localStorage.setItem('layoutFixtureLive','true'); localStorage.setItem('navigationFixture','mixed')`);
      await command("Page.reload");
      assert.equal(await wait("!!document.querySelector('#session-prompt')"), true);
      await evaluate("document.querySelector('#project-list button[title=\"/fixture/other\"]').click()");
      assert.equal(await wait("document.querySelector('.session-header h1')?.textContent==='other-session'"), true);
      await evaluate("document.querySelector('[aria-label=\"Create session\"]').click(); document.querySelector('[aria-label^=\"Rename project\"]').click()");
      assert.equal(await wait("settingsShellFixture.projectNameReads.length===1 && !!document.querySelector('.session-create')"), true);
      if (transition === "close") await evaluate("document.querySelector('.session-tabs [aria-selected=true]').parentElement.querySelector('.close-session-tab').click()");
      else await evaluate("window.publishLayoutCatalog({...settingsShellFixture.catalog,sessions:settingsShellFixture.catalog.sessions.filter(x=>x.id!=='other-session')})");
      assert.equal(await wait("document.querySelector('.session-header h1')?.textContent==='one'"), true);
      assert.equal(await evaluate("!!document.querySelector('.session-create')"), false, `${transition}: scope transition closes old create form`);
      await evaluate("window.publishLayoutCatalog(settingsShellFixture.catalog)"); await frames();
      await evaluate("document.querySelector('#project-list button[title=\"/fixture/other\"]').click(); settingsShellFixture.releaseProjectName()");
      await frames();
      assert.equal(await evaluate("!!document.querySelector('.project-rename')"), false, `${transition}: late prior-scope preflight must not revive edit UI after ABA`);
      await evaluate("document.querySelector('[aria-label^=\"Rename project\"]').click()");
      assert.equal(await wait("settingsShellFixture.projectNameReads.length===1"), true);
      await evaluate("settingsShellFixture.releaseProjectName()");
      assert.equal(await wait("!!document.querySelector('.project-rename')"), true);
      await workflowLanguages(evaluate, workflowCalls, ".project-rename", ".project-rename button", "Save project name");
      if (transition === "close") {
        await evaluate("(()=>{const input=document.querySelector('.project-rename input');Object.getOwnPropertyDescriptor(HTMLInputElement.prototype,'value').set.call(input,'Settings');input.dispatchEvent(new Event('input',{bubbles:true}));})()");
        await workflowLanguages(evaluate, workflowCalls, ".project-rename", ".project-rename button", "Save project name");
        await evaluate("document.querySelector('.project-rename button').click()");
        assert.equal(await wait("settingsShellFixture.projectRenames.length===1"), true);
        await workflowLanguages(evaluate, workflowCalls, ".project-rename", ".project-rename button", "Save project name", "code", "settingsShellFixture.projectRenames[0].request");
        await evaluate("settingsShellFixture.projectRenames[0].reject(Error('fake lost reply'))");
        assert.equal(await wait("document.querySelector('.project-rename input').disabled"), true);
        await frames();
        await workflowLanguages(evaluate, workflowCalls, ".project-rename", ".project-rename button", "Save project name", "code", "settingsShellFixture.projectRenames[0].request");
        assert.equal(await evaluate("settingsShellFixture.projectRenames[0].request.displayName==='Settings'&&settingsShellFixture.projectRenames.length===1"), true);
      }
      if (transition === "close") await evaluate("document.querySelector('.session-tabs [aria-selected=true]').parentElement.querySelector('.close-session-tab').click()");
      else await evaluate("window.publishLayoutCatalog({...settingsShellFixture.catalog,sessions:settingsShellFixture.catalog.sessions.filter(x=>x.id!=='other-session')})");
      assert.equal(await wait("document.querySelector('.session-header h1')?.textContent==='one' && !document.querySelector('.project-rename')"), true);
    }
    const editLocal = (text: string) => evaluate(`(() => { const input=document.querySelector('#catalog-prompt');
      Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype,'value').set.call(input,${JSON.stringify(text)});
      input.dispatchEvent(new Event('input',{bubbles:true})); })()`);
    for (const change of ["valid", "edit", "input-aba", "cancel", "cancel-before-reply", "scope-aba", "tab-aba", "settings-aba", "missing", "error", "failure", "duplicate", "destination", "catalog", "archived", "unavailable"]) {
      await evaluate(`window.unmountShellFixture(); localStorage.clear(); localStorage.setItem('settingsFixtureOwned',${JSON.stringify(change !== "catalog" ? "true" : "false")});
        localStorage.setItem('settingsFixtureSecondProject','true'); localStorage.setItem('layoutFixtureLive','true'); localStorage.setItem('navigationFixture','mixed')`);
      await command("Page.reload");
      assert.equal(await wait("document.querySelector('.session-header h1')?.textContent==='one'"), true);
      await evaluate("document.querySelector('.session-tabs [role=tab]').click()");
      assert.equal(await wait("document.querySelector('.session-header h1')?.textContent==='Prompt draft' && !!document.querySelector('#catalog-prompt')"), true);
      await editLocal("  Original local prompt\nexact  ");
      assert.equal(await evaluate("settingsShellFixture.creates.length===0 && settingsShellFixture.sends.length===0 && settingsShellFixture.displayCalls.length===settingsShellFixture.displayCleanup.length && !document.querySelector('.session-tabs [aria-selected=true]').parentElement.querySelector('.close-session-tab')"), true);
      if (change === "archived") await evaluate("window.publishLayoutCatalog({...settingsShellFixture.catalog,projects:settingsShellFixture.catalog.projects.map(p=>({...p,archived:true}))})");
      if (change === "unavailable") await evaluate("window.publishLayoutState({kind:'error',message:'Unavailable'})");
      if (["catalog", "archived", "unavailable"].includes(change)) {
        assert.equal(await wait("[...document.querySelectorAll('button')].find(b=>b.textContent==='Create and transfer draft')?.disabled"), true);
        assert.equal(await evaluate("settingsShellFixture.creates.length"), 0); continue;
      }
      if (change === "valid") {
        await evaluate("document.querySelector('.project-root-list button').click()");
        assert.equal(await wait("document.querySelector('#catalog-prompt')?.value===''"), true);
        await editLocal("Global draft");
        await evaluate("document.querySelector('#project-list button[title=\"/fixture/project\"]').click()");
        assert.equal(await wait("document.querySelector('#catalog-prompt')?.value==='  Original local prompt\\nexact  '"), true);
        await evaluate("[...document.querySelectorAll('.session-tabs [role=tab]')].find(b=>b.textContent.startsWith('one')).click()");
        assert.equal(await wait("!!document.querySelector('[data-reminder-count]')"), true);
        await evaluate("localStorage.setItem('layoutFixtureReminders','true'); document.querySelector('[data-reminder-count]').click()");
        assert.equal(await wait("!!document.querySelector('.reminders-destination') && !document.querySelector('.workspace-shell')"), true);
        await evaluate("document.querySelector('.reminders-destination > button').click()");
        assert.equal(await wait("!!document.querySelector('.session-tabs')"), true);
        await evaluate("document.querySelector('.session-tabs [role=tab]').click()");
        assert.equal(await wait("document.querySelector('#catalog-prompt')?.value==='  Original local prompt\\nexact  '"), true);
        await evaluate("document.querySelector('[aria-label=\"Expand prompt editor\"]').click()");
        assert.equal(await wait("document.querySelector('.expanded-prompt-dialog')?.open && document.querySelector('[aria-label=\"Expanded prompt\"]').value.includes('Original local prompt')"), true);
        await evaluate("document.querySelector('.expanded-prompt-dialog button').click()");
        const editingViewport = await evaluate("({width:innerWidth,height:innerHeight})") as { width: number; height: number };
        for (const locale of ["de", "ja", "en"]) {
          await evaluate("document.querySelector('.project-rail .icon-label-button').click()");
          await evaluate("document.querySelector('[data-settings-section=appearance]').click()");
          await chooseLanguage(locale);
          assert.equal(await wait(`document.documentElement.lang===${JSON.stringify(locale)}`), true);
          await evaluate("document.querySelector('.settings-dialog-header button').click()");
          await evaluate("window.tabLocaleDraft=document.querySelector('#catalog-prompt');window.tabLocaleText=tabLocaleDraft.value;document.querySelector('.close-session-tab').click()");
          assert.equal(await wait("document.querySelectorAll('.close-session-tab').length===0 && tabLocaleDraft===document.querySelector('#catalog-prompt')"), true);
          await evaluate(`document.querySelector('.session-tabs > button').click()`);
          assert.equal(await wait("document.querySelector('.session-header h1')?.textContent==='one'"), true);
          await evaluate("document.querySelector('.session-tabs [role=tab]').click()");
          assert.equal(await wait("document.querySelector('#catalog-prompt')?.value===tabLocaleText && settingsShellFixture.sends.length===0 && settingsShellFixture.creates.length===0"), true, `${locale}: localized close/reopen preserves draft and never submits or creates`);
          for (const theme of ["light", "dark"]) {
            await command("Emulation.setDeviceMetricsOverride", { width: 390, height: 500, deviceScaleFactor: 1, mobile: false });
            await evaluate(`document.documentElement.dataset.theme=${JSON.stringify(theme)};document.querySelector('#catalog-prompt').focus()`);
            await editLocal("");
            await command("Input.dispatchKeyEvent", { type: "keyDown", key: "/", code: "Slash", text: "/" });
            await command("Input.dispatchKeyEvent", { type: "keyUp", key: "/", code: "Slash" });
            assert.equal(await wait("document.querySelector('.command-palette')?.open && document.querySelector('#catalog-prompt').value===''"), true);
            await evaluate("document.querySelector('.command-palette header button').click()");
            assert.equal(await wait("document.activeElement===document.querySelector('#catalog-prompt')"), true);
            await command("Input.dispatchKeyEvent", { type: "keyDown", key: "?", code: "Slash", text: "?", modifiers: 8 });
            await command("Input.dispatchKeyEvent", { type: "keyUp", key: "?", code: "Slash", modifiers: 8 });
            assert.equal(await wait("!!document.querySelector('.shortcut-dialog') && document.querySelector('#catalog-prompt').value===''"), true, `${locale} ${theme}: empty native /? opens transient help`);
            await evaluate("document.querySelector('.shortcut-dialog header button').click()");
            assert.equal(await wait("document.activeElement===document.querySelector('#catalog-prompt')"), true);
            await command("Input.insertText", { text: "literal /? 日本語" });
            await evaluate("document.querySelector('#catalog-prompt').dispatchEvent(new KeyboardEvent('keydown',{key:'?',isComposing:true,bubbles:true}))");
            assert.equal(await evaluate("document.querySelector('#catalog-prompt').value==='literal /? 日本語' && !document.querySelector('.shortcut-dialog')"), true);
            await evaluate(`document.querySelector('[aria-label=${JSON.stringify(translate(locale, "Expand prompt editor"))}]').click()`);
            assert.equal(await wait("document.querySelector('.expanded-prompt-dialog')?.open && document.activeElement===document.querySelector('.expanded-prompt-dialog textarea')"), true);
            assert.equal(await evaluate("(()=>{const d=document.querySelector('.expanded-prompt-dialog');return d.getBoundingClientRect().width<=innerWidth && d.getBoundingClientRect().height<=innerHeight && d.scrollWidth<=d.clientWidth+1})()"), true, `${locale} ${theme}: expanded controls fit the short narrow viewport`);
            await command("Input.dispatchKeyEvent", { type: "keyDown", key: "Escape", code: "Escape", windowsVirtualKeyCode: 27 });
            await command("Input.dispatchKeyEvent", { type: "keyUp", key: "Escape", code: "Escape", windowsVirtualKeyCode: 27 });
            assert.equal(await wait("!document.querySelector('.expanded-prompt-dialog') && document.querySelector('#catalog-prompt').value==='literal /? 日本語'"), true);
          }
        }
        await command("Emulation.setDeviceMetricsOverride", { ...editingViewport, deviceScaleFactor: 1, mobile: false });
        await editLocal("  Original local prompt\nexact  ");
        await evaluate("document.querySelector('.rail-footer .icon-label-button').click()");
        assert.equal(await wait("document.querySelector('.settings-dialog')?.open"), true);
        await evaluate("document.querySelector('[aria-label=\"Close settings\"]').click()");
      }
      await evaluate("localStorage.setItem('creationFixtureHoldSnapshot','true'); const button=[...document.querySelectorAll('button')].find(b=>b.textContent==='Create and transfer draft'); button.click(); button.click()");
      assert.equal(await wait("settingsShellFixture.creates.length===1"), true);
      assert.equal(await evaluate("document.querySelector('[aria-label=\"Create session\"]').disabled"), true);
      if (change === "cancel-before-reply") {
        await evaluate("[...document.querySelectorAll('button')].find(b=>b.textContent==='Cancel transfer').click(); settingsShellFixture.releaseCreate()");
      } else if (change === "failure") {
        await evaluate("settingsShellFixture.releaseCreate('create_unconfirmed')");
      } else {
        await evaluate("settingsShellFixture.releaseCreate()");
        assert.equal(await wait("settingsShellFixture.snapshots.length===1"), true);
        if (change === "edit" || change === "input-aba") { await editLocal("Newer local prompt"); if (change === "input-aba") await editLocal("  Original local prompt\nexact  "); }
        if (change === "cancel") await evaluate("[...document.querySelectorAll('button')].find(b=>b.textContent==='Cancel transfer').click()");
        if (change === "scope-aba") {
          await evaluate("document.querySelector('#project-list button[title=\"/fixture/other\"]').click()");
          await editLocal("Other scope prompt");
          await evaluate("document.querySelector('#project-list button[title=\"/fixture/project\"]').click()");
        }
        if (change === "tab-aba") {
          await evaluate("[...document.querySelectorAll('.session-tabs [role=tab]')].find(b=>b.textContent.startsWith('one')).click()");
          await evaluate("document.querySelector('.session-tabs [role=tab]').click()");
        }
        if (change === "settings-aba") {
          await evaluate("document.querySelector('.rail-footer .icon-label-button').click()");
          assert.equal(await wait("document.querySelector('.settings-dialog')?.open"), true);
          await evaluate("document.querySelector('[aria-label=\"Close settings\"]').click()");
        }
        if (change === "destination") await evaluate("localStorage.setItem('codealta.desktop.prompt.created','Existing newer session draft')");
        if (change === "duplicate") await evaluate("settingsShellFixture.catalog.sessions.push({...settingsShellFixture.catalog.sessions[0],id:'created'})");
        await evaluate(`settingsShellFixture.releaseSnapshot(${JSON.stringify(change === "missing" || change === "error" ? change : "ok")})`);
      }
      assert.equal(await wait("!document.querySelector('[aria-label=\"Create session\"]').disabled"), true);
      assert.equal(await evaluate("settingsShellFixture.creates.length===1 && settingsShellFixture.sends.length===0 && document.querySelector('[aria-label=\"Original creation draft 1\"]').value==='  Original local prompt\\nexact  '"), true, `${change}: original evidence and no implicit Send`);
      if (change === "valid") {
        assert.equal(await wait("document.querySelector('.session-header h1')?.textContent==='created' && document.querySelector('#session-prompt')?.value==='  Original local prompt\\nexact  '"), true);
        await evaluate("document.querySelector('.session-tabs [role=tab]').click()");
      } else assert.equal(await evaluate("document.querySelector('.session-header h1').textContent"), "Prompt draft", `${change}: no stale transfer/navigation`);
      assert.equal(await evaluate("document.querySelector('#catalog-prompt').value"), change === "edit" ? "Newer local prompt" : "  Original local prompt\nexact  ");
      if (change === "valid") {
        await evaluate("localStorage.removeItem('creationFixtureHoldSnapshot'); window.unmountShellFixture()");
        await command("Page.reload");
        assert.equal(await wait("!!document.querySelector('#catalog-prompt')"), true);
        await evaluate("document.querySelector('#project-list button[title=\"/fixture/project\"]').click()");
        assert.equal(await wait("document.querySelector('#catalog-prompt')?.value==='  Original local prompt\\nexact  '"), true, "scope draft reload restores stored text without a create");
        assert.equal(await evaluate("settingsShellFixture.creates.length"), 0);
      }
    }
    // Actual composer -> scoped search -> insertion -> frozen reference-enabled Send.
    await evaluate("window.unmountShellFixture(); localStorage.clear(); localStorage.setItem('settingsFixtureOwned','true'); localStorage.setItem('layoutFixtureLive','true'); localStorage.setItem('navigationFixture','mixed')");
    await command("Page.reload");
    assert.equal(await wait("!!document.querySelector('#session-prompt')"), true);
    await evaluate(`(() => { const input=document.querySelector('#session-prompt'); input.focus();
      Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype,'value').set.call(input,'@sr');
      input.dispatchEvent(new Event('input',{bubbles:true})); })()`);
    assert.equal(await wait("settingsShellFixture.referenceReads.length===1"), true);
    const referenceCalls = "[settingsShellFixture.rpcCalls,settingsShellFixture.referenceReads,settingsShellFixture.referenceObservations,settingsShellFixture.sends,document.querySelector('#session-prompt')?.value,document.querySelector('#session-prompt')?.selectionStart,document.querySelector('#session-prompt')?.selectionEnd]";
    const referenceLanguages = () => workflowLanguages(evaluate, referenceCalls, ".reference-presentation", ".reference-presentation button", "Check reference metadata", "pre", "settingsShellFixture.referenceReads[0].request");
    await workflowLanguages(evaluate, referenceCalls, ".project-reference-picker", ".project-reference-picker > button", "Close references", ".reference-path", "settingsShellFixture.referenceReads[0].request");
    assert.equal(await evaluate("settingsShellFixture.referenceReads[0].request.projectId==='project' && settingsShellFixture.referenceReads[0].request.projectPath==='/fixture/project' && settingsShellFixture.referenceReads[0].request.sessionId==='one'"), true);
    for (const [value, count] of [["@sx", 2], ["@sr", 3]] as const) {
      await evaluate(`(() => { const input=document.querySelector('#session-prompt');
        Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype,'value').set.call(input,${JSON.stringify(value)});
        input.dispatchEvent(new Event('input',{bubbles:true})); })()`);
      assert.equal(await wait(`settingsShellFixture.referenceReads.length===${count}`), true);
    }
    await evaluate("settingsShellFixture.releaseReferences(0)"); await frames();
    assert.equal(await evaluate("document.querySelectorAll('.project-reference-picker [role=option]').length"), 0, "input ABA cannot revive an old query result");
    await evaluate("settingsShellFixture.releaseReferences()");
    assert.equal(await wait("document.querySelectorAll('.project-reference-picker [role=option]').length===2"), true);
    await workflowLanguages(evaluate, referenceCalls, ".project-reference-picker", ".project-reference-picker > button", "Close references", ".reference-path", "settingsShellFixture.referenceReads[0].request");
    await evaluate("document.querySelector('#session-prompt').dispatchEvent(new KeyboardEvent('keydown',{key:'Enter',isComposing:true,bubbles:true,cancelable:true}))");
    assert.equal(await evaluate("document.querySelector('#session-prompt').value"), "@sr");
    await evaluate(`(() => { const original=requestAnimationFrame; window.heldInsertion=[];
      window.requestAnimationFrame=callback => { heldInsertion.push(callback); return 987654; };
      document.querySelector('#session-prompt').dispatchEvent(new KeyboardEvent('keydown',{key:'Enter',bubbles:true,cancelable:true}));
      window.requestAnimationFrame=original; })()`);
    assert.equal(await wait(`document.querySelector('#session-prompt')?.value==='@"src/app.cs" ' && !document.querySelector('.project-reference-picker')`), true);
    for (const value of ['changed draft', '@"src/app.cs" ']) {
      await evaluate(`(() => { const input=document.querySelector('#session-prompt');
        Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype,'value').set.call(input,${JSON.stringify(value)});
        input.dispatchEvent(new Event('input',{bubbles:true})); })()`); await frames();
    }
    await referenceLanguages();
    await evaluate("document.querySelector('#session-prompt').setSelectionRange(0,0); document.querySelector('.send-button').focus(); heldInsertion.forEach(callback=>callback(performance.now()))");
    assert.equal(await evaluate("document.activeElement===document.querySelector('.send-button') && document.querySelector('#session-prompt').selectionStart===0"), true,
      "deferred insertion must not focus or move caret after equal-text input ABA");
    assert.equal(await evaluate("settingsShellFixture.sends.length"), 0, "popup Enter is not Send");
    for (const change of ["valid", "scope-aba", "replacement"] as const) {
      const reads = await evaluate("settingsShellFixture.referenceReads.length") as number;
      await evaluate(`(() => { const input=document.querySelector('#session-prompt');
        Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype,'value').set.call(input,'@sr');
        input.dispatchEvent(new Event('input',{bubbles:true})); })()`);
      assert.equal(await wait(`settingsShellFixture.referenceReads.length===${reads + 1}`), true);
      await evaluate("settingsShellFixture.releaseReferences()");
      assert.equal(await wait("document.querySelectorAll('.project-reference-picker [role=option]').length===2"), true);
      await evaluate(`(() => { const original=requestAnimationFrame; window.heldInsertion=[];
        window.requestAnimationFrame=callback => { heldInsertion.push(callback); return 987654; };
        document.querySelector('.project-reference-picker [role=option]').click(); window.requestAnimationFrame=original; })()`);
      await frames();
      if (change === "scope-aba") {
        await evaluate("document.querySelector('.rail-footer .icon-label-button').click()"); await frames();
        await evaluate("document.querySelector('[aria-label=\"Close settings\"]').click()"); await frames();
      }
      if (change === "replacement") {
        await evaluate("document.querySelector('#expand-session-prompt').click()"); await frames();
        await evaluate("document.querySelector('.expanded-prompt-dialog button').click()"); await frames();
      }
      await evaluate("document.querySelector('#session-prompt').setSelectionRange(0,0); document.querySelector('.send-button').focus(); heldInsertion.forEach(callback=>callback(performance.now()))");
      assert.equal(await evaluate(change === "valid"
        ? "document.activeElement===document.querySelector('#session-prompt') && document.querySelector('#session-prompt').selectionStart===14"
        : "document.activeElement===document.querySelector('.send-button') && document.querySelector('#session-prompt').selectionStart===0"), true, `${change}: deferred focus lifetime`);
    }
    assert.equal(await evaluate("settingsShellFixture.referenceObservations.length"), 0, "typing does not automatically validate metadata");
    await evaluate("document.querySelector('.reference-presentation button').click()");
    await referenceLanguages();
    await evaluate("settingsShellFixture.releaseReferenceObservation()");
    assert.equal(await wait("!!document.querySelector('.reference-presentation .reference-resolved')"), true);
    assert.equal(await evaluate("document.querySelector('.reference-presentation pre').textContent===document.querySelector('#session-prompt').value && document.querySelector('#session-prompt').selectionStart===0"), true, "inline raw preview leaves caret and input unchanged");
    await referenceLanguages();
    await evaluate("document.querySelector('#expand-session-prompt').click()"); await frames();
    await evaluate("document.querySelector('.reference-presentation button').focus()");
    await command("Input.dispatchKeyEvent", { type: "keyDown", key: "Enter", code: "Enter", text: "\r", windowsVirtualKeyCode: 13 });
    await command("Input.dispatchKeyEvent", { type: "keyUp", key: "Enter", code: "Enter", windowsVirtualKeyCode: 13 });
    assert.equal(await wait("settingsShellFixture.referenceObservations.length===2 && document.querySelector('.expanded-prompt-dialog')?.open"), true, "expanded metadata button has native keyboard activation, not editor close");
    await evaluate("settingsShellFixture.releaseReferenceObservation()");
    assert.equal(await wait("!!document.querySelector('.reference-presentation .reference-resolved')"), true, "expanded editor presents host spans");
    await workflowNarrow(evaluate, command, ".expanded-prompt-dialog");
    await evaluate("document.querySelector('.expanded-prompt-dialog button').click()"); await frames();
    await evaluate("document.querySelector('.send-button').click()");
    assert.equal(await wait("settingsShellFixture.sends.length===1"), true);
    assert.equal(await evaluate(`settingsShellFixture.sends[0].references.projectId==='project' && settingsShellFixture.sends[0].references.projectPath==='/fixture/project' && settingsShellFixture.sends[0].text==='@"src/app.cs" '`), true);
    await evaluate("document.querySelector('.session-tabs [role=tab]').click()");
    const localReferenceReads = await evaluate("settingsShellFixture.referenceReads.length") as number;
    await editLocal("@sr");
    assert.equal(await wait(`settingsShellFixture.referenceReads.length===${localReferenceReads + 1}`), true);
    await editLocal("@ok @missing @@literal");
    const metadataReads = await evaluate("settingsShellFixture.referenceObservations.length") as number;
    await evaluate("document.querySelector('#catalog-prompt').dispatchEvent(new CompositionEvent('compositionstart',{bubbles:true})); document.querySelector('.reference-presentation button').click()");
    assert.equal(await evaluate("settingsShellFixture.referenceObservations.length"), metadataReads, "IME does not initiate metadata reads");
    await evaluate("document.querySelector('#catalog-prompt').dispatchEvent(new CompositionEvent('compositionend',{bubbles:true})); document.querySelector('.reference-presentation button').click(); settingsShellFixture.releaseReferenceObservation(undefined,[{start:0,length:3,status:'resolved'},{start:4,length:8,status:'unresolved'},{start:13,length:2,status:'escaped'}])");
    assert.equal(await wait("document.querySelectorAll('.reference-presentation mark').length===3"), true);
    assert.equal(await evaluate("document.querySelector('.reference-presentation pre').textContent==='@ok @missing @@literal' && !!document.querySelector('.reference-unresolved') && !!document.querySelector('.reference-escaped') && document.querySelector('#catalog-prompt').value==='@ok @missing @@literal'"), true);
    await referenceLanguages();
    await evaluate("document.querySelector('.reference-presentation button').click()");
    await editLocal("edited"); await editLocal("@ok @missing @@literal");
    await evaluate("settingsShellFixture.releaseReferenceObservation()"); await frames();
    assert.equal(await evaluate("!document.querySelector('.reference-presentation pre')"), true, "equal-text input ABA cannot revive observed metadata");
    await evaluate("document.querySelector('.reference-presentation button').click()");
    await evaluate("document.querySelector('.project-root-list button').click(); settingsShellFixture.releaseReferences()");
    await evaluate("settingsShellFixture.releaseReferenceObservation()");
    await frames();
    assert.equal(await evaluate("!document.querySelector('.project-reference-picker') && settingsShellFixture.creates.length===0 && settingsShellFixture.sends.length===1"), true, "stale local-draft results cannot cross scope or allocate a session");
    assert.equal(await evaluate("!document.querySelector('.reference-presentation pre')"), true, "stale metadata cannot cross draft scope");
    // Native child dialogs publish the existing lifetime at the transition itself.
    // No locale or unrelated App update is used to settle these transitions.
    for (const [trigger, dialog] of [["[aria-label='Session info']", ".session-info-dialog"],
      [".project-details-trigger", ".project-details-dialog"], ["#expand-session-prompt", ".expanded-prompt-dialog"]]) {
      await evaluate("window.unmountShellFixture(); localStorage.clear(); localStorage.setItem('settingsFixtureOwned','true'); localStorage.setItem('layoutFixtureLive','true'); localStorage.setItem('navigationFixture','mixed')");
      await command("Page.reload");
      assert.equal(await wait("!!document.querySelector('#session-prompt')"), true);
      await evaluate(`(() => { const input=document.querySelector('#session-prompt'); input.focus();
        Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype,'value').set.call(input,'@sr');
        input.dispatchEvent(new Event('input',{bubbles:true})); })()`);
      assert.equal(await wait("settingsShellFixture.referenceReads.length===1"), true);
      await evaluate("void(window.transitionInput=document.querySelector('#session-prompt'))");
      for (let cycle = 0; cycle < 2; cycle++) {
        await evaluate("document.querySelector('.reference-presentation button').click()");
        assert.equal(await wait(`settingsShellFixture.referenceObservations.length===${cycle + 1} && document.querySelector('.reference-presentation button').disabled`), true);
        // Complete the old read inside the native event, before a later task could
        // hide a publication gap. Creation's synchronous invalidation is untouched.
        await evaluate(`window.transitionRequest=settingsShellFixture.referenceObservations[${cycle}].request;
          document.addEventListener('beforetoggle',()=>settingsShellFixture.releaseReferenceObservation(${cycle}),{capture:true,once:true});
          document.querySelector(${JSON.stringify(trigger)}).click()`);
        assert.equal(await wait(`!!document.querySelector(${JSON.stringify(dialog + "[open]")})`), true);
        await frames();
        assert.equal(await evaluate(`!document.querySelector('.reference-presentation pre') && settingsShellFixture.referenceObservations[${cycle}].request===transitionRequest && transitionRequest.text==='@sr' && document.querySelector('#session-prompt')===transitionInput && transitionInput.value==='@sr'`), true, `${dialog}/${cycle}: late pre-transition observation cannot publish or replace draft`);
        await evaluate(`document.querySelector(${JSON.stringify(dialog)}).querySelector('header button,button').click()`);
        assert.equal(await wait(`!document.querySelector(${JSON.stringify(dialog)})`), true);
        await frames();
        assert.equal(await evaluate(`!document.querySelector('.reference-presentation pre') && !document.querySelector('.reference-presentation button').disabled && settingsShellFixture.referenceObservations.length===${cycle + 1} && settingsShellFixture.referenceReads.length===1`), true, `${dialog}/${cycle}: close publishes without another reference request`);
      }
      // A new read after open/close/reopen ABA remains owned across all locales;
      // neither the locale cycle nor a duplicate old completion may cancel it.
      await evaluate("document.querySelector('.reference-presentation button').click()");
      assert.equal(await wait("settingsShellFixture.referenceObservations.length===3 && document.querySelector('.reference-presentation button').disabled"), true);
      await referenceLanguages();
      await evaluate("settingsShellFixture.releaseReferenceObservation(0); settingsShellFixture.releaseReferenceObservation(2)");
      assert.equal(await wait("document.querySelector('.reference-presentation pre')?.textContent==='@sr'"), true);
      assert.equal(await evaluate("settingsShellFixture.sends.length===0 && settingsShellFixture.creates.length===0 && settingsShellFixture.referenceObservations.length===3 && document.querySelector('#session-prompt')===transitionInput && transitionInput.value==='@sr'"), true);
      await evaluate("settingsShellFixture.releaseReferences(0)"); await frames();
      assert.equal(await evaluate("!document.querySelector('.project-reference-picker')"), true, `${dialog}: pre-transition search cannot return after modal ABA`);
      await evaluate(`(() => { const input=document.querySelector('#session-prompt');
        Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype,'value').set.call(input,'@sx');
        input.dispatchEvent(new Event('input',{bubbles:true})); })()`);
      assert.equal(await wait("settingsShellFixture.referenceReads.length===2"), true);
      await evaluate("settingsShellFixture.releaseReferences(1)");
      assert.equal(await wait("document.querySelectorAll('.project-reference-picker [role=option]').length===2"), true);
      await evaluate(`(() => { const original=requestAnimationFrame; window.heldModalInsertion=[];
        window.requestAnimationFrame=callback=>{heldModalInsertion.push(callback);return 987654;};
        document.querySelector('.project-reference-picker [role=option]').click(); window.requestAnimationFrame=original; })()`);
      for (let cycle = 0; cycle < 2; cycle++) {
        await evaluate(`document.querySelector(${JSON.stringify(trigger)}).click()`);
        assert.equal(await wait(`!!document.querySelector(${JSON.stringify(dialog + "[open]")})`), true);
        await evaluate(`document.querySelector(${JSON.stringify(dialog)}).querySelector('header button,button').click()`);
        assert.equal(await wait(`!document.querySelector(${JSON.stringify(dialog)})`), true);
        await frames();
      }
      await evaluate("transitionInput.setSelectionRange(0,0); document.querySelector('.send-button').focus(); heldModalInsertion.forEach(callback=>callback(performance.now()))");
      assert.equal(await evaluate(`document.activeElement===document.querySelector('.send-button') && transitionInput.selectionStart===0 && transitionInput.selectionEnd===0 && transitionInput.value==='@"src/app.cs" ' && settingsShellFixture.sends.length===0 && settingsShellFixture.referenceObservations.length===3`), true, `${dialog}: old insertion cannot regain focus authority after open/close/reopen ABA`);
    }
    // Saved-session browser uses loaded catalog metadata and the ordinary tab transition.
    await evaluate("window.unmountShellFixture(); localStorage.clear(); localStorage.setItem('settingsFixtureOwned','true'); localStorage.setItem('layoutFixtureLive','true'); localStorage.setItem('navigationFixture','tabs')");
    await command("Page.reload");
    assert.equal(await wait("!!document.querySelector('#session-prompt')"), true);
    await evaluate(`(() => { const input=document.querySelector('#session-prompt'); input.focus();
      Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype,'value').set.call(input,'Browser original draft'); input.dispatchEvent(new Event('input',{bubbles:true}));
      input.dispatchEvent(new KeyboardEvent('keydown',{key:'b',ctrlKey:true,altKey:true,bubbles:true,cancelable:true})); })()`);
    assert.equal(await evaluate("!document.querySelector('.session-browser')"), true, "Ctrl+Alt+B does not override text editing");
    await evaluate("document.querySelector('.send-button').click()");
    assert.equal(await wait("settingsShellFixture.sends.length===1"), true);
    await evaluate("document.querySelector('.timeline-scroll').scrollTop=120; document.querySelector('.timeline-scroll').dispatchEvent(new Event('scroll'))"); await frames();
    const browserScroll = await evaluate("document.querySelector('.timeline-scroll').scrollTop");
    const browserAttachmentReads = await evaluate("settingsShellFixture.displayCalls.length");
    for (const extra of ["repeat:true", "isComposing:true"]) {
      await evaluate(`document.querySelector('.browse-sessions-button').dispatchEvent(new KeyboardEvent('keydown',{key:'b',ctrlKey:true,altKey:true,bubbles:true,cancelable:true,${extra}}))`);
      assert.equal(await evaluate("!document.querySelector('.session-browser')"), true);
    }
    await evaluate("document.querySelector('.browse-sessions-button').focus(); document.querySelector('.browse-sessions-button').dispatchEvent(new KeyboardEvent('keydown',{key:'b',ctrlKey:true,altKey:true,bubbles:true,cancelable:true}))");
    assert.equal(await wait("document.querySelector('.session-browser')?.open"), true);
    await evaluate("document.querySelector('[aria-label=\"Find saved sessions\"]').dispatchEvent(new KeyboardEvent('keydown',{key:'ArrowDown',bubbles:true,cancelable:true}))");
    assert.equal(await wait("document.querySelector('.session-browser [aria-selected=true]')?.textContent.includes('two')"), true);
    assert.equal(await evaluate("document.querySelector('.session-header h1').textContent==='one' && document.querySelector('.timeline-scroll').scrollTop===" + browserScroll), true, "browsing preserves hidden selection and scroll");
    const editBrowser = async (value: string) => {
      await evaluate(`(() => { const input=document.querySelector('[aria-label="Find saved sessions"]');
        Object.getOwnPropertyDescriptor(HTMLInputElement.prototype,'value').set.call(input,${JSON.stringify(value)}); input.dispatchEvent(new Event('input',{bubbles:true})); })()`); await frames();
    };
    await editBrowser("no such title");
    assert.equal(await evaluate("document.querySelector('.session-browser-results').textContent.includes('No matching') && document.querySelector('.session-header h1').textContent==='one'"), true);
    await editBrowser("TWO");
    assert.equal(await evaluate("document.querySelectorAll('.session-browser-results [role=option]').length===1 && settingsShellFixture.sends.length===1"), true);
    assert.equal(await evaluate("settingsShellFixture.displayCalls.length"), browserAttachmentReads, "opening/filtering the browser creates no display attachment");
    await evaluate("document.querySelector('[aria-label=\"Find saved sessions\"]').dispatchEvent(new KeyboardEvent('keydown',{key:'Enter',isComposing:true,bubbles:true,cancelable:true}))");
    assert.equal(await evaluate("document.querySelector('.session-browser').open"), true);
    await evaluate("document.querySelector('[aria-label=\"Find saved sessions\"]').dispatchEvent(new KeyboardEvent('keydown',{key:'Enter',bubbles:true,cancelable:true}))");
    assert.equal(await wait("document.querySelector('.session-header h1')?.textContent==='two' && !document.querySelector('.session-browser')"), true);
    assert.equal(await wait("settingsShellFixture.displayCalls.length-settingsShellFixture.displayCleanup.length===1"), true, "opening uses the single active workspace attachment");
    await evaluate("document.querySelector('.browse-sessions-button').click()");
    await editBrowser("one");
    await evaluate("document.querySelector('.session-browser-results button').click()");
    assert.equal(await wait("document.querySelector('#session-prompt')?.value==='Browser original draft' && document.querySelector('#session-prompt').disabled"), true, "browser switching retains original pending Send");
    assert.equal(await evaluate("document.querySelector('.timeline-scroll').scrollTop"), browserScroll, "browser selection reuses existing scroll retention");
    assert.equal(await evaluate("settingsShellFixture.sends.length===1 && settingsShellFixture.creates.length===0"), true);
    await evaluate("document.querySelector('.session-tabs [role=tab]').click()"); await editLocal("Browser local draft retained");
    await evaluate("document.querySelector('.browse-sessions-button').click()"); await editBrowser("one");
    await evaluate("document.querySelector('.session-browser-results button').click()"); await frames();
    await evaluate("document.querySelector('.session-tabs [role=tab]').click()");
    assert.equal(await wait("document.querySelector('#catalog-prompt')?.value==='Browser local draft retained'"), true);
    await evaluate("document.querySelector('.browse-sessions-button').click()"); await editBrowser("one");
    await evaluate("document.querySelector('.session-browser-results button').click()"); await frames();
    // Refresh, removal/move/duplicates, host change and ABA invalidate captures before activation.
    for (const change of ["refresh", "duplicate", "moved", "removed", "archive", "error", "aba", "scope-aba"] as const) {
      await evaluate("document.querySelector('.browse-sessions-button').click()");
      assert.equal(await wait("document.querySelector('.session-browser')?.open"), true);
      await editBrowser("two");
      await evaluate(`(() => { const original=settingsShellFixture.catalog; let next=structuredClone(original);
        ${change === "duplicate" ? "next.sessions.push({...next.sessions[1]});" : change === "moved" ? "next.sessions[1].workspacePath='/moved';" : change === "removed" ? "next.sessions=next.sessions.filter(row=>row.id!=='two');" : change === "archive" ? "next.projects[0].archived=true;" : ""}
        ${change === "error" ? "publishLayoutState({kind:'error',message:'Disposable catalog error'});" : change === "scope-aba" ? "" : "publishLayoutCatalog(next);"}
        ${change === "aba" ? "publishLayoutCatalog(original);" : ""} })()`); await frames();
      if (change === "scope-aba") {
        await evaluate("document.querySelector('.project-root-list button').click()"); await frames();
        await evaluate("document.querySelector('#project-list button[title=\"/fixture/project\"]').click()"); await frames();
      }
      assert.equal(await evaluate("!!document.querySelector('.session-browser [role=alert]') && [...document.querySelectorAll('.session-browser-results button')].every(button=>button.disabled)"), true, `${change}: stale results refused`);
      if (change === "duplicate") {
        await evaluate("document.querySelector('.session-browser header button').click()"); await frames();
        await evaluate("document.querySelector('.browse-sessions-button').click()"); await editBrowser("two");
        assert.equal(await evaluate("document.querySelectorAll('.session-browser-results button:disabled').length===2 && document.querySelector('.session-browser-results').textContent.includes('Ambiguous')"), true, "fresh duplicate identities remain unavailable");
      }
      await evaluate("document.querySelector('.session-browser header button').click(); publishLayoutCatalog(settingsShellFixture.catalog)"); await frames();
    }
    // Fresh archived snapshot is browsable, but existing workspace mutation rules remain read-only.
    await evaluate("publishLayoutCatalog({...settingsShellFixture.catalog,projects:settingsShellFixture.catalog.projects.map(row=>({...row,archived:true}))})"); await frames();
    await evaluate("document.querySelector('.browse-sessions-button').click()");
    await editBrowser("two"); await evaluate("document.querySelector('.session-browser-results button').click()");
    assert.equal(await wait("document.querySelector('.session-header h1')?.textContent==='two' && !document.querySelector('.owned-session')"), true);
    await evaluate("publishLayoutCatalog({...settingsShellFixture.catalog,sessions:[...settingsShellFixture.catalog.sessions,{...settingsShellFixture.catalog.sessions[0],id:'global-browser',title:'Global browser',fullTitle:'Global browser',projectId:null,scopeKind:'global',workspacePath:null}]})"); await frames();
    await evaluate("document.querySelector('.browse-sessions-button').click()");
    await evaluate("const scope=document.querySelector('[aria-label=\"Session browser scope\"]'); scope.value='global'; scope.dispatchEvent(new Event('change',{bubbles:true}))"); await frames();
    assert.equal(await evaluate("document.querySelectorAll('.session-browser-results button').length===1 && document.querySelector('.session-header h1').textContent==='two'"), true, "scope filtering does not navigate");
    for (const theme of ["light", "dark"]) {
      await command("Emulation.setDeviceMetricsOverride", { width: 390, height: 780, deviceScaleFactor: 1, mobile: false });
      await evaluate(`document.documentElement.dataset.theme=${JSON.stringify(theme)}`); await frames();
      assert.equal(await evaluate("(()=>{const r=document.querySelector('.session-browser').getBoundingClientRect();return r.left>=0 && r.right<=innerWidth && r.height<=innerHeight})()"), true, `${theme}: narrow dialog fits viewport`);
    }
    await command("Emulation.clearDeviceMetricsOverride");
    await evaluate("document.querySelector('.session-browser-results button').click()");
    assert.equal(await wait("document.querySelector('.session-header h1')?.textContent==='Global browser'"), true);
    // The palette uses its existing captured-context handoff, rather than a new navigation owner.
    await evaluate("document.querySelector('.browse-sessions-button').focus(); document.querySelector('.browse-sessions-button').dispatchEvent(new KeyboardEvent('keydown',{key:'p',ctrlKey:true,bubbles:true,cancelable:true}))");
    assert.equal(await wait("!![...document.querySelectorAll('button')].find(button=>button.textContent.includes('Browse saved sessions (Ctrl+Alt+B'))"), true);
    await evaluate("[...document.querySelectorAll('button')].find(button=>button.textContent.includes('Browse saved sessions (Ctrl+Alt+B')).click()");
    assert.equal(await wait("document.querySelector('.session-browser')?.open"), true);
    await evaluate("document.querySelector('.session-browser header button').click()");
    // Single-project metadata confirmation uses the actual App owner, never a fixture UI.
    // Explicit runtime reads use saved identities without another Display attachment or activation.
    await evaluate("document.querySelector('#project-list button[title=\"/fixture/project\"]').click()"); await frames();
    await evaluate("[...document.querySelectorAll('.session-row > button:first-child')].find(b=>b.textContent.includes('one')).click()"); await frames();
    const observationDisplayCount = await evaluate("settingsShellFixture.displayCalls.length");
    const observationOriginalSend = await evaluate("JSON.stringify(settingsShellFixture.sends[0])");
    assert.equal(await evaluate("settingsShellFixture.runtimeReads.length"), 0, "no automatic observation reads");
    await evaluate("document.querySelector('.browse-sessions-button').click()"); await frames();
    await evaluate("document.querySelector('.session-browser .runtime-observation-controls button').focus()");
    assert.equal(await evaluate("document.activeElement===document.querySelector('.session-browser .runtime-observation-controls button')"), true);
    await command("Input.dispatchKeyEvent", { type: "keyDown", key: "Enter", code: "Enter", text: "\r", windowsVirtualKeyCode: 13 });
    await command("Input.dispatchKeyEvent", { type: "keyUp", key: "Enter", code: "Enter", windowsVirtualKeyCode: 13 });
    assert.equal(await wait("settingsShellFixture.runtimeReads.length===1"), true);
    await editBrowser("two"); await editBrowser(""); await editBrowser("one");
    assert.equal(await evaluate("settingsShellFixture.runtimeReads[0].signal.aborted"), true);
    await evaluate("document.querySelector('.session-browser .runtime-observation-controls button').click()");
    assert.equal(await wait("settingsShellFixture.runtimeReads.length===2"), true);
    await evaluate("settingsShellFixture.releaseRuntime(1)");
    assert.equal(await wait("document.querySelector('.session-browser-results .runtime-observation')?.textContent==='Observed active run'"), true);
    await evaluate("settingsShellFixture.releaseRuntime(0,'absent')"); await frames();
    assert.equal(await evaluate("document.querySelector('.session-browser-results .runtime-observation')?.textContent==='Observed active run'"), true, "old canceled batch cannot overwrite current facts");
    await evaluate("(()=>{const select=document.querySelector('[aria-label=\"Order loaded sessions\"]');select.value='activity';select.dispatchEvent(new Event('change',{bubbles:true}))})()"); await frames();
    assert.equal(await evaluate("document.querySelector('.session-browser-results').textContent.includes('Observed activity · 2026-01-01T12:00:00.0000001+02:00')"), true);
    assert.equal(await evaluate("document.querySelector('.session-browser').textContent.includes('not historical latest or maximum')"), true);
    assert.equal(await evaluate("settingsShellFixture.runtimeReads.length"), 2, "changing presentation order never reads or scans");
    for (const theme of ["light", "dark"]) {
      await command("Emulation.setDeviceMetricsOverride", { width: 390, height: 780, deviceScaleFactor: 1, mobile: false });
      await evaluate(`document.documentElement.dataset.theme=${JSON.stringify(theme)}`); await frames();
      assert.equal(await evaluate("(()=>{const r=document.querySelector('.session-browser .runtime-observation').getBoundingClientRect();return r.left>=0 && r.right<=innerWidth})()"), true);
    }
    await command("Emulation.clearDeviceMetricsOverride");
    assert.equal(await evaluate("settingsShellFixture.displayCalls.length"), observationDisplayCount, "observing multiple rows does not attach hidden Display readers");
    assert.equal(await evaluate("JSON.stringify(settingsShellFixture.sends[0])"), observationOriginalSend);
    assert.equal(await evaluate("settingsShellFixture.creates.length"), 0);
    await evaluate("publishLayoutCatalog(structuredClone(settingsShellFixture.catalog)); publishLayoutCatalog(settingsShellFixture.catalog)"); await frames();
    assert.equal(await evaluate("document.querySelector('.session-browser .runtime-observation-controls button').disabled && document.querySelector('.session-tabs .runtime-observation').textContent.includes('Stale')"), true);
    assert.equal(await evaluate("document.querySelector('.session-browser-results .unknown-activity')?.textContent.includes('Unknown activity') && !document.querySelector('.session-browser-results .observed-activity')"), true, "catalog ABA withholds activity sorting evidence");
    await evaluate("document.querySelector('.session-browser header button').click()"); await frames();

    await evaluate("document.querySelector('.browse-sessions-button').focus(); document.querySelector('.browse-sessions-button').dispatchEvent(new KeyboardEvent('keydown',{key:'p',ctrlKey:true,bubbles:true,cancelable:true}))");
    assert.equal(await wait("!![...document.querySelectorAll('button')].find(button=>button.textContent.includes('Refresh statuses of open session tabs'))"), true);
    await evaluate("[...document.querySelectorAll('button')].find(button=>button.textContent.includes('Refresh statuses of open session tabs')).click()");
    assert.equal(await wait("settingsShellFixture.runtimeReads.length===3"), true);
    await evaluate("publishLayoutCatalog(structuredClone(settingsShellFixture.catalog)); publishLayoutCatalog(settingsShellFixture.catalog)"); await frames();
    assert.equal(await evaluate("settingsShellFixture.runtimeReads[2].signal.aborted"), true);
    await evaluate("settingsShellFixture.releaseRuntime(2)"); await frames();
    assert.equal(await evaluate("settingsShellFixture.runtimeReads.length"), 3, "catalog ABA cancels remaining reads");

    // Session Info uses two explicit scoped observations, never another workspace or command.
    const infoDisplays = await evaluate("settingsShellFixture.displayCalls.length");
    const infoUsageStart = Number(await evaluate("settingsShellFixture.usageReads.length"));
    const refreshInfo = () => evaluate("[...document.querySelectorAll('.session-info-dialog button')].find(b=>b.textContent==='Refresh observed details').click()");
    const copyInfo = () => evaluate("[...document.querySelectorAll('.session-info-dialog button')].find(b=>b.textContent==='Copy displayed details').click()");
    await evaluate("document.querySelector('[aria-label=\"Session info\"]').click()"); await frames();
    assert.equal(await evaluate("settingsShellFixture.runtimeReads.length"), 3, "opening Info is recorded-only until explicit refresh");
    await refreshInfo(); assert.equal(await wait("settingsShellFixture.runtimeReads.length===4"), true);
    await evaluate("settingsShellFixture.releaseRuntime(3,'info')");
    assert.equal(await wait(`settingsShellFixture.usageReads.length===${infoUsageStart + 1}`), true);
    await evaluate("settingsShellFixture.releaseInfoUsage()");
    assert.equal(await wait("document.querySelector('[aria-labelledby=\"session-info-runtime\"]')?.textContent.includes('observed-model')"), true);
    assert.equal(await evaluate("document.querySelector('[aria-labelledby=\"session-info-saved\"]').textContent.includes('fixture') && document.querySelector('[aria-labelledby=\"session-info-usage\"]').textContent.includes('0 / Unknown / 0')"), true);
    await evaluate("window.infoCopies=[]; Object.defineProperty(navigator,'clipboard',{configurable:true,value:{writeText:async text=>{infoCopies.push(text)}}})");
    await copyInfo(); assert.equal(await wait("document.querySelector('.session-info-dialog').textContent.includes('Displayed details copied.')"), true);
    assert.equal(await evaluate("infoCopies[0].includes('Saved metadata') && infoCopies[0].includes('observed-provider') && infoCopies[0].includes('pending-agent') && infoCopies[0].includes('LocalProviderUsage') && infoCopies[0].length<=32768"), true);
    await evaluate("Object.defineProperty(navigator,'clipboard',{configurable:true,value:{writeText:async()=>{throw new Error('private clipboard failure')}}})");
    await copyInfo(); assert.equal(await wait("document.querySelector('.session-info-dialog').textContent.includes('Could not copy displayed details.')"), true);
    assert.equal(await evaluate("document.querySelector('.session-info-dialog').textContent.includes('private clipboard failure')"), false);
    for (const theme of ["light", "dark"]) {
      await command("Emulation.setDeviceMetricsOverride", { width: 390, height: 780, deviceScaleFactor: 1, mobile: false });
      await evaluate(`document.documentElement.dataset.theme=${JSON.stringify(theme)}`); await frames();
      assert.equal(await evaluate("(()=>{const r=document.querySelector('.session-info-dialog').getBoundingClientRect();return r.left>=0 && r.right<=innerWidth && r.height<=innerHeight})()"), true);
    }
    await command("Emulation.clearDeviceMetricsOverride");
    await evaluate("Object.defineProperty(navigator,'clipboard',{configurable:true,value:{writeText:()=>new Promise(resolve=>{window.releaseInfoCopy=resolve})}})");
    await copyInfo();
    await refreshInfo(); assert.equal(await wait("settingsShellFixture.runtimeReads.length===5"), true);
    await evaluate("document.querySelector('[aria-label=\"Close session info\"]').click()"); await frames();
    assert.equal(await evaluate("settingsShellFixture.runtimeReads[4].signal.aborted"), true);
    await evaluate("document.querySelector('[aria-label=\"Session info\"]').click()"); await frames();
    await refreshInfo(); assert.equal(await wait("settingsShellFixture.runtimeReads.length===6"), true);
    await evaluate("settingsShellFixture.releaseRuntime(4,'info'); releaseInfoCopy(); settingsShellFixture.releaseRuntime(5,'info')");
    assert.equal(await wait(`settingsShellFixture.usageReads.length===${infoUsageStart + 2}`), true);
    await evaluate("settingsShellFixture.releaseInfoUsage('2')");
    assert.equal(await wait("document.querySelector('[aria-labelledby=\"session-info-runtime\"]').textContent.includes('attachment or scope changed')"), true);
    assert.equal(await evaluate("document.querySelector('.session-info-dialog').textContent.includes('Displayed details copied.') || document.querySelector('.session-info-dialog').textContent.includes('observed-model')"), false);
    await refreshInfo(); assert.equal(await wait("settingsShellFixture.runtimeReads.length===7"), true);
    await evaluate("publishLayoutCatalog(structuredClone(settingsShellFixture.catalog)); publishLayoutCatalog(settingsShellFixture.catalog)"); await frames();
    assert.equal(await evaluate("settingsShellFixture.runtimeReads[6].signal.aborted"), true);
    await evaluate("settingsShellFixture.releaseRuntime(6,'info')"); await frames();
    assert.equal(await evaluate("document.querySelector('.session-info-dialog').textContent.includes('Not requested.')"), true);
    for (const [index, cycle] of [[7, "cycleInfoSelection"], [8, "cycleInfoHost"]] as const) {
      await refreshInfo(); assert.equal(await wait(`settingsShellFixture.runtimeReads.length===${index + 1}`), true);
      await evaluate(`${cycle}()`); await frames();
      assert.equal(await evaluate(`settingsShellFixture.runtimeReads[${index}].signal.aborted`), true);
      await evaluate(`settingsShellFixture.releaseRuntime(${index},'info')`); await frames();
      assert.equal(await evaluate("document.querySelector('.session-info-dialog').textContent.includes('Not requested.') && !document.querySelector('.session-info-dialog').textContent.includes('observed-model')"), true);
    }
    await evaluate("document.querySelector('.session-info-dialog').dispatchEvent(new KeyboardEvent('keydown',{key:'Escape',isComposing:true,bubbles:true,cancelable:true}))");
    assert.equal(await evaluate("document.querySelector('.session-info-dialog').open"), true);
    await evaluate("document.querySelector('.session-info-dialog').dispatchEvent(new KeyboardEvent('keydown',{key:'Escape',bubbles:true,cancelable:true}))"); await frames();
    assert.equal(await evaluate("document.activeElement===document.querySelector('[aria-label=\"Session info\"]')"), true);
    assert.equal(await evaluate("settingsShellFixture.displayCalls.length"), infoDisplays);
    assert.equal(await evaluate("JSON.stringify(settingsShellFixture.sends[0])"), observationOriginalSend);

    const archiveOriginalSend = await evaluate("JSON.stringify(settingsShellFixture.sends[0])");
    // App-owned sequential exact deletion: review is not admission; originals outlive the browser.
    const batchStart = Number(await evaluate("settingsShellFixture.deleteRequests.length"));
    const batchPreserved = await evaluate("JSON.stringify({selection:document.querySelector('.session-header h1').textContent,draft:document.querySelector('#catalog-prompt')?.value,scroll:document.querySelector('.timeline-scroll').scrollTop,tabs:[...document.querySelectorAll('.session-tabs [role=tab]')].map(t=>t.textContent),displays:settingsShellFixture.displayCalls.length})");
    const batchOpen = async () => { await evaluate("document.querySelector('.browse-sessions-button').click()"); await frames(); };
    const batchButton = (text: string) => evaluate(`[...document.querySelectorAll('.session-batch-delete button')].find(b=>b.textContent===${JSON.stringify(text)}).click()`);
    const batchReview = async () => { await batchButton("Select visible eligible"); await frames(); await batchButton("Review exact deletion targets"); await frames(); };
    const batchConfirm = async () => { await evaluate(`(()=>{const input=document.querySelector('[aria-label="Confirm exact batch deletion"]');Object.getOwnPropertyDescriptor(HTMLInputElement.prototype,'value').set.call(input,'DELETE 2');input.dispatchEvent(new Event('input',{bubbles:true}));})()`); await frames(); };
    await batchOpen();
    await evaluate("document.querySelector('.session-batch-delete summary').focus()");
    await command("Input.dispatchKeyEvent", { type: "keyDown", key: "Enter", code: "Enter", windowsVirtualKeyCode: 13, text: "\r", unmodifiedText: "\r" });
    await command("Input.dispatchKeyEvent", { type: "keyUp", key: "Enter", code: "Enter", windowsVirtualKeyCode: 13 });
    assert.equal(await evaluate("document.querySelector('.session-batch-delete details').open && document.querySelector('.session-browser').open"), true, "native summary Enter must not activate the browser row");
    await evaluate("document.querySelector('.session-batch-delete input[type=checkbox]').focus()");
    await command("Input.dispatchKeyEvent", { type: "keyDown", key: " ", code: "Space", windowsVirtualKeyCode: 32 });
    await command("Input.dispatchKeyEvent", { type: "keyUp", key: " ", code: "Space", windowsVirtualKeyCode: 32 }); await frames();
    assert.equal(await evaluate("document.querySelector('.session-batch-delete legend').textContent.startsWith('1 selected')"), true);
    await batchReview();
    assert.equal(await evaluate("document.querySelectorAll('.session-batch-report li').length"), 2);
    assert.equal(await evaluate("document.querySelector('.session-batch-report').textContent.includes('/fixture/project') && document.querySelector('.session-batch-report').textContent.includes('host: 12345678-1234-1234-1234-123456789abc')"), true);
    assert.equal(await evaluate("settingsShellFixture.deleteRequests.length"), batchStart);
    for (const theme of ["light", "dark"]) {
      await command("Emulation.setDeviceMetricsOverride", { width: 390, height: 780, deviceScaleFactor: 1, mobile: false });
      await evaluate(`document.documentElement.dataset.theme=${JSON.stringify(theme)}`); await frames();
      assert.equal(await evaluate("(()=>{const r=document.querySelector('.session-browser').getBoundingClientRect();return r.left>=0&&r.right<=innerWidth&&r.height<=innerHeight})()"), true);
    }
    await command("Emulation.clearDeviceMetricsOverride");
    await batchConfirm();
    await evaluate("document.querySelector('[aria-label=\"Confirm exact batch deletion\"]').focus()");
    await command("Input.dispatchKeyEvent", { type: "keyDown", key: "Enter", code: "Enter", windowsVirtualKeyCode: 13, text: "\r", unmodifiedText: "\r" });
    await command("Input.dispatchKeyEvent", { type: "keyUp", key: "Enter", code: "Enter", windowsVirtualKeyCode: 13 });
    assert.equal(await evaluate("document.querySelector('.session-browser').open"), true, "confirmation input Enter is not a browser-open shortcut");
    await evaluate("document.querySelector('[aria-label=\"Confirm exact batch deletion\"]').dispatchEvent(new KeyboardEvent('keydown',{key:'Enter',isComposing:true,bubbles:true,cancelable:true}))");
    assert.equal(await evaluate("settingsShellFixture.deleteRequests.length"), batchStart, "IME/confirmation text does not implicitly submit");
    const batchReads = await evaluate("settingsShellFixture.snapshotCalls.length");
    await evaluate("(()=>{const b=[...document.querySelectorAll('.session-batch-delete button')].find(b=>b.textContent==='Delete reviewed sessions');b.click();b.click()})()");
    assert.equal(await wait(`settingsShellFixture.deleteRequests.length===${batchStart + 1}`), true);
    await evaluate("document.querySelector('.session-browser header button').click();document.querySelector('.project-rail .icon-label-button').click()"); await frames();
    assert.equal(await evaluate("document.querySelector('.settings-dialog').open"), true);
    await evaluate("document.querySelector('[aria-label=\"Close settings\"]').click()"); await frames();
    await batchOpen();
    assert.equal(await evaluate("document.querySelector('.session-batch-report').textContent.includes('pending')"), true, "pending original survives dismissal/Settings/reopen");
    await evaluate("settingsShellFixture.releaseExactDelete('ok')"); await frames();
    assert.equal(await evaluate("document.querySelector('.session-batch-report').textContent.includes('deleted') && document.querySelector('.session-batch-report').textContent.includes('not-started')"), true);
    assert.equal(await evaluate("settingsShellFixture.deleteRequests.length"), batchStart + 1, "dismissal stops successors, not the pending original");
    assert.equal(await evaluate("settingsShellFixture.snapshotCalls.length"), batchReads, "no automatic refresh or recovery unlock");
    await batchButton("Acknowledge and clear settled report (no continuation)"); await frames();
    await batchReview(); await batchConfirm(); await batchButton("Delete reviewed sessions");
    assert.equal(await wait(`settingsShellFixture.deleteRequests.length===${batchStart + 2}`), true);
    await evaluate("settingsShellFixture.releaseExactDelete('session_in_use')");
    assert.equal(await wait(`settingsShellFixture.deleteRequests.length===${batchStart + 3}`), true);
    await evaluate("settingsShellFixture.releaseExactDelete('ok')"); await frames();
    assert.equal(await evaluate("document.querySelector('.session-batch-report').textContent.includes('refused') && document.querySelector('.session-batch-report').textContent.includes('deleted')"), true);
    await batchButton("Acknowledge and clear settled report (no continuation)"); await frames();
    // Every authority ABA invalidates a pre-admission review, even if the final values match.
    for (const cycle of ["cycleInfoSelection()", "cycleInfoHost()", "publishLayoutCatalog(structuredClone(settingsShellFixture.catalog));publishLayoutCatalog(settingsShellFixture.catalog)"]) {
      await batchReview(); await batchConfirm(); await evaluate(cycle); await frames();
      assert.equal(await evaluate("!document.querySelector('[aria-label=\"Confirm exact batch deletion\"]')"), true);
      await evaluate("document.querySelector('.session-browser header button').click()"); await frames(); await batchOpen();
    }
    await batchReview();
    await evaluate("(()=>{const s=document.querySelector('[aria-label=\"Order loaded sessions\"]');s.value='name';s.dispatchEvent(new Event('change',{bubbles:true}))})()"); await frames();
    assert.equal(await evaluate("!document.querySelector('[aria-label=\"Confirm exact batch deletion\"]')"), true, "order changes invalidate review without requests");
    assert.equal(await evaluate("settingsShellFixture.deleteRequests.length"), batchStart + 3);
    // A changed exact title/catalog while the original is held never retargets it or starts its successor.
    await batchReview(); await batchConfirm(); await batchButton("Delete reviewed sessions");
    assert.equal(await wait(`settingsShellFixture.deleteRequests.length===${batchStart + 4}`), true);
    await evaluate("publishLayoutCatalog({...settingsShellFixture.catalog,sessions:settingsShellFixture.catalog.sessions.map(s=>({...s,title:'changed '+s.title,fullTitle:'changed '+s.fullTitle}))})"); await frames();
    assert.equal(await evaluate("settingsShellFixture.deleteRequests.at(-1).confirmedTitle"), "one");
    await evaluate("settingsShellFixture.releaseExactDelete('session_missing')"); await frames();
    assert.equal(await evaluate("document.querySelector('.session-batch-report').textContent.includes('not-started') && document.querySelector('.session-batch-report').textContent.includes('session_missing')"), true);
    await batchButton("Acknowledge and clear settled report (no continuation)");
    await evaluate("publishLayoutCatalog(settingsShellFixture.catalog);document.querySelector('.session-browser header button').click()"); await frames(); await batchOpen();
    await batchButton("Select visible eligible"); await frames();
    await batchButton("Invert visible eligible"); await frames();
    assert.equal(await evaluate("document.querySelector('.session-batch-delete legend').textContent.startsWith('0 selected')"), true);
    await batchReview(); await batchConfirm(); await batchButton("Delete reviewed sessions");
    assert.equal(await wait(`settingsShellFixture.deleteRequests.length===${batchStart + 5}`), true);
    await evaluate("settingsShellFixture.releaseExactDelete('delete_unconfirmed')"); await frames();
    assert.equal(await evaluate("document.querySelector('.session-batch-report').textContent.includes('uncertain') && ![...document.querySelectorAll('.session-batch-delete button')].some(b=>b.textContent.startsWith('Acknowledge'))"), true);
    await evaluate("publishLayoutCatalog(structuredClone(settingsShellFixture.catalog))"); await frames();
    await evaluate("document.querySelector('.session-browser header button').click()"); await frames(); await batchOpen();
    assert.equal(await evaluate("document.querySelector('.session-batch-report').textContent.includes('uncertain')"), true);
    assert.equal(await evaluate("JSON.stringify(settingsShellFixture.sends[0])"), archiveOriginalSend, "batch selections and deletion outcomes preserve the original pending Send");
    assert.equal(await evaluate("JSON.stringify({selection:document.querySelector('.session-header h1').textContent,draft:document.querySelector('#catalog-prompt')?.value,scroll:document.querySelector('.timeline-scroll').scrollTop,tabs:[...document.querySelectorAll('.session-tabs [role=tab]')].map(t=>t.textContent),displays:settingsShellFixture.displayCalls.length})"), batchPreserved, "batch owner does not change active selection, draft, scroll, tab order or Display owner");
    await evaluate("document.querySelector('.session-browser header button').click()"); await frames();
    await evaluate("document.querySelector('.session-actions-trigger').click()"); await frames();
    assert.equal(await evaluate("[...document.querySelectorAll('.session-actions-menu button')].find(b=>b.textContent.startsWith('Delete')).disabled"), true, "uncertain batch blocks conflicting single deletion");
    await evaluate("document.querySelector('.session-actions-menu').dispatchEvent(new KeyboardEvent('keydown',{key:'Escape',bubbles:true,cancelable:true}))"); await frames();
    const countPreservation = await evaluate("JSON.stringify({selection:document.querySelector('.session-header h1').textContent,draft:document.querySelector('#catalog-prompt')?.value,scroll:document.querySelector('.timeline-scroll').scrollTop,tabs:[...document.querySelectorAll('.session-tabs [role=tab]')].map(t=>t.textContent),reads:settingsShellFixture.runtimeReads.length,displays:settingsShellFixture.displayCalls.length})");
    await evaluate("document.querySelector('.project-rail .icon-label-button').click()");
    assert.equal(await wait("!!document.querySelector('#settings-recent-count')"), true);
    await evaluate("(()=>{const s=document.querySelector('#settings-recent-count');s.value='1';s.dispatchEvent(new Event('change',{bubbles:true}))})()"); await frames();
    assert.equal(await evaluate("localStorage.getItem('codealta.desktop.recent-session-count.v1')"), "1");
    await evaluate("document.querySelector('[aria-label=\"Close settings\"]').click()"); await frames();
    assert.equal(await evaluate("!!document.querySelector('.session-list button[aria-pressed=true]')"), true, "display count never hides the active navigator row");
    assert.equal(await evaluate("JSON.stringify({selection:document.querySelector('.session-header h1').textContent,draft:document.querySelector('#catalog-prompt')?.value,scroll:document.querySelector('.timeline-scroll').scrollTop,tabs:[...document.querySelectorAll('.session-tabs [role=tab]')].map(t=>t.textContent),reads:settingsShellFixture.runtimeReads.length,displays:settingsShellFixture.displayCalls.length})"), countPreservation);
    await evaluate("document.querySelector('.browse-sessions-button').click()"); await frames();
    assert.equal(await evaluate("document.querySelector('.session-browser').textContent.includes('Limit 1;')"), true);
    await evaluate("[...document.querySelectorAll('.session-browser button')].find(b=>b.textContent==='Show all loaded matches').click()"); await frames();
    assert.equal(await evaluate("document.querySelector('.session-browser').textContent.includes('Use recent session limit')"), true);
    await evaluate("document.querySelector('.session-browser header button').click();document.querySelector('.project-rail .icon-label-button').click()"); await frames();
    await evaluate("(()=>{const s=document.querySelector('#settings-recent-count');s.value='20';s.dispatchEvent(new Event('change',{bubbles:true}))})()");
    await evaluate("document.querySelector('[aria-label=\"Close settings\"]').click()"); await frames();
    assert.equal(await evaluate("JSON.stringify(settingsShellFixture.sends[0])"), archiveOriginalSend, "presentation changes preserve the original pending Send");
    await evaluate("document.querySelector('#project-list button[title=\"/fixture/project\"]').click()"); await frames();
    await evaluate("document.querySelector('.session-tabs [role=tab]').click()"); await editLocal("Archive local draft retained");
    const archiveOpen = () => evaluate("[...document.querySelectorAll('.project-rail button')].find(button=>/^(Archive|Unarchive) project/.test(button.textContent)).click()");
    const archiveClose = () => evaluate("[...document.querySelectorAll('.archive-dialog button')].find(button=>button.textContent==='Close').click()");
    await archiveOpen();
    assert.equal(await wait("document.querySelector('.archive-dialog')?.open && !![...document.querySelectorAll('.archive-dialog button')].find(b=>b.textContent==='Confirm archive')"), true);
    const archiveLanguages = () => workflowLanguages(evaluate, workflowCalls, ".archive-dialog", "#archive-title", "Project archive / unarchive", "code", "settingsShellFixture.archives.at(-1)?.request");
    await archiveLanguages();
    await workflowNarrow(evaluate, command, ".archive-dialog");
    assert.equal(await evaluate("document.querySelector('.archive-dialog').textContent.includes('Existing work is not stopped') && document.querySelector('.archive-dialog').textContent.includes('sessions and drafts are retained')"), true);
    for (const theme of ["light", "dark"]) {
      await command("Emulation.setDeviceMetricsOverride", { width: 390, height: 780, deviceScaleFactor: 1, mobile: false });
      await evaluate(`document.documentElement.dataset.theme=${JSON.stringify(theme)}`); await frames();
      assert.equal(await evaluate("(()=>{const r=document.querySelector('.archive-dialog').getBoundingClientRect();return r.left>=0 && r.right<=innerWidth && r.height<=innerHeight})()"), true);
    }
    await command("Emulation.clearDeviceMetricsOverride");
    assert.equal(await evaluate("(()=>{const b=[...document.querySelectorAll('.archive-dialog button')].find(b=>b.textContent==='Confirm archive'); return !b.dispatchEvent(new KeyboardEvent('keydown',{key:'Enter',isComposing:true,bubbles:true,cancelable:true})) && settingsShellFixture.archives.length===0})()"), true);
    // Refresh/ABA invalidates this confirmation, even when exact metadata returns.
    await evaluate("publishLayoutCatalog(structuredClone(settingsShellFixture.catalog)); publishLayoutCatalog(settingsShellFixture.catalog)"); await frames();
    assert.equal(await evaluate("document.querySelector('.archive-dialog button').disabled"), true);
    await archiveClose(); await frames(); await archiveOpen();
    assert.equal(await wait("!!document.querySelector('.archive-dialog button:not(:disabled)') && document.querySelector('.archive-dialog').textContent.includes('Confirm archive')"), true);
    await evaluate("window.archiveConfirm=[...document.querySelectorAll('.archive-dialog button')].find(b=>b.textContent==='Confirm archive'); archiveConfirm.focus()");
    await command("Input.dispatchKeyEvent", { type: "keyDown", key: "Enter", code: "Enter", windowsVirtualKeyCode: 13 });
    await command("Input.dispatchKeyEvent", { type: "keyUp", key: "Enter", code: "Enter", windowsVirtualKeyCode: 13 });
    await evaluate("archiveConfirm.click()");
    assert.equal(await wait("settingsShellFixture.archives.length===1 && document.querySelector('.archive-dialog').textContent.includes('Original write pending')"), true);
    await archiveLanguages();
    await archiveClose(); await frames();
    await evaluate("document.querySelector('.project-root-list button').click()"); await frames();
    await evaluate("document.querySelector('#project-list button[title=\"/fixture/project\"]').click()"); await frames();
    await evaluate("document.querySelector('.project-rail .icon-label-button').click()");
    assert.equal(await wait("document.querySelector('.settings-dialog')?.open"), true);
    await evaluate("document.querySelector('[aria-label=\"Close settings\"]').click()"); await frames();
    await archiveOpen();
    assert.equal(await wait("document.querySelector('.archive-dialog')?.textContent.includes('Original write pending')"), true);
    await evaluate("settingsShellFixture.releaseArchive()");
    assert.equal(await wait("document.querySelector('.archive-dialog')?.textContent.includes('Archive confirmed.')"), true);
    await archiveClose(); await frames();
    assert.equal(await evaluate("document.querySelector('#catalog-prompt')?.value==='Archive local draft retained' && !document.querySelector('.owned-session') && settingsShellFixture.sends.length===1 && settingsShellFixture.creates.length===0"), true, "archive retains local draft and original pending Send, without creation");
    assert.equal(await evaluate("JSON.stringify(settingsShellFixture.sends[0])"), archiveOriginalSend, "archive cannot retarget the retained original Send");
    await archiveOpen();
    assert.equal(await wait("document.querySelector('.archive-dialog')?.textContent.includes('Confirm unarchive')"), true);
    await evaluate("[...document.querySelectorAll('.archive-dialog button')].find(b=>b.textContent==='Confirm unarchive').click()");
    assert.equal(await wait("settingsShellFixture.archives.length===2"), true);
    await evaluate("settingsShellFixture.releaseArchive()");
    assert.equal(await wait("document.querySelector('.archive-dialog')?.textContent.includes('Unarchive confirmed.')"), true);
    await archiveClose(); await frames(); await archiveOpen();
    assert.equal(await wait("document.querySelector('.archive-dialog')?.textContent.includes('Confirm archive')"), true);
    await evaluate("[...document.querySelectorAll('.archive-dialog button')].find(b=>b.textContent==='Confirm archive').click()");
    assert.equal(await wait("settingsShellFixture.archives.length===3"), true);
    await evaluate("settingsShellFixture.releaseArchive('archive_unconfirmed')");
    assert.equal(await wait("document.querySelector('.archive-dialog')?.textContent.includes('Write unconfirmed')"), true);
    await archiveLanguages();
    await archiveClose(); await evaluate("publishLayoutCatalog(structuredClone(settingsShellFixture.catalog))"); await frames(); await archiveOpen();
    assert.equal(await wait("document.querySelector('.archive-dialog')?.textContent.includes('no retry will be sent')"), true);
    assert.equal(await evaluate("settingsShellFixture.archives.length===3 && ![...document.querySelectorAll('.archive-dialog button')].some(b=>b.textContent.startsWith('Confirm'))"), true);
    await archiveClose(); await frames();
    await evaluate("document.querySelector('.browse-sessions-button').click(); loseLayoutHost()"); await frames();
    assert.equal(await evaluate("!!document.querySelector('.session-browser [role=alert]') && [...document.querySelectorAll('.session-browser-results button')].every(button=>button.disabled)"), true, "host change refuses captured results");
    await evaluate("document.querySelector('.session-browser header button').click()"); await frames();
    await evaluate("document.querySelector('.browse-sessions-button').click()"); await frames();
    assert.equal(await evaluate("!!document.querySelector('.session-browser-results button:not(:disabled)')"), true, "fresh catalog-only metadata remains browsable");
    assert.equal(await evaluate("[...document.querySelectorAll('.project-rail button')].find(button=>/^(Archive|Unarchive) project/.test(button.textContent)).disabled"), true, "catalog-only archive refuses");
    assert.equal(await evaluate("settingsShellFixture.sends.length===1 && settingsShellFixture.creates.length===0"), true, "browsing never retries Send or creates a session");
    await evaluate("document.querySelector('.session-browser header button').click()");
    // Fresh disposable App: stale deletion evidence must invalidate the actual shared authority.
    await evaluate("unmountShellFixture();localStorage.clear();localStorage.setItem('settingsFixtureOwned','true');localStorage.setItem('layoutFixtureLive','true');localStorage.setItem('navigationFixture','tabs')");
    await command("Page.reload");
    assert.equal(await wait("document.querySelector('.session-header h1')?.textContent==='one' && !!readBatchCapability()"), true);
    await evaluate("window.originalBatchCapability=readBatchCapability()");
    await batchOpen(); await batchReview(); await batchConfirm(); await batchButton("Delete reviewed sessions");
    assert.equal(await wait("settingsShellFixture.deleteRequests.length===1"), true);
    await evaluate("document.querySelector('.session-browser header button').click();cycleInfoHost();document.querySelector('.project-rail .icon-label-button').click()"); await frames();
    await evaluate("settingsShellFixture.releaseExactDelete('stale_epoch','00000000-0000-0000-0000-000000000002')"); await frames();
    assert.equal(await evaluate("originalBatchCapability.canSubmit({expectedEpoch:'12345678-1234-1234-1234-123456789abc'})"), false, "late stale batch response must invalidate shared Send/create authority after dismissal and host ABA");
    assert.equal(await evaluate("readBatchCapability()===originalBatchCapability && !readBatchCapability().canMutate() && settingsShellFixture.deleteRequests.length===1"), true);
    await evaluate("document.querySelector('[aria-label=\"Close settings\"]').click()"); await frames(); await batchOpen();
    assert.equal(await evaluate("document.querySelector('.session-batch-report').textContent.includes('refused') && document.querySelector('.session-batch-report').textContent.includes('not-started')"), true);
    await evaluate("document.querySelector('.session-browser header button').click()");
    await evaluate("window.unmountShellFixture()");
    assert.equal(await wait("document.querySelector('#root').childElementCount===0 && settingsShellFixture.displayCalls.length===settingsShellFixture.displayCleanup.length"), true);
  } finally {
    socket?.close(); browser?.kill();
    await writeFile(join(root, "session-content-observations.json"), JSON.stringify(layoutObservations, null, 2));
  }
});
