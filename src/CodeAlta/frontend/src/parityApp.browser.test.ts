import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { existsSync } from "node:fs";
import { mkdtemp, readFile, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { fileURLToPath, pathToFileURL } from "node:url";
import { build } from "esbuild";

const edge = ["C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe", "C:/Program Files/Microsoft/Edge/Application/msedge.exe"].find(existsSync);
test("actual App mounts against an isolated bridge and records parity baselines", { skip: !edge, timeout: 90_000 }, async t => {
  const root = await mkdtemp(join(tmpdir(), "codealta-parity-app-"));
  t.diagnostic(`Actual-App baseline artifacts (not visual acceptance): ${root}`);
  let browser: ReturnType<typeof spawn> | undefined, socket: WebSocket | undefined;
  try {
    // Independent stock-style reference: no application CSS or overrides.
    await build({ stdin: { contents: '@import "normalize.css"; @import "@blueprintjs/core/lib/css/blueprint.css";', loader: "css",
      resolveDir: fileURLToPath(new URL("../", import.meta.url)) }, outfile: join(root, "blueprint-reference.css"), bundle: true });
    await build({ entryPoints: [fileURLToPath(new URL("./main.tsx", import.meta.url))], outfile: join(root, "fixture.js"), bundle: true, platform: "browser", format: "iife",
      alias: { "#neoastra": fileURLToPath(new URL("./parityApp.bridge.ts", import.meta.url)) },
      define: { "import.meta.env.VITE_DEMO_MODE": '"false"' }, metafile: true,
    }).then(result => {
      assert.ok(!Object.keys(result.metafile!.inputs).some(path => path.includes("obj/neoastra") || path.includes("@neoastra/client")), "No native bridge may enter the fixture bundle");
    });
    await writeFile(join(root, "fixture.html"), '<!doctype html><html><head><meta charset="utf-8"><link rel="stylesheet" href="fixture.css"></head><body><div id="root"></div><script src="fixture.js"></script></body></html>');
    await build({ entryPoints: [fileURLToPath(new URL("./providerChooser.mount.tsx", import.meta.url))], outfile: join(root, "provider.js"), bundle: true, platform: "browser", format: "iife",
      alias: { "#neoastra": fileURLToPath(new URL("./providerChooser.bridge.ts", import.meta.url)) } });
    await writeFile(join(root, "provider.html"), '<!doctype html><html><head><meta charset="utf-8"><link rel="stylesheet" href="provider.css"></head><body><div id="root"></div><script src="provider.js"></script></body></html>');
    const profile = join(root, "profile");
    browser = spawn(edge!, ["--headless=new", "--disable-gpu", "--no-first-run", "--disable-background-networking", "--disable-extensions", `--user-data-dir=${profile}`, "--remote-debugging-port=0", "about:blank"], { stdio: "ignore", windowsHide: true });
    let port = "";
    for (let attempt = 0; attempt < 100 && !port; attempt++) {
      try { port = (await readFile(join(profile, "DevToolsActivePort"), "utf8")).split(/\r?\n/)[0]; }
      catch { await new Promise(resolve => setTimeout(resolve, 50)); }
    }
    assert.match(port, /^\d+$/);
    const pages: { type?: string; webSocketDebuggerUrl?: string }[] = await (await fetch(`http://127.0.0.1:${port}/json/list`, { signal: AbortSignal.timeout(10_000) })).json();
    socket = new WebSocket(pages.find(page => page.type === "page")!.webSocketDebuggerUrl!);
    await new Promise<void>((resolve, reject) => { socket!.addEventListener("open", () => resolve(), { once: true }); socket!.addEventListener("error", () => reject(Error("browser unavailable")), { once: true }); });
    const exceptions: unknown[] = [], requests: string[] = [];
    socket.addEventListener("message", event => {
      const message = JSON.parse(String(event.data));
      if (message.method === "Runtime.exceptionThrown") exceptions.push(message.params.exceptionDetails);
      if (message.method === "Network.requestWillBeSent") requests.push(message.params.request.url);
    });
    let sequence = 0;
    const command = (method: string, params: object = {}) => new Promise<any>((resolve, reject) => {
      const id = ++sequence;
      const reply = (event: MessageEvent) => {
        const message = JSON.parse(String(event.data)); if (message.id !== id) return;
        socket!.removeEventListener("message", reply); clearTimeout(timer);
        if (message.error) reject(Error(`${method}: ${JSON.stringify(message.error)}`)); else resolve(message.result ?? {});
      };
      const timer = setTimeout(() => { socket!.removeEventListener("message", reply); reject(Error(`${method} timed out`)); }, 12_000);
      socket!.addEventListener("message", reply); socket!.send(JSON.stringify({ id, method, params }));
    });
    const evaluate = async (expression: string) => {
      const result = await command("Runtime.evaluate", { expression, returnByValue: true, awaitPromise: true });
      assert.equal(result.exceptionDetails, undefined, JSON.stringify(result.exceptionDetails)); return result.result?.value;
    };
    const createStyleReference = async (theme: string) => {
      const reference = `<!doctype html><html class="${theme === "dark" ? "bp6-dark" : ""}"><head><link rel="stylesheet" href="blueprint-reference.css"></head><body><div class="bp6-html-select"><select><option>Reference</option></select></div></body></html>`;
      await evaluate(`new Promise(resolve=>{const frame=document.createElement('iframe');frame.id='blueprint-reference';frame.style.cssText='position:fixed;left:-10000px;top:0;width:300px;height:200px';frame.onload=()=>resolve(true);frame.srcdoc=${JSON.stringify(reference)};(document.querySelector('dialog[open]')??document.body).append(frame)})`);
    };
    const compareStyles = (selector: string, focused = false) => evaluate(`(() => {
      const select=document.querySelector(${JSON.stringify(selector)}), frame=document.querySelector('#blueprint-reference');
      const reference=frame.contentDocument.querySelector('select'); reference.disabled=select.disabled;
      const properties=${JSON.stringify(focused ? ["outlineColor", "outlineStyle", "outlineWidth", "outlineOffset"] : ["color", "backgroundColor", "backgroundImage", "boxShadow", "borderRadius", "borderWidth", "paddingLeft", "paddingRight", "height", "fontSize"])};
      if (${focused}) select.focus();
      const actual=Object.fromEntries(properties.map(name=>[name,getComputedStyle(select)[name]]));
      if (${focused}) reference.focus();
      const expected=frame.contentWindow.getComputedStyle(reference);
      const differences=properties.filter(name=>actual[name]!==expected[name]).map(name=>({name,actual:actual[name],expected:expected[name]}));
      if (${focused}) select.blur();
      return differences;
    })()`);
    await command("Page.enable"); await command("Runtime.enable"); await command("Network.enable");
    await command("Emulation.setFocusEmulationEnabled", { enabled: true });
    // Block resource attempts outside the isolated fixture even if a regression introduces one.
    await command("Network.setBlockedURLs", { urls: ["http://*", "https://*", "ws://*", "wss://*"] });
    await command("Page.navigate", { url: pathToFileURL(join(root, "fixture.html")).href });
    assert.equal(await evaluate("new Promise(resolve=>{const end=Date.now()+7000;function check(){if(document.body.textContent.includes('Create a usable desktop workspace'))resolve(true);else if(Date.now()>end)resolve(false);else setTimeout(check,20)}check()})"), true, JSON.stringify(exceptions));
    assert.equal(await evaluate("document.documentElement.classList.contains('bp6-dark')"), true, "Default dark preference also initializes Blueprint's theme");
    const measurements = [];
    for (const width of [390, 1280]) for (const theme of ["light", "dark"]) {
      await command("Emulation.setDeviceMetricsOverride", { width, height: 900, deviceScaleFactor: 1, mobile: false });
      // Use the production preference path, not a test-only dataset assignment.
      await evaluate("document.querySelector('.activity-settings').click();new Promise(r=>requestAnimationFrame(r))");
      await evaluate(`document.querySelectorAll('.settings-card .segmented button')[${theme === "dark" ? 0 : 1}].click();new Promise(r=>requestAnimationFrame(()=>requestAnimationFrame(r)))`);
      assert.equal(await evaluate("document.documentElement.dataset.theme"), theme);
      assert.equal(await evaluate("document.documentElement.classList.contains('bp6-dark')"), theme === "dark");
      await createStyleReference(theme);
      for (const selector of ["#settings-language", "#settings-project-sort", "#settings-recent-count"]) {
        assert.deepEqual(await compareStyles(selector), [], `${selector} uses stock Blueprint ${theme} styles`);
      }
      await evaluate("document.querySelector('#settings-language').disabled=true");
      assert.deepEqual(await compareStyles("#settings-language"), [], "Disabled dropdown retains Blueprint styling");
      await evaluate("document.querySelector('#settings-language').disabled=false");
      assert.deepEqual(await compareStyles("#settings-language", true), [], "Focus styling comes from Blueprint");
      await evaluate("document.querySelector('#blueprint-reference').remove();document.querySelector('.settings-dialog-header button').click();new Promise(r=>requestAnimationFrame(r))");
      assert.equal(await evaluate("document.body.textContent.includes('The workspace shell is ready.')"), true);
      assert.equal(await evaluate("document.querySelectorAll('.timeline-tool-group .tool-tile-title').length"), 3);
      assert.equal(await evaluate("document.querySelectorAll('.timeline-tool-group').length"), 1);
      assert.equal(await evaluate("document.querySelector('.message-assistant .message-heading strong').textContent"), "Assistant");
      assert.equal(await evaluate("parseFloat(getComputedStyle(document.querySelector('.messages')).paddingLeft) >= 12"), true);
      assert.equal(await evaluate("document.querySelectorAll('.message-notes').length"), 0);
      assert.equal(await evaluate("document.querySelectorAll('[data-file-record]').length"), 2);
      assert.equal(await evaluate("document.querySelector('.tool-group-counts').textContent.includes('3 call(s)') && document.querySelector('.tool-group-counts').textContent.includes('3 done')"), true);
      assert.equal(await evaluate("document.querySelector('.tool-command-preview').textContent.includes('git diff')"), true);
      assert.equal(await evaluate("document.querySelector('.tool-output-stats').textContent"), "27L · 1.4 KB");
      assert.equal(await evaluate("[...document.querySelectorAll('select')].every(select=>select.parentElement.classList.contains('bp6-html-select'))"), true);
      assert.equal(await evaluate("(() => { const card=document.querySelector('.timeline-tool-group .message');const heading=card.querySelector('.message-heading > span');return heading.clientWidth > card.clientWidth * .6; })()"), true, "Tool names use the card width, not the old 45% cap");
      assert.equal(await evaluate("[...document.querySelectorAll('.tool-command-preview,.tool-output-stats')].every(e=>e.scrollWidth<=e.clientWidth+1)"), true, "Commands wrap and totals remain visible");
      const successColor = await evaluate("getComputedStyle(document.querySelector('.tool-command-preview')).color");
      await evaluate("document.querySelector('.timeline-tool-group .message').dataset.toolPhase='failed'");
      const failureColor = await evaluate("getComputedStyle(document.querySelector('.tool-command-preview')).color");
      assert.notEqual(successColor, failureColor, "Completed and failed commands use different semantic colors in each theme");
      assert.equal(await evaluate("getComputedStyle(document.querySelector('.tool-tile-title strong')).color"), failureColor);
      await evaluate("document.querySelector('.timeline-tool-group .message').dataset.toolPhase='completed'");
      assert.equal(await evaluate("document.querySelector('.file-change-inspection li .file-counts').textContent"), "+2 \u22120");
      assert.equal(await evaluate("document.querySelector('.file-change-inspection li').getBoundingClientRect().height <= 44"), true);
      assert.equal(await evaluate("document.querySelectorAll('.timeline-source-trigger').length"), 0);
      assert.equal(await evaluate("document.querySelector('.messages').textContent.includes('Some details were shortened')"), false);
      await evaluate("document.querySelector('[data-file-record=\"1\"]').click();new Promise(r=>requestAnimationFrame(r))");
      assert.equal(await evaluate("document.querySelector('[data-file-diff]')?.textContent.includes('+new file')"), true);
      assert.equal(await evaluate("document.querySelector('[data-file-diff]').textContent.includes('@@')"), false);
      assert.equal(await evaluate("getComputedStyle(document.querySelector('[data-file-diff]')).fontFamily.includes('monospace')"), true);
      assert.equal(await evaluate("getComputedStyle(document.querySelector('dialog[open] > header > button')).backgroundColor"), "rgba(0, 0, 0, 0)");
      await evaluate("document.querySelector('dialog[open] button').click();new Promise(r=>requestAnimationFrame(r))");
      for (let index = 0; index < 3; index++) {
        await evaluate(`document.querySelectorAll('.tool-tile-title')[${index}].click();new Promise(r=>requestAnimationFrame(r))`);
        assert.equal(await evaluate("!!document.querySelector('dialog[open]')"), true);
        assert.equal(await evaluate(`document.querySelector('dialog[open]').textContent.includes(${JSON.stringify(["Read source", "Search references", "Inspect changes"][index])})`), true);
        assert.equal(await evaluate("!!document.querySelector('dialog[open] .json-key') && getComputedStyle(document.querySelector('dialog[open] pre')).whiteSpace==='pre-wrap' && parseFloat(getComputedStyle(document.querySelector('dialog[open] pre')).paddingLeft)>=10"), true);
        assert.equal(await evaluate("[...document.querySelectorAll('dialog[open] pre')].every(pre=>pre.scrollWidth<=pre.clientWidth+1)"), true);
        if (index === 0) {
          const popup = await command("Page.captureScreenshot", { format: "png", captureBeyondViewport: false });
          await writeFile(join(root, `popup-${width}-${theme}.png`), Buffer.from(popup.data, "base64"));
          await command("Input.dispatchMouseEvent", { type: "mousePressed", x: 1, y: 1, button: "left", clickCount: 1 });
          await command("Input.dispatchMouseEvent", { type: "mouseReleased", x: 1, y: 1, button: "left", clickCount: 1 });
        } else {
          await command("Input.dispatchKeyEvent", { type: "keyDown", key: "Escape", code: "Escape", windowsVirtualKeyCode: 27 });
          await command("Input.dispatchKeyEvent", { type: "keyUp", key: "Escape", code: "Escape", windowsVirtualKeyCode: 27 });
        }
        await evaluate("new Promise(r=>requestAnimationFrame(()=>requestAnimationFrame(r)))");
        assert.equal(await evaluate("!document.querySelector('dialog[open]')"), true);
        assert.equal(await evaluate(`document.activeElement===document.querySelectorAll('.tool-tile-title')[${index}]`), true);
      }
      const shot = await command("Page.captureScreenshot", { format: "png", captureBeyondViewport: false });
      await writeFile(join(root, `${width}-${theme}.png`), Buffer.from(shot.data, "base64"));
      measurements.push({ width, theme, scrollWidth: await evaluate("document.documentElement.scrollWidth"), text: await evaluate("document.body.innerText") });
      await evaluate("document.querySelector('#expand-session-prompt').click();new Promise(r=>requestAnimationFrame(r))");
      const draft = "# Draft heading\n\n**Review** `source` before sending.\n\n<script>window.previewUnsafe=true</script>";
      await evaluate(`(()=>{const input=document.querySelector('.expanded-prompt-dialog textarea');Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype,'value').set.call(input,${JSON.stringify(draft)});input.dispatchEvent(new Event('input',{bubbles:true}));})();new Promise(r=>requestAnimationFrame(r))`);
      assert.equal(await evaluate("document.querySelector('#expanded-prompt-preview h1')?.textContent"), "Draft heading");
      assert.equal(await evaluate("!!window.previewUnsafe || !!document.querySelector('#expanded-prompt-preview script')"), false);
      await evaluate("document.querySelector('.expanded-prompt-actions [aria-pressed]').click();new Promise(r=>requestAnimationFrame(r))");
      assert.equal(await evaluate("document.querySelector('#expanded-prompt-preview').hidden"), true);
      assert.equal(await evaluate("document.querySelector('.expanded-prompt-dialog textarea').value"), draft);
      await evaluate("document.querySelector('.expanded-prompt-actions button:last-child').click();new Promise(r=>requestAnimationFrame(r))");
      assert.equal(await evaluate("document.querySelector('#catalog-prompt').value"), draft);
    }
    await writeFile(join(root, "baseline.json"), JSON.stringify(measurements, null, 2));
    assert.deepEqual(await evaluate("parityFixture.unexpected"), []);
    assert.deepEqual(exceptions, []);
    assert.deepEqual(requests.filter(url => !url.startsWith(pathToFileURL(root).href + "/") && !url.startsWith("data:")), []);
    assert.ok((await evaluate("parityFixture.calls")).includes("workspace.historyTimeline:demo-active"));
    await command("Page.navigate", { url: pathToFileURL(join(root, "provider.html")).href });
    assert.equal(await evaluate("new Promise(resolve=>{const end=Date.now()+5000;function check(){if(document.querySelector('.current-provider'))resolve(true);else if(Date.now()>end)resolve(false);else setTimeout(check,20)}check()})"), true);
    for (const theme of ["light", "dark"]) {
      await evaluate(`document.documentElement.dataset.theme='${theme}';document.documentElement.classList.toggle('bp6-dark',${theme === "dark"})`);
      await createStyleReference(theme);
      assert.deepEqual(await compareStyles(".current-provider select"), [], `Composer provider uses stock Blueprint ${theme} styles`);
      await evaluate("document.querySelector('#blueprint-reference').remove()");
    }
    await evaluate("document.querySelector('.current-provider select').focus();new Promise(r=>requestAnimationFrame(()=>requestAnimationFrame(r)))");
    assert.equal(await evaluate("document.querySelector('.provider-chooser').textContent.includes('Target')"), true);
    assert.equal(await evaluate("document.querySelector('.current-provider').classList.contains('bp6-html-select') && !document.querySelector('dialog')"), true);
    const chooseProvider = (value: string) => evaluate(`(()=>{const select=document.querySelector('.current-provider select');select.value=${JSON.stringify(value)};select.dispatchEvent(new Event('change',{bubbles:true}));})();new Promise(r=>requestAnimationFrame(()=>requestAnimationFrame(r)))`);
    await chooseProvider("target");
    assert.equal(await evaluate("!document.querySelector('dialog[open]') && document.querySelector('.current-provider select').value==='target'"), true);
    assert.equal(await evaluate("document.querySelector('textarea').value"), "Keep my unsent prompt");
    assert.equal(await evaluate("providerBridge.requests.length"), 1);
    assert.equal(await evaluate("providerBridge.requests[0].expectedProviderKey==='original' && providerBridge.requests[0].attachmentGeneration==='2' && providerBridge.requests[0].revision==='42'"), true);
    await evaluate("providerFixture.busy(true);new Promise(r=>requestAnimationFrame(r))");
    assert.equal(await evaluate("document.querySelector('.current-provider select').disabled"), true);
    await evaluate("providerFixture.busy(false);providerBridge.status('stale_selection');new Promise(r=>requestAnimationFrame(r))");
    await chooseProvider("original");
    assert.equal(await evaluate("document.querySelector('.provider-chooser [role=alert]').textContent.includes('stale_selection') && document.querySelector('.current-provider select').value==='target'"), true);
    assert.equal(await evaluate("providerBridge.requests.length"), 2, "A rejected mutation is not retried");
    await evaluate("providerBridge.status('ok');providerBridge.hold();document.querySelector('.current-provider select').blur();document.querySelector('.current-provider select').focus();new Promise(r=>requestAnimationFrame(()=>requestAnimationFrame(r)))");
    await chooseProvider("original");
    assert.equal(await evaluate("document.querySelector('.current-provider select').disabled"), true);
    await evaluate("providerFixture.busy(true);new Promise(r=>requestAnimationFrame(r))");
    await evaluate("providerFixture.busy(false);new Promise(r=>requestAnimationFrame(r))");
    await evaluate("providerBridge.release();new Promise(r=>requestAnimationFrame(()=>requestAnimationFrame(r)))");
    assert.equal(await evaluate("document.querySelector('.current-provider select').value==='original' && providerBridge.requests.length===3"), true, "An admitted selection survives transient idle-control disablement");
    await evaluate("providerBridge.hold()");
    await chooseProvider("target");
    assert.equal(await evaluate("document.querySelector('.current-provider select').disabled"), true);
    await evaluate("providerFixture.shown(false);new Promise(r=>requestAnimationFrame(r))");
    await evaluate("providerBridge.release();new Promise(r=>requestAnimationFrame(()=>requestAnimationFrame(r)))");
    assert.equal(await evaluate("!document.querySelector('.current-provider') && document.querySelector('#selected-provider').textContent==='original' && providerBridge.requests.length===4"), true, "Completion cannot update an unmounted chooser");
    assert.equal(await evaluate("document.querySelector('textarea').value"), "Keep my unsent prompt");
    assert.deepEqual(exceptions, []);
    assert.deepEqual(requests.filter(url => !url.startsWith(pathToFileURL(root).href + "/") && !url.startsWith("data:")), []);
  } finally { socket?.close(); browser?.kill(); }
});
