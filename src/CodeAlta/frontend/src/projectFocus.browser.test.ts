import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { existsSync, readFileSync } from "node:fs";
import { mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { fileURLToPath, pathToFileURL } from "node:url";
import { build } from "esbuild";

const edge = ["C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe", "C:/Program Files/Microsoft/Edge/Application/msedge.exe"].find(existsSync);
test("Ctrl+O focuses the opened project's prompt and cancellation restores its origin", { skip: !edge, timeout: 60_000 }, async () => {
  const root = await mkdtemp(join(tmpdir(), "codealta-project-focus-"));
  let browser: ReturnType<typeof spawn> | undefined;
  let socket: WebSocket | undefined;
  try {
    await build({ entryPoints: [fileURLToPath(new URL("./main.tsx", import.meta.url))], outfile: join(root, "fixture.js"),
      bundle: true, platform: "browser", format: "iife", loader: { ".css": "empty", ".flf": "text", ".svg": "dataurl" },
      define: { "import.meta.env.VITE_DEMO_MODE": '"false"' }, plugins: [{ name: "isolated-project-focus", setup(bundle) {
        bundle.onResolve({ filter: /^#neoastra$/ }, () => ({ path: fileURLToPath(new URL("./projectFocus.neoastra.mount.ts", import.meta.url)) }));
        bundle.onResolve({ filter: /^monaco-editor\/.*\?worker$/ }, args => ({ path: args.path, namespace: "fixture-worker" }));
        bundle.onLoad({ filter: /.*/, namespace: "fixture-worker" }, async () => {
          const worker = await build({ entryPoints: [fileURLToPath(new URL("../node_modules/monaco-editor/esm/vs/editor/editor.worker.js", import.meta.url))],
            bundle: true, platform: "browser", format: "iife", write: false });
          return { loader: "js", contents: `export default class extends Worker { constructor() {
            const url=URL.createObjectURL(new Blob([${JSON.stringify(worker.outputFiles[0].text)}],{type:"text/javascript"}));
            super(url); URL.revokeObjectURL(url);
          } }` };
        });
      } }] });
    await writeFile(join(root, "style.css"), readFileSync(new URL("../node_modules/flexlayout-react/style/light.css", import.meta.url), "utf8") + readFileSync(new URL("./style.css", import.meta.url), "utf8"));
    const page = join(root, "fixture.html");
    await writeFile(page, '<!doctype html><html><head><link rel="stylesheet" href="style.css"></head><body><div id="root"></div><script src="fixture.js"></script></body></html>');
    browser = spawn(edge!, ["--headless=new", "--disable-gpu", "--no-first-run", "--disable-background-networking", "--disable-extensions", "--allow-file-access-from-files",
      `--user-data-dir=${join(root, "profile")}`, "--remote-debugging-port=0", "about:blank"], { stdio: "ignore", windowsHide: true });
    let port = "";
    for (let i = 0; i < 100 && !port; i++) {
      try { port = (await readFile(join(root, "profile", "DevToolsActivePort"), "utf8")).split(/\r?\n/)[0]; }
      catch { await new Promise(resolve => setTimeout(resolve, 50)); }
    }
    assert.match(port, /^\d+$/);
    const pages = await (await fetch(`http://127.0.0.1:${port}/json/list`)).json() as { type?: string; webSocketDebuggerUrl?: string }[];
    const tab = pages.find(tab => tab.type === "page");
    assert.ok(tab?.webSocketDebuggerUrl);
    socket = new WebSocket(tab.webSocketDebuggerUrl);
    await new Promise<void>((resolve, reject) => { socket!.addEventListener("open", () => resolve(), { once: true }); socket!.addEventListener("error", reject, { once: true }); });
    const exceptions: string[] = [];
    socket.addEventListener("message", event => {
      const value = JSON.parse(String(event.data));
      if (value.method === "Runtime.exceptionThrown") exceptions.push(value.params.exceptionDetails.exception?.description ?? value.params.exceptionDetails.text);
      if (value.method === "Runtime.consoleAPICalled" && value.params.type === "error")
        exceptions.push(value.params.args.map((arg: { description?: string; value?: unknown }) => arg.description ?? arg.value).join(" "));
    });
    let sequence = 0;
    const command = (method: string, params: object = {}) => new Promise<Record<string, any>>((resolve, reject) => {
      const id = ++sequence;
      const reply = (event: MessageEvent) => {
        const value = JSON.parse(String(event.data));
        if (value.id !== id) return;
        socket!.removeEventListener("message", reply); clearTimeout(timer);
        value.error ? reject(new Error(JSON.stringify(value.error))) : resolve(value.result);
      };
      const timer = setTimeout(() => { socket!.removeEventListener("message", reply); reject(new Error(`${method} timed out`)); }, 12_000);
      socket!.addEventListener("message", reply); socket!.send(JSON.stringify({ id, method, params }));
    });
    const evaluate = async (expression: string) => {
      const value = await command("Runtime.evaluate", { expression, returnByValue: true, awaitPromise: true });
      assert.equal(value.exceptionDetails, undefined); return value.result?.value;
    };
    const wait = (condition: string) => evaluate(`new Promise(resolve=>{const end=Date.now()+7000;const tick=()=>{if(${condition})resolve(true);else if(Date.now()>end)resolve({focus:document.activeElement?.outerHTML.slice(0,300),promptText:document.querySelector('#catalog-prompt .view-lines')?.textContent,project:document.querySelector('.welcome-subtitle')?.textContent,errors:window.projectFocusErrors});else setTimeout(tick,20)};tick()})`);
    const frames = () => evaluate("new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(()=>requestAnimationFrame(()=>resolve(true)))))");
    const key = async (key: string, code: string, virtual: number, ctrl = false) => {
      for (const type of ["keyDown", "keyUp"]) await command("Input.dispatchKeyEvent", { type, key, code, windowsVirtualKeyCode: virtual, modifiers: ctrl ? 2 : 0 });
    };
    const open = async () => {
      await key("o", "KeyO", 79, true);
      assert.equal(await wait("document.activeElement?.id==='saved-project-filter'"), true);
    };
    const filter = (text: string) => evaluate(`(()=>{const input=document.querySelector('#saved-project-filter');
      Object.getOwnPropertyDescriptor(HTMLInputElement.prototype,'value').set.call(input,${JSON.stringify(text)});
      input.dispatchEvent(new Event('input',{bubbles:true}));})()`);
    const opened = async (path: string) => {
      assert.equal(await wait(`!document.querySelector('dialog[open]') && document.querySelector('.welcome-subtitle')?.textContent.includes(${JSON.stringify(path)})`), true);
      assert.equal(await wait("!!document.activeElement?.closest('#catalog-prompt')"), true, "typing belongs to the opened project, not the dialog's origin");
      await frames();
      assert.equal(await evaluate("!!document.activeElement?.closest('#catalog-prompt')"), true, "focus remains in the new prompt after deferred restoration");
    };
    await command("Page.enable");
    await command("Runtime.enable");
    await command("Page.addScriptToEvaluateOnNewDocument", { source: "window.projectFocusErrors=[];window.addEventListener('error',e=>projectFocusErrors.push(e.message))" });
    await command("Emulation.setFocusEmulationEnabled", { enabled: true });
    await command("Emulation.setDeviceMetricsOverride", { width: 1280, height: 900, deviceScaleFactor: 1, mobile: false });
    await command("Page.navigate", { url: pathToFileURL(page).href });
    assert.equal(await wait("!!document.querySelector('#catalog-prompt')"), true, exceptions.join("\n"));
    await evaluate("localStorage.setItem('settingsFixtureOwned','true');localStorage.setItem('settingsFixtureSecondProject','true')");
    await command("Page.reload");
    assert.equal(await wait("!!document.querySelector('#session-prompt') && !!document.querySelector('#project-list')"), true);

    // Cancel keeps the origin instead of focusing an unrelated project draft.
    await evaluate(`window.projectFocusOrigin=document.querySelector('button[aria-label="Add a project folder"]');projectFocusOrigin.focus()`);
    await open(); await key("Escape", "Escape", 27);
    assert.equal(await wait("!document.querySelector('dialog[open]') && document.activeElement===projectFocusOrigin"), true);

    // "+" goes straight to the folder dialog. A canceled dialog opens no window.
    await evaluate("projectFocusFixture.pick={status:'canceled',path:null};projectFocusOrigin.click()");
    assert.equal(await wait("projectFocusFixture.picks.length===1"), true);
    await frames();
    assert.equal(await evaluate("!document.querySelector('dialog[open]')"), true);
    assert.deepEqual(await evaluate("projectFocusFixture.picks[0]"), { title: "Add a project folder", initialDirectory: null });
    // A folder that is no project yet is shown in the window, checked and ready to be trusted.
    await evaluate("projectFocusFixture.pick={status:'ok',path:'C:/fixture/picked'};projectFocusOrigin.click()");
    assert.equal(await wait("document.querySelector('.project-import-confirm code')?.textContent==='C:/fixture/picked'"), true);
    assert.equal(await evaluate("document.querySelector('#saved-project-filter').value"), "C:/fixture/picked");
    assert.equal(await wait("document.activeElement===document.querySelector('.project-import-confirm button')"), true, "Enter trusts the picked folder");
    // In the window, Ctrl+O and the browse button open the folder dialog in the folder the field names.
    await evaluate("projectFocusFixture.pick={status:'ok',path:'C:/fixture/browsed'}");
    await key("o", "KeyO", 79, true);
    assert.equal(await wait("document.querySelector('.project-import-confirm code')?.textContent==='C:/fixture/browsed'"), true);
    assert.equal(await evaluate("projectFocusFixture.picks[2].initialDirectory"), "C:/fixture/picked");
    await evaluate("projectFocusFixture.pick={status:'canceled',path:null};document.querySelector('.open-project-browse').click()");
    assert.equal(await wait("projectFocusFixture.picks.length===4 && document.activeElement?.id==='saved-project-filter'"), true);
    assert.equal(await evaluate("document.querySelector('.project-import-confirm code')?.textContent"), "C:/fixture/browsed", "a canceled dialog keeps the checked folder");
    // A system without a folder dialog says so, and the path can still be typed.
    await evaluate("projectFocusFixture.pick={status:'unavailable',path:null};document.querySelector('.open-project-browse').click()");
    assert.equal(await wait("document.querySelector('.open-project-notice.error-text')?.textContent.includes('no folder dialog')"), true);
    await key("Escape", "Escape", 27);
    assert.equal(await wait("!document.querySelector('dialog[open]')"), true);
    assert.equal(await evaluate("projectFocusFixture.imports.filter(r=>r.confirmed).length"), 0, "choosing a folder trusts nothing");
    // A folder that is already a project is opened without the window.
    await evaluate("projectFocusFixture.pick={status:'ok',path:'/fixture/other/'};projectFocusOrigin.focus();projectFocusOrigin.click()");
    await opened("/fixture/other");
    assert.equal(await evaluate("projectFocusFixture.imports.length"), 2, "only the two picked folders were checked");
    await evaluate("projectFocusFixture.imports.length=0;projectFocusFixture.pick={status:'unavailable',path:null}");

    // Opening from a non-editor control must put real keyboard input into the new prompt.
    await open(); await filter("Other project"); await key("Enter", "Enter", 13);
    await opened("/fixture/other");
    await command("Input.insertText", { text: "preserved project draft" });
    assert.equal(await wait("document.querySelector('#catalog-prompt .view-lines')?.textContent.replaceAll('\\u00a0',' ').includes('preserved project draft')"), true);
    await evaluate("window.projectFocusEditor=document.activeElement;true");
    await open(); await key("Escape", "Escape", 27);
    assert.equal(await wait("!document.querySelector('dialog[open]') && document.activeElement===projectFocusEditor"), true);

    // Reopening the same project still focuses its retained Monaco editor and preserves the draft.
    await evaluate("projectFocusOrigin.focus();window.projectFocusPrompt=document.querySelector('#catalog-prompt');true");
    await open(); await filter("Other project"); await key("Enter", "Enter", 13);
    await opened("/fixture/other");
    assert.equal(await evaluate("projectFocusPrompt===document.querySelector('#catalog-prompt') && projectFocusPrompt.textContent.replaceAll('\\u00a0',' ').includes('preserved project draft')"), true);

    // A retained existing-session editor must not reclaim focus after project navigation.
    await evaluate(`[...document.querySelectorAll('.session-dock [data-session-node]')].find(n=>n.textContent.startsWith('one -')).closest('[role=tab]').click()`);
    assert.equal(await wait("!!document.querySelector('#session-prompt')"), true);
    await evaluate("document.querySelector('#session-prompt').focus()");
    await open(); await filter("Other project"); await key("Enter", "Enter", 13);
    await opened("/fixture/other");
    assert.equal(await evaluate("document.querySelector('#catalog-prompt').textContent.replaceAll('\\u00a0',' ').includes('preserved project draft')"), true);

    // Successful folder import closes through a different asynchronous path.
    await evaluate("projectFocusOrigin.focus()");
    await open(); await filter("C:/fixture/imported"); await key("Enter", "Enter", 13);
    assert.equal(await wait("!!document.querySelector('.project-import-confirm button')"), true);
    await evaluate("document.querySelector('.project-import-confirm button').click()");
    await opened("C:/fixture/imported");
    assert.deepEqual(await evaluate("projectFocusFixture.imports.map(r=>r.confirmed)"), [false, true]);
    assert.equal(await evaluate("settingsShellFixture.sends.length"), 0);

    // Catalog-only navigation also focuses its draft, including in a narrow workspace.
    await evaluate("localStorage.setItem('settingsFixtureOwned','false')");
    await command("Emulation.setDeviceMetricsOverride", { width: 390, height: 650, deviceScaleFactor: 1, mobile: false });
    await command("Page.reload");
    assert.equal(await wait("!!document.querySelector('#catalog-prompt')"), true);
    await evaluate(`document.querySelector('button[aria-label="Open command palette"]').focus()`);
    await open(); await filter("Other project"); await key("Enter", "Enter", 13);
    await opened("/fixture/other");
    assert.equal(await evaluate("projectFocusFixture.imports.length"), 0);
    assert.equal(await evaluate("settingsShellFixture.sends.length"), 0);
    assert.deepEqual(await evaluate("projectFocusErrors"), []);
    assert.deepEqual(exceptions, []);
  } finally {
    // Edge's launcher can exit while the browser it started goes on: the browser itself is asked to close.
    if (socket?.readyState === WebSocket.OPEN) socket.send(JSON.stringify({ id: 9999, method: "Browser.close" }));
    socket?.close(); browser?.kill(); await rm(root, { recursive: true, force: true, maxRetries: 8, retryDelay: 100 });
  }
});
