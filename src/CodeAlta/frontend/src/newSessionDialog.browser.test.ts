import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { fileURLToPath, pathToFileURL } from "node:url";
import { build } from "esbuild";
import { browserBaseArgs, browserExecutable, waitForBrowserPort } from "./browserTarget";

test("project + creates through a modal with remembered model and workspace, without sending a prompt", { skip: !browserExecutable, timeout: 90_000 }, async () => {
  const root = await mkdtemp(join(tmpdir(), "codealta-new-session-"));
  let browser: ReturnType<typeof spawn> | undefined, socket: WebSocket | undefined;
  try {
    await build({ entryPoints: [fileURLToPath(new URL("./main.tsx", import.meta.url))], outfile: join(root, "fixture.js"),
      bundle: true, platform: "browser", format: "iife", loader: { ".css": "empty", ".flf": "text", ".svg": "dataurl" },
      define: { "import.meta.env.VITE_DEMO_MODE": '"false"' }, plugins: [{ name: "isolated-creation", setup(bundle) {
        bundle.onResolve({ filter: /^#neoastra$/ }, () => ({ path: fileURLToPath(new URL("./newSessionDialog.neoastra.mount.ts", import.meta.url)) }));
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
    await writeFile(join(root, "style.css"), (await Promise.all([
      "../node_modules/@blueprintjs/core/lib/css/blueprint.css", "../node_modules/flexlayout-react/style/light.css", "./style.css", "./explorer/explorer.css",
    ].map(path => readFile(new URL(path, import.meta.url), "utf8")))).join("\n"));
    const page = join(root, "fixture.html");
    await writeFile(page, '<!doctype html><html><head><link rel="stylesheet" href="style.css"></head><body><div id="root"></div><script src="fixture.js"></script></body></html>');
    browser = spawn(browserExecutable!, [...browserBaseArgs, "--allow-file-access-from-files",
      `--user-data-dir=${join(root, "profile")}`, "--remote-debugging-port=0", "about:blank"], { stdio: ["ignore", "ignore", "pipe"], windowsHide: true });
    const port = await waitForBrowserPort(join(root, "profile"), browser);
    const pages = await (await fetch(`http://127.0.0.1:${port}/json/list`)).json() as { type: string; webSocketDebuggerUrl: string }[];
    socket = new WebSocket(pages.find(tab => tab.type === "page")!.webSocketDebuggerUrl);
    await new Promise<void>((resolve, reject) => { socket!.addEventListener("open", () => resolve(), { once: true }); socket!.addEventListener("error", reject, { once: true }); });
    const exceptions: string[] = [];
    socket.addEventListener("message", event => {
      const value = JSON.parse(String(event.data));
      if (value.method === "Runtime.exceptionThrown") exceptions.push(value.params.exceptionDetails.exception?.description ?? value.params.exceptionDetails.text);
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
      assert.equal(value.exceptionDetails, undefined, JSON.stringify(value.exceptionDetails)); return value.result?.value;
    };
    const until = async (condition: string, what: string) => {
      assert.equal(await evaluate(`new Promise(resolve=>{const end=Date.now()+7000;const tick=()=>{if(${condition})resolve(true);else if(Date.now()>end)resolve(false);else setTimeout(tick,20)};tick()})`), true,
        `${what}; ${exceptions.join("\n")}; dialog: ${await evaluate("document.querySelector('.new-session-dialog')?.textContent")}`);
    };
    const click = (selector: string) => evaluate(`document.querySelector(${JSON.stringify(selector)}).click()`);
    const select = (selector: string, value: string) => evaluate(`(() => { const node=document.querySelector(${JSON.stringify(selector)}); node.value=${JSON.stringify(value)}; node.dispatchEvent(new Event('change',{bubbles:true})); })()`);
    const key = async (key: string, virtual: number) => {
      for (const type of ["keyDown", "keyUp"]) await command("Input.dispatchKeyEvent", { type, key, code: key, windowsVirtualKeyCode: virtual });
    };
    const open = async () => { await click(".project-session-trigger"); await until("!!document.querySelector('.new-session-dialog[open]') && !document.querySelector('.new-session-dialog button[type=submit]').disabled", "ready creation dialog"); };
    await command("Page.enable"); await command("Runtime.enable");
    await command("Emulation.setFocusEmulationEnabled", { enabled: true });
    await command("Emulation.setDeviceMetricsOverride", { width: 1600, height: 1000, deviceScaleFactor: 1, mobile: false });
    await command("Page.addScriptToEvaluateOnNewDocument", { source: "localStorage.setItem('settingsFixtureOwned','true')" });
    await command("Page.navigate", { url: pathToFileURL(page).href });
    await until("!!document.querySelector('.project-session-trigger') && document.querySelector('.session-row > button[aria-pressed=true]')", "existing session selected");

    // Creation moved out of the actions menu and out of the Explorer's inline form.
    await click("#project-list .project-actions-trigger");
    await until("!!document.querySelector('.session-tab-popup')", "project actions");
    assert.equal(await evaluate("[...document.querySelectorAll('.session-tab-popup [role=menuitem]')].some(node=>node.textContent.trim()==='New session')"), false);
    await key("Escape", 27);
    await open();
    assert.equal(await evaluate("document.querySelector('.session-create')"), null);
    assert.equal(await evaluate("document.activeElement.id"), "new-session-name");
    await command("Input.insertText", { text: "Named work" });
    await click(".new-session-dialog .composer-selection");
    await until("!!document.querySelector('#composer-model-create')", "configuration picker inside the modal top layer");
    await select("#composer-agent-create", "plan");
    await select(".new-session-dialog select[aria-label='Provider for new session']", "alternate");
    await until("[...document.querySelector('#composer-model-create').options].some(option=>option.value==='old') && !document.querySelector('#composer-model-create').disabled", "alternate models");
    await select("#composer-model-create", "old");
    await key("Escape", 27);
    assert.equal(await evaluate("!!document.querySelector('.new-session-dialog[open]')"), true, "Escape closes the picker, not its parent dialog");
    await until("!!document.querySelector('.new-session-dialog .project-context-place')", "workspace picker");
    await click(".new-session-dialog .project-context-place");
    await until("!!document.querySelector('.work-place-form')", "working place");
    await evaluate("[...document.querySelectorAll('.work-place-form button')].find(node=>node.textContent==='New worktree').click()");
    await until("[...document.querySelectorAll('.work-place-form select option')].some(node=>node.value==='feature')", "base branches");
    await select(".work-place-form select", "feature");
    await key("Escape", 27);
    await click(".new-session-dialog button[type=submit]");
    await until("!document.querySelector('.new-session-dialog') && document.querySelector('.session-row[data-session-id=created-1] button[aria-pressed=true]')", "created session opens");
    assert.deepEqual(await evaluate("newSessionFixture.creates.map(({title,providerId,worktree,baseBranch})=>({title,providerId,worktree,baseBranch}))"),
      [{ title: "Named work", providerId: "alternate", worktree: true, baseBranch: "feature" }]);
    await until("[...document.querySelectorAll('.owned-session .composer-selection')].some(node=>node.textContent.includes('Plan') && node.textContent.includes('alternate') && node.textContent.includes('Old model'))", "chosen configuration on the created composer");
    assert.deepEqual(await evaluate("settingsShellFixture.sends"), [], "creation never sends the existing prompt");

    await open();
    assert.equal(await evaluate("document.querySelector('#new-session-name').value"), "", "names are not reused");
    assert.match(await evaluate("document.querySelector('.new-session-dialog .composer-selection').textContent"), /Plan.*alternate.*Old model/);
    assert.match(await evaluate("document.querySelector('.new-session-dialog .project-context-place').textContent"), /New worktree from feature/);
    await command("Emulation.setDeviceMetricsOverride", { width: 900, height: 650, deviceScaleFactor: 1, mobile: false });
    assert.equal(await evaluate("document.querySelector('.new-session-form').scrollWidth > document.querySelector('.new-session-form').clientWidth"), false, "narrow dialog has no horizontal overflow");
    await key("Escape", 27);
    await until("!document.querySelector('.new-session-dialog')", "cancel by keyboard");
    assert.equal(await evaluate("newSessionFixture.creates.length"), 1, "Cancel creates nothing");

    await open();
    await evaluate("newSessionFixture.hold=true");
    await click(".new-session-dialog button[type=submit]");
    await until("newSessionFixture.creates.length===2", "creation admitted once");
    await click(".new-session-dialog button[type=submit]");
    await key("Escape", 27);
    assert.equal(await evaluate("!!document.querySelector('.new-session-dialog[open]')"), true, "pending creation cannot be dismissed or duplicated");
    await evaluate("newSessionFixture.release(); newSessionFixture.hold=false");
    await until("!document.querySelector('.new-session-dialog')", "second creation completes");
    assert.equal(await evaluate("newSessionFixture.creates[1].title"), null, "session name is optional");
    assert.equal(await evaluate("newSessionFixture.creates.length"), 2);

    // A fresh window still reads the remembered model/provider and project workspace from local storage.
    await command("Page.reload");
    await until("!!document.querySelector('.project-session-trigger')", "reloaded shell");
    await open();
    assert.match(await evaluate("document.querySelector('.new-session-dialog .composer-selection').textContent"), /Plan.*alternate.*Old model/);
    await until("!!document.querySelector('.new-session-dialog .project-context-place')", "restored workspace");
    assert.match(await evaluate("document.querySelector('.new-session-dialog .project-context-place').textContent"), /New worktree from feature/);
    await key("Escape", 27);
    await until("!document.querySelector('.new-session-dialog')", "closed restored dialog");

    // Saved model IDs are preferences, not authority: a removed model falls back to the current catalog.
    await evaluate("localStorage.setItem('newSessionFixtureMissingModel','true')");
    await command("Page.reload");
    await until("!!document.querySelector('.project-session-trigger')", "shell with changed models");
    await open();
    assert.match(await evaluate("document.querySelector('.new-session-dialog .composer-selection').textContent"), /alternate.*New model.*High/);
    await evaluate("newSessionFixture.refused=true");
    await click(".new-session-dialog button[type=submit]");
    await until("document.querySelector('.new-session-dialog [role=alert]') && !document.querySelector('.new-session-dialog button[type=submit]').disabled", "definite refusal permits correcting the workspace");
    assert.equal(await evaluate("newSessionFixture.creates.length"), 1);
    await evaluate("newSessionFixture.refused=false; newSessionFixture.uncertain=true");
    await click(".new-session-dialog button[type=submit]");
    await until("document.querySelector('.new-session-dialog button[type=submit]').disabled && document.querySelector('.new-session-dialog [role=status]')?.textContent.includes('unconfirmed')", "uncertain creation is not retried");
    await click(".new-session-dialog button[type=submit]");
    assert.equal(await evaluate("newSessionFixture.creates.length"), 2);

    const preferences = await evaluate("localStorage.getItem('codealta.desktop.newSessionPreferences.v1')");
    await evaluate("localStorage.setItem('newSessionFixtureFailModels','true')");
    await command("Page.reload");
    await until("!!document.querySelector('.project-session-trigger')", "shell with failed model catalog");
    await click(".project-session-trigger");
    await until("!!document.querySelector('.new-session-dialog') && document.querySelector('.new-session-dialog').textContent.includes('Refresh composer choices')", "failed models can be refreshed");
    assert.equal(await evaluate("document.querySelector('.new-session-dialog button[type=submit]').disabled"), true);
    assert.equal(await evaluate("localStorage.getItem('codealta.desktop.newSessionPreferences.v1')"), preferences, "failed read never overwrites remembered choices");
    assert.equal(await evaluate("newSessionFixture.creates.length"), 0);

    await evaluate("localStorage.removeItem('newSessionFixtureFailModels'); localStorage.setItem('newSessionFixtureHoldModels','true')");
    await command("Page.reload");
    await until("!!document.querySelector('.project-session-trigger')", "shell with delayed models");
    await click(".project-session-trigger");
    await until("!!document.querySelector('.new-session-dialog') && newSessionFixture.modelReads>0", "model initialization pending");
    await click(".new-session-dialog .composer-selection");
    await until("!!document.querySelector('#composer-agent-create')", "agent choices while models load");
    assert.equal(await evaluate("document.querySelector('#composer-agent-create').disabled"), true, "an early agent change cannot freeze a temporary null model");
    assert.equal(await evaluate("document.querySelector('.new-session-dialog button[type=submit]').disabled"), true);
    await evaluate("newSessionFixture.holdModels=false; newSessionFixture.releaseModels()");
    await until("!document.querySelector('#composer-agent-create').disabled && !document.querySelector('.new-session-dialog button[type=submit]').disabled", "successful model initialization enables choices and creation");
    assert.equal(await evaluate("document.querySelector('#composer-model-create').value"), "new");

    // The shared choices and create pipeline still hand off an ordinary draft into its first Send.
    await key("Escape", 27);
    await until("!document.querySelector('.new-session-dialog .bp6-popover')", "configuration picker exit animation completed");
    await key("Escape", 27);
    await until("!document.querySelector('.new-session-dialog')", "closed after model initialization");
    await click("#project-list button[data-scope]");
    await until("!!document.querySelector('#catalog-prompt textarea')", "ordinary new-session draft");
    await evaluate("document.querySelector('#catalog-prompt').focus()");
    await command("Input.insertText", { text: "Original local draft" });
    await until("!document.querySelector('button[aria-label=\"Start session\"]').disabled", "draft is ready to send");
    await click("button[aria-label='Start session']");
    await until("settingsShellFixture.sends.length===1", "draft handoff sends exactly once");
    assert.equal(await evaluate("newSessionFixture.creates.length"), 1);
    assert.equal(await evaluate("newSessionFixture.creates[0].worktree"), true);
    assert.equal(await evaluate("newSessionFixture.creates[0].baseBranch"), "feature");
    assert.equal(await evaluate("settingsShellFixture.sends[0].text"), "Original local draft");
    assert.deepEqual(exceptions, []);
  } finally {
    socket?.close(); browser?.kill();
    if (browser && browser.exitCode === null) await new Promise<void>(resolve => browser!.once("exit", () => resolve()));
    await rm(root, { recursive: true, force: true, maxRetries: 10, retryDelay: 100 });
  }
});
