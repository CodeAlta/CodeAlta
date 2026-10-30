import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { mkdir, mkdtemp, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";
import { build } from "esbuild";
import { browserBaseArgs, browserExecutable, endBrowser, waitForBrowserPort } from "../browserTarget";
import type { Ask } from "./contract";
import { pageFiles, session, startFixtureServer } from "./fixtureServer";

const edge = browserExecutable;
// Where the pictures of this test go, when a person wants to look at them.
const shots = process.env.CODEALTA_SHOTS ?? "";

const questions: Ask = {
  id: "ask1", revision: "1", state: "pending", review: null,
  questions: [
    {
      title: "Database", question: "Which database should the parser cache use?", description: "Both are supported by the build.",
      choices: [{ title: "Postgres", description: "Shared with the service" }, { title: "SQLite", description: "A file next to the cache" }],
      freeform: { title: "Notes", placeholder: "Anything to add" },
    },
    { title: "Name", question: "What should the cache be called?", description: null, choices: [], freeform: { title: null, placeholder: null } },
  ],
};

// What the live stream says of the ask of a session.
const mark = (ask: Ask) => ({ id: ask.id, revision: ask.revision, state: ask.state, immediate: !!ask.immediate });

// A question of a script, which waits while its turn runs.
const scripted: Ask = {
  id: "ask4", revision: "0", state: "pending", immediate: true, review: null,
  questions: [{ title: "Deploy", question: "Deploy the cache now?", description: null, choices: [{ title: "Yes", description: null }, { title: "Later", description: null }], freeform: { title: null, placeholder: null } }],
};

const review: Ask = {
  id: "ask2", revision: "3", state: "pending", questions: [],
  review: { kind: "file", path: "docs/plan.md", title: null, message: "Here is the plan of the **cache**.", comments: 0 },
};

test("the phone page answers the questions and the review requests of agents as the desktop does", { skip: !edge, timeout: 120_000 }, async () => {
  const directory = await mkdtemp(join(tmpdir(), "codealta-mobile-ask-"));
  let browser: ReturnType<typeof spawn> | undefined, socket: WebSocket | undefined;
  const bundle = await build({ entryPoints: [fileURLToPath(new URL("./main.ts", import.meta.url))], bundle: true, format: "esm", platform: "browser",
    write: false, outdir: join(directory, "out"), external: ["pele/lazy"], define: { "process.env.NODE_ENV": '"production"' }, logLevel: "silent" });
  const script = bundle.outputFiles.find(file => file.path.endsWith(".js"))!.text, style = bundle.outputFiles.find(file => file.path.endsWith(".css"))!.text;
  const server = await startFixtureServer(await pageFiles(script, style));
  server.state.sessions = [
    session("s1", "Cache the parser", "p1", 2, { kind: "assistant", line: "Two questions first.", asking: mark(questions) }),
    session("s2", "Plan the cache", "p1", 5, { kind: "assistant", line: "The plan is ready.", asking: mark(review) }),
    session("s3", "Write the docs", "p2", 30),
    session("s4", "Deploy the cache", "p2", 40, { live: "running", kind: "assistant", line: "The script asks.", asking: mark(scripted) }),
  ];
  server.state.asks.set("s1", questions);
  server.state.asks.set("s2", review);
  server.state.asks.set("s4", scripted);
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
    const typeInto = (selector: string, text: string) => evaluate(`(() => { const box = document.querySelector(${JSON.stringify(selector)}); box.focus(); box.value = ${JSON.stringify(text)}; box.dispatchEvent(new Event("input", { bubbles: true })); })()`);
    const click = (selector: string) => evaluate(`document.querySelector(${JSON.stringify(selector)}).click()`);
    const text = (selector: string) => evaluate<string | null>(`document.querySelector(${JSON.stringify(selector)})?.textContent ?? null`);
    const texts = (selector: string) => evaluate<string[]>(`[...document.querySelectorAll(${JSON.stringify(selector)})].map(e => e.textContent)`);
    const shown = (selector: string) => `(() => { const e = document.querySelector(${JSON.stringify(selector)}); return !!e && !e.closest("[hidden]") && e.getClientRects().length > 0; })()`;
    const posts = (path: string) => server.requests.filter(r => r.method === "POST" && r.path === path);
    const fits = () => evaluate<boolean>("document.documentElement.scrollWidth <= innerWidth && (() => { const bar = document.getElementById('ask'); return bar.scrollWidth <= bar.clientWidth; })()");
    const shot = async (name: string) => {
      if (!shots) return;
      await mkdir(shots, { recursive: true });
      await writeFile(join(shots, name), Buffer.from(((await command("Page.captureScreenshot", { format: "png" })) as { data: string }).data, "base64"));
    };

    await command("Runtime.enable"); await command("Log.enable"); await command("Page.enable");
    await command("Emulation.setDeviceMetricsOverride", { width: 390, height: 844, deviceScaleFactor: shots ? 3 : 2, mobile: true });
    await command("Page.navigate", { url: `${server.origin}/#k=${server.state.token}` });
    await waitFor(`document.querySelectorAll("#sessions .row").length === 4`, "the list of sessions");
    if (await evaluate<boolean>(shown("#install-bar"))) await click("#install-later");

    // The list says which sessions wait for an answer, as the tabs of the desktop do.
    assert.equal(await text('.row[data-id="s1"] .waiting-badge'), "Waits for your answer");
    assert.equal(await text('.row[data-id="s3"] .waiting-badge'), null);
    assert.equal(await text('.row[data-id="s4"] .waiting-badge'), "Waits for your answer", "a question of a script, while its turn runs");
    await shot("01-list-asking.png");

    // The questions take the place of the composer: the badge, the steps, the question, its choices as numbered cards.
    await evaluate(`location.hash = "#/session/s1"`);
    await waitFor(shown("#ask .ask-card"), "the questions of the agent");
    assert.equal(await evaluate("document.getElementById('composer').hidden"), true, "no composer while the agent asks");
    assert.equal(await evaluate("!!document.querySelector('#ask #foot')"), true, "the footer goes with the bar");
    assert.equal((await text("#ask .ask-badge"))?.trim(), "Questions for you");
    assert.deepEqual(await texts("#ask .ask-step-title"), ["Database", "Name"]);
    assert.equal(await text("#ask .ask-question h4"), "Which database should the parser cache use?");
    assert.equal(await text("#ask .ask-question .detail"), "Both are supported by the build.");
    assert.deepEqual(await texts("#ask .ask-choice-number"), ["1", "2"]);
    assert.deepEqual(await evaluate("[...document.querySelectorAll('#ask .ask-choice')].map(c => c.hasAttribute('data-selected'))"), [true, false], "the first choice until another is picked");
    assert.equal(await text("#ask .ask-freeform > span"), "Notes");
    assert.equal(await text("#ask .ask-answer"), "Next");
    assert.equal(await evaluate("document.querySelector('#ask .ask-back').disabled"), true);
    assert.equal(await fits(), true, "nothing wider than the phone");
    await shot("02-question.png");

    // A choice, a note, then the next question; Back keeps what was answered.
    await click("#ask .ask-choice:nth-child(2) input");
    await waitFor(`document.querySelector('#ask .ask-choice:nth-child(2)').hasAttribute('data-selected')`, "the second choice picked");
    await typeInto('#ask textarea[data-field="text"]', "Keep it small");
    await click("#ask .ask-answer");
    await waitFor(`document.querySelector('#ask .ask-question h4')?.textContent === "What should the cache be called?"`, "the second question");
    assert.equal(await evaluate("document.activeElement?.dataset.field"), "text", "the focus in its field");
    assert.deepEqual(await evaluate("[...document.querySelectorAll('#ask [data-step]')].map(b => b.dataset.state)"), ["seen", "current"]);
    assert.equal(await text("#ask .ask-freeform > span"), "Answer");
    assert.equal(await evaluate(`document.querySelector('#ask textarea[data-field="text"]').maxLength`), 8192 - "Keep it small".length, "the answers share one limit");
    assert.equal(await text("#ask .ask-answer"), "Submit");
    await typeInto('#ask textarea[data-field="text"]', "parser-cache");
    await click("#ask .ask-back");
    await waitFor(`document.querySelector('#ask .ask-choice:nth-child(2)')?.hasAttribute('data-selected')`, "the first question as it was answered");
    assert.equal(await evaluate(`document.querySelector('#ask textarea[data-field="text"]').value`), "Keep it small");
    await click('#ask [data-step="1"]');
    await waitFor(`document.querySelector('#ask .ask-answer')?.textContent === "Submit"`, "the last question again");

    // Submit sends every answer, as the desktop does, and the composer comes back.
    await click("#ask .ask-answer");
    await waitFor(`document.getElementById('ask').hidden && !document.getElementById('composer').hidden`, "the composer after the answer");
    const answer = posts("/api/sessions/s1/ask/answer");
    assert.equal(answer.length, 1);
    const sent = answer[0].body as { actionId: string; askId: string; revision: string; answers: unknown[]; review?: unknown };
    assert.match(sent.actionId, /^[0-9a-f-]{36}$/);
    assert.deepEqual({ ...sent, actionId: "" }, {
      actionId: "", askId: "ask1", revision: "1",
      answers: [{ question: 0, choices: [1], text: "Keep it small" }, { question: 1, choices: [], text: "parser-cache" }],
    });
    assert.equal(await evaluate("!!document.querySelector('#composer #foot')"), true);

    // A new question while a turn runs waits for the turn to end, then takes the place of the composer.
    const next: Ask = { ...questions, id: "ask3", questions: [questions.questions[0]] };
    server.state.asks.set("s1", next);
    server.push({ ...server.state.sessions[0].live!, live: "running", asking: mark(next) });
    await new Promise(resolve => setTimeout(resolve, 400));
    assert.equal(await evaluate("document.getElementById('ask').hidden"), true, "not while the turn runs");
    server.push({ ...server.state.sessions[0].live!, live: "idle", asking: mark(next) });
    await waitFor(shown("#ask .ask-card"), "the new question once the turn ended");
    assert.equal((await text("#ask .ask-badge"))?.trim(), "A question for you");
    assert.equal(await evaluate("document.querySelectorAll('#ask .ask-steps').length"), 0, "one question has no steps");
    assert.equal(await text("#ask .ask-answer"), "Submit");

    // A question of a script is answered while its turn runs. An answer the computer still sends shows read-only; the session
    // does not take it, the ask waits again in a new revision, and the draft is still there to answer it again.
    await evaluate(`location.hash = "#/session/s4"`);
    await waitFor(shown("#ask .ask-card"), "the question of the script while the turn runs");
    assert.equal(await evaluate("document.getElementById('composer').hidden"), true);
    await click("#ask .ask-choice:nth-child(2) input");
    await typeInto('#ask textarea[data-field="text"]', "After the release");
    server.state.askResult = { ok: true, status: "submitting", pending: true, error: null };
    server.state.askAfter = { ...scripted, state: "submitting" };
    await click("#ask .ask-answer");
    await waitFor(shown('#ask .ask-notice[data-mode="sending"]'), "the answer on its way, read-only");
    assert.match(await text("#ask .ask-notice") ?? "", /Sending your answer/);
    assert.equal(await evaluate("document.getElementById('composer').hidden"), true, "no composer while the answer goes");
    const first = (posts("/api/sessions/s4/ask/answer")[0].body as { actionId: string }).actionId;
    const again: Ask = { ...scripted, revision: "1" };
    server.state.asks.set("s4", again);
    server.push({ ...server.state.sessions[3].live!, asking: mark(again) });
    await waitFor(shown("#ask .ask-answer"), "the question again, in its new revision");
    assert.equal(await text("#ask .ask-note"), "The session did not take the answer. Try again.");
    assert.equal(await evaluate(`document.querySelector('#ask textarea[data-field="text"]').value`), "After the release", "the draft is kept");
    assert.equal(await evaluate("document.querySelector('#ask .ask-choice:nth-child(2)').hasAttribute('data-selected')"), true);
    await click("#ask .ask-answer");
    await waitFor(`document.getElementById('ask').hidden && !document.getElementById('composer').hidden`, "the composer once the session took it");
    const retried = posts("/api/sessions/s4/ask/answer")[1].body as { actionId: string; revision: string };
    assert.equal(retried.revision, "1");
    assert.notEqual(retried.actionId, first, "a new answer for the new revision");

    // An answer whose outcome the computer cannot tell: the card says to check on the computer.
    const unknown: Ask = { ...scripted, id: "ask5", state: "indeterminate" };
    server.state.asks.set("s4", unknown);
    server.push({ ...server.state.sessions[3].live!, asking: mark(unknown) });
    await waitFor(shown('#ask .ask-notice[data-mode="unknown"]'), "the outcome to check on the computer");
    assert.match(await text("#ask .ask-notice") ?? "", /Check the session on the computer/);
    assert.equal(await fits(), true, "nothing wider than the phone");

    // A review request: its message, where its comments are written, and the verdict of the desktop.
    await evaluate(`location.hash = "#/session/s2"`);
    await waitFor(shown("#ask .ask-review-verdict"), "the review request");
    assert.equal((await text("#ask .ask-badge"))?.trim(), "Review requestedplan.md");
    assert.equal(await text("#ask .ask-review-message strong"), "cache");
    assert.match(await text("#ask .ask-review-where") ?? "", /on the computer/);
    assert.equal(await text("#ask .ask-review-counts"), "Your review · No comment yet");
    assert.deepEqual(await texts("#ask .review-verdict strong"), ["Approve", "Request changes", "Comment"]);
    assert.equal(await evaluate("document.querySelector('#ask .review-verdict[data-selected]')?.dataset.verdict"), "approved", "no comment: approval");
    assert.equal(await text("#ask .ask-answer"), "Submit review");
    await click('#ask input[data-verdict="commented"]');
    await waitFor(`document.querySelector('#ask .ask-answer').disabled`, "a comment-only review with nothing to say");
    assert.equal(await evaluate(shown("#ask .ask-review-hint")), true);
    await typeInto('#ask textarea[data-field="overall"]', "Fine, but name the tables.");
    await waitFor(`!document.querySelector('#ask .ask-answer').disabled`, "something to say");
    assert.equal(await fits(), true, "nothing wider than the phone");
    await shot("03-review.png");

    // An answer the session did not take says why, and the review stays to be answered.
    server.state.askResult = { ok: false, status: "rejected", error: "This question was answered or changed meanwhile." };
    await click("#ask .ask-answer");
    await waitFor(`document.querySelector('#ask .ask-note')?.textContent === "This question was answered or changed meanwhile."`, "why the answer did not go");
    assert.deepEqual((posts("/api/sessions/s2/ask/answer")[0].body as { review: unknown }).review, { verdict: "commented", overall: "Fine, but name the tables." });
    assert.equal(await evaluate(shown("#ask .ask-card")), true);

    // Cancel asks first, as the desktop does: keep answering, or leave without a response.
    await click("#ask .ask-cancel");
    await waitFor(shown("#ask-cancel-sheet"), "the confirmation of the cancel");
    assert.equal(await text("#ask-cancel-title"), "Cancel Ask");
    assert.equal(await evaluate("document.activeElement?.id"), "ask-cancel-keep", "the safe choice has the focus");
    await shot("04-cancel.png");
    await click("#ask-cancel-keep");
    await waitFor(`!(${shown("#ask-cancel-sheet")})`, "the confirmation closed");
    assert.equal(posts("/api/sessions/s2/ask/cancel").length, 0);
    await click("#ask .ask-cancel");
    await waitFor(shown("#ask-cancel-sheet"), "the confirmation again");
    await click("#ask-cancel-exit");
    await waitFor(`document.getElementById('ask').hidden && !document.getElementById('composer').hidden`, "the composer after the cancel");
    const cancel = posts("/api/sessions/s2/ask/cancel");
    assert.equal(cancel.length, 1);
    assert.deepEqual({ ...(cancel[0].body as object), actionId: "" }, { actionId: "", askId: "ask2", revision: "3" });

    assert.deepEqual(failures, [], "no error, no policy violation");
  } finally {
    try { socket?.close(); } catch { /* already closed */ }
    await endBrowser(browser);
    await server.close();
    await rm(directory, { recursive: true, force: true, maxRetries: 5, retryDelay: 200 }).catch(() => { });
  }
});
