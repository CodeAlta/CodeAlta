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
test("the window shows one space at a time: its projects, its sessions and its own tabs, which come back with it", { skip: !edge, timeout: 90_000 }, async () => {
  const root = await mkdtemp(join(tmpdir(), "codealta-spaces-"));
  let browser: ReturnType<typeof spawn> | undefined;
  let socket: WebSocket | undefined;
  try {
    await build({ entryPoints: [fileURLToPath(new URL("../main.tsx", import.meta.url))], outfile: join(root, "fixture.js"),
      bundle: true, platform: "browser", format: "iife", loader: { ".css": "empty", ".flf": "text", ".svg": "dataurl" },
      define: { "import.meta.env.VITE_DEMO_MODE": '"false"' }, plugins: [{ name: "isolated-spaces", setup(bundle) {
        bundle.onResolve({ filter: /^#neoastra$/ }, () => ({ path: fileURLToPath(new URL("./spaces.neoastra.mount.ts", import.meta.url)) }));
        bundle.onResolve({ filter: /^monaco-editor\/.*\?worker$/ }, args => ({ path: args.path, namespace: "fixture-worker" }));
        bundle.onLoad({ filter: /.*/, namespace: "fixture-worker" }, async () => {
          const worker = await build({ entryPoints: [fileURLToPath(new URL("../../node_modules/monaco-editor/esm/vs/editor/editor.worker.js", import.meta.url))],
            bundle: true, platform: "browser", format: "iife", write: false });
          return { loader: "js", contents: `export default class extends Worker { constructor() {
            const url=URL.createObjectURL(new Blob([${JSON.stringify(worker.outputFiles[0].text)}],{type:"text/javascript"}));
            super(url); URL.revokeObjectURL(url);
          } }` };
        });
      } }] });
    await writeFile(join(root, "style.css"), readFileSync(new URL("../../node_modules/flexlayout-react/style/light.css", import.meta.url), "utf8")
      + readFileSync(new URL("../style.css", import.meta.url), "utf8") + readFileSync(new URL("./spaces.css", import.meta.url), "utf8"));
    const page = join(root, "fixture.html");
    await writeFile(page, '<!doctype html><html><head><link rel="stylesheet" href="style.css"></head><body><div id="root"></div><script src="fixture.js"></script></body></html>');
    browser = spawn(edge!, ["--headless=new", "--disable-gpu", "--no-first-run", "--disable-background-networking", "--disable-extensions", "--edge-skip-compat-layer-relaunch", "--allow-file-access-from-files",
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
    const wait = (condition: string) => evaluate(`new Promise(resolve=>{const end=Date.now()+7000;const tick=()=>{if(${condition})resolve(true);else if(Date.now()>end)resolve(document.body.innerText.slice(0,600));else setTimeout(tick,25);};tick();})`);
    // What the window shows: the space of the switch, the projects of the Explorer, the tabs of the dock.
    const projects = () => evaluate("[...document.querySelectorAll('#project-list .project-action-row > button:first-child')].map(row=>row.textContent.trim())");
    const tabs = () => evaluate("[...document.querySelectorAll('.flexlayout__tab_button')].map(tab=>tab.textContent.trim())");
    const shown = () => evaluate("document.querySelector('.space-switch-name')?.textContent ?? null");
    const chip = (name: string) => evaluate(`[...document.querySelectorAll('.space-chip')].find(chip=>chip.getAttribute('aria-label').startsWith(${JSON.stringify(name)})).click()`);
    const openSession = (title: string) => evaluate(`[...document.querySelectorAll('.session-row > button:first-child, .explorer-session > button:first-child, #project-list button')].find(row=>row.textContent.trim().startsWith(${JSON.stringify(title)})).click()`);

    await command("Page.enable");
    await command("Runtime.enable");
    await command("Emulation.setFocusEmulationEnabled", { enabled: true });
    await command("Emulation.setDeviceMetricsOverride", { width: 1280, height: 900, deviceScaleFactor: 1, mobile: false });
    await command("Page.navigate", { url: pathToFileURL(page).href });
    assert.equal(await wait("!!document.querySelector('#catalog-prompt')"), true, exceptions.join("\n"));
    await evaluate("localStorage.setItem('settingsFixtureOwned','true');localStorage.setItem('settingsFixtureSecondProject','true')");
    await command("Page.reload");
    assert.equal(await wait("!!document.querySelector('#project-list') && !!document.querySelector('.space-switch-name')"), true, exceptions.join("\n"));

    // The default space: every project, and one icon for each space at the foot of the Explorer.
    assert.equal(await shown(), "Default");
    assert.deepEqual(await projects(), ["Other project", "Project"]);
    assert.equal(await evaluate("document.querySelectorAll('.space-chip').length"), 2);
    assert.equal(await wait("spacesFixture.shown.includes('default')"), true, "The host is told which space the window shows.");
    assert.equal(await wait("[...document.querySelectorAll('.flexlayout__tab_button')].some(tab=>tab.textContent.includes('one'))"), true);
    const defaultTabs = await tabs() as string[];

    // Work has the other project alone: its Explorer and its tabs are its own, and the session of the first project is no longer in the page.
    await chip("Work");
    assert.equal(await wait("document.querySelector('.space-switch-name')?.textContent==='Work'"), true);
    assert.deepEqual(await projects(), ["Other project"]);
    assert.equal(await evaluate("document.querySelector('.project-rail .panel-title .count')?.textContent"), "1");
    assert.equal((await tabs() as string[]).some(tab => tab.includes("one")), false);
    assert.equal(await evaluate("localStorage.getItem('codealta.desktop.space.v1')"), "work");
    assert.equal(await wait("spacesFixture.shown.at(-1)==='work'"), true);
    await openSession("other-session");
    assert.equal(await wait("[...document.querySelectorAll('.flexlayout__tab_button')].some(tab=>tab.textContent.includes('other-session'))"), true);
    const workTabs = await tabs() as string[];
    assert.equal(await wait("!!localStorage.getItem('codealta.desktop.sessionTabs.v1.work')?.includes('other-session')"), true, "The tabs of a space are kept under its own key.");
    assert.equal(String(await evaluate("localStorage.getItem('codealta.desktop.sessionTabs.v1')")).includes("other-session"), false);

    // Back to the default space: the tabs it had are there again; and back to Work with the keyboard: its own.
    await chip("Default");
    assert.equal(await wait("document.querySelector('.space-switch-name')?.textContent==='Default'"), true);
    assert.deepEqual(await projects(), ["Other project", "Project"]);
    assert.deepEqual(await tabs(), defaultTabs);
    await evaluate("window.dispatchEvent(new KeyboardEvent('keydown',{key:'PageDown',ctrlKey:true,altKey:true,bubbles:true,cancelable:true}))");
    assert.equal(await wait("document.querySelector('.space-switch-name')?.textContent==='Work'"), true);
    assert.deepEqual(await tabs(), workTabs);

    // A command asks the window for a space.
    await evaluate("spacesFixture.tell({kind:'show',spaceId:'default'})");
    assert.equal(await wait("document.querySelector('.space-switch-name')?.textContent==='Default'"), true);
    await evaluate("spacesFixture.tell({kind:'show',spaceId:'work'})");
    assert.equal(await wait("document.querySelector('.space-switch-name')?.textContent==='Work'"), true);

    // A session that waits for the user in a project the shown space does not have is called out at the foot of the Explorer.
    await evaluate("spacesFixture.sessions=[{sessionId:'one',projectId:'project',title:'one',running:false,backgroundTasks:0,failed:false,waiting:true}]");
    assert.equal(await wait("!!document.querySelector('.space-call')"), true);
    assert.equal(await evaluate("document.querySelector('.space-call').title"), "one waits for you in Default");
    assert.equal(await evaluate("document.querySelector('.space-switch').dataset.attention ?? null"), null, "The default space has every project: it is not what the dot of the switch is for.");
    await evaluate("document.querySelector('.space-call').click()");
    assert.equal(await wait("document.querySelector('.space-switch-name')?.textContent==='Default' && document.querySelector('.flexlayout__tab_button--selected')?.textContent.includes('one')"), true);
    assert.equal(await evaluate("document.querySelectorAll('.space-call').length"), 0);

    // The next start comes back on the space the window showed, with its tabs.
    await chip("Work");
    assert.equal(await wait("document.querySelector('.space-switch-name')?.textContent==='Work'"), true);
    await command("Page.reload");
    assert.equal(await wait("document.querySelector('.space-switch-name')?.textContent==='Work' && !!document.querySelector('#project-list')"), true);
    assert.deepEqual(await projects(), ["Other project"]);
    assert.equal(await wait("[...document.querySelectorAll('.flexlayout__tab_button')].some(tab=>tab.textContent.includes('other-session'))"), true);
    assert.deepEqual(exceptions, []);
  } finally {
    // Edge's launcher can exit while the browser it started goes on: the browser itself is asked to close.
    if (socket?.readyState === WebSocket.OPEN) socket.send(JSON.stringify({ id: 9999, method: "Browser.close" }));
    socket?.close(); browser?.kill(); await rm(root, { recursive: true, force: true, maxRetries: 8, retryDelay: 100 });
  }
});
