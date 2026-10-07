import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { existsSync, readFileSync } from "node:fs";
import { mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { fileURLToPath, pathToFileURL } from "node:url";
import { build } from "esbuild";

const edge = ["C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe", "C:/Program Files/Microsoft/Edge/Application/msedge.exe"].find(existsSync);

// Mounts the production OwnedSessionPanel with the production styles. The fixture plays the host: it takes or
// refuses a Send, starts and ends the run, and takes steering.
test("a prompt sent while the session works is queued or steers, and is never refused", { skip: !edge, timeout: 90_000 }, async () => {
  const root = await mkdtemp(join(tmpdir(), "codealta-composer-"));
  let browser: ReturnType<typeof spawn> | undefined;
  let socket: WebSocket | undefined;
  try {
    await build({ entryPoints: [fileURLToPath(new URL("./composer.mount.tsx", import.meta.url))], outfile: join(root, "fixture.js"),
      bundle: true, platform: "browser", format: "iife", loader: { ".css": "empty", ".flf": "text", ".svg": "dataurl" },
      define: { "import.meta.env.VITE_DEMO_MODE": '"false"' }, plugins: [{ name: "isolated-composer", setup(bundle) {
        bundle.onResolve({ filter: /^#neoastra$/ }, () => ({ path: fileURLToPath(new URL("./composer.neoastra.mount.ts", import.meta.url)) }));
        bundle.onResolve({ filter: /^monaco-editor\/.*\?worker$/ }, args => ({ path: args.path, namespace: "fixture-worker" }));
        bundle.onLoad({ filter: /.*/, namespace: "fixture-worker" }, async () => {
          const worker = await build({ entryPoints: [fileURLToPath(new URL("../node_modules/monaco-editor/esm/vs/editor/editor.worker.js", import.meta.url))],
            bundle: true, platform: "browser", format: "iife", write: false });
          return { loader: "js", contents: `export default class extends Worker { constructor() {
            const url=URL.createObjectURL(new Blob([${JSON.stringify(worker.outputFiles[0].text)}],{type:"text/javascript"}));
            super(url); URL.revokeObjectURL(url);
          } }` };
        });
      } }] });
    await writeFile(join(root, "style.css"), readFileSync(new URL("../node_modules/@blueprintjs/core/lib/css/blueprint.css", import.meta.url), "utf8")
      + readFileSync(new URL("./style.css", import.meta.url), "utf8"));
    const page = join(root, "fixture.html");
    await writeFile(page, '<!doctype html><html><head><link rel="stylesheet" href="style.css"></head><body><div id="app"></div><script src="fixture.js"></script></body></html>');
    browser = spawn(edge!, ["--headless=new", "--disable-gpu", "--no-first-run", "--disable-background-networking", "--disable-extensions", "--allow-file-access-from-files",
      `--user-data-dir=${join(root, "profile")}`, "--remote-debugging-port=0", "about:blank"], { stdio: "ignore", windowsHide: true });
    let port = "";
    for (let i = 0; i < 100 && !port; i++) {
      try { port = (await readFile(join(root, "profile", "DevToolsActivePort"), "utf8")).split(/\r?\n/)[0]; }
      catch { await new Promise(resolve => setTimeout(resolve, 50)); }
    }
    assert.match(port, /^\d+$/);
    const pages = await (await fetch(`http://127.0.0.1:${port}/json/list`)).json() as { type?: string; webSocketDebuggerUrl?: string }[];
    const tab = pages.find(tab => tab.type === "page");
    assert.ok(tab?.webSocketDebuggerUrl);
    socket = new WebSocket(tab.webSocketDebuggerUrl);
    await new Promise<void>((resolve, reject) => { socket!.addEventListener("open", () => resolve(), { once: true }); socket!.addEventListener("error", reject, { once: true }); });
    const exceptions: string[] = [];
    socket.addEventListener("message", event => {
      const value = JSON.parse(String(event.data));
      if (value.method === "Runtime.exceptionThrown") exceptions.push(value.params.exceptionDetails.exception?.description ?? value.params.exceptionDetails.text);
      if (value.method === "Runtime.consoleAPICalled" && value.params.type === "error")
        exceptions.push(value.params.args.map((arg: { description?: string; value?: unknown }) => arg.description ?? arg.value).join(" "));
    });
    let sequence = 0;
    const command = (method: string, params: object = {}) => new Promise<Record<string, any>>((resolve, reject) => {
      const id = ++sequence;
      const reply = (event: MessageEvent) => {
        const value = JSON.parse(String(event.data));
        if (value.id !== id) return;
        socket!.removeEventListener("message", reply); clearTimeout(timer);
        value.error ? reject(new Error(JSON.stringify(value.error))) : resolve(value.result);
      };
      const timer = setTimeout(() => { socket!.removeEventListener("message", reply); reject(new Error(`${method} timed out`)); }, 12_000);
      socket!.addEventListener("message", reply); socket!.send(JSON.stringify({ id, method, params }));
    });
    const evaluate = async (expression: string) => {
      const value = await command("Runtime.evaluate", { expression, returnByValue: true, awaitPromise: true });
      assert.equal(value.exceptionDetails, undefined, JSON.stringify(value.exceptionDetails)); return value.result?.value;
    };
    // Resolves to true, or to what the page shows when the condition never held.
    const wait = (condition: string) => evaluate(`new Promise(resolve=>{const end=Date.now()+8000;const tick=()=>{if(${condition})resolve(true);
      else if(Date.now()>end)resolve({rows:fixture.rows(),sends:fixture.sendCalls.map(call=>call.text),steers:fixture.steerCalls.map(call=>call.text),
        run:fixture.run(),status:document.querySelector('.composer-status-line')?.textContent,prompt:promptText()});else setTimeout(tick,20)};tick()})`);
    const key = async (key: string, code: string, virtual: number, ctrl = false) => {
      for (const type of ["keyDown", "keyUp"]) await command("Input.dispatchKeyEvent", { type, key, code, windowsVirtualKeyCode: virtual, modifiers: ctrl ? 2 : 0 });
    };
    const enter = (ctrl = false) => key("Enter", "Enter", 13, ctrl);
    const write = async (text: string) => {
      await evaluate("document.querySelector('#session-prompt').focus()");
      assert.equal(await wait("!!document.activeElement?.closest('#session-prompt')"), true);
      await command("Input.insertText", { text });
      assert.equal(await wait(`promptText()===${JSON.stringify(text)}`), true);
    };
    const rows = async () => JSON.parse(await evaluate("JSON.stringify(fixture.rows())")) as string[];
    const sends = async () => JSON.parse(await evaluate("JSON.stringify(fixture.sendCalls.map(call=>call.text))")) as string[];
    const steers = async () => JSON.parse(await evaluate("JSON.stringify(fixture.steerCalls.map(call=>call.text))")) as string[];
    const running = "!!document.querySelector('.composer-toolbar [aria-label=\"Cancel observed run\"]')";
    const idle = "!!document.querySelector('.composer-toolbar [aria-label=\"Send\"]')";
    const row = (text: string) => `[...document.querySelectorAll('.composer-queue-row')].find(row=>row.querySelector('.composer-queue-preview').textContent===${JSON.stringify(text)})`;

    await command("Page.enable");
    await command("Runtime.enable");
    await command("Page.addScriptToEvaluateOnNewDocument", { source: `window.promptText=()=>(document.querySelector('#session-prompt .view-lines')?.textContent??'').replaceAll('\\u00a0',' ')` });
    await command("Emulation.setFocusEmulationEnabled", { enabled: true });
    await command("Emulation.setDeviceMetricsOverride", { width: 1120, height: 800, deviceScaleFactor: 1, mobile: false });
    await command("Page.navigate", { url: pathToFileURL(page).href });
    assert.equal(await wait(`!!document.querySelector('#session-prompt') && ${idle}`), true, exceptions.join("\n"));

    // The UI tools type with events of the page, which the browser's own text input does not take: the editor
    // types their characters, and one event replaces its text.
    await evaluate("document.querySelector('#session-prompt').focus()");
    assert.equal(await wait("!!document.activeElement?.closest('#session-prompt')"), true);
    await evaluate(`(()=>{const node=document.activeElement;for(const key of ['O','k',' ','!']){
      node.dispatchEvent(new KeyboardEvent('keydown',{key,bubbles:true,cancelable:true}));node.dispatchEvent(new KeyboardEvent('keyup',{key,bubbles:true,cancelable:true}));}})()`);
    assert.equal(await wait("promptText()==='Ok !'"), true);
    assert.equal(await evaluate("!document.activeElement.dispatchEvent(new CustomEvent('codealta:fill',{bubbles:true,cancelable:true,detail:'replaced by a tool'}))"), true);
    assert.equal(await wait("promptText()==='replaced by a tool'"), true);
    // A shortcut is no character.
    await evaluate(`(()=>{const node=document.activeElement;
      node.dispatchEvent(new KeyboardEvent('keydown',{key:'b',ctrlKey:true,bubbles:true,cancelable:true}));node.dispatchEvent(new KeyboardEvent('keyup',{key:'b',ctrlKey:true,bubbles:true,cancelable:true}));})()`);
    assert.equal(await evaluate("promptText()"), "replaced by a tool");
    assert.equal(await evaluate("!document.activeElement.dispatchEvent(new CustomEvent('codealta:fill',{bubbles:true,cancelable:true,detail:''}))"), true);
    assert.equal(await wait("promptText()===''"), true);

    // An idle session takes the prompt at once; the Stop button then holds the Send slot.
    await write("first"); await enter();
    assert.equal(await wait(`fixture.sendCalls.length===1 && ${running} && promptText()===''`), true);
    assert.equal(await evaluate("!!document.querySelector('.composer-queue-strip')"), false);

    // Enter while the session works: the prompt waits above the composer. Nothing is sent, refused or announced.
    await write("second"); await enter();
    assert.equal(await wait("fixture.rows().length===1 && promptText()===''"), true);
    await write("third"); await enter();
    assert.deepEqual(await rows(), ["Queue:waiting:1:second", "Queue:waiting:1:third"]);
    assert.deepEqual(await sends(), ["first"]);
    assert.equal(await evaluate("document.querySelectorAll('.bp6-toast').length"), 0, "a prompt is never answered with a toast");
    assert.deepEqual(JSON.parse(await evaluate("JSON.stringify(fixture.echoes())")), ["accepted:first"]);
    assert.deepEqual(JSON.parse(await evaluate("JSON.stringify([...document.querySelectorAll('.composer-queue-position')].map(mark=>mark.textContent))")), ["1", "2"]);
    assert.equal(await evaluate(`!!document.querySelector('.composer-toolbar [aria-label="Steer the running turn"]') && !!document.querySelector('.composer-toolbar [aria-label="Enqueue for the next turn"]')`), true);

    // The repeat count, by its stepper and by a typed number.
    await evaluate(`${row("second")}.querySelector('[aria-label="Increase repeat count"]').click()`);
    assert.equal(await wait(`fixture.rows()[0]==='Queue:waiting:2:second'`), true);
    assert.equal(await evaluate(`${row("second")}.querySelector('[aria-label="Repeat count"]').value`), "×2");
    assert.match(await evaluate("document.querySelector('.composer-queue-summary').textContent"), /2 queued · 3 sends/);
    await evaluate(`${row("third")}.querySelector('[aria-label="Repeat count"]').focus()`);
    assert.equal(await wait("document.activeElement.value==='1' && document.activeElement.selectionEnd-document.activeElement.selectionStart===1"), true,
      "the field shows the bare number, selected for typing over");
    await command("Input.insertText", { text: "4" });
    await key("Enter", "Enter", 13);
    assert.equal(await wait(`fixture.rows()[1]==='Queue:waiting:4:third'`), true);
    await evaluate(`${row("third")}.querySelector('[aria-label="Decrease repeat count"]').click()`);
    await evaluate(`${row("third")}.querySelector('[aria-label="Decrease repeat count"]').click()`);
    await evaluate(`${row("third")}.querySelector('[aria-label="Decrease repeat count"]').click()`);
    assert.equal(await wait(`fixture.rows()[1]==='Queue:waiting:1:third'`), true);
    assert.equal(await evaluate(`${row("third")}.querySelector('[aria-label="Decrease repeat count"]').disabled`), true);

    // The rows stay one line each and inside the window, in both themes and in a narrow window.
    for (const width of [390, 1120]) {
      await command("Emulation.setDeviceMetricsOverride", { width, height: 800, deviceScaleFactor: 1, mobile: false });
      for (const theme of ["dark", "light"]) {
        await evaluate(`document.documentElement.dataset.theme='${theme}'`);
        const layout = JSON.parse(await evaluate(`JSON.stringify((()=>{const strip=document.querySelector('.composer-queue-strip').getBoundingClientRect();
          return {rows:[...document.querySelectorAll('.composer-queue-row')].map(row=>Math.round(row.getBoundingClientRect().height)),
            trash:[...document.querySelectorAll('.composer-queue-delete')].every(button=>{const box=button.getBoundingClientRect();return box.width>0&&box.right<=strip.right+1}),
            page:document.documentElement.scrollWidth,view:document.documentElement.clientWidth,right:Math.round(strip.right)}})())`));
        assert.ok(layout.rows.every((height: number) => height >= 28 && height <= 34), `${width}px ${theme} rows are one line: ${JSON.stringify(layout)}`);
        assert.ok(layout.trash && layout.page <= layout.view + 1 && layout.right <= layout.view, `${width}px ${theme} rows fit the window: ${JSON.stringify(layout)}`);
      }
    }

    // The queue leaves one prompt at a time, when the session is idle, each as a normal Send.
    await evaluate("fixture.finishRun()");
    assert.equal(await wait("fixture.sendCalls.length===2 && fixture.rows()[0]==='Queue:waiting:1:second'"), true);
    assert.deepEqual(await rows(), ["Queue:waiting:1:second", "Queue:waiting:1:third"], "a repeated prompt keeps its place for its next send");
    await evaluate("fixture.finishRun()");
    assert.equal(await wait("fixture.sendCalls.length===3 && fixture.rows().length===1"), true);
    await evaluate("fixture.finishRun()");
    assert.equal(await wait("fixture.sendCalls.length===4 && fixture.rows().length===0"), true);
    assert.deepEqual(await sends(), ["first", "second", "second", "third"]);
    assert.equal(await evaluate("fixture.sendCalls.every(call=>call.selection===null||call.selection.providerKey==='fixture-provider')"), true);
    assert.equal(await evaluate("!!document.querySelector('.composer-queue-strip')"), false);
    await evaluate("fixture.finishRun()");
    assert.equal(await wait(idle), true);

    // The host says the session is busy although it looked idle: the prompt joins the queue and leaves later.
    await evaluate("fixture.sendMode('busy')");
    await write("refused"); await enter();
    assert.equal(await wait("fixture.rows()[0]==='Queue:waiting:1:refused' && promptText()===''"), true);
    assert.equal(await evaluate("fixture.echoes().some(echo=>echo.startsWith('failed'))||document.querySelectorAll('.bp6-toast').length>0"), false);
    await evaluate("fixture.sendMode('accept')");
    assert.equal(await wait("fixture.rows().length===0 && fixture.sendCalls.at(-1).text==='refused' && fixture.echoes().includes('accepted:refused')"), true);
    await evaluate("fixture.finishRun()");
    assert.equal(await wait(idle), true);

    // Another refusal keeps the prompt in the composer and says why in the status line, without a toast.
    await evaluate("fixture.sendMode('refuse')");
    await write("not taken"); await enter();
    assert.equal(await wait("document.querySelector('.composer-status-line').textContent.includes('was not accepted')"), true);
    assert.equal(await evaluate("promptText()==='not taken' && fixture.rows().length===0 && document.querySelectorAll('.bp6-toast').length===0"), true);
    await evaluate("fixture.sendMode('accept')");
    await command("Input.insertText", { text: " yet" });
    assert.equal(await wait("!document.querySelector('.composer-status-line').textContent.includes('was not accepted')"), true, "editing clears the notice");

    // Ctrl+Enter without a running turn has nothing to steer: it is a Send.
    const before = (await sends()).length;
    await enter(true);
    assert.equal(await wait(`fixture.sendCalls.length===${before + 1} && ${running} && promptText()===''`), true);
    assert.deepEqual([(await sends()).at(-1), await steers()], ["not taken yet", []]);

    // Ctrl+Enter during a turn steers it. The row stays until the agent takes the prompt up.
    await write("steer live"); await enter(true);
    assert.equal(await wait("fixture.steerCalls.length===1 && fixture.rows()[0]==='Steer:delivering:1:steer live' && promptText()===''"), true);
    assert.equal(await evaluate("fixture.steerCalls[0].expectedRunId===fixture.run() && fixture.steerCalls[0].expectedAttachmentGeneration==='12'"), true);
    assert.equal(await evaluate(`${row("steer live")}.querySelector('.composer-queue-status').textContent`), "Steer pending");
    assert.equal(await evaluate(`!${row("steer live")}.querySelector('[aria-label="Repeat count"]') && !${row("steer live")}.querySelector('[aria-label="Edit queued prompt"]')`), true);

    // Steering is listed first. An empty Ctrl+Enter steers with the first queued prompt.
    await write("queued"); await enter();
    assert.equal(await wait("fixture.rows().length===2"), true);
    assert.deepEqual(await rows(), ["Steer:delivering:1:steer live", "Queue:waiting:1:queued"]);
    await evaluate("document.querySelector('#session-prompt').focus()");
    await enter(true);
    assert.equal(await wait("fixture.steerCalls.length===2 && fixture.rows().every(row=>row.startsWith('Steer:delivering'))"), true);
    assert.deepEqual(await steers(), ["steer live", "queued"]);

    // The Steer now button of a queued row does the same for that row.
    await write("later"); await enter();
    assert.equal(await wait(`!!${row("later")}`), true);
    await evaluate(`${row("later")}.querySelector('[aria-label="Steer now"]').click()`);
    assert.equal(await wait("fixture.steerCalls.length===3 && fixture.rows().length===3 && fixture.rows().every(row=>row.startsWith('Steer:delivering'))"), true);

    // Steering the turn cannot take becomes the next queued prompt.
    await evaluate("fixture.steerMode('refuse')");
    await write("cannot steer"); await enter(true);
    assert.equal(await wait("fixture.steerCalls.length===4 && fixture.rows().at(-1)==='Queue:waiting:1:cannot steer'"), true);
    // Steering the host ran earlier, and keeps no receipt of, is not sent a second time as the next prompt.
    await evaluate("fixture.steerMode('expired')");
    await write("already delivered"); await enter(true);
    assert.equal(await wait("fixture.steerCalls.length===5 && fixture.rows().includes('Steer:failed:1:already delivered')"), true);
    assert.equal(await evaluate("fixture.rows().filter(row=>row.endsWith(':already delivered')).length"), 1);
    await evaluate(`${row("already delivered")}.querySelector('.composer-queue-delete').click()`);
    assert.equal(await wait("fixture.rows().length===4 && fixture.rows().at(-1)==='Queue:waiting:1:cannot steer'"), true);
    await evaluate("fixture.steerMode('accept')");

    // A sent steering prompt can be taken off the list; the others leave with their turn, and the queue goes on.
    await evaluate(`${row("steer live")}.querySelector('.composer-queue-delete').click()`);
    assert.equal(await wait("fixture.rows().length===3"), true);
    const sent = (await sends()).length;
    await evaluate("fixture.finishRun()");
    assert.equal(await wait(`fixture.sendCalls.length===${sent + 1} && fixture.rows().length===0`), true);
    assert.equal((await sends()).at(-1), "cannot steer");

    // Deleting a queued prompt, and clearing the queue.
    assert.equal(await wait(running), true);
    for (const text of ["one", "two", "three"]) { await write(text); await enter(); assert.equal(await wait(`!!${row(text)}`), true); }
    await evaluate(`${row("two")}.querySelector('.composer-queue-delete').click()`);
    assert.equal(await wait("fixture.rows().length===2"), true);
    assert.deepEqual(await rows(), ["Queue:waiting:1:one", "Queue:waiting:1:three"]);
    await evaluate("document.querySelector('.composer-queue-clear').click()");
    assert.equal(await wait("fixture.rows().length===0 && !document.querySelector('.composer-queue-strip')"), true);
    const total = (await sends()).length;
    await evaluate("fixture.finishRun()");
    assert.equal(await wait(idle), true);
    assert.equal((await sends()).length, total, "a cleared queue sends nothing");

    assert.equal(await evaluate("document.querySelectorAll('.bp6-toast').length"), 0);
    assert.deepEqual(exceptions, []);
  } finally {
    // Edge's launcher can exit while the browser it started goes on: the browser itself is asked to close.
    if (socket?.readyState === WebSocket.OPEN) socket.send(JSON.stringify({ id: 9999, method: "Browser.close" }));
    socket?.close(); browser?.kill(); await rm(root, { recursive: true, force: true, maxRetries: 8, retryDelay: 100 });
  }
});
