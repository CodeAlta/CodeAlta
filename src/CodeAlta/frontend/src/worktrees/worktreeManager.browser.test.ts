import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { fileURLToPath, pathToFileURL } from "node:url";
import { build } from "esbuild";
import { browserBaseArgs, browserExecutable } from "../browserTarget";

const edge = browserExecutable;

test("the window of the worktrees selects, asks, removes in part and says what became of each worktree", { skip: !edge, timeout: 180_000 }, async t => {
  const root = await mkdtemp(join(tmpdir(), "codealta-worktrees-window-"));
  let browser: ReturnType<typeof spawn> | undefined;
  let socket: WebSocket | undefined;
  try {
    await build({ entryPoints: [fileURLToPath(new URL("./worktreeManager.mount.tsx", import.meta.url))], outfile: join(root, "fixture.js"), bundle: true, platform: "browser", format: "iife",
      // The window is given the host it talks to: the generated client is not part of this page.
      plugins: [{ name: "no-host", setup(bundle) { bundle.onResolve({ filter: /^#neoastra$/ }, () => ({ path: fileURLToPath(new URL("../demo-api.ts", import.meta.url)) })); } }] });
    await writeFile(join(root, "style.css"), await readFile(new URL("../../node_modules/@blueprintjs/core/lib/css/blueprint.css", import.meta.url), "utf8")
      + await readFile(new URL("../style.css", import.meta.url), "utf8") + await readFile(new URL("./worktrees.css", import.meta.url), "utf8"));
    const page = join(root, "fixture.html");
    await writeFile(page, '<!doctype html><html><head><meta charset="utf-8"><link rel="stylesheet" href="style.css"></head><body class="bp6-dark"><div id="root"></div><script src="fixture.js"></script></body></html>');
    browser = spawn(edge!, [...browserBaseArgs, `--user-data-dir=${join(root, "profile")}`, "--remote-debugging-port=0", "--window-size=1200,800", "about:blank"], { stdio: "ignore", windowsHide: true });
    let port = "";
    for (let attempt = 0; attempt < 100 && !port; attempt++) {
      try { port = (await readFile(join(root, "profile", "DevToolsActivePort"), "utf8")).split(/\r?\n/)[0]; }
      catch { await new Promise(resolve => setTimeout(resolve, 50)); }
    }
    assert.match(port, /^\d+$/);
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
    const capture = async (name: string) => {
      if (!process.env.WORKTREES_SHOTS) return;
      const shot = await command("Page.captureScreenshot", { format: "png" });
      await writeFile(join(process.env.WORKTREES_SHOTS, `${name}.png`), Buffer.from(shot.data, "base64"));
    };
    await command("Page.navigate", { url: pathToFileURL(page).href });
    assert.equal(await wait("window.worktreesFixture"), true);

    const names = "[...document.querySelectorAll('.worktree-line')].map(line => line.dataset.path.split('\\\\').pop())";
    const line = (name: string) => `[...document.querySelectorAll('.worktree-line')].find(line => line.dataset.path.endsWith('\\\\${name}'))`;
    const footer = "document.querySelector('.worktree-manager-footer')";
    const press = (selector: string) => evaluate(`document.querySelector(${JSON.stringify(selector)}).click()`);
    const escape = () => evaluate("document.querySelector('dialog.worktree-manager-dialog').dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true, cancelable: true }))");
    const statuses = "Object.fromEntries([...document.querySelectorAll('.worktree-results-rows li')].map(item => [item.dataset.path.split('\\\\').pop(), item.dataset.status]))";
    const reset = () => evaluate(`(async () => {
      worktreesFixture.clear();
      const state = worktreesFixture.state;
      state.questions.splice(0).forEach(answer => answer([])); state.finishRemovals.splice(0).forEach(finish => finish());
      await Promise.resolve(); await Promise.resolve();
      Object.assign(state, { rows: [], hold: false, asked: 0, removals: [], outcomes: {}, lost: false, malformed: false,
        holdRemovals: false, editors: [], editor: 'ok', closed: 0, changed: 0, shown: [] });
    })()`);

    await t.test("timer and focus refreshes coalesce while a slow inventory is pending", async () => {
      try {
        await evaluate("worktreesFixture.state.hold = true; worktreesFixture.open()");
        const initial = await evaluate<number>("worktreesFixture.state.questions.length");
        assert.ok(initial > 0);
        // The read completes after the next ten-second tick, as with consistent twelve-second host reads.
        await evaluate("worktreesFixture.tick(); window.dispatchEvent(new Event('focus')); worktreesFixture.tick()");
        assert.equal(await evaluate("worktreesFixture.state.questions.length"), initial, "Automatic refresh must not supersede the pending answer.");
        await evaluate("worktreesFixture.state.questions.at(-1)(worktreesFixture.repository())");
        assert.equal(await wait("document.querySelectorAll('.worktree-line').length === 8"), true, "The slow initial answer can finish loading.");
        for (const count of [7, 6, 5]) {
          const before = await evaluate<number>("worktreesFixture.state.questions.length");
          await evaluate("worktreesFixture.tick(); worktreesFixture.tick(); window.dispatchEvent(new Event('focus'))");
          assert.equal(await evaluate("worktreesFixture.state.questions.length"), before + 1);
          await evaluate(`worktreesFixture.state.questions.at(-1)(worktreesFixture.repository().slice(0, ${count}))`);
          assert.equal(await wait(`document.querySelectorAll('.worktree-line').length === ${count}`), true);
          assert.equal(await evaluate("worktreesFixture.state.questions.length"), before + 1, "Ticks do not queue more reads after completion.");
        }
      } finally { await reset(); }
    });

    await t.test("project switches and closing invalidate held inventory answers without blocking a new read", async () => {
      try {
        await evaluate("worktreesFixture.state.hold = true; worktreesFixture.open()");
        const oldProject = await evaluate<number>("worktreesFixture.state.questions.length - 1");
        await evaluate("worktreesFixture.open('other')");
        assert.equal(await evaluate("worktreesFixture.state.questions.length"), oldProject + 2);
        await evaluate(`worktreesFixture.state.questions[${oldProject}](worktreesFixture.repository())`);
        assert.equal(await evaluate("document.querySelectorAll('.worktree-line').length"), 0, "The old project cannot publish while the new one is still loading.");
        await evaluate("worktreesFixture.tick(); window.dispatchEvent(new Event('focus'))");
        assert.equal(await evaluate("worktreesFixture.state.questions.length"), oldProject + 2, "An obsolete reply cannot clear the new project's pending read.");
        await evaluate("worktreesFixture.state.questions.at(-1)([worktreesFixture.row('new-project')])");
        assert.equal(await wait("document.querySelectorAll('.worktree-line').length === 1"), true);
        assert.deepEqual(await evaluate(names), ["new-project"]);
        await press('.worktree-manager-bar button[aria-label="Refresh"]');
        const closedRead = await evaluate<number>("worktreesFixture.state.questions.length - 1");
        await evaluate("worktreesFixture.clear(); worktreesFixture.tick(); window.dispatchEvent(new Event('focus'))");
        assert.equal(await evaluate("worktreesFixture.state.questions.length"), closedRead + 1, "Closing detaches automatic refresh.");
        await evaluate("worktreesFixture.open()");
        assert.ok(await evaluate<number>("worktreesFixture.state.questions.length") > closedRead + 1);
        await evaluate("worktreesFixture.state.questions.at(-1)([worktreesFixture.row('reopened')])");
        assert.equal(await wait("document.querySelectorAll('.worktree-line').length === 1"), true);
        await evaluate(`worktreesFixture.state.questions[${closedRead}](worktreesFixture.repository())`);
        assert.deepEqual(await evaluate(names), ["reopened"]);
      } finally { await reset(); }
    });

    await t.test("the worktree count includes a protected project checkout beside its repository's main checkout", async () => {
      try {
        await evaluate("worktreesFixture.state.rows = [worktreesFixture.row('repository', { main: true, protection: 'main' }), worktreesFixture.repository()[0]]; worktreesFixture.open()");
        assert.equal(await wait("document.querySelectorAll('.worktree-line').length === 2"), true);
        assert.equal(await evaluate("document.querySelector('.worktree-manager-count').textContent"), "1 worktree");
        assert.equal(await evaluate("!!document.querySelector('.worktree-manager-none')"), false);
        assert.equal(await evaluate("[...document.querySelectorAll('.worktree-cell-select input')].every(input => input.disabled)"), true);
        // For an ordinary project in the main checkout, that checkout alone is not a linked worktree.
        await evaluate("worktreesFixture.state.rows = [worktreesFixture.repository()[0]]");
        await press('.worktree-manager-bar button[aria-label="Refresh"]');
        assert.equal(await wait("document.querySelectorAll('.worktree-line').length === 1"), true);
        assert.equal(await evaluate("document.querySelector('.worktree-manager-count').textContent"), "0 worktrees");
        assert.equal(await evaluate("!!document.querySelector('.worktree-manager-none')"), true);
      } finally { await reset(); }
    });

    await t.test("Stop during the first held chunk finishes its four admitted removals and never starts the fifth", async () => {
      try {
        await evaluate("worktreesFixture.state.rows = worktreesFixture.repository(); worktreesFixture.state.holdRemovals = true; worktreesFixture.open()");
        assert.equal(await wait("document.querySelectorAll('.worktree-line').length === 8"), true);
        await press("thead .worktree-cell-select input");
        await evaluate(`${footer}.querySelector('button.bp6-intent-danger').click()`);
        assert.equal(await wait("document.querySelector('.worktree-review')"), true);
        await press(".worktree-review footer button.bp6-intent-danger");
        assert.equal(await wait("worktreesFixture.state.finishRemovals.length === 1 && document.querySelector('.worktree-manager[data-phase=removing]')"), true);
        assert.equal(await evaluate("document.querySelector('.worktree-manager-empty button').textContent"), "Stop");
        await press(".worktree-manager-empty button");
        assert.equal(await evaluate("document.querySelector('.worktree-manager').dataset.phase"), "removing");
        assert.equal(await evaluate("worktreesFixture.state.rows.length"), 8, "The admitted request is still held, not canceled or reported complete.");
        assert.equal(await evaluate("worktreesFixture.state.removals.length"), 1);
        await evaluate("worktreesFixture.state.finishRemovals[0]()");
        assert.equal(await wait("document.querySelector('.worktree-results')"), true);
        assert.equal(await evaluate("worktreesFixture.state.removals.length"), 1, "Stop prevents the second chunk.");
        assert.deepEqual(await evaluate("worktreesFixture.state.removals[0].paths.map(path => path.split('\\\\').pop())"), ["quiet-heron", "amber-denali", "calm-egret", "pale-wren"]);
        assert.deepEqual(await evaluate(statuses), { "quiet-heron": "ok", "amber-denali": "ok", "calm-egret": "ok", "pale-wren": "ok", "lost-otter": "canceled" });
        assert.equal(await evaluate("document.querySelector('.worktree-results h3').textContent"), "4 of 5 removed, 1 not removed.");
        assert.equal(await evaluate("worktreesFixture.state.rows.length"), 4, "Every already-admitted removal finished.");
        assert.equal(await evaluate("worktreesFixture.state.changed"), 1);
      } finally { await reset(); }
    });

    await t.test("an unreadable result stays unknown even when the admitted removals happened", async () => {
      try {
        await evaluate("worktreesFixture.state.rows = worktreesFixture.repository(); worktreesFixture.state.malformed = true; worktreesFixture.open()");
        assert.equal(await wait("document.querySelectorAll('.worktree-line').length === 8"), true);
        await press("thead .worktree-cell-select input");
        await evaluate(`${footer}.querySelector('button.bp6-intent-danger').click()`);
        assert.equal(await wait("document.querySelector('.worktree-review')"), true);
        await press(".worktree-review footer button.bp6-intent-danger");
        assert.equal(await wait("document.querySelector('.worktree-results')"), true);
        assert.equal(await evaluate("worktreesFixture.state.removals.length"), 1);
        assert.equal(await evaluate("worktreesFixture.state.rows.length"), 4);
        assert.deepEqual(await evaluate(statuses), { "quiet-heron": "read_failed", "amber-denali": "read_failed", "calm-egret": "read_failed", "pale-wren": "read_failed", "lost-otter": "canceled" });
        assert.equal(await evaluate("document.querySelector('.worktree-results h3').textContent"), "0 of 5 removed, 1 not removed, 4 unknown.");
        assert.equal(await evaluate("document.querySelector('.worktree-results-rows li[data-status=read_failed] .worktree-result-text strong').textContent"), "Outcome unknown");
      } finally { await reset(); }
    });

    // The window opens on every checkout git lists: what is on disk, then what git only still lists.
    await evaluate("worktreesFixture.state.rows = worktreesFixture.repository(); worktreesFixture.open()");
    assert.equal(await wait("document.querySelectorAll('.worktree-line').length === 8"), true);
    assert.equal(await evaluate("document.querySelector('dialog.worktree-manager-dialog').open"), true);
    assert.deepEqual(await evaluate(`[...document.querySelectorAll('tbody[data-group="present"] .worktree-line')].length`), 7);
    assert.deepEqual(await evaluate(`[...document.querySelectorAll('tbody[data-group="gone"] .worktree-line')].map(line => line.dataset.path.split('\\\\').pop())`), ["lost-otter"]);
    assert.equal(await evaluate("document.querySelector('.worktree-manager-count').textContent"), "7 worktrees");
    // While it only lists, a press beside the window closes it.
    assert.equal(await evaluate("document.querySelector('dialog.worktree-manager-dialog').dataset.outsidePress"), undefined);
    await capture("list");

    // An answer to an older question does not replace the answer to a newer one.
    await evaluate("worktreesFixture.state.hold = true");
    const before = await evaluate<number>("worktreesFixture.state.questions.length");
    await press('.worktree-manager-bar button[aria-label="Refresh"]');
    await press('.worktree-manager-bar button[aria-label="Refresh"]');
    assert.equal(await wait(`worktreesFixture.state.questions.length >= ${before + 2}`), true);
    await evaluate("worktreesFixture.state.questions.at(-1)(worktreesFixture.repository().filter(row => row.name !== 'pale-wren'))");
    assert.equal(await wait("document.querySelectorAll('.worktree-line').length === 7"), true);
    await evaluate(`worktreesFixture.state.questions[${before}](worktreesFixture.repository())`);
    await new Promise(resolve => setTimeout(resolve, 150));
    assert.equal(await evaluate("document.querySelectorAll('.worktree-line').length"), 7, "The older answer was dropped.");
    await evaluate("worktreesFixture.state.hold = false; worktreesFixture.state.rows = worktreesFixture.repository()");
    await press('.worktree-manager-bar button[aria-label="Refresh"]');
    assert.equal(await wait("document.querySelectorAll('.worktree-line').length === 8"), true);

    // Everything that can go is selected at once; what is protected is not, and cannot be.
    assert.equal(await evaluate(`${footer}.querySelector('button.bp6-intent-danger').disabled`), true);
    await press("thead .worktree-cell-select input");
    assert.equal(await wait(`${footer}.querySelector('[role="status"]').textContent === '5 selected'`), true);
    assert.deepEqual(await evaluate(`[...document.querySelectorAll('.worktree-line[data-selected]')].map(line => line.dataset.path.split('\\\\').pop())`),
      ["quiet-heron", "amber-denali", "calm-egret", "pale-wren", "lost-otter"]);
    for (const kept of ["alpha", "busy-finch", "kept-ibis"])
      assert.equal(await evaluate(`${line(kept)}.querySelector('.worktree-cell-select input').disabled`), true, kept);
    // One is taken out of the selection, and put back.
    await evaluate(`${line("pale-wren")}.querySelector('.worktree-cell-select input').click()`);
    assert.equal(await wait(`${footer}.querySelector('[role="status"]').textContent === '4 selected'`), true);
    await evaluate(`${line("pale-wren")}.querySelector('.worktree-cell-select input').click()`);
    assert.equal(await wait(`${footer}.querySelector('[role="status"]').textContent === '5 selected'`), true);
    await capture("selected");

    // Removing asks first: the worktrees are named, nothing is asked of the host, and Escape goes back without closing.
    await evaluate(`${footer}.querySelector('button.bp6-intent-danger').click()`);
    assert.equal(await wait("document.querySelector('.worktree-review h3')?.textContent === 'Remove these 5 worktrees?'"), true);
    assert.equal(await evaluate("document.querySelectorAll('.worktree-review-rows li').length"), 5);
    // The question says what goes without being asked about: the files git ignores.
    assert.equal(await evaluate("document.querySelector('.worktree-review-ignored').textContent"), "Files that git ignores are deleted with the folder.");
    assert.equal(await evaluate("document.querySelector('.worktree-review-ignored').getBoundingClientRect().height > 0"), true);
    assert.equal(await evaluate("document.querySelector('dialog.worktree-manager-dialog').dataset.outsidePress"), "keep");
    assert.equal(await evaluate("document.querySelector('.worktree-review-branches input').checked"), false);
    assert.equal(await evaluate("worktreesFixture.state.removals.length"), 0);
    await capture("review");
    await escape();
    assert.equal(await wait("document.querySelector('.worktree-table') && !document.querySelector('.worktree-review')"), true);
    assert.equal(await evaluate("worktreesFixture.state.closed"), 0);
    assert.equal(await evaluate(`${footer}.querySelector('[role="status"]').textContent`), "5 selected");

    // Confirmed: the host is asked a few worktrees at a time, never to throw changes away and never to delete a branch.
    await evaluate("worktreesFixture.state.outcomes = { 'amber-denali': { status: 'dirty' }, 'calm-egret': { status: 'in_use' }, 'quiet-heron': { status: 'ok' } }");
    await evaluate(`${footer}.querySelector('button.bp6-intent-danger').click()`);
    assert.equal(await wait("document.querySelector('.worktree-review')"), true);
    await press(".worktree-review footer button.bp6-intent-danger");
    assert.equal(await wait("document.querySelector('.worktree-results')"), true);
    const first = await evaluate<{ paths: string[]; discard: string[]; deleteMergedBranches: boolean }[]>("worktreesFixture.state.removals");
    assert.deepEqual(first.map(request => request.paths.map(path => path.split("\\").pop())), [["quiet-heron", "amber-denali", "calm-egret", "pale-wren"], ["lost-otter"]]);
    assert.ok(first.every(request => request.discard.length === 0 && request.deleteMergedBranches === false));
    // What went through in part says so, worktree by worktree.
    assert.equal(await evaluate("document.querySelector('.worktree-results h3').textContent"), "3 of 5 removed, 2 not removed.");
    assert.deepEqual(await evaluate(statuses), { "quiet-heron": "ok", "amber-denali": "dirty", "calm-egret": "in_use", "pale-wren": "ok", "lost-otter": "ok" });
    assert.match(await evaluate<string>("document.querySelector('.worktree-results-rows li[data-status=\"in_use\"]').textContent"), /Not removed.*A session is working there/u);
    assert.match(await evaluate<string>("document.querySelector('.worktree-results-rows li[data-status=\"dirty\"]').textContent"), /Not removed.*holds changes that are not committed/u);
    assert.equal(await evaluate("worktreesFixture.state.changed"), 1);
    await capture("results");

    // The worktree that holds changes is asked about by itself; only then is the host asked to throw them away, and for it alone.
    await press(".worktree-results-dirty button");
    assert.equal(await wait("document.querySelector('.worktree-review[data-discard] h3')?.textContent === 'Remove this worktree with its changes?'"), true);
    assert.deepEqual(await evaluate("[...document.querySelectorAll('.worktree-review-rows li')].map(item => item.dataset.path.split('\\\\').pop())"), ["amber-denali"]);
    assert.equal(await evaluate("worktreesFixture.state.removals.length"), 2);
    await capture("discard");
    // Escape leaves the changes where they are, and shows the outcome again.
    await escape();
    assert.equal(await wait("document.querySelector('.worktree-results') && !document.querySelector('.worktree-review')"), true);
    assert.equal(await evaluate("worktreesFixture.state.removals.length"), 2);
    await press(".worktree-results-dirty button");
    assert.equal(await wait("document.querySelector('.worktree-review[data-discard]')"), true);
    await press(".worktree-review footer button.bp6-intent-danger");
    assert.equal(await wait("worktreesFixture.state.removals.length === 3 && document.querySelector('.worktree-results')"), true);
    const discard = (await evaluate<{ paths: string[]; discard: string[] }[]>("worktreesFixture.state.removals"))[2];
    assert.deepEqual([discard.paths, discard.discard], [["C:\\trees\\alpha\\amber-denali"], ["C:\\trees\\alpha\\amber-denali"]]);
    assert.equal(await wait("document.querySelector('.worktree-results h3').textContent === '4 of 5 removed, 1 not removed.'"), true);
    assert.deepEqual(await evaluate(statuses), { "quiet-heron": "ok", "amber-denali": "ok", "calm-egret": "in_use", "pale-wren": "ok", "lost-otter": "ok" });
    assert.equal(await evaluate("!!document.querySelector('.worktree-results-dirty')"), false);

    // Done shows what is left, read again from the host.
    await press(".worktree-results footer button.bp6-intent-primary");
    assert.equal(await wait("document.querySelector('.worktree-table') && document.querySelectorAll('.worktree-line').length === 4"), true);
    assert.deepEqual(await evaluate(names), ["alpha", "busy-finch", "kept-ibis", "calm-egret"]);
    assert.equal(await evaluate(`${footer}.querySelector('[role="status"]').textContent`), "");

    // One worktree from its row, with its branch asked to go.
    await evaluate("worktreesFixture.state.outcomes = { 'calm-egret': { status: 'ok', branchDeleted: 'alta/calm-egret' } }");
    await evaluate(`${line("calm-egret")}.querySelector('button[aria-label="Remove the worktree calm-egret"]').click()`);
    assert.equal(await wait("document.querySelector('.worktree-review h3')?.textContent === 'Remove this worktree?'"), true);
    await press(".worktree-review-branches input");
    assert.equal(await wait("document.querySelector('.worktree-review-note').textContent === 'A branch that holds commits of its own is kept.'"), true);
    await press(".worktree-review footer button.bp6-intent-danger");
    assert.equal(await wait("document.querySelector('.worktree-results h3')?.textContent === 'The worktree was removed.'"), true);
    assert.deepEqual((await evaluate<{ paths: string[]; discard: string[]; deleteMergedBranches: boolean }[]>("worktreesFixture.state.removals"))[3],
      { paths: ["C:\\trees\\alpha\\calm-egret"], discard: [], deleteMergedBranches: true });
    assert.match(await evaluate<string>("document.querySelector('.worktree-results-rows li').textContent"), /The branch alta\/calm-egret was deleted\./u);
    await press(".worktree-results footer button.bp6-intent-primary");
    assert.equal(await wait("document.querySelectorAll('.worktree-line').length === 3"), true);
    // The next question starts without it: a branch is never deleted because the box was ticked before.
    await evaluate("worktreesFixture.state.rows = worktreesFixture.repository()");
    await press('.worktree-manager-bar button[aria-label="Refresh"]');
    assert.equal(await wait("document.querySelectorAll('.worktree-line').length === 8"), true);
    await press("thead .worktree-cell-select input");
    await evaluate(`${footer}.querySelector('button.bp6-intent-danger').click()`);
    assert.equal(await wait("document.querySelector('.worktree-review')"), true);
    assert.equal(await evaluate("document.querySelector('.worktree-review-branches input').checked"), false);

    // The host does not answer: nothing is concluded for what was asked, and nothing more is asked.
    await evaluate("worktreesFixture.state.lost = true; worktreesFixture.state.outcomes = {}");
    const sent = await evaluate<number>("worktreesFixture.state.removals.length");
    await press(".worktree-review footer button.bp6-intent-danger");
    assert.equal(await wait("document.querySelector('.worktree-results')"), true);
    assert.equal(await evaluate("worktreesFixture.state.removals.length"), sent + 1);
    assert.deepEqual(await evaluate(statuses), { "quiet-heron": "unconfirmed", "amber-denali": "unconfirmed", "calm-egret": "unconfirmed", "pale-wren": "unconfirmed", "lost-otter": "canceled" });
    assert.equal(await evaluate("document.querySelector('.worktree-results h3').textContent"), "0 of 5 removed, 1 not removed, 4 unknown.");
    assert.equal(await evaluate("document.querySelector('.worktree-results-rows li[data-status=unconfirmed] .worktree-result-text strong').textContent"), "Outcome unknown");
    assert.match(await evaluate<string>("document.querySelector('.worktree-results-rows li[data-status=\"unconfirmed\"]').textContent"), /The answer did not arrive/u);
    assert.match(await evaluate<string>("document.querySelector('.worktree-results-rows li[data-status=\"canceled\"]').textContent"), /Not started\./u);
    await evaluate("worktreesFixture.state.lost = false");
    await press(".worktree-results footer button.bp6-intent-primary");
    assert.equal(await wait("document.querySelectorAll('.worktree-line').length === 8"), true);

    // A project that lives in a worktree has the main checkout of its repository in the list: it is not one to remove,
    // and its changes are asked for by its own folder, like those of any checkout.
    await evaluate("worktreesFixture.state.rows = [worktreesFixture.row('repository', { path: 'C:\\\\code\\\\repository', folder: 'C:\\\\code\\\\repository', branch: 'main', main: true, protection: 'main' }), ...worktreesFixture.repository()]");
    await press('.worktree-manager-bar button[aria-label="Refresh"]');
    assert.equal(await wait("document.querySelectorAll('.worktree-line').length === 9"), true);
    assert.equal(await evaluate("document.querySelector('.worktree-manager-count').textContent"), "8 worktrees");
    assert.match(await evaluate<string>(`${line("repository")}.querySelector('.worktree-cell-states').textContent`), /Main checkout/u);
    assert.equal(await evaluate(`${line("repository")}.querySelector('.worktree-cell-select input').disabled`), true);
    assert.equal(await evaluate(`${line("repository")}.querySelector('button[aria-label="Remove the worktree repository"]').disabled`), true);
    assert.equal(await evaluate(`${line("repository")}.querySelector('button[aria-label="Show the changes of repository"]').disabled`), false);
    await evaluate(`${line("repository")}.querySelector('button[aria-label="Show the changes of repository"]').click()`);
    assert.deepEqual(await evaluate("worktreesFixture.state.shown"), ["C:\\code\\repository"]);
    await evaluate("worktreesFixture.state.shown = []; worktreesFixture.state.rows = worktreesFixture.repository()");
    await press('.worktree-manager-bar button[aria-label="Refresh"]');
    assert.equal(await wait("document.querySelectorAll('.worktree-line').length === 8"), true);

    // The changes of a checkout are those of its own folder, and the editor is asked for the checkout that was pressed.
    await evaluate(`${line("quiet-heron")}.querySelector('button[aria-label="Show the changes of quiet-heron"]').click()`);
    assert.deepEqual(await evaluate("worktreesFixture.state.shown"), ["C:\\trees\\alpha\\quiet-heron"]);
    await evaluate("worktreesFixture.state.editor = 'worktree_missing'");
    await evaluate(`${line("quiet-heron")}.querySelector('button[aria-label="Open quiet-heron in the code editor"]').click()`);
    assert.equal(await wait("document.querySelector('.worktree-manager-notice')?.textContent.includes('quiet-heron: The folder of this worktree is gone.')"), true);
    assert.equal(await evaluate("worktreesFixture.state.closed"), 0, "A refusal leaves the window open.");
    await evaluate("worktreesFixture.state.editor = 'ok'");
    await evaluate(`${line("amber-denali")}.querySelector('button[aria-label="Open amber-denali in the code editor"]').click()`);
    assert.equal(await wait("worktreesFixture.state.closed === 1"), true);
    assert.deepEqual(await evaluate("worktreesFixture.state.editors"), ["C:\\trees\\alpha\\quiet-heron", "C:\\trees\\alpha\\amber-denali"]);
    // Escape closes the window while it only lists.
    await escape();
    assert.equal(await wait("worktreesFixture.state.closed === 2"), true);

    // The menu of a project leads to the window of its worktrees; an archived project has no such line.
    await evaluate("worktreesFixture.clear(); worktreesFixture.projectMenu()");
    const menuLines = "[...document.querySelectorAll('.session-tab-popup [role=menuitem]')].map(item => item.textContent.trim())";
    await evaluate("document.querySelectorAll('.project-actions-trigger')[0].click()");
    assert.equal(await wait(`${menuLines}.includes('Worktrees…')`), true);
    await evaluate("[...document.querySelectorAll('.session-tab-popup [role=menuitem]')].find(item => item.textContent.trim() === 'Worktrees…').click()");
    assert.equal(await wait("worktreesFixture.state.managed.length === 1"), true);
    assert.deepEqual(await evaluate("worktreesFixture.state.managed"), ["p1"]);
    assert.equal(await wait("!document.querySelector('.session-tab-popup')"), true);
    await evaluate("document.querySelectorAll('.project-actions-trigger')[1].click()");
    assert.equal(await wait(`${menuLines}.includes('Details')`), true);
    assert.equal(await evaluate(`${menuLines}.includes('Worktrees…')`), false);

    // A narrow window keeps the name, the state, the last use and the actions.
    await command("Emulation.setDeviceMetricsOverride", { width: 620, height: 700, deviceScaleFactor: 1, mobile: false });
    await evaluate("worktreesFixture.clear(); localStorage.clear(); worktreesFixture.open()");
    assert.equal(await wait("document.querySelectorAll('.worktree-line').length === 8"), true);
    assert.equal(await evaluate("document.querySelector('.worktree-line td.worktree-cell-states').getBoundingClientRect().width"), 0);
    assert.equal(await evaluate(`getComputedStyle(${line("busy-finch")}.querySelector('.worktree-states-inline')).display`), "block");
    assert.equal(await evaluate(`${line("busy-finch")}.querySelector('.worktree-states-inline').textContent`), "In use");
    assert.equal(await evaluate(`getComputedStyle(${line("quiet-heron")}.querySelector('.worktree-states-inline')).display`), "none");
    assert.equal(await evaluate("Math.round(document.querySelector('.worktree-table').getBoundingClientRect().width) === Math.round(document.querySelector('.worktree-manager-rows').clientWidth)"), true);
    assert.equal(await evaluate(`${footer}.querySelector('button.bp6-intent-danger').textContent`), "Remove…");
    assert.equal(await evaluate(`${line("quiet-heron")}.querySelector('button[aria-label="Remove the worktree quiet-heron"]').getBoundingClientRect().right <= document.querySelector('.app-window').getBoundingClientRect().right`), true);
    await capture("narrow");
  } finally {
    // Edge's launcher can exit while the browser it started goes on: the browser itself is asked to close.
    if (socket?.readyState === WebSocket.OPEN) socket.send(JSON.stringify({ id: 9999, method: "Browser.close" }));
    socket?.close();
    if (browser && browser.exitCode === null) { const closed = new Promise<void>(resolve => browser!.once("exit", () => resolve())); browser.kill(); await Promise.race([closed, new Promise(resolve => setTimeout(resolve, 5000))]); }
    await rm(root, { recursive: true, force: true, maxRetries: 10, retryDelay: 100 });
  }
});
