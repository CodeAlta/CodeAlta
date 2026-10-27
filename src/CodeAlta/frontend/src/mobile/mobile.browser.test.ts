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
  permissionModes: { claude: [{ id: "default", name: "Ask first" }, { id: "acceptEdits", name: "Accept edits" }, { id: "bypassPermissions", name: "Bypass permissions" }] },
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
    // A phone: 390 × 844 CSS pixels (at the scale of a recent phone when the pictures are kept).
    await command("Emulation.setDeviceMetricsOverride", { width: 390, height: 844, deviceScaleFactor: shots ? 3 : 2, mobile: true });

    // Sign-in: the token of the link is in the fragment; it leaves the address and gets the cookie.
    await command("Page.navigate", { url: `${server.origin}/#k=${server.state.token}` });
    await waitFor(`document.querySelectorAll("#sessions .row").length === 2`, "the list of sessions");
    assert.equal(await evaluate("location.href"), `${server.origin}/`);
    // "Not now" on the offer to install the app.
    if (await evaluate<boolean>(shown("#install-bar"))) await click("#install-later");
    assert.equal(await evaluate("localStorage.getItem('alta.token')"), "key1", "the phone keeps the key the computer gave it, not the pairing token");
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
    await shot("01-list-dark.png");

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
    assert.deepEqual(await texts("#detail-meta .meta-item"), ["Work", "parser"], "where a prompt goes, under the title");
    assert.ok(await evaluate<boolean>(shown("#composer #foot")), "the footer is under the composer on a session's page");
    // What it runs on, on the line of the buttons: the chip of the model and its effort, and "⋯".
    await waitUntil(() => count("GET", "/api/sessions/s1/choices") === 1, "the choices of the session read");
    assert.equal(await evaluate("document.getElementById('pick-model-text').textContent"), "GPT-6.1 Sol · low");
    const line = await evaluate<{ chip: number; send: number; box: number; composer: number }>(`(() => { const r = e => document.getElementById(e).getBoundingClientRect();
      return { chip: r("pick-model").top, send: r("send").top, box: r("prompt").height, composer: r("composer").height }; })()`);
    assert.equal(line.chip, line.send, "the chip is on the line of Send: no room taken");
    assert.ok(line.composer <= line.box + 44 + 60, `the composer at rest is a box, one line of buttons and the footer (${JSON.stringify(line)})`);
    assert.ok(await evaluate<number>("document.getElementById('pick-more').getBoundingClientRect().height") >= 44, "touch targets of 44 px");
    await click("#timeline .act-toggle");
    await waitFor(`document.querySelectorAll("#timeline .act-body > *").length === 2`, "the opened activity");
    assert.equal(await evaluate("document.querySelector('#timeline .tool-row')?.dataset.phase"), "completed");
    await shot("02-session-dark.png");

    // The chip opens the model sheet: the effort, then the models of the session's provider. A model keeps the effort shown when it has
    // it, else takes its start effort; the sheet stays open until Done.
    await evaluate("document.getElementById('pick-model').focus()");
    await click("#pick-model");
    await waitFor(shown("#model-sheet"), "the model sheet of the session");
    assert.equal(await evaluate<boolean>(shown("#tiers")), false, "a session has no tiers");
    assert.equal(await evaluate("document.getElementById('model-provider').textContent"), "Codex");
    assert.deepEqual(await texts("#model-list button > span"), ["GPT-6.1 Argon", "GPT-6.1 Sol", "GPT-5.5"]);
    assert.deepEqual(await texts("#sheet-effort .reasoning-labels button"), ["low", "medium", "high"]);
    assert.equal(await evaluate("document.querySelector('#sheet-effort .reasoning-labels button.on').textContent"), "low");
    await evaluate(`[...document.querySelectorAll("#model-list button")].find(b => b.textContent.includes("Argon")).click()`);
    await waitFor(`document.querySelector('#sheet-effort .reasoning-labels button.on')?.textContent === "high"`, "the start effort of the other model");
    assert.deepEqual(await texts("#sheet-effort .reasoning-labels button"), ["medium", "high", "xhigh"]);
    await evaluate(`[...document.querySelectorAll("#sheet-effort .reasoning-labels button")].find(b => b.textContent === "medium").click()`);
    await waitFor(`document.getElementById('pick-model-text').textContent === "GPT-6.1 Argon · medium"`, "the chip says the pick");
    assert.equal(await evaluate("document.getElementById('pick-model').classList.contains('pending')"), true, "a pick waits for the next prompt");
    await shot("03-model-sheet-dark.png");
    await click("#model-done");
    await waitFor(`!(${shown("#model-sheet")})`, "the model sheet closed");
    assert.equal(await evaluate("document.activeElement?.id"), "pick-model", "the focus back on the chip");

    // "⋯": the agent prompt, the permissions of the provider, and queueing rather than steering.
    await click("#pick-more");
    await waitFor(shown("#session-more-sheet"), "the options of the session");
    assert.deepEqual(await evaluate("[...document.querySelectorAll('#session-agent option')].map(o => o.textContent)"), ["Default", "Plan", "Review"]);
    assert.deepEqual(await evaluate("[...document.querySelectorAll('#session-mode option')].map(o => o.textContent)"),
      ["The provider's setting", "Ask first", "Accept edits", "Bypass permissions"]);
    await evaluate(`(() => { for (const [id, value] of [["session-agent", "plan"], ["session-mode", "acceptEdits"]]) {
      const select = document.getElementById(id); select.value = value; select.dispatchEvent(new Event("change", { bubbles: true })); } })()`);
    await waitFor(`document.getElementById('session-mode-text').textContent === "Accept file edits; ask before commands."`, "what the mode does");
    await shot("04-options-sheet-dark.png");
    await click("#session-more-done");
    await waitFor(`!(${shown("#session-more-sheet")})`, "the options closed");
    assert.equal(await evaluate("document.getElementById('pick-more-text').textContent.trim()"), "Plan", "⋯ says the agent prompt that is not the default one");
    assert.match(await evaluate<string>("document.getElementById('pick-more').getAttribute('aria-label')"), /agent prompt Plan, permissions Accept edits, for the next prompt/);

    // Send: the prompt leaves the composer and its draft, with what was picked; then the session runs on it.
    await typeInto("#prompt", "Add a test");
    await click("#send");
    await waitUntil(() => count("POST", "/api/sessions/s1/prompt") === 1, "the prompt sent");
    assert.deepEqual(server.requests.find(r => r.path === "/api/sessions/s1/prompt")?.body,
      { text: "Add a test", mode: "send", model: "gpt-6.1-argon", reasoning: "medium", agent: "plan", permissionMode: "acceptEdits" });
    await waitFor(`!document.getElementById('pick-model').classList.contains('pending') && !document.getElementById('pick-more').classList.contains('pending')`, "nothing waits any more");
    assert.equal(await evaluate("document.getElementById('pick-model-text').textContent"), "GPT-6.1 Argon · medium", "the session's now");
    await waitUntil(() => count("GET", "/api/sessions/s1/choices") >= 2, "the choices read again after the send");
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
    server.state.events.get("s1")!.push(event("user.message", "Use the fast path"));
    server.push({ id: "s1", projectId: "p1", live: "running", kind: "user", line: "Use the fast path", at: new Date().toISOString(), activity: null, canSteer: true });
    await waitFor(`!document.querySelector("#timeline > li.pending") && [...document.querySelectorAll("#timeline > li.msg-user")].at(-1)?.textContent.includes("Use the fast path")`, "the journal's message in its place");

    // Queue rather than steer, chosen in "⋯": Queue becomes the primary action, Steer the second one; a pick goes with the queue.
    await click("#pick-more");
    await waitFor(shown("#session-more-sheet"), "the options of the session");
    await click("#session-queue-first");
    await click("#session-more-done");
    assert.equal(await evaluate("localStorage.getItem('alta.queueFirst')"), "1", "kept on the phone");
    await typeInto("#prompt", "Then the docs");
    assert.deepEqual([await evaluate("document.querySelector('#send span').textContent"), await evaluate("document.querySelector('#queue span').textContent")], ["Queue", "Steer"]);
    await click("#pick-model");
    await waitFor(shown("#model-sheet"), "the model sheet");
    await evaluate(`[...document.querySelectorAll("#sheet-effort .reasoning-labels button")].find(b => b.textContent === "xhigh").click()`);
    await click("#model-done");
    await evaluate("document.getElementById('composer').requestSubmit()");
    await waitUntil(() => count("POST", "/api/sessions/s1/prompt") === 3, "the prompt queued");
    assert.deepEqual(server.requests.filter(r => r.path === "/api/sessions/s1/prompt").at(-1)?.body, { text: "Then the docs", mode: "queue", reasoning: "xhigh" });
    await waitFor(`[...document.querySelectorAll("#timeline > li.pending .msg-label")].some(e => e.textContent === "You · queued")`, "the queued prompt");
    server.state.events.get("s1")!.push(event("user.message", "Then the docs"));
    await click("#pick-more");
    await click("#session-queue-first");
    await click("#session-more-done");

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

    // Several projects can be checked: the list keeps to them once the sheet closes, and no chip takes room above it.
    assert.equal(await evaluate("!!document.getElementById('chip')"), false, "no chip of the project");
    const row = (project: string) => `document.querySelector('#sheet-list button[data-project="${project}"]')`;
    await click("#filter");
    await waitFor(shown("#sheet"), "the project sheet");
    await evaluate(`${row("p2")}.click()`);
    await evaluate(`${row("p1")}.click()`);
    assert.deepEqual(await evaluate<string[]>("[...document.querySelectorAll('#sheet-list button[aria-pressed=\"true\"]')].map(b => b.dataset.project)"), ["p1", "p2"],
      "both checked, the rows where they were");
    assert.equal(await evaluate("document.querySelector('#sheet-list li:first-child button').getAttribute('aria-pressed')"), "false", "All projects is no longer checked");
    const listReads = count("GET", "/api/sessions");
    await command("Input.dispatchKeyEvent", { type: "keyDown", key: "Escape", code: "Escape", windowsVirtualKeyCode: 27 });
    await command("Input.dispatchKeyEvent", { type: "keyUp", key: "Escape", code: "Escape", windowsVirtualKeyCode: 27 });
    await waitUntil(() => count("GET", "/api/sessions") > listReads, "the list read again for the projects");
    assert.match(server.requests.filter(r => r.path === "/api/sessions").at(-1)?.query ?? "", /^\?offset=0&limit=\d+&project=p2&project=p1$/);
    await waitFor(`document.querySelector('.row[data-id="s1"]') && document.querySelector('.row[data-id="s2"]')`, "the sessions of both projects");
    assert.match(await evaluate<string>("document.getElementById('filter').getAttribute('aria-label')"), /^Filter by project: site, parser$/);
    // Unchecking one keeps to the other; the checked one comes first when the sheet opens again.
    await click("#filter");
    await waitFor(shown("#sheet"), "the project sheet");
    await evaluate(`${row("p2")}.click()`);
    await command("Input.dispatchKeyEvent", { type: "keyDown", key: "Escape", code: "Escape", windowsVirtualKeyCode: 27 });
    await command("Input.dispatchKeyEvent", { type: "keyUp", key: "Escape", code: "Escape", windowsVirtualKeyCode: 27 });
    await waitFor(`!document.querySelector('.row[data-id="s2"]') && document.querySelector('.row[data-id="s1"]')`, "the sessions of parser only");
    await click("#filter");
    await waitFor(shown("#sheet"), "the project sheet");
    assert.deepEqual(await evaluate<string[]>("[...document.querySelectorAll('#sheet-list button[data-project]')].map(b => b.dataset.project)"), ["p1", "p2"],
      "the checked project first");
    // All projects unchecks them all, and closes the sheet.
    await evaluate("document.querySelector('#sheet-list li:first-child button').click()");
    await waitFor(`!(${shown("#sheet")}) && document.querySelector('.row[data-id="s2"]')`, "every project again");
    assert.equal(await evaluate("document.querySelector('#filter .badge').hidden"), true);

    // A new session: the default tier of the settings; a prompt that did not go is back in the composer of its session.
    // The screen is the project, the prompt, and one line of chips beside Start.
    await click("#new");
    await waitFor(`document.getElementById('new-model-name').textContent === "Standard · GPT-6.1 · medium"`, "the default tier");
    const bar = await evaluate<{ chip: number; more: number; start: number; height: number }>(`(() => { const r = e => document.getElementById(e).getBoundingClientRect();
      return { chip: r("new-model").top, more: r("new-more").top, start: r("new-start").top, height: r("create-bar").height }; })()`);
    assert.ok(bar.chip === bar.start && bar.more === bar.start, `the chips are on the line of Start (${JSON.stringify(bar)})`);
    await click("#new-model");
    await waitFor(shown("#model-sheet"), "the model sheet");
    assert.deepEqual(await evaluate("[...document.querySelectorAll('#tiers .tier')].map(b => [b.textContent, b.disabled])"),
      [["ComplexOpus 4.7", false], ["StandardGPT-6.1", false], ["QuickNo model is set for this tier.", true]]);
    await click("#tiers .tier");
    await waitFor(`document.getElementById('new-model-name').textContent === "Complex · Opus 4.7 · high"`, "the complex tier");
    assert.deepEqual(await texts("#sheet-effort .reasoning-labels button"), ["low", "high"], "the efforts of the tier's model");
    await click("#model-done");
    assert.equal(await evaluate("document.getElementById('new-space').textContent"), "Work", "the space the session goes to, beside its project");
    assert.ok(await evaluate<boolean>("document.getElementById('new-start').getBoundingClientRect().bottom <= innerHeight"), "Start is on the screen without scrolling");
    server.state.createAnswer = { sendError: "The provider is busy." };
    await typeInto("#new-text", "Make it faster");
    await shot("05-new-session-dark.png");
    // "⋯": the agent prompt, the provider, the permissions its provider offers, a worktree.
    await click("#new-more");
    await waitFor(shown("#new-more-sheet"), "the options of the new session");
    assert.equal(await evaluate("document.getElementById('new-provider').value"), "claude");
    assert.deepEqual(await evaluate("[...document.querySelectorAll('#new-mode option')].map(o => o.value)"), ["", "default", "acceptEdits", "bypassPermissions"]);
    await evaluate(`(() => { const select = document.getElementById("new-mode"); select.value = "acceptEdits"; select.dispatchEvent(new Event("change", { bubbles: true })); })()`);
    await click("#new-worktree");
    await shot("06-new-options-dark.png");
    await click("#new-more-done");
    assert.match(await evaluate<string>("document.getElementById('new-more').getAttribute('aria-label')"), /new worktree, permissions Accept edits/);
    assert.equal(await evaluate("JSON.parse(localStorage.getItem('codealta.mobile.newSessionPreferences.v1')).p1.permissionMode"), "acceptEdits", "remembered for the project");
    await evaluate("document.getElementById('new-form').requestSubmit()");
    await waitFor(`location.hash === "#/session/new1"`, "the page of the new session");
    assert.deepEqual(server.requests.find(r => r.method === "POST" && r.path === "/api/sessions")?.body,
      { project: "p1", text: "Make it faster", prompt: "default", tier: "complex", permissionMode: "acceptEdits", worktree: true });
    await waitFor(`document.getElementById('detail-note').textContent.includes("not sent: The provider is busy.")`, "the failed send on its page");
    assert.equal(await evaluate("document.getElementById('prompt').value"), "Make it faster");
    await waitFor(`document.querySelector('#detail-title').textContent === "New: Make it faster"`, "a session out of the list read alone");

    // The page fits the phone: nothing wider than the screen.
    for (const hash of ["#/", "#/session/s1", "#/new"]) {
      await evaluate(`location.hash = ${JSON.stringify(hash)}`);
      await new Promise(resolve => setTimeout(resolve, 150));
      assert.ok(await evaluate<boolean>("document.documentElement.scrollWidth <= window.innerWidth"), `no horizontal overflow on ${hash}`);
    }

    // The light theme: the page of a session at rest, and the new-session screen.
    await click("#theme");
    await evaluate("location.hash = '#/session/s1'");
    await waitFor(`document.querySelector('#detail-title').textContent === "Fix the parser" && document.getElementById('pick-model-text').textContent === "GPT-6.1 Argon · xhigh"`, "the session in light");
    await typeInto("#prompt", "");
    await evaluate("window.scrollTo(0, document.documentElement.scrollHeight)");
    await shot("07-session-light.png");
    await evaluate("location.hash = '#/new'");
    await waitFor(shown("#new-model"), "the new-session screen in light");
    await typeInto("#new-text", "Write the release notes of the parser");
    await shot("08-new-session-light.png");
    await typeInto("#new-text", "");
    await click("#theme");
    await evaluate("location.hash = '#/'");

    // An expired cookie signs in again with the kept token; a reset token says the phone is no longer connected.
    server.state.cookies.clear();
    const logins = count("POST", "/api/login");
    await click("#refresh");
    await waitUntil(() => count("POST", "/api/login") === logins + 1, "a new sign-in with the kept token");
    assert.deepEqual(server.requests.filter(r => r.path === "/api/login").at(-1)?.body, { token: "key1" }, "with the key of the phone");
    await waitFor(`document.querySelectorAll("#sessions .row").length >= 2`, "the list after signing in again");
    server.state.cookies.clear();
    server.state.token = "reset";
    server.state.keys.clear();
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
