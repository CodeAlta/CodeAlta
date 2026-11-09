import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { fileURLToPath, pathToFileURL } from "node:url";
import { build } from "esbuild";
import { browserBaseArgs, browserExecutable, endBrowser, waitForBrowserPort } from "../browserTarget";

test("sessions checked in the saved sessions are archived, restored and deleted together, the deepest first, never without the sessions below them",
  { skip: !browserExecutable, timeout: 180_000 }, async () => {
  const root = await mkdtemp(join(tmpdir(), "codealta-checked-sessions-"));
  let browser: ReturnType<typeof spawn> | undefined;
  let socket: WebSocket | undefined;
  try {
    await build({ entryPoints: [fileURLToPath(new URL("./checkedSessions.mount.tsx", import.meta.url))], outfile: join(root, "fixture.js"), bundle: true, platform: "browser", format: "iife" });
    await writeFile(join(root, "style.css"), await readFile(new URL("../../node_modules/@blueprintjs/core/lib/css/blueprint.css", import.meta.url), "utf8")
      + await readFile(new URL("../../node_modules/@blueprintjs/table/lib/css/table.css", import.meta.url), "utf8")
      + await readFile(new URL("../style.css", import.meta.url), "utf8"));
    const page = join(root, "fixture.html");
    await writeFile(page, '<!doctype html><html><head><meta charset="utf-8"><link rel="stylesheet" href="style.css"></head><body class="bp6-dark"><div id="root"></div><script src="fixture.js"></script></body></html>');
    browser = spawn(browserExecutable!, [...browserBaseArgs, "--window-size=1200,900",
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
    const dialog = "document.querySelector('dialog.session-browser[open]')";
    const titles = `[...${dialog}.querySelectorAll('.session-browser-name')].map(cell=>cell.textContent)`;
    const archivedButton = `${dialog}.querySelector('.session-browser-archived')`;
    const footerButton = (text: string) => `[...${dialog}.querySelectorAll('.session-browser-footer button')].find(button=>button.textContent===${JSON.stringify(text)})`;
    const confirm = "document.querySelector('.confirm-form')";
    const state = <T,>(name: string) => evaluate<T>(`checkedFixture.state.${name}`);
    await command("Page.navigate", { url: pathToFileURL(page).href });
    assert.equal(await wait("window.checkedFixture"), true);
    const box = (title: string) => `${dialog}.querySelector('input[aria-label="Select ${title}"]')`;
    const all = `${dialog}.querySelector('input[aria-label="Select every session shown"]')`;
    const shiftClick = (title: string) => `${box(title)}.dispatchEvent(new MouseEvent('click',{bubbles:true,shiftKey:true}))`;
    const checkedTitles = `[...${dialog}.querySelectorAll('.session-browser-results input[aria-label^="Select "]:not([aria-label="Select every session shown"])')].filter(input=>input.checked).map(input=>input.getAttribute('aria-label').slice(7))`;
    const status = `${dialog}.querySelector('.checked-footer [role=status]')?.textContent`;

    // The sessions of the project as a tree, the newest first, each with a box; nothing checked, the usual footer.
    await evaluate("checkedFixture.show()");
    assert.equal(await wait(`${dialog} && ${titles}.length===4`), true);
    assert.deepEqual(await evaluate(titles), ["Write the tests", "Review the parser", "Check the lexer", "Plan the release"], "Check the lexer is under the session it comes from.");
    assert.equal(await evaluate(`[...${dialog}.querySelectorAll('.session-browser-title-cell')].map(cell=>parseFloat(cell.style.paddingInlineStart)||0).join()`), "0,0,18,0");
    assert.equal(await evaluate(`!!${footerButton("Open")}`), true);

    // A click checks one; a Shift-click the rows down to it, and the sessions below them; the footer says how many.
    await evaluate(`${box("Write the tests")}.click()`);
    assert.equal(await wait(`${status}==='1 selected'`), true);
    await evaluate(shiftClick("Review the parser"));
    assert.equal(await wait(`${status}==='3 selected'`), true);
    assert.deepEqual(await evaluate(checkedTitles), ["Write the tests", "Review the parser", "Check the lexer"]);
    assert.equal(await evaluate(`${all}.indeterminate`), true);
    assert.equal(await evaluate(`!!${footerButton("Archive 3")} && !!${footerButton("Delete 3…")}`), true);
    // Unchecking a child unchecks the session above it, which is no longer checked whole; checking its last child checks it again.
    await evaluate(`${box("Check the lexer")}.click()`);
    assert.equal(await wait(`${status}==='1 selected'`), true);
    assert.deepEqual(await evaluate(checkedTitles), ["Write the tests"]);
    await evaluate(`${box("Check the lexer")}.click()`);
    assert.equal(await wait(`${status}==='3 selected' && ${box("Review the parser")}.checked && !${box("Review the parser")}.indeterminate`), true);
    // The box of the header checks every session shown, then none.
    await evaluate(`${all}.click()`);
    assert.equal(await wait(`${status}==='4 selected'`), true);
    await evaluate(`${all}.click()`);
    assert.equal(await wait(`!${status} && !!${footerButton("Open")}`), true);

    // A search shows what it finds with the sessions it belongs to, grayed; it keeps what is checked.
    await evaluate(`${box("Plan the release")}.click()`);
    await evaluate(`(()=>{const input=${dialog}.querySelector('input[type=search]');const set=Object.getOwnPropertyDescriptor(HTMLInputElement.prototype,'value').set;set.call(input,'lexer');input.dispatchEvent(new Event('input',{bubbles:true}))})()`);
    assert.equal(await wait(`${titles}.length===2 && ${status}==='1 selected'`), true);
    assert.deepEqual(await evaluate(titles), ["Review the parser", "Check the lexer"]);
    assert.deepEqual(await evaluate(`[...${dialog}.querySelectorAll('.bp6-table-cell.session-browser-title')].map(cell=>cell.classList.contains('session-browser-context'))`), [true, false]);
    assert.match(await evaluate<string>(`${dialog}.querySelector('.session-browser-toolbar [role=status]').textContent`), /^1 of 1 sessions/);
    await evaluate(`(()=>{const input=${dialog}.querySelector('input[type=search]');const set=Object.getOwnPropertyDescriptor(HTMLInputElement.prototype,'value').set;set.call(input,'review');input.dispatchEvent(new Event('input',{bubbles:true}))})()`);
    assert.equal(await wait(`${titles}.length===2`), true, "A match shows the sessions below it.");
    assert.deepEqual(await evaluate(`[...${dialog}.querySelectorAll('.bp6-table-cell.session-browser-title')].map(cell=>cell.classList.contains('session-browser-context'))`), [false, true]);
    await evaluate(`${footerButton("Clear selection")}.click()`);
    assert.equal(await wait(`!${status}`), true);

    // Plan has a child session in another project: it is kept. Tests goes, and its worktree is offered.
    await evaluate(`(()=>{const input=${dialog}.querySelector('input[type=search]');const set=Object.getOwnPropertyDescriptor(HTMLInputElement.prototype,'value').set;set.call(input,'');input.dispatchEvent(new Event('input',{bubbles:true}))})()`);
    assert.equal(await wait(`${titles}.length===4`), true);
    await evaluate(`${box("Write the tests")}.click()`);
    await evaluate(`${box("Plan the release")}.click()`);
    await evaluate(`${footerButton("Delete 2…")}.click()`);
    assert.equal(await wait(confirm), true);
    assert.equal(await evaluate(`${dialog}.contains(${confirm})`), true, "The question opens in the modal window, not under it.");
    assert.match(await evaluate<string>(`${confirm}.textContent`), /Delete this session\?Write the testsIts history is deleted.*One of them has child sessions you did not select: it is kept\..*These sessions work in the worktree quiet-heron/s);
    assert.equal(await evaluate(`[...${confirm}.querySelectorAll('button')].some(button=>button.textContent==='Select their child sessions')`), false, "Its child is not in this list.");
    assert.equal(await wait(`document.activeElement?.textContent==='Cancel'`), true);
    assert.deepEqual(await state("checked"), [["tests"]]);
    await evaluate(`${confirm}.querySelector('input[type=checkbox]').click()`);
    await evaluate(`[...${confirm}.querySelectorAll('button')].find(button=>button.textContent==='Delete').click()`);
    assert.equal(await wait(`!${confirm}`), true);
    assert.deepEqual(await state("removed"), [["tests"]]);
    assert.deepEqual(await state("worktrees"), [["/trees/parser/quiet-heron"]]);
    // What was deleted is no longer checked; what was kept is.
    assert.equal(await wait(`${status}==='1 selected'`), true);
    await evaluate(`${footerButton("Clear selection")}.click()`);

    // Review is checked with its child: both go, the child first.
    await evaluate(`${box("Review the parser")}.click()`);
    assert.equal(await wait(`${status}==='2 selected'`), true);
    await evaluate(`${footerButton("Delete 2…")}.click()`);
    assert.equal(await wait(`${confirm}?.textContent.includes('Delete 2 sessions?')`), true);
    assert.doesNotMatch(await evaluate<string>(`${confirm}.textContent`), /you did not select/);
    // Counted, not named: no session of several matters more than the others.
    assert.equal(await evaluate(`!!${confirm}.querySelector('.confirm-subject')`), false);
    assert.doesNotMatch(await evaluate<string>(`${confirm}.textContent`), /Review the parser|Check the lexer/);
    assert.match(await evaluate<string>(`${confirm}.textContent`), /Delete 2 sessions\?Their history is deleted/);
    await evaluate(`[...${confirm}.querySelectorAll('button')].find(button=>button.textContent==='Delete').click()`);
    assert.equal(await wait(`!${confirm}`), true);
    assert.deepEqual((await state<string[][]>("removed"))[1], ["check", "review"]);

    // Archive archives the checked ones at once.
    await evaluate(`${box("Plan the release")}.click()`);
    await evaluate(`${footerButton("Archive 1")}.click()`);
    assert.equal(await wait(`!${status}`), true);
    assert.deepEqual(await state("archived"), [["plan"]]);

    // Under Archived, the checked ones are restored together; changing the view unchecks everything.
    await evaluate(`${box("Write the tests")}.click()`);
    await evaluate(`${archivedButton}.click()`);
    assert.equal(await wait(`${titles}.length===2 && !${status}`), true);
    await evaluate(`${all}.click()`);
    assert.equal(await wait(`${status}==='2 selected'`), true);
    await evaluate(`${footerButton("Restore 2")}.click()`);
    assert.equal(await wait(`!${status}`), true);
    assert.deepEqual(await state("restored"), [["old", "older"]]);
    assert.equal(await evaluate(`[...${dialog}.querySelectorAll('.app-window-body, .session-browser-results, .session-browser-footer, .session-browser-toolbar')].every(part=>part.scrollWidth<=part.clientWidth+1)`), true);

    // Twelve sessions: every row and every column of the table is drawn, the messages too, not only the first ones.
    await evaluate("checkedFixture.show(8)");
    assert.equal(await wait(`${dialog} && ${titles}.length===12`), true, await evaluate<string>(`String(${titles}.length)`));
    assert.equal(await wait(`${dialog}.querySelectorAll('.session-browser-results .bp6-table-cell.session-browser-messages').length===12`), true);
    assert.equal(await evaluate(`!!${dialog}.querySelector('button[data-column="messages"]')`), true);
  } finally {
    socket?.close();
    await endBrowser(browser);
    await rm(root, { recursive: true, force: true, maxRetries: 8, retryDelay: 100 });
  }
});
