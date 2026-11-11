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
import { testLimit, testLimitRanOut } from "../testTime";

// Where the pictures of this test go, when a person wants to look at them.
const shots = process.env.CODEALTA_SHOTS ?? "";

const numberedLines = (word: string, lines: number) => Array.from({ length: lines }, (_, index) => `${word} ${index + 1}`).join("\n");

test("the phone takes back a prompt that waits, and opens a tool call on what it does", { skip: !browserExecutable, timeout: testLimit(90_000) }, async () => {
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
  server.state.sessions = [session("s1", "A running session", "p1", 0, { live: "running", kind: "activity", line: null, canSteer: true }), session("s2", "The review", "p1", 5)];
  const answer = event("assistant.message", "See [code](src/a.cs#L1250), [the guide](docs/guide.md), [the plan](docs/plan.md#L41), [gone](gone.txt), [elsewhere](//server/share/a.txt), [the web](https://example.com/) and [the review](session:s2).");
  const ownPrompt = event("user.message", "Build it [my prompt](docs/own.md)");
  const linkedFile = (seq: number, link: string) => `s1/${seq}/${link}`;
  const longCode = numberedLines("code", 2500);
  server.state.linkedFiles.set(linkedFile(answer.seq, "src/a.cs#L1250"), longCode);
  server.state.linkedFiles.set(linkedFile(answer.seq, "docs/guide.md"), "# The guide\n\nIts **first** paragraph, and [the next](next.md).");
  server.state.linkedFiles.set(linkedFile(ownPrompt.seq, "docs/own.md"), "# My own notes");
  // Line 41 is the paragraph of step 10, line 59 the heading of step 15.
  const plan = ["# The plan", "", ...Array.from({ length: 30 }, (_, index) => [`## Step ${index + 1}`, "", `Step ${index + 1} does its part.`, ""]).flat()].join("\n");
  server.state.linkedFiles.set(linkedFile(answer.seq, "docs/plan.md#L41"), plan);
  server.state.events.set("s1", [
    answer,
    ownPrompt,
    event("activity.toolcall.started", "Bash", { activityId: "call-1", subject: "dotnet build -c Release" }),
    event("activity.toolcall.started", "Grep", { activityId: "call-2" }),
    event("activity.toolcall.completed", "Grep", { activityId: "call-2" }),
    event("activity.toolcall.started", "Read", { activityId: "call-3", subject: "C:\\work\\skills\\SKILL.md" }),
    event("activity.toolcall.completed", "Read", { activityId: "call-3", subject: "C:\\work\\skills\\SKILL.md" }),
  ]);
  const longLine = "an output line that is much wider than the screen of a phone, ".repeat(6);
  // Three pages of the fixture server, and two.
  const buildOutput = `${numberedLines("built", 4000)}\n${longLine}`;
  server.state.toolOutputs.set("s1/call-1", buildOutput);
  server.state.tools.set("s1/call-1", {
    name: "Bash", phase: "started", command: "dotnet build -c Release", sessionFolder: "C:\\work\\worktrees\\calm-otter",
    output: buildOutput.slice(-2000), outputOffset: buildOutput.length - 2000, outputLength: buildOutput.length, error: null,
  });
  const fileOutput = numberedLines("line", 2500);
  server.state.toolOutputs.set("s1/call-3", fileOutput);
  server.state.tools.set("s1/call-3", { name: "Read", phase: "completed", output: fileOutput.slice(0, 2000), outputOffset: 0, outputLength: fileOutput.length, error: null });
  // A prompt queued before this page opened, as after a reload of the phone.
  server.state.queues.set("s1", [{ id: "queue-old", text: "Queued before the reload", at: now, state: "queued" }]);
  const failures: string[] = [];
  try {
    browser = spawn(browserExecutable!, [...browserBaseArgs, `--user-data-dir=${join(directory, "profile")}`, "--remote-debugging-port=0", "about:blank"], { stdio: ["ignore", "ignore", "pipe"], windowsHide: true });
    const port = await waitForBrowserPort(join(directory, "profile"), browser);
    const pages = await (await fetch(`http://127.0.0.1:${port}/json/list`, { signal: AbortSignal.timeout(testLimit(5000)) })).json() as { type: string; webSocketDebuggerUrl: string }[];
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
      const id = ++sequence, timer = setTimeout(() => { pending.delete(id); reject(new Error(testLimitRanOut(testLimit(10_000), `the answer to ${method}`))); }, testLimit(10_000));
      pending.set(id, reply => { clearTimeout(timer); reply.error ? reject(new Error(JSON.stringify(reply.error))) : resolve(reply.result ?? {}); });
      socket!.send(JSON.stringify({ id, method, params }));
    });
    const evaluate = async <T,>(expression: string): Promise<T> => {
      const reply = await command("Runtime.evaluate", { expression, returnByValue: true, awaitPromise: true });
      assert.equal(reply.exceptionDetails, undefined, JSON.stringify(reply.exceptionDetails));
      return reply.result?.value as T;
    };
    const waitFor = async (expression: string) => {
      const deadline = Date.now() + testLimit(10_000);
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
    assert.equal(server.requests.filter(request => request.path.endsWith("/tools/call-1")).at(-1)?.query, `?since=2`, "from the first event of the call");
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
    assert.equal(pagesOf("call-1").at(-1)?.query, "?since=2", "where it says the most");
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
    assert.equal(pagesOf("call-1").at(-1)?.query, `?since=2&before=${buildOutput.length - 16_000}`);
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
    assert.deepEqual(pagesOf("call-1").slice(pagesBefore).map(request => request.query), ["?since=2&offset=0", "?since=2&offset=16000", "?since=2&offset=32000"]);
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

    // ---- A link to a file of an answer or of a prompt the user sent reads it full screen, a page at a time; Markdown is drawn ----
    const fileLink = (name: string) => `[...document.querySelectorAll("#timeline li.msg a")].find(a => a.textContent === ${JSON.stringify(name)})`;
    const fileReads = () => server.requests.filter(request => request.path === "/api/sessions/s1/file");
    await waitFor(fileLink("code"));
    assert.equal(fileReads().length, 0, "nothing of a file is read until its link is tapped");
    await evaluate(`${fileLink("code")}.click()`);
    await waitFor('document.querySelector(".output-viewer-text")?.textContent.length === 16000');
    // It opens around the line the link names: marked, in sight, with what comes before it above it.
    const markedLine = "code 1250", pageStart = longCode.indexOf(`\n${markedLine}\n`) + 1 - 8_000;
    assert.equal(await evaluate('document.querySelector(".output-viewer-text").textContent'), longCode.slice(pageStart, pageStart + 16_000));
    assert.equal(await evaluate('document.querySelector(".output-viewer-mark").textContent'), `${markedLine}\n`);
    const shownMark = await evaluate<{ top: number; bottom: number; height: number; line: number; scrolled: number }>(`(() => {
      const stage = document.querySelector(".output-viewer-stage"), box = stage.getBoundingClientRect(), marked = document.querySelector(".output-viewer-mark").getBoundingClientRect();
      return { top: marked.top - box.top, bottom: box.bottom - marked.bottom, height: marked.height, line: parseFloat(getComputedStyle(document.querySelector(".output-viewer-text")).lineHeight), scrolled: stage.scrollTop };
    })()`);
    assert.ok(shownMark.top > 0 && shownMark.bottom > 0 && shownMark.scrolled > 0, `the marked line is in sight: ${JSON.stringify(shownMark)}`);
    assert.ok(Math.abs(shownMark.height - shownMark.line) < 1, `one line, with no line of its own for its break: ${JSON.stringify(shownMark)}`);
    await shot("linked-line");
    assert.equal(await evaluate('document.querySelector(".output-viewer").getAttribute("aria-label")'), "File a.cs");
    assert.match(await evaluate<string>('document.querySelector(".output-viewer-title .tool-path-file").textContent'), /^\\a\.cs$/u, "the path the server found");
    assert.equal(new URLSearchParams(fileReads()[0].query).get("seq"), String(answer.seq));
    assert.equal(new URLSearchParams(fileReads()[0].query).get("link"), "src/a.cs#L1250", "the link as the answer wrote it");
    const markTop = () => evaluate<number>('document.querySelector(".output-viewer-mark").getBoundingClientRect().top');
    const topBefore = await markTop();
    await evaluate('document.querySelector(".output-viewer-earlier").click()');
    await waitFor(`document.querySelector(".output-viewer-text").textContent.length === ${16_000 + pageStart}`);
    assert.equal(new URLSearchParams(fileReads().at(-1)!.query).get("before"), String(pageStart));
    assert.ok(Math.abs(await markTop() - topBefore) < 2, "what is read before does not move the marked line");
    await waitFor('!document.querySelector(".output-viewer-later").disabled');
    await evaluate('document.querySelector(".output-viewer-later").click()');
    await waitFor(`document.querySelector(".output-viewer-text").textContent.length === ${longCode.length}`);
    assert.equal(new URLSearchParams(fileReads().at(-1)!.query).get("offset"), String(pageStart + 16_000));
    assert.equal(await evaluate('document.querySelector(".output-viewer-text").textContent'), longCode, "the pages are in their order around the mark");
    assert.equal(await evaluate('document.querySelectorAll(".output-viewer-mark").length'), 1);
    assert.equal(await evaluate('document.querySelector(".output-viewer-switch")'), null, "a file that is not Markdown has only its text");
    await evaluate('document.querySelector(".output-viewer-close").click()');
    await waitFor('!document.querySelector(".output-viewer")');
    await evaluate(`${fileLink("the guide")}.click()`);
    await waitFor('document.querySelector(".output-viewer-drawn h1")');
    assert.equal(await evaluate('document.querySelector(".output-viewer-drawn h1").textContent'), "The guide");
    assert.equal(await evaluate('document.querySelector(".output-viewer-drawn strong").textContent'), "first");
    assert.equal(await evaluate('document.querySelector(".output-viewer-switch").getAttribute("aria-label")'), "Show as text");
    await shot("linked-markdown");
    // A link inside a file opens nothing, and says so above the viewer.
    const toast = 'document.querySelector("#toast:not([hidden]) .toast-text")?.textContent';
    const readsSoFar = fileReads().length;
    await evaluate('[...document.querySelectorAll(".output-viewer-drawn a")].find(a => a.textContent === "the next").click()');
    await waitFor(`${toast} === "Only the links of an answer or of a prompt you sent open on the phone."`);
    assert.equal(fileReads().length, readsSoFar);
    assert.equal(await evaluate('(() => { const box = document.querySelector("#toast").getBoundingClientRect(); return document.elementFromPoint(box.left + box.width / 2, box.top + box.height / 2)?.closest("#toast")?.id; })()'), "toast", "the note is above the viewer");
    await evaluate('document.querySelector(".output-viewer-close").click()');
    await waitFor('!document.querySelector(".output-viewer")');

    // A Markdown file a link opens at a line is text, with the line marked. Its button draws it and shows it as text again,
    // at the same place, reading nothing again and adding no step to Back.
    await evaluate(`${fileLink("the plan")}.click()`);
    await waitFor('document.querySelector(".output-viewer-text .output-viewer-mark")');
    assert.equal(await evaluate('document.querySelector(".output-viewer-mark").textContent'), "Step 10 does its part.\n");
    const switchLabel = () => evaluate<string>('document.querySelector(".output-viewer-switch").getAttribute("aria-label")');
    assert.equal(await switchLabel(), "Show as Markdown");
    await shot("linked-markdown-line");
    const planReads = fileReads().length, entries = await evaluate<number>("history.length");
    const fromStageTop = (element: string) => evaluate<number>(`${element}.getBoundingClientRect().top - document.querySelector(".output-viewer-stage").getBoundingClientRect().top`);
    await evaluate('document.querySelector(".output-viewer-switch").click()');
    await waitFor('document.querySelector(".output-viewer-drawn .output-viewer-block-mark")');
    assert.equal(await evaluate('document.querySelector(".output-viewer-text")'), null, "one view at a time");
    assert.deepEqual(await evaluate('[...document.querySelectorAll(".output-viewer-block-mark")].map(e => `${e.tagName} ${e.textContent}`)'), ["P Step 10 does its part."]);
    const third = await evaluate<number>('document.querySelector(".output-viewer-stage").clientHeight / 3');
    assert.ok(Math.abs(await fromStageTop('document.querySelector(".output-viewer-block-mark")') - third) < 2, "the marked block is a third of the way down");
    assert.equal(await switchLabel(), "Show as text");
    await shot("linked-markdown-line-drawn");
    // Drawn, the block at the top is step 15: as text, its first line is at the top.
    const step15 = 'document.querySelectorAll(".output-viewer-drawn h2")[14]';
    await evaluate(`(() => { const stage = document.querySelector(".output-viewer-stage"); stage.scrollTop += ${step15}.getBoundingClientRect().top - stage.getBoundingClientRect().top; })()`);
    assert.ok(Math.abs(await fromStageTop(step15)) < 2, "the drawn file scrolls that far");
    await evaluate('document.querySelector(".output-viewer-switch").click()');
    await waitFor('document.querySelector(".output-viewer-text")');
    const step15LineTop = await evaluate<number>(`(() => {
      const walker = document.createTreeWalker(document.querySelector(".output-viewer-text"), NodeFilter.SHOW_TEXT);
      for (let node; (node = walker.nextNode());) {
        const at = node.data.indexOf("## Step 15\\n");
        if (at < 0) continue;
        const range = document.createRange();
        range.setStart(node, at); range.setEnd(node, at + 1);
        return range.getClientRects()[0].top - document.querySelector(".output-viewer-stage").getBoundingClientRect().top;
      }
    })()`);
    assert.ok(Math.abs(step15LineTop) < 2, `the first line of that block is at the top: ${step15LineTop}`);
    assert.equal(await evaluate('document.querySelectorAll(".output-viewer-mark").length'), 1, "the line stays marked");
    // The marked line is out of sight: drawn again, the top stays on step 15.
    await evaluate('document.querySelector(".output-viewer-switch").click()');
    await waitFor('document.querySelector(".output-viewer-drawn")');
    assert.ok(Math.abs(await fromStageTop(step15)) < 2, "the block of the line at the top is at the top");
    assert.equal(fileReads().length, planReads, "nothing is read again");
    assert.equal(await evaluate<number>("history.length"), entries, "no step is added to Back");
    await evaluate("history.back()");
    await waitFor('!document.querySelector(".output-viewer")');

    // The link of a prompt the user sent reads the file by that prompt.
    await evaluate(`${fileLink("my prompt")}.click()`);
    await waitFor('document.querySelector(".output-viewer-drawn h1")?.textContent === "My own notes"');
    assert.equal(new URLSearchParams(fileReads().at(-1)!.query).get("seq"), String(ownPrompt.seq));
    await evaluate('document.querySelector(".output-viewer-close").click()');
    await waitFor('!document.querySelector(".output-viewer")');

    // A link of another computer reads nothing, and says so.
    const readsBefore = fileReads().length;
    await evaluate(`${fileLink("elsewhere")}.click()`);
    await waitFor(`${toast} === "This link does not open on the phone."`);
    assert.equal(fileReads().length, readsBefore);
    assert.equal(await evaluate('document.querySelector(".output-viewer")'), null);
    await evaluate(`${fileLink("gone")}.click()`);
    await waitFor('document.querySelector(".output-viewer-note")?.textContent === "This file is not there."');
    await evaluate('document.querySelector(".output-viewer-close").click()');
    await waitFor('!document.querySelector(".output-viewer")');

    // A link to a session is a chip with the project of the session and what it does, and a tap opens that session.
    const chip = fileLink("the review");
    assert.equal(await evaluate(`${chip}.dataset.sessionLink`), "s2");
    assert.equal(await evaluate(`${chip}.dataset.sessionState`), "idle");
    assert.equal(await evaluate(`${chip}.dataset.sessionScope`), "parser", "the project of the session");
    await evaluate(`${chip}.scrollIntoView({ block: "center" })`);
    await shot("session-link");
    await evaluate(`${chip}.click()`);
    await waitFor('location.hash === "#/session/s2"');
    await waitFor('document.querySelector("#detail-title")?.textContent === "The review"');
    await evaluate("history.back()");
    await waitFor('location.hash === "#/session/s1"');
    await waitFor(fileLink("the review"));

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
