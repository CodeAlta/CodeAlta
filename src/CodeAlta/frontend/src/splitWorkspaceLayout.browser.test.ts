import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { existsSync } from "node:fs";
import { mkdtemp, readFile, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";
import { build } from "esbuild";
import type { AppLayoutSnapshot, SplitSnapshot, SplitStats } from "./splitWorkspaceLayout.mount";

// Finite selected-page harness, adapted from the accepted responsive candidate and
// markdownContent.browser.test.ts. No native host, real bridge or all-target claim.
test("opt-in production split: ordinary scheduling, geometry/ARIA and stable caller lifetimes", { timeout: 60_000 }, async t => {
  const edge = ["C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe", "C:/Program Files/Microsoft/Edge/Application/msedge.exe"].find(existsSync);
  assert.ok(edge, "approved frontend browser unavailable: stop, no alternate launcher");
  const directory = await mkdtemp(join(tmpdir(), "codealta-split-resize-lifetime-"));
  t.diagnostic(`Selected-page evidence/profile retained at ${directory}`);
  const config = JSON.parse(await readFile(new URL("../../neoastra.json", import.meta.url), "utf8")) as { assets: { csp: string } };
  const bundle = await build({ entryPoints: [fileURLToPath(new URL("./splitWorkspaceLayout.mount.tsx", import.meta.url))],
    bundle: true, platform: "browser", format: "iife", write: false, outfile: "fixture.js", metafile: true });
  await writeFile(join(directory, "metafile.json"), JSON.stringify(bundle.metafile, null, 2));
  const origin = "https://split-workspace.invalid", page = origin + "/index.html";
  const assets = new Map<string, [string, string]>([
    [page, ["text/html", '<!doctype html><html><head><link rel="icon" href="data:,"><link rel="stylesheet" href="/fixture.css"></head><body><div id="app"></div><script src="/fixture.js"></script></body></html>']],
    [origin + "/fixture.js", ["text/javascript", bundle.outputFiles.find(f => f.path.endsWith(".js"))!.text]],
    [origin + "/fixture.css", ["text/css", bundle.outputFiles.find(f => f.path.endsWith(".css"))!.text +
      "\nhtml,body{margin:0;width:100%;height:100%}#app{position:relative;width:100%;height:100%}"]],
  ]);
  type Reply = { result?: { value?: unknown }; exceptionDetails?: unknown };
  let browser: ReturnType<typeof spawn> | undefined, socket: WebSocket | undefined;
  let command: ((method: string, params?: object) => Promise<Reply>) | undefined;
  let rootExited = false, closeAcknowledged = false;
  const violations: string[] = [], attempts: string[] = [], observations: unknown[] = [];
  try {
    const profile = join(directory, "profile");
    browser = spawn(edge, ["--headless=new", "--disable-gpu", "--no-first-run", "--disable-background-networking", "--disable-extensions",
      "--host-resolver-rules=MAP * ~NOTFOUND", `--user-data-dir=${profile}`, "--remote-debugging-port=0", "about:blank"], { stdio: "ignore", windowsHide: true });
    const exited = new Promise<void>(resolve => browser!.once("exit", () => { rootExited = true; resolve(); }));
    let port = "";
    // Bounded readiness sampling for this one launch, not launch retries.
    for (let i = 0; i < 100 && !port; i++) {
      try { port = (await readFile(join(profile, "DevToolsActivePort"), "utf8")).split(/\r?\n/)[0]; }
      catch { await new Promise(resolve => setTimeout(resolve, 50)); }
    }
    assert.match(port, /^\d+$/);
    const pages = await (await fetch(`http://127.0.0.1:${port}/json/list`, { signal: AbortSignal.timeout(5000) })).json() as { type: string; url: string; webSocketDebuggerUrl: string }[];
    const selected = pages.find(p => p.type === "page" && p.url === "about:blank");
    assert.ok(selected?.webSocketDebuggerUrl);
    socket = new WebSocket(selected.webSocketDebuggerUrl);
    await new Promise<void>((resolve, reject) => { socket!.addEventListener("open", () => resolve(), { once: true }); socket!.addEventListener("error", reject, { once: true }); });
    let sequence = 0;
    const pending = new Map<number, (reply: { error?: unknown; result?: Reply }) => void>(), intake = new Set<Promise<void>>();
    command = (method, params = {}) => new Promise<Reply>((resolve, reject) => {
      const id = ++sequence;
      const timer = setTimeout(() => { pending.delete(id); reject(new Error(`${method} timeout`)); }, 8000);
      pending.set(id, reply => { clearTimeout(timer); reply.error ? reject(new Error(JSON.stringify(reply.error))) : resolve(reply.result ?? {}); });
      socket!.send(JSON.stringify({ id, method, params }));
    });
    socket.addEventListener("message", event => {
      const m = JSON.parse(String(event.data));
      if (pending.has(m.id)) { pending.get(m.id)!(m); pending.delete(m.id); }
      if (m.method === "Fetch.requestPaused") {
        const { requestId, request } = m.params; attempts.push(request.url);
        const asset = assets.get(request.url);
        if (!asset) violations.push(`unexpected request ${request.url}`);
        const work = (asset ? command!("Fetch.fulfillRequest", { requestId, responseCode: 200, responseHeaders: [
          { name: "Content-Type", value: asset[0] }, { name: "Content-Security-Policy", value: config.assets.csp }], body: Buffer.from(asset[1]).toString("base64") })
          : command!("Fetch.failRequest", { requestId, errorReason: "BlockedByClient" })).then(() => {}, e => { violations.push(String(e)); });
        intake.add(work); void work.finally(() => intake.delete(work));
      }
      if (m.method === "Network.requestWillBeSent" && !assets.has(m.params.request.url)) violations.push(`network attempt ${m.params.request.url}`);
      if (["Page.windowOpen", "Page.frameAttached", "Page.frameRequestedNavigation", "Runtime.exceptionThrown"].includes(m.method)) violations.push(JSON.stringify(m));
      if (m.method === "Page.frameNavigated" && m.params.frame.url !== page) violations.push(JSON.stringify(m));
      if (m.method === "Log.entryAdded" && m.params.entry.source === "security") violations.push(m.params.entry.text);
    });
    await command("Page.enable"); await command("Runtime.enable"); await command("Network.enable"); await command("Log.enable");
    await command("Fetch.enable", { patterns: [{ urlPattern: "*", requestStage: "Request" }] });
    const viewport = (width: number) => command!("Emulation.setDeviceMetricsOverride", { width, height: 600, deviceScaleFactor: 1, mobile: false });
    await viewport(1000); await command("Page.navigate", { url: page });
    const evaluate = async <T,>(expression: string): Promise<T> => {
      assert.deepEqual(violations, [], "stop on page-scope safety/setup failure");
      const reply = await command!("Runtime.evaluate", { expression, returnByValue: true, awaitPromise: true });
      assert.equal(reply.exceptionDetails, undefined, JSON.stringify(reply.exceptionDetails));
      assert.deepEqual(violations, []);
      return reply.result?.value as T;
    };
    async function wait(condition: string) {
      let ready = false;
      for (let i = 0; i < 100 && !ready; i++) {
        ready = await evaluate<boolean>(condition);
        if (!ready) await new Promise(resolve => setTimeout(resolve, 40));
      }
      assert.ok(ready, condition);
    }
    await wait('!!window.splitFixture && document.querySelectorAll("[data-fake-pane]").length===2');
    await evaluate("splitFixture.settle()"); await evaluate("splitFixture.capture()");
    async function snap(label: string) {
      await evaluate("splitFixture.settle(6)"); const a = await evaluate<SplitSnapshot>("splitFixture.snapshot()");
      await evaluate("splitFixture.settle()"); const b = await evaluate<SplitSnapshot>("splitFixture.snapshot()");
      observations.push({ label, first: a, second: b });
      for (const key of ["previous", "next", "row", "separator"] as const) assert.deepEqual(a[key], b[key], `${label}: finite geometry samples agree`);
      assert.deepEqual(a.sources, b.sources, `${label}: callback/frame counters settled within nine frames`);
      return b;
    }
    async function quiet(label: string) {
      const before = await evaluate<SplitSnapshot>("splitFixture.snapshot()");
      await evaluate("splitFixture.settle(12)");
      const after = await evaluate<SplitSnapshot>("splitFixture.snapshot()");
      observations.push({ label: `${label}-quiet-12-frames`, before, after });
      assert.deepEqual(after, before, "no measured geometry/ARIA or instrumented callback/frame activity during explicit finite quiet sample");
      assert.equal(after.pending, 0);
    }
    function realResize(before: SplitStats, after: SplitStats) {
      const a = before.sources.resize, b = after.sources.resize;
      assert.ok(b.calls > a.calls && b.calls - a.calls <= 8, "real public resize deliveries are nonzero and bounded");
      assert.ok(b.scheduled > a.scheduled && b.scheduled - a.scheduled <= 4, "real resize deliveries schedule a bounded number of frames");
      assert.equal(b.fired - a.fired, b.scheduled - a.scheduled); assert.equal(b.pending, 0);
      assert.deepEqual(after.sources.key, before.sources.key); assert.deepEqual(after.sources.replay, before.sources.replay);
    }
    function verify(s: SplitSnapshot, inputFocus = false) {
      assert.ok(s.sameSeparator && s.samePanes && s.sameInputs);
      assert.ok(inputFocus ? s.inputFocused : s.focused);
      assert.equal(s.setups, 4); assert.equal(s.cleanups, 2); assert.ok(s.replaySameNodes, "StrictMode replay retains DOM");
      assert.equal(s.subscriptions, 4); assert.equal(s.removals, 2); assert.equal(s.listeners, 2, "resize subscriptions survive StrictMode replay exactly once");
      assert.equal(s.orientation, "vertical"); assert.equal(s.path, initial.path); assert.ok(s.path);
      assert.ok(s.previous.width >= 159.5 && s.next.width >= 159.5);
      assert.ok(s.previous.right <= s.separator.x + 0.5 && s.separator.right <= s.next.x + 0.5);
      assert.equal(s.now, s.expected, "ARIA value matches actual geometry, not weights"); assert.equal(s.text, `${s.expected}%`);
      assert.equal(s.pending, 0);
      assert.equal(s.scheduled, s.fired + s.canceled + s.pending);
      for (const source of Object.values(s.sources)) {
        assert.equal(source.pending, 0); assert.equal(source.scheduled, source.fired + source.canceled + source.pending);
      }
    }
    const initial = await snap("initial"); verify(initial);
    await quiet("initial");
    await evaluate(`(() => { const host=document.querySelector('.workspace-layout');
      for (const key of ['Delete','F2','Escape']) document.activeElement.dispatchEvent(new KeyboardEvent('keydown',{key,bubbles:true,cancelable:true}));
      document.activeElement.dispatchEvent(new KeyboardEvent('keydown',{key:'w',ctrlKey:true,bubbles:true,cancelable:true}));
      for (const type of ['dragstart','dragover','drop']) host.dispatchEvent(new DragEvent(type,{bubbles:true,cancelable:true,dataTransfer:new DataTransfer()}));
    })()`);
    const disabled = await snap("disabled-commands-and-drag"); verify(disabled);
    assert.deepEqual(disabled.separator, initial.separator);
    assert.equal(await evaluate("document.querySelectorAll('.flexlayout__tab').length"), 2);
    assert.equal(await evaluate("document.querySelectorAll('.flexlayout__tab_button,.flexlayout__tabset_header,.flexlayout__floating_window,[aria-keyshortcuts]').length"), 0);
    await evaluate("splitFixture.focusInput(); splitFixture.refresh()");
    await wait('document.querySelector("[data-fake-pane] span")?.textContent==="latest:left"');
    const refreshed = await snap("normal-scheduling-refresh"); verify(refreshed, true);
    assert.deepEqual(refreshed.labels, ["latest:left", "latest:right"]); assert.equal(refreshed.inputValue, "owned draft");
    await evaluate("splitFixture.focusSeparator()");
    async function drag(before: SplitSnapshot, destination: number) {
      const x = before.separator.x + before.separator.width / 2, y = before.separator.y + before.separator.height / 2;
      await command!("Input.dispatchMouseEvent", { type: "mouseMoved", x, y });
      await command!("Input.dispatchMouseEvent", { type: "mousePressed", x, y, button: "left", buttons: 1, clickCount: 1 });
      await command!("Input.dispatchMouseEvent", { type: "mouseMoved", x: x + 12, y, button: "left", buttons: 1 });
      await command!("Input.dispatchMouseEvent", { type: "mouseMoved", x: destination, y, button: "left", buttons: 1 });
      await command!("Input.dispatchMouseEvent", { type: "mouseReleased", x: destination, y, button: "left", buttons: 0, clickCount: 1 });
    }
    async function interactions(label: string, before: SplitSnapshot) {
      for (const type of ["keyDown", "keyUp"]) await command!("Input.dispatchKeyEvent", { type, key: "ArrowRight", code: "ArrowRight", windowsVirtualKeyCode: 39 });
      const keyboard = await snap(`${label}-keyboard`); verify(keyboard); assert.ok(keyboard.separator.x - before.separator.x > 5);
      await drag(keyboard, keyboard.separator.x + 30);
      const pointer = await snap(`${label}-pointer`); verify(pointer); assert.ok(pointer.separator.x - keyboard.separator.x > 15);
      return pointer;
    }
    const large = await interactions("1000", refreshed);
    await drag(large, 999); const rightMin = await snap("right-minimum"); verify(rightMin); assert.equal(rightMin.next.width, 160);
    await drag(rightMin, 1); const leftMin = await snap("left-minimum"); verify(leftMin); assert.equal(leftMin.previous.width, 160);
    const beforeShrink = await evaluate<SplitStats>("splitFixture.stats()");
    await viewport(420); const small = await snap("viewport-420"); verify(small); assert.equal(small.row.width, 420);
    realResize(beforeShrink, small); await quiet("viewport-420");
    await interactions("420", small);
    const beforeExpand = await evaluate<SplitStats>("splitFixture.stats()");
    await viewport(1100); const expanded = await snap("viewport-1100"); verify(expanded); assert.equal(expanded.row.width, 1100);
    realResize(beforeExpand, expanded); await quiet("viewport-1100");
    await evaluate("splitFixture.parentWidth(500)");
    const parent = await snap("parent-only-500"); verify(parent); assert.equal(parent.row.width, 500); assert.equal(await evaluate("innerWidth"), 1100);
    realResize(expanded, parent); await quiet("parent-only-500");
    await interactions("parent-500", parent);
    const beforeBurst = await evaluate<SplitStats>("splitFixture.stats()");
    const pendingBurst = await evaluate<SplitStats>("splitFixture.burst(4)");
    assert.equal(pendingBurst.scheduled - beforeBurst.scheduled, 1); assert.equal(pendingBurst.pending, 1);
    assert.equal(pendingBurst.sources.key.calls - beforeBurst.sources.key.calls, 4);
    assert.equal(pendingBurst.sources.key.scheduled - beforeBurst.sources.key.scheduled, 1);
    const afterBurst = await snap("coalesced-burst"); verify(afterBurst);
    assert.equal(afterBurst.sources.key.fired - beforeBurst.sources.key.fired, 1);
    // Measurement may legitimately deliver another resize callback/frame after this
    // synchronous coalescing sample; do not require one total frame across settling.
    assert.ok(afterBurst.scheduled - beforeBurst.scheduled <= 5); await quiet("key-burst");
    observations.push({ beforeBurst, pendingBurst, afterBurst });
    const replacement = await evaluate<{ pending: SplitStats; disposed: SplitStats; disconnected: boolean }>("splitFixture.replacePending()");
    observations.push({ replacement });
    assert.equal(replacement.pending.pending, 1); assert.equal(replacement.disposed.pending, 0);
    assert.equal(replacement.disposed.canceled, beforeBurst.canceled + 1); assert.ok(replacement.disconnected);
    assert.equal(replacement.disposed.setups, replacement.disposed.cleanups);
    assert.equal(replacement.disposed.listeners, 0); assert.equal(replacement.disposed.subscriptions, replacement.disposed.removals);
    await wait('document.querySelectorAll("[data-fake-pane]").length===2');
    await evaluate("splitFixture.settle()"); await evaluate("splitFixture.capture()");
    const fresh = await snap("fresh-instance"); verify(fresh); assert.equal(fresh.inputValue, "draft"); assert.equal(fresh.sources.key.scheduled, 0);
    await evaluate("splitFixture.burst(2)"); const freshBurst = await snap("fresh-instance-burst"); verify(freshBurst);
    assert.equal(freshBurst.sources.key.scheduled, 1); assert.equal(freshBurst.sources.key.fired, 1);
    await quiet("fresh-key-instance");
    assert.deepEqual(await evaluate("splitFixture.disposed()"), [replacement.disposed], "old callbacks cannot reach the fresh instance");
    // First prove genuine geometry-driven callback/frame activity on this instance.
    await evaluate("splitFixture.parentWidth(560)");
    const beforeReplay = await snap("fresh-real-parent-560"); verify(beforeReplay); assert.equal(beforeReplay.row.width, 560);
    realResize(freshBurst, beforeReplay); await quiet("fresh-real-parent-560");
    // Replay is used ONLY to put resize-origin work pending in the same JS turn
    // as unmount. It neither sets geometry nor stands in for the real tests above.
    const replayReplacement = await evaluate<{ pending: SplitStats; disposed: SplitStats; disconnected: boolean }>("splitFixture.replacePending('replay')");
    observations.push({ label: "explicit-resize-callback-replay-pending-unmount", beforeReplay, replayReplacement });
    assert.deepEqual(replayReplacement.pending.sources.resize, beforeReplay.sources.resize);
    assert.deepEqual(replayReplacement.pending.sources.key, beforeReplay.sources.key);
    assert.equal(replayReplacement.pending.sources.replay.calls, 4);
    assert.equal(replayReplacement.pending.sources.replay.scheduled, 1, "four synchronous resize callback replays coalesce");
    assert.equal(replayReplacement.pending.sources.replay.pending, 1); assert.equal(replayReplacement.pending.pending, 1);
    assert.equal(replayReplacement.disposed.sources.replay.canceled, 1); assert.equal(replayReplacement.disposed.sources.replay.fired, 0);
    assert.equal(replayReplacement.disposed.sources.replay.pending, 0); assert.equal(replayReplacement.disposed.pending, 0);
    assert.equal(replayReplacement.disposed.listeners, 0); assert.ok(replayReplacement.disconnected);
    assert.equal(replayReplacement.disposed.subscriptions, replayReplacement.disposed.removals);
    assert.equal(replayReplacement.disposed.setups, replayReplacement.disposed.cleanups);
    await wait('document.querySelectorAll("[data-fake-pane]").length===2');
    await evaluate("splitFixture.settle();"); await evaluate("splitFixture.capture()");
    const resizeFresh = await snap("resize-disposed-fresh-instance"); verify(resizeFresh);
    assert.equal(resizeFresh.inputValue, "draft"); assert.equal(resizeFresh.sources.replay.calls, 0);
    await evaluate("splitFixture.parentWidth(620)");
    const resizedFresh = await snap("resize-disposed-fresh-real-parent-620"); verify(resizedFresh); assert.equal(resizedFresh.row.width, 620);
    realResize(resizeFresh, resizedFresh); await quiet("resize-disposed-fresh");
    assert.deepEqual(await evaluate("splitFixture.disposed()"), [replacement.disposed, replayReplacement.disposed], "disposed resize callbacks/frames remain unchanged through new geometry deliveries and quiet sample");
    // Single-pane production compatibility, also with normal caller updates.
    await evaluate("splitFixture.single()"); await wait('document.querySelector("[data-fake-pane=single]")!==null');
    await evaluate("splitFixture.settle();"); await evaluate("splitFixture.capture(); splitFixture.focusInput(); splitFixture.refresh()");
    await wait('document.querySelector("[data-fake-pane] span")?.textContent==="latest:single"');
    const single = await evaluate<{ count: number; controls: number; sameInput: boolean; focused: boolean; value: string; width: number }>("splitFixture.singleSnapshot()");
    assert.equal(single.count, 1); assert.equal(single.controls, 0); assert.ok(single.sameInput && single.focused);
    assert.equal(single.value, "owned draft"); assert.ok(single.width > 0);
    const disposed = await evaluate<SplitStats>("splitFixture.unmount()");
    assert.equal(disposed.pending, 0); assert.equal(disposed.setups, disposed.cleanups);
    assert.equal(disposed.listeners, 0);
    await evaluate("splitFixture.settle()"); assert.deepEqual(await evaluate("splitFixture.stats()"), disposed);
    // The actual App-specific adapter, with fake caller nodes and no App owners.
    // Its control is supplied by the caller: real pointer/key behavior is covered
    // by settingsShell's production-main fixture, not reimplemented here.
    await viewport(1120); await evaluate("splitFixture.mountAppLayout()");
    await wait('document.querySelectorAll("[data-fake-pane]").length===2');
    await evaluate("splitFixture.settle(6)"); await evaluate("splitFixture.capture(); splitFixture.focusInput(); splitFixture.refresh()");
    async function appSnap(label: string) {
      await evaluate("splitFixture.settle(6)"); const before = await evaluate<AppLayoutSnapshot>("splitFixture.appSnapshot()");
      await evaluate("splitFixture.settle(12)"); const after = await evaluate<AppLayoutSnapshot>("splitFixture.appSnapshot()");
      observations.push({ label, before, after }); assert.deepEqual(after, before, `${label}: finite quiet geometry and resize-source counters`);
      assert.ok(after.samePanes && after.sameInputs); assert.equal(after.setups, 4); assert.equal(after.cleanups, 2);
      assert.equal(after.subscriptions, 4); assert.equal(after.removals, 2); assert.equal(after.listeners, 2); assert.equal(after.probes, 1);
      assert.equal(after.pending, 0); return after;
    }
    const appInitial = await appSnap("app-adapter-initial");
    assert.equal(appInitial.sessions.width, 310); assert.equal(appInitial.bar.width, 8); assert.equal(appInitial.aria, "310");
    assert.ok(appInitial.focused); assert.equal(appInitial.value, "owned draft"); assert.equal(appInitial.label, "latest:left");
    await viewport(876); const appDesktop = await appSnap("app-adapter-876"); assert.equal(appDesktop.sessions.width, 310);
    await viewport(875); await evaluate("splitFixture.projectAppLayout(true,false,310)");
    const appNarrow = await appSnap("app-adapter-875x600"); assert.equal(appNarrow.sessions.height, 240);
    assert.equal(appNarrow.sessions.width, 875); assert.equal(appNarrow.bar.width, 0); assert.ok(appNarrow.focused);
    await evaluate("splitFixture.projectAppLayout(true,true,310)");
    const appHidden = await appSnap("app-adapter-hidden"); assert.equal(appHidden.sessions.height, 0); assert.equal(appHidden.sessions.width, 0);
    assert.equal(appHidden.content.y, 0); assert.equal(appHidden.content.height, 600);
    await evaluate("splitFixture.projectAppLayout(true,false,310)"); await appSnap("app-adapter-shown");
    await viewport(1120); await evaluate("splitFixture.projectAppLayout(false,false,310)");
    await appSnap("app-adapter-desktop-restored");
    const appBeforeReplace = await evaluate<SplitStats>("splitFixture.stats()");
    const appReplacement = await evaluate<{ pending: SplitStats; disposed: SplitStats; disconnected: boolean }>("splitFixture.replacePending('replay')");
    observations.push({ label: "app-adapter-resize-replay-unmount", appBeforeReplace, appReplacement });
    assert.equal(appReplacement.pending.sources.replay.scheduled - appBeforeReplace.sources.replay.scheduled, 1);
    assert.equal(appReplacement.pending.sources.replay.pending, 1); assert.equal(appReplacement.disposed.sources.replay.pending, 0);
    assert.equal(appReplacement.disposed.sources.replay.canceled - appBeforeReplace.sources.replay.canceled, 1);
    assert.equal(appReplacement.disposed.sources.replay.fired, appBeforeReplace.sources.replay.fired);
    assert.equal(appReplacement.disposed.probes, 0); assert.equal(appReplacement.disposed.listeners, 0); assert.ok(appReplacement.disconnected);
    await wait('document.querySelectorAll("[data-fake-pane]").length===2'); await evaluate("splitFixture.settle(6); splitFixture.capture()");
    await appSnap("app-adapter-fresh");
    await evaluate("splitFixture.parentWidth(900)"); const appFreshResize = await appSnap("app-adapter-fresh-parent-resize");
    assert.ok(appFreshResize.sources.resize.calls > 0); assert.ok(appFreshResize.sources.resize.fired > 0);
    const oldAudits = await evaluate<SplitStats[]>("splitFixture.disposed()"); assert.deepEqual(oldAudits.at(-1), appReplacement.disposed);
    const appDisposed = await evaluate<SplitStats>("splitFixture.unmount()"); await evaluate("splitFixture.settle(12)");
    assert.equal(appDisposed.probes, 0); assert.equal(appDisposed.listeners, 0); assert.equal(appDisposed.pending, 0);
    assert.deepEqual(await evaluate("splitFixture.stats()"), appDisposed);
    assert.deepEqual(attempts.sort(), [...assets.keys()].sort()); await Promise.all([...intake]); assert.deepEqual(violations, []);
    await command("Browser.close"); closeAcknowledged = true;
    await Promise.race([exited, new Promise((_, reject) => setTimeout(() => reject(new Error("root exit unavailable")), 5000))]);
  } finally {
    if (!rootExited && command && socket?.readyState === WebSocket.OPEN) {
      try { await command("Browser.close"); closeAcknowledged = true; } catch (e) { violations.push(`close: ${e}`); }
      await new Promise(resolve => setTimeout(resolve, 500));
    }
    socket?.close();
    if (!rootExited && browser) { browser.kill(); violations.push("task-owned root required kill; cleanup uncertain"); }
    await writeFile(join(directory, "observations.json"), JSON.stringify({ observations, attempts, violations, closeAcknowledged, rootExited,
      scope: "selected page only; no transport-finality/all-target/native/descendant containment claim" }, null, 2));
  }
  assert.deepEqual(violations, []); assert.ok(closeAcknowledged && rootExited);
});
