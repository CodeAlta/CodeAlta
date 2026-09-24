import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { existsSync } from "node:fs";
import { mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { fileURLToPath, pathToFileURL } from "node:url";
import { build } from "esbuild";

const edge = ["C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe",
  "C:/Program Files/Microsoft/Edge/Application/msedge.exe"].find(existsSync);

// Headless Edge + test-owned profile/fixture when installed. This is a mounted browser layout test,
// not a native WebView2 acceptance test or a launch of the real desktop/catalog.
test("mounted timeline follows the final page across later layout growth", { skip: !edge, timeout: 25_000 }, async () => {
  const root = await mkdtemp(join(tmpdir(), "codealta-timeline-mounted-"));
  let browser: ReturnType<typeof spawn> | undefined;
  let socket: WebSocket | undefined;
  try {
    await build({ entryPoints: [fileURLToPath(new URL("./timelineScroll.mount.ts", import.meta.url))],
      outfile: join(root, "fixture.js"), bundle: true, platform: "browser", format: "iife" });
    const page = join(root, "fixture.html");
    await writeFile(page, '<!doctype html><html><body><div id="app"></div><script src="fixture.js"></script></body></html>');
    const profile = join(root, "profile");
    browser = spawn(edge!, ["--headless=new", "--disable-gpu", "--no-first-run", "--disable-background-networking", "--disable-extensions",
      `--user-data-dir=${profile}`, "--remote-debugging-port=0", "about:blank"], { stdio: "ignore", windowsHide: true });
    let port = "";
    for (let attempt = 0; attempt < 100 && !port; attempt++) {
      try { port = (await readFile(join(profile, "DevToolsActivePort"), "utf8")).split(/\r?\n/)[0]; }
      catch { await new Promise(resolve => setTimeout(resolve, 50)); }
    }
    assert.match(port, /^\d+$/, "test-owned headless Edge did not start");
    const pages: { type?: string; url?: string; webSocketDebuggerUrl?: string }[] =
      await (await fetch(`http://127.0.0.1:${port}/json/list`, { signal: AbortSignal.timeout(10_000) })).json();
    const tab = pages.find(value => value.type === "page" && value.url === "about:blank");
    assert.ok(tab?.webSocketDebuggerUrl, "test-owned browser tab unavailable");
    socket = new WebSocket(tab.webSocketDebuggerUrl);
    await new Promise<void>((resolve, reject) => {
      socket!.addEventListener("open", () => resolve(), { once: true });
      socket!.addEventListener("error", () => reject(new Error("test browser debugger unavailable")), { once: true });
    });
    let sequence = 0;
    const command = (method: string, params: object = {}) => new Promise<{ result?: { value?: string } }>((resolve, reject) => {
      const id = ++sequence;
      const timer = setTimeout(() => reject(new Error(`test browser ${method} timed out`)), 12_000);
      const reply = (event: MessageEvent) => {
        const message = JSON.parse(String(event.data)) as { id?: number; result?: { result?: { value?: string } }; error?: object };
        if (message.id !== id) return;
        socket!.removeEventListener("message", reply);
        clearTimeout(timer);
        if (message.error) reject(new Error(`test browser ${method} failed`));
        else resolve(message.result ?? {});
      };
      socket!.addEventListener("message", reply);
      socket!.send(JSON.stringify({ id, method, params }));
    });
    await command("Page.enable");
    const loaded = new Promise<void>((resolve, reject) => {
      const timer = setTimeout(() => { socket!.removeEventListener("message", listener); reject(new Error("test page did not load")); }, 10_000);
      const listener = (event: MessageEvent) => {
        if ((JSON.parse(String(event.data)) as { method?: string }).method !== "Page.loadEventFired") return;
        socket!.removeEventListener("message", listener);
        clearTimeout(timer);
        resolve();
      };
      socket!.addEventListener("message", listener);
    });
    const navigation = await command("Page.navigate", { url: pathToFileURL(page).href });
    await loaded;
    const response = await command("Runtime.evaluate", { awaitPromise: true, returnByValue: true, expression: `new Promise(resolve => {
      const check = () => { const text = document.getElementById('result')?.textContent;
        if (text && text !== 'waiting') { observer.disconnect(); resolve(text); } };
      const observer = new MutationObserver(check);
      observer.observe(document.body, { childList: true, subtree: true, characterData: true });
      check();
      setTimeout(() => { observer.disconnect(); resolve('timeout'); }, 6000);
    })` });
    const raw = response.result?.value;
    if (!raw || raw === "timeout") {
      const state = await command("Runtime.evaluate", { returnByValue: true,
        expression: "JSON.stringify({ url: document.URL, body: document.body?.innerHTML.slice(0,500) })" });
      assert.fail(`React timeline fixture did not settle: ${JSON.stringify(navigation)} ${state.result?.value ?? "no DOM"}`);
    }
    const result = JSON.parse(raw) as { atLatest: { initial: number; top: number; target: number; following: boolean };
      readingTop: number; readingFollow: boolean; finalTarget: number };
    assert.ok(result.atLatest.initial >= 799, `initial final-page restore did not reach bottom: ${raw}`);
    assert.ok(result.atLatest.target >= 1999, `test-owned layout did not grow: ${raw}`);
    assert.equal(result.atLatest.following, true);
    assert.ok(Math.abs(result.atLatest.top - result.atLatest.target) <= 1, `timeline lost the latest conversation: ${raw}`);
    assert.ok(result.finalTarget >= 2499, `older content did not grow the timeline: ${raw}`);
    assert.equal(result.readingFollow, false);
    assert.equal(result.readingTop, 420, `user reading position was lost after further growth: ${raw}`);
  } finally {
    if (socket?.readyState === WebSocket.OPEN) socket.send(JSON.stringify({ id: 999, method: "Browser.close" }));
    socket?.close();
    if (browser?.pid) {
      const killer = spawn("taskkill", ["/F", "/T", "/PID", String(browser.pid)], { stdio: "ignore", windowsHide: true });
      await new Promise(resolve => killer.once("exit", resolve));
    }
    await rm(root, { recursive: true, force: true, maxRetries: 20, retryDelay: 100 });
  }
});
