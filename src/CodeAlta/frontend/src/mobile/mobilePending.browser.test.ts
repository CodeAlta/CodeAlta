import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { mkdtemp, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";
import { build } from "esbuild";
import { browserBaseArgs, browserExecutable, endBrowser, waitForBrowserPort } from "../browserTarget";
import type { TimelineEvent } from "./contract";
import { pageFiles, session, startFixtureServer } from "./fixtureServer";

// Where the pictures of this test go, when a person wants to look at them.
const shots = process.env.CODEALTA_SHOTS ?? "";

test("the phone takes back a prompt that waits, and opens a tool call on what it does", { skip: !browserExecutable, timeout: 90_000 }, async () => {
  const directory = await mkdtemp(join(tmpdir(), "codealta-mobile-pending-"));
  let browser: ReturnType<typeof spawn> | undefined, socket: WebSocket | undefined;
  const bundle = await build({ entryPoints: [fileURLToPath(new URL("./main.ts", import.meta.url))], bundle: true, format: "esm", platform: "browser",
    write: false, outdir: join(directory, "out"), external: ["pele/lazy"], loader: { ".woff2": "file", ".woff": "file", ".ttf": "file" }, define: { "process.env.NODE_ENV": '"production"' }, logLevel: "silent" });
  const script = bundle.outputFiles.find(file => file.path.endsWith(".js"))!.text, style = bundle.outputFiles.find(file => file.path.endsWith(".css"))!.text;
  const server = await startFixtureServer(await pageFiles(script, style));
  const now = new Date().toISOString();
  let seq = 0;
  const event = (kind: string, text: string, extra: Partial<TimelineEvent> = {}): TimelineEvent => ({ seq: ++seq, at: now, kind, text, activityId: null, ...extra });
  const running = { id: "s1", projectId: "p1", live: "running", kind: "activity", line: null, at: now, activity: null, canSteer: true } as const;
  server.state.sessions = [session("s1", "A running session", "p1", 0, { live: "running", kind: "activity", line: null, canSteer: true })];
  server.state.events.set("s1", [
    event("user.message", "Build it"),
    event("activity.toolcall.started", "Bash", { activityId: "call-1", subject: "dotnet build -c Release" }),
    event("activity.toolcall.started", "Grep", { activityId: "call-2" }),
    event("activity.toolcall.completed", "Grep", { activityId: "call-2" }),
  ]);
  const longLine = "an output line that is much wider than the screen of a phone, ".repeat(6);
  server.state.tools.set("s1/call-1", {
    name: "Bash", phase: "started", command: "dotnet build -c Release", sessionFolder: "C:\\work\\worktrees\\calm-otter", output: `${longLine}\nbuilding`, outputLength: 40_000, error: null,
  });
  // A prompt queued before this page opened, as after a reload of the phone.
  server.state.queues.set("s1", [{ id: "queue-old", text: "Queued before the reload", at: now, state: "queued" }]);
  const failures: string[] = [];
  try {
    browser = spawn(browserExecutable!, [...browserBaseArgs, `--user-data-dir=${join(directory, "profile")}`, "--remote-debugging-port=0", "about:blank"], { stdio: ["ignore", "ignore", "pipe"], windowsHide: true });
    const port = await waitForBrowserPort(join(directory, "profile"), browser);
    const pages = await (await fetch(`http://127.0.0.1:${port}/json/list`, { signal: AbortSignal.timeout(5000) })).json() as { type: string; webSocketDebuggerUrl: string }[];
    socket = new WebSocket(pages.find(page => page.type === "page")!.webSocketDebuggerUrl);
    await new Promise<void>((resolve, reject) => { socket!.addEventListener("open", () => resolve(), { once: true }); socket!.addEventListener("error", reject, { once: true }); });
    let sequence = 0;
    type Reply = { result?: { value?: unknown }; exceptionDetails?: unknown };
    const pending = new Map<number, (reply: { result?: Reply; error?: unknown }) => void>();
    socket.addEventListener("message", message => {
      const data = JSON.parse(String(message.data));
      if (pending.has(data.id)) { pending.get(data.id)!(data); pending.delete(data.id); }
      if (data.method === "Runtime.exceptionThrown") failures.push(JSON.stringify(data.params.exceptionDetails));
    });
    const command = (method: string, params: object = {}) => new Promise<Reply>((resolve, reject) => {
      const id = ++sequence, timer = setTimeout(() => { pending.delete(id); reject(new Error(`${method} timeout`)); }, 10_000);
      pending.set(id, reply => { clearTimeout(timer); reply.error ? reject(new Error(JSON.stringify(reply.error))) : resolve(reply.result ?? {}); });
      socket!.send(JSON.stringify({ id, method, params }));
    });
    const evaluate = async <T,>(expression: string): Promise<T> => {
      const reply = await command("Runtime.evaluate", { expression, returnByValue: true, awaitPromise: true });
      assert.equal(reply.exceptionDetails, undefined, JSON.stringify(reply.exceptionDetails));
      return reply.result?.value as T;
    };
    const waitFor = async (expression: string) => {
      const deadline = Date.now() + 10_000;
      while (Date.now() < deadline) { if (await evaluate<boolean>(`!!(${expression})`)) return; await new Promise(resolve => setTimeout(resolve, 50)); }
      assert.fail(`Not seen: ${expression}`);
    };
    const shot = async (name: string) => {
      if (!shots) return;
      const picture = await command("Page.captureScreenshot", { format: "png" }) as { data?: string };
      await writeFile(join(shots, `${name}.png`), Buffer.from(picture.data!, "base64"));
    };
    const card = (text: string) => `[...document.querySelectorAll("#timeline > li.pending")].find(e => e.textContent.includes(${JSON.stringify(text)}))`;
    const pendingTexts = () => evaluate<string[]>('[...document.querySelectorAll("#timeline > li.pending .markdown-content")].map(e => e.textContent.trim())');
    const steer = async (text: string) => {
      await evaluate(`(() => { const box = document.getElementById("prompt"); box.focus(); box.value = ${JSON.stringify(text)}; box.dispatchEvent(new Event("input", { bubbles: true })); })()`);
      await waitFor(`document.querySelector("#send span").textContent === "Steer"`);
      await evaluate('document.getElementById("send").click()');
      await waitFor(`${card(text)}?.querySelector(".pending-delete")`);
    };
    await command("Runtime.enable"); await command("Page.enable");
    await command("Emulation.setDeviceMetricsOverride", { width: 390, height: 844, deviceScaleFactor: 2, mobile: true });
    await command("Page.navigate", { url: `${server.origin}/#k=${server.state.token}` });
    await waitFor('document.querySelector(".row[data-id=s1] .card-link")');
    await evaluate('document.querySelector(".row[data-id=s1] .card-link").click()');
    await waitFor('document.querySelector("#timeline .activity")');

    // ---- The running tool says what it works on, folded; open, a row says it too, and a tap reads the call ----
    assert.match(await evaluate<string>('document.querySelector("#timeline .activity .act-current").textContent'), /^Bash · dotnet build -c Release$/u);
    assert.equal(server.requests.filter(request => request.path.includes("/tools/")).length, 0, "nothing of a call is read until it is opened");
    await evaluate('document.querySelector("#timeline .activity .act-toggle").click()');
    await waitFor('document.querySelector("#timeline .tool-row[data-activity=call-1] .tool-subject")');
    assert.equal(await evaluate('document.querySelector("#timeline .tool-row[data-activity=call-1] .tool-subject").textContent'), "dotnet build -c Release");
    assert.equal(await evaluate('document.querySelector("#timeline .tool-row[data-activity=call-2] .tool-subject")'), null, "a call that says nothing more has its name alone");
    await evaluate('document.querySelector("#timeline .tool-row[data-activity=call-1]").click()');
    await waitFor('document.querySelector(".tool-call .tool-detail .tool-text")');
    const facts = await evaluate<string[]>('[...document.querySelectorAll(".tool-detail .tool-fact")].map(e => e.textContent)');
    assert.deepEqual(facts, ["FolderC:\\work\\worktrees\\calm-otter"], "the folder it works in");
    assert.deepEqual(await evaluate('[...document.querySelectorAll(".tool-detail .tool-section")].map(e => e.textContent)'), ["Command", "Output"]);
    assert.match(await evaluate<string>('document.querySelector(".tool-detail .tool-note").textContent'), /^The last [\d,. ]+ of 40[,. ]?000 characters\.$/u, "says that only the end came");
    assert.equal(server.requests.filter(request => request.path.endsWith("/tools/call-1")).at(-1)?.query, `?since=1`, "from the first event of the call");
    assert.equal(await evaluate("document.documentElement.scrollWidth <= window.innerWidth"), true, "a long line scrolls inside its box, never the page");
    assert.equal(await evaluate('(() => { const output = [...document.querySelectorAll(".tool-detail .tool-text")].at(-1); return output.scrollWidth > output.clientWidth; })()'), true, "the output scrolls sideways");
    await shot("tool-call");
    // What is open follows the call: once it ended, it says how.
    server.state.tools.set("s1/call-1", { name: "Bash", phase: "failed", command: "dotnet build -c Release", sessionFolder: "C:\\work\\worktrees\\calm-otter", exitCode: 1, failure: "It broke", output: "error CS0001", outputLength: 12, error: null });
    server.state.events.get("s1")!.push(event("activity.toolcall.failed", "Bash", { activityId: "call-1", subject: "dotnet build -c Release" }));
    server.push(running);
    await waitFor('[...document.querySelectorAll(".tool-detail .tool-fact")].some(e => e.textContent === "Exit code1")');
    assert.equal(await evaluate('document.querySelector(".tool-detail .tool-bad")?.textContent'), "1");
    // A second tap folds it.
    await evaluate('document.querySelector("#timeline .tool-row[data-activity=call-1]").click()');
    await waitFor('!document.querySelector(".tool-detail")');
    assert.equal(await evaluate('document.querySelector("#timeline .tool-row[data-activity=call-1]").getAttribute("aria-expanded")'), "false");

    // ---- A prompt queued before the page opened shows, and can be deleted ----
    await waitFor(`${card("Queued before the reload")}`);
    assert.equal(await evaluate(`${card("Queued before the reload")}.querySelector(".msg-label").firstChild.textContent`), "You · queued");
    await evaluate(`${card("Queued before the reload")}.querySelector(".pending-delete").click()`);
    await waitFor(`!${card("Queued before the reload")}`);
    assert.deepEqual(server.state.queues.get("s1"), [], "it left the queue of the session");

    // ---- A steered prompt the run still holds is deleted ----
    await steer("Forget about this");
    assert.deepEqual(await pendingTexts(), ["Forget about this"]);
    await evaluate("window.scrollTo(0, document.documentElement.scrollHeight)");
    await shot("pending-steer");
    await evaluate(`${card("Forget about this")}.querySelector(".pending-delete").click()`);
    await waitFor(`!${card("Forget about this")}`);
    assert.equal(server.state.steers.size, 0);
    assert.equal(await evaluate('document.getElementById("prompt").value'), "", "a delete does not fill the composer");

    // ---- Edit takes it back to the composer, before what was being typed ----
    await steer("Fix the typo");
    await evaluate(`(() => { const box = document.getElementById("prompt"); box.value = "and more"; box.dispatchEvent(new Event("input", { bubbles: true })); })()`);
    await evaluate(`${card("Fix the typo")}.querySelector(".pending-edit").click()`);
    await waitFor(`!${card("Fix the typo")}`);
    assert.equal(await evaluate('document.getElementById("prompt").value'), "Fix the typo\n\nand more");
    await evaluate(`(() => { const box = document.getElementById("prompt"); box.value = ""; box.dispatchEvent(new Event("input", { bubbles: true })); })()`);

    // ---- Too late: the agent has it. The card stays, says so, and offers nothing more; nothing goes back to the composer ----
    await steer("Too late for this");
    server.state.steers.clear();
    await evaluate(`${card("Too late for this")}.querySelector(".pending-edit").click()`);
    await waitFor('document.getElementById("detail-note")?.textContent.includes("the agent already has this prompt")');
    await waitFor(`!${card("Too late for this")}.querySelector(".pending-delete")`);
    assert.equal(await evaluate('document.getElementById("prompt").value'), "");

    // ---- Queued from the phone, then taken as the session was sending it: said, and it stays ----
    await evaluate(`(() => { const box = document.getElementById("prompt"); box.focus(); box.value = "Then the docs"; box.dispatchEvent(new Event("input", { bubbles: true })); })()`);
    await waitFor('!document.getElementById("queue").hidden');
    await evaluate('document.getElementById("queue").click()');
    await waitFor(`${card("Then the docs")}?.querySelector(".pending-delete")`);
    server.state.unqueueStatus = "sending";
    await evaluate(`${card("Then the docs")}.querySelector(".pending-delete").click()`);
    await waitFor('document.getElementById("detail-note")?.textContent.includes("the session is taking this prompt")');
    assert.ok(await evaluate<boolean>(`!!${card("Then the docs")}`));

    assert.deepEqual(failures, []);
  } finally {
    try { socket?.close(); } catch { /* already closed */ }
    await endBrowser(browser); await server.close();
    await rm(directory, { recursive: true, force: true, maxRetries: 5, retryDelay: 200 }).catch(() => {});
  }
});
