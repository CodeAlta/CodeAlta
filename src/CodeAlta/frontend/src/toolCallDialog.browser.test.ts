import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { existsSync } from "node:fs";
import { mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { fileURLToPath, pathToFileURL } from "node:url";
import { build } from "esbuild";
import { browserBaseArgs, browserExecutable, waitForBrowserPort } from "./browserTarget";

const edge = browserExecutable;

const call = { eventType: "activity", kind: "ToolCall", activityId: "call", name: "shell_command" };
const shellArguments = JSON.stringify({ command: "dotnet build\n  -c Release", workdir: "C:\\code\\app", timeoutMs: 30000 });
const tool = (values: object) => ({ primary: null, isCommand: false, output: null, outputLines: 0, outputBytes: null, fields: [], added: null, removed: null, exitCode: null, ...values });
const started = { ...call, offset: "20", phase: "Started", timestamp: "2026-10-07T08:00:00Z",
  tool: tool({ primary: "dotnet build", isCommand: true, fields: [{ path: "arguments", text: shellArguments, truncated: false }] }) };
const completed = { ...call, offset: "40", phase: "Failed", timestamp: "2026-10-07T08:00:13Z", text: "shell_command exited with code 3.",
  tool: tool({ primary: "dotnet build", isCommand: true, fields: [{ path: "arguments", text: shellArguments, truncated: false }] }) };
const output = { offset: "30", eventType: "contentCompleted", kind: "ToolOutput", contentId: "call:output", parentActivityId: "call", text: "exit_code: 3",
  tool: tool({ output: "restored", outputLines: 3, outputBytes: 30, exitCode: 3 }) };
const shellResult = "exit_code: 3\r\nworking_directory: C:\\code\\app\r\nstdout:\r\nrestored\r\n<img src=x onerror=\"toolFixture.state.injected++\">\r\nstderr:\r\nerror CS1002: ; expected";
const diff = "diff --git a/src/Greeter.cs b/src/Greeter.cs\n--- a/src/Greeter.cs\n+++ b/src/Greeter.cs\n@@ -1,3 +1,4 @@\n public class Greeter\n {\n-    int count;\n+    string name; // the name\n+    int count;\n }\n"
  + "diff --git a/config.json b/config.json\nnew file mode 100644\n--- /dev/null\n+++ b/config.json\n@@ -0,0 +1,3 @@\n+{\n+  \"language\": \"en\"\n+}\n";

test("the window of a tool call follows it: live output, then its record, with a view for each kind of call", { skip: !edge, timeout: 180_000 }, async () => {
  const root = await mkdtemp(join(tmpdir(), "codealta-tool-call-"));
  let browser: ReturnType<typeof spawn> | undefined;
  let socket: WebSocket | undefined;
  try {
    await build({ entryPoints: [fileURLToPath(new URL("./toolCallDialog.mount.tsx", import.meta.url))], outfile: join(root, "fixture.js"), bundle: true, platform: "browser", format: "iife" });
    await writeFile(join(root, "style.css"), await readFile(new URL("../node_modules/@blueprintjs/core/lib/css/blueprint.css", import.meta.url), "utf8")
      + await readFile(new URL("../node_modules/@xterm/xterm/css/xterm.css", import.meta.url), "utf8") + await readFile(new URL("./style.css", import.meta.url), "utf8"));
    const page = join(root, "fixture.html");
    await writeFile(page, '<!doctype html><html><head><meta charset="utf-8"><link rel="stylesheet" href="style.css"></head><body class="bp6-dark"><div id="root"></div><script src="fixture.js"></script></body></html>');
    browser = spawn(edge!, [...browserBaseArgs, "--window-size=1400,1000",
      `--user-data-dir=${join(root, "profile")}`, "--remote-debugging-port=0", "about:blank"], { stdio: ["ignore", "ignore", "pipe"], windowsHide: true });
    const port = await waitForBrowserPort(join(root, "profile"), browser);
    const pages: { type?: string; webSocketDebuggerUrl?: string }[] = await (await fetch(`http://127.0.0.1:${port}/json/list`, { signal: AbortSignal.timeout(10_000) })).json();
    socket = new WebSocket(pages.find(value => value.type === "page")!.webSocketDebuggerUrl!);
    await new Promise<void>((resolve, reject) => { socket!.addEventListener("open", () => resolve(), { once: true }); socket!.addEventListener("error", () => reject(Error("Browser unavailable")), { once: true }); });
    let sequence = 0;
    const command = (method: string, params: object = {}) => new Promise<any>((resolve, reject) => {
      const id = ++sequence;
      const timer = setTimeout(() => reject(Error(`${method} timed out`)), 30_000);
      const reply = (event: MessageEvent) => {
        const message = JSON.parse(String(event.data));
        if (message.id !== id) return;
        socket!.removeEventListener("message", reply); clearTimeout(timer);
        if (message.error) reject(Error(JSON.stringify(message.error))); else resolve(message.result);
      };
      socket!.addEventListener("message", reply); socket!.send(JSON.stringify({ id, method, params }));
    });
    const evaluate = async <T,>(expression: string): Promise<T> => {
      const result = await command("Runtime.evaluate", { expression, returnByValue: true, awaitPromise: true });
      assert.equal(result.exceptionDetails, undefined, JSON.stringify(result.exceptionDetails));
      return result.result?.value as T;
    };
    const wait = (expression: string, milliseconds = 8_000) => evaluate<boolean>(
      `new Promise(resolve=>{const end=Date.now()+${milliseconds};const tick=()=>{let ok=false;try{ok=!!(${expression})}catch{}ok?resolve(true):Date.now()>end?resolve(false):setTimeout(tick,25)};tick()})`);
    const show = (...entries: object[]) => evaluate(`toolFixture.show(${JSON.stringify(entries)})`);
    const dialog = "document.querySelector('dialog.tool-call-dialog[open]')";
    const terminal = `${dialog}.querySelector('.output-terminal .xterm-rows').textContent`;
    const facts = `${dialog}.querySelector('.tool-summary').textContent`;
    await command("Page.navigate", { url: pathToFileURL(page).href });
    assert.equal(await wait("window.toolFixture"), true);

    // A running command: its whole command line, the terminal waiting, and what the call writes as it comes.
    await show(started);
    assert.equal(await wait(dialog), true);
    await evaluate(`window.keptDialog=${dialog}; true`);
    assert.equal(await evaluate(`${dialog}.querySelector('.tool-state').dataset.state + '|' + ${dialog}.querySelector('.tool-title-name').textContent`), "running|shell_command");
    assert.equal(await evaluate(`${dialog}.querySelector('.tool-command-text').textContent`), "dotnet build\n  -c Release");
    assert.equal(await evaluate(`${dialog}.querySelector('.tool-waiting').textContent`), "Waiting for output…");
    assert.equal(await evaluate(`[...${dialog}.querySelectorAll('.tool-tabs > .bp6-tab-list [role=tab]')].map(tab=>tab.textContent).join('|')`), "Output|Details");
    // The arrow keys move between the tabs (Blueprint skips them under React 19 unless each Tab says `disabled={false}`).
    await evaluate(`${dialog}.querySelector('.tool-tabs > .bp6-tab-list [role=tab]').focus(); true`);
    await command("Input.dispatchKeyEvent", { type: "keyDown", key: "ArrowRight", code: "ArrowRight", windowsVirtualKeyCode: 39 });
    await command("Input.dispatchKeyEvent", { type: "keyUp", key: "ArrowRight", code: "ArrowRight", windowsVirtualKeyCode: 39 });
    assert.equal(await wait(`document.activeElement?.textContent==='Details'`), true, "The arrow key moves from Output to Details.");
    await evaluate(`${dialog}.querySelector('.tool-tabs > .bp6-tab-list [role=tab]').focus(); true`);
    assert.equal(await wait("toolFixture.state.opened.length===1"), true);
    await evaluate(`toolFixture.push('call',{text:'step 1\\nstep 2\\n',start:'0',total:'14',isReset:true})`);
    assert.equal(await wait(`${terminal}.includes('step 1') && ${terminal}.includes('step 2')`), true, "The terminal shows the live output.");
    assert.equal(await wait(`${facts}.includes('2 lines')`), true);
    await evaluate(`toolFixture.push('call',{text:'step 3\\n',start:'14',total:'21'})`);
    assert.equal(await wait(`${terminal}.includes('step 3') && !${dialog}.querySelector('.tool-waiting')`), true);
    assert.equal(await evaluate(`${facts}.includes('3 lines') && ${facts}.includes('Timeout 30 s') && ${facts}.includes('C:\\\\code\\\\app')`), true);

    // The call ends: the same window shows the output of its record, its exit code and its duration.
    await evaluate(`toolFixture.record('40','30',${JSON.stringify({ kind: "ToolCall", phase: "Failed", name: "shell_command", timestamp: "2026-10-07T08:00:13Z", message: null,
      command: "dotnet build\n  -c Release", workingDirectory: "C:\\code\\app", exitCode: null, error: "shell_command exited with code 3.",
      arguments: { text: shellArguments, length: shellArguments.length, more: false }, output: { text: shellResult, length: shellResult.length, more: false }, diff: null,
      readFiles: [], modifiedFiles: [] })})`);
    await show(started, output, completed);
    assert.equal(await wait(`${dialog}.querySelector('.tool-state').dataset.state==='failed' && ${terminal}.includes('error CS1002')`), true);
    assert.equal(await evaluate(`${dialog}===window.keptDialog`), true, "The window is the one that was open.");
    assert.equal(await evaluate(`${terminal}.includes('restored') && ${terminal}.includes('stderr') && !${terminal}.includes('step 1') && !${terminal}.includes('exit_code')`), true,
      "The terminal shows what the command wrote, not the header of its result.");
    assert.equal(await evaluate(`${dialog}.querySelector('.tool-exit').textContent + '|' + ${dialog}.querySelector('.tool-exit').classList.contains('bp6-intent-danger')`), "Exit code 3|true");
    assert.equal(await evaluate(`${facts}.includes('13 s') && ${facts}.includes('3 lines')`), true);
    assert.equal(await evaluate(`!${dialog}.querySelector('.tool-error')`), true, "An error that only repeats the exit code is not said twice.");
    assert.equal(await evaluate("toolFixture.state.injected + document.querySelectorAll('dialog img').length"), 0, "The output of a command is text.");
    assert.deepEqual(await evaluate("toolFixture.state.reads"), ["20||", "40|30|"]);

    // The Details tab has what the records hold: the identity of the call and its arguments as JSON.
    await evaluate(`[...${dialog}.querySelectorAll('.tool-tabs > .bp6-tab-list [role=tab]')].find(tab=>tab.textContent==='Details').click()`);
    assert.equal(await wait(`${dialog}.querySelector('.tool-facts')`), true);
    assert.equal(await evaluate(`[...${dialog}.querySelectorAll('.tool-facts dt')].map(name=>name.textContent).join('|')`), "Tool|Kind|Provider|Run|Call|Started|Ended|Duration|Working directory");
    assert.equal(await evaluate(`${dialog}.querySelector('.tool-details .tool-code').textContent.includes('"timeoutMs": 30000') && ${dialog}.querySelectorAll('.tool-details .tool-code [class^=hljs-]').length>0`), true);
    assert.equal(await evaluate(`${dialog}.querySelector('.tool-details .tool-output').textContent.startsWith('exit_code: 3')`), true, "The text of the result is there as it was written.");
    // Escape closes the window.
    await evaluate(`${dialog}.dispatchEvent(new KeyboardEvent('keydown',{key:'Escape',bubbles:true,cancelable:true}))`);
    assert.equal(await wait("toolFixture.state.closed>=1 && !document.querySelector('dialog.tool-call-dialog')"), true);

    // An edit of several files: a tab per file, the changes of the chosen one, a new file as the file it is.
    await show({ ...call, name: "apply_patch", offset: "50", phase: "Completed", tool: tool({ added: 5, removed: 1, fields: [{ path: "diff", text: diff, truncated: false }] }) });
    assert.equal(await wait(`${dialog}?.querySelectorAll('.tool-edit-files > .bp6-tab-list [role=tab]').length===2`), true);
    assert.equal(await evaluate(`[...${dialog}.querySelectorAll('.tool-edit-files .tool-file-tab')].map(tab=>tab.textContent).join('|')`), "Greeter.cs+2 −1|config.json+3 −0");
    assert.equal(await evaluate(`${facts}.includes('2 files') && ${facts}.includes('+5')`), true);
    assert.equal(await evaluate(`[...${dialog}.querySelectorAll('.diff-preview-line')].map(row=>row.dataset.kind).join(',')`), "context,context,removed,added,added,context");
    assert.equal(await evaluate(`${dialog}.querySelector('.diff-preview-line[data-kind=added] .hljs-comment').textContent`), "// the name", "The lines have the colors of the language of the file.");
    assert.equal(await evaluate(`${dialog}.querySelector('.tool-file-header').textContent.startsWith('Greeter.cssrc')`), true);
    await evaluate(`${dialog}.querySelectorAll('.tool-edit-files > .bp6-tab-list [role=tab]')[1].click()`);
    assert.equal(await wait(`${dialog}.querySelector('.tool-file-header strong')?.textContent==='config.json'`), true);
    assert.equal(await evaluate(`${dialog}.querySelector('.tool-file-header .bp6-tag').textContent + '|' + ${dialog}.querySelectorAll('.tool-code-line').length + '|' + !!${dialog}.querySelector('.diff-preview-line')`), "New file|3|false");

    // A file that was read: its lines with their numbers. A search: its matches by file, marked.
    await show({ ...call, name: "read_file", offset: "60", phase: "Completed", tool: tool({ primary: "src/a.cs", fields: [{ path: "arguments", text: '{"path":"src/a.cs","offset":12}', truncated: false },
      { path: "result.content", text: "   12: using System;\r\n   13: \r\n   14: class A { }", truncated: false }] }) });
    assert.equal(await wait(`${dialog}?.querySelectorAll('.tool-read .tool-code-line').length===3`), true);
    assert.equal(await evaluate(`[...${dialog}.querySelectorAll('.tool-code-number')].map(n=>n.textContent).join(',') + '|' + ${dialog}.querySelector('.tool-file-facts').textContent + '|' + ${dialog}.querySelector('.tool-code-line .hljs-keyword').textContent`),
      "12,13,14|Lines 12–14|using");
    await show({ ...call, name: "grep", offset: "70", phase: "Completed", tool: tool({ primary: "class", fields: [{ path: "arguments", text: '{"pattern":"class","path":"src","caseSensitive":false}', truncated: false },
      { path: "result.content", text: "src/a.cs:14: class A { }\nsrc/b.cs:3: // Class B\nsrc/b.cs:9: class B", truncated: false }] }) });
    assert.equal(await wait(`${dialog}?.querySelectorAll('.tool-search-file').length===2`), true);
    assert.equal(await evaluate(`[...${dialog}.querySelectorAll('.tool-search mark')].map(mark=>mark.textContent).join(',') + '|' + [...${dialog}.querySelectorAll('.tool-search .tool-file-facts')].map(count=>count.textContent).join(',')`),
      "class,Class,class|1 match,2 matches");
    assert.equal(await evaluate(`[...${dialog}.querySelectorAll('.tool-arguments dt')].map(name=>name.textContent).join(',')`), "pattern,path,caseSensitive");

    // A long result stays in the window: the view that holds it scrolls, and nothing is cut at the bottom.
    const fits = (view: string) => `(()=>{const body=${dialog}.querySelector('.app-window-body'),part=${dialog}.querySelector('${view}');
      return part.scrollHeight>part.clientHeight+200 && part.getBoundingClientRect().bottom<=body.getBoundingClientRect().bottom+1 && body.scrollHeight<=body.clientHeight+1})()`;
    const long = Array.from({ length: 400 }, (_, index) => `line ${index + 1}`).join("\n");
    await show({ ...call, name: "lookup", offset: "74", phase: "Completed", tool: tool({ primary: "everything", fields: [{ path: "arguments", text: '{"query":"everything"}', truncated: false },
      { path: "result.content", text: long, truncated: false }] }) });
    assert.equal(await wait(`${dialog}?.querySelector('.tool-panel[data-view=generic] .tool-output')?.textContent.includes('line 400')`), true);
    assert.equal(await evaluate(fits(".tool-panel")), true, "A long result scrolls in its view.");
    await show({ ...call, name: "read_file", offset: "76", phase: "Completed", tool: tool({ primary: "src/long.txt", fields: [{ path: "arguments", text: '{"path":"src/long.txt"}', truncated: false },
      { path: "result.content", text: long.split("\n").map((line, index) => `${String(index + 1).padStart(5)}: ${line}`).join("\n"), truncated: false }] }) });
    assert.equal(await wait(`${dialog}?.querySelectorAll('.tool-read .tool-code-line').length===400`), true);
    assert.equal(await evaluate(fits(".tool-code-lines")), true, "A long file scrolls under its name.");

    // A call that failed with a message says it once, above its arguments.
    await show({ ...call, name: "read_file", offset: "80", phase: "Failed", text: "File 'missing.txt' was not found.", tool: tool({ primary: "missing.txt",
      fields: [{ path: "arguments", text: '{"path":"missing.txt"}', truncated: false }, { path: "result.content", text: "File 'missing.txt' was not found.", truncated: false }] }) });
    assert.equal(await wait(`${dialog}?.querySelector('.tool-error')?.textContent.includes("was not found")`), true);
    assert.equal(await evaluate(`!${dialog}.querySelector('.tool-output') && ${dialog}.querySelector('.tool-arguments dd').textContent`), "missing.txt");

    // A press beside the window closes it, as Escape does. One that begins in the window and ends beside it, as a
    // selection does, leaves it open.
    const mouse = (type: string, x: number, y: number) => command("Input.dispatchMouseEvent", { type, x, y, button: "left", buttons: type === "mouseReleased" ? 0 : 1, clickCount: 1 });
    const box = await evaluate<{ left: number; top: number }>(`(()=>{const box=${dialog}.querySelector('.app-window-body').getBoundingClientRect();return {left:box.left,top:box.top}})()`);
    assert.ok(box.left > 40 && box.top > 40, "The window leaves room beside it.");
    const closed = await evaluate<number>("toolFixture.state.closed");
    await mouse("mousePressed", box.left + 30, box.top + 30); await mouse("mouseMoved", 12, 12); await mouse("mouseReleased", 12, 12);
    assert.equal(await evaluate(`!!${dialog} && toolFixture.state.closed===${closed}`), true, "A press that began in the window does not close it.");
    await mouse("mousePressed", 12, 12); await mouse("mouseReleased", 12, 12);
    assert.equal(await wait(`toolFixture.state.closed===${closed + 1} && !document.querySelector('dialog.tool-call-dialog')`), true, "A press beside the window closes it.");

    // A job completion is a compact card even with a long title, command and output. It opens the same kind
    // of window, retaining every recorded fact and the notice that this output is only a tail.
    const jobCommand = ("dotnet test " + "--filter VeryLongTestName ".repeat(30)).trim();
    const jobTitle = ("Verify the application " + "and all of its tests ".repeat(15)).trim();
    const jobPrompt = (result: string) => `[CodeAlta background job]\nJob: job-1234\nTitle: ${jobTitle}\nCommand: ${jobCommand}\nResult: ${result}\n\nEnd of the output (\`alta job output job-1234\` reads more):\n\`\`\`\n${long}\n<img src=x onerror="toolFixture.state.injected++">\n\`\`\``;
    const job = { offset: "90", eventType: "contentCompleted", kind: "User", text: jobPrompt("succeeded (exit code 0) after 10 min 45 s") };
    const assistant = { offset: "91", eventType: "contentCompleted", kind: "Assistant", text: "The job has ended." };
    const card = "document.querySelector('.message-job')";
    const trigger = `${card}.querySelector('.tool-tile-title')`;
    const jobLayout = `(()=>{const card=${card},row=card.closest('.messages'),r=card.getBoundingClientRect(),bounds=row.getBoundingClientRect(),style=getComputedStyle(row),cardStyle=getComputedStyle(card);
      return Math.abs(r.left-bounds.left-parseFloat(style.paddingLeft))<1 && Math.abs(r.right-bounds.right+parseFloat(style.paddingRight))<1
        && Math.abs(card.querySelector('.message-actions time').getBoundingClientRect().right-r.right+parseFloat(cardStyle.paddingRight)+parseFloat(cardStyle.borderRightWidth))<1
        && r.height<=80 && card.querySelector('.tool-argument-preview').getBoundingClientRect().width<=568 && card.scrollWidth<=card.clientWidth})()`;
    const jobAlignment = `(()=>{const card=${card},reference=document.querySelector('.message-assistant'),rect=(element,selector)=>element.querySelector(selector).getBoundingClientRect();
      return Math.abs(rect(card,'.avatar').left-rect(reference,'.avatar').left)<1
        && Math.abs(rect(card,'.message-heading').left-rect(reference,'.message-heading').left)<1
        && Math.abs(rect(card,'.message-actions time').right-rect(reference,'.message-actions time').right)<1})()`;
    const jobIdentity = `(()=>{const card=${card},style=getComputedStyle(card),avatar=card.querySelector('.avatar'),title=card.querySelector('.tool-tile-title');
      const reference=document.createElement('span');reference.style.color='var(--user-accent)';reference.style.background='color-mix(in srgb, var(--user-accent) 8%, var(--panel))';card.append(reference);
      const expected=getComputedStyle(reference),matches=style.borderLeftWidth==='2px' && style.borderLeftColor===expected.color && style.backgroundColor===expected.backgroundColor
        && getComputedStyle(avatar).display!=='none' && getComputedStyle(avatar).color===expected.color && !!avatar.querySelector('svg')
        && getComputedStyle(title.querySelector('strong')).color===expected.color && getComputedStyle(title).fontFamily===getComputedStyle(card.querySelector('.message-heading')).fontFamily
        && !title.querySelector('.tool-state-dot');reference.remove();return matches})()`;
    await show(job, assistant);
    assert.equal(await wait(card), true);
    assert.equal(await evaluate(`!${card}.querySelector('.markdown-content, pre, dialog')`), true, "The output is not rendered in the timeline.");
    assert.equal(await evaluate(jobLayout), true, "The job container fills the row, with its time at the far right and compact content about two tool cards wide.");
    assert.equal(await evaluate(jobAlignment), true, "The icon, heading and timestamp align with the other timeline cards.");
    assert.equal(await evaluate(`${card}.dataset.toolPhase`), "completed");
    assert.equal(await evaluate(jobIdentity), true, "The compact card keeps its purple border, tinted surface, terminal icon and heading.");
    // Native keyboard activation and focus restoration, not a synthetic click on the heading.
    await evaluate(`${trigger}.focus()`);
    assert.equal(await evaluate(`document.activeElement===${trigger}`), true, "The card's title is keyboard-focusable.");
    await command("Input.dispatchKeyEvent", { type: "keyDown", key: "Enter", code: "Enter", windowsVirtualKeyCode: 13, text: "\r" });
    await command("Input.dispatchKeyEvent", { type: "keyUp", key: "Enter", code: "Enter", windowsVirtualKeyCode: 13 });
    assert.equal(await wait(dialog), true, "Enter opens the window without activating its Close button on key release.");
    assert.equal(await evaluate(`${dialog}.querySelector('.tool-command-text').textContent`), jobCommand);
    assert.equal(await evaluate(`${dialog}.querySelector('.tool-title-name').textContent`), jobTitle);
    assert.equal(await evaluate(`${facts}`), "succeeded (exit code 0) after 10 min 45 s");
    assert.equal(await evaluate(`${dialog}.textContent.includes('reads more') && ${dialog}.textContent.includes('line 400')`), true);
    assert.equal(await evaluate(`toolFixture.state.injected + ${dialog}.querySelectorAll('img').length`), 0);
    assert.equal(await evaluate(fits(".tool-panel")), true, "Long recorded output scrolls within the window.");
    await evaluate(`${dialog}.querySelector('[aria-label="Copy command"]').click()`);
    assert.equal(await wait(`toolFixture.state.copied===${JSON.stringify(jobCommand)}`), true);
    await evaluate(`[...${dialog}.querySelectorAll('[role=tab]')].find(tab=>tab.textContent==='Details').click()`);
    assert.equal(await wait(`${dialog}.querySelector('.tool-facts')?.textContent.includes('job-1234')`), true);
    assert.equal(await evaluate(`${dialog}.querySelector('.tool-facts').textContent.includes(${JSON.stringify(jobTitle)}) && ${dialog}.textContent.includes('Event: contentCompleted')`), true);
    await command("Input.dispatchKeyEvent", { type: "keyDown", key: "Escape", code: "Escape", windowsVirtualKeyCode: 27 });
    await command("Input.dispatchKeyEvent", { type: "keyUp", key: "Escape", code: "Escape", windowsVirtualKeyCode: 27 });
    assert.equal(await wait(`!${dialog} && document.activeElement===${trigger}`), true);

    for (const [result, phase] of [["failed (exit code 3) after 12 s", "failed"], ["timed out and was stopped after 30 min 0 s", "failed"], ["stopped before it ended after 1 s", "canceled"]]) {
      await show({ ...job, text: jobPrompt(result) }, assistant);
      assert.equal(await evaluate(`${card}.dataset.toolPhase`), phase);
      assert.equal(await evaluate(jobIdentity), true, "The job identity stays purple independently of its outcome.");
      // Clicking the card's summary opens its details, not just the heading button.
      await evaluate(`${card}.querySelector('.tool-argument-preview').click()`);
      assert.equal(await wait(dialog), true);
      assert.equal(await evaluate(`${facts}`), result);
      await mouse("mousePressed", 12, 12); await mouse("mouseReleased", 12, 12);
      assert.equal(await wait(`!${dialog}`), true);
    }
    await command("Emulation.setDeviceMetricsOverride", { width: 500, height: 650, deviceScaleFactor: 1, mobile: false });
    await evaluate("document.body.classList.remove('bp6-dark');document.documentElement.dataset.theme='light'");
    assert.equal(await evaluate(jobIdentity), true, "The job identity follows the light theme's purple accent.");
    assert.equal(await evaluate(jobLayout), true, "The full-row container and its content shrink to a narrow timeline without overflow.");
    assert.equal(await evaluate(jobAlignment), true, "The job header stays aligned with the other timeline cards at narrow widths.");
    assert.equal(await evaluate(`(()=>{const r=${card}.getBoundingClientRect();return r.height<=80 && r.right<=innerWidth && document.documentElement.scrollWidth<=innerWidth})()`), true);
    await evaluate(`${trigger}.click()`);
    assert.equal(await wait(dialog), true);
    assert.equal(await evaluate(`(()=>{const r=${dialog}.querySelector('.app-window').getBoundingClientRect();return r.left>=0 && r.right<=innerWidth && r.bottom<=innerHeight})()`), true);
    // A replaced history record retires its window; it cannot keep the previous output under a new card.
    await show({ ...job, text: jobPrompt("failed (exit code 5) after 20 s") }, assistant);
    assert.equal(await wait(`!${dialog}`), true);
    assert.deepEqual(await evaluate("toolFixture.state.jobReads"), [], "Recorded timeline results never depend on a live job lookup.");

    // The list above the prompt opens the very same job window, with live output while a job runs.
    const task = { id: "job-live", kind: "command", description: "Verify background output " + "with a long title ".repeat(15), toolCallId: null,
      startedAt: new Date(Date.now() - 10_000).toISOString(), state: "running", job: true, exitCode: null, endedAt: null };
    const recent = { ...task, id: "job-recent", description: "Recent failure", state: "failed", exitCode: 7, endedAt: new Date().toISOString() };
    const providerTask = { ...task, id: "provider-task", job: false, description: "Provider task" };
    const jobs = (values: object[]) => evaluate(`toolFixture.jobs(${JSON.stringify(values)})`);
    const status = "document.querySelector('.background-tasks-status')";
    const openList = async () => { await evaluate(`${status}.click()`); assert.equal(await wait("document.querySelector('.background-tasks')"), true); };
    await jobs([task, recent, providerTask]);
    await openList();
    assert.equal(await evaluate("document.querySelectorAll('.background-task-open').length"), 2, "Whole job rows open details; provider tasks do not pretend to have host-job output.");
    assert.equal(await evaluate("[...document.querySelectorAll('.background-tasks-section > h6')].map(e=>e.textContent).join('|')"), "Running|Recent");
    assert.equal(await evaluate("document.querySelector('.background-task-open').querySelector('strong').textContent"), task.description);
    for (const width of [500, 1400]) {
      await command("Emulation.setDeviceMetricsOverride", { width, height: 800, deviceScaleFactor: 1, mobile: false });
      for (const theme of ["dark", "light"]) {
        await evaluate(`document.documentElement.dataset.theme='${theme}';document.body.classList.toggle('bp6-dark','${theme}'==='dark')`);
        assert.equal(await wait("(()=>{const list=document.querySelector('.background-tasks'),r=list.getBoundingClientRect();return r.left>=0&&r.right<=innerWidth&&list.scrollWidth<=list.clientWidth})()"), true, `${width}px ${theme}: the task list fits.`);
      }
    }
    const rowButton = "document.querySelector('.background-task-open')";
    // Native Enter must not immediately activate Close on key release.
    await evaluate(`${rowButton}.focus()`);
    await command("Input.dispatchKeyEvent", { type: "keyDown", key: "Enter", code: "Enter", windowsVirtualKeyCode: 13, text: "\r" });
    await command("Input.dispatchKeyEvent", { type: "keyUp", key: "Enter", code: "Enter", windowsVirtualKeyCode: 13 });
    assert.equal(await wait("document.querySelector('dialog.job-result-dialog[open]')"), true, "The popup uses the same job-details window as the timeline.");
    assert.equal(await wait(`${dialog}.querySelector('.tool-command-text')?.textContent==='dotnet test\\n  -c Release'`), true);
    assert.equal(await evaluate(`[...${dialog}.querySelectorAll('.tool-tabs > .bp6-tab-list [role=tab]')].map(tab=>tab.textContent).join('|')`), "Output|Details");
    await evaluate(`toolFixture.push('job-live',{text:'step 1\\n',start:'0',total:'7',isReset:true})`);
    assert.equal(await wait(`${terminal}.includes('step 1')`), true);
    await evaluate(`[...${dialog}.querySelectorAll('[role=tab]')].find(tab=>tab.textContent==='Details').click()`);
    assert.equal(await wait(`${dialog}.querySelector('.tool-facts')?.textContent.includes('job-live')`), true);
    assert.equal(await evaluate(`${dialog}.querySelector('.tool-facts').textContent.includes('C:\\\\code\\\\app')`), true);
    await evaluate(`[...${dialog}.querySelectorAll('[role=tab]')].find(tab=>tab.textContent==='Output').click()`);
    await evaluate(`${dialog}.querySelector('[aria-label="Copy command"]').click()`);
    assert.equal(await wait("toolFixture.state.copied==='dotnet test\\n  -c Release'"), true);
    await evaluate(`toolFixture.push('job-live',{text:'done\\n',start:'7',total:'12',isComplete:true})`);
    await jobs([{ ...task, state: "completed", exitCode: 0, endedAt: new Date().toISOString() }, recent, providerTask]);
    assert.equal(await wait(`${dialog}.querySelector('.tool-state')?.dataset.state==='completed' && ${terminal}.includes('done')`), true);
    assert.equal(await evaluate(`!${dialog}.querySelector('[aria-label="Stop this background task"]')`), true);
    await evaluate(`${dialog}.querySelector('[aria-label="Copy output"]').click()`);
    assert.equal(await wait("toolFixture.state.copied==='step 1\\ndone\\n'"), true);
    await command("Input.dispatchKeyEvent", { type: "keyDown", key: "Escape", code: "Escape", windowsVirtualKeyCode: 27 });
    await command("Input.dispatchKeyEvent", { type: "keyUp", key: "Escape", code: "Escape", windowsVirtualKeyCode: 27 });
    assert.equal(await wait(`!${dialog} && document.activeElement===${status}`), true, "Closing returns focus to the task-list trigger.");
    await jobs([task, recent, providerTask]);
    await openList();
    // Stop is a separate action, never nested inside the open button or opening a dialog itself.
    await evaluate("document.querySelector('.background-tasks [aria-label=\"Stop this background task\"]').click()");
    assert.equal(await wait("toolFixture.state.stopped.includes('job-live')"), true);
    assert.equal(await evaluate(`!${dialog} && !document.querySelector('.background-task-open button')`), true);
    await evaluate("document.querySelector('.background-task-open strong').click()");
    assert.equal(await wait(dialog), true, "Clicking the row's title opens it, not just a small trailing icon.");
    const closeJob = async () => {
      await evaluate(`${dialog}.querySelector('[aria-label="Close"]').click()`);
      assert.equal(await wait(`!${dialog} && document.activeElement===${status}`), true);
    };
    await closeJob();
    await openList();
    // Click the row's empty padding with real pointer events, not an element's synthetic click.
    const point = await evaluate<{ x: number; y: number }>(`(()=>{const r=${rowButton}.getBoundingClientRect();return {x:r.left+4,y:r.top+4}})()`);
    await command("Input.dispatchMouseEvent", { type: "mousePressed", ...point, button: "left", clickCount: 1 });
    await command("Input.dispatchMouseEvent", { type: "mouseReleased", ...point, button: "left", clickCount: 1 });
    assert.equal(await wait(dialog), true, "Even the blank part of a job row opens its details.");
    await closeJob();
    await jobs([recent, providerTask]);
    await openList();
    await evaluate(`${rowButton}.focus()`);
    await command("Input.dispatchKeyEvent", { type: "keyDown", key: " ", code: "Space", windowsVirtualKeyCode: 32, text: " " });
    await command("Input.dispatchKeyEvent", { type: "keyUp", key: " ", code: "Space", windowsVirtualKeyCode: 32 });
    assert.equal(await wait(`${dialog}?.querySelector('.tool-state')?.dataset.state==='failed'`), true, "Space opens a recent result too.");
    await evaluate(`toolFixture.push('job-recent',{text:'retained tail\\n',start:'100',total:'114',isReset:true,isComplete:true})`);
    assert.equal(await wait(`${terminal}.includes('retained tail') && ${dialog}.textContent.includes('Only the latest output is retained.')`), true);
    assert.equal(await evaluate(`!${dialog}.querySelector('[aria-label="Stop this background task"]')`), true);
    await closeJob();
    await evaluate("toolFixture.state.jobAvailable=false");
    await openList();
    await evaluate(`${rowButton}.click()`);
    assert.equal(await wait(`${dialog}?.textContent.includes('Details: Unavailable')`), true, "A missing job command is not invented or left loading.");
    await closeJob();
    await evaluate("toolFixture.state.jobAvailable=true");
    await jobs([task, providerTask]);
    await openList();
    await evaluate(`${rowButton}.click()`);
    assert.equal(await wait(dialog), true);
    const stopsBefore = await evaluate<number>("toolFixture.state.stopped.length");
    await evaluate(`${dialog}.querySelector('[aria-label="Stop this background task"]').click()`);
    assert.equal(await wait(`toolFixture.state.stopped.length===${stopsBefore + 1}`), true, "The shared window retains its own Stop action.");
  } finally {
    // Edge's launcher can exit while the browser it started goes on: the browser itself is asked to close.
    if (socket?.readyState === WebSocket.OPEN) socket.send(JSON.stringify({ id: 9999, method: "Browser.close" }));
    socket?.close();
    if (browser && browser.exitCode === null) { const closed = new Promise<void>(resolve => browser!.once("exit", () => resolve())); browser.kill(); await Promise.race([closed, new Promise(resolve => setTimeout(resolve, 5000))]); }
    await rm(root, { recursive: true, force: true, maxRetries: 10, retryDelay: 100 });
  }
});
