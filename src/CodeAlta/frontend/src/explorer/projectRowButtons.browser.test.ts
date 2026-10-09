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

type Layout = { width: number; name: number; buttons: Record<string, { left: number; width: number; shown: boolean }> };
const row = (id: string) => `document.querySelector('#project-list button[data-scope="${id}"]').parentElement`;
// The buttons of a row, in their order: where each is in the row, the room it takes and whether it is shown.
const inspect = (id: string) => `(() => {
  const row = ${row(id)}, box = row.getBoundingClientRect(), buttons = {};
  for (const button of row.querySelectorAll(":scope > .project-row-action, :scope > .project-actions-trigger")) {
    const rect = button.getBoundingClientRect();
    buttons[button.classList[button.classList.length - 1].replace(/^project-|-trigger$/g, "")] = { left: Math.round(rect.left - box.left), width: Math.round(rect.width),
      shown: rect.width > 0 && getComputedStyle(button).visibility === "visible" };
  }
  return { width: Math.round(box.width), name: Math.round(row.querySelector("strong").getBoundingClientRect().width), buttons };
})()`;
const shown = (layout: Layout) => Object.entries(layout.buttons).filter(([, button]) => button.shown).map(([name]) => name);
const places = (layout: Layout) => Object.fromEntries(Object.entries(layout.buttons).map(([name, button]) => [name, button.left]));

test("the buttons of a project row are where the icon of an open tab already is, with the pointer on the row or not", { skip: !edge, timeout: 60_000 }, async () => {
  const root = await mkdtemp(join(tmpdir(), "codealta-project-row-buttons-"));
  let browser: ReturnType<typeof spawn> | undefined;
  let socket: WebSocket | undefined;
  try {
    await build({ entryPoints: [fileURLToPath(new URL("./projectRowButtons.mount.tsx", import.meta.url))], outfile: join(root, "fixture.js"), bundle: true, platform: "browser", format: "iife" });
    await writeFile(join(root, "style.css"), await readFile(new URL("../../node_modules/@blueprintjs/core/lib/css/blueprint.css", import.meta.url), "utf8")
      + await readFile(new URL("../style.css", import.meta.url), "utf8") + await readFile(new URL("./explorer.css", import.meta.url), "utf8"));
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
    const frames = () => evaluate("new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(() => resolve(true))))");
    const pointer = async (x: number, y: number) => { await command("Input.dispatchMouseEvent", { type: "mouseMoved", x, y }); await frames(); };
    const away = () => pointer(900, 700);
    const over = async (id: string) => {
      const at = await evaluate(`(() => { const box = ${row(id)}.getBoundingClientRect(); return { x: box.left + 60, y: box.top + box.height / 2 }; })()`) as { x: number; y: number };
      await pointer(at.x, at.y);
    };
    const layout = async (id: string) => await evaluate(inspect(id)) as Layout;
    await command("Page.enable");
    await command("Emulation.setFocusEmulationEnabled", { enabled: true });
    await command("Emulation.setDeviceMetricsOverride", { width: 1280, height: 900, deviceScaleFactor: 1, mobile: false });
    await command("Page.navigate", { url: pathToFileURL(page).href });
    assert.equal(await evaluate(`new Promise(resolve => { const end = Date.now() + 7000; (function check() {
      if (document.querySelector("#project-list button[data-scope]")) resolve(true); else if (Date.now() > end) resolve(false); else setTimeout(check, 20); })(); })`), true);
    assert.equal(await evaluate(`matchMedia("(hover: hover)").matches`), true, "this is about a pointer that can be over a row");

    await away();
    const { width } = await layout("editor");
    // The star, the changes, the code editor, the terminal and the menu, each in a column of its own at the end of the row.
    const columns = { favorite: width - 120, changes: width - 96, editor: width - 72, terminal: width - 48, actions: width - 24 };
    const all = Object.keys(columns);

    // A row with an open tab, or with a terminal, shows that icon alone, and keeps the room of the other buttons:
    // none of them moves when the pointer comes, and the name is cut at the same place.
    for (const [id, open] of [["editor", ["editor"]], ["changes", ["changes"]], ["terminal", ["terminal"]], ["both", ["changes", "editor", "terminal"]]] as const) {
      await away();
      const alone = await layout(id);
      assert.deepEqual(shown(alone), open, `${id}: what is shown without the pointer`);
      assert.deepEqual(places(alone), columns, `${id}: where the buttons are without the pointer`);
      await over(id);
      const pointed = await layout(id);
      assert.deepEqual(shown(pointed), all, `${id}: what is shown under the pointer`);
      assert.deepEqual(places(pointed), columns, `${id}: where the buttons are under the pointer`);
      assert.equal(pointed.name, alone.name, `${id}: the name`);
    }

    // A row with nothing to show gives its whole width to the name, and its buttons come in the same columns.
    await away();
    const free = await layout("none");
    assert.deepEqual(shown(free), []);
    assert.deepEqual(Object.values(free.buttons).map(button => button.width), [0, 0, 0, 0, 0]);
    await over("none");
    const used = await layout("none");
    assert.deepEqual(shown(used), all);
    assert.deepEqual(places(used), columns);
    assert.equal(free.name - used.name, 120, "the buttons take their room from the name while the pointer is there");

    // An archived project has no tab and no terminal to open: its star and its menu are in the same last columns.
    await over("old");
    const old = await layout("old");
    assert.deepEqual(shown(old), ["favorite", "actions"]);
    assert.deepEqual(places(old), { favorite: columns.favorite + 72, actions: columns.actions });

    // The keyboard on a row shows its buttons as the pointer does.
    await away();
    await evaluate(`${row("editor")}.querySelector("button").focus()`);
    await frames();
    const focused = await layout("editor");
    assert.deepEqual(shown(focused), all);
    assert.deepEqual(places(focused), columns);
  } finally {
    // Edge's launcher can exit while the browser it started goes on: the browser itself is asked to close.
    if (socket?.readyState === WebSocket.OPEN) socket.send(JSON.stringify({ id: 9999, method: "Browser.close" }));
    socket?.close(); browser?.kill(); await rm(root, { recursive: true, force: true, maxRetries: 8, retryDelay: 100 });
  }
});
