import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { mkdtemp, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";
import { build } from "esbuild";
import { browserBaseArgs, browserExecutable, endBrowser, waitForBrowserPort } from "../browserTarget";
import type { TimelineEvent } from "./contract";
import { pageFiles, session, startFixtureServer } from "./fixtureServer";

test("the phone opens a session on its latest messages, says when they were written, and reads older ones as it scrolls up", { skip: !browserExecutable, timeout: 90_000 }, async () => {
  const directory = await mkdtemp(join(tmpdir(), "codealta-mobile-history-"));
  let browser: ReturnType<typeof spawn> | undefined, socket: WebSocket | undefined;
  const bundle = await build({ entryPoints: [fileURLToPath(new URL("./main.ts", import.meta.url))], bundle: true, format: "esm", platform: "browser",
    write: false, outdir: join(directory, "out"), external: ["pele/lazy"], loader: { ".woff2": "file", ".woff": "file", ".ttf": "file" }, define: { "process.env.NODE_ENV": '"production"' }, logLevel: "silent" });
  const script = bundle.outputFiles.find(file => file.path.endsWith(".js"))!.text, style = bundle.outputFiles.find(file => file.path.endsWith(".css"))!.text;
  const server = await startFixtureServer(await pageFiles(script, style));
  // Forty turns over the last days, each a long prompt and a long answer with a tool call between them.
  const now = new Date();
  const dayAt = (daysAgo: number, hour: number) => new Date(now.getFullYear(), now.getMonth(), now.getDate() - daysAgo, hour, 0).toISOString();
  const long = "A line of words that takes room on the screen of a phone. ".repeat(8);
  const events: TimelineEvent[] = [];
  let seq = 0;
  const event = (kind: string, text: string, when: string, activityId: string | null = null) => events.push({ seq: ++seq, at: when, kind, text, activityId });
  for (let turn = 0; turn < 40; turn++) {
    const when = turn < 20 ? dayAt(2, 9) : turn < 30 ? dayAt(1, 9) : dayAt(0, 0);
    event("user.message", `Prompt ${turn}. ${long}`, when);
    event("activity.toolcall.started", "read_file", when, `call-${turn}`);
    event("activity.toolcall.completed", "read_file", when, `call-${turn}`);
    event("assistant.message", `Answer ${turn}. ${long}`, when);
  }
  server.state.sessions = [session("s1", "A long session", "p1", 0)];
  server.state.events.set("s1", events);
  server.state.messagesPerPage = 20;
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
    const messages = () => evaluate<number>('document.querySelectorAll("#timeline .msg").length');
    const atBottom = () => evaluate<boolean>("window.innerHeight + window.scrollY >= document.documentElement.scrollHeight - 40");
    const latestReads = () => server.requests.filter(request => request.path.endsWith("/events/latest"));
    await command("Runtime.enable"); await command("Page.enable");
    await command("Emulation.setDeviceMetricsOverride", { width: 390, height: 844, deviceScaleFactor: 2, mobile: true });
    await command("Page.navigate", { url: `${server.origin}/#k=${server.state.token}` });
    await waitFor('document.querySelector(".row[data-id=s1] .card-link")');
    await evaluate('document.querySelector(".row[data-id=s1] .card-link").click()');
    await waitFor('document.querySelector("#timeline .msg")');

    // It opens on the latest messages, at the end, without reading the whole session.
    assert.equal(await messages(), 20, "a page of messages");
    assert.ok(await atBottom(), "the end of the timeline shows");
    assert.ok(!server.requests.some(request => request.path.endsWith("/events") && request.query === "?since=0"), "the session is not read from its start");
    assert.deepEqual(latestReads().map(request => request.query), [""]);
    assert.ok((await evaluate<string>('[...document.querySelectorAll("#timeline .msg")].at(-1).textContent')).includes("Answer 39"));

    // Each message says when it was written, and each day has its line.
    assert.deepEqual(await evaluate('[...document.querySelectorAll("#timeline .day-mark")].map(e => e.textContent)'), ["Today"]);
    assert.match(await evaluate<string>('document.querySelector("#timeline .msg-user .msg-label").textContent'), /^You · \S+/u);
    assert.match(await evaluate<string>('document.querySelector("#timeline .msg-assistant .msg-label").textContent'), /^Agent · \S+/u);
    assert.equal(await evaluate('getComputedStyle(document.querySelector("#timeline .msg-time")).fontWeight'), "400");

    // Coming near the top reads the page before, and what shows does not move.
    const topMessage = '[...document.querySelectorAll("#timeline .msg")].find(e => e.textContent.includes("Prompt 30"))';
    // Measured in the same step as the scroll: the read it starts cannot have ended yet.
    const before = await evaluate<number>(`window.__seen = ${topMessage}; window.scrollTo(0, 0); window.__seen.getBoundingClientRect().top`);
    await waitFor('document.querySelectorAll("#timeline .msg").length === 40');
    assert.ok(Math.abs(await evaluate<number>("window.__seen.getBoundingClientRect().top") - before) < 2, "the message that showed stays where it was");
    assert.ok(await evaluate("window.__seen.isConnected"), "the messages held keep their elements");
    assert.deepEqual(latestReads().map(request => request.query), ["", "?before=121"]);
    assert.deepEqual(await evaluate('[...document.querySelectorAll("#timeline .day-mark")].map(e => e.textContent)'), ["Yesterday", "Today"], "the older page brings its day");
    await new Promise(resolve => setTimeout(resolve, 200));
    assert.equal(latestReads().length, 2, "the top is far again: nothing more is read until the page comes near it");

    // A read that fails says so, is not made again by itself, and a tap makes it again.
    server.state.olderFails = true;
    await evaluate("window.scrollTo(0, 0)");
    await waitFor('document.querySelector("#detail-note").textContent.includes("Could not read the older messages")');
    const failed = latestReads().length;
    await new Promise(resolve => setTimeout(resolve, 300));
    assert.equal(latestReads().length, failed, "a failed read does not repeat by itself");
    assert.equal(await evaluate('document.querySelector("#older").hidden || document.querySelector("#older").disabled'), false, "it can be tapped");
    server.state.olderFails = false;
    await evaluate('document.querySelector("#older").click()');
    await waitFor(`document.querySelectorAll("#timeline .msg").length === 60`);

    // Up to the first message: then nothing is left to read, and each message shows once.
    for (let attempt = 0; attempt < 20 && !await evaluate<boolean>('document.querySelector("#older").hidden'); attempt++) {
      await evaluate("window.scrollTo(0, 0)");
      await new Promise(resolve => setTimeout(resolve, 100));
    }
    assert.equal(await evaluate('document.querySelector("#older").hidden'), true);
    assert.equal(await messages(), 80, "the whole session");
    assert.ok((await evaluate<string>('document.querySelector("#timeline .msg").textContent')).includes("Prompt 0"));
    const keys = await evaluate<string[]>('[...document.querySelectorAll("#timeline > li")].map(e => e.dataset.key)');
    assert.equal(new Set(keys).size, keys.length, "no block twice");
    assert.equal((await evaluate<string[]>('[...document.querySelectorAll("#timeline .day-mark")].map(e => e.textContent)')).length, 3, "a line for each of the three days");
    assert.equal(await evaluate('document.querySelector("#timeline > li").className'), "day-mark", "the first block has its day above it");
    assert.deepEqual(await evaluate('[...document.querySelectorAll("#timeline .day-mark")].slice(1).map(e => e.textContent)'), ["Yesterday", "Today"]);

    // A new event arrives by the read after the last one held.
    event("assistant.message", "A new answer.", dayAt(0, 0));
    server.push({ id: "s1", projectId: "p1", live: "idle", kind: "assistant", line: "A new answer.", at: new Date().toISOString(), activity: null, canSteer: false });
    await waitFor('[...document.querySelectorAll("#timeline .msg")].at(-1).textContent.includes("A new answer.")');
    assert.ok(server.requests.some(request => request.path.endsWith("/events") && request.query === `?since=${seq - 1}`), "only what is new is read");

    // A refresh, far up in the session, goes back to the latest messages at the end.
    await evaluate("window.scrollTo(0, 0)");
    await evaluate('document.querySelector("#refresh").click()');
    await waitFor('document.querySelectorAll("#timeline .msg").length <= 21 && !document.querySelector("#refresh").classList.contains("spin")');
    assert.ok(await atBottom(), "what showed is gone: the page shows the end");
    assert.equal(await evaluate('document.querySelector("#older").hidden'), false, "and the older messages can be read again");

    assert.deepEqual(failures, []);
  } finally {
    try { socket?.close(); } catch { /* already closed */ }
    await endBrowser(browser); await server.close();
    await rm(directory, { recursive: true, force: true, maxRetries: 5, retryDelay: 200 }).catch(() => {});
  }
});
