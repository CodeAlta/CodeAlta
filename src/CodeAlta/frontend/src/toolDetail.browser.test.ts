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

test("mounted persisted tool details wrap without changing follow, older anchor or explicit newest handshake", { skip: !edge, timeout: 70_000 }, async () => {
  const source = readFileSync(fileURLToPath(new URL("./main.tsx", import.meta.url)), "utf8");
  assert.match(source, /read=\{workspace\.historyTail\}/);
  const root = await mkdtemp(join(tmpdir(), "codealta-tool-detail-"));
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
        if (message.error) reject(new Error(`browser ${method} failed`)); else resolve(message.result ?? {});
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
    const latest = ".timeline-message:last-child";
    const details = `${latest} .event-details`;
    const pre = `${details} pre`;
    await command("Emulation.setDeviceMetricsOverride", { width: 390, height: 720, deviceScaleFactor: 1, mobile: false });
    assert.equal(await wait("document.querySelectorAll('.timeline-message').length===1000 && document.querySelector('.history').dataset.windowReady==='true'"), true);
    assert.equal(await evaluate("document.querySelector('.timeline-message').textContent.includes('turn-205')"), true);
    assert.equal(await evaluate(`document.querySelector('${details}').open`), false);
    assert.equal(await evaluate("document.querySelectorAll('.tool-detail-wrap').length"), 1);
    assert.equal(await evaluate("document.querySelector('.timeline-message:last-child').textContent.includes('Some details were shortened')"), true);
    assert.equal(await evaluate("document.querySelector('.timeline-message:last-child .markdown-content code').textContent"), "literal code stays as Markdown");
    assert.equal(await wait(`${distance}<3`), true);
    await evaluate(`(() => { window.toolFixture.writes=0;window.toolFixture.network=0;window.toolFixture.copied=[];
      Storage.prototype.setItem=()=>window.toolFixture.writes++;window.fetch=()=>{window.toolFixture.network++;throw Error('network forbidden')};
      Object.defineProperty(navigator,'clipboard',{configurable:true,value:{writeText:(v)=>{window.toolFixture.copied.push(v);return Promise.resolve()}}}); })()`);
    await click(`${details} summary`);
    assert.equal(await wait(`${distance}<3 && document.querySelector('.timeline-scroll').dataset.following==='true'`), true,
      String(await evaluate(`JSON.stringify({distance:${distance}, following:document.querySelector('.timeline-scroll').dataset.following,
        focus:document.activeElement?.tagName})`)));
    assert.equal(await evaluate(`getComputedStyle(document.querySelector('${pre}')).whiteSpace`), "pre-wrap");
    assert.equal(await evaluate(`!!document.querySelector('${pre} img') || !!document.querySelector('${pre} script')`), false);
    await click(`${latest} .copy-markdown`);
    assert.equal(await wait("window.toolFixture.copied.length===1"), true);
    const originalCopy = await evaluate("window.toolFixture.copied[0]");
    assert.equal(String(originalCopy).includes("<img src=x onerror=alert(1)>"), true);
    for (const width of [390, 1120]) {
      await command("Emulation.setDeviceMetricsOverride", { width, height: 720, deviceScaleFactor: 1, mobile: false });
      const backgrounds: string[] = [];
      for (const theme of ["dark", "light"]) {
        await evaluate(`document.documentElement.dataset.theme=${JSON.stringify(theme)}`);
        const wrapped = await evaluate(`({right:document.querySelector('${pre}').getBoundingClientRect().right,
          scroll:document.querySelector('${pre}').scrollWidth,client:document.querySelector('${pre}').clientWidth,
          outer:document.documentElement.scrollWidth, background:getComputedStyle(document.querySelector('${pre}')).backgroundColor})`) as {right:number;scroll:number;client:number;outer:number;background:string};
        assert.ok(wrapped.right<=width+2 && wrapped.scroll<=wrapped.client+1 && wrapped.outer<=width+2, `${width}/${theme} wrapped ${JSON.stringify(wrapped)}`);
        backgrounds.push(wrapped.background);
        await evaluate(`document.querySelector('${details} input').focus()`);
        await press(" ", "Space", 32);
        assert.equal(await wait(`!document.querySelector('${details} input').checked`), true);
        const unwrapped = await evaluate(`({right:document.querySelector('${pre}').getBoundingClientRect().right,
          scroll:document.querySelector('${pre}').scrollWidth,client:document.querySelector('${pre}').clientWidth,
          outer:document.documentElement.scrollWidth})`) as {right:number;scroll:number;client:number;outer:number};
        assert.ok(unwrapped.right<=width+2 && unwrapped.scroll>unwrapped.client && unwrapped.outer<=width+2,
          `${width}/${theme} unwrapped ${JSON.stringify(unwrapped)}`);
        await click(`${details} input`);
        assert.equal(await wait(`${distance}<3 && document.querySelector('.timeline-scroll').dataset.following==='true'`), true,
          String(await evaluate(`JSON.stringify({distance:${distance},following:document.querySelector('.timeline-scroll').dataset.following,
            checked:document.querySelector('${details} input').checked,focus:document.activeElement?.outerHTML.slice(0,100)})`)));
      }
      assert.notEqual(backgrounds[0], backgrounds[1]);
    }
    await command("Emulation.setDeviceMetricsOverride", { width: 390, height: 720, deviceScaleFactor: 1, mobile: false });
    await click(`${details} summary`);
    assert.equal(await wait(`!document.querySelector('${details}').open && ${distance}<3 && document.querySelector('.timeline-scroll').dataset.following==='true'`), true);
    await click(`${details} summary`);
    assert.equal(await wait(`document.querySelector('${details}').open && ${distance}<3 && document.querySelector('.timeline-scroll').dataset.following==='true'`), true,
      String(await evaluate(`JSON.stringify({open:document.querySelector('${details}').open,distance:${distance},
        following:document.querySelector('.timeline-scroll').dataset.following,focus:document.activeElement?.tagName})`)));
    await evaluate("window.toolFixture.grow(180)");
    assert.equal(await wait(`${distance}<3 && document.querySelector('.timeline-scroll').dataset.following==='true'`), true,
      String(await evaluate(`JSON.stringify({distance:${distance}, following:document.querySelector('.timeline-scroll').dataset.following,
        focus:document.activeElement?.tagName})`)));
    await click(`${latest} .copy-markdown`);
    assert.equal(await wait("window.toolFixture.copied.length===2"), true);
    assert.equal(await evaluate("window.toolFixture.copied[1]"), originalCopy);
    await evaluate(`(() => { const s=document.querySelector('.timeline-scroll'),p=document.querySelector('${pre}');
      p.scrollTop=40;p.dispatchEvent(new WheelEvent('wheel',{deltaY:-600,bubbles:true}));
      document.querySelector('.deferred-layout').style.height='220px';s.dispatchEvent(new Event('scroll',{bubbles:true})); })()`);
    assert.equal(await wait(`${distance}<3 && document.querySelector('.timeline-scroll').dataset.following==='true'`), true);
    await evaluate("(() => { const s=document.querySelector('.timeline-scroll');document.querySelector('.deferred-layout').style.height='400px';s.dispatchEvent(new WheelEvent('wheel',{deltaY:100,bubbles:true}));s.scrollTop+=20;s.dispatchEvent(new Event('scroll',{bubbles:true})); })()");
    assert.equal(await wait(`${distance}<3 && document.querySelector('.timeline-scroll').dataset.following==='true'`), true);
    await evaluate(`(() => {const s=document.querySelector('.timeline-scroll'),input=document.querySelector('${details} input');
      input.dispatchEvent(new PointerEvent('pointerdown',{bubbles:true,pointerType:'mouse',pointerId:10}));
      input.dispatchEvent(new PointerEvent('pointerup',{bubbles:true,pointerType:'mouse',pointerId:10}));
      input.dispatchEvent(new PointerEvent('pointerdown',{bubbles:true,pointerType:'touch',pointerId:9,clientY:100}));
      input.dispatchEvent(new PointerEvent('pointerup',{bubbles:true,pointerType:'touch',pointerId:9,clientY:100}));
      document.querySelector('.deferred-layout').style.height='460px';s.dispatchEvent(new Event('scroll',{bubbles:true})); })()`);
    assert.equal(await wait(`${distance}<3 && document.querySelector('.timeline-scroll').dataset.following==='true'`), true);
    await evaluate(`new Promise(resolve => { const s=document.querySelector('.timeline-scroll');
      document.querySelector('.deferred-layout').style.height='500px';requestAnimationFrame(()=>{
        s.scrollTop=s.scrollHeight;s.dispatchEvent(new Event('scroll',{bubbles:true}));requestAnimationFrame(()=>{
          s.scrollTop=s.scrollHeight-s.clientHeight-410;s.dispatchEvent(new Event('scroll',{bubbles:true}));resolve(null);
        });
      });
    })`);
    assert.equal(await wait(`${distance}<3 && document.querySelector('.timeline-scroll').dataset.following==='true'`), true);
    // A reader scroll can arrive after layout growth but before ResizeObserver settles it.
    // Explicit wheel intent must win over following in that ordering.
    await evaluate("(() => { const s=document.querySelector('.timeline-scroll'); document.querySelector('.deferred-layout').style.height='540px'; s.dispatchEvent(new WheelEvent('wheel',{deltaY:-600,bubbles:true})); s.scrollTop=600;s.dispatchEvent(new Event('scroll',{bubbles:true})); })()");
    assert.equal(await wait("document.querySelector('.timeline-scroll').dataset.following==='false'"), true);
    const unfollowed = Number(await evaluate("document.querySelector('.timeline-scroll').scrollTop"));
    await click(`${details} input`);
    await evaluate("window.toolFixture.grow(360)");
    await evaluate("new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(resolve)))");
    assert.equal(await evaluate("document.querySelector('.timeline-scroll').dataset.following"), "false");
    assert.ok(Math.abs(Number(await evaluate("document.querySelector('.timeline-scroll').scrollTop"))-unfollowed)<3);
    await evaluate("window.toolFixture.grow(0)");
    await evaluate("new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(resolve)))");
    assert.equal(await evaluate("document.querySelector('.timeline-scroll').dataset.following"), "false");
    assert.ok(Math.abs(Number(await evaluate("document.querySelector('.timeline-scroll').scrollTop"))-unfollowed)<3);
    await evaluate(`(() => { const s=document.querySelector('.timeline-scroll'),v=s.getBoundingClientRect();
      const row=[...s.querySelectorAll('.timeline-message')].find(r=>r.getBoundingClientRect().bottom>v.top);
      window.toolFixture.anchor=row;window.toolFixture.anchorTop=row.getBoundingClientRect().top;
      document.querySelector('.load-more').click(); })()`);
    assert.equal(await wait("document.querySelector('.history').dataset.windowReady==='true' && document.body.innerText.includes('Newer journal events are no longer')"), true);
    assert.equal(await evaluate("document.querySelectorAll('.timeline-message').length"), 1000);
    assert.equal(await evaluate("document.querySelector('.timeline-message').textContent.includes('turn-105')"), true);
    const anchored = await evaluate("({connected:window.toolFixture.anchor.isConnected, delta:window.toolFixture.anchor.getBoundingClientRect().top-window.toolFixture.anchorTop})") as {connected:boolean;delta:number};
    assert.equal(anchored.connected, true);
    assert.ok(Math.abs(anchored.delta)<3, `older window moved reader: ${JSON.stringify(anchored)}`);
    assert.equal(await evaluate("document.querySelector('.timeline-scroll').dataset.following"), "false");
    await evaluate("window.toolFixture.hold();document.querySelector('.keyboard-target').focus();document.activeElement.dispatchEvent(new KeyboardEvent('keydown',{key:'F4',ctrlKey:true,bubbles:true,cancelable:true}))");
    assert.equal(await wait("document.querySelector('.navigation-notice').textContent.includes('Refreshing')"), true);
    assert.equal(await evaluate("document.querySelector('.timeline-scroll').dataset.following"), "false");
    await evaluate("window.toolFixture.release()");
    assert.equal(await wait("document.querySelector('.navigation-notice').textContent.includes('following visible content') && document.querySelector('.history').dataset.windowReady==='true'"), true);
    assert.equal(await wait(`${distance}<3 && document.querySelector('.timeline-scroll').dataset.following==='true'`), true);
    await evaluate("(() => { const s=document.querySelector('.timeline-scroll');s.tabIndex=0;s.focus({preventScroll:true}); document.querySelector('.deferred-layout').style.height='720px';s.dispatchEvent(new KeyboardEvent('keydown',{key:'PageUp',bubbles:true}));s.scrollTop=600;s.dispatchEvent(new Event('scroll',{bubbles:true})); })()");
    assert.equal(await wait("document.querySelector('.timeline-scroll').dataset.following==='false'"), true);
    await evaluate("(() => {const s=document.querySelector('.timeline-scroll');s.scrollTop=s.scrollHeight;s.dispatchEvent(new Event('scroll',{bubbles:true}))})()");
    assert.equal(await wait(`${distance}<3 && document.querySelector('.timeline-scroll').dataset.following==='true'`), true);
    await evaluate("document.querySelector('.timeline-scroll').focus({preventScroll:true})");
    await press("PageUp", "PageUp", 33);
    assert.equal(await wait("document.querySelector('.timeline-scroll').dataset.following==='false'"), true);
    await evaluate("(() => {const s=document.querySelector('.timeline-scroll');s.scrollTop=s.scrollHeight;s.dispatchEvent(new Event('scroll',{bubbles:true}))})()");
    assert.equal(await wait(`${distance}<3 && document.querySelector('.timeline-scroll').dataset.following==='true'`), true);
    await evaluate("(() => { const s=document.querySelector('.timeline-scroll'); document.querySelector('.deferred-layout').style.height='790px'; const x=s.getBoundingClientRect().left+s.clientWidth+2; s.dispatchEvent(new PointerEvent('pointerdown',{bubbles:true,pointerType:'mouse',pointerId:7,clientX:x}));s.scrollTop=600;s.dispatchEvent(new Event('scroll',{bubbles:true}));s.dispatchEvent(new PointerEvent('pointerup',{bubbles:true,pointerType:'mouse',pointerId:7})); })()");
    assert.equal(await wait("document.querySelector('.timeline-scroll').dataset.following==='false'"), true);
    await evaluate("(() => {const s=document.querySelector('.timeline-scroll');s.scrollTop=s.scrollHeight;s.dispatchEvent(new Event('scroll',{bubbles:true}))})()");
    assert.equal(await wait(`${distance}<3 && document.querySelector('.timeline-scroll').dataset.following==='true'`), true);
    await evaluate("(() => { const s=document.querySelector('.timeline-scroll');document.querySelector('.deferred-layout').style.height='860px';s.dispatchEvent(new PointerEvent('pointerdown',{bubbles:true,pointerType:'touch',pointerId:8,clientY:200}));s.dispatchEvent(new PointerEvent('pointermove',{bubbles:true,pointerType:'touch',pointerId:8,clientY:300}));s.scrollTop=600;s.dispatchEvent(new Event('scroll',{bubbles:true}));s.dispatchEvent(new PointerEvent('pointerup',{bubbles:true,pointerType:'touch',pointerId:8})); })()");
    assert.equal(await wait("document.querySelector('.timeline-scroll').dataset.following==='false'"), true);
    await evaluate("(() => {const s=document.querySelector('.timeline-scroll');s.scrollTop=s.scrollHeight;s.dispatchEvent(new Event('scroll',{bubbles:true}))})()");
    assert.equal(await wait(`${distance}<3 && document.querySelector('.timeline-scroll').dataset.following==='true'`), true);
    await evaluate("document.querySelector('.timeline-scroll').focus({preventScroll:true})");
    await press("PageUp", "PageUp", 33);
    assert.equal(await wait("document.querySelector('.timeline-scroll').dataset.following==='false'"), true);
    await evaluate("window.toolFixture.hold();document.querySelector('.keyboard-target').focus();document.activeElement.dispatchEvent(new KeyboardEvent('keydown',{key:'F4',ctrlKey:true,bubbles:true,cancelable:true}))");
    assert.equal(await wait("document.querySelector('.navigation-notice').textContent.includes('Refreshing')"), true);
    await evaluate("window.toolFixture.select('B');window.toolFixture.release()");
    assert.equal(await wait("document.querySelectorAll('.timeline-message').length===3 && document.querySelector('.history').dataset.windowReady==='true'"), true);
    assert.equal(await evaluate("document.querySelector('.navigation-notice').textContent.includes('following visible content')"), false);
    assert.equal(await evaluate("document.querySelector('.timeline-message:last-child').textContent.includes('turn-2')"), true);
    assert.equal(await evaluate("!!document.querySelector('.timeline-message:last-child .tool-detail-wrap')"), false);
    assert.equal(await evaluate("document.querySelectorAll('.timeline-message')[1].textContent.includes('Additional diagnostic details were omitted')"), true);
    assert.equal(await evaluate("!!document.querySelectorAll('.timeline-message')[1].querySelector('.tool-detail-wrap')"), false);
    assert.equal(await wait(`${distance}<3 && document.querySelector('.timeline-scroll').dataset.following==='true'`), true);
    await evaluate("new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(resolve)))");
    await evaluate("document.querySelector('.timeline-scroll').addEventListener('wheel',e=>window.toolFixture.lastWheel={y:e.deltaY,top:e.currentTarget.scrollTop,target:e.target?.tagName})");
    const viewport = await evaluate("(() => { const s=document.querySelector('.timeline-scroll'),r=s.getBoundingClientRect(); return {x:r.left+s.clientWidth-12,y:r.top+s.clientHeight/2}; })()") as {x:number;y:number};
    await command("Input.dispatchMouseEvent", { type: "mouseWheel", ...viewport, deltaX: 0, deltaY: -600 });
    assert.equal(await wait("document.querySelector('.timeline-scroll').dataset.following==='false' && document.querySelector('.timeline-scroll').scrollTop===0"), true,
      String(await evaluate("JSON.stringify((s=>({top:s.scrollTop,height:s.scrollHeight,client:s.clientHeight,following:s.dataset.following,wheel:window.toolFixture.lastWheel}))(document.querySelector('.timeline-scroll')))")+JSON.stringify(viewport)));
    await pointer(".timeline-message:first-child .event-details summary");
    assert.equal(await wait("document.querySelector('.timeline-message:first-child .event-details').open"), true);
    assert.equal(await evaluate("document.querySelector('.timeline-scroll').dataset.following"), "false");
    await evaluate("document.querySelector('.timeline-scroll').scrollTop=0");
    await pointer(".timeline-message:first-child .event-details summary");
    assert.equal(await wait("!document.querySelector('.timeline-message:first-child .event-details').open"), true);
    await evaluate("document.querySelector('.timeline-message:first-child .event-details summary').focus({preventScroll:true})");
    await press(" ", "Space", 32);
    assert.equal(await wait("document.querySelector('.timeline-message:first-child .event-details').open"), true);
    await evaluate("document.querySelector('.timeline-message:first-child .tool-detail-wrap input').scrollIntoView({block:'nearest'})");
    await pointer(".timeline-message:first-child .tool-detail-wrap input");
    assert.equal(await wait("!document.querySelector('.timeline-message:first-child .tool-detail-wrap input').checked"), true);
    await evaluate("document.querySelector('.timeline-message:first-child .tool-detail-wrap input').scrollIntoView({block:'nearest'})");
    await pointer(".timeline-message:first-child .tool-detail-wrap input");
    assert.equal(await wait("document.querySelector('.timeline-message:first-child .tool-detail-wrap input').checked"), true);
    assert.equal(await evaluate("window.toolFixture.writes"), 0);
    assert.equal(await evaluate("window.toolFixture.network"), 0);
    assert.ok((await evaluate("window.toolFixture.calls.length") as number) <= 27);
  } finally {
    socket?.close(); browser?.kill(); await rm(root, { recursive: true, force: true, maxRetries: 8, retryDelay: 100 });
  }
});
