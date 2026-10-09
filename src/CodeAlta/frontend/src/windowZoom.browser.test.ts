import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { existsSync } from "node:fs";
import { mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { fileURLToPath, pathToFileURL } from "node:url";
import { build } from "esbuild";
import { browserBaseArgs, browserExecutable } from "./browserTarget";

const edge = browserExecutable;

type Bar = { zoom: { text: string; left: number; right: number; height: number; middle: number; color: string } | null;
  theme: { left: number; right: number; height: number; middle: number; color: string }; actions: { left: number; right: number }; tabs: number; width: number };
// The end of the title bar: the zoom, the theme switch, the box that holds them, and where the tabs must end.
const bar = `(() => {
  const box = element => { const rect = element.getBoundingClientRect(); return { left: Math.round(rect.left), right: Math.round(rect.right), height: Math.round(rect.height),
    middle: Math.round(rect.top + rect.height / 2), color: getComputedStyle(element).color }; };
  const zoom = document.querySelector(".window-zoom"), actions = box(document.querySelector(".window-actions"));
  return { zoom: zoom && { text: zoom.textContent, ...box(zoom) }, theme: box(document.querySelector(".theme-switch")), actions: { left: actions.left, right: actions.right },
    tabs: Math.round(document.querySelector(".tabs").getBoundingClientRect().right), width: document.documentElement.clientWidth };
})()`;
type Steps = { out: { title: string; disabled: boolean }; value: { title: string; text: string }; in: { title: string; disabled: boolean }; top: number; right: number } | null;
// What the zoom opens: one step out, the percentage, one step in.
const steps = `(() => {
  const menu = document.querySelector(".window-zoom-steps");
  if (!menu) return null;
  const [out, value, into] = menu.querySelectorAll("button"), rect = menu.getBoundingClientRect();
  return { out: { title: out.title, disabled: out.disabled }, value: { title: value.title, text: value.textContent }, in: { title: into.title, disabled: into.disabled },
    top: Math.round(rect.top), right: Math.round(rect.right) };
})()`;

test("the zoom of the window is in the title bar before the theme switch, and steps with the commands of the keyboard", { skip: !edge, timeout: 60_000 }, async () => {
  const root = await mkdtemp(join(tmpdir(), "codealta-window-zoom-"));
  let browser: ReturnType<typeof spawn> | undefined;
  let socket: WebSocket | undefined;
  try {
    await build({ entryPoints: [fileURLToPath(new URL("./windowZoom.mount.tsx", import.meta.url))], outfile: join(root, "fixture.js"), bundle: true, platform: "browser", format: "iife" });
    await writeFile(join(root, "style.css"), await readFile(new URL("../node_modules/@blueprintjs/core/lib/css/blueprint.css", import.meta.url), "utf8")
      + await readFile(new URL("./style.css", import.meta.url), "utf8"));
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
    const frames = () => evaluate("new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(() => resolve(true))))");
    const shown = async () => { await frames(); return await evaluate(bar) as Bar; };
    const ran = async () => await evaluate("windowZoom.ran.splice(0)") as string[];
    // A click on a button of the open steps, then the zoom the title bar says once the host has answered.
    const click = async (index: number, expected: string) => {
      await evaluate(`document.querySelectorAll(".window-zoom-steps button")[${index}].click()`);
      assert.equal(await until(`document.querySelector(".window-zoom").textContent === ${JSON.stringify(expected)}`), true, `the zoom becomes ${expected}`);
      return await evaluate(steps) as NonNullable<Steps>;
    };
    await command("Page.enable");
    await command("Emulation.setDeviceMetricsOverride", { width: 1100, height: 600, deviceScaleFactor: 1, mobile: false });
    await command("Page.navigate", { url: pathToFileURL(page).href });
    assert.equal(await until(`window.windowZoom && document.querySelector(".window-zoom") && document.querySelector(".theme-switch")`), true);

    // The zoom is written before the theme switch, at its height and in its color, in both themes. The two end
    // where the room of the native caption buttons starts, and the tabs end before them.
    for (const theme of ["dark", "light"]) {
      await evaluate(`windowZoom.setTheme(${JSON.stringify(theme)})`);
      const { zoom, theme: switched, actions, tabs, width } = await shown();
      assert.ok(zoom, theme);
      assert.deepEqual([zoom.text, zoom.height, zoom.middle, zoom.color], ["100%", switched.height, switched.middle, switched.color], theme);
      assert.ok(actions.left <= zoom.left && zoom.right <= switched.left && switched.right <= actions.right, `${theme}: ${JSON.stringify({ zoom, switched, actions })}`);
      assert.deepEqual([actions.right, tabs <= actions.left], [width - 138, true], `${theme}: ${JSON.stringify({ actions, tabs, width })}`);
    }
    await evaluate(`windowZoom.setTheme("dark")`);
    const first = await shown();

    // It opens its steps under itself, inside the window; each button names its command and its key.
    assert.equal(await evaluate(steps), null);
    await evaluate(`document.querySelector(".window-zoom").click()`);
    assert.equal(await until(`document.querySelector(".window-zoom-steps")`), true);
    await frames();
    const opened = await evaluate(steps) as NonNullable<Steps>;
    assert.deepEqual({ out: opened.out, value: opened.value, in: opened.in }, { out: { title: "Zoom out (Ctrl+-)", disabled: false },
      value: { title: "Reset Zoom (Ctrl+0)", text: "100%" }, in: { title: "Zoom in (Ctrl+=)", disabled: false } });
    assert.ok(opened.top >= first.zoom!.middle && opened.right <= first.width, JSON.stringify(opened));

    // A step in and a step out run the commands, and the steps stay open for the next one.
    assert.equal((await click(2, "110%")).value.text, "110%");
    assert.equal((await click(2, "125%")).value.text, "125%");
    assert.equal((await click(0, "110%")).value.text, "110%");
    assert.deepEqual(await ran(), ["zoomIn", "zoomIn", "zoomOut"]);
    // The percentage goes back to 100%.
    await click(2, "125%");
    assert.equal((await click(1, "100%")).value.text, "100%");
    assert.deepEqual(await ran(), ["zoomIn", "resetZoom"]);

    // At the largest zoom there is no further step in, at the smallest none out; the title bar keeps its layout.
    let last = opened;
    for (const zoom of ["110%", "125%", "150%", "175%", "200%", "250%", "300%", "400%", "500%"]) last = await click(2, zoom);
    assert.deepEqual([last.in.disabled, last.out.disabled], [true, false]);
    const largest = await shown();
    assert.deepEqual([largest.zoom!.text, largest.zoom!.left, largest.zoom!.right, largest.theme.left, largest.actions], ["500%", first.zoom!.left, first.zoom!.right, first.theme.left, first.actions]);
    await click(1, "100%");
    for (const zoom of ["90%", "80%", "75%", "67%", "50%", "33%", "25%"]) last = await click(0, zoom);
    assert.deepEqual([last.out.disabled, last.in.disabled], [true, false]);
    assert.deepEqual([(await shown()).zoom!.left, (await shown()).zoom!.right], [first.zoom!.left, first.zoom!.right]);
    await ran();

    // A zoom asked elsewhere (a key, a typed command) is the one shown, here and in the open steps.
    await evaluate(`windowZoom.run("resetZoom"); windowZoom.run("zoomIn")`);
    assert.equal(await until(`document.querySelector(".window-zoom").textContent === "110%" && document.querySelector(".window-zoom-value").textContent === "110%"`), true);

    // Without a shell there is no zoom to show: the theme switch is alone, and the tabs get the room back.
    await evaluate(`windowZoom.setShell(false)`);
    const alone = await shown();
    assert.equal(alone.zoom, null);
    assert.deepEqual([alone.actions.right - alone.actions.left, alone.actions.right, alone.tabs], [40, alone.width - 138, alone.width - 138 - 40]);
    assert.equal(first.actions.right - first.actions.left, 82);
  } finally {
    // Edge's launcher can exit while the browser it started goes on: the browser itself is asked to close.
    if (socket?.readyState === WebSocket.OPEN) socket.send(JSON.stringify({ id: 9999, method: "Browser.close" }));
    socket?.close(); browser?.kill(); await rm(root, { recursive: true, force: true, maxRetries: 8, retryDelay: 100 });
  }
});
