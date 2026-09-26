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
      } }] });
    await writeFile(join(root, "style.css"), readFileSync(fileURLToPath(new URL("./style.css", import.meta.url))));
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
    const evaluate = async (expression: string) => {
      const response = await command("Runtime.evaluate", { expression, returnByValue: true, awaitPromise: true });
      return response.result?.value;
    };
    const wait = (condition: string) => evaluate(`new Promise(resolve => { const end=Date.now()+7000; const tick=()=>{
      if (${condition}) resolve(true); else if (Date.now()>end) resolve(document.body.innerText.slice(-1200));
      else setTimeout(tick,20); }; tick(); })`);
    assert.equal(await wait("!!document.querySelector('#catalog-prompt') && !!document.querySelector('.project-rail .icon-label-button')"), true);
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
  } finally {
    socket?.close(); browser?.kill(); await rm(root, { recursive: true, force: true, maxRetries: 6, retryDelay: 100 });
  }
});
