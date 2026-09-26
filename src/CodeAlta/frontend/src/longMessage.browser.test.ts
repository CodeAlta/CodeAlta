import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { existsSync, readFileSync } from "node:fs";
import { mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { fileURLToPath, pathToFileURL } from "node:url";
import { build } from "esbuild";

const edge = ["C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe",
  "C:/Program Files/Microsoft/Edge/Application/msedge.exe"].find(existsSync);

test("mounted long persisted messages preserve copy, identity, follow and older anchor", { skip: !edge, timeout: 150_000 }, async () => {
  const source = readFileSync(fileURLToPath(new URL("./main.tsx", import.meta.url)), "utf8");
  assert.match(source, /read=\{workspace\.historyTail\}/);
  const root = await mkdtemp(join(tmpdir(), "codealta-long-message-"));
  let browser: ReturnType<typeof spawn> | undefined;
  let socket: WebSocket | undefined;
  try {
    await build({ entryPoints: [fileURLToPath(new URL("./toolDetail.mount.tsx", import.meta.url))],
      outfile: join(root, "fixture.js"), bundle: true, platform: "browser", format: "iife" });
    await writeFile(join(root, "style.css"), readFileSync(fileURLToPath(new URL("./style.css", import.meta.url))));
    const page = join(root, "fixture.html");
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
      socket!.addEventListener("error", () => reject(new Error("test browser unavailable")), { once: true });
    });
    let sequence = 0;
    const command = (method: string, params: object = {}) => new Promise<{ result?: { value?: unknown } }>((resolve, reject) => {
      const id = ++sequence;
      const timer = setTimeout(() => reject(new Error(`browser ${method} timed out`)), 12_000);
      const reply = (event: MessageEvent) => {
        const message = JSON.parse(String(event.data)) as { id?: number; result?: { result?: { value?: unknown } }; error?: object };
        if (message.id !== id) return;
        socket!.removeEventListener("message", reply); clearTimeout(timer);
        if (message.error) reject(new Error(`browser ${method} failed: ${JSON.stringify(message.error)}`)); else resolve(message.result ?? {});
      };
      socket!.addEventListener("message", reply);
      socket!.send(JSON.stringify({ id, method, params }));
    });
    await command("Page.enable");
    await command("Emulation.setFocusEmulationEnabled", { enabled: true });
    await command("Page.navigate", { url: pathToFileURL(page).href });
    const evaluate = async (expression: string) => (await command("Runtime.evaluate", { expression, returnByValue: true, awaitPromise: true })).result?.value;
    const wait = (condition: string) => evaluate(`new Promise(resolve=>{const end=Date.now()+10000;function check(){if(${condition})resolve(true);
      else if(Date.now()>end)resolve(document.body.innerText.slice(0,350));else setTimeout(check,30)}check()})`);
    const click = (selector: string) => evaluate(`document.querySelector(${JSON.stringify(selector)}).click()`);
    const pointer = async (selector: string) => {
      const bounds = await evaluate(`(() => { const target=document.querySelector(${JSON.stringify(selector)});
        const r=target.getBoundingClientRect(), x=r.left+r.width/2, y=r.top+r.height/2;
        return { x, y, hit:target===document.elementFromPoint(x,y),
          at:document.elementFromPoint(x,y)?.outerHTML.slice(0,110), top:document.querySelector('.timeline-scroll').scrollTop,
          following:document.querySelector('.timeline-scroll').dataset.following }; })()`) as {x:number;y:number;hit:boolean;at:string;top:number;following:string};
      assert.equal(bounds.hit, true, `pointer target obscured: ${selector} ${JSON.stringify(bounds)}`);
      await command("Input.dispatchMouseEvent", { type: "mousePressed", x: bounds.x, y: bounds.y, button: "left", clickCount: 1 });
      await command("Input.dispatchMouseEvent", { type: "mouseReleased", x: bounds.x, y: bounds.y, button: "left", clickCount: 1 });
    };
    const press = async (key: string, code: string, virtual: number) => {
      await command("Input.dispatchKeyEvent", { type: "keyDown", key, code, windowsVirtualKeyCode: virtual });
      await command("Input.dispatchKeyEvent", { type: "keyUp", key, code, windowsVirtualKeyCode: virtual });
    };
    const distance = "(s=>s.scrollHeight-s.clientHeight-s.scrollTop)(document.querySelector('.timeline-scroll'))";
    await command("Emulation.setDeviceMetricsOverride", { width: 390, height: 720, deviceScaleFactor: 1, mobile: false });
    assert.equal(await wait("document.querySelectorAll('.timeline-message').length===1000 && document.querySelector('.history').dataset.windowReady==='true'"), true);
    await evaluate("window.toolFixture.enableLong();document.querySelector('.history .section-heading button').click()");
    assert.equal(await wait("document.querySelectorAll('.long-message-toggle').length===2 && document.querySelector('.history').dataset.windowReady==='true'"), true);
    assert.equal(await evaluate("document.querySelectorAll('.message-assistant:not(:has(.long-message-toggle))').length>0"), true);
    assert.equal(await evaluate("document.querySelectorAll('.message-user').length===2 && document.querySelector('.message-user:not(:has(.long-message-toggle)) .markdown-content').textContent.includes('turn-1203')"), true);
    assert.equal(await evaluate("document.querySelector('.message-user .long-message-toggle').getAttribute('aria-expanded')"), "false");
    assert.equal(await evaluate("(() => {const button=document.querySelector('.message-user .long-message-toggle');return button.textContent.includes('Show full message') && !!document.getElementById(button.getAttribute('aria-controls'))})()"), true);
    assert.equal(await evaluate("!!document.querySelector('.message-user .markdown-content a')"), false, "collapsed links are not tabbable");
    assert.equal(await evaluate("!!document.querySelector('.message-user img')"), false, "raw excerpt is inert text");
    assert.equal(await evaluate("document.querySelector('.message-user').textContent.includes('Some details were shortened') && document.querySelector('.message-user').textContent.includes('omitted')"), true);
    await evaluate(`(() => {window.toolFixture.copied=[];Object.defineProperty(navigator,'clipboard',{configurable:true,value:{writeText:text=>{window.toolFixture.copied.push(text);return Promise.resolve()}}})})()`);
    await click(".message-user .copy-markdown");
    assert.equal(await wait("window.toolFixture.copied.length===1"), true);
    assert.match(String(await evaluate("window.toolFixture.copied[0]")), /FULL END A-1201$/);
    await evaluate("Object.defineProperty(navigator,'clipboard',{configurable:true,value:{writeText:()=>Promise.reject(Error('clipboard unavailable'))}})");
    await click(".message-user .copy-markdown");
    assert.equal(await wait("document.querySelector('.message-user .copy-markdown').getAttribute('aria-label')==='Copy failed'"), true);
    await evaluate("(() => {const s=document.querySelector('.timeline-scroll');s.dispatchEvent(new WheelEvent('wheel',{deltaY:-300,bubbles:true}));document.querySelector('.message-user .long-message-toggle').scrollIntoView({block:'center'});s.dispatchEvent(new Event('scroll',{bubbles:true}))})()");
    assert.equal(await wait("document.querySelector('.timeline-scroll').dataset.following==='false'"), true);
    await pointer(".message-user .long-message-toggle");
    assert.equal(await wait("document.querySelector('.message-user .long-message-toggle').getAttribute('aria-expanded')==='true'"), true);
    assert.equal(await evaluate("document.querySelector('.message-user .long-message-toggle').textContent.includes('Collapse message')"), true);
    assert.equal(await evaluate("document.querySelector('.message-user .markdown-content a').textContent"), "reference");
    assert.equal(await evaluate("document.querySelector('.message-user .markdown-content code').textContent.includes('const answer = 42')"), true);
    assert.equal(await evaluate("!window.toolFixture.injected && !document.querySelector('.message-user img[onerror]')"), true);
    await evaluate("window.toolFixture.rerender()");
    assert.equal(await evaluate("document.querySelector('.message-user .long-message-toggle').getAttribute('aria-expanded')"), "true");
    await evaluate("document.querySelector('.message-user .long-message-toggle').focus({preventScroll:true})");
    await press(" ", "Space", 32);
    assert.equal(await wait("document.querySelector('.message-user .long-message-toggle').getAttribute('aria-expanded')==='false'"), true);
    assert.equal(await evaluate("!!document.querySelector('.message-user .markdown-content a')"), false);
    assert.equal(await evaluate("document.activeElement.classList.contains('long-message-toggle')"), true);
    // Following observes height changes; an explicit reader scroll preserves position on subsequent disclosure.
    await evaluate("(() => {const s=document.querySelector('.timeline-scroll');s.scrollTop=s.scrollHeight;s.dispatchEvent(new Event('scroll',{bubbles:true}))})()");
    assert.equal(await wait(`${distance}<3 && document.querySelector('.timeline-scroll').dataset.following==='true'`), true);
    await click(".message-user .long-message-toggle");
    assert.equal(await wait(`${distance}<3 && document.querySelector('.timeline-scroll').dataset.following==='true'`), true);
    await evaluate("(() => {const s=document.querySelector('.timeline-scroll');s.dispatchEvent(new WheelEvent('wheel',{deltaY:-300,bubbles:true}));s.scrollTop=18000;s.dispatchEvent(new Event('scroll',{bubbles:true}))})()");
    assert.equal(await wait("document.querySelector('.timeline-scroll').dataset.following==='false'"), true);
    const top = Number(await evaluate("document.querySelector('.timeline-scroll').scrollTop"));
    await click(".message-user .long-message-toggle");
    await evaluate("new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(resolve)))");
    assert.equal(await evaluate("document.querySelector('.timeline-scroll').dataset.following"), "false");
    assert.ok(Math.abs(Number(await evaluate("document.querySelector('.timeline-scroll').scrollTop"))-top)<3);
    await click(".message-assistant .long-message-toggle");
    assert.equal(await evaluate("document.querySelector('.message-assistant .long-message-toggle').getAttribute('aria-expanded')"), "true");
    await evaluate("(() => {const s=document.querySelector('.timeline-scroll'),v=s.getBoundingClientRect();const row=[...s.querySelectorAll('.timeline-message')].find(r=>r.getBoundingClientRect().bottom>v.top);window.toolFixture.anchor=row;window.toolFixture.top=row.getBoundingClientRect().top;document.querySelector('.load-more').click()})()");
    assert.equal(await wait("document.querySelector('.history').dataset.windowReady==='true' && document.body.innerText.includes('Newer journal events are no longer')"), true);
    assert.equal(await evaluate("window.toolFixture.anchor.isConnected"), true);
    assert.ok(Math.abs(Number(await evaluate("window.toolFixture.anchor.getBoundingClientRect().top-window.toolFixture.top")))<3);
    assert.equal(await evaluate("document.querySelector('.timeline-scroll').dataset.following"), "false");
    assert.equal(await evaluate("!!document.querySelector('.message-user .long-message-toggle')"), false);
    assert.equal(await evaluate("document.querySelector('.message-assistant .long-message-toggle').getAttribute('aria-expanded')"), "true", "retained record keeps its disclosure on older page");
    await evaluate("window.toolFixture.select('B')");
    assert.equal(await wait("document.querySelectorAll('.timeline-message').length===1000 && document.querySelector('.history').dataset.windowReady==='true'"), true);
    assert.equal(await evaluate("document.querySelector('.message-assistant .long-message-toggle').getAttribute('aria-expanded')"), "false", "same offset in a new session cannot reuse disclosure state");
    await click(".message-assistant .long-message-toggle");
    assert.equal(await wait("document.querySelector('.message-assistant .long-message-toggle').getAttribute('aria-expanded')==='true'"), true);
    // Refresh through production History with the same session/revision/offset, not a test-only component key.
    const toggle = ".message-assistant .long-message-toggle";
    await evaluate("window.disclosureNode=document.querySelector('.message-assistant:has(.long-message-toggle)');window.disclosureToggle=window.disclosureNode.querySelector('.long-message-toggle');void 0");
    const refreshAssistant = async (replacement: object) => {
      await evaluate(`window.toolFixture.replaceAssistant(${JSON.stringify(replacement)});document.querySelector('.history .section-heading button').click()`);
      assert.equal(await wait("document.querySelector('.history').dataset.windowReady==='true'"), true);
      assert.equal(await evaluate("window.disclosureNode===document.querySelector('.message-assistant:has(.long-message-toggle)') || window.disclosureNode.isConnected && !window.disclosureNode.querySelector('.long-message-toggle')"), true,
        "same-key replacement retains the production row");
    };
    await refreshAssistant({});
    assert.equal(await evaluate("window.disclosureToggle.getAttribute('aria-expanded')"), "true", "equivalent allocated records retain intentional expansion");
    await refreshAssistant({ text: "Changed long body ".repeat(100) });
    assert.equal(await evaluate("window.disclosureToggle.getAttribute('aria-expanded')"), "false");
    await refreshAssistant({});
    assert.equal(await evaluate("window.disclosureToggle.getAttribute('aria-expanded')"), "false", "A to B to A must not revive A's expansion");
    for (const replacement of [{ text: "short body" }, { eventType: "contentDelta" },
      { providerId: "other-provider" }, { runId: "other-run" }, { timestamp: "2026-01-02T00:00:00Z" },
      { textTruncated: true }, { bodyOmitted: true }]) {
      await click(toggle);
      assert.equal(await evaluate("window.disclosureNode.querySelector('.long-message-toggle').getAttribute('aria-expanded')"), "true");
      await evaluate("window.disclosureNode.querySelector('.long-message-toggle').focus({preventScroll:true})");
      await refreshAssistant(replacement);
      if ("text" in replacement) {
        assert.equal(await evaluate("window.disclosureNode.querySelector('.long-message-toggle')===null && window.disclosureNode.querySelector('.markdown-content').textContent.trim()==='short body'"), true);
      } else {
        assert.equal(await evaluate("window.disclosureNode.querySelector('.long-message-toggle').getAttribute('aria-expanded')"), "false", JSON.stringify(replacement));
        assert.equal(await evaluate("document.activeElement===window.disclosureNode.querySelector('.long-message-toggle')"), true, "source invalidation preserves the stable toggle's focus");
        if ("textTruncated" in replacement) assert.equal(await evaluate("window.disclosureNode.textContent.includes('Some details were shortened')"), true);
        if ("bodyOmitted" in replacement) assert.equal(await evaluate("window.disclosureNode.textContent.includes('Additional message content was omitted')"), true);
      }
      await refreshAssistant({});
      assert.equal(await evaluate("window.disclosureNode.querySelector('.long-message-toggle').getAttribute('aria-expanded')"), "false", "intervening source semantics cannot revive expansion");
    }
    await click(toggle);
    // The B-700 row keeps its History key across a same-revision refresh, but its copy source changes.
    await evaluate(`(() => {
      const savedSet=window.setTimeout.bind(window),savedClear=window.clearTimeout.bind(window);
      window.toolFixture.copyResets=[];window.toolFixture.pendingCopies=[];
      window.toolFixture.restoreTimers=()=>{window.setTimeout=savedSet;window.clearTimeout=savedClear};
      window.setTimeout=(fn,ms,...args)=>{if(ms!==1600)return savedSet(fn,ms,...args);
        const id=savedSet(()=>{},60000);window.toolFixture.copyResets.push({id,fn,cleared:false});return id};
      window.clearTimeout=id=>{const reset=window.toolFixture.copyResets.find(v=>v.id===id);
        if(reset)reset.cleared=true;savedClear(id)};
      Object.defineProperty(navigator,'clipboard',{configurable:true,value:{writeText:text=>new Promise((resolve,reject)=>{
        window.toolFixture.pendingCopies.push({text,resolve,reject})})}});
      window.toolFixture.copyNode=document.querySelector('.message-assistant:has(.long-message-toggle) .copy-markdown');
    })()`);
    const copyButton = ".message-assistant:has(.long-message-toggle) .copy-markdown";
    await click(copyButton);
    assert.equal(await wait("window.toolFixture.pendingCopies.length===1"), true);
    assert.match(String(await evaluate("window.toolFixture.pendingCopies[0].text")), /B-700$/);
    await evaluate("window.toolFixture.changeBody();document.querySelector('.history .section-heading button').click()");
    assert.equal(await wait("document.querySelector('.history').dataset.windowReady==='true' && document.querySelector('.message-assistant .long-message-toggle').getAttribute('aria-expanded')==='false'"), true,
      "a changed body at the same journal offset cannot inherit disclosure");
    assert.equal(await evaluate(`window.toolFixture.copyNode===document.querySelector(${JSON.stringify(copyButton)})`), true,
      "the production History owner reuses the row at the same key and revision");
    await click(copyButton);
    assert.equal(await wait("window.toolFixture.pendingCopies.length===2"), true);
    assert.match(String(await evaluate("window.toolFixture.pendingCopies[1].text")), /changed B-700$/);
    await evaluate("window.toolFixture.pendingCopies[1].resolve()");
    assert.equal(await wait("window.toolFixture.copyResets.length===1 && window.toolFixture.copyNode.getAttribute('aria-label')==='Copied'"), true);
    await evaluate("window.toolFixture.pendingCopies[0].reject(Error('stale private clipboard failure'))");
    await evaluate("new Promise(resolve=>setTimeout(resolve,0))");
    assert.equal(await evaluate("window.toolFixture.copyNode.getAttribute('aria-label')"), "Copied",
      "a stale pre-refresh failure cannot replace newer feedback");
    await click(copyButton);
    assert.equal(await wait("window.toolFixture.pendingCopies.length===3"), true);
    await evaluate("window.toolFixture.pendingCopies[2].resolve()");
    assert.equal(await wait("window.toolFixture.copyResets.length===2 && window.toolFixture.copyNode.getAttribute('aria-label')==='Copied'"), true);
    assert.equal(await evaluate("window.toolFixture.copyResets[0].cleared"), true);
    await evaluate("window.toolFixture.copyResets[0].fn()"); // Deliver a queued callback despite clearTimeout.
    assert.equal(await evaluate("window.toolFixture.copyNode.getAttribute('aria-label')"), "Copied",
      "a canceled older reset cannot clear newer feedback");
    await evaluate("window.toolFixture.select('A')");
    assert.equal(await wait("document.querySelector('.history').dataset.windowReady==='true' && document.querySelector('.message-user .long-message-toggle')"), true);
    assert.equal(await evaluate("window.toolFixture.copyResets[1].cleared"), true, "unmount clears its live timer");
    await evaluate("window.toolFixture.select('B')");
    assert.equal(await wait("document.querySelector('.history').dataset.windowReady==='true' && document.querySelector('.message-assistant .long-message-toggle')"), true);
    assert.equal(await evaluate("document.querySelector('.message-assistant:has(.long-message-toggle) .copy-markdown').getAttribute('aria-label')"), "Copy CodeAlta as Markdown");
    await click(copyButton);
    assert.equal(await wait("window.toolFixture.pendingCopies.length===4"), true);
    await evaluate("window.toolFixture.select('A')");
    assert.equal(await wait("document.querySelector('.history').dataset.windowReady==='true' && document.querySelector('.message-user .long-message-toggle')"), true);
    await evaluate("window.toolFixture.select('B')");
    assert.equal(await wait("document.querySelector('.history').dataset.windowReady==='true' && document.querySelector('.message-assistant .long-message-toggle')"), true);
    await evaluate("window.toolFixture.pendingCopies[3].resolve();window.toolFixture.copyResets[1].fn()");
    await evaluate("new Promise(resolve=>setTimeout(resolve,0))");
    assert.equal(await evaluate("document.querySelector('.message-assistant:has(.long-message-toggle) .copy-markdown').getAttribute('aria-label')"),
      "Copy CodeAlta as Markdown", "an unmounted callback cannot publish on the remounted source");
    await evaluate("window.toolFixture.restoreTimers()");
    await evaluate("Object.defineProperty(navigator,'clipboard',{configurable:true,value:{writeText:text=>{window.toolFixture.copied.push(text);return Promise.resolve()}}})");
    await click(".message-assistant:has(.long-message-toggle) .copy-markdown");
    assert.equal(await wait("window.toolFixture.copied.length===2"), true);
    assert.match(String(await evaluate("window.toolFixture.copied[1]")), /changed B-700$/);
    for (const width of [390, 1120]) for (const theme of ["light", "dark"]) {
      await command("Emulation.setDeviceMetricsOverride", { width, height: 720, deviceScaleFactor: 1, mobile: false });
      await evaluate(`document.documentElement.dataset.theme=${JSON.stringify(theme)}`);
      const layout = await evaluate("({outer:document.documentElement.scrollWidth,button:document.querySelector('.long-message-toggle').getBoundingClientRect().right,preview:document.querySelector('.long-message-preview').getBoundingClientRect().right})") as {outer:number;button:number;preview:number};
      assert.ok(layout.outer<=width+2 && layout.button<=width+2 && layout.preview<=width+2, `${width}/${theme}: ${JSON.stringify(layout)}`);
    }
  } finally {
    socket?.close(); browser?.kill(); await rm(root, { recursive: true, force: true, maxRetries: 8, retryDelay: 100 });
  }
});
