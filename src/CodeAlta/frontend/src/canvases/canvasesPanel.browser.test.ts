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

test("the Canvases tab lists what plugins declare, opens a canvas for the project or the session, and the search finds each canvas", { skip: !edge, timeout: 180_000 }, async () => {
  const root = await mkdtemp(join(tmpdir(), "codealta-canvases-page-"));
  let browser: ReturnType<typeof spawn> | undefined;
  let socket: WebSocket | undefined;
  try {
    await build({ entryPoints: [fileURLToPath(new URL("./canvasesPanel.mount.tsx", import.meta.url))], outfile: join(root, "fixture.js"), bundle: true, platform: "browser", format: "iife" });
    await writeFile(join(root, "style.css"), await readFile(new URL("../../node_modules/@blueprintjs/core/lib/css/blueprint.css", import.meta.url), "utf8")
      + await readFile(new URL("../style.css", import.meta.url), "utf8") + await readFile(new URL("../workItems/workItems.css", import.meta.url), "utf8")
      + await readFile(new URL("./canvases.css", import.meta.url), "utf8"));
    const page = join(root, "fixture.html");
    await writeFile(page, '<!doctype html><html><head><meta charset="utf-8"><link rel="stylesheet" href="style.css"></head><body class="bp6-dark"><div id="root" style="height:600px"></div><script src="fixture.js"></script></body></html>');
    browser = spawn(edge!, [...browserBaseArgs, `--user-data-dir=${join(root, "profile")}`, "--remote-debugging-port=0", "--window-size=1100,800", "about:blank"], { stdio: "ignore", windowsHide: true });
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
      if (!process.env.CANVASES_SHOTS) return;
      const shot = await command("Page.captureScreenshot", { format: "png" });
      await writeFile(join(process.env.CANVASES_SHOTS, `${name}.png`), Buffer.from(shot.data, "base64"));
    };
    await command("Page.navigate", { url: pathToFileURL(page).href });
    assert.equal(await wait("window.canvasesFixture"), true);

    // Nothing declared: a statement and a way to ask for one, and the page asked the host once it was shown.
    await evaluate("canvasesFixture.page()");
    assert.equal(await wait("document.querySelector('.canvases-empty')?.textContent.includes('No plugin provides a canvas yet.')"), true);
    assert.equal(await evaluate("document.querySelectorAll('.canvases-empty button').length"), 1, "New canvas is offered in the empty page");
    assert.equal(await wait("canvasesFixture.state.refreshed >= 1"), true);
    // Without a project in front a canvas cannot be asked for.
    await evaluate("canvasesFixture.page({ canNew: false })");
    assert.equal(await evaluate("document.querySelectorAll('.canvases-empty button').length"), 0);
    assert.equal(await evaluate("document.querySelector('.work-header button').disabled"), true);

    // Plugins declare canvases: one card each, with what each is about.
    await evaluate(`canvasesFixture.declare([
      canvasesFixture.item('stats', 'Application', { plugin: 'Statistics', pluginKey: 'builtin:statistics', description: 'What the sessions cost.', actions: 2, icon: 'chart-column' }),
      canvasesFixture.item('checklist', 'Project', { description: 'The steps of a release.', actions: 1 }),
      canvasesFixture.item('board', 'Session', {})])`);
    await evaluate("canvasesFixture.page()");
    assert.equal(await wait("document.querySelectorAll('.canvas-card').length === 3"), true);
    assert.deepEqual(await evaluate("[...document.querySelectorAll('.canvas-card-title')].map(node => node.textContent)"), ["Stats", "Checklist", "Board"]);
    assert.deepEqual(await evaluate("[...document.querySelectorAll('.canvas-card')].map(card => card.querySelector('.bp6-tag').textContent)"), ["Application", "Project", "Session"]);
    assert.deepEqual(await evaluate("[...document.querySelectorAll('.canvas-card-facts')].map(node => node.textContent)"), ["Statistics2 actions", "Tools1 action", "Tools"]);
    assert.equal(await evaluate("document.querySelectorAll('.canvases-page > .work-header + .canvases-list').length"), 1);
    // The project and the session in front are the defaults of the cards that need one.
    assert.equal(await evaluate("document.querySelector('.canvases-list > li:nth-child(2) select').value"), "p1");
    assert.equal(await evaluate("document.querySelector('.canvases-list > li:nth-child(3) select').value"), "s1");
    await capture("canvases-page");

    // Open runs with the target of the card: nothing for an application canvas, the project, the session with its project.
    await evaluate("document.querySelector('.canvases-list > li:nth-child(1) .canvas-card-actions button').click()");
    await evaluate("document.querySelector('.canvases-list > li:nth-child(2) .canvas-card-actions button').click()");
    await evaluate("document.querySelector('.canvases-list > li:nth-child(3) .canvas-card-actions button').click()");
    assert.deepEqual(await evaluate("canvasesFixture.state.opened"), [{ id: "stats", project: null, session: null }, { id: "checklist", project: "p1", session: null }, { id: "board", project: "p1", session: "s1" }]);
    // The user chooses another project on a card: it opens for that one, and the card keeps the choice.
    await evaluate(`(() => { const select = document.querySelector('.canvases-list > li:nth-child(2) select'); const set = Object.getOwnPropertyDescriptor(HTMLSelectElement.prototype, 'value').set;
      set.call(select, 'p2'); select.dispatchEvent(new Event('change', { bubbles: true })); })()`);
    await evaluate("document.querySelector('.canvases-list > li:nth-child(2) .canvas-card-actions button').click()");
    assert.deepEqual(await evaluate("canvasesFixture.state.opened.at(-1)"), { id: "checklist", project: "p2", session: null });
    // A chat has no project.
    await evaluate(`(() => { const select = document.querySelector('.canvases-list > li:nth-child(3) select'); const set = Object.getOwnPropertyDescriptor(HTMLSelectElement.prototype, 'value').set;
      set.call(select, 's2'); select.dispatchEvent(new Event('change', { bubbles: true })); })()`);
    await evaluate("document.querySelector('.canvases-list > li:nth-child(3) .canvas-card-actions button').click()");
    assert.deepEqual(await evaluate("canvasesFixture.state.opened.at(-1)"), { id: "board", project: null, session: "s2" });
    // New canvas asks the owner.
    await evaluate("document.querySelector('.work-header button').click()");
    assert.equal(await evaluate("canvasesFixture.state.created"), 1);

    // Nothing selected: the cards that need a project or a session cannot be opened until one is chosen, the application's can.
    await evaluate("canvasesFixture.page({ selection: 'nothing' })");
    await evaluate("canvasesFixture.clear()");
    await evaluate("canvasesFixture.page({ selection: 'nothing' })");
    assert.deepEqual(await evaluate("[...document.querySelectorAll('.canvas-card-actions > .bp6-button')].map(button => button.disabled)"), [false, true, true]);
    assert.equal(await evaluate("document.querySelector('.canvases-list > li:nth-child(2) .canvas-card-actions button').title"), "Select a project first.");
    assert.equal(await evaluate("document.querySelector('.canvases-list > li:nth-child(2) select').value"), "");

    // What is open in this space is marked on its card, with where.
    await evaluate("canvasesFixture.page({ tabs: [canvasesFixture.tab(canvasesFixture.item('checklist', 'Project'), 'p1'), canvasesFixture.tab(canvasesFixture.item('checklist', 'Project'), 'p2'), canvasesFixture.tab(canvasesFixture.item('stats', 'Application', { pluginKey: 'builtin:statistics' }))] })");
    assert.deepEqual(await evaluate("[...document.querySelectorAll('.canvas-card')].map(card => card.querySelector('.bp6-intent-success')?.textContent ?? null)"), ["Open in this space", "Open: One, Two", null]);
    assert.equal(await evaluate("document.querySelectorAll('.canvas-card[data-open]').length"), 2);
    await capture("canvases-page-open");
    await evaluate("document.body.classList.remove('bp6-dark'); document.documentElement.dataset.theme = 'light'");
    await capture("canvases-page-light");
    await evaluate("document.body.classList.add('bp6-dark'); delete document.documentElement.dataset.theme");

    // The keyboard reaches Open and runs it.
    await evaluate("(() => { const button = document.querySelector('.canvases-list > li:nth-child(1) .canvas-card-actions button'); button.focus(); })()");
    assert.equal(await evaluate("document.activeElement.textContent.trim()"), "Open");
    await command("Input.dispatchKeyEvent", { type: "keyDown", key: "Enter", code: "Enter", windowsVirtualKeyCode: 13, text: "\r" });
    await command("Input.dispatchKeyEvent", { type: "keyUp", key: "Enter", code: "Enter", windowsVirtualKeyCode: 13 });
    assert.equal(await wait("canvasesFixture.state.opened.at(-1).id === 'stats'"), true);

    // A tab that is hidden does not read; it reads again when it is shown.
    const reads = await evaluate<number>("canvasesFixture.state.refreshed");
    await evaluate("canvasesFixture.page({ visible: false })");
    await new Promise(resolve => setTimeout(resolve, 200));
    assert.equal(await evaluate("canvasesFixture.state.refreshed"), reads);
    await evaluate("canvasesFixture.page({ visible: true })");
    assert.equal(await wait(`canvasesFixture.state.refreshed > ${reads}`), true);

    // The menus of rows list the canvases of their scope, a few, then the page.
    await evaluate("canvasesFixture.declare([canvasesFixture.item('a', 'Project'), canvasesFixture.item('b', 'Project'), canvasesFixture.item('c', 'Project'), canvasesFixture.item('d', 'Project'), canvasesFixture.item('e', 'Project'), canvasesFixture.item('s', 'Session')])");
    assert.deepEqual(await evaluate("(() => { const menu = canvasesFixture.menu('Project'); return [menu.items.map(value => value.id), menu.more]; })()"), [["a", "b", "c", "d"], true]);
    assert.deepEqual(await evaluate("(() => { const menu = canvasesFixture.menu('Session'); return [menu.items.map(value => value.id), menu.more]; })()"), [["s"], false]);

    // The menu of a project row lists the canvases of its scope after its own lines: a separator, a few lines, then the page.
    await evaluate("canvasesFixture.clear(); canvasesFixture.projectMenu()");
    await evaluate("document.querySelector('.project-actions-trigger').click()");
    const menuLines = "[...document.querySelectorAll('.session-tab-popup [role=menuitem]')].map(item => item.textContent.trim())";
    assert.equal(await wait(`${menuLines}.length > 0`), true);
    assert.deepEqual((await evaluate<string[]>(menuLines)).slice(-6), ["Archive project…", "Open A", "Open B", "Open C", "Open D", "More…"]);
    assert.equal(await evaluate("document.querySelectorAll('.session-tab-popup [role=separator]').length"), 1);
    await capture("project-menu-canvases");
    await evaluate("[...document.querySelectorAll('.session-tab-popup [role=menuitem]')].find(item => item.textContent.trim() === 'Open B').click()");
    assert.deepEqual(await evaluate("canvasesFixture.state.opened.at(-1)"), { id: "b", project: "p1", session: null });
    await evaluate("document.querySelector('.project-actions-trigger').click()");
    assert.equal(await wait(`${menuLines}.length > 0`), true);
    await evaluate("[...document.querySelectorAll('.session-tab-popup [role=menuitem]')].find(item => item.textContent.trim() === 'More…').click()");
    assert.deepEqual(await evaluate("canvasesFixture.state.chosen"), ["page"]);

    // The search lists each canvas under the commands, by its words, and opens it with what is selected.
    await evaluate("canvasesFixture.clear(); canvasesFixture.declare([canvasesFixture.item('stats', 'Application', { plugin: 'Statistics', description: 'What the sessions cost.' }), canvasesFixture.item('checklist', 'Project'), canvasesFixture.item('board', 'Session')])");
    const canvasRows = "[...document.querySelectorAll('.global-search-row')].filter(row => row.textContent.startsWith('Open canvas:'))";
    await evaluate("canvasesFixture.search({ text: 'canvas' })");
    assert.equal(await wait(`${canvasRows}.length === 3`), true);
    assert.deepEqual(await evaluate(`${canvasRows}.map(row => row.querySelector('.global-search-title').textContent)`),
      ["Open canvas: Stats/open_stats", "Open canvas: Checklist/open_checklist", "Open canvas: Board/open_board"]);
    assert.deepEqual(await evaluate(`${canvasRows}.map(row => row.getAttribute('aria-disabled'))`), ["false", "false", "false"]);
    assert.deepEqual(await evaluate(`${canvasRows}.map(row => row.querySelector('.global-search-detail').textContent)`), ["What the sessions cost.", "A canvas of Tools.", "A canvas of Tools."]);
    await capture("search-canvases");
    await evaluate(`${canvasRows}[1].click()`);
    assert.deepEqual(await evaluate("canvasesFixture.state.chosen"), ["page", "checklist"]);
    // By the name of the plugin.
    await evaluate("canvasesFixture.clear(); canvasesFixture.search({ text: 'statistics' })");
    assert.equal(await wait(`${canvasRows}.length === 1`), true);
    assert.match(await evaluate<string>(`${canvasRows}[0].textContent`), /Open canvas: Stats/u);
    // By its slash name.
    await evaluate("canvasesFixture.clear(); canvasesFixture.search({ text: '/open_board' })");
    assert.equal(await wait(`${canvasRows}.length === 1`), true);
    // Nothing selected: a canvas that needs a project is listed and cannot be run.
    await evaluate("canvasesFixture.clear(); canvasesFixture.search({ text: 'checklist', selection: 'nothing' })");
    assert.equal(await wait(`${canvasRows}.length === 1`), true);
    assert.equal(await evaluate(`${canvasRows}[0].getAttribute('aria-disabled')`), "true");
    await evaluate(`${canvasRows}[0].click()`);
    assert.deepEqual(await evaluate("canvasesFixture.state.chosen"), ["page", "checklist"], "A canvas that cannot be opened is not chosen.");
  } finally {
    // Edge's launcher can exit while the browser it started goes on: the browser itself is asked to close.
    if (socket?.readyState === WebSocket.OPEN) socket.send(JSON.stringify({ id: 9999, method: "Browser.close" }));
    socket?.close();
    if (browser && browser.exitCode === null) { const closed = new Promise<void>(resolve => browser!.once("exit", () => resolve())); browser.kill(); await Promise.race([closed, new Promise(resolve => setTimeout(resolve, 5000))]); }
    await rm(root, { recursive: true, force: true, maxRetries: 10, retryDelay: 100 });
  }
});
