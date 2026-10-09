import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { existsSync } from "node:fs";
import { mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { fileURLToPath, pathToFileURL } from "node:url";
import { build } from "esbuild";
import { browserBaseArgs, browserExecutable } from "../browserTarget";

const edge = browserExecutable;

// The line of the folder in the header of the side: how it is cut, and where it is against the buttons beside it.
const header = `(() => {
  const line = document.querySelector(".editor-side-root"), box = line.getBoundingClientRect(), text = line.querySelector("bdi").firstChild;
  const letter = index => { const range = document.createRange(); range.setStart(text, index); range.setEnd(text, index + 1); return range.getBoundingClientRect(); };
  const first = letter(0), last = letter(text.length - 1), actions = document.querySelector(".editor-side-actions").getBoundingClientRect();
  const side = document.querySelector(".editor-side").getBoundingClientRect(), title = document.querySelector(".editor-side-title").getBoundingClientRect();
  return { cut: line.scrollWidth > line.clientWidth, lines: Math.round(box.height / parseFloat(getComputedStyle(line).lineHeight)),
    startHidden: first.right <= box.left + 1, endShown: last.left >= box.left - 1 && last.right <= box.right + 1,
    under: box.top >= title.bottom - 1, clear: box.right <= actions.left + 1, inside: actions.right <= side.right + 1,
    compact: document.querySelector(".editor-side-header").getBoundingClientRect().height <= 36, tooltip: line.title };
})()`;
// The status bar: the button of the path, as wide as its text, and the items after it at the end of the bar.
const status = `(() => {
  const bar = document.querySelector(".editor-status").getBoundingClientRect(), button = document.querySelector("button.editor-status-path");
  const box = button.getBoundingClientRect(), items = [...document.querySelectorAll(".editor-status-item")].map(item => item.getBoundingClientRect());
  return { text: button.textContent, tooltip: button.title, icon: button.querySelector("svg").getAttribute("class"), width: Math.round(box.width),
    atEnd: Math.round(bar.right - items[items.length - 1].right), afterPath: items[0].left >= box.right - 1, oneRow: bar.height < 30 };
})()`;

test("the code editor says where its folder and its file are, and copies the full path of the file", { skip: !edge, timeout: 60_000 }, async () => {
  const root = await mkdtemp(join(tmpdir(), "codealta-editor-paths-"));
  let browser: ReturnType<typeof spawn> | undefined;
  let socket: WebSocket | undefined;
  try {
    await build({ entryPoints: [fileURLToPath(new URL("./editorPaths.mount.tsx", import.meta.url))], outfile: join(root, "fixture.js"), bundle: true, platform: "browser", format: "iife" });
    await writeFile(join(root, "style.css"), await readFile(new URL("../../node_modules/@blueprintjs/core/lib/css/blueprint.css", import.meta.url), "utf8")
      + await readFile(new URL("../style.css", import.meta.url), "utf8") + await readFile(new URL("./editor.css", import.meta.url), "utf8"));
    const page = join(root, "fixture.html");
    await writeFile(page, '<!doctype html><html><head><meta charset="utf-8"><link rel="stylesheet" href="style.css"></head><body><div id="root"></div><script src="fixture.js"></script></body></html>');
    browser = spawn(edge!, [...browserBaseArgs, `--user-data-dir=${join(root, "profile")}`, "--remote-debugging-port=0", "about:blank"], { stdio: "ignore", windowsHide: true });
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
    const until = (condition: string) => evaluate(`new Promise(resolve => { const end = Date.now() + 7000; (function check() {
      if (${condition}) resolve(true); else if (Date.now() > end) resolve(false); else setTimeout(check, 20); })(); })`);
    await command("Page.enable");
    await command("Emulation.setDeviceMetricsOverride", { width: 1280, height: 900, deviceScaleFactor: 1, mobile: false });
    await command("Page.navigate", { url: pathToFileURL(page).href });
    assert.equal(await until(`window.editorPaths && document.querySelector(".editor-side-root") && document.querySelector("button.editor-status-path")`), true);
    const folder = await evaluate("editorPaths.root") as string, file = `${folder}\\src\\app\\main.ts`;

    // The folder is one line under the name of the project. Longer than the side, it loses its start and keeps
    // its end, beside the buttons of the header and not under them; the whole of it is its tooltip.
    assert.deepEqual(await evaluate(header), { cut: true, lines: 1, startHidden: true, endShown: true, under: true, clear: true, inside: true, compact: true, tooltip: folder });

    // The status bar is still one row. The path in the folder is its text, the full path its tooltip, and what
    // follows the path stays at the end of the bar.
    const before = await evaluate(status) as { text: string; tooltip: string; icon: string; width: number; atEnd: number; afterPath: boolean; oneRow: boolean };
    assert.deepEqual({ text: before.text, tooltip: before.tooltip, atEnd: before.atEnd, afterPath: before.afterPath, oneRow: before.oneRow },
      { text: "src/app/main.ts", tooltip: file, atEnd: 6, afterPath: true, oneRow: true });
    assert.ok(before.width < 160, `the button is as wide as its text: ${before.width}`);

    // A click copies the full path, and says so for a moment where the icon of the file is.
    await evaluate(`document.querySelector("button.editor-status-path").click()`);
    assert.equal(await until(`document.querySelector("button.editor-status-path").title === "Copied"`), true);
    assert.deepEqual(await evaluate("editorPaths.copied"), [file]);
    const copied = await evaluate(status) as typeof before;
    assert.notEqual(copied.icon, before.icon);
    assert.deepEqual([copied.text, copied.width, copied.atEnd], [before.text, before.width, before.atEnd], "nothing of the bar moves");
    assert.equal(await until(`document.querySelector("button.editor-status-path").title !== "Copied"`), true);
    assert.deepEqual(await evaluate(status), before);
  } finally {
    // Edge's launcher can exit while the browser it started goes on: the browser itself is asked to close.
    if (socket?.readyState === WebSocket.OPEN) socket.send(JSON.stringify({ id: 9999, method: "Browser.close" }));
    socket?.close(); browser?.kill(); await rm(root, { recursive: true, force: true, maxRetries: 8, retryDelay: 100 });
  }
});
