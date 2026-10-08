import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { existsSync } from "node:fs";
import { mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { fileURLToPath, pathToFileURL } from "node:url";
import { build } from "esbuild";
import { translate } from "./localization";

const edge = ["C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe", "C:/Program Files/Microsoft/Edge/Application/msedge.exe"].find(existsSync);
const fixture = "window.settingsFilesFixture";
const calls = (method: string) => `${fixture}.state.calls.filter(call => call.method === ${JSON.stringify(method)}).map(call => call.request)`;
// The rows of the files of the page, above its list.
const rows = "[...document.querySelectorAll('.settings-file-locations > .settings-file-location')]";
const button = (scope: string, title: string) => `${scope}.querySelector('button[title=${JSON.stringify(title)}]')`;

test("a settings page says where its files are, and opens, copies and shows them", { skip: !edge, timeout: 120_000 }, async () => {
  const root = await mkdtemp(join(tmpdir(), "codealta-settings-files-"));
  let browser: ReturnType<typeof spawn> | undefined;
  let socket: WebSocket | undefined;
  try {
    await build({ entryPoints: [fileURLToPath(new URL("./settingsFiles.mount.tsx", import.meta.url))], outfile: join(root, "fixture.js"), bundle: true, platform: "browser", format: "iife",
      define: { "import.meta.env.VITE_DEMO_MODE": '"false"' }, plugins: [{ name: "isolated-settings-files", setup(bundle) {
        bundle.onResolve({ filter: /^#neoastra$/ }, () => ({ path: fileURLToPath(new URL("./settingsFiles.neoastra.mount.ts", import.meta.url)) }));
      } }] });
    await writeFile(join(root, "style.css"), await readFile(new URL("../node_modules/@blueprintjs/core/lib/css/blueprint.css", import.meta.url), "utf8")
      + await readFile(new URL("./style.css", import.meta.url), "utf8"));
    const page = join(root, "fixture.html");
    await writeFile(page, '<!doctype html><html><head><meta charset="utf-8"><link rel="stylesheet" href="style.css"></head><body><div id="root"></div><script src="fixture.js"></script></body></html>');
    browser = spawn(edge!, ["--headless=new", "--disable-gpu", "--no-first-run", "--disable-background-networking", "--disable-extensions", "--edge-skip-compat-layer-relaunch", `--user-data-dir=${join(root, "profile")}`, "--remote-debugging-port=0", "about:blank"], { stdio: "ignore", windowsHide: true });
    let port = "";
    for (let attempt = 0; attempt < 100 && !port; attempt++) {
      try { port = (await readFile(join(root, "profile", "DevToolsActivePort"), "utf8")).split(/\r?\n/)[0]; }
      catch { await new Promise(resolve => setTimeout(resolve, 50)); }
    }
    assert.match(port, /^\d+$/);
    const pages: { type?: string; webSocketDebuggerUrl?: string }[] = await (await fetch(`http://127.0.0.1:${port}/json/list`, { signal: AbortSignal.timeout(10_000) })).json();
    socket = new WebSocket(pages.find(value => value.type === "page")!.webSocketDebuggerUrl!);
    await new Promise<void>((resolve, reject) => { socket!.addEventListener("open", () => resolve(), { once: true }); socket!.addEventListener("error", () => reject(Error("browser unavailable")), { once: true }); });
    let sequence = 0;
    const command = (method: string, params: object = {}) => new Promise<{ result?: { value?: unknown }; exceptionDetails?: unknown }>((resolve, reject) => {
      const id = ++sequence, timer = setTimeout(() => reject(Error(`${method} timed out`)), 12_000);
      const reply = (event: MessageEvent) => {
        const message = JSON.parse(String(event.data)); if (message.id !== id) return;
        socket!.removeEventListener("message", reply); clearTimeout(timer);
        if (message.error) reject(Error(`${method}: ${JSON.stringify(message.error)}`)); else resolve(message.result ?? {});
      };
      socket!.addEventListener("message", reply); socket!.send(JSON.stringify({ id, method, params }));
    });
    const evaluate = async (expression: string) => {
      const result = await command("Runtime.evaluate", { expression, returnByValue: true, awaitPromise: true });
      assert.equal(result.exceptionDetails, undefined, JSON.stringify(result.exceptionDetails));
      return result.result?.value;
    };
    // An action, then the answers of the host and what React shows of them.
    const act = (script: string) => evaluate(`(async () => { ${script}; for (let turn = 0; turn < 4; turn++) await new Promise(resolve => requestAnimationFrame(() => setTimeout(resolve, 0))); return true; })()`);
    const until = async (expression: string, label = expression) => assert.equal(await evaluate(`new Promise(resolve => { const end = Date.now() + 7000; (function check() {
      if (${expression}) resolve(true); else if (Date.now() > end) resolve(false); else setTimeout(check, 20); })(); })`), true, label);
    await command("Page.enable");
    await command("Emulation.setDeviceMetricsOverride", { width: 1280, height: 900, deviceScaleFactor: 1, mobile: false });
    await command("Page.navigate", { url: pathToFileURL(page).href });
    await until(`${rows}.length === 2`, "the folders of the plugins are listed above the list");

    // Each row has its scope, its whole path, and its buttons; a folder CodeAlta creates is opened before it exists.
    const home = "C:\\Users\\someone-with-a-long-name\\.alta\\plugins", work = "D:\\work\\a-project-with-a-long-name\\.alta\\plugins";
    assert.deepEqual(await evaluate(`${rows}.map(row => [row.querySelector('.settings-file-label').textContent, row.querySelector('code').textContent, row.querySelector('code').title,
      [...row.querySelectorAll('button')].map(value => value.title)])`), [
      ["Global", home, home, ["Edit in the code editor", "Copy path", "Reveal in File Explorer"]],
      ["Project", work, work, ["Edit in the code editor", "Copy path"]]]);

    // In a narrow window a path is cut at its start: its end, which says what it is, stays in view.
    await act(`${fixture}.setWidth(430)`);
    assert.deepEqual(await evaluate(`${rows}.map(row => { const code = row.querySelector('code').getBoundingClientRect(), text = row.querySelector('bdi').getBoundingClientRect(), frame = row.getBoundingClientRect();
      return [text.left < code.left - 1, Math.abs(text.right - code.right) <= 1, code.height < 24, [...row.querySelectorAll('button')].every(value => value.getBoundingClientRect().right <= frame.right + 1)]; })`),
      [[true, true, true, true], [true, true, true, true]], "start cut, end shown, one line, buttons in the row");
    // Nothing of the page is wider than the page: what would make it scroll sideways is named.
    assert.deepEqual(await evaluate(`(() => { const page = document.querySelector('main'), edge = page.getBoundingClientRect().right - parseFloat(getComputedStyle(page).paddingRight);
      return [...page.querySelectorAll('*')].filter(value => value.getBoundingClientRect().right > edge + 1 && value.getBoundingClientRect().width > 0)
        .map(value => value.tagName.toLowerCase() + '.' + value.className).slice(0, 6); })()`), [], "the page does not scroll sideways");
    await act(`${fixture}.setWidth(900)`);

    // Open: the page names the folder by kind and scope, never by its path, and leaves Settings once it is shown.
    await act(`${button(`${rows}[0]`, "Edit in the code editor")}.click()`);
    assert.deepEqual(await evaluate(calls("files.open")), [{ expectedEpoch: "epoch", projectId: "p", kind: "plugins", scope: "Global", id: null, part: null }]);
    assert.equal(await evaluate(`${fixture}.state.opened`), 1);
    // A folder that cannot be shown says so, and the page stays.
    await act(`${fixture}.state.openStatus = "failed"; ${button(`${rows}[1]`, "Edit in the code editor")}.click()`);
    assert.equal(await evaluate(`${fixture}.state.opened`), 1);
    assert.equal(await evaluate("document.querySelector('[role=alert].bp6-callout')?.textContent"), translate("en", "The file could not be opened."));
    await act(`${fixture}.state.openStatus = "ok"`);

    // Copy and show.
    await act(`${button(`${rows}[1]`, "Copy path")}.click()`);
    assert.deepEqual(await evaluate(`${fixture}.state.copied`), [work]);
    await act(`${button(`${rows}[0]`, "Reveal in File Explorer")}.click()`);
    assert.deepEqual(await evaluate(calls("files.reveal")), [{ expectedEpoch: "epoch", projectId: "p", kind: "plugins", scope: "Global", id: null, part: null }]);

    // The row of a source plugin has the path of its folder, shown in the file manager by the id of that folder.
    const card = (name: string) => `[...document.querySelectorAll('.settings-editor-rows .bp6-card')].find(value => value.querySelector('strong').textContent === ${JSON.stringify(name)})`;
    assert.equal(await evaluate(`${card("commits")}.querySelector('.settings-file-location code').title`), `${home}\\commits`);
    assert.equal(await evaluate(`${card("MCP")}.querySelector('.settings-file-location')`), null, "A plugin that ships with CodeAlta has no folder.");
    await act(`${button(card("commits"), "Reveal in File Explorer")}.click()`);
    assert.deepEqual(await evaluate(calls("reveal")), [{ expectedEpoch: "epoch", projectId: "plugin:global:commits", path: "" }]);

    // A configuration file that does not parse is named in red above the list, and is opened from there.
    const problem = "document.querySelector('.plugin-problems .plugin-failure')";
    assert.equal(await evaluate(`[...${problem}.childNodes].filter(node => node.nodeType === Node.TEXT_NODE).map(node => node.textContent).join("")`),
      translate("en", "The configuration file {path} could not be read.", { path: "D:\\work\\a-project-with-a-long-name\\.alta\\config.toml" }) + " (2,1): expected ]");
    assert.equal(await evaluate(`getComputedStyle(${problem}).color !== getComputedStyle(${card("commits")}.querySelector('strong')).color`), true, "what could not be read is not in the color of the text");
    await act(`${button(problem, "Edit in the code editor")}.click()`);
    assert.deepEqual(await evaluate(`${calls("files.open")}.at(-1)`), { expectedEpoch: "epoch", projectId: "p", kind: "config", scope: "Project", id: null, part: null });
    assert.equal(await evaluate(`${fixture}.state.opened`), 2);
  } finally {
    // Edge's launcher can exit while the browser it started goes on: the browser itself is asked to close.
    if (socket?.readyState === WebSocket.OPEN) socket.send(JSON.stringify({ id: 9999, method: "Browser.close" }));
    socket?.close();
    if (browser && browser.exitCode === null) { const closed = new Promise<void>(resolve => browser!.once("exit", () => resolve())); browser.kill(); await Promise.race([closed, new Promise(resolve => setTimeout(resolve, 5000))]); }
    await rm(root, { recursive: true, force: true, maxRetries: 10, retryDelay: 100 });
  }
});
