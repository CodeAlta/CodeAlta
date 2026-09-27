import assert from "node:assert/strict";
import { timelineGeometryProbe } from "./timelineGeometryProbe";
import { spawn } from "node:child_process";
import { existsSync, readFileSync } from "node:fs";
import { mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { fileURLToPath, pathToFileURL } from "node:url";
import { build } from "esbuild";
import { locales, translate } from "./localization";

const edge = ["C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe",
  "C:/Program Files/Microsoft/Edge/Application/msedge.exe"].find(existsSync);

// This mounts the production History component and scroll hook against an isolated v2 journal
// fixture. It is not native WebView2 acceptance and never opens a real catalog or user profile.
test("mounted reverse history retains latest, anchors older pages and fences switched/stale reads", { skip: !edge, timeout: 70_000 }, async () => {
  const source = readFileSync(fileURLToPath(new URL("./main.tsx", import.meta.url)), "utf8");
  assert.match(source, /read=\{workspace\.historyTail\}/, "production must wire the v2 route");
  const root = await mkdtemp(join(tmpdir(), "codealta-history-mounted-"));
  let browser: ReturnType<typeof spawn> | undefined;
  let socket: WebSocket | undefined;
  try {
    await build({ entryPoints: [fileURLToPath(new URL("./historyPanel.mount.tsx", import.meta.url))],
      outfile: join(root, "fixture.js"), bundle: true, platform: "browser", format: "iife" });
    const page = join(root, "fixture.html");
    await writeFile(join(root, "style.css"), readFileSync(fileURLToPath(new URL("./style.css", import.meta.url))));
    await writeFile(page, '<!doctype html><html><head><link rel="stylesheet" href="style.css"></head><body><div id="app"></div><script src="fixture.js"></script></body></html>');
    const profile = join(root, "profile");
    browser = spawn(edge!, ["--headless=new", "--disable-gpu", "--no-first-run", "--disable-background-networking", "--disable-extensions",
      `--user-data-dir=${profile}`, "--remote-debugging-port=0", "about:blank"], { stdio: "ignore", windowsHide: true });
    let port = "";
    for (let attempt = 0; attempt < 100 && !port; attempt++) {
      try { port = (await readFile(join(profile, "DevToolsActivePort"), "utf8")).split(/\r?\n/)[0]; }
      catch { await new Promise(resolve => setTimeout(resolve, 50)); }
    }
    assert.match(port, /^\d+$/);
    const pages: { type?: string; url?: string; webSocketDebuggerUrl?: string }[] =
      await (await fetch(`http://127.0.0.1:${port}/json/list`, { signal: AbortSignal.timeout(10_000) })).json();
    const tab = pages.find(value => value.type === "page" && value.url === "about:blank");
    assert.ok(tab?.webSocketDebuggerUrl);
    socket = new WebSocket(tab.webSocketDebuggerUrl);
    await new Promise<void>((resolve, reject) => {
      socket!.addEventListener("open", () => resolve(), { once: true });
      socket!.addEventListener("error", () => reject(new Error("test browser debugger unavailable")), { once: true });
    });
    let sequence = 0;
    const command = (method: string, params: object = {}) => new Promise<{ result?: { value?: string } }>((resolve, reject) => {
      const id = ++sequence;
      const timer = setTimeout(() => reject(new Error(`test browser ${method} timed out`)), 12_000);
      const reply = (event: MessageEvent) => {
        const message = JSON.parse(String(event.data)) as { id?: number; result?: { result?: { value?: string } }; error?: object };
        if (message.id !== id) return;
        socket!.removeEventListener("message", reply);
        clearTimeout(timer);
        if (message.error) reject(new Error(`test browser ${method} failed`));
        else resolve(message.result ?? {});
      };
      socket!.addEventListener("message", reply);
      socket!.send(JSON.stringify({ id, method, params }));
    });
    await command("Page.enable");
    const loaded = new Promise<void>((resolve, reject) => {
      const timer = setTimeout(() => reject(new Error("test page did not load")), 10_000);
      const listener = (event: MessageEvent) => {
        if ((JSON.parse(String(event.data)) as { method?: string }).method !== "Page.loadEventFired") return;
        clearTimeout(timer); socket!.removeEventListener("message", listener); resolve();
      };
      socket!.addEventListener("message", listener);
    });
    await command("Page.navigate", { url: pathToFileURL(page).href });
    await loaded;
    const evaluate = async (expression: string) => (await command("Runtime.evaluate", { expression, returnByValue: true,
      awaitPromise: true })).result?.value;
    const wait = async (predicate: string) => {
      const result = await evaluate(`new Promise(resolve => { const end = Date.now() + 9000; const check = () => {
        if (${predicate}) resolve('ready'); else if (Date.now() > end) resolve('timeout'); else setTimeout(check, 35); }; check(); })`);
      if (result !== "ready") assert.fail(`mounted history did not reach ${predicate}: ${await evaluate("document.body.innerText.slice(0, 360)")}`);
    };
    const snapshot = () => evaluate(`JSON.stringify({ rows: document.querySelectorAll('.timeline-message').length,
      first: document.querySelector('.timeline-message')?.textContent, last: [...document.querySelectorAll('.timeline-message')].at(-1)?.textContent,
      top: document.querySelector('.timeline-scroll')?.scrollTop, height: document.querySelector('.timeline-scroll')?.scrollHeight,
      following: document.querySelector('.timeline-scroll')?.dataset.following, calls: window.fixture.calls })`);
    const languages = async () => {
      const paint = () => evaluate("new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(()=>resolve(true))))");
      await paint();
      await evaluate(`void(window.languageHistory={calls:JSON.stringify(fixture.calls),focus:document.activeElement,scroller:document.querySelector('.timeline-scroll'),
        outer:document.querySelector('.outer-scroll').scrollTop,row:[...document.querySelectorAll('.timeline-message')].find(row=>row.getBoundingClientRect().bottom>document.querySelector('.timeline-scroll').getBoundingClientRect().top)});
        languageHistory.top=languageHistory.row?.getBoundingClientRect().top;languageHistory.offset=languageHistory.top-languageHistory.scroller.getBoundingClientRect().top;languageHistory.following=languageHistory.scroller.dataset.following;languageHistory.metrics=[]`);
      for (const locale of locales) {
        await evaluate(`fixture.language('${locale}')`);
        await wait(`document.querySelector('#history-heading').textContent===${JSON.stringify(translate(locale, "Session timeline"))}`);
        await paint();
        if (process.env.CODEALTA_TIMELINE_GEOMETRY) console.log("HISTORY GEOMETRY", locale, JSON.stringify(await evaluate(timelineGeometryProbe)));
        await evaluate(`languageHistory.metrics.push({locale:'${locale}',top:languageHistory.row?.getBoundingClientRect().top,viewport:languageHistory.scroller.getBoundingClientRect().top,outer:document.querySelector('.outer-scroll').scrollTop,scroll:languageHistory.scroller.scrollTop,header:document.querySelector('.section-heading').getBoundingClientRect().height,banners:[...document.querySelectorAll('.banner')].map(n=>n.getBoundingClientRect().height),button:document.querySelector('.load-more')?.getBoundingClientRect().height})`);
        // Inner anchoring must stay enabled; the independent outer access scroller
        // must not compensate a second time for the same locale reflow.
        assert.equal(await evaluate("getComputedStyle(languageHistory.scroller).overflowAnchor==='auto' && getComputedStyle(document.querySelector('.outer-scroll')).overflowAnchor==='none'"), true);
        assert.equal(await evaluate("document.querySelector('.outer-scroll').scrollTop===languageHistory.outer"), true, `${locale}: locale reflow must not move the outer scroller`);
        assert.equal(await evaluate(`JSON.stringify(fixture.calls)===languageHistory.calls && document.activeElement===languageHistory.focus && document.querySelector('.timeline-scroll')===languageHistory.scroller && languageHistory.scroller.dataset.following===languageHistory.following && (!languageHistory.row || languageHistory.row.isConnected && Math.abs(languageHistory.row.getBoundingClientRect().top-languageHistory.scroller.getBoundingClientRect().top-languageHistory.offset)<2)`), true, `${locale}: history keeps viewport-relative anchor, follow, mounted owner and admitted reads`);
      }
      await evaluate("fixture.language('en')"); await wait("document.querySelector('#history-heading').textContent==='Session timeline'");
      await paint();
      assert.equal(await evaluate("!languageHistory.row || Math.abs(languageHistory.row.getBoundingClientRect().top-languageHistory.top)<2"), true, `restoring English also retains the history anchor: ${await evaluate("JSON.stringify({metrics:languageHistory.metrics,top:languageHistory.row?.getBoundingClientRect().top,scroll:languageHistory.scroller.scrollTop,header:document.querySelector('.section-heading').getBoundingClientRect().height,banners:[...document.querySelectorAll('.banner')].map(n=>n.getBoundingClientRect().height),button:document.querySelector('.load-more')?.getBoundingClientRect().height})")}`);
    };
    await wait("document.querySelectorAll('.timeline-message').length === 1000 && document.body.innerText.includes('latest user prompt')");
    let result = JSON.parse((await snapshot())!) as { rows: number; first: string; last: string; top: number; height: number; following: string; calls: string[] };
    assert.match(result.first, /turn-205/);
    assert.match(result.last, /latest user prompt/);
    assert.equal(result.following, "true");
    await languages();
    assert.ok(result.calls.length <= 11 && result.calls.every(call => call.startsWith("A:tail") || call.startsWith("A:2:")));
    await evaluate(`(() => { const outer = document.querySelector('.outer-scroll');
      document.querySelector('.keyboard-target').focus(); outer.scrollTop = 35;
      document.activeElement.dispatchEvent(new KeyboardEvent('keydown', { key: 'F3', ctrlKey: true, bubbles: true, cancelable: true }));
      return true; })()`);
    await wait("document.querySelector('.navigation-notice').textContent.includes('turn-205')");
    assert.equal((await snapshot())!.includes('"following":"false"'), true, "first retained explicitly unfollows");
    const firstPosition = JSON.parse((await evaluate(`JSON.stringify({ offset: document.querySelector('.timeline-message').getBoundingClientRect().top -
      document.querySelector('.timeline-scroll').getBoundingClientRect().top, outer: document.querySelector('.outer-scroll').scrollTop,
      focus: document.activeElement.className })`))!) as { offset: number; outer: number; focus: string };
    assert.ok(Math.abs(firstPosition.offset) < 2, "Ctrl+F3 positions the first retained row, not the outer scroller");
    assert.equal(firstPosition.outer, 35);
    assert.equal(firstPosition.focus, "keyboard-target");
    await languages();
    await evaluate(`(() => { const outside = document.querySelector('.outside-target'); outside.focus();
      outside.dispatchEvent(new KeyboardEvent('keydown', { key: 'F4', bubbles: true, cancelable: true }));
      outside.dispatchEvent(new KeyboardEvent('keydown', { key: 'F4', ctrlKey: true, bubbles: true, cancelable: true }));
      document.querySelector('.keyboard-target').focus();
      const modal = document.createElement('dialog'); modal.setAttribute('open', '');
      modal.innerHTML = '<button class="modal-target">Modal</button>'; document.querySelector('.workspace-shell').append(modal);
      modal.querySelector('button').focus();
      modal.querySelector('button').dispatchEvent(new KeyboardEvent('keydown', { key: 'F4', bubbles: true, cancelable: true }));
      modal.querySelector('button').dispatchEvent(new KeyboardEvent('keydown', { key: 'F4', ctrlKey: true, bubbles: true, cancelable: true }));
      modal.remove(); document.querySelector('.keyboard-target').focus();
      document.activeElement.dispatchEvent(new KeyboardEvent('keydown', { key: 'F4', isComposing: true, bubbles: true, cancelable: true }));
      document.activeElement.dispatchEvent(new KeyboardEvent('keydown', { key: 'F4', ctrlKey: true, isComposing: true, bubbles: true, cancelable: true }));
      return true; })()`);
    assert.match((await evaluate("document.querySelector('.navigation-notice').textContent"))!, /turn-205/,
      "outside focus, modal and IME must not navigate");
    await evaluate(`document.activeElement.dispatchEvent(new KeyboardEvent('keydown', { key: 'F4', bubbles: true, cancelable: true }))`);
    await wait("document.querySelector('.navigation-notice').textContent.includes('turn-206')");
    await evaluate(`document.activeElement.dispatchEvent(new KeyboardEvent('keydown', { key: 'F3', bubbles: true, cancelable: true }))`);
    await wait("document.querySelector('.navigation-notice').textContent.includes('turn-205')");
    await evaluate(`document.activeElement.dispatchEvent(new KeyboardEvent('keydown', { key: 'F3', bubbles: true, cancelable: true }))`);
    await wait("document.querySelector('.navigation-notice').textContent.includes('First retained message; older journal')");
    await evaluate(`(() => { const scroller = document.querySelector('.timeline-scroll');
      const row = [...document.querySelectorAll('.timeline-message')].find(row => row.textContent.includes('turn-1198'));
      scroller.scrollTop += row.getBoundingClientRect().top - scroller.getBoundingClientRect().top;
      scroller.dispatchEvent(new Event('scroll', { bubbles: true }));
      for (let i = 0; i < 4; i++)
        document.activeElement.dispatchEvent(new KeyboardEvent('keydown', { key: 'F4', bubbles: true, cancelable: true }));
      return true; })()`);
    await wait("document.querySelector('.navigation-notice').textContent.includes('latest user prompt')");
    assert.equal((await snapshot())!.includes('"following":"false"'), true, "navigating onto the bottom row stays unfollowed");
    await evaluate(`(() => { const prompt = document.querySelector('#session-prompt'); prompt.focus();
      prompt.dispatchEvent(new KeyboardEvent('keydown', { key: 'F3', ctrlKey: true, bubbles: true, cancelable: true }));
      prompt.dispatchEvent(new KeyboardEvent('keydown', { key: 'F4', ctrlKey: true, bubbles: true, cancelable: true }));
      return true; })()`);
    assert.match((await evaluate("document.querySelector('.navigation-notice').textContent"))!, /latest user prompt/,
      "composer editing keeps its own shortcuts and focus");
    await evaluate("document.querySelector('.keyboard-target').focus()");
    await evaluate(`(() => { const scroller = document.querySelector('.timeline-scroll'); scroller.scrollTop = 2000;
      scroller.dispatchEvent(new Event('scroll', { bubbles: true })); const viewport = scroller.getBoundingClientRect();
      const anchor = [...document.querySelectorAll('.timeline-message')].find(row => row.getBoundingClientRect().bottom > viewport.top);
      window.fixture.anchor = anchor; window.fixture.anchorTop = anchor.getBoundingClientRect().top;
      window.fixture.holdNext(); document.querySelector('.load-more').click(); return true; })()`);
    await wait("document.querySelector('.load-more').disabled");
    await languages();
    await evaluate("fixture.release()");
    await wait("document.body.innerText.includes('Newer journal events are no longer')");
    result = JSON.parse((await snapshot())!);
    assert.equal(result.rows, 1000);
    assert.doesNotMatch(result.last, /latest user prompt/);
    assert.match(result.first, /turn-105/);
    assert.equal(result.following, "false");
    const anchored = JSON.parse((await evaluate(`JSON.stringify({ connected: window.fixture.anchor.isConnected,
      delta: window.fixture.anchor.getBoundingClientRect().top - window.fixture.anchorTop })`))!) as { connected: boolean; delta: number };
    assert.equal(anchored.connected, true);
    assert.ok(Math.abs(anchored.delta) < 2, `older-page prepend moved the reading anchor: ${JSON.stringify(anchored)}`);
    await languages();
    await evaluate(`document.activeElement.dispatchEvent(new KeyboardEvent('keydown', { key: 'F3', ctrlKey: true, bubbles: true, cancelable: true }))`);
    await wait("document.querySelector('.navigation-notice').textContent.includes('turn-105')");
    await evaluate(`document.activeElement.dispatchEvent(new KeyboardEvent('keydown', { key: 'F3', bubbles: true, cancelable: true }))`);
    await wait("document.querySelector('.navigation-notice').textContent.includes('First retained message; older journal')");
    await evaluate(`(() => { const scroller = document.querySelector('.timeline-scroll'); scroller.scrollTop = scroller.scrollHeight;
      scroller.dispatchEvent(new Event('scroll', { bubbles: true }));
      for (let i = 0; i < 8; i++)
        document.activeElement.dispatchEvent(new KeyboardEvent('keydown', { key: 'F4', bubbles: true, cancelable: true }));
      return true; })()`);
    await wait("document.querySelector('.navigation-notice').textContent.includes('Last retained message; refresh newest')");
    const olderBottom = JSON.parse((await snapshot())!);
    const olderRowTop = Number(await evaluate("[...document.querySelectorAll('.timeline-message')].at(-1).getBoundingClientRect().top"));
    const outerBeforeLatest = Number(await evaluate("document.querySelector('.outer-scroll').scrollTop"));
    await evaluate(`(() => { window.fixture.holdNext();
      for (let i = 0; i < 2; i++) document.activeElement.dispatchEvent(new KeyboardEvent('keydown',
        { key: 'F4', ctrlKey: true, bubbles: true, cancelable: true })); return true; })()`);
    await wait(`window.fixture.calls.length === ${olderBottom.calls.length + 1}`);
    assert.match((await evaluate("document.querySelector('.navigation-notice').textContent"))!, /Refreshing the newest persisted/);
    assert.equal(JSON.parse((await snapshot())!).following, "false", "latest cannot follow before its held tail read settles");
    assert.ok(Math.abs(Number(await evaluate("[...document.querySelectorAll('.timeline-message')].at(-1).getBoundingClientRect().top"))
      - olderRowTop) < 2, "pending newest read keeps the old visible row anchored");
    assert.doesNotMatch((await snapshot())!, /latest user prompt/, "old retained bottom is not latest");
    await evaluate("window.fixture.release()");
    await wait("document.querySelector('.navigation-notice').textContent.includes('Newest persisted history window loaded')");
    result = JSON.parse((await snapshot())!);
    assert.match(result.last, /latest user prompt/);
    assert.equal(result.following, "true");
    assert.ok(Math.abs(result.top - (result.height - 260)) < 2, "successful settled newest window follows its bottom");
    assert.equal(Number(await evaluate("document.querySelector('.outer-scroll').scrollTop")), outerBeforeLatest);
    assert.equal(await evaluate("document.activeElement.className"), "keyboard-target");
    await evaluate(`(() => { const style = document.createElement('style'); style.textContent = '.timeline-message { height: 56px; }';
      document.head.append(style); return new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))); })()`);
    result = JSON.parse((await snapshot())!);
    assert.ok(Math.abs(result.top - (result.height - 260)) < 2, "explicit newest follow survives deferred layout growth");
    await evaluate(`(() => { const style = document.createElement('style'); style.textContent = '.timeline-message { height: 48px; }';
      document.head.append(style); return new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))); })()`);
    await evaluate("document.activeElement.dispatchEvent(new KeyboardEvent('keydown', { key: 'F3', bubbles: true, cancelable: true }))");
    await wait("document.querySelector('.timeline-scroll').dataset.following === 'false'");
    await evaluate("document.querySelector('.load-more').click()");
    await wait("document.body.innerText.includes('Newer journal events are no longer')");
    await evaluate(`(() => { document.querySelector('.keyboard-target').focus(); window.fixture.failNext('read_failed');
      document.activeElement.dispatchEvent(new KeyboardEvent('keydown', { key: 'F4', ctrlKey: true, bubbles: true, cancelable: true }));
      return true; })()`);
    await wait("document.querySelector('.navigation-notice').textContent.includes('Newest history refresh failed')");
    result = JSON.parse((await snapshot())!);
    assert.match(result.last, /turn-1104/, "failed newest read retains older known window");
    assert.equal(result.following, "false", "failed newest read retains explicit unfollow");
    assert.equal(await evaluate("document.querySelector('.history').dataset.windowReady"), "false");
    await evaluate(`(() => { window.fixture.holdNext(); document.activeElement.dispatchEvent(new KeyboardEvent('keydown',
      { key: 'F4', ctrlKey: true, bubbles: true, cancelable: true })); return true; })()`);
    await wait("window.fixture.calls.at(-1) === 'A:tail:end'");
    await evaluate(`(() => { const scroller = document.querySelector('.timeline-scroll');
      scroller.scrollTop = 2000; scroller.dispatchEvent(new Event('scroll', { bubbles: true }));
      return true; })()`);
    await wait("document.querySelector('.navigation-notice').textContent.includes('automatic follow canceled')");
    await evaluate("window.fixture.release()");
    await wait("document.querySelector('.history').dataset.windowReady === 'true' && document.body.innerText.includes('latest user prompt')");
    assert.equal(JSON.parse((await snapshot())!).following, "false", "manual scroll overrides pending latest follow");
    assert.doesNotMatch((await evaluate("document.querySelector('.navigation-notice').textContent"))!, /window loaded/);
    await evaluate("document.querySelector('.load-more').click()");
    await wait("document.body.innerText.includes('Newer journal events are no longer')");
    await evaluate(`(() => { window.fixture.mismatchCursor(); document.activeElement.dispatchEvent(new KeyboardEvent('keydown',
      { key: 'F4', ctrlKey: true, bubbles: true, cancelable: true })); return true; })()`);
    await wait("document.querySelector('.navigation-notice').textContent.includes('Newest history refresh failed')");
    assert.match(JSON.parse((await snapshot())!).last, /turn-1104/, "changed cursor revision cannot publish a partial newest window");
    assert.equal(JSON.parse((await snapshot())!).following, "false");
    await evaluate(`(() => { window.fixture.holdNext(); document.activeElement.dispatchEvent(new KeyboardEvent('keydown',
      { key: 'F4', ctrlKey: true, bubbles: true, cancelable: true })); return true; })()`);
    await wait("window.fixture.calls.at(-1) === 'A:tail:end'");
    await evaluate("window.fixture.host('new-host'); window.fixture.release()");
    await wait("document.querySelector('.history').dataset.windowReady === 'true'");
    assert.equal(JSON.parse((await snapshot())!).following, "false", "host change cannot follow the old command");
    assert.doesNotMatch((await evaluate("document.querySelector('.navigation-notice').textContent"))!, /window loaded/);
    await evaluate("window.fixture.select('B')");
    await wait("document.querySelectorAll('.timeline-message').length === 3 && document.body.innerText.includes('turn-2')");
    await evaluate("window.fixture.select('A')");
    await wait("document.querySelectorAll('.timeline-message').length === 1000 && document.body.innerText.includes('latest user prompt')");
    result = JSON.parse((await snapshot())!);
    assert.equal(result.following, "false", "returning reader must retain user-unfollow preference");
    assert.ok(result.top >= 1900, `returning session should restore its reader's position after loading: ${result.top}`);
    await evaluate(`(() => { document.querySelector('.keyboard-target').focus(); window.fixture.holdNext();
      document.activeElement.dispatchEvent(new KeyboardEvent('keydown', { key: 'F4', ctrlKey: true, bubbles: true, cancelable: true }));
      return true; })()`);
    await wait("window.fixture.calls.at(-1) === 'A:tail:end'");
    await evaluate("window.fixture.select('B')");
    await wait("document.querySelectorAll('.timeline-message').length === 3 && document.body.innerText.includes('turn-2')");
    await evaluate("window.fixture.release()");
    await wait("document.querySelector('.history').dataset.windowReady === 'true'");
    assert.doesNotMatch((await evaluate("document.querySelector('.navigation-notice').textContent"))!, /window loaded/,
      "late A tail cannot follow or report success in B");
    await evaluate("window.fixture.select('A')");
    await wait("document.querySelectorAll('.timeline-message').length === 1000 && document.body.innerText.includes('latest user prompt')");
    await evaluate("window.fixture.holdNext(); document.querySelector('.load-more').click()");
    await wait("window.fixture.calls.at(-1).startsWith('A:2:')");
    assert.equal(await evaluate("document.querySelector('.history').dataset.windowReady"), "false", "pending read blocks keyboard navigation");
    await evaluate("window.fixture.select('B'); window.fixture.release()");
    await wait("document.querySelectorAll('.timeline-message').length === 3 && document.body.innerText.includes('turn-2')");
    assert.doesNotMatch((await snapshot())!, /latest user prompt/);
    await evaluate("window.fixture.select('C')");
    await wait("document.querySelectorAll('.timeline-message').length === 100 && document.body.innerText.includes('turn-99')");
    result = JSON.parse((await snapshot())!);
    assert.ok(result.calls.some(call => call === "C:2:20000"), "empty metadata-only page must continue");
    await evaluate(`(() => { document.querySelector('.keyboard-target').focus();
      document.activeElement.dispatchEvent(new KeyboardEvent('keydown', { key: 'F3', ctrlKey: true, bubbles: true, cancelable: true }));
      window.fixture.failCursor('history_changed');
      document.activeElement.dispatchEvent(new KeyboardEvent('keydown', { key: 'F4', ctrlKey: true, bubbles: true, cancelable: true }));
      return true; })()`);
    await wait("document.querySelector('.navigation-notice').textContent.includes('Newest history refresh failed')");
    assert.equal(JSON.parse((await snapshot())!).rows, 100, "revision failure after metadata page retains old visible rows");
    assert.equal(JSON.parse((await snapshot())!).following, "false");
    await evaluate(`document.activeElement.dispatchEvent(new KeyboardEvent('keydown', { key: 'F4', ctrlKey: true, bubbles: true, cancelable: true }))`);
    await wait("document.querySelector('.navigation-notice').textContent.includes('Newest persisted history window loaded')");
    assert.equal(JSON.parse((await snapshot())!).following, "true", "metadata-only first page is not premature success");
    await evaluate(`(() => { window.fixture.failNext('read_failed'); document.activeElement.dispatchEvent(new KeyboardEvent('keydown',
      { key: 'F4', ctrlKey: true, bubbles: true, cancelable: true })); return true; })()`);
    await wait("document.querySelector('.navigation-notice').textContent.includes('Newest history refresh failed')");
    assert.equal(JSON.parse((await snapshot())!).following, "true", "failed refresh also retains a preexisting follow preference");
    assert.equal(JSON.parse((await snapshot())!).rows, 100);
    await evaluate("document.activeElement.dispatchEvent(new KeyboardEvent('keydown', { key: 'F4', ctrlKey: true, bubbles: true, cancelable: true }))");
    await wait("document.querySelector('.history').dataset.windowReady === 'true' && document.querySelector('.navigation-notice').textContent.includes('window loaded')");
    await evaluate("document.activeElement.dispatchEvent(new KeyboardEvent('keydown', { key: 'F3', ctrlKey: true, bubbles: true, cancelable: true }))");
    await wait("document.querySelector('.timeline-scroll').dataset.following === 'false'");
    await evaluate(`(() => { window.fixture.holdNext(); document.activeElement.dispatchEvent(new KeyboardEvent('keydown',
      { key: 'F4', ctrlKey: true, bubbles: true, cancelable: true })); return true; })()`);
    await wait("window.fixture.calls.at(-1) === 'C:tail:end'");
    await evaluate("[...document.querySelectorAll('button')].find(b => b.textContent.includes('Refresh newest')).click()");
    await wait("document.querySelector('.history').dataset.windowReady === 'true' && document.querySelectorAll('.timeline-message').length === 100");
    await evaluate("window.fixture.release()");
    assert.equal(JSON.parse((await snapshot())!).following, "false", "manual Refresh supersedes keyboard follow without opting in");
    assert.doesNotMatch((await evaluate("document.querySelector('.navigation-notice').textContent"))!, /window loaded/);
    await evaluate("window.fixture.select('A')");
    await wait("document.querySelectorAll('.timeline-message').length === 1000 && document.body.innerText.includes('latest user prompt')");
    await evaluate("window.fixture.failNext('history_changed'); document.querySelector('.load-more').click()");
    await wait("document.querySelector('[role=alert]')?.textContent.includes('journal changed')");
    assert.equal(JSON.parse((await snapshot())!).rows, 0, "stale revision must clear accumulated history");
    assert.equal(await evaluate("document.querySelector('.history').dataset.windowReady"), "false");
    await evaluate("[...document.querySelectorAll('button')].find(b => b.textContent.includes('Refresh newest')).click()");
    await wait("document.querySelectorAll('.timeline-message').length === 1000 && document.body.innerText.includes('latest user prompt')");
    assert.equal(JSON.parse((await snapshot())!).following, "false", "manual Refresh newest keeps the reader's unfollow preference");
    await evaluate("window.fixture.failNext('read_failed'); document.querySelector('.load-more').click()");
    await wait("document.querySelector('[role=alert]')?.textContent.includes('could not be read')");
    assert.equal(JSON.parse((await snapshot())!).rows, 1000, "non-revision older failure retains known recent history");
    assert.equal(await evaluate("document.querySelector('.history').dataset.windowReady"), "false", "failed page is not navigable as a settled revision");
    await evaluate(`(() => { document.querySelector('.keyboard-target').focus();
      document.activeElement.dispatchEvent(new KeyboardEvent('keydown', { key: 'F3', ctrlKey: true, bubbles: true, cancelable: true }));
      return true; })()`);
    assert.equal(await evaluate("document.querySelector('.navigation-notice').textContent"), "",
      "failed reads cannot present an old-window navigation as settled");
    await evaluate("[...document.querySelectorAll('button')].find(b => b.textContent.includes('Refresh newest')).click()");
    await wait("document.querySelector('.history').dataset.windowReady === 'true' && document.querySelectorAll('.timeline-message').length === 1000");
    await evaluate("document.querySelector('.follow-target').click()");
    await evaluate(`(() => { const style = document.createElement('style'); style.textContent = '.timeline-message { height: 72px; }';
      document.head.append(style); return new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))); })()`);
    const grown = JSON.parse((await snapshot())!);
    assert.equal(grown.following, "true");
    assert.ok(Math.abs(grown.top - (grown.height - 260)) < 2, "following catches deferred row growth");
    await evaluate(`(() => { document.querySelector('.keyboard-target').focus();
      document.activeElement.dispatchEvent(new KeyboardEvent('keydown', { key: 'F3', bubbles: true, cancelable: true }));
      return true; })()`);
    await wait("document.querySelector('.timeline-scroll').dataset.following === 'false'");
    const pausedTop = JSON.parse((await snapshot())!).top;
    await evaluate(`(() => { const style = document.createElement('style'); style.textContent = '.timeline-message { height: 96px; }';
      document.head.append(style); return new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))); })()`);
    assert.equal(JSON.parse((await snapshot())!).top, pausedTop, "deferred layout cannot override keyboard unfollow");
    assert.equal(await evaluate("document.activeElement.className"), "keyboard-target");
  } finally {
    if (socket?.readyState === WebSocket.OPEN) socket.send(JSON.stringify({ id: 999, method: "Browser.close" }));
    socket?.close();
    if (browser?.pid) {
      const killer = spawn("taskkill", ["/F", "/T", "/PID", String(browser.pid)], { stdio: "ignore", windowsHide: true });
      await new Promise(resolve => killer.once("exit", resolve));
    }
    await rm(root, { recursive: true, force: true, maxRetries: 20, retryDelay: 100 });
  }
});
