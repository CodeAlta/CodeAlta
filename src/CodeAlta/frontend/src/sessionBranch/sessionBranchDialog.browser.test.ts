import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { fileURLToPath, pathToFileURL } from "node:url";
import { build } from "esbuild";
import { browserBaseArgs, browserExecutable, waitForBrowserPort } from "../browserTarget";

const edge = browserExecutable;

test("the window of a fork or a rewind lists the points of a session, says what the action does and makes it on Enter", { skip: !edge, timeout: 180_000 }, async () => {
  const root = await mkdtemp(join(tmpdir(), "codealta-session-branch-"));
  let browser: ReturnType<typeof spawn> | undefined;
  let socket: WebSocket | undefined;
  try {
    await build({ entryPoints: [fileURLToPath(new URL("./sessionBranchDialog.mount.tsx", import.meta.url))], outfile: join(root, "fixture.js"), bundle: true, platform: "browser", format: "iife" });
    await writeFile(join(root, "style.css"), await readFile(new URL("../../node_modules/@blueprintjs/core/lib/css/blueprint.css", import.meta.url), "utf8")
      + await readFile(new URL("../style.css", import.meta.url), "utf8") + await readFile(new URL("./sessionBranch.css", import.meta.url), "utf8"));
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
    // A key as the keyboard sends it: to the element that has the focus.
    const keys: Record<string, number> = { ArrowDown: 40, ArrowUp: 38, PageDown: 34, PageUp: 33, Home: 36, End: 35, Enter: 13, Escape: 27, Tab: 9, o: 79 };
    const press = async (key: string, control = false) => {
      const params = { key, code: key.length === 1 ? `Key${key.toUpperCase()}` : key, windowsVirtualKeyCode: keys[key], modifiers: control ? 2 : 0 };
      await command("Input.dispatchKeyEvent", { type: "rawKeyDown", ...params });
      await command("Input.dispatchKeyEvent", { type: "keyUp", ...params });
    };
    const type = (text: string) => command("Input.insertText", { text });
    const dialog = "document.querySelector('dialog.session-branch-dialog[open]')";
    const rows = `[...${dialog}.querySelectorAll('[role=option]')].map(row=>row.querySelector('.branch-number').textContent||row.querySelector('.branch-prompt').textContent).join('|')`;
    const selected = `${dialog}.querySelector('[role=option][aria-selected=true] .branch-prompt').textContent`;
    const consequence = `${dialog}.querySelector('.branch-consequence').innerText`;
    const act = `${dialog}.querySelector('.branch-act')`;
    const mode = `${dialog}.dataset.mode`;
    const state = (name: string) => evaluate<unknown>(`branchFixture.state.${name}`);
    // The pages that were asked for. The fixture mounts in strict mode, which runs the first read of a window twice.
    const reads = () => evaluate<unknown>("branchFixture.state.reads.filter((value,index,all)=>index===0||value!==all[index-1])");
    await command("Page.navigate", { url: pathToFileURL(page).href });
    assert.equal(await wait("window.branchFixture"), true);

    // Five turns: the third was sent by another agent session, the others by the user.
    await evaluate(`branchFixture.turns([{prompt:'review the parser'},{prompt:'what color is the sky?',toolCalls:2},{prompt:'the review of the child is done',sourceSessionId:'child'},{prompt:'fix the tests',status:'failed'},{prompt:'run it again',compactions:1}])`);
    await evaluate("branchFixture.show('rewind')");
    assert.equal(await wait(`${dialog} && ${dialog}.querySelectorAll('[role=option]').length===5`), true);
    // The newest first, after the end of the session; the turn of the agent is not listed; a rewind opens on the last prompt.
    assert.equal(await evaluate(rows), "End of the session|5|4|2|1");
    assert.equal(await evaluate(selected), "run it again");
    assert.equal(await evaluate(`[document.activeElement.getAttribute('role'), document.activeElement.getAttribute('aria-expanded'), ${dialog}.querySelector('[role=listbox]').getAttribute('aria-label'), ${dialog}.getAttribute('aria-modal')].join('|')`),
      "combobox|true|Points of the session|true");
    assert.equal(await evaluate(`document.activeElement.getAttribute('aria-activedescendant')===${dialog}.querySelector('[role=option][aria-selected=true]').id`), true);
    assert.equal(await evaluate(`${dialog}.querySelector('.reference-status').textContent`), "5 turns");
    assert.match(await evaluate<string>(consequence), /Turn 5, the last one, is removed from this session\.\nThe session then ends after turn 4\.\nThe prompt of turn 5 is put back in the composer, for you to edit and send\.\nFiles changed by this turn are not restored\.\nThe session as it is now is kept in a separate session, “Parser review \(before rewind\)”\./);
    assert.equal(await evaluate(`${act}.textContent + '|' + ${act}.disabled + '|' + ${dialog}.querySelector('.branch-keep input').checked`), "Rewind|false|true");
    assert.deepEqual(await reads(), [null]);

    // The arrows move the selection, and the preview says the numbers of the turn that is selected.
    await press("ArrowDown");
    assert.equal(await wait(`${selected}==='fix the tests'`), true);
    assert.match(await evaluate<string>(consequence), /Turns 4 to 5 \(2 turns\) are removed from this session\.\nThe session then ends after turn 3\.[\s\S]*Files changed by these turns are not restored\./);
    assert.equal(await evaluate(`${dialog}.querySelector('.branch-preview-heading').innerText.replace(/\\s+/g,' ').trim().startsWith('Turn 4') && ${dialog}.querySelector('.branch-preview-heading .branch-tag').textContent`), "Failed");
    await press("PageDown");
    assert.equal(await wait(`${selected}==='review the parser'`), true);
    assert.match(await evaluate<string>(consequence), /Turns 1 to 5 \(5 turns\) are removed from this session\.\nThe session is then empty\./);
    await press("PageUp");
    assert.equal(await wait(`${dialog}.querySelector('[role=option][aria-selected=true]').classList.contains('branch-row-end')`), true);
    // The conversation behind is told the prompt of the turn that is selected, and nothing for the end of the session.
    assert.deepEqual(await state("points"), ["5000", "4000", "1000", null]);

    // The end of the session is no turn to rewind to: the window says so, and Enter does nothing.
    assert.equal(await evaluate(`${dialog}.querySelector('.branch-block').innerText.trim() + '|' + ${act}.disabled`), "A rewind goes back to a turn: choose the turn to rewind to.|true");
    await press("Enter");
    assert.deepEqual(await state("rewinds"), []);
    assert.equal(await evaluate(`!!${dialog}`), true);

    // Tab in the filter switches to Fork: the end of the session is where a fork starts from.
    await press("Tab");
    assert.equal(await wait(`${mode}==='fork'`), true);
    assert.equal(await evaluate(`${dialog}.querySelector('[role=radio][aria-checked=true]').textContent + '|' + ${act}.textContent + '|' + ${act}.disabled + '|' + !${dialog}.querySelector('.branch-keep')`), "Fork|Fork|false|true");
    assert.match(await evaluate<string>(consequence), /A new session starts with all 5 turns of this session\.\nThis session is not changed\.\nThe composer of the new session starts empty\./);
    assert.equal(await evaluate(`document.activeElement===${dialog}.querySelector('input[type=search]')`), true, "Tab keeps the focus in the filter.");
    // Back to Rewind from the end of the session: the last turn is selected instead.
    await press("Tab");
    assert.equal(await wait(`${mode}==='rewind' && ${selected}==='run it again'`), true);
    await press("Tab");
    assert.equal(await wait(`${mode}==='fork'`), true);

    // Typing filters the turns by their prompt; the end of the session has none.
    await type("the");
    assert.equal(await wait(`${dialog}.querySelectorAll('[role=option]').length===3`), true);
    assert.equal(await evaluate(rows), "4|2|1");
    assert.equal(await evaluate(selected), "fix the tests");
    await type(" sky");
    assert.equal(await wait(`${dialog}.querySelectorAll('[role=option]').length===1`), true);
    assert.equal(await evaluate(selected), "what color is the sky?");
    assert.match(await evaluate<string>(consequence), /A new session starts with turn 1 of this session\.\nThis session is not changed\.\nThe prompt of turn 2 is put in the composer of the new session, for you to edit and send\./);
    assert.equal(await evaluate(`${dialog}.querySelector('.branch-note').textContent`), "This turn: 1 answer, 2 tool calls.");
    await type("zzz");
    assert.equal(await wait(`${dialog}.querySelector('.reference-empty')?.textContent==='No turn matches.'`), true);
    assert.equal(await evaluate(`${act}.disabled`), true);
    await evaluate(`(()=>{const input=${dialog}.querySelector('input[type=search]');input.select();})()`);
    await type("the");
    assert.equal(await wait(`${dialog}.querySelectorAll('[role=option]').length===3`), true);

    // Ctrl+O lists every turn: the one an agent sent says so, and a fork before it puts nothing in the composer.
    await press("o", true);
    assert.equal(await wait(`${dialog}.querySelectorAll('[role=option]').length===4`), true);
    assert.equal(await evaluate(rows), "4|3|2|1");
    assert.equal(await evaluate(`${dialog}.querySelector('.branch-filter input[type=checkbox]').checked`), false);
    await press("ArrowDown");
    assert.equal(await wait(`${selected}==='the review of the child is done'`), true);
    assert.equal(await evaluate(`${dialog}.querySelector('[role=option][aria-selected=true] .branch-tag').textContent + '|' + ${dialog}.querySelector('.branch-sender').textContent`), "Agent|Sent by an agent session");
    assert.match(await evaluate<string>(consequence), /A new session starts with turns 1 to 2 of this session\.\nThis session is not changed\.\nTurn 3 was not started by a prompt of yours: the composer of the new session stays empty\./);

    // Enter makes the fork: the buttons wait for the answer, a refusal keeps the window open with its reason.
    await evaluate("branchFixture.answer('session_in_use', true)");
    await press("Enter");
    assert.equal(await wait(`${dialog}.querySelector('.branch-outcome [role=status]')?.textContent==='Creating the fork…'`), true);
    assert.equal(await evaluate(`${act}.disabled + '|' + [...${dialog}.querySelectorAll('.branch-actions .bp6-button')].every(button=>button.disabled) + '|' + [...${dialog}.querySelectorAll('[role=radio]')].every(button=>button.disabled)`), "true|true|true");
    await press("Enter");
    await press("Escape");
    assert.deepEqual(await state("forks"), ["run-3"], "A change that is under way is neither sent again nor dismissed.");
    assert.equal(await evaluate(`!!${dialog}`), true);
    await evaluate("branchFixture.release()");
    assert.equal(await wait(`${dialog}.querySelector('.branch-outcome [role=alert]')?.textContent==='The session is working or being changed. Wait until it is idle, then try again.'`), true);
    assert.equal(await evaluate(`${act}.disabled + '|' + branchFixture.state.closed`), "false|0");
    // An outcome that is not known ends what this window sends.
    await evaluate("branchFixture.answer('fork_unconfirmed')");
    await press("Enter");
    assert.equal(await wait(`${dialog}.querySelector('.branch-outcome [role=alert]')?.textContent.startsWith('The fork may have been created.')`), true);
    assert.equal(await evaluate(`${act}.disabled`), true);
    await press("Enter");
    assert.deepEqual(await state("forks"), ["run-3", "run-3"]);
    // Escape closes the window, and the focus goes back to where it was.
    await press("Escape");
    assert.equal(await wait(`!${dialog} && branchFixture.state.closed===1`), true);
    assert.equal(await wait("document.activeElement?.id==='origin'"), true);

    // A message of the timeline opens the window on its own turn, whoever sent it: an agent's turn is then listed.
    await evaluate("branchFixture.state.forks.length=0; branchFixture.answer(null); branchFixture.show('fork','3000')");
    assert.equal(await wait(`${dialog} && ${selected}==='the review of the child is done'`), true);
    assert.equal(await evaluate(`${selected} + '|' + ${dialog}.querySelector('.branch-filter input[type=checkbox]').checked + '|' + ${mode}`), "the review of the child is done|false|fork");
    // A message sent while a turn ran belongs to that turn.
    await evaluate("branchFixture.close(); branchFixture.show('fork','2400')");
    assert.equal(await wait(`${dialog} && ${selected}==='what color is the sky?'`), true);
    // The fork is made: the right turn is asked for, once, and the window closes.
    await press("Enter");
    assert.equal(await wait(`!${dialog}`), true);
    assert.deepEqual(await state("forks"), ["run-2"]);
    assert.equal(await state("closed"), 2);

    // A rewind that does not keep what it removes says so, is remembered, and is sent as chosen.
    await evaluate("branchFixture.show('rewind','4000')");
    assert.equal(await wait(`${dialog} && ${selected}==='fix the tests'`), true);
    await evaluate(`${dialog}.querySelector('.branch-keep input').click()`);
    assert.equal(await wait(`${act}.textContent==='Rewind and delete'`), true);
    assert.match(await evaluate<string>(consequence), /The removed turns are deleted: they cannot be brought back\./);
    assert.equal(await evaluate(`${act}.classList.contains('bp6-intent-danger')`), true);
    assert.deepEqual(await state("keeps"), [false]);
    await evaluate(`${dialog}.querySelector('input[type=search]').focus()`);
    await press("Enter");
    assert.equal(await wait(`!${dialog}`), true);
    assert.deepEqual(await state("rewinds"), [["run-4", false]]);

    // While the session works a rewind is not offered, and the reason is said; a fork before the turn that runs is.
    await evaluate(`branchFixture.turns([{prompt:'first'},{prompt:'second'},{prompt:'third',status:'running',endedAt:null}],'run-3')`);
    await evaluate("branchFixture.state.rewinds.length=0; branchFixture.state.forks.length=0; branchFixture.show('rewind',null,{running:true})");
    assert.equal(await wait(`${dialog} && ${selected}==='third'`), true);
    assert.equal(await evaluate(`${dialog}.querySelector('[role=option][aria-selected=true] .branch-tag').textContent + '|' + ${dialog}.querySelector('.branch-block').innerText.trim() + '|' + ${act}.disabled`),
      "Running|The session is working: it can be rewound once it is idle. A fork is possible now.|true");
    await press("Enter");
    assert.deepEqual(await state("rewinds"), []);
    await press("ArrowDown");
    assert.equal(await wait(`${selected}==='second'`), true);
    assert.equal(await evaluate(`${act}.disabled`), true, "No turn of a session that works can be rewound to.");
    await press("ArrowUp");
    await press("ArrowUp");
    await press("Tab");
    // At the end, the turn that runs is not copied; before it, a fork is the turns that ended.
    assert.equal(await wait(`${mode}==='fork' && ${dialog}.querySelector('[role=option][aria-selected=true]').classList.contains('branch-row-end')`), true);
    assert.match(await evaluate<string>(consequence), /A new session starts with all 2 turns of this session\.\nThe turn that is running now is not copied\./);
    await press("ArrowDown");
    assert.equal(await wait(`${selected}==='third'`), true);
    assert.match(await evaluate<string>(consequence), /A new session starts with turns 1 to 2 of this session\./);
    assert.equal(await evaluate(`${act}.disabled`), false);
    await press("Escape");
    assert.equal(await wait(`!${dialog}`), true);
    // The other reasons a pane knows: a question that waits, a task in the background, a session that is read-only.
    for (const [facts, reason] of [["{question:true}", "A question of this session waits for your answer. Answer it before a rewind."],
      ["{background:2}", "A command or a task of this session still runs in the background. Wait for it or stop it before a rewind."],
      ["{canMutate:false}", "This session is read-only here: it cannot be forked or rewound."]] as const) {
      await evaluate(`branchFixture.turns([{prompt:'first'},{prompt:'second'}]); branchFixture.show('rewind',null,${facts})`);
      assert.equal(await wait(`${dialog} && ${selected}==='second'`), true);
      assert.equal(await evaluate(`${dialog}.querySelector('.branch-block').innerText.trim() + '|' + ${act}.disabled`), `${reason}|true`);
      await evaluate("branchFixture.close()");
    }
    // A draft in the composer is kept by a rewind: the window says the prompt is offered instead.
    await evaluate("branchFixture.show('rewind',null,{draft:true})");
    assert.equal(await wait(`${dialog} && ${selected}==='second'`), true);
    assert.match(await evaluate<string>(consequence), /The composer holds a draft, which is kept: the prompt of turn 2 is offered for you to copy instead\./);
    await evaluate("branchFixture.close()");

    // A long session: the last hundred turns, and a row that loads the ones before, which join the list.
    await evaluate(`branchFixture.turns(Array.from({length:150},(_,index)=>({prompt:'step '+(index+1)}))); branchFixture.state.reads.length=0; branchFixture.show('fork')`);
    assert.equal(await wait(`${dialog} && ${dialog}.querySelectorAll('[role=option]').length===102`), true);
    assert.equal(await evaluate(`${dialog}.querySelector('.reference-status').textContent + '|' + ${dialog}.querySelector('.branch-row-more').textContent`), "150 turns|Load older turns50 more");
    await press("PageUp");
    for (let step = 0; step < 17; step++) await press("PageDown");
    assert.equal(await wait(`${dialog}.querySelector('[role=option][aria-selected=true]').classList.contains('branch-row-more')`), true);
    assert.equal(await evaluate(`${dialog}.querySelector('.branch-preview .reference-empty').textContent + '|' + ${act}.disabled`), "Press Enter to list the turns before turn 51.|true");
    // The selected row is kept in view.
    assert.equal(await evaluate(`(()=>{const list=${dialog}.querySelector('[role=listbox]').getBoundingClientRect(), row=${dialog}.querySelector('[aria-selected=true]').getBoundingClientRect();return row.top>=list.top-1&&row.bottom<=list.bottom+1})()`), true);
    await press("Enter");
    assert.equal(await wait(`${dialog}.querySelectorAll('[role=option]').length===151 && !${dialog}.querySelector('.branch-row-more')`), true);
    assert.deepEqual(await reads(), [null, 51]);
    assert.deepEqual(await state("forks"), []);
    assert.equal(await evaluate(`[...${dialog}.querySelectorAll('[role=option] .branch-number')].slice(-2).map(number=>number.textContent).join('|')`), "2|1");
    // A message whose turn is older than the last hundred: the pages before are read until it is found.
    await evaluate("branchFixture.close(); branchFixture.state.reads.length=0; branchFixture.show('rewind','20000')");
    assert.equal(await wait(`${dialog} && ${selected}==='step 20'`), true);
    assert.deepEqual(await reads(), [null, 51]);
    assert.match(await evaluate<string>(consequence), /Turns 20 to 150 \(131 turns\) are removed from this session\.\nThe session then ends after turn 19\./);

    // The window fits a narrow page: the list goes above the preview, and nothing that scrolls does so sideways.
    await command("Emulation.setDeviceMetricsOverride", { width: 560, height: 620, deviceScaleFactor: 1, mobile: false });
    assert.equal(await wait(`getComputedStyle(${dialog}.querySelector('.branch-panes')).gridTemplateColumns.split(' ').length===1`), true);
    assert.equal(await evaluate(`[...${dialog}.querySelectorAll('.app-window-body,.branch-panes,.branch-list,.branch-preview')].every(part=>part.scrollWidth<=part.clientWidth+1) && ${dialog}.querySelector('.app-window').getBoundingClientRect().right<=innerWidth`), true);
  } finally {
    // Edge's launcher can exit while the browser it started goes on: the browser itself is asked to close.
    if (socket?.readyState === WebSocket.OPEN) socket.send(JSON.stringify({ id: 9999, method: "Browser.close" }));
    socket?.close();
    if (browser && browser.exitCode === null) { const closed = new Promise<void>(resolve => browser!.once("exit", () => resolve())); browser.kill(); await Promise.race([closed, new Promise(resolve => setTimeout(resolve, 5000))]); }
    await rm(root, { recursive: true, force: true, maxRetries: 10, retryDelay: 100 });
  }
});
