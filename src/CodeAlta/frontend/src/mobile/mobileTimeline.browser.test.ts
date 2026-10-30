import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { mkdir, mkdtemp, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";
import { crc32, deflateSync } from "node:zlib";
import { build } from "esbuild";
import { browserBaseArgs, browserExecutable, endBrowser, waitForBrowserPort } from "../browserTarget";
import type { Ask, TimelineEvent, WorkflowFacts } from "./contract";
import { pageFiles, session, startFixtureServer } from "./fixtureServer";

const edge = browserExecutable;
// Where the pictures of this test go, when a person wants to look at them.
const shots = process.env.CODEALTA_SHOTS ?? "";

/** A PNG of a size, each pixel drawn by a function: RGB, 8 bits. */
function png(width: number, height: number, pixel: (x: number, y: number) => readonly [number, number, number]): Buffer {
  const rows = Buffer.alloc((width * 3 + 1) * height);
  for (let y = 0; y < height; y++) {
    rows[y * (width * 3 + 1)] = 0;
    for (let x = 0; x < width; x++) rows.set(pixel(x, y), y * (width * 3 + 1) + 1 + x * 3);
  }
  const chunk = (type: string, data: Buffer) => {
    const head = Buffer.alloc(8);
    head.writeUInt32BE(data.length, 0);
    head.write(type, 4, "ascii");
    const sum = Buffer.alloc(4);
    sum.writeUInt32BE(crc32(Buffer.concat([head.subarray(4), data])) >>> 0, 0);
    return Buffer.concat([head, data, sum]);
  };
  const header = Buffer.alloc(13);
  header.writeUInt32BE(width, 0); header.writeUInt32BE(height, 4); header[8] = 8; header[9] = 2;
  return Buffer.concat([Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]), chunk("IHDR", header), chunk("IDAT", deflateSync(rows)), chunk("IEND", Buffer.alloc(0))]);
}

// A screenshot of a sign-in page, a picture a tool gave the model, and a chart an answer shows.
const login = png(270, 480, (x, y) => y < 56 ? [45, 114, 210] : y > 150 && y < 190 && x > 30 && x < 240 ? [220, 224, 229] : y > 210 && y < 250 && x > 30 && x < 240 ? [220, 224, 229]
  : y > 280 && y < 320 && x > 30 && x < 240 ? [35, 133, 81] : [250, 250, 252]);
const screenshot = png(480, 300, (x, y) => [Math.round(40 + x / 3), Math.round(60 + y / 2), 170]);
const chart = png(400, 250, (x, y) => {
  const bar = Math.floor((x - 30) / 70), inBar = x > 30 && (x - 30) % 70 < 46 && bar < 5;
  const height = [120, 180, 90, 200, 150][bar] ?? 0;
  return inBar && y > 230 - height && y < 230 ? [[76, 144, 240], [50, 164, 103], [240, 183, 38], [231, 106, 110], [152, 129, 243]][bar] as [number, number, number]
    : y === 230 ? [120, 120, 120] : [255, 255, 255];
});

const question: Ask = {
  id: "ask-wf", revision: "0", state: "pending", immediate: true, review: null,
  questions: [{ title: "Cache", question: "Which cache should the service use?", description: null,
    choices: [{ title: "Redis", description: "Shared, fast" }, { title: "In-process LRU", description: "No server" }], freeform: null }],
};

test("the phone timeline shows what the host sent as what it is, images, and the runs of scripts", { skip: !edge, timeout: 120_000 }, async () => {
  const directory = await mkdtemp(join(tmpdir(), "codealta-mobile-timeline-"));
  let browser: ReturnType<typeof spawn> | undefined, socket: WebSocket | undefined;
  const bundle = await build({ entryPoints: [fileURLToPath(new URL("./main.ts", import.meta.url))], bundle: true, format: "esm", platform: "browser",
    write: false, outdir: join(directory, "out"), external: ["pele/lazy"], loader: { ".woff2": "file", ".woff": "file", ".ttf": "file" }, define: { "process.env.NODE_ENV": '"production"' }, logLevel: "silent" });
  const script = bundle.outputFiles.find(file => file.path.endsWith(".js"))!.text, style = bundle.outputFiles.find(file => file.path.endsWith(".css"))!.text;
  const server = await startFixtureServer(await pageFiles(script, style));
  let seq = 0;
  const start = Date.now() - 10 * 60_000;
  const at = (seconds: number) => new Date(start + seconds * 1000).toISOString();
  const event = (kind: string, text: string | null, seconds: number, extra: Partial<TimelineEvent> = {}): TimelineEvent =>
    ({ seq: ++seq, at: at(seconds), kind, text, activityId: null, ...extra });
  const run = (runId: string, callId: string) => (kind: string, seconds: number, facts: Partial<WorkflowFacts> = {}, text: string | null = null) =>
    event(`workflow.${kind}`, text, seconds, { workflow: { runId, callId, ...facts } });
  const done = run("run-7f3c2a", "exec-1");
  const declared = { steps: [{ id: "research", title: "Research each option" }, { id: "compare", title: "Compare" }, { id: "report", title: "Write the report" }] };
  const job = "[CodeAlta background job]\nJob: job-42\nTitle: Run the parser tests\nCommand: dotnet test -c Debug --filter Parser\nResult: succeeded (exit code 0) after 26 s\n"
    + "This message comes from CodeAlta.\n\nOutput:\n```\nTest run for Parser.Tests.dll (.NETCoreApp,Version=v10.0)\nPassed!  - Failed: 0, Passed: 214, Skipped: 0, Total: 214, Duration: 3 s - Parser.Tests.dll (net10.0)\n```";
  server.state.sessions = [
    session("t1", "Compare the caches", "p1", 1),
    session("s2", "Write the docs", "p2", 30),
    session("t2", "Pick a cache", "p1", 0, { live: "tools", kind: "activity", line: "Calling tools", canSteer: true, asking: { id: question.id, revision: question.revision, state: "pending", immediate: true } }),
    session("s-redis", "Research: Redis", "p1", 5, {}, { parentId: "t1" }),
  ];
  server.state.events.set("t1", [
    event("user.message", "Why does the login page look broken?", 0, { attachments: [{ title: "login.png", mediaType: "image/png", id: "20261027-01-login.png" }, { title: "notes.txt", mediaType: "text/plain", id: "20261027-02-notes.txt" }] }),
    event("activity.turn.started", "turn", 1),
    event("assistant.reasoning", "**Looking at the screenshot**", 2),
    event("activity.toolcall.started", "view_image", 3, { activityId: "view-1" }),
    event("activity.toolcall.completed", "view_image", 4, { activityId: "view-1" }),
    event("tool.output", "", 4, { parentId: "view-1", images: [{ title: "Rendered page", mediaType: "image/png", id: "20261027-tool-render.png" }] }),
    event("activity.toolcall.started", "exec", 5, { activityId: "exec-1" }),
    done("runStarted", 5, { title: "Compare caches" }),
    done("stepsDeclared", 5, declared),
    done("stepStarted", 6, { stepId: "research" }),
    done("sessionStarted", 6, { stepId: "research", childSessionId: "s-redis", title: "Research: Redis" }),
    event("activity.toolcall.started", "read_file", 7, { activityId: "inner-1", parentId: "exec-1" }),
    event("activity.toolcall.completed", "read_file", 8, { activityId: "inner-1", parentId: "exec-1" }),
    done("sessionEnded", 40, { childSessionId: "s-redis", status: "completed", durationMs: 34_000 }),
    done("stepEnded", 41, { stepId: "research", status: "completed", durationMs: 35_000 }),
    done("stepStarted", 41, { stepId: "compare" }),
    done("stepEnded", 52, { stepId: "compare", status: "completed", durationMs: 11_000 }),
    done("stepStarted", 52, { stepId: "report" }),
    done("stepEnded", 60, { stepId: "report", status: "completed", durationMs: 8_000 }),
    done("runEnded", 60, { status: "completed", durationMs: 55_000 }),
    event("activity.toolcall.completed", "exec", 60, { activityId: "exec-1" }),
    event("assistant.message", "The login form overflows at 390 px. Here is the measure of each breakpoint:\n\n![Width of the form at each breakpoint](docs/chart.png)\n\n"
      + "| Breakpoint | Width | Overflow | Font | Padding | Margin | Notes |\n|---|---|---|---|---|---|---|\n| phone | 390 px | 42 px | 15 px | 16 px | 8 px | The submit button wraps |\n\n"
      + "```css\n.login-form { width: calc(100vw - 2 * var(--page-padding-inline-start-and-end-on-small-screens)); }\n```", 61,
      { images: [{ id: "0", title: "Width of the form at each breakpoint", src: "docs/chart.png" }] }),
    event("activity.turn.completed", "turn", 62),
    event("user.message", job, 100, { provenance: "background-job" }),
    event("user.message", "Redis wins on latency; the in-process LRU wins on cost. Details are in `docs/cache.md`.", 120, { provenance: "agent-session", sourceSessionId: "s2" }),
    event("user.message", "Check that the nightly build is green before you merge.", 140, { provenance: "reminder" }),
    event("user.message", "Script run-9d1e (Nightly report) ended:\nScript completed · 2.0 s · 3 calls\nPresented to the user (they saw it on the card of the run; do not repeat it):\n````markdown\n## Nightly\nAll **3** checks passed.\n````\nResult:\n```json\n{ \"checks\": 3, \"source\": \"// <summary> *not* Markdown\" }\n```", 160, { provenance: "workflow" }),
    // Last, so that the numbers of the events before stay those the fixture serves pictures by.
    done("presented", 59, { stepId: "report" }, "## Report\nRedis wins on **latency**."),
  ]);
  const live = run("run-b2", "exec-2");
  server.state.events.set("t2", [
    event("user.message", "Pick the cache with a script, and ask me before you decide.", 0),
    event("activity.turn.started", "turn", 1),
    event("activity.toolcall.started", "exec", 2, { activityId: "exec-2" }),
    live("runStarted", 2, { title: "Pick a cache" }),
    live("stepsDeclared", 2, declared),
    live("stepStarted", 3, { stepId: "research" }),
    live("sessionStarted", 3, { stepId: "research", childSessionId: "s-redis", title: "Research: Redis" }),
    live("stepEnded", 30, { stepId: "research", status: "completed", durationMs: 27_000 }),
    live("stepStarted", 31, { stepId: "compare" }),
    live("waitStarted", 32, { stepId: "compare", status: "question", title: "Which cache should the service use?" }),
  ]);
  server.state.asks.set("t2", question);
  for (const [key, body] of [["t1/20261027-01-login.png", login], ["t1/20261027-tool-render.png", screenshot], [`t1/linked/22/0`, chart]] as const)
    server.state.images.set(key, { type: "image/png", body });
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
    const click = (selector: string) => evaluate(`document.querySelector(${JSON.stringify(selector)}).click()`);
    const texts = (selector: string) => evaluate<string[]>(`[...document.querySelectorAll(${JSON.stringify(selector)})].map(e => e.textContent)`);
    const shot = async (name: string) => {
      if (!shots) return;
      await mkdir(shots, { recursive: true });
      await new Promise(resolve => setTimeout(resolve, 150));
      await writeFile(join(shots, name), Buffer.from(((await command("Page.captureScreenshot", { format: "png" })) as { data: string }).data, "base64"));
    };
    const theme = async (wanted: "light" | "dark") => { if (await evaluate<string>("document.documentElement.dataset.theme") !== wanted) await click("#theme"); };
    // Nothing is wider than the phone: the page never scrolls sideways.
    const noOverflow = async (what: string) => {
      const widths = await evaluate<{ page: number; view: number; wide: string[] }>(`(() => ({ page: document.documentElement.scrollWidth, view: innerWidth,
        wide: [...document.querySelectorAll("#timeline *")].filter(e => e.getBoundingClientRect().right > innerWidth + 1 && !e.closest("pre, table, .markdown-content table")).slice(0, 5).map(e => e.className || e.tagName) }))()`);
      assert.ok(widths.page <= widths.view && widths.wide.length === 0, `${what}: ${JSON.stringify(widths)}`);
    };

    await command("Runtime.enable"); await command("Log.enable"); await command("Page.enable");
    await command("Emulation.setDeviceMetricsOverride", { width: 390, height: 844, deviceScaleFactor: shots ? 3 : 2, mobile: true });
    await command("Page.navigate", { url: `${server.origin}/#k=${server.state.token}` });
    await waitFor(`document.querySelectorAll("#sessions .row").length >= 3`, "the list of sessions");
    if (await evaluate<boolean>(`!!document.querySelector("#install-bar:not([hidden])")`)) await click("#install-later");

    // ---- A session whose timeline has the host's prompts, images and a run of a script ----
    await evaluate(`location.hash = "#/session/t1"`);
    await waitFor(`document.querySelectorAll("#timeline > li").length === 7`, "the timeline of t1");
    assert.deepEqual(await evaluate("[...document.querySelectorAll('#timeline > li')].map(li => li.dataset.host ? 'host:' + li.dataset.host : li.className)"),
      ["msg msg-user", "activity", "msg msg-assistant", "host:job", "host:agent", "host:host", "host:workflow"]);
    assert.equal(await evaluate("document.querySelectorAll('.msg-user').length"), 1, "only the prompt the user typed is theirs");
    assert.ok(!(await evaluate<string>("[...document.querySelectorAll('.msg-user')].map(e => e.textContent).join()")).includes("[CodeAlta"), "no envelope as a prompt of the user");

    // The background job: one line, its title, a chip of how it ended; its command and output open on a tap.
    assert.equal(await evaluate("document.querySelector('[data-host=job] .host-name').textContent"), "Run the parser tests");
    assert.equal(await evaluate("document.querySelector('[data-host=job] .chip').textContent"), "26 s");
    assert.equal(await evaluate("document.querySelector('[data-host=job] .host-body')"), null, "folded");
    assert.ok(await evaluate<number>("document.querySelector('[data-host=job]').getBoundingClientRect().height") <= 48, "one line");
    await click("[data-host=job] .host-head");
    await waitFor(`document.querySelector("[data-host=job] .host-body pre")`, "the output of the job");
    assert.deepEqual(await texts("[data-host=job] .host-body pre code"), ["dotnet test -c Debug --filter Parser\n",
      "Test run for Parser.Tests.dll (.NETCoreApp,Version=v10.0)\nPassed!  - Failed: 0, Passed: 214, Skipped: 0, Total: 214, Duration: 3 s - Parser.Tests.dll (net10.0)\n"]);
    assert.ok(await evaluate<boolean>("!!document.querySelector('[data-host=job] .host-body pre .markdown-copy')"), "the output can be copied");
    assert.equal(await evaluate("document.querySelector('[data-host=job] .host-head').getAttribute('aria-expanded')"), "true");

    // The message of another agent says who sent it; the reminder and the result of a script are titled by what they are.
    assert.equal(await evaluate("document.querySelector('[data-host=agent] .host-source').textContent"), "Write the docs");
    assert.match(await evaluate<string>("document.querySelector('[data-host=agent] .host-preview').textContent"), /^Redis wins on latency/);
    assert.deepEqual(await texts("[data-host=host] .host-name, [data-host=workflow] .host-name"), ["Reminder", "Nightly report"]);
    // The result of a script of the background shows what it presented, whole, as an answer; its value is data, folded, as text.
    assert.deepEqual(await texts("[data-host=workflow] .host-body h2, [data-host=workflow] .host-body strong"), ["Nightly", "3"], "shown without a tap");
    assert.equal(await evaluate("document.querySelector('[data-host=workflow] .host-value').open"), false, "the value is folded");
    assert.match(await evaluate<string>("document.querySelector('[data-host=workflow] .host-value pre').textContent"), /"checks": 3.*<summary> \*not\* Markdown/s, "as it is, not as Markdown");

    // Images: the files of a prompt as pictures in fixed boxes, read by their id; a tap shows them full screen.
    assert.deepEqual(await evaluate("[...document.querySelectorAll('.msg-user .thumb img')].map(i => [new URL(i.src).pathname, i.alt, i.loading])"),
      [["/api/sessions/t1/images/20261027-01-login.png", "login.png", "lazy"]]);
    assert.deepEqual(await texts(".msg-user .msg-files .attach-chip"), ["notes.txt"], "a file that is no image stays a chip");
    await waitFor(`document.querySelector('.msg-user .thumb img').naturalWidth === 270`, "the picture read");
    assert.deepEqual(await evaluate("(() => { const r = document.querySelector('.msg-user .thumb').getBoundingClientRect(); return [r.width, r.height]; })()"), [88, 88]);
    // The images the tools gave the model show under the folded line of the activity; the run of its script too.
    assert.equal(await evaluate("document.querySelector('.activity').dataset.expanded"), "false");
    assert.equal(await evaluate("new URL(document.querySelector('.act-extras .tool-images .thumb img').src).pathname"), "/api/sessions/t1/images/20261027-tool-render.png");
    // A picture of the disk the answer shows: read by the number of the answer and its place, never by its path.
    await waitFor(`document.querySelector('.msg-assistant .md-image img')?.naturalWidth === 400`, "the picture of the answer");
    assert.equal(await evaluate("new URL(document.querySelector('.msg-assistant .md-image img').src).pathname"), "/api/sessions/t1/linked-images/22/0");
    assert.ok(server.requests.every(r => !r.path.includes("docs/chart.png") && !r.query.includes("chart")), "no path of the computer is asked for");
    await noOverflow("t1 with a wide table and a long line of code");

    // The run of the script: done, 3 of 3, which opens on its steps and the sessions it started; the calls of the script are not rows.
    assert.equal(await evaluate("document.querySelector('.wf-card').dataset.state"), "completed");
    assert.equal(await evaluate("document.querySelector('.wf-card .wf-progress').textContent"), "3/3");
    // What the script presented shows on its card, whole and without a tap, as it does on the desktop.
    assert.deepEqual(await texts(".wf-card .wf-presented h2, .wf-card .wf-presented strong"), ["Report", "latency"]);
    assert.match(await evaluate<string>("document.querySelector('.wf-card .wf-sub').textContent"), /^Done · 55s · 1 session$/);
    await click(".activity .act-toggle");
    await waitFor(`document.querySelector(".activity .act-body")`, "the rows of the activity");
    assert.deepEqual(await texts(".activity .act-body .tool-row strong"), ["view_image", "exec"], "the exec call is one line; the calls of its script are the script's");
    await click(".wf-card .wf-head");
    await waitFor(`document.querySelectorAll(".wf-card .wf-step").length === 3`, "the steps of the run");
    assert.deepEqual(await evaluate("[...document.querySelectorAll('.wf-card .wf-step')].map(s => [s.dataset.state, s.querySelector('.wf-step-title').textContent, s.querySelector('.wf-step-time').textContent])"),
      [["completed", "Research each option", "35s"], ["completed", "Compare", "11s"], ["completed", "Write the report", "8s"]]);
    assert.deepEqual(await texts(".wf-card .wf-session .wf-session-title"), ["Research: Redis"]);
    await theme("light");
    await evaluate(`document.querySelector('.msg-user').scrollIntoView({ block: "start" })`);
    await shot("t1-light-top.png");
    await theme("dark");
    await shot("t1-dark-top.png");
    await evaluate(`document.querySelector('[data-host=job]').scrollIntoView({ block: "center" })`);
    await shot("t1-dark-host-cards.png");
    await theme("light");
    await shot("t1-light-host-cards.png");

    // The viewer: full screen, the image whole with its title; Escape closes it.
    await click(".msg-user .thumb");
    await waitFor(`document.querySelector(".img-viewer img")?.naturalWidth === 270`, "the image full screen");
    assert.deepEqual(await evaluate("[document.querySelector('.img-viewer').getAttribute('role'), document.querySelector('.img-viewer img').alt, document.querySelector('.img-viewer-title').textContent, document.activeElement.className]"),
      ["dialog", "login.png", "login.png", "icon-btn img-viewer-close"]);
    await theme("dark");
    await shot("viewer.png");
    await evaluate(`document.dispatchEvent(new KeyboardEvent("keydown", { key: "Escape", bubbles: true }))`);
    await waitFor(`!document.querySelector(".img-viewer")`, "the viewer closed");
    // The pictures of the answer are a group of their own: a tap opens them.
    await click(".msg-assistant .md-image");
    await waitFor(`document.querySelector(".img-viewer-title")?.textContent === "Width of the form at each breakpoint"`, "the picture of the answer full screen");
    await click(".img-viewer-close");
    await waitFor(`!document.querySelector(".img-viewer")`, "the viewer closed by its button");
    assert.equal(await evaluate("location.hash"), "#/session/t1", "Back took the entry of the viewer only");

    // The largest text: still nothing wider than the phone.
    for (let step = 0; step < 6 && await evaluate<string>("document.documentElement.dataset.fontStep ?? ''") !== "5"; step++) await click("#font");
    assert.equal(await evaluate("document.documentElement.dataset.fontStep"), "5");
    await noOverflow("t1 at the largest text");
    await shot("t1-dark-largest-text.png");
    for (let step = 0; step < 6 && await evaluate<string>("document.documentElement.dataset.fontStep ?? ''") !== "2"; step++) await click("#font");

    // The session of the message of an agent opens on a tap.
    await click("[data-host=agent] .host-source");
    await waitFor(`location.hash === "#/session/s2"`, "the session that sent the message");

    // ---- A run that waits for the user: its card says so, and the ask of its script is the ask card ----
    await evaluate(`location.hash = "#/session/t2"`);
    await waitFor(`document.querySelector('.wf-card')`, "the card of the running script");
    assert.equal(await evaluate("document.querySelector('.wf-card').dataset.state"), "waiting");
    assert.equal(await evaluate("document.querySelector('.wf-card .wf-progress').textContent"), "1/3");
    assert.equal(await evaluate("document.querySelector('.wf-card .wf-sub').textContent"), "Waits for your answer");
    await waitFor(`document.querySelector("#ask:not([hidden]) .ask-card")`, "the question of the script");
    await click(".wf-card .wf-head");
    await waitFor(`document.querySelectorAll(".wf-card .wf-step").length === 3`, "the steps of the running run");
    assert.deepEqual(await evaluate("[...document.querySelectorAll('.wf-card .wf-step')].map(s => s.dataset.state)"), ["completed", "waiting", "pending"]);
    await noOverflow("t2");
    await shot("t2-dark-waiting.png");
    await theme("light");
    await shot("t2-light-waiting.png");
    // A session the script started opens on a tap.
    await click(".wf-card .wf-session");
    await waitFor(`location.hash === "#/session/s-redis"`, "the session the script started");

    assert.deepEqual(failures, [], "no error, no policy violation");
  } finally {
    try { socket?.close(); } catch { /* already closed */ }
    await endBrowser(browser);
    await server.close();
    await rm(directory, { recursive: true, force: true, maxRetries: 5, retryDelay: 200 }).catch(() => { });
  }
});
