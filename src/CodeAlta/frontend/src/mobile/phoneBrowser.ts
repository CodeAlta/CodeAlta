import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { mkdir, mkdtemp, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { fileURLToPath } from "node:url";
import { build } from "esbuild";
import { browserBaseArgs, browserExecutable, endBrowser, waitForBrowserPort } from "../browserTarget";
import { pageFiles, startFixtureServer } from "./fixtureServer";

/** The fixture server of a phone test. */
export type PhoneFixture = Awaited<ReturnType<typeof startFixtureServer>>;

/**
 * The phone page in headless Edge, at 390 × 844 CSS pixels, against the fixture server: the page is bundled from the
 * sources, the browser is driven over its DevTools protocol, and every error or policy violation of the page is kept.
 * `shots` is a folder the pictures of `shot` go to; none takes no picture.
 */
export async function openPhone(prepare: (server: PhoneFixture) => void, shots: string) {
  const directory = await mkdtemp(join(tmpdir(), "codealta-phone-"));
  let browser: ReturnType<typeof spawn> | undefined, socket: WebSocket | undefined;
  const bundle = await build({ entryPoints: [fileURLToPath(new URL("./main.ts", import.meta.url))], bundle: true, format: "esm", platform: "browser",
    write: false, outdir: join(directory, "out"), external: ["mermaid"], define: { "process.env.NODE_ENV": '"production"' }, logLevel: "silent" });
  const script = bundle.outputFiles.find(file => file.path.endsWith(".js"))!.text, style = bundle.outputFiles.find(file => file.path.endsWith(".css"))!.text;
  const server = await startFixtureServer(await pageFiles(script, style));
  prepare(server);
  const failures: string[] = [];
  const close = async () => {
    try { socket?.close(); } catch { /* already closed */ }
    await endBrowser(browser);
    await server.close();
    await rm(directory, { recursive: true, force: true, maxRetries: 5, retryDelay: 200 }).catch(() => { });
  };
  try {
    browser = spawn(browserExecutable!, [...browserBaseArgs, `--user-data-dir=${join(directory, "profile")}`, "--remote-debugging-port=0", "about:blank"],
      { stdio: ["ignore", "ignore", "pipe"], windowsHide: true });
    const port = await waitForBrowserPort(join(directory, "profile"), browser);
    const pages = await (await fetch(`http://127.0.0.1:${port}/json/list`, { signal: AbortSignal.timeout(5000) })).json() as { type: string; url: string; webSocketDebuggerUrl: string }[];
    socket = new WebSocket(pages.find(p => p.type === "page")!.webSocketDebuggerUrl);
    await new Promise<void>((resolve, reject) => { socket!.addEventListener("open", () => resolve(), { once: true }); socket!.addEventListener("error", reject, { once: true }); });
  } catch (error) {
    await close();
    throw error;
  }
  let sequence = 0;
  type Reply = { result?: { value?: unknown }; exceptionDetails?: unknown };
  const pending = new Map<number, (reply: { result?: Reply; error?: unknown }) => void>();
  socket.addEventListener("message", message => {
    const m = JSON.parse(String(message.data));
    if (pending.has(m.id)) { pending.get(m.id)!(m); pending.delete(m.id); }
    if (m.method === "Runtime.exceptionThrown") failures.push(JSON.stringify(m.params.exceptionDetails));
    if (m.method === "Log.entryAdded" && (m.params.entry.source === "security" || m.params.entry.level === "error")
      && !/status of 40[14]|favicon/.test(m.params.entry.text)) failures.push(`${m.params.entry.source}: ${m.params.entry.text}`);
  });
  const command = (method: string, params: object = {}) => new Promise<Reply>((resolve, reject) => {
    const id = ++sequence;
    const timer = setTimeout(() => { pending.delete(id); reject(new Error(`${method} timeout`)); }, 10_000);
    pending.set(id, reply => { clearTimeout(timer); reply.error ? reject(new Error(JSON.stringify(reply.error))) : resolve(reply.result ?? {}); });
    socket!.send(JSON.stringify({ id, method, params }));
  });
  const evaluate = async <T,>(expression: string): Promise<T> => {
    const reply = await command("Runtime.evaluate", { expression, returnByValue: true, awaitPromise: true });
    assert.equal(reply.exceptionDetails, undefined, JSON.stringify(reply.exceptionDetails));
    return reply.result?.value as T;
  };
  const waitFor = async (expression: string, what: string, timeout = 8000) => {
    const deadline = Date.now() + timeout;
    while (Date.now() < deadline) {
      if (await evaluate<boolean>(`!!(${expression})`)) return;
      await new Promise(resolve => setTimeout(resolve, 50));
    }
    assert.fail(`${what}: not seen within ${timeout} ms (${expression})`);
  };
  await command("Runtime.enable"); await command("Log.enable"); await command("Page.enable");
  await command("Emulation.setDeviceMetricsOverride", { width: 390, height: 844, deviceScaleFactor: shots ? 3 : 2, mobile: true });
  return {
    server, failures, command, evaluate, waitFor, close,
    click: (selector: string) => evaluate(`document.querySelector(${JSON.stringify(selector)}).click()`),
    texts: (selector: string) => evaluate<string[]>(`[...document.querySelectorAll(${JSON.stringify(selector)})].map(e => e.textContent)`),
    /** The ids of the rows of the list, top to bottom. */
    rows: () => evaluate<string[]>("[...document.querySelectorAll('#sessions .row')].map(r => r.dataset.id)"),
    /** Opens the page signed in. */
    async signIn() {
      await command("Page.navigate", { url: `${server.origin}/#k=${server.state.token}` });
      await waitFor(`document.querySelectorAll("#sessions .row").length > 0`, "the list of sessions");
      if (await evaluate<boolean>("!document.getElementById('install-bar')?.hidden")) await evaluate("document.getElementById('install-later')?.click()");
    },
    async shot(name: string) {
      if (!shots) return;
      await mkdir(shots, { recursive: true });
      await writeFile(join(shots, name), Buffer.from(((await command("Page.captureScreenshot", { format: "png" })) as { data: string }).data, "base64"));
    },
  };
}
