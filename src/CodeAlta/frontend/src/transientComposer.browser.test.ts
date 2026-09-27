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

test("empty regular composer transient help and palette keep drafts and ownership", { skip: !edge, timeout: 90_000 }, async () => {
  const root = await mkdtemp(join(tmpdir(), "codealta-transient-composer-"));
  let browser: ReturnType<typeof spawn> | undefined;
  let socket: WebSocket | undefined;
  try {
    await build({ entryPoints: [fileURLToPath(new URL("./main.tsx", import.meta.url))], outfile: join(root, "fixture.js"),
      bundle: true, platform: "browser", format: "iife", loader: { ".css": "empty" },
      define: { "import.meta.env.VITE_DEMO_MODE": '"false"' }, plugins: [{ name: "isolated-bridge", setup(bundle) {
        bundle.onResolve({ filter: /^#neoastra$/ }, () => ({ path: fileURLToPath(new URL("./settingsShell.neoastra.mount.ts", import.meta.url)) }));
      } }] });
    await writeFile(join(root, "style.css"), readFileSync(new URL("../node_modules/flexlayout-react/style/light.css", import.meta.url), "utf8") +
      "\n" + readFileSync(new URL("./style.css", import.meta.url), "utf8"));
    const page = join(root, "fixture.html");
    await writeFile(page, '<!doctype html><html><head><link rel="stylesheet" href="style.css"></head><body><div id="root"></div><script src="fixture.js"></script></body></html>');
    browser = spawn(edge!, ["--headless=new", "--disable-gpu", "--no-first-run", "--disable-background-networking", "--disable-extensions",
      `--user-data-dir=${join(root, "profile")}`, "--remote-debugging-port=0", "about:blank"], { stdio: "ignore", windowsHide: true });
    let port = "";
    for (let attempt = 0; attempt < 100 && !port; attempt++) {
      try { port = (await readFile(join(root, "profile", "DevToolsActivePort"), "utf8")).split(/\r?\n/)[0]; }
      catch { await new Promise(resolve => setTimeout(resolve, 50)); }
    }
    assert.match(port, /^\d+$/);
    const pages: { type?: string; url?: string; webSocketDebuggerUrl?: string }[] =
      await (await fetch(`http://127.0.0.1:${port}/json/list`, { signal: AbortSignal.timeout(10_000) })).json();
    const tab = pages.find(value => value.type === "page" && value.url === "about:blank");
    assert.ok(tab?.webSocketDebuggerUrl);
    socket = new WebSocket(tab.webSocketDebuggerUrl);
    await new Promise<void>((resolve, reject) => { socket!.addEventListener("open", () => resolve(), { once: true });
      socket!.addEventListener("error", () => reject(new Error("test browser unavailable")), { once: true }); });
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
    // Do not edit a fully clipped composer in the headless default 750x485 viewport.
    await command("Emulation.setDeviceMetricsOverride", { width: 1120, height: 800, deviceScaleFactor: 1, mobile: false });
    await command("Page.navigate", { url: pathToFileURL(page).href });
    const evaluate = async (expression: string) => (await command("Runtime.evaluate", { expression, returnByValue: true, awaitPromise: true })).result?.value;
    const wait = (condition: string) => evaluate(`new Promise(resolve=>{const end=Date.now()+7000;const tick=()=>{
      if(${condition}) resolve(true); else if(Date.now()>end) resolve(document.body.innerText.slice(-1000)); else setTimeout(tick,20);};tick();})`);
    const key = async (value: string, options: { repeat?: boolean; modifiers?: number } = {}) => {
      const code = value === "?" || value === "/" ? "Slash" : value;
      const windowsVirtualKeyCode = code === "Slash" ? 191 : value.charCodeAt(0);
      const params = { key: value, code, windowsVirtualKeyCode, text: value, modifiers: options.modifiers ?? (value === "?" ? 8 : 0) };
      await command("Input.dispatchKeyEvent", { type: "keyDown", ...params, autoRepeat: options.repeat ?? false });
      await command("Input.dispatchKeyEvent", { type: "keyUp", ...params });
    };
    const focus = (selector: string) => evaluate(`document.querySelector('${selector}').focus()`);
    const close = async (selector: string) => { await evaluate(`document.querySelector('${selector}').click()`); assert.equal(await wait(`!document.querySelector('${selector}')`), true); };
    // Intersect with every clipping/scrolling ancestor, not just the textarea's own box.
    const installEditorProbe = () => evaluate(`window.visibleEditorBox=selector=>{const e=document.querySelector(selector);if(!e)return null;
      const r=e.getBoundingClientRect();let top=Math.max(0,r.top),bottom=Math.min(innerHeight,r.bottom),left=Math.max(0,r.left),right=Math.min(innerWidth,r.right);
      for(let p=e.parentElement;p;p=p.parentElement){const s=getComputedStyle(p),b=p.getBoundingClientRect();
        if(/auto|scroll|hidden|clip/.test(s.overflowY)){top=Math.max(top,b.top);bottom=Math.min(bottom,b.bottom);}
        if(/auto|scroll|hidden|clip/.test(s.overflowX)){left=Math.max(left,b.left);right=Math.min(right,b.right);}
        if(p.matches('dialog:modal'))break;}
      return {height:Math.max(0,bottom-top),width:Math.max(0,right-left)};};true`);
    const shortEditor = async (selector: string) => {
      await installEditorProbe();
      const original = await evaluate(`document.querySelector('${selector}').value`) as string;
      await evaluate(`window.shortEditorIdentity=document.querySelector('${selector}');true`);
      for (const [width, height] of [[750, 485], [390, 500], [1120, 800], [750, 485]]) {
        await command("Emulation.setDeviceMetricsOverride", { width, height, deviceScaleFactor: 1, mobile: false });
        assert.equal(await wait(`innerWidth===${width} && innerHeight===${height} && ${width < 875
          ? `Math.abs(document.querySelector('.content').getBoundingClientRect().width-${width})<2 && document.querySelector('.active-session-content').clientHeight<${height * .7}`
          : `document.querySelector('.content').getBoundingClientRect().width<${width * .8} && document.querySelector('.active-session-content').clientHeight>${height * .7}`}`), true,
          "wait for the public layout container to adopt the requested viewport before editing");
        await focus(selector);
        await evaluate(`document.documentElement.dataset.theme='${width === 390 ? "light" : "dark"}'`);
        await evaluate(`document.querySelector('${selector}').scrollIntoView({block:'center',inline:'nearest'})`);
        assert.equal(await wait(`visibleEditorBox('${selector}')?.height>=48 && visibleEditorBox('${selector}')?.width>=80`), true,
          `${selector} ${width}x${height}: native editor is visibly usable through all scroll ancestors`);
        assert.equal(await evaluate(`shortEditorIdentity===document.querySelector('${selector}') && document.activeElement===shortEditorIdentity`), true);
        await evaluate(`shortEditorIdentity.select()`);
        await command("Input.insertText", { text: "/" }); await key("?");
        assert.deepEqual(await evaluate(`({value:shortEditorIdentity.value,start:shortEditorIdentity.selectionStart,end:shortEditorIdentity.selectionEnd,modal:!!document.querySelector('dialog[open]')})`),
          { value: "/?", start: 2, end: 2, modal: false });
        await evaluate("shortEditorIdentity.setSelectionRange(0,2)"); await key("/");
        assert.deepEqual(await evaluate("[shortEditorIdentity.value,shortEditorIdentity.selectionStart,shortEditorIdentity.selectionEnd]"), ["/", 1, 1]);
        if (width === 390 && await evaluate("!!document.querySelector('.composer-splitter')")) {
          await focus(".composer-splitter");
          for (let i = 0; i < 32; i++) {
            await command("Input.dispatchKeyEvent", { type: "keyDown", key: "ArrowDown", code: "ArrowDown", windowsVirtualKeyCode: 40 });
            await command("Input.dispatchKeyEvent", { type: "keyUp", key: "ArrowDown", code: "ArrowDown", windowsVirtualKeyCode: 40 });
          }
          assert.equal(await wait("(()=>{const s=document.querySelector('.composer-splitter');return s.getAttribute('aria-valuenow')===s.getAttribute('aria-valuemin')})()"), true);
          await focus(selector); await evaluate("shortEditorIdentity.scrollIntoView({block:'center',inline:'nearest'})");
          assert.equal(await evaluate(`visibleEditorBox('${selector}').height>=48`), true, "minimum manual composer remains scrollably editable");
          await key("?");
          assert.deepEqual(await evaluate("[shortEditorIdentity.value,shortEditorIdentity.selectionStart,shortEditorIdentity.selectionEnd]"), ["/?", 2, 2]);
          await focus(".composer-splitter");
          await command("Input.dispatchKeyEvent", { type: "keyDown", key: "Home", code: "Home", windowsVirtualKeyCode: 36 });
          await command("Input.dispatchKeyEvent", { type: "keyUp", key: "Home", code: "Home", windowsVirtualKeyCode: 36 });
          assert.equal(await wait("!document.querySelector('.composer-region.resized')"), true);
          await focus(selector);
        }
        // Real Tab traversal must reveal the primary editor action, without DOM clicking hidden controls.
        for (let i = 0; i < 24 && !await evaluate("document.activeElement?.id==='expand-session-prompt'"); i++) {
          await command("Input.dispatchKeyEvent", { type: "keyDown", key: "Tab", code: "Tab", windowsVirtualKeyCode: 9 });
          await command("Input.dispatchKeyEvent", { type: "keyUp", key: "Tab", code: "Tab", windowsVirtualKeyCode: 9 });
        }
        assert.equal(await evaluate("document.activeElement?.id==='expand-session-prompt' && visibleEditorBox('#expand-session-prompt').height>=20"), true,
          "Tab scrolls the editor action into view");
      }
      await focus(selector); await evaluate("shortEditorIdentity.select()");
      await command("Input.insertText", { text: original });
    };

    assert.equal(await wait("!!document.querySelector('#catalog-prompt')"), true);
    await focus("#catalog-prompt");
    await key("/");
    assert.equal(await wait("document.querySelector('.command-palette')?.open"), true, "catalog draft composer opens implemented palette");
    assert.equal(await evaluate("document.querySelector('#catalog-prompt').value"), "");
    await close('[aria-label="Close command palette"]');
    assert.equal(await wait("document.activeElement===document.querySelector('#catalog-prompt')"), true);
    await key("?");
    assert.equal(await wait("!!document.querySelector('.shortcut-dialog')"), true, "Shift+/? opens help, not literal text");
    await close('.shortcut-dialog [aria-label="Close"]');
    assert.equal(await wait("document.activeElement===document.querySelector('#catalog-prompt')"), true);
    assert.equal(await evaluate("localStorage.getItem('codealta.desktop.prompt.one')"), null);

    await command("Input.insertText", { text: "/" });
    assert.equal(await wait("document.querySelector('#catalog-prompt').value==='/'"), true, "paste stays literal");
    assert.deepEqual(await evaluate("(()=>{const e=document.querySelector('#catalog-prompt');return [e.selectionStart,e.selectionEnd,document.activeElement===e]})()"), [1, 1, true]);
    await key("?");
    assert.equal(await evaluate("document.querySelector('#catalog-prompt').value==='/?' && !document.querySelector('.shortcut-dialog')"), true);
    await evaluate("document.querySelector('#catalog-prompt').setSelectionRange(0,2)");
    await key("/");
    assert.equal(await evaluate("document.querySelector('#catalog-prompt').value==='/' && !document.querySelector('.command-palette')"), true,
      "selection replacement is text, never a command");
    assert.deepEqual(await evaluate("(()=>{const e=document.querySelector('#catalog-prompt');return [e.selectionStart,e.selectionEnd,document.activeElement===e]})()"), [1, 1, true]);
    await command("Input.insertText", { text: " " });
    await key("/");
    assert.equal(await evaluate("document.querySelector('#catalog-prompt').value==='/ /' && !document.querySelector('.command-palette')"), true,
      "whitespace and existing text cannot invoke transient shortcuts");
    await shortEditor("#catalog-prompt");

    await evaluate("localStorage.setItem('usageFixtureArchived','true'); localStorage.removeItem('codealta.desktop.prompt.one')");
    await command("Page.reload");
    assert.equal(await wait("!!document.querySelector('#catalog-prompt') && document.querySelector('#catalog-draft-status')?.textContent.includes('Archived')"), true);
    await evaluate("document.querySelector('#catalog-prompt').focus(); document.querySelector('#catalog-prompt').setSelectionRange(0,0)");
    await key("?");
    assert.equal(await wait("!!document.querySelector('.shortcut-dialog')"), true);
    await close('.shortcut-dialog [aria-label="Close"]');
    assert.equal(await evaluate("window.settingsShellFixture.sends.length===0 && !!document.querySelector('#catalog-prompt')"), true);
    await focus("#catalog-prompt");
    await key("/");
    assert.equal(await wait("document.querySelector('.command-palette')?.open"), true);
    assert.equal(await evaluate("!!document.querySelector('#palette-option-settings') && !document.querySelector('#palette-option-reminders')"), true,
      "archived scope retains navigation without owned reminder actions");
    await evaluate("document.querySelector('#palette-option-settings').click()");
    assert.equal(await wait("document.querySelector('.settings-dialog')?.open"), true);
    await close('[aria-label="Close settings"]');

    await evaluate("localStorage.removeItem('usageFixtureArchived'); localStorage.setItem('settingsFixtureOwned','true'); localStorage.removeItem('codealta.desktop.prompt.one')");
    await command("Page.reload");
    assert.equal(await wait("!!document.querySelector('#session-prompt')"), true);
    await focus("#session-prompt");
    await key("/");
    assert.equal(await wait("document.querySelector('.command-palette')?.open"), true, "owned composer opens same palette");
    assert.equal(await evaluate("document.querySelector('#session-prompt').value==='' && window.settingsShellFixture.sends.length===0"), true);
    await close('[aria-label="Close command palette"]');
    assert.equal(await wait("document.activeElement===document.querySelector('#session-prompt')"), true);
    await key("?");
    assert.equal(await wait("!!document.querySelector('.shortcut-dialog')"), true);
    await evaluate("document.querySelector('#session-prompt').dispatchEvent(new KeyboardEvent('keydown',{key:'/',bubbles:true,cancelable:true}))");
    assert.equal(await evaluate("!!document.querySelector('.shortcut-dialog') && !document.querySelector('.command-palette')"), true,
      "an open modal cannot be replaced by a background composer gesture");
    await evaluate("window.originalFrame=requestAnimationFrame; window.helpCloseFrames=[]; window.requestAnimationFrame=callback=>{window.helpCloseFrames.push(callback); return 123456;}; document.querySelector('.shortcut-dialog [aria-label=\"Close\"]').click()");
    assert.equal(await wait("!document.querySelector('.shortcut-dialog')"), true);
    await evaluate("window.requestAnimationFrame=window.originalFrame; document.querySelector('.activity-rail button[aria-label=\"Open command palette\"]').click()");
    assert.equal(await wait("document.querySelector('.command-palette')?.open"), true);
    assert.equal(await evaluate("(() => { for(const callback of window.helpCloseFrames) callback(performance.now()); return document.activeElement?.closest('.command-palette')!==null; })()"), true,
      "help close must not steal newer palette focus");
    await focus("#palette-search");
    await key("/");
    assert.equal(await evaluate("document.querySelector('#palette-search').value==='/' && document.querySelector('.command-palette')?.open"), true,
      "typing slash into another editor remains text");
    await close('[aria-label="Close command palette"]');
    await focus("#session-prompt");
    assert.equal(await evaluate("localStorage.getItem('codealta.desktop.prompt.one')===null && window.settingsShellFixture.sends.length===0"), true);

    await evaluate(`(() => { const input=document.querySelector('#session-prompt');
      for(const props of [{key:'/',isComposing:true},{key:'?',keyCode:229},{key:'/',repeat:true},
        {key:'/',ctrlKey:true},{key:'?',metaKey:true},{key:'/',altKey:true}]) {
        const event=new KeyboardEvent('keydown',{...props,bubbles:true,cancelable:true});
        if(props.keyCode) Object.defineProperty(event,'keyCode',{value:props.keyCode});
        input.dispatchEvent(event);
      }
      input.addEventListener('keydown',event=>event.preventDefault(),{capture:true,once:true});
      input.dispatchEvent(new KeyboardEvent('keydown',{key:'/',bubbles:true,cancelable:true}));
    })()`);
    assert.deepEqual(await evaluate("({dialogs:[...document.querySelectorAll('.shortcut-dialog,.command-palette')].map(node=>node.className),value:document.querySelector('#session-prompt').value})"), {dialogs: [], value: ""},
      "composing/229, repeats, handled and modified keys remain unhandled by navigation");
    await command("Input.insertText", { text: "?/" });
    assert.equal(await wait("document.querySelector('#session-prompt').value==='?/'"), true, "pasted characters stay literal");
    await evaluate("document.querySelector('#session-prompt').setSelectionRange(0,2)");
    await key("/");
    assert.equal(await evaluate("document.querySelector('#session-prompt').value==='/' && !document.querySelector('.command-palette')"), true);
    assert.deepEqual(await evaluate("(()=>{const e=document.querySelector('#session-prompt');return [e.selectionStart,e.selectionEnd,document.activeElement===e]})()"), [1, 1, true]);
    await shortEditor("#session-prompt");
    await evaluate("document.querySelector('#expand-session-prompt').click()");
    assert.equal(await wait("document.querySelector('.expanded-prompt-dialog')?.open"), true);
    await focus('.expanded-prompt-dialog textarea');
    assert.equal(await evaluate("visibleEditorBox('.expanded-prompt-dialog textarea').height>=48 && visibleEditorBox('.expanded-prompt-dialog textarea').width>=80"), true,
      "expanded editor remains visibly editable at 750x485");
    await key("?");
    assert.equal(await evaluate("document.querySelector('.expanded-prompt-dialog textarea').value.includes('?') && !document.querySelector('.shortcut-dialog')"), true,
      "expanded editor does not intercept transient characters");
    await evaluate("document.querySelector('.expanded-prompt-dialog button').click()");
    assert.equal(await wait("!document.querySelector('.expanded-prompt-dialog') && document.querySelector('#session-prompt').value.includes('?')"), true);
    assert.equal(await evaluate("window.settingsShellFixture.sends.length===0 && localStorage.getItem('codealta.desktop.prompt.one')===document.querySelector('#session-prompt').value"), true,
      "literal draft is retained without Send or Steer");
    await evaluate("window.firstDraft=document.querySelector('#session-prompt').value; document.querySelector('.session-row:nth-child(2) > button:first-child').click()");
    assert.equal(await wait("document.querySelector('.session-header h1')?.textContent==='two' && document.querySelector('#session-prompt')?.value===''"), true);
    await focus("#session-prompt");
    await key("?");
    assert.equal(await wait("!!document.querySelector('.shortcut-dialog')"), true);
    await evaluate("window.oldPrompt=document.querySelector('#session-prompt'); window.originalFrame=requestAnimationFrame; window.helpCloseFrames=[]; window.requestAnimationFrame=callback=>{window.helpCloseFrames.push(callback); return 123456;}; document.querySelector('.shortcut-dialog [aria-label=\"Close\"]').click()");
    assert.equal(await wait("!document.querySelector('.shortcut-dialog')"), true);
    await evaluate("window.requestAnimationFrame=window.originalFrame; document.querySelector('.session-row:nth-child(1) > button:first-child').click()");
    assert.equal(await wait("document.querySelector('.session-header h1')?.textContent==='one' && !window.oldPrompt.isConnected"), true);
    assert.equal(await evaluate(`(() => { const editor=document.querySelector('#session-prompt'); editor.focus();
      for(const callback of window.helpCloseFrames) callback(performance.now());
      return document.activeElement===editor && editor.value===window.firstDraft; })()`), true,
      "old help close frame must not steal replacement session focus or lose its original draft");

    await evaluate("document.querySelector('.session-row:nth-child(2) > button:first-child').click(); document.documentElement.dataset.theme='light'");
    await command("Emulation.setDeviceMetricsOverride", { width: 390, height: 800, deviceScaleFactor: 1, mobile: false });
    assert.equal(await wait("document.querySelector('.session-header h1')?.textContent==='two' && document.querySelector('#session-prompt')?.value===''"), true);
    await focus("#session-prompt");
    await key("/");
    assert.equal(await wait("document.querySelector('.command-palette')?.open"), true);
    await close('[aria-label="Close command palette"]');
    assert.equal(await wait("document.activeElement===document.querySelector('#session-prompt')"), true,
      "narrow light palette close restores the still-live composer");
    await key("?");
    assert.equal(await wait("!!document.querySelector('.shortcut-dialog')"), true);
    await command("Input.dispatchKeyEvent", { type: "keyDown", key: "Escape", code: "Escape", windowsVirtualKeyCode: 27 });
    await command("Input.dispatchKeyEvent", { type: "keyUp", key: "Escape", code: "Escape", windowsVirtualKeyCode: 27 });
    assert.equal(await wait("!document.querySelector('.shortcut-dialog') && document.activeElement===document.querySelector('#session-prompt')"), true,
      "Escape from help restores the original regular composer");
    assert.equal(await evaluate("window.settingsShellFixture.sends.length===0 && document.querySelector('#session-prompt').value==='' && localStorage.getItem('codealta.desktop.prompt.two')===null"), true);
    await evaluate("document.querySelector('.session-tabs [role=tab]').click()");
    assert.equal(await wait("!!document.querySelector('#catalog-prompt') && document.querySelector('.session-header h1')?.textContent==='Prompt draft'"), true);
    await focus("#catalog-prompt"); await command("Input.insertText", { text: "local draft" });
    await shortEditor("#catalog-prompt");
    assert.equal(await evaluate("document.querySelector('#catalog-prompt').value==='local draft' && settingsShellFixture.sends.length===0"), true);
    for (let i = 0; i < 12 && !await evaluate("document.activeElement?.textContent==='Create and transfer draft'"); i++) {
      await command("Input.dispatchKeyEvent", { type: "keyDown", key: "Tab", code: "Tab", windowsVirtualKeyCode: 9 });
      await command("Input.dispatchKeyEvent", { type: "keyUp", key: "Tab", code: "Tab", windowsVirtualKeyCode: 9 });
    }
    assert.equal(await evaluate("(()=>{const b=document.activeElement,r=b.getBoundingClientRect();return b.textContent==='Create and transfer draft' && document.elementFromPoint(r.x+r.width/2,r.y+r.height/2)?.closest('button')===b})()"), true,
      "local draft creation action is reachable without creating or sending");
  } finally {
    socket?.close(); browser?.kill();
    await rm(root, { recursive: true, force: true, maxRetries: 8, retryDelay: 100 });
  }
});
