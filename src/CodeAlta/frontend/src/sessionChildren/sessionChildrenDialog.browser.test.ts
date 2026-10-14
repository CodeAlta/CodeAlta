import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { fileURLToPath, pathToFileURL } from "node:url";
import { build } from "esbuild";
import { browserBaseArgs, browserExecutable, endBrowser, waitForBrowserPort } from "../browserTarget";

test("the child sessions of a session show their tree across projects, chats and spaces, and are deleted with it only once asked",
  { skip: !browserExecutable, timeout: 180_000 }, async () => {
  const root = await mkdtemp(join(tmpdir(), "codealta-session-children-"));
  let browser: ReturnType<typeof spawn> | undefined;
  let socket: WebSocket | undefined;
  try {
    await build({ entryPoints: [fileURLToPath(new URL("./sessionChildrenDialog.mount.tsx", import.meta.url))], outfile: join(root, "fixture.js"), bundle: true, platform: "browser", format: "iife" });
    await writeFile(join(root, "style.css"), await readFile(new URL("../../node_modules/@blueprintjs/core/lib/css/blueprint.css", import.meta.url), "utf8")
      + await readFile(new URL("../style.css", import.meta.url), "utf8") + await readFile(new URL("./sessionChildren.css", import.meta.url), "utf8"));
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
    const dialog = "document.querySelector('dialog.session-children-dialog[open]')";
    const rows = `[...${dialog}.querySelectorAll('.session-children-row')]`;
    const deleteButton = `[...${dialog}.querySelectorAll('.session-children-delete button')].find(button=>button.textContent.includes('Delete with all'))`;
    const confirm = "document.querySelector('.confirm-form')";
    const state = <T,>(name: string) => evaluate<T>(`childrenFixture.state.${name}`);
    await command("Page.navigate", { url: pathToFileURL(page).href });
    assert.equal(await wait("window.childrenFixture"), true);

    // Opened by Delete: the window says what Delete does, and shows the whole tree under the session.
    await evaluate("childrenFixture.show(true)");
    assert.equal(await wait(`${dialog} && ${rows}.length===4`), true);
    assert.match(await evaluate<string>(`${dialog}.querySelector('.session-children-note').textContent`), /Delete removes this session only, and not while it has child sessions/);
    assert.equal(await evaluate(`${dialog}.querySelector('h2').textContent.trim()`), "Child sessions of Plan the release");
    assert.match(await evaluate<string>(`${dialog}.querySelector('.reference-status').textContent`), /2 direct · 3 in all/);
    // Each row says where its session is: its project or the chats, and its space; it is indented under its parent.
    assert.deepEqual(await evaluate(`${rows}.map(row=>[row.querySelector('.session-children-title').textContent, row.querySelector('.session-children-scope').textContent,
      row.querySelector('.session-children-space').textContent, parseFloat(row.style.paddingInlineStart)].join('|'))`),
      ["Plan the release|Chat|Work|10", "Review the parser|Parser|Personal|28", "Write the tests|Chat|Personal|46", "Summarize|Chat|Work|28"]);
    assert.equal(await evaluate(`${rows}[2].getAttribute('aria-label')`), "Write the tests, child of Review the parser");
    assert.equal(await evaluate(`[...${dialog}.querySelectorAll('.app-window-body, .session-children-list')].every(part=>part.scrollWidth<=part.clientWidth+1)`), true);

    // Delete with all of them is asked about on its own, Cancel first; the answer names the sessions the user saw.
    assert.equal(await evaluate(`${deleteButton}.disabled`), false);
    await evaluate(`${deleteButton}.click()`);
    assert.equal(await wait(confirm), true);
    assert.match(await evaluate<string>(`${confirm}.textContent`), /Delete 4 sessions\?.*Plan the release.*the 3 sessions below it are deleted with their history\. Projects and their files are kept\./s);
    assert.equal(await evaluate(`${confirm}.querySelector('input[type=checkbox]')`), null, "There is no Do not ask again: it is asked every time.");
    assert.equal(await wait(`document.activeElement?.textContent==='Cancel'`), true);
    await evaluate("childrenFixture.answer('descendants_changed')");
    const reads = await state<number>("reads");
    await evaluate(`[...${confirm}.querySelectorAll('button')].find(button=>button.textContent==='Delete all').click()`);
    assert.equal(await wait(`${dialog}.querySelector('.session-children-status').textContent.includes('The child sessions changed')`), true);
    assert.deepEqual(await state("deletes"), [["child", "grandchild", "second"]]);
    assert.equal(await wait(`childrenFixture.state.reads>${reads}`), true, "The tree is read again.");
    assert.equal(await evaluate(`!!${confirm}`), false);

    await evaluate("childrenFixture.answer(null)");
    await evaluate(`${deleteButton}.click()`);
    assert.equal(await wait(confirm), true);
    await evaluate(`[...${confirm}.querySelectorAll('button')].find(button=>button.textContent==='Delete all').click()`);
    assert.equal(await wait(`!${dialog}`), true, "The window closes once everything is deleted.");
    assert.equal(await state("closed"), 1);

    // A session at work keeps the tree: the window says which, and does not offer the deletion.
    await evaluate("childrenFixture.show(false, true)");
    assert.equal(await wait(`${dialog} && ${rows}.length===4`), true);
    assert.match(await evaluate<string>(`${dialog}.querySelector('.session-children-note').textContent`), /Delete removes one session only/);
    assert.equal(await evaluate(`${rows}.filter(row=>row.querySelector('.session-children-work')).map(row=>row.querySelector('.session-children-title').textContent).join()`), "Write the tests");
    assert.equal(await evaluate(`${deleteButton}.disabled`), true);
    assert.equal(await evaluate(`${dialog}.querySelector('.session-children-status').textContent`), "Wait for the sessions at work to finish.");
    // A row opens its session, wherever it is.
    await evaluate(`${rows}[1].click()`);
    assert.equal(await wait(`!${dialog}`), true);
    assert.deepEqual(await state("opened"), ["child"]);
  } finally {
    socket?.close();
    await endBrowser(browser);
    await rm(root, { recursive: true, force: true, maxRetries: 8, retryDelay: 100 });
  }
});
