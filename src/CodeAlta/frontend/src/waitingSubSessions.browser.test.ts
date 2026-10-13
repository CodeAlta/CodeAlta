import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { fileURLToPath, pathToFileURL } from "node:url";
import { build } from "esbuild";
import { browserBaseArgs, browserExecutable, endBrowser, waitForBrowserPort } from "./browserTarget";

const edge = browserExecutable;

test("a request of a sub-session answered on the session above it reaches that sub-session alone, and the prompt takes the keyboard back", { skip: !edge, timeout: 60_000 }, async () => {
  const root = await mkdtemp(join(tmpdir(), "codealta-waiting-sub-sessions-"));
  let browser: ReturnType<typeof spawn> | undefined;
  let socket: WebSocket | undefined;
  try {
    await build({ entryPoints: [fileURLToPath(new URL("./waitingSubSessions.mount.tsx", import.meta.url))], outfile: join(root, "fixture.js"), bundle: true, platform: "browser", format: "iife" });
    await writeFile(join(root, "style.css"), await readFile(new URL("./style.css", import.meta.url), "utf8"));
    const page = join(root, "fixture.html");
    await writeFile(page, '<!doctype html><html><head><meta charset="utf-8"><link rel="stylesheet" href="style.css"></head><body><div id="root"></div><script src="fixture.js"></script></body></html>');
    browser = spawn(edge!, [...browserBaseArgs, `--user-data-dir=${join(root, "profile")}`, "--remote-debugging-port=0", "about:blank"], { stdio: ["ignore", "ignore", "pipe"], windowsHide: true });
    const port = await waitForBrowserPort(join(root, "profile"), browser);
    const pages: { type?: string; webSocketDebuggerUrl?: string }[] = await (await fetch(`http://127.0.0.1:${port}/json/list`, { signal: AbortSignal.timeout(10_000) })).json();
    socket = new WebSocket(pages.find(value => value.type === "page")!.webSocketDebuggerUrl!);
    await new Promise<void>((resolve, reject) => { socket!.addEventListener("open", () => resolve(), { once: true }); socket!.addEventListener("error", () => reject(Error("browser unavailable")), { once: true }); });
    let sequence = 0;
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
    const until = (condition: string) => evaluate(`new Promise(resolve => { const end = Date.now() + 7000; (function check() {
      if (${condition}) resolve(true); else if (Date.now() > end) resolve(false); else setTimeout(check, 20); })(); })`);
    // The card of a session, by its place among the sessions that wait, and its first choice once it can be answered.
    const card = (index: number) => `document.querySelectorAll(".waiting-sub-session")[${index}]`;
    const allow = (index: number) => `${card(index)}.querySelector("[data-permission-decision=allow_once]:not(:disabled)")`;
    await command("Page.enable");
    await command("Page.navigate", { url: pathToFileURL(page).href });
    assert.equal(await until(`document.querySelectorAll(".waiting-sub-session").length === 2 && !!${allow(0)} && !!${allow(1)}`), true, "Each sub-session shows its request");
    assert.deepEqual(await evaluate(`[0, 1].map(index => document.querySelectorAll(".waiting-sub-session")[index].querySelector("[data-permission-command]").textContent)`),
      ["echo child", "echo grandchild"], "each card shows the request of its own session");
    assert.deepEqual(await evaluate(`[...document.querySelectorAll(".waiting-sub-session .command-permission-panel")].map(panel => panel.getAttribute("aria-label"))`),
      ["Pending permissions of Fix the parser", "Pending permissions of Run the tests"]);
    // Showing the pane gives the keyboard to the choice of its own request only: Enter there must not answer for a sub-session.
    assert.equal(await evaluate(`waitingSubSessions.ownChoice()`), null, "The requests of the sub-sessions do not take the keyboard");
    assert.equal(await evaluate(`(() => {
      const own = document.createElement("div"); own.className = "command-permission-panel";
      own.innerHTML = '<button data-permission-choice id="own-choice"></button>';
      document.querySelector(".composer-region").append(own);
      const choice = waitingSubSessions.ownChoice()?.id; own.remove(); return choice; })()`), "own-choice", "The request of the pane's own session does");

    await evaluate(`${allow(0)}.focus(); ${allow(0)}.click()`);
    assert.equal(await until(`waitingSubSessions.decisions.length === 1 && !${card(0)}.querySelector(".command-permission-panel")`), true, "The answered request leaves its card");
    assert.deepEqual(await evaluate(`waitingSubSessions.decisions.map(request => [request.handle.sessionId, request.handle.attemptId, request.decision])`),
      [["child", "22222222-2222-4222-8222-222222222222", "allow_once"]], "The answer goes to the request of the sub-session it was given for");
    assert.equal(await evaluate(`!!${allow(1)} && ${card(1)}.querySelector("[data-permission-command]").textContent === "echo grandchild"`), true,
      "The request of the other sub-session still waits");
    assert.equal(await evaluate(`document.activeElement === document.querySelector("#session-prompt")`), true, "What is typed next goes to the prompt");
  } finally {
    // Edge's launcher can exit while the browser it started goes on: the browser itself is asked to close.
    if (socket?.readyState === WebSocket.OPEN) socket.send(JSON.stringify({ id: 9999, method: "Browser.close" }));
    socket?.close();
    await endBrowser(browser);
    await rm(root, { recursive: true, force: true, maxRetries: 8, retryDelay: 100 });
  }
});
