import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { existsSync, readFileSync } from "node:fs";
import { mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { fileURLToPath, pathToFileURL } from "node:url";
import { build } from "esbuild";

const edge = ["C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe",
  "C:/Program Files/Microsoft/Edge/Application/msedge.exe"].find(existsSync);

// This mounts the production History component and scroll hook against an isolated v2 journal
// fixture. It is not native WebView2 acceptance and never opens a real catalog or user profile.
test("mounted reverse history retains latest, anchors older pages and fences switched/stale reads", { skip: !edge, timeout: 45_000 }, async () => {
  const source = readFileSync(fileURLToPath(new URL("./main.tsx", import.meta.url)), "utf8");
  assert.match(source, /read=\{workspace\.historyTail\}/, "production must wire the v2 route");
  const root = await mkdtemp(join(tmpdir(), "codealta-history-mounted-"));
  let browser: ReturnType<typeof spawn> | undefined;
  let socket: WebSocket | undefined;
  try {
    await build({ entryPoints: [fileURLToPath(new URL("./historyPanel.mount.tsx", import.meta.url))],
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
    assert.match(port, /^\d+$/);
    const pages: { type?: string; url?: string; webSocketDebuggerUrl?: string }[] =
      await (await fetch(`http://127.0.0.1:${port}/json/list`, { signal: AbortSignal.timeout(10_000) })).json();
    const tab = pages.find(value => value.type === "page" && value.url === "about:blank");
    assert.ok(tab?.webSocketDebuggerUrl);
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
      const timer = setTimeout(() => reject(new Error("test page did not load")), 10_000);
      const listener = (event: MessageEvent) => {
        if ((JSON.parse(String(event.data)) as { method?: string }).method !== "Page.loadEventFired") return;
        clearTimeout(timer); socket!.removeEventListener("message", listener); resolve();
      };
      socket!.addEventListener("message", listener);
    });
    await command("Page.navigate", { url: pathToFileURL(page).href });
    await loaded;
    const evaluate = async (expression: string) => (await command("Runtime.evaluate", { expression, returnByValue: true,
      awaitPromise: true })).result?.value;
    const wait = async (predicate: string) => {
      const result = await evaluate(`new Promise(resolve => { const end = Date.now() + 9000; const check = () => {
        if (${predicate}) resolve('ready'); else if (Date.now() > end) resolve('timeout'); else setTimeout(check, 35); }; check(); })`);
      if (result !== "ready") assert.fail(`mounted history did not reach ${predicate}: ${await evaluate("document.body.innerText.slice(0, 360)")}`);
    };
    const snapshot = () => evaluate(`JSON.stringify({ rows: document.querySelectorAll('.timeline-message').length,
      first: document.querySelector('.timeline-message')?.textContent, last: [...document.querySelectorAll('.timeline-message')].at(-1)?.textContent,
      top: document.querySelector('.timeline-scroll')?.scrollTop, height: document.querySelector('.timeline-scroll')?.scrollHeight,
      following: document.querySelector('.timeline-scroll')?.dataset.following, calls: window.fixture.calls })`);
    await wait("document.querySelectorAll('.timeline-message').length === 1000 && document.body.innerText.includes('latest user prompt')");
    let result = JSON.parse((await snapshot())!) as { rows: number; first: string; last: string; top: number; height: number; following: string; calls: string[] };
    assert.match(result.first, /turn-205/);
    assert.match(result.last, /latest user prompt/);
    assert.equal(result.following, "true");
    assert.ok(result.calls.length <= 11 && result.calls.every(call => call.startsWith("A:tail") || call.startsWith("A:2:")));
    await evaluate(`(() => { const scroller = document.querySelector('.timeline-scroll'); scroller.scrollTop = 2000;
      scroller.dispatchEvent(new Event('scroll', { bubbles: true })); const viewport = scroller.getBoundingClientRect();
      const anchor = [...document.querySelectorAll('.timeline-message')].find(row => row.getBoundingClientRect().bottom > viewport.top);
      window.fixture.anchor = anchor; window.fixture.anchorTop = anchor.getBoundingClientRect().top;
      document.querySelector('.load-more').click(); return true; })()`);
    await wait("document.body.innerText.includes('Newer journal events are no longer')");
    result = JSON.parse((await snapshot())!);
    assert.equal(result.rows, 1000);
    assert.doesNotMatch(result.last, /latest user prompt/);
    assert.match(result.first, /turn-105/);
    assert.equal(result.following, "false");
    const anchored = JSON.parse((await evaluate(`JSON.stringify({ connected: window.fixture.anchor.isConnected,
      delta: window.fixture.anchor.getBoundingClientRect().top - window.fixture.anchorTop })`))!) as { connected: boolean; delta: number };
    assert.equal(anchored.connected, true);
    assert.ok(Math.abs(anchored.delta) < 2, `older-page prepend moved the reading anchor: ${JSON.stringify(anchored)}`);
    await evaluate("window.fixture.select('B')");
    await wait("document.querySelectorAll('.timeline-message').length === 3 && document.body.innerText.includes('turn-2')");
    await evaluate("window.fixture.select('A')");
    await wait("document.querySelectorAll('.timeline-message').length === 1000 && document.body.innerText.includes('latest user prompt')");
    result = JSON.parse((await snapshot())!);
    assert.equal(result.following, "false", "returning reader must retain user-unfollow preference");
    assert.ok(result.top > 2000, "returning session should restore its reading position after loading");
    await evaluate("window.fixture.holdNext(); document.querySelector('.load-more').click()");
    await wait("window.fixture.calls.at(-1).startsWith('A:2:')");
    await evaluate("window.fixture.select('B'); window.fixture.release()");
    await wait("document.querySelectorAll('.timeline-message').length === 3 && document.body.innerText.includes('turn-2')");
    assert.doesNotMatch((await snapshot())!, /latest user prompt/);
    await evaluate("window.fixture.select('C')");
    await wait("document.querySelectorAll('.timeline-message').length === 100 && document.body.innerText.includes('turn-99')");
    result = JSON.parse((await snapshot())!);
    assert.ok(result.calls.some(call => call === "C:2:20000"), "empty metadata-only page must continue");
    await evaluate("window.fixture.select('A')");
    await wait("document.querySelectorAll('.timeline-message').length === 1000 && document.body.innerText.includes('latest user prompt')");
    await evaluate("window.fixture.failNext('history_changed'); document.querySelector('.load-more').click()");
    await wait("document.querySelector('[role=alert]')?.textContent.includes('journal changed')");
    assert.equal(JSON.parse((await snapshot())!).rows, 0, "stale revision must clear accumulated history");
    await evaluate("[...document.querySelectorAll('button')].find(b => b.textContent.includes('Refresh newest')).click()");
    await wait("document.querySelectorAll('.timeline-message').length === 1000 && document.body.innerText.includes('latest user prompt')");
    await evaluate("window.fixture.failNext('read_failed'); document.querySelector('.load-more').click()");
    await wait("document.querySelector('[role=alert]')?.textContent.includes('could not be read')");
    assert.equal(JSON.parse((await snapshot())!).rows, 1000, "non-revision older failure retains known recent history");
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
