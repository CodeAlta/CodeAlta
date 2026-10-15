import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { fileURLToPath, pathToFileURL } from "node:url";
import { build } from "esbuild";
import { browserBaseArgs, browserExecutable, endBrowser, waitForBrowserPort } from "../browserTarget";

test("the saved sessions show the archived sessions of their scope under Archived, where they are restored, read and deleted",
  { skip: !browserExecutable, timeout: 180_000 }, async () => {
  const root = await mkdtemp(join(tmpdir(), "codealta-archived-sessions-"));
  let browser: ReturnType<typeof spawn> | undefined;
  let socket: WebSocket | undefined;
  try {
    await build({ entryPoints: [fileURLToPath(new URL("./archivedSessions.mount.tsx", import.meta.url))], outfile: join(root, "fixture.js"), bundle: true, platform: "browser", format: "iife" });
    await writeFile(join(root, "style.css"), await readFile(new URL("../../node_modules/@blueprintjs/core/lib/css/blueprint.css", import.meta.url), "utf8")
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
    const titles = `[...${dialog}.querySelectorAll('tbody td[data-column="title"] strong')].map(cell=>cell.textContent)`;
    const archivedButton = `${dialog}.querySelector('.session-browser-archived')`;
    const footerButton = (text: string) => `[...${dialog}.querySelectorAll('.session-browser-footer button')].find(button=>button.textContent===${JSON.stringify(text)})`;
    const confirm = "document.querySelector('.confirm-form')";
    const state = <T,>(name: string) => evaluate<T>(`archivedFixture.state.${name}`);
    await command("Page.navigate", { url: pathToFileURL(page).href });
    assert.equal(await wait("window.archivedFixture"), true);

    // The sessions of the scope, without the archived ones; the button says how many are archived in the scope.
    await evaluate("archivedFixture.show()");
    assert.equal(await wait(`${dialog} && ${titles}.length===1`), true);
    assert.deepEqual(await evaluate(titles), ["Draft the notes"]);
    assert.equal(await evaluate(`${archivedButton}.getAttribute('aria-pressed')`), "false");
    assert.equal(await evaluate(`${archivedButton}.textContent`), "Archived2");

    // Archived: the archived sessions of the scope, the one archived last first, with when each was archived.
    await evaluate(`${archivedButton}.click()`);
    assert.equal(await wait(`${archivedButton}.getAttribute('aria-pressed')==='true' && ${titles}.length===2`), true);
    assert.deepEqual(await evaluate(titles), ["Plan the release", "Fix the parser"]);
    assert.equal(await evaluate(`${dialog}.querySelector('th[data-column="updated"]').textContent`), "Archived");
    assert.equal(await evaluate(`${dialog}.querySelector('table').getAttribute('aria-label')`), "Archived sessions");
    assert.equal(await evaluate(`${dialog}.querySelector('tbody time').getAttribute('datetime')`), "2026-10-15T08:00:00.000Z");
    assert.equal(await evaluate(`!!${footerButton("Open")}`), false, "An archived session is restored, not opened.");
    assert.equal(await evaluate(`[...${dialog}.querySelectorAll('.app-window-body, .session-browser-results, .session-browser-footer, .session-browser-toolbar')].every(part=>part.scrollWidth<=part.clientWidth+1)`), true,
      await evaluate<string>(`JSON.stringify([...${dialog}.querySelectorAll('.app-window-body, .session-browser-results, .session-browser-footer, .session-browser-toolbar')].map(part=>[part.className, part.scrollWidth, part.clientWidth, [...part.children].map(child=>child.className+':'+Math.round(child.getBoundingClientRect().width)).join(',')]))`));

    // Typing filters them too; Info shows what is recorded of the selected one.
    await evaluate(`(()=>{const input=${dialog}.querySelector('input[type=search]');const set=Object.getOwnPropertyDescriptor(HTMLInputElement.prototype,'value').set;set.call(input,'parser');input.dispatchEvent(new Event('input',{bubbles:true}))})()`);
    assert.equal(await wait(`${titles}.length===1 && ${titles}[0]==='Fix the parser'`), true);
    await evaluate(`${footerButton("Info…")}.click()`);
    assert.deepEqual(await state("info"), ["older"]);

    // Delete asks beside its button, with Do not ask again; what went wrong is said in the question.
    await evaluate("archivedFixture.answer('The session could not be deleted.')");
    await evaluate(`${footerButton("Delete…")}.click()`);
    assert.equal(await wait(confirm), true);
    assert.match(await evaluate<string>(`${confirm}.textContent`), /Delete this session\?.*Fix the parser.*Its history is deleted/s);
    assert.equal(await evaluate(`!!${confirm}.querySelector('input[type=checkbox]')`), true);
    assert.equal(await evaluate(`${dialog}.contains(${confirm})`), true, "The question opens in the modal window, not under it.");
    await evaluate(`[...${confirm}.querySelectorAll('button')].find(button=>button.textContent==='Delete').click()`);
    assert.equal(await wait(`${confirm}?.querySelector('.confirm-error')?.textContent==='The session could not be deleted.'`), true);
    assert.deepEqual(await state("removed"), [["older", false]]);
    await evaluate("archivedFixture.answer(null)");
    await evaluate(`[...${confirm}.querySelectorAll('button')].find(button=>button.textContent==='Delete').click()`);
    assert.equal(await wait(`!${confirm}`), true);

    // Enter restores the selected session; the browser closes for it to be shown.
    await evaluate(`${dialog}.querySelector('input[type=search]').dispatchEvent(new KeyboardEvent('keydown',{key:'Enter',bubbles:true}))`);
    assert.equal(await wait(`!${dialog}`), true);
    assert.deepEqual(await state("restored"), ["older"]);

    // Without a question, Delete deletes at once; Restore restores the one archived last.
    await evaluate("archivedFixture.show(false)");
    assert.equal(await wait(`${dialog} && ${archivedButton}`), true);
    await evaluate(`${archivedButton}.click()`);
    assert.equal(await wait(`${titles}.length===2`), true);
    await evaluate(`${footerButton("Delete")}.click()`);
    assert.equal(await wait("archivedFixture.state.removed.length===3"), true);
    assert.deepEqual((await state<[string, boolean][]>("removed"))[2], ["old", false]);
    assert.equal(await evaluate(`!!${confirm}`), false);
    await evaluate(`${footerButton("Restore")}.click()`);
    assert.equal(await wait(`!${dialog}`), true);
    assert.deepEqual(await state("restored"), ["older", "old"]);
  } finally {
    socket?.close();
    await endBrowser(browser);
    await rm(root, { recursive: true, force: true, maxRetries: 8, retryDelay: 100 });
  }
});
