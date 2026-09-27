import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { existsSync, readFileSync } from "node:fs";
import { mkdtemp, readFile, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { fileURLToPath, pathToFileURL } from "node:url";
import { build } from "esbuild";

const edge = ["C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe", "C:/Program Files/Microsoft/Edge/Application/msedge.exe"].find(existsSync);
test("user and assistant Markdown is always visible, sanitized and width-bounded", { skip: !edge, timeout: 60_000 }, async t => {
  const root = await mkdtemp(join(tmpdir(), "codealta-message-content-"));
  t.diagnostic(`Mounted message evidence: ${root}`);
  let browser: ReturnType<typeof spawn> | undefined, socket: WebSocket | undefined;
  try {
    await build({ entryPoints: [fileURLToPath(new URL("./longTimelineBodies.mount.tsx", import.meta.url))], outfile: join(root, "fixture.js"), bundle: true, platform: "browser", format: "iife" });
    await writeFile(join(root, "style.css"), readFileSync(new URL("./style.css", import.meta.url)));
    await writeFile(join(root, "fixture.html"), '<!doctype html><html><head><link rel="stylesheet" href="style.css"></head><body><div class="ide-shell"><div id="app" class="messages"></div></div><script src="fixture.js"></script></body></html>');
    const profile = join(root, "profile");
    browser = spawn(edge!, ["--headless=new", "--disable-gpu", "--no-first-run", "--disable-background-networking", "--disable-extensions", `--user-data-dir=${profile}`, "--remote-debugging-port=0", "about:blank"], { stdio: "ignore", windowsHide: true });
    let port = "";
    for (let attempt = 0; attempt < 100 && !port; attempt++) {
      try { port = (await readFile(join(profile, "DevToolsActivePort"), "utf8")).split(/\r?\n/)[0]; }
      catch { await new Promise(resolve => setTimeout(resolve, 50)); }
    }
    assert.match(port, /^\d+$/);
    const pages: { type?: string; webSocketDebuggerUrl?: string }[] = await (await fetch(`http://127.0.0.1:${port}/json/list`, { signal: AbortSignal.timeout(10_000) })).json();
    socket = new WebSocket(pages.find(page => page.type === "page")!.webSocketDebuggerUrl!);
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
      assert.equal(result.exceptionDetails, undefined, JSON.stringify(result.exceptionDetails)); return result.result?.value;
    };
    await command("Page.enable");
    await command("Page.navigate", { url: pathToFileURL(join(root, "fixture.html")).href });
    assert.equal(await evaluate("new Promise(resolve=>{const end=Date.now()+7000;function check(){if(window.bodyFixture)resolve(true);else if(Date.now()>end)resolve(false);else setTimeout(check,20)}check()})"), true);
    for (const width of [390, 1280]) for (const theme of ["light", "dark"]) for (const kind of ["User", "Assistant"]) {
      await command("Emulation.setDeviceMetricsOverride", { width, height: 720, deviceScaleFactor: 1, mobile: false });
      await evaluate(`document.documentElement.dataset.theme='${theme}';bodyFixture.replace({kind:'${kind}'});new Promise(r=>requestAnimationFrame(()=>requestAnimationFrame(r)))`);
      assert.equal(await evaluate("!document.querySelector('.long-message-toggle, .long-message-preview')"), true, `${kind} must not require expansion`);
      assert.match(String(await evaluate("document.querySelector('.message-body .markdown-content').textContent.slice(-100)")), / END\s*$/, "Supplied tail is rendered (Markdown may append a paragraph newline)");
      assert.equal(await evaluate("document.querySelector('.markdown-content strong').textContent"), "supplied");
      assert.equal(await evaluate("!document.querySelector('article img, article script') && !window.injected"), true);
      assert.equal(await evaluate(`document.documentElement.scrollWidth<=${width}`), true, `${width}/${theme}/${kind}`);
      await evaluate("document.querySelector('.copy-markdown').click();new Promise(r=>setTimeout(r,0))");
      assert.equal(await evaluate("bodyFixture.copies.at(-1)===bodyFixture.expectedCopy()"), true);
    }
    await evaluate("bodyFixture.replace({kind:'Assistant',text:'New **content**'});bodyFixture.scope()");
    assert.equal(await evaluate("document.querySelector('.markdown-content').textContent.trim()"), "New content");
    assert.equal(await evaluate("!document.querySelector('.long-message-toggle')"), true);
  } finally { socket?.close(); browser?.kill(); }
});
