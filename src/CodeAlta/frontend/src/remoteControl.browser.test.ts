import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { fileURLToPath, pathToFileURL } from "node:url";
import { build } from "esbuild";
import { browserBaseArgs, browserExecutable } from "./browserTarget";

const edge = browserExecutable;

// The color of the icon of each button, and the colors the theme gives green, yellow and red.
const colors = `(() => {
  const color = selector => getComputedStyle(document.querySelector(selector).querySelector("svg") ?? document.querySelector(selector)).color;
  return { off: color('[data-remote-control="off"]'), connecting: color('[data-remote-control="connecting"]'), connected: color('[data-remote-control="connected"]'),
    failed: color('[data-remote-control="failed"]'), neighbour: color("[data-neighbour]"),
    green: color('[data-color="green"]'), yellow: color('[data-color="yellow"]'), red: color('[data-color="red"]') };
})()`;

test("the Remote Control button is in the color of its state in both themes, and its popover closes when the link opens", { skip: !edge, timeout: 60_000 }, async () => {
  const root = await mkdtemp(join(tmpdir(), "codealta-remote-control-"));
  let browser: ReturnType<typeof spawn> | undefined;
  let socket: WebSocket | undefined;
  try {
    await build({ entryPoints: [fileURLToPath(new URL("./remoteControl.mount.tsx", import.meta.url))], outfile: join(root, "fixture.js"), bundle: true, platform: "browser", format: "iife" });
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
    await command("Page.enable");
    await command("Page.navigate", { url: pathToFileURL(page).href });
    assert.equal(await until(`window.remoteControl && remoteControl.ask && document.querySelectorAll("[data-remote-control]").length === 5 && document.querySelector("[data-neighbour]")`), true);

    for (const theme of ["dark", "light"]) {
      await evaluate(`remoteControl.setTheme(${JSON.stringify(theme)})`);
      const shown = await evaluate(colors) as Record<string, string>;
      assert.equal(new Set([shown.green, shown.yellow, shown.red, shown.neighbour]).size, 4, `${theme}: ${JSON.stringify(shown)}`);
      assert.deepEqual([shown.off, shown.connecting, shown.connected, shown.failed], [shown.neighbour, shown.yellow, shown.green, shown.red], `${theme}: ${JSON.stringify(shown)}`);
    }

    // Opening the link leaves for the browser: the popover closes, so that its button does not keep the focus of the
    // window, which a permission request that appears meanwhile takes only from nowhere or from the composer.
    await evaluate(`document.querySelector('[data-remote-control="connected"]').click()`);
    assert.equal(await until(`!!document.querySelector("[data-remote-control-open]")`), true, "The popover opens");
    await evaluate(`document.querySelector("[data-remote-control-open]").focus();document.querySelector("[data-remote-control-open]").click()`);
    assert.equal(await until(`!document.querySelector("[data-remote-control-open]")`), true, "Opening the link closes the popover");
    assert.deepEqual(await evaluate("remoteControl.opened"), ["https://claude.ai/code/session_test"]);
    assert.equal(await evaluate(`!document.activeElement || document.activeElement === document.body || !!document.activeElement.closest(".ide-shell")`), true,
      "and the focus is not left in a popover");

    // The Actions menu asks to show it once: closed, it is not shown again when its button is mounted again.
    await evaluate("remoteControl.ask()");
    assert.equal(await until(`!!document.querySelector("[data-remote-control-on]")`), true, "The request of the menu opens the popover");
    await evaluate(`document.querySelector("[data-asked] [data-remote-control]").click()`);
    assert.equal(await until(`!document.querySelector("[data-remote-control-on]")`), true, "The popover closes");
    await evaluate("remoteControl.remount()");
    await new Promise(resolve => setTimeout(resolve, 400));
    assert.equal(await evaluate(`!document.querySelector("[data-remote-control-on]")`), true, "A request already shown does not open it again");
  } finally {
    // Edge's launcher can exit while the browser it started goes on: the browser itself is asked to close.
    if (socket?.readyState === WebSocket.OPEN) socket.send(JSON.stringify({ id: 9999, method: "Browser.close" }));
    socket?.close(); browser?.kill(); await rm(root, { recursive: true, force: true, maxRetries: 8, retryDelay: 100 });
  }
});
