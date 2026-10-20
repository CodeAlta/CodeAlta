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

test("the card of a held conversation offers its choices, answers once, reads again, then shows the wait with Stop", { skip: !edge, timeout: 90_000 }, async () => {
  const root = await mkdtemp(join(tmpdir(), "codealta-conversation-held-"));
  let browser: ReturnType<typeof spawn> | undefined;
  let socket: WebSocket | undefined;
  try {
    await build({ entryPoints: [fileURLToPath(new URL("./conversationHeld.mount.tsx", import.meta.url))], outfile: join(root, "fixture.js"),
      bundle: true, platform: "browser", format: "iife", jsx: "automatic", logLevel: "silent" });
    await writeFile(join(root, "style.css"), await read("../../node_modules/@blueprintjs/core/lib/css/blueprint.css") + await read("../style.css") + await read("./conversationHeld.css"));
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

    // The question of a conversation open in VS Code: every choice, Open here anyway saying what it does, Cancel last.
    await until("window.held && held.lists.length === 1", "a running session reads its questions at once");
    await evaluate(`held.lists[0].resolve(held.page([held.entry("vsCode", "VS Code", ["wait", "continueAsCopy", "openAnyway", "cancel"])]))`);
    await until("!!document.querySelector('.conversation-held-panel h3')", "the card is shown");
    assert.equal(await evaluate("document.querySelector('.conversation-held-panel h3').textContent"), "This Claude Code conversation is open in VS Code");
    assert.deepEqual(await texts("[data-held-detail] > *"), ["Process 48212", "C:\\dev\\a-project-with-a-rather-long-name\\and\\a\\deep\\folder"]);
    assert.deepEqual(await evaluate("[...document.querySelectorAll('[data-held-choice]')].map(b => b.dataset.heldChoice)"), ["wait", "continueAsCopy", "openAnyway", "cancel"]);
    assert.deepEqual(await texts("[data-held-choice]"), ["Wait until it is free", "Continue as a copy", "Open here anywayBoth would write the same conversation", "Cancel"]);
    assert.equal(await evaluate("document.querySelector('[data-held-choice=wait]').disabled"), true, "a question that just appeared is not answered by a click meant for something else");
    await until("!document.querySelector('[data-held-choice=wait]').disabled", "its choices arm");

    // Both themes and a narrow window: the card fits, Cancel is quieter than the choices.
    for (const theme of ["dark", "light"]) {
      await evaluate(`held.setTheme(${JSON.stringify(theme)})`);
      for (const [width, height] of [[1600, 1000], [900, 650]]) {
        await command("Emulation.setDeviceMetricsOverride", { width, height, deviceScaleFactor: 1, mobile: false });
        const shown = await evaluate(`(() => { const card = document.querySelector('.conversation-held-panel'); const style = node => getComputedStyle(node);
          const cancel = document.querySelector('.conversation-held-cancel'), wait = document.querySelector('[data-held-choice=wait]');
          return { overflow: card.scrollWidth > card.clientWidth || document.documentElement.scrollWidth > document.documentElement.clientWidth,
            cancelBorder: style(cancel).borderTopColor, waitBorder: style(wait).borderTopColor, cancelColor: style(cancel).color, waitColor: style(wait).color,
            background: style(card).backgroundColor }; })()`) as Record<string, unknown>;
        assert.equal(shown.overflow, false, `${theme} ${width}: ${JSON.stringify(shown)}`);
        assert.notEqual(shown.cancelBorder, shown.waitBorder, `${theme}: Cancel has no border`);
        assert.notEqual(shown.cancelColor, shown.waitColor, `${theme}: Cancel is muted`);
      }
    }
    await command("Emulation.setDeviceMetricsOverride", { width: 1600, height: 1000, deviceScaleFactor: 1, mobile: false });
    await evaluate("held.setTheme('dark')");

    // Wait: the answer is sent once, the list is read again, and the wait the run reports is shown with Stop.
    await evaluate("document.querySelector('[data-held-choice=wait]').click(); document.querySelector('[data-held-choice=wait]')?.click()");
    await until("held.answers.length === 1", "the answer is sent");
    assert.deepEqual(await evaluate("[held.answers[0].request.choice, held.answers[0].request.expectedHostEpoch, held.answers[0].request.handle.attemptId]"),
      ["wait", "11111111-1111-4111-8111-111111111111", "22222222-2222-4222-8222-222222222222"]);
    assert.deepEqual(await texts(".conversation-held-panel [role=status]"), ["Sending your choice…"]);
    await evaluate("held.answers[0].resolve({ status: 'resolved', hostEpoch: held.epoch, handle: held.answers[0].request.handle })");
    await until("held.lists.length === 2", "the list is read again after the answer");
    await evaluate("held.lists[1].resolve(held.page([]))");
    await until("!document.querySelector('.conversation-held-panel')", "nothing is left to answer");
    await evaluate("held.setWait({ originText: 'VS Code' })");
    await until("!!document.querySelector('[data-held-waiting]')", "the wait is shown");
    assert.equal(await evaluate("document.querySelector('.conversation-held-wait [role=status]').textContent"), "Waiting for VS Code to release the conversation…");
    await evaluate("document.querySelector('[data-held-stop]').click()");
    assert.equal(await evaluate("held.stops.length"), 1, "Stop stops the run");
    await evaluate("held.setWait(null)");
    await until("!document.querySelector('.conversation-held-panel')", "a released conversation ends the wait");

    // A background session cannot be opened anyway. An answer that is not taken is not sent again: the list is read
    // again and the question that still waits is shown, with a line that says so.
    await until("held.lists.length === 3", "the questions are read again while the run goes on");
    await evaluate(`held.lists[2].resolve(held.page([held.entry("backgroundSession", "a background session of Claude Code", ["wait", "continueAsCopy", "cancel"], "44444444-4444-4444-8444-444444444444")]))`);
    await until("!!document.querySelector('[data-held-choice=continueAsCopy]') && !document.querySelector('[data-held-choice=continueAsCopy]').disabled", "the next question is shown and armed");
    assert.deepEqual(await evaluate("[...document.querySelectorAll('[data-held-choice]')].map(b => b.dataset.heldChoice)"), ["wait", "continueAsCopy", "cancel"]);
    assert.equal(await evaluate("document.querySelector('.conversation-held-panel h3').textContent"), "This Claude Code conversation is open in a background session of Claude Code");
    await evaluate("document.querySelector('[data-held-choice=continueAsCopy]').click()");
    await until("held.answers.length === 2", "the answer is sent");
    await evaluate("held.answers[1].resolve({ status: 'rejected', hostEpoch: held.epoch, handle: held.answers[1].request.handle })");
    await until("held.lists.length === 4", "a rejected answer reads the list again");
    await evaluate(`held.lists[3].resolve(held.page([held.entry("backgroundSession", "a background session of Claude Code", ["wait", "continueAsCopy", "cancel"], "44444444-4444-4444-8444-444444444444")]))`);
    await until("!!document.querySelector('[data-held-notice=rejected]') && !!document.querySelector('[data-held-choice]')", "the question and the notice are shown");
    assert.equal(await evaluate("held.answers.length"), 2, "nothing is sent again by itself");

    // In the language of the window.
    await evaluate("held.language('fr')");
    await until("document.querySelector('.conversation-held-panel h3').textContent === 'Cette conversation Claude Code est ouverte dans une session d’arrière-plan de Claude Code'", "the card is in French");
    assert.deepEqual(await texts("[data-held-choice]"), ["Attendre qu’elle soit libre", "Continuer dans une copie", "Annuler"]);
    await evaluate("held.language('en')");

    // The run ends with the question shown: one read shows that nothing is left.
    await evaluate("held.run(false)");
    await until("held.lists.length === 5", "the end of the run reads the list once");
    await evaluate("held.lists[4].resolve(held.page([]))");
    await until("!document.querySelector('[data-held-choice]')", "the question went with its run");
    await new Promise(resolve => setTimeout(resolve, 1_800));
    assert.equal(await evaluate("held.lists.length"), 5, "a session that does not run is not read again");
    assert.deepEqual(exceptions, []);
  } finally {
    // Edge's launcher can exit while the browser it started goes on: the browser itself is asked to close.
    if (socket?.readyState === WebSocket.OPEN) socket.send(JSON.stringify({ id: 9999, method: "Browser.close" }));
    socket?.close();
    await endBrowser(browser);
    await rm(root, { recursive: true, force: true, maxRetries: 10, retryDelay: 100 });
  }
});
