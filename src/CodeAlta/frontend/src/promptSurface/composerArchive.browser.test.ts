import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { fileURLToPath, pathToFileURL } from "node:url";
import { build } from "esbuild";
import { browserBaseArgs, browserExecutable, endBrowser, waitForBrowserPort } from "../browserTarget";

// Mounts main.tsx itself: the archive asked from the `⋯` menu of a composer is asked where the user is, above that
// menu, not beside the row of the session in the Explorer, which is collapsed here.
test("archiving a session with child sessions from its composer asks above the composer, with the Explorer collapsed", { skip: !browserExecutable, timeout: 90_000 }, async () => {
  const root = await mkdtemp(join(tmpdir(), "codealta-composer-archive-"));
  let browser: ReturnType<typeof spawn> | undefined, socket: WebSocket | undefined;
  try {
    await build({ entryPoints: [fileURLToPath(new URL("../main.tsx", import.meta.url))], outfile: join(root, "fixture.js"),
      bundle: true, platform: "browser", format: "iife", loader: { ".css": "empty", ".flf": "text", ".svg": "dataurl" },
      define: { "import.meta.env.VITE_DEMO_MODE": '"false"' }, plugins: [{ name: "isolated-composer-archive", setup(bundle) {
        bundle.onResolve({ filter: /^#neoastra$/ }, () => ({ path: fileURLToPath(new URL("./composerArchive.neoastra.mount.ts", import.meta.url)) }));
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
    await writeFile(join(root, "style.css"), (await Promise.all([
      "../../node_modules/@blueprintjs/core/lib/css/blueprint.css", "../../node_modules/flexlayout-react/style/light.css", "../style.css",
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
      assert.equal(value.exceptionDetails, undefined, JSON.stringify(value.exceptionDetails)); return value.result?.value;
    };
    const until = async (condition: string, what: string) => {
      const found = await evaluate(`new Promise(resolve=>{const end=Date.now()+8000;const tick=()=>{if(${condition})resolve(true);else if(Date.now()>end)resolve(false);else setTimeout(tick,20)};tick()})`);
      assert.equal(found, true, `${what}; ${exceptions.join("\n")}`);
    };
    const row = (id: string) => `document.querySelector('.session-row[data-session-id="${id}"]')`;
    const shown = (selector: string) => `(()=>{const element=${selector};if(!element)return false;const box=element.getBoundingClientRect();return box.width>0&&box.height>0&&!!element.offsetParent})()`;
    const more = "document.querySelector('.prompt-surface .prompt-surface-more-button')";
    const archive = "[...document.querySelectorAll('.prompt-surface-more-menu .bp6-button')].find(button=>button.textContent.trim()==='Archive…')";
    const question = "document.querySelector('.rename-popover .confirm-form')";

    await command("Page.enable"); await command("Runtime.enable");
    await command("Emulation.setFocusEmulationEnabled", { enabled: true });
    await command("Emulation.setDeviceMetricsOverride", { width: 1280, height: 900, deviceScaleFactor: 1, mobile: false });
    await command("Page.addScriptToEvaluateOnNewDocument", { source: "localStorage.setItem('settingsFixtureOwned','true')" });
    await command("Page.navigate", { url: pathToFileURL(page).href });
    await until(`!!${row("one")} && ${row("one")}.firstElementChild.getAttribute('aria-pressed')==='true' && ${shown(more)}`, "session one open with its composer");

    // The Explorer is collapsed: the row of the session is not on screen.
    await evaluate("[...document.querySelectorAll('button')].find(button=>button.getAttribute('aria-label')==='Explorer').click()");
    await until(`!${shown(row("one"))}`, "the Explorer is collapsed");

    // Archive… in the `⋯` menu asks above that menu, with the child sessions chosen.
    await evaluate(`${more}.click()`);
    await until(`!!${archive} && !${archive}.disabled`, "Archive… in the menu of the composer");
    await evaluate(`${archive}.click()`);
    await until(shown(question), "the question of the archive");
    const asked = await evaluate(`(()=>{const form=${question};const box=form.getBoundingClientRect();const button=${more}.getBoundingClientRect();
      return {text:form.textContent,tree:form.querySelector('input[value=tree]')?.checked,inside:box.left>=0&&box.top>=0&&box.right<=innerWidth&&box.bottom<=innerHeight,
        above:box.bottom<=button.top+2,near:box.left<button.right&&box.right>button.left-40}})()`);
    assert.ok(asked.text.includes("Archive this session?") && asked.text.includes("Archive with its child session"), JSON.stringify(asked));
    assert.equal(asked.tree, true, "the child sessions go with it by default");
    assert.ok(asked.inside && asked.above && asked.near, `the question is above the menu of the composer: ${JSON.stringify(asked)}`);

    // Cancel leaves everything as it is; asked again and confirmed, the session goes with its child.
    await evaluate(`[...${question}.querySelectorAll('button')].find(button=>button.textContent.trim()==='Cancel').click()`);
    await until(`!${question}`, "Cancel closes the question");
    assert.deepEqual(await evaluate("composerArchiveFixture.state.archives"), []);
    await evaluate(`${more}.click()`);
    await until(`!!${archive}`, "Archive… again");
    await evaluate(`${archive}.click()`);
    await until(shown(question), "the question again");
    await evaluate(`[...${question}.querySelectorAll('button')].find(button=>button.textContent.trim()==='Archive').click()`);
    await until("composerArchiveFixture.state.archives.length===1", "the archive was asked of the host");
    assert.deepEqual(await evaluate("composerArchiveFixture.state.archives"), [{ sessionId: "one", withChildren: true }]);
    assert.deepEqual(exceptions, []);
  } finally {
    if (socket?.readyState === WebSocket.OPEN) socket.send(JSON.stringify({ id: 9999, method: "Browser.close" }));
    socket?.close(); await endBrowser(browser); await rm(root, { recursive: true, force: true, maxRetries: 8, retryDelay: 100 });
  }
});
