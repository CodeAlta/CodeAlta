import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { fileURLToPath, pathToFileURL } from "node:url";
import { build } from "esbuild";
import { browserBaseArgs, browserExecutable, endBrowser, waitForBrowserPort } from "../browserTarget";

const edge = browserExecutable;
const read = (path: string) => readFile(new URL(path, import.meta.url), "utf8");

test("the Claude Code sessions started elsewhere show once found, closed until opened as they were left, one line per row with pages of more, a click previews, and the menu continues them here or opens them elsewhere", { skip: !edge, timeout: 90_000 }, async () => {
  const root = await mkdtemp(join(tmpdir(), "codealta-claude-code-sessions-"));
  let browser: ReturnType<typeof spawn> | undefined;
  let socket: WebSocket | undefined;
  try {
    await build({ entryPoints: [fileURLToPath(new URL("./claudeCodeSessions.mount.tsx", import.meta.url))], outfile: join(root, "fixture.js"),
      bundle: true, platform: "browser", format: "iife", jsx: "automatic", logLevel: "silent" });
    await writeFile(join(root, "style.css"), await read("../../node_modules/@blueprintjs/core/lib/css/blueprint.css") + await read("../style.css")
      + await read("../explorer/explorer.css") + await read("./claudeCodeSessions.css"));
    const page = join(root, "fixture.html");
    await writeFile(page, '<!doctype html><html><head><meta charset="utf-8"><link rel="stylesheet" href="style.css"></head><body><div id="root"></div><script src="fixture.js"></script></body></html>');
    browser = spawn(edge!, [...browserBaseArgs, `--user-data-dir=${join(root, "profile")}`, "--remote-debugging-port=0", "about:blank"], { stdio: ["ignore", "ignore", "pipe"], windowsHide: true });
    const port = await waitForBrowserPort(join(root, "profile"), browser);
    const pages: { type?: string; webSocketDebuggerUrl?: string }[] = await (await fetch(`http://127.0.0.1:${port}/json/list`, { signal: AbortSignal.timeout(10_000) })).json();
    socket = new WebSocket(pages.find(value => value.type === "page")!.webSocketDebuggerUrl!);
    await new Promise<void>((resolve, reject) => { socket!.addEventListener("open", () => resolve(), { once: true }); socket!.addEventListener("error", () => reject(Error("browser unavailable")), { once: true }); });
    let sequence = 0;
    const exceptions: string[] = [];
    socket.addEventListener("message", event => {
      const message = JSON.parse(String(event.data));
      if (message.method === "Runtime.exceptionThrown") exceptions.push(JSON.stringify(message.params.exceptionDetails));
    });
    const command = (method: string, params: object = {}) => new Promise<{ result?: { value?: unknown }; exceptionDetails?: unknown }>((resolve, reject) => {
      const id = ++sequence, timer = setTimeout(() => reject(Error(`${method} timed out`)), 12_000);
      const reply = (event: MessageEvent) => {
        const message = JSON.parse(String(event.data)); if (message.id !== id) return;
        socket!.removeEventListener("message", reply); clearTimeout(timer);
        if (message.error) reject(Error(`${method}: ${JSON.stringify(message.error)}`)); else resolve(message.result ?? {});
      };
      socket!.addEventListener("message", reply); socket!.send(JSON.stringify({ id, method, params }));
    });
    const evaluate = async (expression: string) => {
      const result = await command("Runtime.evaluate", { expression, returnByValue: true, awaitPromise: true });
      assert.equal(result.exceptionDetails, undefined, JSON.stringify(result.exceptionDetails));
      return result.result?.value;
    };
    const until = async (condition: string, message: string) => assert.equal(await evaluate(`new Promise(resolve => { const end = Date.now() + 7000; (function check() {
      if (${condition}) resolve(true); else if (Date.now() > end) resolve(false); else setTimeout(check, 20); })(); })`), true, message);
    const texts = (selector: string) => evaluate(`[...document.querySelectorAll(${JSON.stringify(selector)})].map(node => node.textContent.trim())`);
    await command("Runtime.enable");
    await command("Page.enable");
    await command("Emulation.setDeviceMetricsOverride", { width: 1600, height: 1000, deviceScaleFactor: 1, mobile: false });
    await command("Page.navigate", { url: pathToFileURL(page).href });

    const A = "0f5c2c1e-7a0e-4a52-9d55-2a8d2b1c0e11", B = "1f5c2c1e-7a0e-4a52-9d55-2a8d2b1c0e12", C = "2f5c2c1e-7a0e-4a52-9d55-2a8d2b1c0e13";
    const longTitle = "Fix the parser of quoted keys in a rather long title that does not fit";
    // A held by a terminal that works on it, B open in a VS Code panel (idle), C held by nothing; then sessions of their own.
    const firstThree = `[group.session("${A}", "${longTitle}", "vscode", true, "busy"), group.session("${B}", "Review the release", "terminal", true, "idle"),
      group.session("${C}", "Plan the next step", "other", false)]`;

    // Nothing is shown until sessions were found, nor when there are none.
    await until("window.group && group.lists.length === 1", "the group reads its sessions when the list of the project is shown");
    assert.deepEqual(await evaluate("group.lists[0].request"), { expectedHostEpoch: "11111111-1111-4111-8111-111111111111", projectId: "p1",
      projectPath: "C:\\dev\\a-project-with-a-rather-long-name", offset: 0, count: 20, spaceId: null }, "the first 20 sessions");
    assert.equal(await evaluate("!!document.querySelector('.claude-code-sessions')"), false);
    await evaluate("group.lists[0].resolve(group.listing([]))");
    await new Promise(resolve => setTimeout(resolve, 100));
    assert.equal(await evaluate("!!document.querySelector('.claude-code-sessions')"), false, "no session found: no group");

    // Found: closed, with their number, after the sessions of CodeAlta.
    await evaluate("group.refresh()");
    await until("group.lists.length === 2", "a change of the sessions of the window reads them again");
    await evaluate(`group.lists[1].resolve(group.listing([...${firstThree}, ...group.many(3, 17)], 75))`);
    await until("!!document.querySelector('.claude-code-sessions-toggle')", "the group is shown");
    assert.equal(await evaluate("document.querySelector('.claude-code-sessions-toggle').textContent"), "From Claude Code (75)");
    assert.equal(await evaluate("document.querySelector('.claude-code-sessions-toggle').getAttribute('aria-expanded')"), "false");
    assert.equal(await evaluate("document.querySelectorAll('.claude-code-session-row').length"), 0, "closed, it lists nothing");
    assert.equal(await evaluate("document.querySelector('.session-list').lastElementChild.classList.contains('claude-code-sessions')"), true, "it is last");

    // Opened: read again, the first 20 listed, one line each, with where each was started and the one in use elsewhere marked.
    await evaluate("document.querySelector('.claude-code-sessions-toggle').click()");
    await until("group.lists.length === 3", "opening it reads the sessions again");
    assert.deepEqual(await evaluate("group.openChanges"), [true], "the window keeps that the group was opened");
    assert.deepEqual(await evaluate("[group.lists[2].request.offset, group.lists[2].request.count]"), [0, 20]);
    await evaluate(`group.lists[2].resolve(group.listing([...${firstThree}, ...group.many(3, 17)], 75))`);
    await until("document.querySelectorAll('.claude-code-session-row').length === 20", "the first 20 sessions are listed");
    assert.deepEqual((await texts(".claude-code-session-row .session-title") as string[]).slice(0, 3), [longTitle, "Review the release", "Plan the next step"]);
    assert.deepEqual(await evaluate(`[...document.querySelectorAll('.claude-code-session-row')].slice(0, 3).map(row => row.querySelector('.claude-code-session-origin').getAttribute('aria-label'))`),
      ["Started in VS Code", "Started in a terminal", "Started in another program"]);
    assert.deepEqual(await evaluate(`[...document.querySelectorAll('.claude-code-session-row')].slice(0, 3).map(row => !!row.querySelector('.claude-code-session-held'))`),
      [true, false, false], "only a session another process works on is marked: one merely open in VS Code is not");
    assert.equal(await evaluate("document.querySelector('.claude-code-session-held').title"), "In use in another Claude process now");
    assert.deepEqual(await texts(".claude-code-sessions-more"), ["Show more… (55)"]);
    const listItems = `[...document.querySelector('.claude-code-sessions-list[role="list"]').children].every(child => child.getAttribute('role') === 'listitem')`;
    assert.equal(await evaluate(listItems), true, "the list holds its rows only: \"Show more\" comes after it");
    const row = async () => await evaluate(`(() => { const own = document.querySelector('.codealta-session-row').getBoundingClientRect();
      const rows = [...document.querySelectorAll('.claude-code-session-row')]; const title = rows[0].querySelector('.session-title');
      const icon = rows[0].querySelector('.claude-code-session-icon'); const time = rows[0].querySelector('.session-meta time');
      const origin = rows[0].querySelector('.claude-code-session-origin').getBoundingClientRect(); const held = rows[0].querySelector('.claude-code-session-held').getBoundingClientRect();
      return { heights: [...new Set(rows.map(row => Math.round(row.getBoundingClientRect().height)))], own: Math.round(own.height),
        cut: title.scrollWidth > title.clientWidth && getComputedStyle(title).textOverflow === 'ellipsis',
        oneLine: rows.every(row => { const button = row.firstElementChild.getBoundingClientRect(); return [...row.firstElementChild.children].every(child => {
          const box = child.getBoundingClientRect(); return box.top >= button.top - 0.5 && box.bottom <= button.bottom + 0.5; }); }),
        order: held.right <= origin.left + 0.5 && origin.right <= time.getBoundingClientRect().left + 0.5,
        dimmed: Number(getComputedStyle(icon).opacity) < 1 }; })()`) as Record<string, unknown>;
    const shownRow = await row();
    assert.deepEqual(shownRow.heights, [shownRow.own], `every row has the height of a session row: ${JSON.stringify(shownRow)}`);
    assert.equal(shownRow.cut, true, "a long title is cut with an ellipsis");
    assert.equal(shownRow.oneLine, true, "the marks and the time are on the line of the title");
    assert.equal(shownRow.order, true, "the marks stand before the time, on the right");
    assert.equal(shownRow.dimmed, true, "the logo of the provider is dimmed");

    // "Show more" adds 50 at a time until the end.
    await evaluate("document.querySelector('.claude-code-sessions-more button').click()");
    await until("group.lists.length === 4", "the next page is read");
    assert.deepEqual(await evaluate("[group.lists[3].request.offset, group.lists[3].request.count]"), [20, 50]);
    await evaluate("group.lists[3].resolve(group.listing(group.many(20, 50), 75, 20))");
    await until("document.querySelectorAll('.claude-code-session-row').length === 70", "50 more are listed");
    assert.deepEqual(await texts(".claude-code-sessions-more"), ["Show more… (5)"]);
    await evaluate("document.querySelector('.claude-code-sessions-more button').click()");
    await until("group.lists.length === 5", "the last page is read");
    assert.deepEqual(await evaluate("[group.lists[4].request.offset, group.lists[4].request.count]"), [70, 50]);
    await evaluate("group.lists[4].resolve(group.listing(group.many(70, 5), 75, 70))");
    await until("document.querySelectorAll('.claude-code-session-row').length === 75", "the list is whole");
    assert.equal(await evaluate("!!document.querySelector('.claude-code-sessions-more')"), false, "nothing more to show");
    // A read again keeps what was shown: pages of 50 from the start.
    await evaluate("group.refresh()");
    await until("group.lists.length === 6", "the list is read again");
    assert.deepEqual(await evaluate("[group.lists[5].request.offset, group.lists[5].request.count]"), [0, 50]);
    await evaluate(`group.lists[5].resolve(group.listing([...${firstThree}, ...group.many(3, 47)], 75))`);
    await until("group.lists.length === 7", "and its second page");
    assert.deepEqual(await evaluate("[group.lists[6].request.offset, group.lists[6].request.count]"), [50, 25]);
    await evaluate("group.lists[6].resolve(group.listing(group.many(50, 25), 75, 50))");
    await new Promise(resolve => setTimeout(resolve, 100));
    assert.equal(await evaluate("document.querySelectorAll('.claude-code-session-row').length"), 75, "as many rows as before");

    // Both themes and a narrow window: the rows fit the rail.
    for (const theme of ["dark", "light"]) {
      await evaluate(`group.setTheme(${JSON.stringify(theme)})`);
      for (const [width, height] of [[1600, 1000], [900, 650]]) {
        await command("Emulation.setDeviceMetricsOverride", { width, height, deviceScaleFactor: 1, mobile: false });
        const shown = await evaluate(`(() => { const rail = document.querySelector('.session-rail'); const row = document.querySelector('.claude-code-session-row');
          const time = row.querySelector('.session-meta');
          return { overflow: rail.scrollWidth > rail.clientWidth || document.documentElement.scrollWidth > document.documentElement.clientWidth,
            row: row.getBoundingClientRect().width <= rail.getBoundingClientRect().width + 0.5, time: time.getBoundingClientRect().right <= rail.getBoundingClientRect().right + 0.5 }; })()`) as Record<string, unknown>;
        assert.equal(shown.overflow, false, `${theme} ${width}: ${JSON.stringify(shown)}`);
        assert.equal(shown.row, true, `${theme} ${width}: the row fits the rail`);
        assert.equal(shown.time, true, `${theme} ${width}: the time stays in the rail`);
        const shownRows = await row();
        assert.deepEqual(shownRows.heights, [shownRows.own], `${theme} ${width}: one line`);
      }
    }
    await command("Emulation.setDeviceMetricsOverride", { width: 1600, height: 1000, deviceScaleFactor: 1, mobile: false });

    // A click on a row opens its preview, which writes nothing; the row of the preview shown is marked.
    await evaluate(`document.querySelector('[data-cli-session-id="${A}"] > button').click()`);
    await until(`group.previews.length === 1`, "the preview is asked for");
    assert.deepEqual(await evaluate("[group.previews, group.imports.length]"), [[A], 0], "nothing is imported");
    await until(`document.querySelector('[data-cli-session-id="${A}"]').classList.contains('selected')`, "the row of the preview is marked");
    assert.equal(await evaluate(`document.querySelector('[data-cli-session-id="${A}"] > button').getAttribute('aria-current')`), "page");
    assert.match(await evaluate(`document.querySelector('[data-cli-session-id="${A}"] > button').title`) as string, /your first message continues it in CodeAlta/);

    // The menu of a row, by a right click or its "…": its preview, continue in CodeAlta, or open the session where it was started; never a deletion.
    const menuLines = () => texts('.session-tab-popup [role="menuitem"]');
    const menuOf = async (id: string, how: "context" | "button") => {
      await evaluate(how === "context"
        ? `document.querySelector('[data-cli-session-id="${id}"] > button').dispatchEvent(new MouseEvent('contextmenu', { bubbles: true, cancelable: true, clientX: 50, clientY: 50 }))`
        : `document.querySelector('[data-cli-session-id="${id}"] .session-actions-trigger').click()`);
      await until(`!!document.querySelector('.session-tab-popup [role="menuitem"]')`, `the menu of ${id}`);
    };
    const choose = async (line: string) => {
      await evaluate(`[...document.querySelectorAll('.session-tab-popup [role="menuitem"]')].find(item => item.textContent.trim() === ${JSON.stringify(line)}).click()`);
      await until("!document.querySelector('.session-tab-popup')", "the menu closed");
    };
    assert.equal(await evaluate(`document.querySelector('[data-cli-session-id="${A}"] .session-actions-trigger').getAttribute('aria-label')`),
      `Actions for the Claude Code session ${longTitle}`);
    await menuOf(B, "context");
    assert.deepEqual(await menuLines(), ["Open preview", "Continue in CodeAlta", "Open in VS Code", "Copy resume command", "Run in terminal"]);
    await choose("Open preview");
    await until("group.previews.length === 2", "the preview of the other session takes its place");
    assert.deepEqual(await evaluate("group.previews"), [A, B]);
    await until(`document.querySelector('[data-cli-session-id="${B}"]').classList.contains('selected') && !document.querySelector('[data-cli-session-id="${A}"]').classList.contains('selected')`,
      "the row of the preview shown is the one marked");
    await menuOf(A, "context");
    await choose("Copy resume command");
    await until("group.conversations.length === 1", "the host is asked for the command");
    assert.deepEqual(await evaluate("group.conversations[0].request"), { expectedHostEpoch: "11111111-1111-4111-8111-111111111111", projectId: "p1",
      projectPath: "C:\\dev\\a-project-with-a-rather-long-name", cliSessionId: A, spaceId: null });
    await evaluate(`group.conversations[0].resolve(group.described("${A}"))`);
    await until("group.outcomes.length === 1", "the copy is said");
    assert.deepEqual(await evaluate("[group.copied, group.outcomes[0]]"),
      [[`claude --resume ${A}`], { ok: true, message: "Resume command copied. Run it in a terminal opened in the folder of the project." }]);

    await menuOf(A, "button");
    await choose("Run in terminal");
    await until("group.conversations.length === 2", "the host is asked for the command again");
    await evaluate(`group.conversations[1].resolve(group.described("${A}"))`);
    await until("group.outcomes.length === 2", "the terminal is said");
    assert.deepEqual(await evaluate("[group.terminals(), group.typed]"), [1, [{ id: "t1", data: `claude --resume ${A}\r` }]]);

    await menuOf(B, "context");
    await choose("Open in VS Code");
    await until("group.opens.length === 1", "the host opens it");
    assert.equal(await evaluate("group.opens[0].request.cliSessionId"), B);
    await evaluate(`group.opens[0].resolve(group.open("${B}"))`);
    await until("group.outcomes.length === 3", "the opening is said");
    assert.deepEqual(await evaluate("group.outcomes[2]"), { ok: true, message: "Opened in VS Code." });
    // A session a session of CodeAlta continues meanwhile: said, and the list read again.
    await menuOf(B, "context");
    await choose("Open in VS Code");
    await until("group.opens.length === 2", "the host is asked again");
    const reads = await evaluate("group.lists.length") as number;
    await evaluate(`group.opens[1].resolve(group.open("${B}", "existing"))`);
    await until("group.outcomes.length === 4", "the refusal is said");
    assert.deepEqual(await evaluate("group.outcomes[3]"), { ok: false, message: "A session of CodeAlta continues this conversation now: open it from the list of sessions.", reread: true });
    await until(`group.lists.length === ${reads + 1}`, "the list is read again");
    await evaluate(`group.lists[${reads}].resolve(group.listing([...${firstThree}, ...group.many(3, 47)], 75))`);
    await until(`group.lists.length === ${reads + 2}`, "and its second page");
    await evaluate(`group.lists[${reads + 1}].resolve(group.listing(group.many(50, 25), 75, 50))`);

    // Continue in CodeAlta imports it; the menu then waits for the import.
    await menuOf(A, "context");
    await choose("Continue in CodeAlta");
    await until("group.imports.length === 1", "the session is imported");
    assert.deepEqual(await evaluate("group.imports[0].request"), { expectedHostEpoch: "11111111-1111-4111-8111-111111111111", projectId: "p1",
      projectPath: "C:\\dev\\a-project-with-a-rather-long-name", cliSessionId: A, spaceId: null });
    assert.equal(await evaluate("[...document.querySelectorAll('.claude-code-session-row > button:first-child')].every(button => button.disabled)"), true);
    await menuOf(B, "button");
    assert.deepEqual(await evaluate(`[...document.querySelectorAll('.session-tab-popup [role="menuitem"]')].map(item => item.getAttribute('aria-disabled') === 'true')`),
      [true, true, true, true, true], "nothing else while an import runs");
    await evaluate("document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }))");
    await evaluate(`document.querySelector('[data-cli-session-id="${B}"] > button').click()`);
    assert.equal(await evaluate("group.imports.length"), 1, "one import at a time");
    await evaluate(`group.imports[0].resolve(group.imported("${A}"))`);
    await until("group.opened.length === 1", "the session that continues it is opened");
    assert.deepEqual(await evaluate("group.opened[0]"), { id: "s-new", existing: false, title: longTitle, cliSessionId: A });

    // A session that cannot be imported any more: said, and the list read again.
    await until(`!document.querySelector('[data-cli-session-id="${B}"] > button').disabled`, "the rows can be used again");
    const before = await evaluate("group.lists.length") as number;
    await menuOf(B, "button");
    await choose("Continue in CodeAlta");
    await until("group.imports.length === 2", "the second one is imported");
    await evaluate(`group.imports[1].resolve(group.imported("${B}", "not_found", null))`);
    await until("!!document.querySelector('.claude-code-sessions-error')", "the failure is said");
    assert.equal(await evaluate("!!document.querySelector('.claude-code-sessions-list .claude-code-sessions-error')"), false, "the failure is said after the list, not in it");
    assert.equal(await evaluate("document.querySelector('.claude-code-sessions-error').textContent"),
      "This Claude Code session can no longer be imported: it changed or was removed.");
    await until(`group.lists.length === ${before + 1}`, "the list is read again");
    assert.equal(await evaluate("group.opened.length"), 1, "nothing is opened");

    // Shown again (a restart), the group is as it was left: open, read at once; closed again, it stays closed.
    await evaluate("group.remount()");
    await until(`document.querySelector('.claude-code-sessions-toggle')?.getAttribute('aria-expanded') === 'true' || group.lists.length > ${before + 1}`, "the group reads its sessions");
    const shown = await evaluate("group.lists.length") as number;
    await evaluate(`group.lists[${shown - 1}].resolve(group.listing([...${firstThree}, ...group.many(3, 17)], 75))`);
    await until("document.querySelector('.claude-code-sessions-toggle')?.getAttribute('aria-expanded') === 'true' && document.querySelectorAll('.claude-code-session-row').length === 20",
      "the group left open is open");
    await evaluate("document.querySelector('.claude-code-sessions-toggle').click()");
    assert.deepEqual(await evaluate("group.openChanges"), [true, false]);
    await evaluate("group.remount()");
    await until(`group.lists.length === ${shown + 1}`, "the group reads its sessions again");
    await evaluate(`group.lists[${shown}].resolve(group.listing([...${firstThree}, ...group.many(3, 17)], 75))`);
    await until("document.querySelector('.claude-code-sessions-toggle')?.getAttribute('aria-expanded') === 'false'", "the group left closed is closed");
    assert.equal(await evaluate("document.querySelectorAll('.claude-code-session-row').length"), 0);

    // In the language of the window.
    await evaluate("group.language('de')");
    await until("document.querySelector('.claude-code-sessions-toggle').textContent === 'Aus Claude Code (75)'", "the group is in German");
    await evaluate("document.querySelector('.claude-code-sessions-toggle').click()");
    await until(`group.lists.length === ${shown + 2}`, "opened, it reads its sessions");
    await evaluate(`group.lists[${shown + 1}].resolve(group.listing([...${firstThree}, ...group.many(3, 17)], 75))`);
    await until("!!document.querySelector('.claude-code-session-held')", "the rows are listed");
    assert.equal(await evaluate("document.querySelector('.claude-code-session-held').title"), "Wird gerade in einem anderen Claude-Prozess verwendet");
    assert.equal(await evaluate("document.querySelector('.claude-code-session-origin').getAttribute('aria-label')"), "In VS Code gestartet");
    assert.deepEqual(exceptions, []);
  } finally {
    // Edge's launcher can exit while the browser it started goes on: the browser itself is asked to close.
    if (socket?.readyState === WebSocket.OPEN) socket.send(JSON.stringify({ id: 9999, method: "Browser.close" }));
    socket?.close();
    await endBrowser(browser);
    await rm(root, { recursive: true, force: true, maxRetries: 10, retryDelay: 100 });
  }
});
