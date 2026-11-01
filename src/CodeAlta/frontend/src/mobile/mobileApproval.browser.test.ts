import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { mkdir, mkdtemp, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";
import { build } from "esbuild";
import { browserBaseArgs, browserExecutable, endBrowser, waitForBrowserPort } from "../browserTarget";
import { approvalArmDelay } from "./approvalPanel";
import type { ApprovalRequest } from "./contract";
import { pageFiles, session, startFixtureServer } from "./fixtureServer";

const edge = browserExecutable;
// Where the pictures of this test go, when a person wants to look at them.
const shots = process.env.CODEALTA_SHOTS ?? "";

const command: ApprovalRequest = {
  id: "11111111-1111-4111-8111-111111111111", kind: "commandExecution", command: `git push origin main ${"--force-with-lease ".repeat(40)}`, workingDirectory: "C:/code/parser",
  grantRoot: null, reason: "To publish the fix.", shortened: true, outsideSandbox: true, skill: null,
};
const files: ApprovalRequest = {
  id: "22222222-2222-4222-8222-222222222222", kind: "fileChange", command: null, workingDirectory: null, grantRoot: "C:/code/parser",
  reason: "The agent requested filesystem edits via replace_in_file: src/cache.ts", shortened: false, outsideSandbox: false, skill: null,
};
const mark = (requests: readonly ApprovalRequest[]) => ({ id: requests[0].id, count: requests.length });

test("the phone page shows what sessions ask permission for, and answers it", { skip: !edge, timeout: 120_000 }, async () => {
  const directory = await mkdtemp(join(tmpdir(), "codealta-mobile-approval-"));
  let browser: ReturnType<typeof spawn> | undefined, socket: WebSocket | undefined;
  const bundle = await build({ entryPoints: [fileURLToPath(new URL("./main.ts", import.meta.url))], bundle: true, format: "esm", platform: "browser",
    write: false, outdir: join(directory, "out"), external: ["pele/lazy"], loader: { ".woff2": "file", ".woff": "file", ".ttf": "file" }, define: { "process.env.NODE_ENV": '"production"' }, logLevel: "silent" });
  const script = bundle.outputFiles.find(file => file.path.endsWith(".js"))!.text, style = bundle.outputFiles.find(file => file.path.endsWith(".css"))!.text;
  const server = await startFixtureServer(await pageFiles(script, style));
  const both = [command, files];
  server.state.sessions = [
    session("s1", "Publish the parser", "p1", 2, { live: "tools", kind: "assistant", line: "Pushing.", canSteer: true, approving: mark(both) }),
    session("s2", "Write the docs", "p2", 30),
  ];
  server.state.approvals.set("s1", both);
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
    const send = (method: string, params: object = {}) => new Promise<Reply>((resolve, reject) => {
      const id = ++sequence;
      const timer = setTimeout(() => { pending.delete(id); reject(new Error(`${method} timeout`)); }, 10_000);
      pending.set(id, reply => { clearTimeout(timer); reply.error ? reject(new Error(JSON.stringify(reply.error))) : resolve(reply.result ?? {}); });
      socket!.send(JSON.stringify({ id, method, params }));
    });
    const evaluate = async <T,>(expression: string): Promise<T> => {
      const reply = await send("Runtime.evaluate", { expression, returnByValue: true, awaitPromise: true });
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
    const text = (selector: string) => evaluate<string | null>(`document.querySelector(${JSON.stringify(selector)})?.textContent ?? null`);
    const shown = (selector: string) => `(() => { const e = document.querySelector(${JSON.stringify(selector)}); return !!e && !e.closest("[hidden]") && e.getClientRects().length > 0; })()`;
    const enabled = (selector: string) => `(() => { const e = document.querySelector(${JSON.stringify(selector)}); return !!e && !e.disabled; })()`;
    const posts = () => server.requests.filter(r => r.method === "POST" && r.path === "/api/sessions/s1/permissions/resolve");
    const shot = async (name: string) => {
      if (!shots) return;
      await mkdir(shots, { recursive: true });
      await writeFile(join(shots, name), Buffer.from(((await send("Page.captureScreenshot", { format: "png" })) as { data: string }).data, "base64"));
    };
    const allow = '#approval [data-decision="allow_once"]', deny = '#approval [data-decision="deny"]';

    await send("Runtime.enable"); await send("Log.enable"); await send("Page.enable");
    await send("Emulation.setDeviceMetricsOverride", { width: 390, height: 844, deviceScaleFactor: shots ? 3 : 2, mobile: true });
    await send("Page.navigate", { url: `${server.origin}/#k=${server.state.token}` });
    await waitFor(`document.querySelectorAll("#sessions .row").length === 2`, "the list of sessions");
    if (await evaluate<boolean>(shown("#install-bar"))) await click("#install-later");

    // The list says which session waits, also while its turn runs.
    assert.equal(await text('.row[data-id="s1"] .waiting-badge'), "Waits for your approval");
    assert.equal(await text('.row[data-id="s2"] .waiting-badge'), null);
    await shot("01-list-approving.png");

    // The card shows the request above the prompt, whose Stop stays.
    await evaluate(`location.hash = "#/session/s1"`);
    await waitFor(shown("#approval .approval-card"), "the card of the request");
    assert.equal(await text("#approval .approval-question"), "Allow this command?");
    assert.equal(await text("#approval .approval-subject"), command.command);
    assert.equal(await text("#approval .approval-folder"), command.workingDirectory);
    assert.equal(await text("#approval .approval-reason"), command.reason);
    assert.equal(await evaluate(`[...document.querySelectorAll("#approval .approval-warning")].length`), 2, "outside the sandbox, and too long to show in full");
    assert.match(await evaluate<string>(`document.getElementById("approval").textContent`), /More requests are waiting/);
    assert.equal(await evaluate(shown("#composer #prompt")), true, "the prompt stays");
    assert.equal(await evaluate("document.documentElement.scrollWidth <= innerWidth"), true, "a long command does not widen the page");
    assert.equal(await evaluate(`(() => { const r = document.querySelector(${JSON.stringify(allow)}).getBoundingClientRect(); return r.top >= 0 && r.bottom <= innerHeight; })()`), true, "the choices stay in view");

    // A tap in the first moments answers nothing: it was meant for what showed before.
    await click(allow);
    await new Promise(resolve => setTimeout(resolve, 50));
    assert.equal(posts().length, 0, "the choices are off at first");
    await waitFor(enabled(allow), "the choices armed", approvalArmDelay + 4000);
    await shot("02-card-command.png");

    // A refused answer says why, and the request stays.
    server.state.approvalResult = { ok: false, status: "rejected", error: "This request was answered or ended meanwhile." };
    await click(allow);
    await waitFor(`document.querySelector("#approval .ask-note")?.textContent === "This request was answered or ended meanwhile."`, "why the answer was refused");
    assert.deepEqual(posts()[0].body, { requestId: command.id, decision: "allow_once" });
    server.state.approvalResult = null;

    // Allow once goes; the next request shows, with its choices off again.
    await waitFor(enabled(allow), "the choices after the refusal");
    await click(allow);
    await waitFor(`document.querySelector("#approval .approval-question")?.textContent === "Allow file changes under this folder?"`, "the next request");
    assert.equal(await evaluate(enabled(allow)), false, "a second tap does not allow the next request");
    assert.equal(await text("#approval .approval-subject"), files.grantRoot);
    assert.equal(await text("#approval .approval-reason"), files.reason);
    assert.equal(posts().length, 2);
    await waitFor(enabled(deny), "the choices of the next request", approvalArmDelay + 4000);
    await shot("03-card-files.png");

    // Deny goes too, and the card leaves.
    await click(deny);
    await waitFor(`document.getElementById("approval").hidden`, "the card gone");
    assert.deepEqual(posts()[2].body, { requestId: files.id, decision: "deny" });
    assert.equal(await evaluate(shown("#composer #prompt")), true);

    // A request that comes while the session is on screen shows from the live stream; one answered elsewhere leaves.
    const next = { ...files, id: "33333333-3333-4333-8333-333333333333" };
    server.state.approvals.set("s1", [next]);
    server.push({ ...server.state.sessions[0].live!, approving: mark([next]) });
    await waitFor(`document.querySelector("#approval .approval-card")?.dataset.request === ${JSON.stringify(next.id)}`, "the request of the live stream");
    server.state.approvals.set("s1", []);
    server.push({ ...server.state.sessions[0].live!, approving: null });
    await waitFor(`document.getElementById("approval").hidden`, "the request answered on the computer");

    // What a session sends is text, never markup.
    const marked = { ...next, id: "44444444-4444-4444-8444-444444444444", grantRoot: `<img src=x onerror="window.hacked=1">`, reason: "<b>bold</b>" };
    server.state.approvals.set("s1", [marked]);
    server.push({ ...server.state.sessions[0].live!, approving: mark([marked]) });
    await waitFor(shown("#approval .approval-card"), "the marked request");
    assert.equal(await evaluate(`document.querySelectorAll("#approval img, #approval b").length`), 0);
    assert.equal(await evaluate("window.hacked === undefined"), true);

    assert.deepEqual(failures, [], "no error, no policy violation");
  } finally {
    try { socket?.close(); } catch { /* already closed */ }
    await endBrowser(browser);
    await server.close();
    await rm(directory, { recursive: true, force: true, maxRetries: 5, retryDelay: 200 }).catch(() => { });
  }
});
