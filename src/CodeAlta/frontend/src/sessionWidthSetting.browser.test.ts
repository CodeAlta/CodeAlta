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

type Slider = { left: number; width: number; y: number; handle: number; shown: string; value: number };
// The track of the slider, the middle of its handle, and what the setting says beside it. The setting is brought
// into view first: a narrow window scrolls its page.
const inspect = `(() => {
  const field = document.querySelector(".settings-session-width");
  field.scrollIntoView({ block: "center" });
  const track = field.querySelector(".bp6-slider-track").getBoundingClientRect();
  const handle = field.querySelector(".bp6-slider-handle"), box = handle.getBoundingClientRect();
  return { left: track.left, width: track.width, y: track.top + track.height / 2, handle: box.left + box.width / 2, shown: field.querySelector("output").textContent,
    value: Number(handle.getAttribute("aria-valuenow")) };
})()`;

test("the width of the conversation is set with the slider of the settings, by a press on its track and by a drag", { skip: !edge, timeout: 60_000 }, async () => {
  const root = await mkdtemp(join(tmpdir(), "codealta-session-width-setting-"));
  let browser: ReturnType<typeof spawn> | undefined;
  let socket: WebSocket | undefined;
  try {
    // Built as the application is, and as a development build: there React mounts everything twice, and the second
    // mount of the slider is in the open dialog.
    const modes = ["production", "development"];
    for (const mode of modes) await build({ entryPoints: [fileURLToPath(new URL("./sessionWidthSetting.mount.tsx", import.meta.url))], outfile: join(root, `${mode}.js`),
      bundle: true, platform: "browser", format: "iife", define: { "process.env.NODE_ENV": JSON.stringify(mode) } });
    await writeFile(join(root, "style.css"), await readFile(new URL("../node_modules/@blueprintjs/core/lib/css/blueprint.css", import.meta.url), "utf8")
      + await readFile(new URL("./style.css", import.meta.url), "utf8"));
    for (const mode of modes) await writeFile(join(root, `${mode}.html`),
      `<!doctype html><html><head><meta charset="utf-8"><link rel="stylesheet" href="style.css"></head><body><div id="root"></div><script src="${mode}.js"></script></body></html>`);
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
    const slider = async () => { await frames(); return await evaluate(inspect) as Slider; };
    const mouse = (type: string, x: number, y: number, pressed = false) => command("Input.dispatchMouseEvent", { type, x, y, button: pressed || type !== "mouseMoved" ? "left" : "none",
      buttons: type === "mouseReleased" || !pressed && type === "mouseMoved" ? 0 : 1, clickCount: type === "mouseMoved" ? 0 : 1 });
    // Where a width is on the track: 40 at its start, 100 at its end.
    const at = (track: Slider, percent: number) => track.left + track.width * (percent - 40) / 60;
    const changes = async () => await evaluate("sessionWidthSetting.changes.splice(0)") as number[];
    const press = async (percent: number) => {
      const track = await slider();
      await mouse("mouseMoved", at(track, percent), track.y); await mouse("mousePressed", at(track, percent), track.y); await mouse("mouseReleased", at(track, percent), track.y);
      return await slider();
    };
    await command("Page.enable");
    for (const mode of modes) {
    await command("Emulation.setDeviceMetricsOverride", { width: 1280, height: 900, deviceScaleFactor: 1, mobile: false });
    await command("Page.navigate", { url: pathToFileURL(join(root, `${mode}.html`)).href });
    assert.equal(await evaluate(`new Promise(resolve => { const end = Date.now() + 7000; (function check() {
      if (window.sessionWidthSetting && document.querySelector("dialog[open] .settings-session-width .bp6-slider-handle")) resolve(true); else if (Date.now() > end) resolve(false); else setTimeout(check, 20); })(); })`), true);

    // The setting starts at the whole width: the handle is at the end of the track.
    const start = await slider();
    assert.ok(start.width > 100, `the track has a width: ${start.width}`);
    assert.deepEqual([start.shown, start.value], ["100%", 100]);
    assert.ok(Math.abs(start.handle - at(start, 100)) <= 2, `the handle is at the end of the track: ${start.handle} for ${at(start, 100)}`);

    // A press in the middle of the track sets the width of the middle, not the smallest one.
    const middle = await press(70);
    assert.deepEqual([middle.shown, middle.value, await changes()], ["70%", 70, [70]]);
    assert.ok(Math.abs(middle.handle - at(middle, 70)) <= 2, `the handle is in the middle of the track: ${middle.handle} for ${at(middle, 70)}`);
    // A press further on sets that width, not the largest one.
    assert.deepEqual([(await press(85)).shown, await changes()], ["85%", [85]]);

    // A drag of the handle goes through every step on its way, down and up, and ends where the button is released.
    const drag = async (from: number, to: number) => {
      const track = await slider(), steps = Math.abs(to - from) / 5 * 4;
      await mouse("mouseMoved", at(track, from), track.y); await mouse("mousePressed", at(track, from), track.y);
      for (let step = 1; step <= steps; step++) await mouse("mouseMoved", at(track, from + (to - from) * step / steps), track.y, true);
      // The handle is still the one that was pressed: the slider was not made again under the pointer.
      assert.equal(await evaluate(`document.querySelector(".settings-session-width .bp6-slider-handle").classList.contains("bp6-active")`), true);
      await mouse("mouseReleased", at(track, to), track.y);
      return await slider();
    };
    assert.deepEqual([(await drag(85, 55)).shown, await changes()], ["55%", [80, 75, 70, 65, 60, 55]]);
    assert.deepEqual([(await drag(55, 100)).shown, await changes()], ["100%", [60, 65, 70, 75, 80, 85, 90, 95, 100]]);
    assert.deepEqual([(await drag(100, 40)).value, (await changes()).at(-1)], [40, 40]);

    // The keyboard moves the handle by one step.
    await evaluate(`document.querySelector(".settings-session-width .bp6-slider-handle").focus()`);
    await command("Input.dispatchKeyEvent", { type: "keyDown", key: "ArrowRight", code: "ArrowRight", windowsVirtualKeyCode: 39 });
    await command("Input.dispatchKeyEvent", { type: "keyUp", key: "ArrowRight", code: "ArrowRight", windowsVirtualKeyCode: 39 });
    assert.deepEqual([(await slider()).shown, await changes()], ["45%", [45]]);

    // Another language gives the button beside the slider another width, and the track with it: a press is still
    // read on the track as it is now.
    const english = await slider();
    await evaluate(`sessionWidthSetting.setLocale("de")`);
    const german = await slider();
    assert.notEqual(Math.round(german.width), Math.round(english.width), "the track changed with the language");
    assert.ok(Math.abs(german.handle - at(german, 45)) <= 2, `the handle follows the track: ${german.handle} for ${at(german, 45)}`);
    assert.deepEqual([(await press(70)).shown, await changes()], ["70%", [70]]);
    assert.deepEqual([(await press(95)).shown, await changes()], ["95%", [95]]);

    // A narrow window leaves the slider less room: the same.
    await command("Emulation.setDeviceMetricsOverride", { width: 330, height: 700, deviceScaleFactor: 1, mobile: false });
    const narrow = await slider();
    assert.ok(narrow.width < german.width - 20, `the track is shorter in a narrow window: ${narrow.width} for ${german.width}`);
    assert.deepEqual([(await press(60)).shown, await changes()], ["60%", [60]]);
    assert.deepEqual([(await drag(60, 80)).shown, await changes()], ["80%", [65, 70, 75, 80]]);
    }
  } finally {
    // Edge's launcher can exit while the browser it started goes on: the browser itself is asked to close.
    if (socket?.readyState === WebSocket.OPEN) socket.send(JSON.stringify({ id: 9999, method: "Browser.close" }));
    socket?.close(); browser?.kill(); await rm(root, { recursive: true, force: true, maxRetries: 8, retryDelay: 100 });
  }
});
