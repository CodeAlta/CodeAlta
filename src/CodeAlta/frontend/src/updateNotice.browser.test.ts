import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { fileURLToPath, pathToFileURL } from "node:url";
import { build } from "esbuild";
import { browserBaseArgs, browserExecutable } from "./browserTarget";

test("the update notice survives ordinary toast traffic, replaces old versions, and respects dismissal", { skip: !browserExecutable, timeout: 60_000 }, async () => {
  const root = await mkdtemp(join(tmpdir(), "codealta-update-notice-"));
  let browser: ReturnType<typeof spawn> | undefined;
  let socket: WebSocket | undefined;
  try {
    await build({ entryPoints: [fileURLToPath(new URL("./updateNotice.mount.tsx", import.meta.url))], outfile: join(root, "fixture.js"), bundle: true, platform: "browser", format: "iife" });
    await writeFile(join(root, "style.css"), await readFile(new URL("../node_modules/@blueprintjs/core/lib/css/blueprint.css", import.meta.url), "utf8")
      + await readFile(new URL("./style.css", import.meta.url), "utf8"));
    const page = join(root, "fixture.html");
    await writeFile(page, '<!doctype html><html><head><meta charset="utf-8"><link rel="stylesheet" href="style.css"></head><body><script src="fixture.js"></script></body></html>');
    browser = spawn(browserExecutable!, [...browserBaseArgs, `--user-data-dir=${join(root, "profile")}`, "--remote-debugging-port=0", "about:blank"], { stdio: "ignore", windowsHide: true });
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
    assert.equal(await until("!!window.updateNoticePreview"), true);
    assert.equal(await evaluate('updateNoticePreview.show("99.0.0", true, "en", false)'), false, "hidden windows defer the notice");
    assert.equal(await evaluate('updateNoticePreview.show("99.0.0")'), true);
    assert.equal(await until('!!document.querySelector(".update-notice")'), true);
    await evaluate("updateNoticePreview.ordinaryNotices()");
    assert.equal(await until('document.querySelectorAll("[data-ordinary-notice]").length === 4'), true);
    assert.equal(await until('!document.querySelector(".bp6-toast-exit")'), true, "wait for any queue eviction animation");
    assert.equal(await evaluate('!!document.querySelector(".update-notice")'), true, "ordinary toast traffic must not evict the update");
    assert.equal(await evaluate('updateNoticePreview.show("99.0.0")'), false, "the same version is not announced twice");
    assert.equal(await evaluate('updateNoticePreview.show("99.0.1")'), true);
    assert.equal(await until('document.querySelector(".update-notice")?.textContent.includes("99.0.1")'), true);
    assert.equal(await evaluate('document.querySelectorAll(".update-notice").length'), 1, "the newer version replaces the older notice");
    await evaluate('document.querySelector(".update-notice-actions button").click(); document.querySelector(".update-notice-notes").click()');
    assert.deepEqual(await evaluate("updateNoticePreview.actions"), ["install (preview only)", "release-notes (preview only)"]);
    await evaluate('document.querySelector(".update-notice").closest(".bp6-toast").querySelector("button[aria-label=Close]").click()');
    assert.equal(await until('!document.querySelector(".update-notice")'), true);
    assert.equal(await evaluate('updateNoticePreview.show("99.0.1")'), false, "dismissal is respected");
    await evaluate("updateNoticePreview.dispose()");
    assert.equal(await until('document.querySelectorAll(".bp6-toast").length === 0 && !window.updateNoticePreview'), true, "unmount cleans up both test toasters");
  } finally {
    if (socket?.readyState === WebSocket.OPEN) socket.send(JSON.stringify({ id: 9999, method: "Browser.close" }));
    socket?.close(); browser?.kill(); await rm(root, { recursive: true, force: true, maxRetries: 8, retryDelay: 100 });
  }
});
