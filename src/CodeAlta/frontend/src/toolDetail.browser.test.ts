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

test("mounted persisted details and code retain access, follow, older anchors and explicit newest handshake", { skip: !edge, timeout: 110_000 }, async () => {
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
    // A still-held scrollbar gesture must outlive the 500 ms one-shot intent timeout.
    await evaluate("(async () => { const s=document.querySelector('.timeline-scroll'); const x=s.getBoundingClientRect().left+s.clientWidth+2; s.dispatchEvent(new PointerEvent('pointerdown',{bubbles:true,pointerType:'mouse',pointerId:7,clientX:x,buttons:1})); await new Promise(resolve=>setTimeout(resolve,550)); document.querySelector('.deferred-layout').style.height='790px'; s.dispatchEvent(new PointerEvent('pointermove',{bubbles:true,pointerType:'mouse',pointerId:7,clientX:x,buttons:1}));s.scrollTop=600;s.dispatchEvent(new Event('scroll',{bubbles:true}));s.dispatchEvent(new PointerEvent('pointerup',{bubbles:true,pointerType:'mouse',pointerId:7})); })()");
    assert.equal(await wait("document.querySelector('.timeline-scroll').dataset.following==='false'"), true);
    await evaluate("(() => {const s=document.querySelector('.timeline-scroll');s.scrollTop=s.scrollHeight;s.dispatchEvent(new Event('scroll',{bubbles:true}))})()");
    assert.equal(await wait(`${distance}<3 && document.querySelector('.timeline-scroll').dataset.following==='true'`), true);
    // The first scroll consumes its intent. Returning to the tail while still held must
    // not prevent the next movement from opting out when geometry grows again.
    await evaluate(`(() => {const s=document.querySelector('.timeline-scroll'),x=s.getBoundingClientRect().left+s.clientWidth+2;
      s.dispatchEvent(new PointerEvent('pointerdown',{bubbles:true,pointerType:'mouse',pointerId:11,clientX:x,buttons:1}));
      s.dispatchEvent(new PointerEvent('pointermove',{bubbles:true,pointerType:'mouse',pointerId:11,buttons:1}));
      s.scrollTop=600;s.dispatchEvent(new Event('scroll',{bubbles:true}));})()`);
    assert.equal(await wait("document.querySelector('.timeline-scroll').dataset.following==='false'"), true);
    await evaluate("(() => {const s=document.querySelector('.timeline-scroll');s.scrollTop=s.scrollHeight;s.dispatchEvent(new Event('scroll',{bubbles:true}))})()");
    assert.equal(await wait(`${distance}<3 && document.querySelector('.timeline-scroll').dataset.following==='true'`), true);
    await evaluate(`(() => {const s=document.querySelector('.timeline-scroll');document.querySelector('.deferred-layout').style.height='820px';
      window.dispatchEvent(new PointerEvent('pointermove',{bubbles:true,pointerType:'mouse',pointerId:11,buttons:1}));
      s.scrollTop=600;s.dispatchEvent(new Event('scroll',{bubbles:true}));
      window.dispatchEvent(new PointerEvent('pointerup',{pointerType:'mouse',pointerId:11}));})()`);
    assert.equal(await wait("document.querySelector('.timeline-scroll').dataset.following==='false'"), true);
    await evaluate("(() => {const s=document.querySelector('.timeline-scroll');s.scrollTop=s.scrollHeight;s.dispatchEvent(new Event('scroll',{bubbles:true}))})()");
    assert.equal(await wait(`${distance}<3 && document.querySelector('.timeline-scroll').dataset.following==='true'`), true);
    // A released/canceled/lost drag cannot lend an unconsumed intent to later layout-only scrolls.
    for (const [index, end] of ["pointerup", "pointercancel", "blur", "lost-buttons"].entries()) {
      await evaluate(`(() => {const s=document.querySelector('.timeline-scroll'),x=s.getBoundingClientRect().left+s.clientWidth+2;
        s.dispatchEvent(new PointerEvent('pointerdown',{bubbles:true,pointerType:'mouse',pointerId:12,clientX:x,buttons:1}));
        window.dispatchEvent(new PointerEvent('pointermove',{pointerType:'mouse',pointerId:12,buttons:1}));
        ${end === "blur" ? "window.dispatchEvent(new Event('blur'));" :
          `window.dispatchEvent(new PointerEvent('${end === "lost-buttons" ? "pointermove" : end}',{pointerType:'mouse',pointerId:12,buttons:0}));`}
        document.querySelector('.deferred-layout').style.height='${830 + index * 10}px';
        s.scrollTop=600;s.dispatchEvent(new Event('scroll',{bubbles:true}));})()`);
      assert.equal(await wait(`${distance}<3 && document.querySelector('.timeline-scroll').dataset.following==='true'`), true, end);
    }
    await evaluate("(() => { const s=document.querySelector('.timeline-scroll');document.querySelector('.deferred-layout').style.height='860px';s.dispatchEvent(new PointerEvent('pointerdown',{bubbles:true,pointerType:'touch',pointerId:8,clientY:200}));s.dispatchEvent(new PointerEvent('pointermove',{bubbles:true,pointerType:'touch',pointerId:8,clientY:300}));s.scrollTop=600;s.dispatchEvent(new Event('scroll',{bubbles:true}));s.dispatchEvent(new PointerEvent('pointerup',{bubbles:true,pointerType:'touch',pointerId:8})); })()");
    assert.equal(await wait("document.querySelector('.timeline-scroll').dataset.following==='false'"), true);
    await evaluate("(() => {const s=document.querySelector('.timeline-scroll');s.scrollTop=s.scrollHeight;s.dispatchEvent(new Event('scroll',{bubbles:true}))})()");
    assert.equal(await wait(`${distance}<3 && document.querySelector('.timeline-scroll').dataset.following==='true'`), true);
    await evaluate("document.querySelector('.timeline-scroll').focus({preventScroll:true})");
    await press("PageUp", "PageUp", 33);
    assert.equal(await wait("document.querySelector('.timeline-scroll').dataset.following==='false'"), true);
    await evaluate("(() => {const s=document.querySelector('.timeline-scroll'),x=s.getBoundingClientRect().left+s.clientWidth+2;s.dispatchEvent(new PointerEvent('pointerdown',{bubbles:true,pointerType:'mouse',pointerId:13,clientX:x,buttons:1}));})()");
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
    await evaluate("(() => {const s=document.querySelector('.timeline-scroll');window.dispatchEvent(new PointerEvent('pointermove',{pointerType:'mouse',pointerId:13,buttons:1}));document.querySelector('.deferred-layout').style.height='120px';s.scrollTop=0;s.dispatchEvent(new Event('scroll',{bubbles:true}));})()");
    assert.equal(await wait(`${distance}<3 && document.querySelector('.timeline-scroll').dataset.following==='true'`), true);
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
    await evaluate("window.toolFixture.select('C')");
    assert.equal(await wait("document.querySelector('.history').dataset.windowReady==='true' && document.querySelectorAll('.history .timeline-message').length===4"), true);
    const codeReads = await evaluate("window.toolFixture.calls.length");
    await click(".history .long-message-toggle");
    const code = ".history .message-assistant:has(.long-message-toggle) .markdown-content pre";
    const metrics = await evaluate(`(() => { const p=document.querySelector('${code}'),s=getComputedStyle(p);return {height:p.getBoundingClientRect().height,
      bound:14*parseFloat(s.lineHeight)+parseFloat(s.paddingTop)+parseFloat(s.paddingBottom)+2,
      tab:p.tabIndex,label:p.getAttribute('aria-label'),text:p.textContent};})()`) as {height:number;bound:number;tab:number;label:string;text:string};
    assert.ok(metrics.height <= metrics.bound + 1, `long persisted code must be capped: ${JSON.stringify(metrics)}`);
    assert.equal(metrics.tab, 0, "code has explicit keyboard access");
    assert.ok(metrics.label);
    assert.equal(metrics.text.trimEnd(), await evaluate("window.toolFixture.codeText"));
    assert.equal(await evaluate("document.querySelectorAll('.history .message-assistant:has(.long-message-toggle) pre.timeline-code').length"), 2, "fenced and indented code are both accessible");
    assert.equal(await evaluate("window.codeInjected===undefined && !document.querySelector('.history .markdown-content script')"), true);
    assert.equal(await evaluate("document.querySelectorAll('.unchanged-markdown pre.timeline-code').length"), 0, "shared default and live Markdown remain unchanged");
    assert.equal(await evaluate("[...document.querySelectorAll('.unchanged-markdown pre')].every(p=>getComputedStyle(p).maxHeight==='none' && !p.hasAttribute('tabindex'))"), true);
    assert.equal(await evaluate("(() => {const p=document.querySelector('.history .message-assistant:not(:has(.long-message-toggle)) pre');return p.clientHeight<80 && p.scrollHeight===p.clientHeight})()"), true, "short code retains natural height");
    const codeRow = ".history .message-assistant:has(.long-message-toggle)";
    await evaluate(`(() => {const s=document.querySelector('.timeline-scroll');s.dispatchEvent(new WheelEvent('wheel',{deltaY:-200,bubbles:true}));document.querySelector('${codeRow}').scrollIntoView({block:'start'});s.dispatchEvent(new Event('scroll',{bubbles:true}))})()`);
    assert.equal(await wait("document.querySelector('.timeline-scroll').dataset.following==='false'"), true);
    await evaluate(`document.querySelector('${codeRow} .long-message-toggle').focus({preventScroll:true})`);
    await press("Tab", "Tab", 9);
    assert.equal(await evaluate(`document.activeElement===document.querySelector('${code}')`), true, "Tab enters the code region");
    const outerTop = Number(await evaluate("document.querySelector('.timeline-scroll').scrollTop"));
    for (const [key, virtual] of [["End", 35], ["Home", 36], ["ArrowDown", 40], ["ArrowUp", 38], ["ArrowLeft", 37], ["ArrowRight", 39], ["PageDown", 34], ["PageUp", 33]] as const) {
      await press(key, key, virtual);
      assert.equal(await evaluate(`document.activeElement===document.querySelector('${code}')`), true);
      assert.ok(Math.abs(Number(await evaluate("document.querySelector('.timeline-scroll').scrollTop")) - outerTop) < 3, key);
      if (key === "End") assert.equal(await evaluate(`(p=>p.scrollHeight-p.clientHeight-p.scrollTop<2)(document.querySelector('${code}'))`), true, "last line reachable");
      if (key === "Home") assert.equal(await evaluate(`document.querySelector('${code}').scrollTop`), 0, "first line reachable");
    }
    await press("Tab", "Tab", 9);
    assert.equal(await evaluate(`document.activeElement===document.querySelectorAll('${code}')[1]`), true);
    await command("Input.dispatchKeyEvent", { type: "keyDown", key: "Tab", code: "Tab", windowsVirtualKeyCode: 9, modifiers: 8 });
    await command("Input.dispatchKeyEvent", { type: "keyUp", key: "Tab", code: "Tab", windowsVirtualKeyCode: 9, modifiers: 8 });
    assert.equal(await evaluate(`document.activeElement===document.querySelector('${code}')`), true, "Shift+Tab exits backward normally");
    await evaluate(`(() => {const r=document.createRange();r.selectNodeContents(document.querySelector('${code} code'));getSelection().removeAllRanges();getSelection().addRange(r)})()`);
    assert.equal(await evaluate("getSelection().toString().trimEnd()===window.toolFixture.codeText"), true, "all retained code remains selectable");
    // Do not dispatch Copy to the real clipboard. Verify modifiers stay uncanceled, and fake the message Copy API.
    assert.equal(await evaluate(`(() => {const e=new KeyboardEvent('keydown',{key:'c',ctrlKey:true,bubbles:true,cancelable:true});document.querySelector('${code}').dispatchEvent(e);return !e.defaultPrevented})()`), true);
    await evaluate("getSelection().removeAllRanges();Object.defineProperty(navigator,'clipboard',{configurable:true,value:{writeText:text=>{window.codeCopied=text;return Promise.resolve()}}})");
    await click(`${codeRow} .copy-markdown`);
    assert.equal(await wait("window.codeCopied===window.toolFixture.codeMarkdown"), true);
    for (const width of [390, 1120]) for (const theme of ["light", "dark"]) {
      await command("Emulation.setDeviceMetricsOverride", { width, height: 720, deviceScaleFactor: 1, mobile: false });
      await evaluate(`document.documentElement.dataset.theme='${theme}';document.querySelector('${code}').focus({preventScroll:true})`);
      assert.equal(await evaluate(`(() => {const p=document.querySelector('${code}'),s=getComputedStyle(p);return p.scrollWidth<=p.clientWidth+1 && p.getBoundingClientRect().height<=14*parseFloat(s.lineHeight)+27 && s.outlineStyle==='solid' && document.documentElement.scrollWidth<=${width}+2})()`), true, `${width}/${theme}: wrapping, bound and focus`);
    }
    await evaluate("document.querySelector('.history .message-prompt details').open=true;document.querySelector('.timeline-scroll').style.height='600px'");
    const tailCode = ".history .message-prompt .markdown-content pre:last-of-type";
    assert.equal(await evaluate(`document.querySelector('${tailCode}').tabIndex`), 0, "Markdown details opt in, unlike raw diagnostics");
    const follow = async () => {
      await evaluate("(() => {const s=document.querySelector('.timeline-scroll');s.dispatchEvent(new Event('scroll',{bubbles:true}));s.dispatchEvent(new WheelEvent('wheel',{deltaY:600,bubbles:true}));s.scrollTop=s.scrollHeight;s.dispatchEvent(new Event('scroll',{bubbles:true}))})()");
      assert.equal(await wait("document.querySelector('.timeline-scroll').dataset.following==='true'"), true);
      await evaluate("new Promise(resolve=>setTimeout(resolve,160))");
    };
    await follow();
    await evaluate(`document.querySelector('${tailCode}').scrollTop=180`);
    const point = await evaluate(`(() => {const p=document.querySelector('${tailCode}'),r=p.getBoundingClientRect();return {x:r.left+35,y:r.top+80}})()`) as {x:number;y:number};
    assert.equal(await evaluate(`document.elementFromPoint(${point.x},${point.y}).closest('pre')===document.querySelector('${tailCode}')`), true);
    await command("Input.dispatchMouseEvent", { type: "mouseWheel", ...point, deltaX: 0, deltaY: -90 });
    assert.equal(await wait(`document.querySelector('${tailCode}').scrollTop<180`), true, "real wheel scrolls inside code");
    assert.equal(await evaluate("document.querySelector('.timeline-scroll').dataset.following"), "true");
    await evaluate(`document.querySelector('${tailCode}').scrollTop=0`);
    await command("Input.dispatchMouseEvent", { type: "mouseWheel", ...point, deltaX: 0, deltaY: -400 });
    assert.equal(await wait("document.querySelector('.timeline-scroll').dataset.following==='false'"), true, "boundary wheel chains to the timeline");
    await follow();
    // A real scrollbar click changes the inner viewport without lending intent to the outer scroller.
    const gutter = await evaluate(`(() => {const p=document.querySelector('${tailCode}'),r=p.getBoundingClientRect();p.scrollTop=0;return {x:r.right-7,y:r.bottom-25}})()`) as {x:number;y:number};
    await command("Input.dispatchMouseEvent", { type: "mousePressed", ...gutter, button: "left", clickCount: 1 });
    await command("Input.dispatchMouseEvent", { type: "mouseReleased", ...gutter, button: "left", clickCount: 1 });
    assert.equal(await wait(`document.querySelector('${tailCode}').scrollTop>0`), true, "native code scrollbar is operable");
    assert.equal(await evaluate("document.querySelector('.timeline-scroll').dataset.following"), "true");
    await command("Emulation.setTouchEmulationEnabled", { enabled: true, maxTouchPoints: 1 });
    await evaluate("new Promise(resolve=>setTimeout(resolve,400))"); // Finish native scrollbar page animation before the next gesture.
    await evaluate("window.touchEvidence=[];for(const type of ['pointerdown','pointermove','pointercancel','touchstart','touchmove'])document.addEventListener(type,e=>window.touchEvidence.push([type,e.target.tagName,e.defaultPrevented]),{passive:true});document.querySelector('.timeline-scroll').addEventListener('scroll',e=>window.touchEvidence.push(['outerScroll',e.currentTarget.scrollTop,e.currentTarget.dataset.following]))");
    const swipeDown = async () => {
      await evaluate("new Promise(resolve=>setTimeout(resolve,200))"); // Let programmatic setup reach the compositor before native hit testing/latching.
      const p = await evaluate(`(() => {const r=document.querySelector('${tailCode}').getBoundingClientRect();return {x:r.left+60,y:r.top+70}})()`) as {x:number;y:number};
      await command("Input.dispatchTouchEvent", { type: "touchStart", touchPoints: [{ ...p, id: 1 }] });
      for (let step = 1; step <= 5; step++) {
        await command("Input.dispatchTouchEvent", { type: "touchMove", touchPoints: [{ x:p.x, y:p.y+step*30, id:1 }] });
        await evaluate("new Promise(resolve=>setTimeout(resolve,30))");
      }
      await evaluate("new Promise(resolve=>setTimeout(resolve,200))"); // End without a momentum fling leaking into the next independent gesture.
      await command("Input.dispatchTouchEvent", { type: "touchEnd", touchPoints: [] });
      await evaluate("new Promise(resolve=>setTimeout(resolve,250))");
    };
    await evaluate(`document.querySelector('${tailCode}').scrollTop=180`);
    await swipeDown();
    assert.equal(await wait(`document.querySelector('${tailCode}').scrollTop<180`), true, String(await evaluate(`JSON.stringify({events:window.touchEvidence,top:document.querySelector('${tailCode}').scrollTop,outer:document.querySelector('.timeline-scroll').scrollTop})`)));
    assert.equal(await evaluate("document.querySelector('.timeline-scroll').dataset.following"), "true");
    await evaluate(`document.querySelector('${tailCode}').scrollTop=0`);
    await swipeDown();
    const touchBoundary = await evaluate(`({distance:${distance},following:document.querySelector('.timeline-scroll').dataset.following})`) as {distance:number;following:string};
    assert.equal(touchBoundary.following, touchBoundary.distance > 72 ? "false" : "true", "native touch changes follow only if it actually moves the timeline away");
    console.log("Emulated boundary touch (native chaining is host-dependent):", touchBoundary);
    await command("Emulation.setTouchEmulationEnabled", { enabled: false });
    await follow();
    // Deterministic boundary ownership even when this headless host does not chain native touch.
    await evaluate(`(() => {const p=document.querySelector('${tailCode}'),s=document.querySelector('.timeline-scroll');p.scrollTop=0;
      p.dispatchEvent(new PointerEvent('pointerdown',{bubbles:true,pointerType:'touch',pointerId:71,clientY:200}));
      p.dispatchEvent(new PointerEvent('pointermove',{bubbles:true,pointerType:'touch',pointerId:71,clientY:350}));
      s.scrollTop-=200;s.dispatchEvent(new Event('scroll',{bubbles:true}));
      p.dispatchEvent(new PointerEvent('pointerup',{bubbles:true,pointerType:'touch',pointerId:71}));})()`);
    assert.equal(await wait("document.querySelector('.timeline-scroll').dataset.following==='false'"), true, "actual outer movement with code-boundary touch intent opts out");
    assert.equal(await evaluate("window.toolFixture.calls.length"), codeReads, "code presentation and gestures add no history reads");
    assert.equal(await evaluate("window.toolFixture.network"), 0);
    // Equivalent History refresh preserves expanded content and its actual code DOM.
    await evaluate(`(() => {window.savedCode=document.querySelector('${code}');window.savedCode.focus({preventScroll:true});window.savedCode.scrollTop=100;
      const r=document.createRange();r.selectNodeContents(window.savedCode.querySelector('code'));getSelection().removeAllRanges();getSelection().addRange(r)})()`);
    await click(".history .section-heading button");
    assert.equal(await wait("document.querySelector('.history').dataset.windowReady==='true'"), true);
    assert.equal(await evaluate(`window.savedCode===document.querySelector('${code}')`), true);
    assert.equal(await evaluate("document.activeElement===window.savedCode && window.savedCode.scrollTop===100 && getSelection().toString().trimEnd()===window.toolFixture.codeText"), true,
      "equivalent refresh preserves code focus, position and selection");
    await evaluate("getSelection().removeAllRanges()");
    await evaluate("window.toolFixture.changeCode()");
    await click(".history .section-heading button");
    assert.equal(await wait("document.querySelector('.history').dataset.windowReady==='true' && !window.savedCode.isConnected"), true);
    assert.equal(await evaluate(`document.querySelector('${codeRow} .long-message-toggle').getAttribute('aria-expanded')`), "false", "changed source does not retain a hidden code region");
    await click(`${codeRow} .long-message-toggle`);
    assert.equal(await evaluate(`document.querySelector('${code}').textContent.trimEnd()===window.toolFixture.codeText`), true);
    // A supplied persisted ToolCall message is retained, not promoted to an outcome/body.
    await evaluate("window.toolFixture.select('D')");
    assert.equal(await wait("document.querySelector('.history').dataset.windowReady==='true' && document.querySelectorAll('.history .timeline-message').length===1"), true);
    const supplied = ".history .timeline-message";
    const suppliedDetails = `${supplied} .event-details`;
    const suppliedCode = `${suppliedDetails} .markdown-content pre`;
    assert.equal(await evaluate(`document.querySelector('${suppliedDetails}').open`), false);
    assert.equal(await evaluate(`document.querySelector('${supplied} .message-body > .markdown-content')===null`), true);
    assert.equal(await evaluate(`document.querySelector('${supplied} .message-heading small').textContent`), "Canceled · Tool Call");
    assert.equal(await evaluate(`document.querySelector('${suppliedDetails}').textContent.includes('Supplied activity message')`), true);
    const messageReads = await evaluate("window.toolFixture.calls.length");
    await evaluate(`document.querySelector('${suppliedDetails} summary').focus()`);
    await press(" ", "Space", 32);
    assert.equal(await wait(`document.querySelector('${suppliedDetails}').open`), true);
    assert.equal(await evaluate(`!!document.querySelector('${suppliedDetails} .markdown-content script, ${suppliedDetails} .markdown-content [onerror]') || !!window.messageInjected || !!window.codeInjected`), false);
    assert.equal(await evaluate(`(() => {const p=document.querySelector('${suppliedCode}');return p.tabIndex===0 && !!p.getAttribute('aria-label') && p.scrollHeight>p.clientHeight && p.clientHeight<400})()`), true);
    assert.equal(await evaluate(`getComputedStyle(document.querySelector('${suppliedDetails} .event-detail-body > pre')).whiteSpace`), "pre-wrap");
    await click(`${suppliedDetails} .tool-detail-wrap input`);
    assert.equal(await evaluate(`document.querySelector('${suppliedDetails} .tool-detail-wrap input').checked`), false);
    await evaluate("window.toolFixture.copied=[];Object.defineProperty(navigator,'clipboard',{configurable:true,value:{writeText:text=>{window.toolFixture.copied.push(text);return Promise.resolve()}}})");
    await click(`${supplied} .copy-markdown`);
    assert.equal(await wait("window.toolFixture.copied.length===1"), true);
    assert.equal(await evaluate("window.toolFixture.copied[0].includes(window.toolFixture.toolMessage) && window.toolFixture.copied[0].includes('tool-diagnostic-')"), true);
    assert.equal(await evaluate(`document.querySelector('${supplied}').textContent.includes('Some details were shortened') && document.querySelector('${supplied}').textContent.includes('Additional diagnostic details were omitted')`), true);
    assert.equal(await evaluate("window.toolFixture.calls.length"), messageReads, "disclosure, Wrap and Copy acquire no new history");
    await evaluate(`window.savedMessageCode=document.querySelector('${suppliedCode}')`);
    await click(".history .section-heading button");
    assert.equal(await wait("document.querySelector('.history').dataset.windowReady==='true'"), true);
    assert.equal(await evaluate(`window.savedMessageCode===document.querySelector('${suppliedCode}') && document.querySelector('${suppliedDetails}').open`), true);
    await evaluate("window.toolFixture.replaceToolMessage('Replacement supplied message')");
    await click(".history .section-heading button");
    assert.equal(await wait(`document.querySelector('${suppliedDetails}').textContent.includes('Replacement supplied message')`), true);
    assert.equal(await evaluate(`document.querySelector('${suppliedDetails}').textContent.includes('MESSAGE END') || window.savedMessageCode.isConnected`), false);
    await click(`${supplied} .copy-markdown`);
    assert.equal(await wait("window.toolFixture.copied.length===2"), true);
    assert.equal(await evaluate("window.toolFixture.copied[1].includes('Replacement supplied message') && !window.toolFixture.copied[1].includes('MESSAGE END')"), true);
    assert.equal(await evaluate("document.querySelector('.unchanged-live-tool').textContent"), "Literal name · Reported CompletedName prefix truncated.Live tool identityProvider literal-provider · run not supplied · activity tool-0");
    assert.equal(await evaluate("window.toolFixture.network"), 0);
    // FileChange changes only the disclosure label, never diagnostic/navigation authority.
    await evaluate("window.toolFixture.select('E')");
    assert.equal(await wait("document.querySelector('.history').dataset.windowReady==='true' && !!document.querySelector('.history .message-file')"), true);
    const fileDetails = ".history .message-file .event-details";
    assert.equal(await evaluate(`document.querySelector('${fileDetails} summary').textContent`), "File change record details");
    assert.equal(await evaluate(`document.querySelector('${fileDetails}').open`), false);
    const fileReads = await evaluate("window.toolFixture.calls.length");
    await evaluate(`document.querySelector('${fileDetails} summary').focus()`);
    await press(" ", "Space", 32);
    assert.equal(await wait(`document.querySelector('${fileDetails}').open`), true);
    assert.equal(await evaluate(`document.querySelector('${fileDetails} pre').textContent.includes('../literal.cs') && document.querySelector('${fileDetails} pre').textContent.includes('<img')`), true);
    assert.equal(await evaluate(`!!document.querySelector('${fileDetails} a, ${fileDetails} img, ${fileDetails} script, ${fileDetails} button') || !!window.fileInjected`), false);
    assert.equal(await evaluate(`getComputedStyle(document.querySelector('${fileDetails} pre')).whiteSpace`), "pre-wrap");
    await click(`${fileDetails} .tool-detail-wrap input`);
    assert.equal(await evaluate(`getComputedStyle(document.querySelector('${fileDetails} pre')).whiteSpace`), "pre");
    await evaluate("window.toolFixture.copied=[]");
    await click(".history .copy-markdown");
    assert.equal(await wait("window.toolFixture.copied.length===1"), true);
    assert.equal(await evaluate("window.toolFixture.copied[0]===window.toolFixture.fileCopy"), true);
    assert.equal(await evaluate("document.querySelector('.history').textContent.includes('Some details were shortened') && document.querySelector('.history').textContent.includes('Additional diagnostic details were omitted')"), true);
    assert.equal(await evaluate("window.toolFixture.calls.length"), fileReads);
    await evaluate(`void(window.savedFileDetails=document.querySelector('${fileDetails}'))`);
    await click(".history .section-heading button");
    assert.equal(await wait("document.querySelector('.history').dataset.windowReady==='true'"), true);
    assert.equal(await evaluate(`window.savedFileDetails===document.querySelector('${fileDetails}') && window.savedFileDetails.open && !window.savedFileDetails.querySelector('input').checked`), true);
    await evaluate("window.toolFixture.replaceFileDetails()");
    await click(".history .section-heading button");
    assert.equal(await wait(`document.querySelector('${fileDetails} pre').textContent.includes('../replacement.cs')`), true);
    assert.equal(await evaluate(`document.querySelector('${fileDetails} pre').textContent.includes('../literal.cs')`), false);
    assert.equal(await evaluate(`window.savedFileDetails===document.querySelector('${fileDetails}') && window.savedFileDetails.open && !window.savedFileDetails.querySelector('input').checked`), true);
    await click(".history .copy-markdown");
    assert.equal(await wait("window.toolFixture.copied.length===2"), true);
    assert.equal(await evaluate("window.toolFixture.copied[1].includes('../replacement.cs') && !window.toolFixture.copied[1].includes('../literal.cs')"), true);
    assert.equal(await evaluate("window.toolFixture.network"), 0);
  } finally {
    socket?.close(); browser?.kill(); await rm(root, { recursive: true, force: true, maxRetries: 8, retryDelay: 100 });
  }
});
