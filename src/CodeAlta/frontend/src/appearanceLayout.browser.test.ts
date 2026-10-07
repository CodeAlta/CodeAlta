import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { existsSync } from "node:fs";
import { mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { fileURLToPath, pathToFileURL } from "node:url";
import { build } from "esbuild";
import { locales } from "./localization";

const edge = ["C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe", "C:/Program Files/Microsoft/Edge/Application/msedge.exe"].find(existsSync);

// What is wrong with the rows of the card as they are laid out now: a control over the name of its row, or
// a name or a control that leaves the card. `arrangement` is, for each row, whether its control is beside
// its name ("beside") or below it ("below").
const inspect = `(() => {
  const card = document.querySelector(".appearance-settings"), style = getComputedStyle(card), box = card.getBoundingClientRect();
  const left = box.left + parseFloat(style.paddingLeft) + parseFloat(style.borderLeftWidth);
  const right = box.right - parseFloat(style.paddingRight) - parseFloat(style.borderRightWidth);
  const problems = [], arrangement = {};
  for (const row of card.querySelectorAll(".settings-field")) {
    const name = row.querySelector(":scope > label, :scope > .settings-field-label");
    const range = document.createRange(); range.selectNodeContents(name);
    const text = range.getBoundingClientRect();
    if (text.left < left - 1 || text.right > right + 1) problems.push(name.textContent + ": its name leaves the card");
    const controls = [...row.querySelectorAll(":scope > .settings-field-control > *")].map(element => element.getBoundingClientRect()).filter(rect => rect.width > 0);
    for (const rect of controls) {
      if (rect.left < left - 1 || rect.right > right + 1) problems.push(name.textContent + ": its control leaves the card");
      if (rect.left < text.right - 1 && rect.right > text.left + 1 && rect.top < text.bottom - 1 && rect.bottom > text.top + 1) problems.push(name.textContent + ": its control covers its name");
    }
    arrangement[row.querySelector(":scope > .settings-field-control [id]")?.id ?? "theme"] = controls.every(rect => rect.top >= text.bottom - 1) ? "below" : "beside";
  }
  return { problems, arrangement, scrolls: document.documentElement.scrollWidth > document.documentElement.clientWidth };
})()`;

// The same for the editor of a color scheme: nothing of it leaves it, and no color has its well over its name.
const inspectEditor = `(() => {
  const editor = document.querySelector(".scheme-editor"), box = editor.getBoundingClientRect(), problems = [];
  for (const element of editor.querySelectorAll("*")) {
    const rect = element.getBoundingClientRect();
    if (rect.width > 0 && (rect.left < box.left - 1 || rect.right > box.right + 1)) problems.push((typeof element.className === "string" && element.className || element.tagName) + ": leaves the editor");
  }
  for (const row of editor.querySelectorAll(".scheme-color")) {
    const name = row.querySelector("label"), range = document.createRange(); range.selectNodeContents(name);
    const text = range.getBoundingClientRect(), controls = row.querySelector(".scheme-color-controls").getBoundingClientRect();
    const end = Math.min(text.right, name.getBoundingClientRect().right);
    if (controls.left < end - 1 && controls.right > text.left + 1 && controls.top < text.bottom - 1 && controls.bottom > text.top + 1) problems.push(name.textContent + ": its controls cover its name");
  }
  return [...new Set(problems)];
})()`;

test("the rows of the Appearance card stay readable from the narrowest Settings window to a wide one, in every language", { skip: !edge, timeout: 120_000 }, async () => {
  const root = await mkdtemp(join(tmpdir(), "codealta-appearance-layout-"));
  let browser: ReturnType<typeof spawn> | undefined;
  let socket: WebSocket | undefined;
  try {
    await build({ entryPoints: [fileURLToPath(new URL("./appearanceLayout.mount.tsx", import.meta.url))], outfile: join(root, "fixture.js"), bundle: true, platform: "browser", format: "iife" });
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
    await command("Page.enable");
    await command("Emulation.setDeviceMetricsOverride", { width: 1280, height: 900, deviceScaleFactor: 1, mobile: false });
    await command("Page.navigate", { url: pathToFileURL(page).href });
    assert.equal(await evaluate(`new Promise(resolve => { const end = Date.now() + 7000; (function check() {
      if (window.appearanceLayout && document.querySelector(".appearance-settings .settings-field")) resolve(true); else if (Date.now() > end) resolve(false); else setTimeout(check, 20); })(); })`), true);
    const laidOut = async (locale: string, width: number) => {
      await evaluate(`appearanceLayout.setLocale(${JSON.stringify(locale)}); appearanceLayout.setWidth(${width}); new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)))`);
      return await evaluate(inspect) as { problems: string[]; arrangement: Record<string, string>; scrolls: boolean };
    };
    // From what the Settings window leaves the card at its smallest size (270) to a wide window.
    for (const locale of locales) for (const width of [270, 300, 340, 380, 420, 460, 500, 540, 580, 620, 700, 900]) {
      const { problems, scrolls } = await laidOut(locale, width);
      assert.deepEqual(problems, [], `${locale} at ${width}`);
      assert.equal(scrolls, false, `${locale} at ${width}`);
    }
    // Wide: every control is beside its name, as it always was.
    for (const locale of locales) {
      const wide = await laidOut(locale, 900);
      assert.deepEqual([...new Set(Object.values(wide.arrangement))], ["beside"], `${locale} wide: ${JSON.stringify(wide.arrangement)}`);
    }
    // Narrow: a list or a group goes below its name, and a switch is small enough to stay beside it.
    const narrow = (await laidOut("en", 270)).arrangement;
    for (const id of ["settings-language", "theme", "settings-color-scheme", "settings-project-sort", "settings-recent-count", "settings-on-close"]) assert.equal(narrow[id], "below", id);
    for (const id of ["settings-darker", "settings-rail-collapsed"]) assert.equal(narrow[id], "beside", id);

    // The editor of a color scheme, opened on a copy of the selected scheme, at the same sizes.
    await evaluate(`document.querySelector(".scheme-customize").click(); new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)))`);
    assert.equal(await evaluate(`!!document.querySelector(".scheme-editor .scheme-color")`), true);
    for (const locale of locales) for (const width of [270, 300, 380, 460, 540, 620, 900]) {
      const { problems, scrolls } = await laidOut(locale, width);
      assert.deepEqual(problems, [], `${locale} at ${width} with the editor`);
      assert.equal(scrolls, false, `${locale} at ${width} with the editor`);
      assert.deepEqual(await evaluate(inspectEditor), [], `${locale} at ${width}: the editor`);
    }
  } finally {
    socket?.close();
    browser?.kill();
    await new Promise(resolve => setTimeout(resolve, 200));
    await rm(root, { recursive: true, force: true }).catch(() => { /* The profile of a browser that is still exiting stays in the temporary folder. */ });
  }
});
