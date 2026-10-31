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

test("a session in the cloud shows its card, sends messages, says why one was refused, and is continued here; a start and the Cloud place are shown",
  { skip: !edge, timeout: 90_000 }, async () => {
    const root = await mkdtemp(join(tmpdir(), "codealta-cloud-sessions-"));
    let browser: ReturnType<typeof spawn> | undefined;
    let socket: WebSocket | undefined;
    try {
      await build({ entryPoints: [fileURLToPath(new URL("./cloudSessions.mount.tsx", import.meta.url))], outfile: join(root, "fixture.js"), bundle: true, platform: "browser", format: "iife",
        // The page is given the host it talks to: the generated client is not part of it.
        plugins: [{ name: "no-host", setup(bundle) { bundle.onResolve({ filter: /^#neoastra$/ }, () => ({ path: fileURLToPath(new URL("../demo-api.ts", import.meta.url)) })); } }] });
      await writeFile(join(root, "style.css"), await readFile(new URL("../../node_modules/@blueprintjs/core/lib/css/blueprint.css", import.meta.url), "utf8")
        + await readFile(new URL("../style.css", import.meta.url), "utf8") + await readFile(new URL("../worktrees/worktrees.css", import.meta.url), "utf8")
        + await readFile(new URL("./cloudSessions.css", import.meta.url), "utf8"));
      const page = join(root, "fixture.html");
      await writeFile(page, '<!doctype html><html><head><meta charset="utf-8"><link rel="stylesheet" href="style.css"></head><body><div id="root"></div><script src="fixture.js"></script></body></html>');
      browser = spawn(edge!, [...browserBaseArgs, `--user-data-dir=${join(root, "profile")}`, "--remote-debugging-port=0", "--window-size=1200,900", "about:blank"],
        { stdio: ["ignore", "ignore", "pipe"], windowsHide: true });
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
      const text = (selector: string) => evaluate(`document.querySelector(${JSON.stringify(selector)})?.textContent ?? null`);
      // Types in a textarea of React: its value is set as the user's typing sets it.
      const type = (selector: string, value: string) => evaluate(`(() => { const area = document.querySelector(${JSON.stringify(selector)});
        Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype, "value").set.call(area, ${JSON.stringify(value)});
        area.dispatchEvent(new Event("input", { bubbles: true })); })()`);
      await command("Page.enable");
      await command("Page.navigate", { url: pathToFileURL(page).href });
      assert.equal(await until(`window.cloud && document.querySelectorAll(".cloud-session-message").length === 2 && document.querySelectorAll(".cloud-readiness li").length > 0`), true);

      // The card: what it is, where it started, its link and a phone, and the messages, the task first.
      assert.equal(await text(".cloud-session-kind"), "In the cloud");
      assert.equal(await text(".cloud-session-title"), "Fix the flaky login test");
      assert.match(String(await text(".cloud-session-meta")), /^Started .+ from main · Claude Code \(Max\)$/u);
      assert.deepEqual(await evaluate(`[...document.querySelectorAll(".cloud-session-message-sent")].map(item => item.textContent)`), ["Started the cloud session", "Sent to the cloud"]);
      await evaluate(`document.querySelector("[data-cloud-open]").click()`);
      assert.deepEqual(await evaluate("cloud.opened"), ["https://claude.ai/code/session_01QK5u8xRwDmhdvgs5BDTzPp"]);
      await evaluate(`document.querySelector("[data-cloud-phone]").click()`);
      assert.equal(await until(`!!document.querySelector(".cloud-phone [data-remote-control-qr]")`), true, "The phone shows the link as a QR code");
      await evaluate(`document.querySelector("[data-cloud-phone]").click()`);
      assert.equal(await until(`!document.querySelector(".cloud-phone")`), true);

      // The card in the tone of the cloud in both themes, and nothing wider than its pane.
      for (const theme of ["dark", "light"]) {
        await evaluate(`cloud.setTheme(${JSON.stringify(theme)})`);
        const look = await evaluate(`(() => { const mark = getComputedStyle(document.querySelector(".cloud-session-mark")).color;
          const kind = getComputedStyle(document.querySelector(".cloud-session-kind")).color; const pane = document.querySelector("[data-pane] .cloud-session-scroll");
          return { same: mark === kind, overflow: pane.scrollWidth > pane.clientWidth }; })()`) as { same: boolean; overflow: boolean };
        assert.deepEqual(look, { same: true, overflow: false }, theme);
      }

      // A message is sent with Enter, and the card lists it.
      await type(".cloud-session-prompt textarea", "Also check the logout");
      await evaluate(`document.querySelector(".cloud-session-prompt textarea").dispatchEvent(new KeyboardEvent("keydown", { key: "Enter", bubbles: true }))`);
      assert.equal(await until(`document.querySelectorAll(".cloud-session-message").length === 3`), true, "The message sent is listed");
      assert.equal(await evaluate(`document.querySelector(".cloud-session-prompt textarea").value`), "", "and the prompt is empty again");
      // A refused one says why, in words, with what Claude Code said, and keeps its text.
      await evaluate(`cloud.setSend("archived")`);
      await type(".cloud-session-prompt textarea", "One more thing");
      await evaluate(`document.querySelector("[data-cloud-send]").click()`);
      assert.equal(await until(`!!document.querySelector(".cloud-session-error")`), true);
      assert.equal(await text(".cloud-session-error"),
        "The cloud session is archived on claude.ai: it takes no more messages. You can still continue it here. Session is archived");
      assert.equal(await evaluate(`document.querySelector(".cloud-session-prompt textarea").value`), "One more thing");

      // Continue here: the folder has changes, so the new worktree is chosen and the folder is not offered.
      await evaluate(`document.querySelector("[data-cloud-continue]").click()`);
      assert.equal(await until(`!!document.querySelector(".cloud-continue-dialog input[value=project]")`), true, "The dialog opens");
      assert.deepEqual(await evaluate(`(() => { const project = document.querySelector(".cloud-continue-dialog input[value=project]"), worktree = document.querySelector(".cloud-continue-dialog input[value=worktree]");
        return { project: project.disabled, worktree: worktree.checked, why: project.closest("label").textContent }; })()`),
        { project: true, worktree: true, why: "Project folder3 files have changes that are not committed: commit them first, or choose a new worktree." });
      await evaluate(`document.querySelector("[data-cloud-continue-run]").click()`);
      assert.equal(await until(`!!document.querySelector(".cloud-continue-error")`), true, "A refusal is shown");
      assert.equal(await text(".cloud-continue-error"), "Claude Code could not bring the cloud session here. Could not fetch the branch claude/fix-login");
      assert.equal(await evaluate(`!!document.querySelector("[data-cloud-terminal]")`), true, "and the terminal is offered");
      await evaluate(`cloud.setContinue("ok")`);
      await evaluate(`document.querySelector("[data-cloud-continue-run]").click()`);
      assert.equal(await until(`!document.querySelector(".cloud-continue-dialog") && cloud.calls.includes("changed")`), true, "Brought back: the dialog closes and the sessions are read again");
      assert.deepEqual((await evaluate("cloud.calls") as string[]).filter(call => call.startsWith("continue")), ["continue worktree=true terminal=false", "continue worktree=true terminal=false"]);

      // The start: its step with Stop, then a cloud session that runs but was not recorded, with its link.
      assert.equal(await text(".cloud-start-step"), "Starting Claude Code…");
      await evaluate(`cloud.startStep("asking")`);
      assert.equal(await until(`document.querySelector(".cloud-start-step").textContent === "Claude Code asks a question: answer it in the terminal tab."`), true);
      await evaluate(`document.querySelector("[data-cloud-stop]").click()`);
      await evaluate(`cloud.startOutcome("unrecorded")`);
      assert.equal(await until(`!!document.querySelector(".cloud-start-link")`), true);
      assert.deepEqual(await evaluate(`[...document.querySelectorAll(".cloud-start-actions button")].map(button => button.textContent)`), ["Open on claude.ai", "Bring it here", "Close"]);
      await evaluate(`cloud.startOutcome("failed")`);
      assert.equal(await until(`document.querySelector(".cloud-start-said")?.textContent === "Error: You must be logged in to claude.ai"`), true, "Claude Code's own words");
      assert.deepEqual(await evaluate(`[...document.querySelectorAll(".cloud-start-actions button")].map(button => button.textContent)`), ["Show the terminal", "Close"]);
      await evaluate(`[...document.querySelectorAll(".cloud-start-actions button")][0].click()`);
      assert.ok((await evaluate("cloud.calls") as string[]).includes("terminal t1"));
      assert.ok((await evaluate("cloud.calls") as string[]).includes("stop"));

      // The readiness of the project, in lines of their tone.
      assert.deepEqual(await evaluate(`[...document.querySelectorAll(".cloud-readiness li")].map(item => item.dataset.tone)`), ["ok", "ok", "warning", "warning", "info"]);

      // The Cloud place, third of where a session works.
      await evaluate(`document.querySelector("[data-place] button").click()`);
      assert.equal(await until(`[...document.querySelectorAll(".work-place-form [role=radio], .work-place-form button")].some(button => button.textContent === "Cloud")`), true);
      await evaluate(`[...document.querySelectorAll(".work-place-form [role=radio], .work-place-form button")].find(button => button.textContent === "Cloud").click()`);
      assert.equal(await until(`!!document.querySelector(".work-place-cloud") && document.querySelector("[data-place] button[data-cloud]")?.textContent === "Cloud"`), true,
        "The chip says Cloud, and the popover what it means");
      assert.deepEqual(await evaluate("cloud.place()"), { worktree: false, base: null, cloud: true });
    } finally {
      // Edge's launcher can exit while the browser it started goes on: the browser itself is asked to close.
      if (socket?.readyState === WebSocket.OPEN) socket.send(JSON.stringify({ id: 9999, method: "Browser.close" }));
      socket?.close(); browser?.kill(); await rm(root, { recursive: true, force: true, maxRetries: 8, retryDelay: 100 });
    }
  });
