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
    event("activity.toolcall.started", "Read", { activityId: "call-3", subject: "C:\\work\\skills\\SKILL.md" }),
    event("activity.toolcall.completed", "Read", { activityId: "call-3", subject: "C:\\work\\skills\\SKILL.md" }),
  ]);
  const longLine = "an output line that is much wider than the screen of a phone, ".repeat(6);
  const numbered = (word: string, lines: number) => Array.from({ length: lines }, (_, index) => `${word} ${index + 1}`).join("\n");
  // Three pages of the fixture server, and two.
  const buildOutput = `${numbered("built", 4000)}\n${longLine}`;
  server.state.toolOutputs.set("s1/call-1", buildOutput);
  server.state.tools.set("s1/call-1", {
    name: "Bash", phase: "started", command: "dotnet build -c Release", sessionFolder: "C:\\work\\worktrees\\calm-otter",
    output: buildOutput.slice(-2000), outputOffset: buildOutput.length - 2000, outputLength: buildOutput.length, error: null,
  });
  const fileOutput = numbered("line", 2500);
  server.state.toolOutputs.set("s1/call-3", fileOutput);
  server.state.tools.set("s1/call-3", { name: "Read", phase: "completed", output: fileOutput.slice(0, 2000), outputOffset: 0, outputLength: fileOutput.length, error: null });
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
    assert.equal(server.requests.filter(request => request.path.endsWith("/tools/call-1")).at(-1)?.query, `?since=1`, "from the first event of the call");
    assert.equal(await evaluate('document.querySelector(".tool-detail .tool-output-text").textContent'), `built 3997\nbuilt 3998\nbuilt 3999\nbuilt 4000\n${longLine}`, "the last lines of a command");
    assert.match(await evaluate<string>('document.querySelector(".tool-detail .tool-output-more").textContent'), /^Read all [\d,. ]+ characters$/u);
    assert.equal(await evaluate("document.documentElement.scrollWidth <= window.innerWidth"), true, "a long line stays inside its box, never wider than the page");
    await shot("tool-call");

    // ---- A small button copies a value whole, and says that it did; a long press takes the whole value ----
    const copyControl = (label: string) => `document.querySelector('.tool-detail .tool-copy[aria-label="Copy ${label}"]')`;
    // A browser without a person refuses the clipboard: this one holds what is written, through the same two ways of writing.
    await evaluate(`Object.defineProperty(navigator, "clipboard", { configurable: true, value: {
      writeText: async text => { window.copied = text; },
      write: async ([item]) => { window.copied = await (await item.getType("text/plain")).text(); },
    } })`);
    const copy = async (label: string) => {
      await evaluate('window.copied = "nothing yet"');
      await evaluate(`${copyControl(label)}.click()`);
      await waitFor(`${copyControl(label)}.dataset.state === "copied"`);
      return evaluate<string>("window.copied");
    };
    assert.equal(await copy("folder"), "C:\\work\\worktrees\\calm-otter");
    assert.equal(await copy("command"), "dotnet build -c Release");
    await waitFor(`${copyControl("folder")}.dataset.state === "idle"`);
    assert.equal(await evaluate(`getComputedStyle(document.querySelector(".tool-detail .tool-fact code")).userSelect`), "all");
    const buttonSizes = await evaluate<number[][]>('[...document.querySelectorAll(".tool-detail .tool-copy")].map(e => { const box = e.getBoundingClientRect(); return [box.width, box.height]; })');
    assert.ok(buttonSizes.length === 3 && buttonSizes.every(([width, height]) => width >= 44 && height >= 32), JSON.stringify(buttonSizes));
    const buttonTops = await evaluate<number[]>('[...document.querySelectorAll(".tool-detail .tool-copy")].map(e => e.getBoundingClientRect().top)');
    assert.ok(buttonTops.every((top, index) => !index || top - buttonTops[index - 1]! >= 32), `two buttons overlap: ${JSON.stringify(buttonTops)}`);

    // ---- A tap on those lines reads the output full screen, a page at a time, and Back closes it ----
    const pagesOf = (call: string) => server.requests.filter(request => request.path.endsWith(`/tools/${call}/output`));
    const shownText = () => evaluate<string>('document.querySelector(".output-viewer-text").textContent');
    const isHidden = (selector: string) => evaluate<boolean>(`document.querySelector(${JSON.stringify(selector)}).hidden`);
    const pageRead = async (length: number) => waitFor(`document.querySelector(".output-viewer-text")?.textContent.length === ${length} && !document.querySelector(".output-viewer-earlier").disabled`);
    assert.equal(pagesOf("call-1").length, 0, "no page is read until the output is opened");
    await evaluate('(() => { const open = document.querySelector(".tool-detail .tool-output"); open.focus(); open.click(); })()');
    await pageRead(16_000);
    assert.equal(pagesOf("call-1").at(-1)?.query, "?since=1", "where it says the most");
    assert.equal(await shownText(), buildOutput.slice(-16_000));
    assert.match(await evaluate<string>('document.querySelector(".output-viewer-title").textContent'), /^Bashdotnet build -c Release$/u, "says of which call");
    assert.deepEqual([await isHidden(".output-viewer-earlier"), await isHidden(".output-viewer-later")], [false, true], "the end of a command: only what comes before is left");
    assert.equal(await evaluate('(() => { const stage = document.querySelector(".output-viewer-stage"); return stage.scrollTop > 0 && stage.scrollTop + stage.clientHeight >= stage.scrollHeight - 2; })()'), true, "opens at the end");
    assert.equal(await evaluate('(() => { const text = document.querySelector(".output-viewer-text"); return text.scrollWidth <= text.clientWidth && document.documentElement.scrollWidth <= window.innerWidth; })()'), true, "a long line wraps");
    await shot("tool-output");
    const fromBottom = () => evaluate<number>('(() => { const stage = document.querySelector(".output-viewer-stage"); return stage.scrollHeight - stage.scrollTop; })()');
    const reading = await fromBottom();
    await evaluate('document.querySelector(".output-viewer-earlier").click()');
    await pageRead(32_000);
    assert.equal(pagesOf("call-1").at(-1)?.query, `?since=1&before=${buildOutput.length - 16_000}`);
    assert.equal(await shownText(), buildOutput.slice(-32_000), "what came before goes first");
    assert.ok(Math.abs(await fromBottom() - reading) <= 2, "the text being read did not move");
    await evaluate('document.querySelector(".output-viewer-earlier").click()');
    await pageRead(buildOutput.length);
    assert.equal(await shownText(), buildOutput, "the whole output, with nothing lost between its pages");
    assert.equal(await isHidden(".output-viewer-earlier"), true, "nothing more before it");
    const hash = await evaluate<string>("location.hash");
    await evaluate("history.back()");
    await waitFor('!document.querySelector(".output-viewer")');
    assert.equal(await evaluate("location.hash"), hash, "Back closes the output and stays on the session");
    assert.equal(await evaluate('document.activeElement.className'), "tool-output", "the keyboard goes back to what opened it");
    // Its copy reads all of it, from its start, though a command shows its end; it does not open the full-screen view.
    const pagesBefore = pagesOf("call-1").length;
    assert.equal(await copy("output"), buildOutput);
    assert.deepEqual(pagesOf("call-1").slice(pagesBefore).map(request => request.query), ["?since=1&offset=0", "?since=1&offset=16000", "?since=1&offset=32000"]);
    assert.equal(await evaluate('document.querySelector(".output-viewer")'), null);
    // What is open follows the call: once it ended, it says how.
    server.state.tools.set("s1/call-1", { name: "Bash", phase: "failed", command: "dotnet build -c Release", sessionFolder: "C:\\work\\worktrees\\calm-otter", exitCode: 1, failure: "It broke", output: "error CS0001", outputLength: 12, error: null });
    server.state.events.get("s1")!.push(event("activity.toolcall.failed", "Bash", { activityId: "call-1", subject: "dotnet build -c Release" }));
    server.push(running);
    await waitFor('[...document.querySelectorAll(".tool-detail .tool-fact")].some(e => e.textContent === "Exit code1")');
    assert.equal(await evaluate('document.querySelector(".tool-detail .tool-bad")?.textContent'), "1");
    assert.equal(await evaluate('[...document.querySelectorAll(".tool-detail .tool-text")].at(-1).textContent'), "error CS0001", "an output these lines hold is only shown");
    assert.equal(await evaluate('document.querySelector(".tool-detail .tool-output")'), null, "with nothing more to open");
    assert.equal(await copy("output"), "error CS0001", "what it shows is all there is to read");
    assert.equal(await evaluate(copyControl("exit code")), null, "a number is not worth a button");
    // A second tap folds it.
    await evaluate('document.querySelector("#timeline .tool-row[data-activity=call-1]").click()');
    await waitFor('!document.querySelector(".tool-detail")');
    assert.equal(await evaluate('document.querySelector("#timeline .tool-row[data-activity=call-1]").getAttribute("aria-expanded")'), "false");

    // ---- A file opens at its start, and what comes after is read on demand ----
    await evaluate('document.querySelector("#timeline .tool-row[data-activity=call-3]").click()');
    await waitFor('document.querySelector(".tool-detail .tool-output-text")');
    assert.equal(await evaluate('document.querySelector(".tool-detail .tool-output-text").textContent'), "line 1\nline 2\nline 3\nline 4\nline 5", "the first lines of a file");
    // The row is a button, whose path cannot be selected: it comes again on a line that can be.
    assert.deepEqual(await evaluate('[...document.querySelectorAll(".tool-detail .tool-fact")].map(e => e.textContent)'), ["PathC:\\work\\skills\\SKILL.md"]);
    assert.equal(await copy("path"), "C:\\work\\skills\\SKILL.md");
    assert.equal(await copy("output"), fileOutput);
    // A copy that fails says why, and leaves the clipboard as it was.
    const kept = server.state.toolOutputs.get("s1/call-3")!;
    server.state.toolOutputs.delete("s1/call-3");
    await evaluate('window.copied = "kept"');
    await evaluate(`${copyControl("output")}.click()`);
    await waitFor(`${copyControl("output")}.dataset.state === "failed"`);
    assert.equal(await evaluate('document.querySelector(".tool-detail .tool-note.tool-bad").textContent'), "Could not copy: The output was not read.");
    assert.equal(await evaluate("window.copied"), "kept");
    server.state.toolOutputs.set("s1/call-3", kept);
    await evaluate('document.querySelector(".tool-detail .tool-output").click()');
    await pageRead(16_000);
    assert.equal(await shownText(), fileOutput.slice(0, 16_000));
    assert.deepEqual([await isHidden(".output-viewer-earlier"), await isHidden(".output-viewer-later")], [true, false], "the start of a file: only what comes after is left");
    assert.equal(await evaluate('document.querySelector(".output-viewer-stage").scrollTop'), 0, "opens at the start");
    assert.match(await evaluate<string>('document.querySelector(".output-viewer-title .tool-path-file").textContent'), /^\\SKILL\.md$/u);
    await evaluate('document.querySelector(".output-viewer-later").click()');
    await waitFor(`document.querySelector(".output-viewer-text").textContent.length === ${fileOutput.length}`);
    assert.match(pagesOf("call-3").at(-1)?.query ?? "", /^\?since=\d+&offset=16000$/u, "the page after the one it has");
    assert.equal(await shownText(), fileOutput);
    assert.equal(await isHidden(".output-viewer-later"), true, "nothing more after it");
    await evaluate('document.querySelector(".output-viewer-close").click()');
    await waitFor('!document.querySelector(".output-viewer")');
    await evaluate('document.querySelector("#timeline .tool-row[data-activity=call-3]").click()');
    await waitFor('!document.querySelector(".tool-detail")');

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
