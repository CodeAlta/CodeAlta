import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { mkdtemp, rm, mkdir, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";
import { build } from "esbuild";
import { browserBaseArgs, browserExecutable, endBrowser, waitForBrowserPort } from "../browserTarget";
import { pageFiles, session, startFixtureServer } from "./fixtureServer";

test("the phone shows background jobs after the turn ends, follows output and stops a job", { skip: !browserExecutable, timeout: 90_000 }, async () => {
  const directory = await mkdtemp(join(tmpdir(), "codealta-mobile-jobs-"));
  let browser: ReturnType<typeof spawn> | undefined, socket: WebSocket | undefined;
  const bundle = await build({ entryPoints: [fileURLToPath(new URL("./main.ts", import.meta.url))], bundle: true, format: "esm", platform: "browser",
    write: false, outdir: join(directory, "out"), external: ["pele/lazy"], loader: { ".woff2": "file", ".woff": "file", ".ttf": "file" }, define: { "process.env.NODE_ENV": '"production"' }, logLevel: "silent" });
  const script = bundle.outputFiles.find(file => file.path.endsWith(".js"))!.text, style = bundle.outputFiles.find(file => file.path.endsWith(".css"))!.text;
  const server = await startFixtureServer(await pageFiles(script, style));
  const startedAt = new Date(Date.now() - 26_000).toISOString();
  server.state.sessions = [session("s1", "Measure production quality", "p1", 0), session("s2", "Another session", "p1", 1)];
  server.state.jobs.set("s1", [
    { id: "job-live", title: "Production Gateway judgment quality v2", command: "npm run eval:tiers -- --suite sufficiency", folder: "C:/code/parser", state: "running", startedAt },
    { id: "job-ended", title: "Preflight tests", command: "npm test", folder: "C:/code/parser", state: "succeeded", startedAt, endedAt: new Date().toISOString(), exitCode: 0 },
  ]);
  server.state.jobOutput.set("job-live", "Starting seven controlled cases…\n<script>window.injected = true</script>");
  const failures: string[] = [];
  try {
    browser = spawn(browserExecutable!, [...browserBaseArgs, `--user-data-dir=${join(directory, "profile")}`, "--remote-debugging-port=0", "about:blank"], { stdio: ["ignore", "ignore", "pipe"], windowsHide: true });
    const port = await waitForBrowserPort(join(directory, "profile"), browser);
    const pages = await (await fetch(`http://127.0.0.1:${port}/json/list`, { signal: AbortSignal.timeout(5000) })).json() as { type: string; webSocketDebuggerUrl: string }[];
    socket = new WebSocket(pages.find(page => page.type === "page")!.webSocketDebuggerUrl);
    await new Promise<void>((resolve, reject) => { socket!.addEventListener("open", () => resolve(), { once: true }); socket!.addEventListener("error", reject, { once: true }); });
    let seq = 0;
    type Reply = { result?: { value?: unknown }; exceptionDetails?: unknown; data?: string };
    const pending = new Map<number, (reply: { result?: Reply; error?: unknown }) => void>();
    socket.addEventListener("message", event => {
      const message = JSON.parse(String(event.data));
      if (pending.has(message.id)) { pending.get(message.id)!(message); pending.delete(message.id); }
      if (message.method === "Runtime.exceptionThrown") failures.push(JSON.stringify(message.params.exceptionDetails));
    });
    const command = (method: string, params: object = {}) => new Promise<Reply>((resolve, reject) => {
      const id = ++seq, timer = setTimeout(() => { pending.delete(id); reject(new Error(`${method} timeout`)); }, 10_000);
      pending.set(id, reply => { clearTimeout(timer); reply.error ? reject(new Error(JSON.stringify(reply.error))) : resolve(reply.result ?? {}); });
      socket!.send(JSON.stringify({ id, method, params }));
    });
    const evaluate = async <T,>(expression: string): Promise<T> => {
      const reply = await command("Runtime.evaluate", { expression, returnByValue: true, awaitPromise: true });
      assert.equal(reply.exceptionDetails, undefined, JSON.stringify(reply.exceptionDetails)); return reply.result?.value as T;
    };
    const waitFor = async (expression: string) => {
      const deadline = Date.now() + 10_000;
      while (Date.now() < deadline) { if (await evaluate<boolean>(`!!(${expression})`)) return; await new Promise(resolve => setTimeout(resolve, 50)); }
      assert.fail(`Not seen: ${expression}`);
    };
    const click = (selector: string) => evaluate(`document.querySelector(${JSON.stringify(selector)}).click()`);
    const screenshot = async (name: string) => {
      if (!process.env.CODEALTA_SHOTS) return;
      await mkdir(process.env.CODEALTA_SHOTS, { recursive: true });
      await writeFile(join(process.env.CODEALTA_SHOTS, name), Buffer.from((await command("Page.captureScreenshot", { format: "png" })).data!, "base64"));
    };
    await command("Runtime.enable"); await command("Page.enable");
    await command("Emulation.setDeviceMetricsOverride", { width: 390, height: 844, deviceScaleFactor: 2, mobile: true });
    await command("Page.navigate", { url: `${server.origin}/#k=${server.state.token}` });
    await waitFor('document.querySelector(".row[data-id=s1]")');
    await click(".row[data-id=s1] .card-link");
    await waitFor('document.querySelector(".background-status").textContent.includes("1 in the background")');
    assert.equal(await evaluate('document.querySelector("#abort").hidden'), true, "the agent turn ended, but the job is still shown");
    assert.equal(await evaluate('document.querySelector("#send").textContent.trim()'), "Send");
    await screenshot("mobile-background-indicator-dark.png");
    await click(".background-status");
    await waitFor('document.querySelectorAll(".background-list section").length === 2');
    assert.deepEqual(await evaluate('[...document.querySelectorAll(".background-list h3")].map(e => e.textContent)'), ["Running", "Recent"]);
    await screenshot("mobile-background-list-dark.png");
    await click(".background-open");
    await waitFor('document.querySelector("pre[aria-label=\'Job output\']").textContent.includes("Starting seven")');
    assert.equal(await evaluate("window.injected"), undefined, "output is plain text");
    const selected = '.background-output pre[aria-label="Job output"]';
    server.state.jobOutput.set("job-live", "Finished case 1.\nWaiting for case 2.");
    await waitFor(`document.querySelector(${JSON.stringify(selected)}).textContent.includes("Finished case 1")`);
    for (const theme of ["dark", "light"]) {
      await evaluate(`document.documentElement.dataset.theme = ${JSON.stringify(theme)}`);
      assert.equal(await evaluate("document.documentElement.scrollWidth > innerWidth"), false, `no page overflow in ${theme}`);
      await screenshot(`mobile-background-output-${theme}.png`);
    }
    await click(".background-output .ghost");
    await waitFor('document.querySelector(".background-output .ghost").hidden');
    await waitFor('document.querySelector(".background-status").textContent.includes("2 ended")');
    assert.equal(server.requests.filter(r => r.path.endsWith("/job-live/stop")).length, 1);
    assert.equal(server.requests.filter(r => r.path.endsWith("/abort")).length, 0, "the stop targets the job");
    await command("Input.dispatchKeyEvent", { type: "keyDown", key: "Escape", code: "Escape", windowsVirtualKeyCode: 27 });
    await command("Input.dispatchKeyEvent", { type: "keyUp", key: "Escape", code: "Escape", windowsVirtualKeyCode: 27 });
    await waitFor('document.activeElement.classList.contains("background-status")');
    // A list response from the old session may arrive after navigating to another one.
    await evaluate(`(() => {
      const original = window.fetch;
      window.fetch = async (...args) => {
        const response = await original(...args);
        if (String(args[0]).endsWith('/sessions/s1/jobs')) {
          window.jobReadHeld = true;
          await new Promise(resolve => { window.releaseJobRead = resolve; });
        }
        return response;
      };
    })()`);
    await click("#refresh");
    await waitFor("window.jobReadHeld");
    await evaluate('location.hash = "#/session/s2"');
    await waitFor('document.querySelector("#detail-title").textContent === "Another session"');
    await evaluate("window.releaseJobRead()");
    await waitFor('!document.querySelector("#refresh").classList.contains("spin")');
    assert.equal(await evaluate('document.querySelector("#background-status").hidden'), true, "no jobs of the previous session");
    await evaluate('location.hash = "#/"');
    await waitFor('!document.querySelector("#home").hidden');
    const reads = server.requests.filter(r => r.path.endsWith("/jobs")).length;
    await new Promise(resolve => setTimeout(resolve, 3200));
    assert.equal(server.requests.filter(r => r.path.endsWith("/jobs")).length, reads, "the list does not poll jobs");
    assert.deepEqual(failures, []);
  } finally {
    try { socket?.close(); } catch { /* already closed */ }
    await endBrowser(browser); await server.close();
    await rm(directory, { recursive: true, force: true, maxRetries: 5, retryDelay: 200 }).catch(() => {});
  }
});
