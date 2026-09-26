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

// The bundle entry is main.tsx, not a copied shell or reimplemented navigation fixture.
test("production shell settings overlay keeps the session workspace mounted and inert", { skip: !edge, timeout: 90_000 }, async () => {
  const root = await mkdtemp(join(tmpdir(), "codealta-settings-shell-"));
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
            .replace('const [projectId, writeProjectId]',
              'Object.assign(window, { publishLayoutCatalog: (snapshot: WorkspaceSnapshot) => publishWorkspaceState({ kind: "ready", snapshot }), loseLayoutHost: () => setStatus({ ...status!, hostAvailable: false, hostEpoch: "different-host" }) }); const [projectId, writeProjectId]') }));
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
    assert.equal(await evaluate(`(() => {const host=document.querySelector('.workspace-layout');
      const panel=host?.querySelector('.flexlayout__tab');
      return !!panel && panel.getBoundingClientRect().height>0 && host.getBoundingClientRect().width>0 &&
        document.querySelector('.workspace-shell').getBoundingClientRect().width>0 &&
        !host.querySelector('.flexlayout__splitter, .flexlayout__tab_button, .flexlayout__floating_window');})()`), true,
      "the actual App must mount one measured, fixed workspace-content Layout without docking controls");
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
    assert.equal(await wait("document.querySelector('.settings-dialog-navigation [aria-current=page]')?.textContent==='Skills' && document.querySelector('.settings-dialog-content .settings-card h2')?.textContent==='Skills'"), true);
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
    await evaluate(`(() => {const input=document.querySelector('#session-prompt'); window.ownedComposer=input;
      Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype,'value').set.call(input,'Owned test draft');
      input.dispatchEvent(new Event('input',{bubbles:true})); })()`);
    assert.equal(await wait("document.querySelector('#session-prompt').value==='Owned test draft'"), true);
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
    await evaluate("document.querySelector('.owned-session .send-button').click()");
    assert.equal(await wait("window.settingsShellFixture.sends.length===1"), true);
    assert.deepEqual(await evaluate("window.settingsShellFixture.sends[0].selection"),
      { providerKey: "fixture", agentPromptId: "default", modelId: "new", reasoningEffort: "High" });
    await evaluate("document.querySelector('.project-rail .icon-label-button').click()");
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
    await evaluate("document.querySelector('[aria-label=\"Close settings\"]').click()");
    await command("Page.reload"); // Fresh isolated fake instance; the previous retained Send was not retried.
    assert.equal(await wait("document.querySelector('.owned-session [aria-label=\"Model\"]')?.value==='new'"), true);
    await evaluate("window.promptComposer=document.querySelector('#session-prompt'); true");
    await evaluate("document.querySelector('.project-rail .icon-label-button').click()");
    await evaluate("[...document.querySelectorAll('.settings-dialog-navigation button')].find(x=>x.textContent==='Agent prompts').click()");
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
      await evaluate(`document.querySelector('.session-create button').click()`);
      assert.equal(await wait("window.settingsShellFixture.creates.length===1"), true);
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
      window.layoutHost=document.querySelector('.workspace-layout'); window.layoutPanel=document.querySelector('.flexlayout__tab');
      window.layoutComposer=document.querySelector('#session-prompt'); window.layoutTimeline=document.querySelector('.timeline-scroll');
      window.layoutReads=JSON.stringify([settingsShellFixture.historyCalls,settingsShellFixture.notesCalls,settingsShellFixture.displayCalls,settingsShellFixture.displayCleanup]);
      Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype,'value').set.call(window.layoutComposer,'Original layout draft');
      window.layoutComposer.dispatchEvent(new Event('input',{bubbles:true}))`);
    assert.equal(await wait("document.querySelector('#session-prompt').value==='Original layout draft'"), true);
    for (const width of [390, 1120]) for (const theme of ["light", "dark"]) {
      await command("Emulation.setDeviceMetricsOverride", { width, height: 800, deviceScaleFactor: 1, mobile: false });
      await evaluate(`document.documentElement.dataset.theme='${theme}';
        window.publishLayoutCatalog({...settingsShellFixture.catalog,sessions:settingsShellFixture.catalog.sessions.map(row=>({...row,title:'fresh '+row.id}))});
        document.querySelector('.project-rail .icon-label-button').click()`);
      assert.equal(await wait("document.querySelector('.settings-dialog')?.open && document.querySelector('.session-header h1')?.textContent==='fresh one'"), true);
      await evaluate("document.querySelector('[aria-label=\"Close settings\"]').click()");
      assert.equal(await wait("!document.querySelector('.settings-dialog')"), true);
      assert.equal(await evaluate(`(() => {const panel=document.querySelector('.flexlayout__tab').getBoundingClientRect();
        const shell=document.querySelector('.workspace-shell').getBoundingClientRect();
        return panel.width>0 && panel.height>0 && shell.width>0 && panel.left>=0 && panel.right<=${width}+1 &&
          panel.bottom<=800 && document.documentElement.scrollWidth<=${width} &&
          window.layoutHost===document.querySelector('.workspace-layout') && window.layoutPanel===document.querySelector('.flexlayout__tab') &&
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
      window.layoutPanel.dispatchEvent(new KeyboardEvent('keydown',{key:'w',ctrlKey:true,bubbles:true,cancelable:true}));
      for(const type of ['dragstart','dragover','drop']) window.layoutHost.dispatchEvent(new DragEvent(type,{bubbles:true,cancelable:true,dataTransfer:new DataTransfer()}))`);
    assert.equal(await evaluate(`window.layoutPanel===document.querySelector('.flexlayout__tab') && document.querySelectorAll('.flexlayout__tab').length===1 &&
      !window.layoutHost.querySelector('.flexlayout__splitter,.flexlayout__tab_button,.flexlayout__tabset_header,.flexlayout__floating_window') &&
      window.layoutOpens===0 && settingsShellFixture.sends.length===0 && settingsShellFixture.creates.length===0 &&
      settingsShellFixture.deleteRequests.length===0 && settingsShellFixture.probes.length===0`), true);
    await evaluate("document.querySelector('.owned-session .send-button').click()");
    assert.equal(await wait("settingsShellFixture.sends.length===1"), true);
    const originalSend = await evaluate("settingsShellFixture.sends[0]");
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
    assert.equal(await wait("document.querySelector('.workspace-layout .empty-workspace h1')?.textContent==='Select a session' && !document.querySelector('.session-workspace')"), true);
    assert.equal(await evaluate("window.layoutHost===document.querySelector('.workspace-layout') && window.layoutPanel===document.querySelector('.flexlayout__tab')"), true,
      "empty content is a fresh factory child, not a new Layout model");
    await evaluate("window.unmountShellFixture()");
    assert.equal(await wait("document.querySelector('#root').childElementCount===0 && settingsShellFixture.displayCalls.length===settingsShellFixture.displayCleanup.length"), true);
  } finally {
    socket?.close(); browser?.kill(); await rm(root, { recursive: true, force: true, maxRetries: 6, retryDelay: 100 });
  }
});
