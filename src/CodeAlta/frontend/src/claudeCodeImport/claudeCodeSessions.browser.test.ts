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

test("the Claude Code sessions started elsewhere show once found, closed with their number, open read again, and a click imports", { skip: !edge, timeout: 90_000 }, async () => {
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

    // Nothing is shown until sessions were found, nor when there are none.
    await until("window.group && group.lists.length === 1", "the group reads its sessions when the list of the project is shown");
    assert.deepEqual(await evaluate("group.lists[0].request"), { expectedHostEpoch: "11111111-1111-4111-8111-111111111111", projectId: "p1",
      projectPath: "C:\\dev\\a-project-with-a-rather-long-name" });
    assert.equal(await evaluate("!!document.querySelector('.claude-code-sessions')"), false);
    await evaluate("group.lists[0].resolve(group.listing([]))");
    await new Promise(resolve => setTimeout(resolve, 100));
    assert.equal(await evaluate("!!document.querySelector('.claude-code-sessions')"), false, "no session found: no group");

    // Found: closed, with their number, after the sessions of CodeAlta.
    await evaluate("group.refresh()");
    await until("group.lists.length === 2", "a change of the sessions of the window reads them again");
    await evaluate(`group.lists[1].resolve(group.listing([group.session("0f5c2c1e-7a0e-4a52-9d55-2a8d2b1c0e11", "Fix the parser of quoted keys in a rather long title that does not fit", "vscode", true),
      group.session("1f5c2c1e-7a0e-4a52-9d55-2a8d2b1c0e12", "Review the release", "terminal", false)], 3))`);
    await until("!!document.querySelector('.claude-code-sessions-toggle')", "the group is shown");
    assert.equal(await evaluate("document.querySelector('.claude-code-sessions-toggle').textContent"), "From Claude Code (3)");
    assert.equal(await evaluate("document.querySelector('.claude-code-sessions-toggle').getAttribute('aria-expanded')"), "false");
    assert.equal(await evaluate("document.querySelectorAll('.claude-code-session-row').length"), 0, "closed, it lists nothing");
    assert.equal(await evaluate("document.querySelector('.session-list').lastElementChild.classList.contains('claude-code-sessions')"), true, "it is last");

    // Opened: read again, then listed with where each was started, and the one open elsewhere marked.
    await evaluate("document.querySelector('.claude-code-sessions-toggle').click()");
    await until("group.lists.length === 3", "opening it reads the sessions again");
    await evaluate(`group.lists[2].resolve(group.listing([group.session("0f5c2c1e-7a0e-4a52-9d55-2a8d2b1c0e11", "Fix the parser of quoted keys in a rather long title that does not fit", "vscode", true),
      group.session("1f5c2c1e-7a0e-4a52-9d55-2a8d2b1c0e12", "Review the release", "terminal", false)], 3))`);
    await until("document.querySelectorAll('.claude-code-session-row').length === 2", "the sessions are listed");
    assert.deepEqual(await texts(".claude-code-session-row .session-title"), ["Fix the parser of quoted keys in a rather long title that does not fit", "Review the release"]);
    assert.deepEqual(await texts(".claude-code-session-origin"), ["VS Code", "terminal"]);
    assert.deepEqual(await texts(".claude-code-session-held"), ["open elsewhere"]);
    assert.deepEqual(await texts(".claude-code-sessions-more"), ["1 more not shown"]);

    // Both themes and a narrow window: the rows fit the rail.
    for (const theme of ["dark", "light"]) {
      await evaluate(`group.setTheme(${JSON.stringify(theme)})`);
      for (const [width, height] of [[1600, 1000], [900, 650]]) {
        await command("Emulation.setDeviceMetricsOverride", { width, height, deviceScaleFactor: 1, mobile: false });
        const shown = await evaluate(`(() => { const rail = document.querySelector('.session-rail'); const row = document.querySelector('.claude-code-session-row');
          const held = document.querySelector('.claude-code-session-held');
          return { overflow: rail.scrollWidth > rail.clientWidth || document.documentElement.scrollWidth > document.documentElement.clientWidth,
            row: row.getBoundingClientRect().width <= rail.getBoundingClientRect().width + 0.5, held: held.getBoundingClientRect().right <= rail.getBoundingClientRect().right + 0.5,
            color: getComputedStyle(held).color, background: getComputedStyle(document.body).backgroundColor }; })()`) as Record<string, unknown>;
        assert.equal(shown.overflow, false, `${theme} ${width}: ${JSON.stringify(shown)}`);
        assert.equal(shown.row, true, `${theme} ${width}: the row fits the rail`);
        assert.equal(shown.held, true, `${theme} ${width}: the mark stays in the rail`);
      }
    }
    await command("Emulation.setDeviceMetricsOverride", { width: 1600, height: 1000, deviceScaleFactor: 1, mobile: false });

    // A click imports that session once; the rows wait meanwhile; the session is then opened.
    await evaluate("document.querySelector('[data-cli-session-id=\"0f5c2c1e-7a0e-4a52-9d55-2a8d2b1c0e11\"] > button').click()");
    await until("group.imports.length === 1", "the session is imported");
    assert.deepEqual(await evaluate("group.imports[0].request"), { expectedHostEpoch: "11111111-1111-4111-8111-111111111111", projectId: "p1",
      projectPath: "C:\\dev\\a-project-with-a-rather-long-name", cliSessionId: "0f5c2c1e-7a0e-4a52-9d55-2a8d2b1c0e11" });
    assert.deepEqual(await evaluate("[...document.querySelectorAll('.claude-code-session-row > button')].map(button => button.disabled)"), [true, true]);
    await evaluate("document.querySelector('[data-cli-session-id=\"1f5c2c1e-7a0e-4a52-9d55-2a8d2b1c0e12\"] > button').click()");
    assert.equal(await evaluate("group.imports.length"), 1, "one import at a time");
    await evaluate(`group.imports[0].resolve(group.imported("0f5c2c1e-7a0e-4a52-9d55-2a8d2b1c0e11"))`);
    await until("group.opened.length === 1", "the session that continues it is opened");
    assert.deepEqual(await evaluate("group.opened[0]"), { id: "s-new", existing: false, title: "Fix the parser of quoted keys in a rather long title that does not fit" });

    // A session that cannot be imported any more: said, and the list read again.
    await evaluate("document.querySelector('[data-cli-session-id=\"1f5c2c1e-7a0e-4a52-9d55-2a8d2b1c0e12\"] > button').click()");
    await until("group.imports.length === 2", "the second one is imported");
    await evaluate(`group.imports[1].resolve(group.imported("1f5c2c1e-7a0e-4a52-9d55-2a8d2b1c0e12", "not_found", null))`);
    await until("!!document.querySelector('.claude-code-sessions-error')", "the failure is said");
    assert.equal(await evaluate("document.querySelector('.claude-code-sessions-error').textContent"),
      "This Claude Code session can no longer be imported: it changed or was removed.");
    await until("group.lists.length === 4", "the list is read again");
    assert.equal(await evaluate("group.opened.length"), 1, "nothing is opened");

    // In the language of the window.
    await evaluate("group.language('de')");
    await until("document.querySelector('.claude-code-sessions-toggle').textContent === 'Aus Claude Code (3)'", "the group is in German");
    assert.deepEqual(await texts(".claude-code-session-held"), ["woanders geöffnet"]);
    assert.deepEqual(exceptions, []);
  } finally {
    // Edge's launcher can exit while the browser it started goes on: the browser itself is asked to close.
    if (socket?.readyState === WebSocket.OPEN) socket.send(JSON.stringify({ id: 9999, method: "Browser.close" }));
    socket?.close();
    await endBrowser(browser);
    await rm(root, { recursive: true, force: true, maxRetries: 10, retryDelay: 100 });
  }
});
