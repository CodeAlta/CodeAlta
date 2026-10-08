import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { existsSync } from "node:fs";
import { mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { fileURLToPath, pathToFileURL } from "node:url";
import { build } from "esbuild";

const edge = ["C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe", "C:/Program Files/Microsoft/Edge/Application/msedge.exe"].find(existsSync);

// What the view shows now: where it is, and for each file where its section starts, how tall it and its diff are,
// and whether its diff is built ("editor"), said in words ("notice"), awaited ("pending") or folded ("folded").
const inspect = `(() => {
  const view = document.querySelector(".changes-all");
  const sections = {};
  for (const section of view.querySelectorAll(":scope > .changes-section")) {
    const body = section.querySelector(".changes-section-body");
    sections[section.dataset.path] = { top: section.offsetTop, height: section.offsetHeight, body: body ? body.offsetHeight : 0,
      shows: !body ? "folded" : body.querySelector(".monaco-editor .view-line") ? "editor" : body.dataset.pending ? "pending" : body.classList.contains("changes-section-notice") ? "notice" : "empty",
      header: section.querySelector(".changes-section-header").getBoundingClientRect().top - view.getBoundingClientRect().top };
  }
  return { top: view.scrollTop, height: view.clientHeight, scrollHeight: view.scrollHeight, current: allChangesFixture.current, reads: { ...allChangesFixture.reads }, sections };
})()`;

// Real diff editors in a real page that scrolls: what is built, how tall it is and where the view stands are the browser's own.
test("the view of all changed files builds the diffs near it, gives each the height of its content and keeps its place", { skip: !edge, timeout: 120_000 }, async () => {
  const root = await mkdtemp(join(tmpdir(), "codealta-all-changes-"));
  let browser: ReturnType<typeof spawn> | undefined;
  let socket: WebSocket | undefined;
  try {
    await build({ entryPoints: [fileURLToPath(new URL("./allChanges.mount.tsx", import.meta.url))], outfile: join(root, "fixture.js"),
      bundle: true, platform: "browser", format: "iife", loader: { ".ttf": "dataurl" }, plugins: [{ name: "inline-worker", setup(bundle) {
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
    // The styles of the editor come out of the bundle beside it; those of the application are the ones it ships.
    await writeFile(join(root, "style.css"), await readFile(new URL("../../node_modules/@blueprintjs/core/lib/css/blueprint.css", import.meta.url), "utf8")
      + await readFile(new URL("../style.css", import.meta.url), "utf8"));
    const page = join(root, "fixture.html");
    await writeFile(page, '<!doctype html><html><head><meta charset="utf-8"><link rel="stylesheet" href="fixture.css"><link rel="stylesheet" href="style.css"></head>'
      + '<body style="margin:0"><div id="root"></div><script src="fixture.js"></script></body></html>');
    browser = spawn(edge!, ["--headless=new", "--disable-gpu", "--no-first-run", "--disable-background-networking", "--disable-extensions", "--edge-skip-compat-layer-relaunch", "--allow-file-access-from-files",
      `--user-data-dir=${join(root, "profile")}`, "--remote-debugging-port=0", "about:blank"], { stdio: "ignore", windowsHide: true });
    let port = "";
    for (let attempt = 0; attempt < 100 && !port; attempt++) {
      try { port = (await readFile(join(root, "profile", "DevToolsActivePort"), "utf8")).split(/\r?\n/)[0]; }
      catch { await new Promise(resolve => setTimeout(resolve, 50)); }
    }
    assert.match(port, /^\d+$/);
    const pages = await (await fetch(`http://127.0.0.1:${port}/json/list`)).json() as { type?: string; webSocketDebuggerUrl?: string }[];
    socket = new WebSocket(pages.find(tab => tab.type === "page")!.webSocketDebuggerUrl!);
    await new Promise<void>((resolve, reject) => { socket!.addEventListener("open", () => resolve(), { once: true }); socket!.addEventListener("error", reject, { once: true }); });
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
    type Section = { top: number; height: number; body: number; shows: string; header: number };
    type State = { top: number; height: number; scrollHeight: number; current: string | null; reads: Record<string, number>; sections: Record<string, Section> };
    // The view once it shows what is expected and has kept the same sizes for a moment, or what it shows instead.
    const settled = async (expected: string) => JSON.parse(await evaluate(`new Promise(resolve => { const end = Date.now() + 8000; let last = "", since = 0; (function check() {
      const state = ${inspect}, text = JSON.stringify(state);
      if (text !== last) { last = text; since = Date.now(); }
      if (((${expected})(state) && Date.now() - since > 250) || Date.now() > end) resolve(text); else setTimeout(check, 25); })(); })`)) as State;

    await command("Page.enable");
    await command("Emulation.setDeviceMetricsOverride", { width: 1000, height: 700, deviceScaleFactor: 1, mobile: false });
    await command("Page.navigate", { url: pathToFileURL(page).href });
    assert.equal(await evaluate(`new Promise(resolve => { const end = Date.now() + 15000; (function check() {
      if (window.allChangesFixture && document.querySelector(".changes-all .changes-section")) resolve(true); else if (Date.now() > end) resolve(false); else setTimeout(check, 20); })(); })`), true);

    // The first files are built; the ones far below are not read at all.
    const start = await settled(`state => state.sections["a.txt"].shows === "editor" && state.sections["b.txt"].shows === "editor"`);
    assert.equal(start.height, 600);
    assert.equal(start.sections["a.txt"].shows, "editor", JSON.stringify(start));
    assert.deepEqual([start.reads["a.txt"], start.reads["long.txt"], start.reads["gone.txt"]], [1, undefined, undefined]);
    assert.equal(start.sections["long.txt"].shows, "pending");
    // A diff is as tall as what it shows: the three changed lines and the lines around them, not the four hundred of the file.
    const first = start.sections["a.txt"];
    assert.ok(first.body > 100 && first.body < 1000, JSON.stringify(first));
    assert.equal(first.height, 34 + first.body + 1);
    assert.equal(start.sections["b.txt"].top, first.height);

    // A file that is asked for comes to the top, and stays there while the diffs around it take their size.
    await evaluate(`allChangesFixture.reveal("new.txt")`);
    const shown = await settled(`state => state.sections["new.txt"].shows === "editor" && state.sections["gone.txt"].shows === "editor"`);
    assert.equal(shown.top, shown.sections["new.txt"].top, JSON.stringify(shown));
    assert.equal(shown.current, "new.txt");
    assert.equal(Math.round(shown.sections["new.txt"].header), 0);
    // A new file is shown whole, a deleted one too, and a binary file says what it is.
    assert.equal(shown.sections["new.txt"].body, 30 * 20 + 20 + 16, JSON.stringify(shown.sections["new.txt"]));
    assert.equal(shown.sections["image.bin"].shows, "notice");
    // The first file is far now: its diff is let go, and its section keeps the height it had.
    assert.equal(shown.sections["a.txt"].shows, "pending");
    assert.equal(shown.sections["a.txt"].height, first.height);

    // The last file is too long to be shown whole: it takes what the view shows under its header and scrolls by itself.
    await evaluate(`allChangesFixture.reveal("long.txt")`);
    const long = await settled(`state => state.sections["long.txt"].shows === "editor" && state.sections["long.txt"].body === 566`);
    assert.equal(long.sections["long.txt"].body, 600 - 34, JSON.stringify(long.sections["long.txt"]));
    assert.equal(long.reads["long.txt"], 1);
    assert.equal(long.current, "long.txt");

    // Scrolled by the user, the file at the top of the view is the selected one, and its header stays at the top.
    await evaluate(`(() => { const view = document.querySelector(".changes-all"); view.dispatchEvent(new WheelEvent("wheel", { bubbles: true }));
      view.scrollTop = view.querySelector('[data-path="c.txt"]').offsetTop + 40; })()`);
    const scrolled = await settled(`state => state.current === "c.txt" && state.sections["c.txt"].shows === "editor"`);
    assert.equal(scrolled.current, "c.txt", JSON.stringify(scrolled));
    assert.equal(scrolled.top - scrolled.sections["c.txt"].top, 40);
    assert.equal(Math.round(scrolled.sections["c.txt"].header), 0);

    // A file folded above the view takes its diff away: what is read stays where it is.
    await evaluate(`allChangesFixture.toggle("a.txt")`);
    const folded = await settled(`state => state.sections["a.txt"].shows === "folded"`);
    assert.equal(folded.sections["a.txt"].height, 35);
    assert.equal(folded.top - folded.sections["c.txt"].top, 40, JSON.stringify(folded));
    assert.equal(folded.current, "c.txt");
  } finally {
    socket?.close();
    browser?.kill();
    await rm(root, { recursive: true, force: true, maxRetries: 5, retryDelay: 200 }).catch(() => { /* The profile of a browser that is still closing. */ });
  }
});
