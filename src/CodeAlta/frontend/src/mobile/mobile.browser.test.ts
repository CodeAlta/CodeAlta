import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { mkdtemp, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";
import { build } from "esbuild";
import { browserBaseArgs, browserExecutable, endBrowser, waitForBrowserPort } from "../browserTarget";
import type { Options, TimelineEvent } from "./contract";
import { mkdir, writeFile } from "node:fs/promises";
import { pageFiles, session, startFixtureServer } from "./fixtureServer";

const edge = browserExecutable;
// Where the pictures of this test go, when a person wants to look at them.
const shots = process.env.CODEALTA_SHOTS ?? "";

const options: Options = {
  models: [
    { provider: "codex", id: "gpt-6.1", name: "GPT-6.1", providerName: "Codex", efforts: ["low", "medium", "high"], defaultEffort: "medium" },
    { provider: "claude", id: "claude-opus-4-7", name: "Opus 4.7", providerName: "Claude", efforts: ["low", "high"], defaultEffort: "high" },
  ],
  prompts: [{ id: "default", name: "Default", description: null }, { id: "plan", name: "Plan", description: null }],
  tiers: [
    { tier: "complex", provider: "claude", model: "claude-opus-4-7", reasoning: "high", error: null },
    { tier: "standard", provider: "codex", model: "gpt-6.1", reasoning: "medium", error: null },
    { tier: "quick", provider: null, model: null, reasoning: null, error: "No model is set for this tier." },
  ],
  defaultTier: "standard",
  defaults: { provider: "codex", model: "gpt-6.1", reasoning: "medium", prompt: "default" },
  error: null,
};

test("the phone page signs in from its link, lists and follows sessions, opens, sends, steers, stops, archives and starts sessions", { skip: !edge, timeout: 120_000 }, async () => {
  const directory = await mkdtemp(join(tmpdir(), "codealta-mobile-"));
  let browser: ReturnType<typeof spawn> | undefined, socket: WebSocket | undefined;
  const bundle = await build({ entryPoints: [fileURLToPath(new URL("./main.ts", import.meta.url))], bundle: true, format: "esm", platform: "browser",
    write: false, outdir: join(directory, "out"), external: ["mermaid"], define: { "process.env.NODE_ENV": '"production"' }, logLevel: "silent" });
  const script = bundle.outputFiles.find(file => file.path.endsWith(".js"))!.text, style = bundle.outputFiles.find(file => file.path.endsWith(".css"))!.text;
  const server = await startFixtureServer(await pageFiles(script, style));
  const minute = 60_000;
  let seq = 0;
  const event = (kind: string, text: string, activityId: string | null = null): TimelineEvent => ({ seq: ++seq, at: new Date().toISOString(), kind, text, activityId });
  server.state.options = options;
  server.state.sessions = [
    session("s1", "Fix the parser", "p1", 2, { kind: "assistant", line: "Fixed." }),
    session("s2", "Write the docs", "p2", 30, {}, { worktree: "docs-site", agent: { id: "plan", name: "Plan" } }),
  ];
  server.state.events.set("s1", [
    event("user.message", "Fix the parser please"),
    event("activity.turn.started", "turn"),
    event("assistant.reasoning", "**Reading the grammar**"),
    event("activity.tool.started", "read_file", "c1"),
    event("activity.tool.completed", "read_file", "c1"),
    event("assistant.message", "Fixed. Steps:\n\n1. Read **the grammar**\n2. Patched `parse()`\n\n```ts\nconst x = 1;\n```"),
    event("activity.turn.completed", "turn"),
  ]);
  server.state.events.set("s2", [event("user.message", "Docs?"), event("assistant.message", "Written.")]);
  const failures: string[] = [];
  try {
    browser = spawn(edge!, [...browserBaseArgs, `--user-data-dir=${join(directory, "profile")}`, "--remote-debugging-port=0", "about:blank"],
      { stdio: ["ignore", "ignore", "pipe"], windowsHide: true });
    const port = await waitForBrowserPort(join(directory, "profile"), browser);
    const pages = await (await fetch(`http://127.0.0.1:${port}/json/list`, { signal: AbortSignal.timeout(5000) })).json() as { type: string; url: string; webSocketDebuggerUrl: string }[];
    socket = new WebSocket(pages.find(p => p.type === "page")!.webSocketDebuggerUrl);
    await new Promise<void>((resolve, reject) => { socket!.addEventListener("open", () => resolve(), { once: true }); socket!.addEventListener("error", reject, { once: true }); });
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
    const waitUntil = async (condition: () => boolean, what: string, timeout = 8000) => {
      const deadline = Date.now() + timeout;
      while (!condition()) {
        if (Date.now() > deadline) assert.fail(`${what}: not seen within ${timeout} ms`);
        await new Promise(resolve => setTimeout(resolve, 50));
      }
    };
    const count = (method: string, path: string | RegExp) => server.requests.filter(r => r.method === method && (typeof path === "string" ? r.path === path : path.test(r.path))).length;
    const typeInto = (selector: string, text: string) => evaluate(`(() => { const box = document.querySelector(${JSON.stringify(selector)}); box.focus(); box.value = ${JSON.stringify(text)}; box.dispatchEvent(new Event("input", { bubbles: true })); })()`);
    const click = (selector: string) => evaluate(`document.querySelector(${JSON.stringify(selector)}).click()`);
    const shot = async (name: string) => {
      if (!shots) return;
      await mkdir(shots, { recursive: true });
      await writeFile(join(shots, name), Buffer.from(((await command("Page.captureScreenshot", { format: "png" })) as { data: string }).data, "base64"));
    };
    const texts = (selector: string) => evaluate<string[]>(`[...document.querySelectorAll(${JSON.stringify(selector)})].map(e => e.textContent)`);
    const shown = (selector: string) => `(() => { const e = document.querySelector(${JSON.stringify(selector)}); return !!e && !e.closest("[hidden]") && e.getClientRects().length > 0; })()`;

    await command("Runtime.enable"); await command("Log.enable"); await command("Page.enable");
    // A phone: 390 × 844 CSS pixels.
    await command("Emulation.setDeviceMetricsOverride", { width: 390, height: 844, deviceScaleFactor: 2, mobile: true });

    // Sign-in: the token of the link is in the fragment; it leaves the address and gets the cookie.
    await command("Page.navigate", { url: `${server.origin}/#k=${server.state.token}` });
    await waitFor(`document.querySelectorAll("#sessions .row").length === 2`, "the list of sessions");
    assert.equal(await evaluate("location.href"), `${server.origin}/`);
    assert.equal(await evaluate("localStorage.getItem('alta.token')"), "secret");
    assert.deepEqual(server.requests.filter(r => r.path === "/api/login").map(r => r.body), [{ token: "secret" }]);
    assert.ok(server.requests.every(r => !/[?&]t=/.test(r.query)), "the token never goes in a query");
    assert.deepEqual(await evaluate("[...document.querySelectorAll('#sessions .row')].map(r => r.dataset.id)"), ["s1", "s2"]);
    assert.equal(await evaluate("document.querySelector('#sessions li li')"), null, "no list item inside a list item");
    await waitFor(`document.getElementById("foot").textContent.includes("FIXTURE-PC · Live")`, "the footer says the stream is live");
    assert.equal(await evaluate("document.querySelector('meta[name=theme-color]').content"), "#252a31");
    // Where the phone is: the computer and the spaces it sees. A card: its facts on one line, compact, no internal state word.
    assert.deepEqual(await texts("#scope-where > span:not(.ico)"), ["FIXTURE-PC", "Work", "Home"]);
    assert.deepEqual(await texts('.row[data-id="s1"] .meta-item'), ["Work", "parser", "GPT-6.1 Sol · low"]);
    assert.deepEqual(await texts('.row[data-id="s2"] .meta-item'), ["Home", "site", "docs-site", "GPT-6.1 Sol · low", "Plan"]);
    assert.equal(await evaluate("/inactive|done/.test(document.getElementById('sessions').textContent)"), false);
    assert.ok(await evaluate<number>("document.querySelector('.row[data-id=\"s1\"]').getBoundingClientRect().height") <= 90, "a card of one title line is compact");

    // A live update changes the card; an update of a session out of the list reads the list once.
    server.push({ id: "s2", projectId: "p2", live: "running", kind: "writing", line: "Writing…", at: new Date().toISOString(), activity: null, canSteer: false });
    await waitFor(`document.querySelector('.row[data-id="s2"] .card').classList.contains("running")`, "a working card");
    assert.equal(await evaluate("document.querySelector('.row[data-id=\"s2\"] .meta-live')?.textContent"), "Writing", "a word only while it works");
    const reads = count("GET", "/api/sessions");
    server.state.sessions.push(session("s9", "Started on the computer", "p1", 0, { live: "running" }, { parentId: "s1" }));
    server.push(server.state.sessions.at(-1)!.live!);
    await waitFor(`document.querySelector('.row[data-id="s9"]')`, "a session started elsewhere joins the list");
    server.push({ ...server.state.sessions.at(-1)!.live!, live: "tools" });
    await new Promise(resolve => setTimeout(resolve, 700));
    assert.equal(count("GET", "/api/sessions"), reads + 1, "one read of the list for it");
    assert.ok((await texts('.row[data-id="s9"] .meta-item')).includes("Sub-agent"));
    await shot("mobile-list.png");

    // The theme follows a tap, with the color of the browser's bar.
    await click("#theme");
    assert.deepEqual(await evaluate("[document.documentElement.dataset.theme, document.querySelector('meta[name=theme-color]').content, localStorage.getItem('alta.theme')]"), ["light", "#ffffff", "light"]);
    await click("#theme");

    // Open a session: its timeline, folded and rendered.
    await click('.row[data-id="s1"] .card-link');
    await waitFor(`document.querySelectorAll("#timeline > li").length === 3`, "the timeline of the session");
    assert.deepEqual(await evaluate("[...document.querySelectorAll('#timeline > li')].map(li => li.className)"), ["msg msg-user", "activity", "msg msg-assistant"]);
    assert.equal(await evaluate("document.querySelector('.msg-assistant ol li strong')?.textContent"), "the grammar", "numbered lists and bold from the real renderer");
    assert.equal(await evaluate("document.querySelector('.msg-assistant pre code')?.textContent"), "const x = 1;\n");
    assert.equal(await evaluate("document.querySelector('#detail-title').textContent"), "Fix the parser");
    assert.deepEqual(await texts("#detail-meta .meta-item"), ["Work", "parser", "GPT-6.1 Sol · low"], "what a prompt goes to, under the title");
    assert.ok(await evaluate<boolean>(shown("#composer #foot")), "the footer is under the composer on a session's page");
    await click("#timeline .act-toggle");
    await waitFor(`document.querySelectorAll("#timeline .act-body > *").length === 2`, "the opened activity");
    assert.equal(await evaluate("document.querySelector('#timeline .tool-row')?.dataset.phase"), "completed");

    // Send: the prompt leaves the composer and its draft.
    await typeInto("#prompt", "Add a test");
    await click("#send");
    await waitUntil(() => count("POST", "/api/sessions/s1/prompt") === 1, "the prompt sent");
    assert.deepEqual(server.requests.find(r => r.path === "/api/sessions/s1/prompt")?.body, { text: "Add a test", mode: "send" });
    assert.equal(await evaluate("document.getElementById('prompt').value"), "");
    assert.equal(await evaluate("localStorage.getItem('alta.draft.s1')"), null);
    assert.deepEqual(await texts("#timeline > li.pending .msg-label"), ["You"], "the prompt shows at once");

    // The timeline grows block by block: the elements already drawn stay.
    await evaluate("document.querySelector('#timeline > li').dataset.mark = 'kept'");
    server.state.events.get("s1")!.push(event("user.message", "Add a test"), event("activity.turn.started", "turn"), event("activity.tool.started", "shell", "c2"));
    server.push({ id: "s1", projectId: "p1", live: "running", kind: "activity", line: null, at: new Date().toISOString(), activity: null, canSteer: true });
    // The sent prompt was shown at once; the journal's message takes its place.
    await waitFor(`document.querySelectorAll("#timeline > li").length === 5 && !document.querySelector("#timeline > li.pending, #timeline > li.working")`, "the new blocks");
    assert.equal(await evaluate("document.querySelector('#timeline > li').dataset.mark"), "kept");
    assert.equal(await evaluate("document.querySelector('#timeline > li:last-child').dataset.state"), "running");
    assert.equal(await evaluate("[...document.querySelectorAll('#timeline .tool-row strong')].map(e => e.textContent).includes('turn')"), false, "turns are not tool rows");

    // While the turn runs: Stop alone, then Steer and Queue once text is typed; the form steers.
    await waitFor(shown("#abort"), "the Stop button");
    assert.equal(await evaluate<boolean>(shown("#send")), false);
    await typeInto("#prompt", "Use the fast path");
    assert.ok(await evaluate<boolean>(shown("#send")) && await evaluate<boolean>(shown("#queue")));
    assert.equal(await evaluate("document.querySelector('#send span').textContent"), "Steer");
    await evaluate("document.getElementById('composer').requestSubmit()");
    await waitUntil(() => count("POST", "/api/sessions/s1/prompt") === 2, "the prompt steered");
    assert.deepEqual(server.requests.filter(r => r.path === "/api/sessions/s1/prompt").at(-1)?.body, { text: "Use the fast path", mode: "steer" });
    // The steered prompt is in the timeline as soon as it is accepted, and becomes the journal's message once it holds it.
    await waitFor(`document.querySelector("#timeline > li.pending")?.textContent.includes("Use the fast path")`, "the steered prompt");
    assert.deepEqual(await texts("#timeline > li.pending .msg-label"), ["You · steering"]);
    await shot("mobile-session.png");
    server.state.events.get("s1")!.push(event("user.message", "Use the fast path"));
    server.push({ id: "s1", projectId: "p1", live: "running", kind: "user", line: "Use the fast path", at: new Date().toISOString(), activity: null, canSteer: true });
    await waitFor(`!document.querySelector("#timeline > li.pending") && [...document.querySelectorAll("#timeline > li.msg-user")].at(-1)?.textContent.includes("Use the fast path")`, "the journal's message in its place");

    // Stop: one tap, "Stopping…" until the session is idle.
    await click("#abort");
    await waitFor(`document.querySelector('#abort span').textContent === "Stopping…"`, "Stopping…");
    assert.equal(count("POST", "/api/sessions/s1/abort"), 1);
    server.push({ id: "s1", projectId: "p1", live: "idle", kind: "assistant", line: "Stopped.", at: new Date().toISOString(), activity: null, canSteer: false });
    await waitFor(`!(${shown("#abort")})`, "Stop leaves with the turn");

    // Drafts are kept per session: a text typed for one session never goes to another.
    await typeInto("#prompt", "draft for s1");
    await evaluate("location.hash = '#/session/s2'");
    await waitFor(`document.querySelector('#detail-title').textContent === "Write the docs"`, "the other session");
    assert.equal(await evaluate("document.getElementById('prompt').value"), "");
    await evaluate("location.hash = '#/session/s1'");
    await waitFor(`document.getElementById('prompt').value === "draft for s1"`, "the draft of s1 back");

    // Archive without a swipe (the button of a card), then Restore from the toast.
    await evaluate("location.hash = '#/'");
    await waitFor(shown("#sessions"), "the list");
    await click('.row[data-id="s2"] .card-archive');
    await waitFor(`document.querySelector('#toast .toast-action')`, "the toast");
    assert.match(await evaluate<string>("document.querySelector('#toast .toast-text').textContent"), /Archived “Write the docs”/);
    await waitUntil(() => count("POST", "/api/sessions/s2/archive") === 1, "the archive asked for");
    await click("#toast .toast-action");
    await waitFor(`document.querySelector('.row[data-id="s2"]') && !document.querySelector('.row[data-id="s2"]').classList.contains("collapsing")`, "the restored card");
    assert.equal(count("POST", "/api/sessions/s2/archive"), 1);
    assert.equal(count("POST", "/api/sessions/s2/restore"), 1);

    // The project filter is a modal sheet: Escape closes it and gives the focus back.
    await evaluate("document.getElementById('filter').focus()");
    await click("#filter");
    await waitFor(shown("#sheet"), "the project sheet");
    assert.equal(await evaluate("document.querySelector('#home').inert"), true);
    await command("Input.dispatchKeyEvent", { type: "keyDown", key: "Escape", code: "Escape", windowsVirtualKeyCode: 27 });
    await command("Input.dispatchKeyEvent", { type: "keyUp", key: "Escape", code: "Escape", windowsVirtualKeyCode: 27 });
    await waitFor(`!(${shown("#sheet")})`, "the sheet closed");
    assert.equal(await evaluate("document.activeElement?.id"), "filter");

    // A new session: the default tier of the settings; a prompt that did not go is back in the composer of its session.
    await click("#new");
    await waitFor(`document.getElementById('new-model-name').textContent === "Standard · GPT-6.1"`, "the default tier");
    await click("#new-model");
    await waitFor(shown("#model-sheet"), "the model sheet");
    assert.deepEqual(await evaluate("[...document.querySelectorAll('#tiers .tier')].map(b => [b.textContent, b.disabled])"),
      [["ComplexOpus 4.7", false], ["StandardGPT-6.1", false], ["QuickNo model is set for this tier.", true]]);
    await click("#tiers .tier");
    await waitFor(`document.getElementById('new-model-name').textContent === "Complex · Opus 4.7"`, "the complex tier");
    assert.equal(await evaluate("document.getElementById('new-space').textContent"), "Work", "the space the session goes to, beside its project");
    assert.ok(await evaluate<boolean>("document.getElementById('new-start').getBoundingClientRect().bottom <= innerHeight"), "Start is on the screen without scrolling");
    server.state.createAnswer = { sendError: "The provider is busy." };
    await typeInto("#new-text", "Make it faster");
    await shot("mobile-new-session.png");
    await evaluate("document.getElementById('new-form').requestSubmit()");
    await waitFor(`location.hash === "#/session/new1"`, "the page of the new session");
    assert.deepEqual(server.requests.find(r => r.method === "POST" && r.path === "/api/sessions")?.body,
      { project: "p1", text: "Make it faster", prompt: "default", tier: "complex", worktree: false });
    await waitFor(`document.getElementById('detail-note').textContent.includes("not sent: The provider is busy.")`, "the failed send on its page");
    assert.equal(await evaluate("document.getElementById('prompt').value"), "Make it faster");
    await waitFor(`document.querySelector('#detail-title').textContent === "New: Make it faster"`, "a session out of the list read alone");

    // The page fits the phone: nothing wider than the screen.
    for (const hash of ["#/", "#/session/s1", "#/new"]) {
      await evaluate(`location.hash = ${JSON.stringify(hash)}`);
      await new Promise(resolve => setTimeout(resolve, 150));
      assert.ok(await evaluate<boolean>("document.documentElement.scrollWidth <= window.innerWidth"), `no horizontal overflow on ${hash}`);
    }
    await evaluate("location.hash = '#/'");

    // An expired cookie signs in again with the kept token; a reset token says the phone is no longer connected.
    server.state.cookies.clear();
    const logins = count("POST", "/api/login");
    await click("#refresh");
    await waitUntil(() => count("POST", "/api/login") === logins + 1, "a new sign-in with the kept token");
    await waitFor(`document.querySelectorAll("#sessions .row").length >= 2`, "the list after signing in again");
    server.state.cookies.clear();
    server.state.token = "reset";
    server.dropStreams();
    await click("#refresh");
    await waitFor(`document.getElementById('note').textContent.includes("no longer connected")`, "the signed-out message");
    assert.equal(await evaluate("localStorage.getItem('alta.token')"), null);
    assert.equal(await evaluate("document.querySelectorAll('#sessions .row').length"), 0);
    assert.match(await evaluate<string>("document.getElementById('foot').textContent"), /Not connected/);

    assert.deepEqual(failures, [], "no error, no policy violation");
  } finally {
    try { socket?.close(); } catch { /* already closed */ }
    await endBrowser(browser);
    await server.close();
    await rm(directory, { recursive: true, force: true, maxRetries: 5, retryDelay: 200 }).catch(() => { });
  }
});
