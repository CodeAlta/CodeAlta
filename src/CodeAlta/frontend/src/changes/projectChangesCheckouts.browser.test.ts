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

// The real Changes tab over a played host: which checkout it asks the host for, and which row it shows as the one chosen.
test("the Changes tab keeps the chosen checkout and collapses its sidebar without losing the comparison", { skip: !edge, timeout: 180_000 }, async () => {
  const root = await mkdtemp(join(tmpdir(), "codealta-changes-checkouts-"));
  let browser: ReturnType<typeof spawn> | undefined;
  let socket: WebSocket | undefined;
  try {
    await build({ entryPoints: [fileURLToPath(new URL("./projectChangesCheckouts.mount.tsx", import.meta.url))], outfile: join(root, "fixture.js"),
      bundle: true, platform: "browser", format: "iife", loader: { ".ttf": "dataurl" }, plugins: [{ name: "fixture", setup(bundle) {
        // The tab is given the host it talks to: the generated client is not part of this page.
        bundle.onResolve({ filter: /^#neoastra$/ }, () => ({ path: fileURLToPath(new URL("../demo-api.ts", import.meta.url)) }));
        bundle.onResolve({ filter: /^monaco-editor\/.*\?worker$/ }, args => ({ path: args.path, namespace: "fixture-worker" }));
        bundle.onLoad({ filter: /.*/, namespace: "fixture-worker" }, async () => {
          const worker = await build({ entryPoints: [fileURLToPath(new URL("../../node_modules/monaco-editor/esm/vs/editor/editor.worker.js", import.meta.url))],
            bundle: true, platform: "browser", format: "iife", write: false });
          return { loader: "js", contents: `export default class extends Worker { constructor() {
            const url=URL.createObjectURL(new Blob([${JSON.stringify(worker.outputFiles[0].text)}],{type:"text/javascript"}));
            super(url); URL.revokeObjectURL(url);
          } }` };
        });
      } }] });
    await writeFile(join(root, "style.css"), await readFile(new URL("../../node_modules/@blueprintjs/core/lib/css/blueprint.css", import.meta.url), "utf8")
      + await readFile(new URL("../style.css", import.meta.url), "utf8") + await readFile(new URL("../worktrees/worktrees.css", import.meta.url), "utf8"));
    const page = join(root, "fixture.html");
    await writeFile(page, '<!doctype html><html><head><meta charset="utf-8"><link rel="stylesheet" href="fixture.css"><link rel="stylesheet" href="style.css"></head>'
      + '<body class="bp6-dark" style="margin:0"><div id="root"></div><script src="fixture.js"></script></body></html>');
    browser = spawn(edge!, [...browserBaseArgs, "--allow-file-access-from-files", `--user-data-dir=${join(root, "profile")}`, "--remote-debugging-port=0", "--window-size=1200,800", "about:blank"],
      { stdio: ["ignore", "ignore", "pipe"], windowsHide: true });
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
    await command("Page.navigate", { url: pathToFileURL(page).href });
    assert.equal(await wait("window.checkoutsFixture"), true);

    // The rows by the name each shows, the ones shown as chosen, and the ones that can be removed.
    const rows = "[...document.querySelectorAll('.worktree-row')]";
    const names = `${rows}.map(row => row.querySelector('strong').textContent)`;
    const chosen = `${rows}.filter(row => row.querySelector('button[role=option]').getAttribute('aria-selected') === 'true').map(row => row.querySelector('strong').textContent)`;
    const removable = `${rows}.filter(row => row.querySelector('.worktree-row-remove')).map(row => row.querySelector('strong').textContent)`;
    const press = (name: string) => evaluate(`${rows}.find(row => row.querySelector('strong').textContent === ${JSON.stringify(name)}).querySelector('button[role=option]').click()`);
    const shownRoot = "document.querySelector('.changes-project-path')?.textContent";
    const lastAsked = "checkoutsFixture.state.asked.at(-1)";
    // The list is read again, as the tab does by itself every few seconds, and the answer has arrived.
    const refresh = async () => {
      const lists = await evaluate<number>("checkoutsFixture.state.lists"), asked = await evaluate<number>("checkoutsFixture.state.asked.length");
      await evaluate("document.querySelector('.changes-refresh').click()");
      assert.equal(await wait(`checkoutsFixture.state.lists > ${lists} && checkoutsFixture.state.asked.length > ${asked}`), true);
      await new Promise(resolve => setTimeout(resolve, 150));
    };

    // A project registered in a linked worktree, asked to show the main checkout of its repository.
    await evaluate("checkoutsFixture.show({ path: null, worktree: checkoutsFixture.repository.folder })");
    assert.equal(await wait(`${rows}.length === 3 && ${shownRoot} === 'C:\\\\code\\\\repository'`), true);
    assert.deepEqual(await evaluate(names), ["repository", "App", "other"]);
    // The row of the main checkout is the one shown as chosen, and only that one: the checkout of the project is another row.
    assert.deepEqual(await evaluate(chosen), ["repository"]);
    assert.equal(await evaluate(lastAsked), "C:\\code\\repository");
    assert.equal(await evaluate("document.querySelector('.changes-worktree').textContent"), "repository");
    // What was asked for is still shown after the list was read again, twice.
    await refresh();
    await refresh();
    assert.deepEqual(await evaluate(chosen), ["repository"]);
    assert.equal(await evaluate(lastAsked), "C:\\code\\repository");
    assert.equal(await evaluate(shownRoot), "C:\\code\\repository");

    // Each of the three rows shows its own checkout.
    await press("App");
    assert.equal(await wait(`${shownRoot} === 'C:\\\\trees\\\\app\\\\home'`), true);
    assert.deepEqual(await evaluate(chosen), ["App"]);
    assert.equal(await evaluate(lastAsked), null, "The checkout of the project is asked as the folder of the project.");
    assert.equal(await evaluate("!!document.querySelector('.changes-worktree')"), false);
    await press("other");
    assert.equal(await wait(`${shownRoot} === 'C:\\\\trees\\\\app\\\\other'`), true);
    assert.deepEqual(await evaluate(chosen), ["other"]);
    assert.equal(await evaluate(lastAsked), "C:\\trees\\app\\other");
    await press("repository");
    assert.equal(await wait(`${shownRoot} === 'C:\\\\code\\\\repository'`), true);
    assert.deepEqual(await evaluate(chosen), ["repository"]);
    await refresh();
    assert.deepEqual(await evaluate(chosen), ["repository"]);

    // Neither the main checkout nor the checkout of the project has a button to remove it; the other worktree has.
    assert.deepEqual(await evaluate(removable), ["other"]);

    // A checkout that is gone from the list gives way to the folder of the project, as before.
    await press("other");
    assert.equal(await wait(`${shownRoot} === 'C:\\\\trees\\\\app\\\\other'`), true);
    await evaluate("checkoutsFixture.state.rows = [checkoutsFixture.repository, checkoutsFixture.home]");
    await refresh();
    assert.equal(await wait(`${shownRoot} === 'C:\\\\trees\\\\app\\\\home'`), true);
    assert.deepEqual(await evaluate(chosen), ["App"]);

    // A project in the main checkout of its repository, as most are: nothing changes for it.
    await evaluate("checkoutsFixture.clear(); checkoutsFixture.state.rows = [checkoutsFixture.plain, checkoutsFixture.other]; checkoutsFixture.state.projectFolder = checkoutsFixture.plain.folder; checkoutsFixture.state.asked = []; checkoutsFixture.show()");
    assert.equal(await wait(`${rows}.length === 2 && ${shownRoot} === 'C:\\\\code\\\\plain'`), true);
    assert.deepEqual(await evaluate(names), ["App", "other"]);
    assert.deepEqual(await evaluate(chosen), ["App"]);
    assert.deepEqual(await evaluate("[...new Set(checkoutsFixture.state.asked)]"), [null]);
    assert.deepEqual(await evaluate(removable), ["other"]);
    await evaluate("checkoutsFixture.show({ path: null, worktree: checkoutsFixture.other.folder })");
    assert.equal(await wait(`${shownRoot} === 'C:\\\\trees\\\\app\\\\other'`), true);
    await refresh();
    assert.deepEqual(await evaluate(chosen), ["other"]);
    await press("App");
    assert.equal(await wait(`${shownRoot} === 'C:\\\\code\\\\plain'`), true);
    assert.deepEqual(await evaluate(chosen), ["App"]);

    // Empty checkouts can hide the worktrees too, without losing the way back to them.
    const toggle = "document.querySelector('.changes-side-toggle')";
    const sidebar = "document.querySelector('.changes-files')";
    assert.equal(await evaluate(`${toggle}?.getAttribute('aria-expanded')`), "true");
    await evaluate(`${toggle}.focus(); ${toggle}.click()`);
    assert.equal(await wait(`${sidebar} && !${sidebar}.checkVisibility()`), true);
    assert.equal(await evaluate(`${toggle}.getAttribute('aria-label')`), "Show the files");
    assert.equal(await evaluate("document.activeElement.classList.contains('changes-side-toggle')"), true);
    // A native keyboard activation reopens it; hidden descendants must not be tab stops.
    await command("Input.dispatchKeyEvent", { type: "keyDown", key: "Enter", code: "Enter", windowsVirtualKeyCode: 13 });
    await command("Input.dispatchKeyEvent", { type: "keyUp", key: "Enter", code: "Enter", windowsVirtualKeyCode: 13 });
    assert.equal(await wait(`${sidebar}.checkVisibility()`), true);
    assert.deepEqual(await evaluate(chosen), ["App"]);

    // A selected commit and file survive hiding the whole sidebar: files, worktrees and history.
    await evaluate(`checkoutsFixture.clear();
      localStorage.setItem('codealta.desktop.changes.v1', JSON.stringify({ autoRefresh: false, listWidth: 333 }));
      checkoutsFixture.state.files = ['a.ts', 'src/b.ts'].map(path => ({ path, originalPath: null, status: 'modified', insertions: 1, deletions: 1, binary: false, revision: 'f' }));
      checkoutsFixture.state.commits = [{ id: 'a'.repeat(40), shortId: 'aaaaaaa', author: 'Test', time: '2026-10-11T12:00:00Z', subject: 'A commit' }];
      checkoutsFixture.show({ path: 'src/b.ts', worktree: checkoutsFixture.other.folder });`);
    assert.equal(await wait("document.querySelector('.changes-file[data-path=\"src/b.ts\"][aria-selected=true]') && document.querySelector('[data-commit]')"), true);
    await evaluate("document.querySelector('[data-commit]').click()");
    assert.equal(await wait("document.querySelector('.changes-scope')?.textContent.includes('aaaaaaa') && document.querySelector('.changes-diff .monaco-editor')"), true);
    await evaluate("document.querySelector('.changes-file[data-path=\"src/b.ts\"]').click()");
    assert.equal(await wait("document.querySelector('.changes-diff-path strong')?.textContent === 'b.ts'"), true);
    const before = await evaluate<number>("document.querySelector('.changes-diff').clientWidth");
    await evaluate("window.keptDiff = document.querySelector('.changes-diff .monaco-editor')");
    await evaluate(`${toggle}.click()`);
    assert.equal(await wait(`${sidebar} && !${sidebar}.checkVisibility()`), true);
    assert.equal(await evaluate("document.querySelector('.changes-diff').clientWidth > " + before + " + 300"), true);
    assert.equal(await evaluate("document.querySelector('.changes-diff .monaco-editor') === window.keptDiff"), true, "The diff is not remounted.");
    assert.equal(await evaluate("document.querySelector('.changes-scope').textContent.includes('aaaaaaa')"), true);
    assert.equal(await evaluate(lastAsked), "C:\\trees\\app\\other");
    assert.equal(await evaluate("!!document.querySelector('.changes-splitter')?.checkVisibility()"), false);

    await evaluate(`${toggle}.click()`);
    assert.equal(await wait(`${sidebar}.checkVisibility()`), true);
    assert.equal(await evaluate("document.querySelector('.changes-file[aria-selected=true]').dataset.path"), "src/b.ts");
    assert.deepEqual(await evaluate(chosen), ["other"]);
    assert.equal(await evaluate("document.querySelector('[data-commit][aria-selected=true]')?.dataset.commit"), "a".repeat(40));
    await evaluate("document.querySelector('.changes-filter input').focus()");
    await command("Input.insertText", { text: "src/" });
    assert.equal(await wait("document.querySelectorAll('.changes-file').length === 1"), true);
    await evaluate(`${toggle}.click()`);
    assert.equal(await wait("document.activeElement.classList.contains('changes-side-toggle')"), true, "Focus is moved out of a hidden filter.");
    await evaluate(`${toggle}.click()`);
    assert.equal(await wait(`${sidebar}.checkVisibility()`), true);
    assert.equal(await evaluate("document.querySelector('.changes-filter input').value"), "src/");
    await evaluate(`(() => { const input = document.querySelector('.changes-filter input');
      Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set.call(input, ''); input.dispatchEvent(new Event('input', { bubbles: true })); })()`);
    assert.equal(await wait("document.querySelectorAll('.changes-file').length === 2"), true);
    await evaluate(`${toggle}.click()`);

    // All-files mode still navigates while the sidebar is hidden, and the choice survives a new tab.
    await evaluate("document.querySelector('[aria-label=\"All files in one view\"]').click()");
    assert.equal(await wait("document.querySelectorAll('.changes-section').length === 2"), true);
    assert.equal(await evaluate(`${sidebar}.checkVisibility()`), false);
    await evaluate("document.querySelector('[aria-label=\"Previous file\"]').click()");
    assert.equal(await wait("document.querySelector('.changes-position')?.textContent === '1 of 2'"), true);
    await evaluate("checkoutsFixture.clear(); checkoutsFixture.show()");
    assert.equal(await wait(`${toggle}?.getAttribute('aria-expanded') === 'false' && document.querySelector('.changes-all')`), true);
    assert.equal(await evaluate("document.activeElement.classList.contains('changes-side-toggle')"), true, "A collapsed tab can take keyboard focus.");
    await evaluate(`${toggle}.click()`);
    assert.equal(await wait(`${sidebar}.checkVisibility()`), true);
    assert.equal(await evaluate(`${sidebar}.getBoundingClientRect().width`), 333);

    // The toggle remains reachable at narrow widths, in both themes and when reading changes fails.
    await command("Emulation.setDeviceMetricsOverride", { width: 600, height: 700, deviceScaleFactor: 1, mobile: false });
    for (const dark of [true, false]) {
      await evaluate(`document.body.classList.toggle('bp6-dark', ${dark})`);
      await evaluate(`${toggle}.click()`);
      assert.equal(await wait(`${sidebar} && !${sidebar}.checkVisibility()`), true);
      assert.equal(await evaluate(`${toggle}.getBoundingClientRect().right <= innerWidth && document.querySelector('.changes-panel').scrollWidth <= innerWidth`), true);
      await evaluate(`${toggle}.click()`);
      assert.equal(await wait(`${sidebar}.checkVisibility()`), true);
    }
    await evaluate("checkoutsFixture.state.failure = 'not_repository'");
    await refresh();
    assert.equal(await wait("document.querySelector('.changes-empty')?.textContent.includes('No changes to show')"), true);
    await evaluate(`${toggle}.click()`);
    assert.equal(await wait(`${sidebar} && !${sidebar}.checkVisibility()`), true);
    await evaluate(`${toggle}.click()`);
    assert.equal(await wait(`${sidebar}.checkVisibility()`), true);
  } finally {
    // Edge's launcher can exit while the browser it started goes on: the browser itself is asked to close.
    if (socket?.readyState === WebSocket.OPEN) socket.send(JSON.stringify({ id: 9999, method: "Browser.close" }));
    socket?.close();
    if (browser && browser.exitCode === null) { const closed = new Promise<void>(resolve => browser!.once("exit", () => resolve())); browser.kill(); await Promise.race([closed, new Promise(resolve => setTimeout(resolve, 5000))]); }
    await rm(root, { recursive: true, force: true, maxRetries: 10, retryDelay: 100 });
  }
});
