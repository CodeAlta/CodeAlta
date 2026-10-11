import assert from "node:assert/strict";
import test from "node:test";
import { mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";
import { spawn } from "node:child_process";
import { build } from "esbuild";
import { browserExecutable, browserBaseArgs, waitForBrowserPort } from "./browserTarget";

test("the real shell publishes hidden-space/draft IDs and refuses to mount a saved editor before protection acknowledgement", { skip: !browserExecutable, timeout: 60_000 }, async () => {
  const root = await mkdtemp(join(tmpdir(), "codealta-retention-"));
  let browser: ReturnType<typeof spawn> | undefined, socket: WebSocket | undefined;
  try {
    await build({ entryPoints: [fileURLToPath(new URL("./main.tsx", import.meta.url))], outfile: join(root, "fixture.js"),
      bundle: true, platform: "browser", format: "iife", loader: { ".css": "empty", ".flf": "text", ".svg": "dataurl" },
      define: { "import.meta.env.VITE_DEMO_MODE": '"false"' }, plugins: [{ name: "isolated-retention", setup(bundle) {
        bundle.onResolve({ filter: /^#neoastra$/ }, () => ({ path: fileURLToPath(new URL("./sessionRetention.neoastra.mount.ts", import.meta.url)) }));
        bundle.onResolve({ filter: /^\.\/sessionRetentionRpc$/ }, () => ({ path: "retention", namespace: "fixture-retention" }));
        bundle.onLoad({ filter: /.*/, namespace: "fixture-retention" }, () => ({ loader: "js", contents: `
          const requests = [], pending = [];
          let accepted = null;
           let acknowledgeLifetime, closeLifetime;
           window.retentionFixture = { requests, lifetimeStarted: false, acknowledgeLifetime() { acknowledgeLifetime(); }, closeLifetime() { closeLifetime(); }, hold() { accepted = null; }, settle(value) { accepted = value;
            for (const resolve of pending.splice(0)) resolve({ accepted: value, reason: value ? null : 'retiring' }); } };
          export function publishSessionRetention(request) { requests.push(request);
            return accepted === null ? new Promise(resolve => pending.push(resolve))
              : Promise.resolve({ accepted, reason: accepted ? null : 'retiring' }); }
           export async function* watchSessionRetention(request, signal) {
             window.retentionFixture.lifetimeStarted = true;
             await new Promise(resolve => { acknowledgeLifetime = resolve; });
             if (signal.aborted) return;
             yield { accepted: true, reason: null };
             await new Promise(resolve => { closeLifetime = resolve; signal.addEventListener('abort', resolve, { once: true }); });
           }
        ` }));
        bundle.onResolve({ filter: /^monaco-editor\/.*\?worker$/ }, args => ({ path: args.path, namespace: "fixture-worker" }));
        bundle.onLoad({ filter: /.*/, namespace: "fixture-worker" }, () => ({ loader: "js", contents: "export default class extends Worker { constructor() { super(URL.createObjectURL(new Blob(['']))); } }" }));
      } }] });
    const page = join(root, "fixture.html");
    await writeFile(join(root, "style.css"), await readFile(new URL("../node_modules/flexlayout-react/style/light.css", import.meta.url), "utf8")
      + await readFile(new URL("./style.css", import.meta.url), "utf8"));
    await writeFile(page, '<!doctype html><html><head><link rel="stylesheet" href="style.css"></head><body><div id="root"></div><script src="fixture.js"></script></body></html>');
    browser = spawn(browserExecutable!, [...browserBaseArgs, "--allow-file-access-from-files", `--user-data-dir=${join(root, "profile")}`,
      "--remote-debugging-port=0", "about:blank"], { stdio: ["ignore", "ignore", "pipe"], windowsHide: true });
    const port = await waitForBrowserPort(join(root, "profile"), browser);
    const pages = await (await fetch(`http://127.0.0.1:${port}/json/list`)).json() as { type?: string; webSocketDebuggerUrl?: string }[];
    socket = new WebSocket(pages.find(value => value.type === "page")!.webSocketDebuggerUrl!);
    await new Promise<void>((resolve, reject) => { socket!.addEventListener("open", () => resolve(), { once: true }); socket!.addEventListener("error", reject, { once: true }); });
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
      assert.equal(value.exceptionDetails, undefined); return value.result?.value;
    };
    const wait = (condition: string) => evaluate(`new Promise(resolve=>{const end=Date.now()+7000;const tick=()=>{
      if(${condition})resolve(true);else if(Date.now()>end)resolve(false);else setTimeout(tick,20)};tick()})`);
    // FlexLayout selects tabs on a pointer gesture, not HTMLElement.click(). Exercise the same
    // input sequence as a user, including reselecting an already active tab while admission waits.
    const click = async (element: string) => {
      const point = await evaluate(`(() => { const rect = (${element}).getBoundingClientRect();
        return { x: rect.left + rect.width / 2, y: rect.top + rect.height / 2 }; })()`);
      await command("Input.dispatchMouseEvent", { type: "mousePressed", ...point, button: "left", clickCount: 1 });
      await command("Input.dispatchMouseEvent", { type: "mouseReleased", ...point, button: "left", clickCount: 1 });
    };
    await command("Page.enable");
    await command("Page.addScriptToEvaluateOnNewDocument", { source: `
      localStorage.setItem('settingsFixtureOwned', 'true');
      localStorage.setItem('settingsFixtureSecondProject', 'true');
      const open = [
        { sessionId: 'one', projectId: 'project', path: '/fixture/project' }
      ];
      localStorage.setItem('codealta.desktop.sessionTabs.v1', JSON.stringify({ version: 1, open, active: open[0] }));
      localStorage.setItem('codealta.desktop.prompt.offscreen', 'draft');
      localStorage.setItem('codealta.desktop.sessionTabs.v1.hidden', JSON.stringify({ version: 1,
        open: [{ sessionId: 'hidden-session', projectId: null, path: null }], active: null }));
    ` });
    await command("Page.navigate", { url: pathToFileURL(page).href });
    assert.equal(await wait("window.retentionFixture?.lifetimeStarted"), true);
    assert.equal(await evaluate("retentionFixture.requests.length"), 0, "opening the lifetime channel is not its first-item acknowledgement");
    assert.equal(await evaluate("!!document.querySelector('#session-prompt')"), false);
    await evaluate("retentionFixture.acknowledgeLifetime()");
    assert.equal(await wait("window.retentionFixture?.requests.length > 0"), true,
      await evaluate("JSON.stringify({ requests: window.retentionFixture?.requests, body: document.body.innerText.slice(0,3000) })"));
    assert.equal(await evaluate("!!document.querySelector('#session-prompt')"), false);
    assert.equal(await evaluate("[...document.querySelectorAll('.session-dock [data-session-node]')].some(node=>node.textContent.startsWith('one -'))"), false, "even restored saved-tab labels wait for acknowledgement");
    const first = await evaluate("retentionFixture.requests[0]");
    assert.ok(first.sessionIds.includes("offscreen")); assert.ok(first.sessionIds.includes("hidden-session"));
    assert.equal(first.ready, false);
    await evaluate("retentionFixture.settle(true)");
    assert.equal(await wait("!!document.querySelector('#session-prompt')"), true);
    assert.equal(await evaluate("retentionFixture.requests.some(request=>request.ready===true && request.sessionIds.includes('one') && request.sessionIds.includes('hidden-session') && request.sessionIds.includes('offscreen'))"), true);
    assert.equal(await evaluate("retentionFixture.requests.every((request,index,all)=>!index || request.revision>all[index-1].revision)"), true);
    const tab = (id: string) => `[...document.querySelectorAll('.session-dock [data-session-node]')].find(node=>node.textContent.startsWith('${id} -')).closest('[role=tab]')`;
    const selected = (id: string) => `JSON.parse(localStorage.getItem('codealta.desktop.sessionTabs.v1')).active?.sessionId === '${id}'`;
    const row = (id: string) => `document.querySelector('.session-row[data-session-id="${id}"] > button')`;
    await evaluate(`retentionFixture.hold(); ${row("two")}.click()`);
    assert.equal(await wait("retentionFixture.requests.at(-1).sessionIds.includes('two')"), true);
    assert.equal(await evaluate(selected("one")), true, "a held row admission does not change selection");
    assert.equal(await evaluate("[...document.querySelectorAll('.session-dock [data-session-node]')].some(node=>node.textContent.startsWith('two -'))"), false);
    await click(tab("one"));
    await evaluate(`retentionFixture.settle(true);
      new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(resolve)))`);
    assert.equal(await evaluate(selected("one")), true, "reselecting the current tab cancels a pending activation");
    await evaluate(`retentionFixture.hold(); ${row("two")}.click()`);
    await evaluate("retentionFixture.settle(false); new Promise(resolve=>setTimeout(resolve,100))");
    assert.equal(await evaluate(selected("one")), true, "retirement rejection cannot expose a row, tab or editor");
    await evaluate("retentionFixture.hold(); document.querySelector('.session-row[data-session-id=two] .session-actions-trigger').click()");
    assert.equal(await wait("!!document.querySelector('.session-tab-popup')"), true);
    await evaluate("[...document.querySelectorAll('.session-tab-popup [role=menuitem]')].find(item=>item.textContent.trim()==='Open session').click()");
    await evaluate("new Promise(resolve=>setTimeout(resolve,100))");
    assert.equal(await evaluate(selected("one")), true, "menu admission also waits before changing selection");
    await evaluate("retentionFixture.settle(true)");
    assert.equal(await wait(selected("two")), true);
    await click(tab("one"));
    assert.equal(await wait(selected("one")), true);
    // A space written after startup is not in the initial acknowledged set. The external show callback
    // must claim its restored tabs before changing either the active space or session shell state.
    await evaluate(`localStorage.setItem('codealta.desktop.sessionTabs.v1.work', JSON.stringify({version:1,
      open:[{sessionId:'other-session',projectId:'other',path:'/fixture/other'}],
      active:{sessionId:'other-session',projectId:'other',path:'/fixture/other'}}));
      retentionFixture.hold(); spacesFixture.tell({kind:'show',spaceId:'work'})`);
    assert.equal(await wait("retentionFixture.requests.at(-1).sessionIds.includes('other-session')"), true);
    assert.notEqual(await evaluate("spacesFixture.shown.at(-1)"), "work", "an external space request cannot expose unacknowledged restored tabs");
    await evaluate("spacesFixture.tell({kind:'show',spaceId:'default'}); new Promise(resolve=>setTimeout(resolve,50))");
    await evaluate("retentionFixture.settle(true)");
    await evaluate("new Promise(resolve=>setTimeout(resolve,100))");
    assert.notEqual(await evaluate("spacesFixture.shown.at(-1)"), "work", "the later external selection fences the held space callback");
    await evaluate("spacesFixture.tell({kind:'show',spaceId:'work'})");
    assert.equal(await wait("spacesFixture.shown.at(-1)==='work' && !!document.querySelector('#session-prompt')"), true);
    await evaluate("spacesFixture.tell({kind:'show',spaceId:'default'})");
    assert.equal(await wait(selected("one")), true);
    assert.equal(await evaluate(`(() => {
      const write = Storage.prototype.setItem;
      Storage.prototype.setItem = function(key, value) { if (key.startsWith('codealta.desktop.prompt.')) throw Error('fixture storage denied'); return write.call(this, key, value); };
      const request = { kind: 'draft', sessionId: 'one', text: 'memory-only draft', handled: false, result: false };
      window.dispatchEvent(new CustomEvent('codealta:plugin', { detail: request })); return request.result;
    })()`), true);
    assert.equal(await evaluate(`(() => {
      const request = { kind: 'enqueue', sessionId: 'one', text: 'deferred with selection', agentPromptId: 'plan', handled: false, result: false };
      window.dispatchEvent(new CustomEvent('codealta:plugin', { detail: request }));
      window.queuedCompletion = null;
      request.completion?.then(value => { window.queuedCompletion = value; });
      return request.result;
    })()`), true);
    assert.equal(await evaluate("retentionPlugins.sends.length"), 0, "deferred Send waits for an observed idle session");
    await evaluate("retentionPlugins.emit({kind:'prompt',requestId:'accepted',sessionId:'one',mode:'send',text:'held admission'})");
    assert.equal(await wait("retentionPlugins.delivered.includes('accepted')"), true);
    assert.equal(await evaluate("retentionPlugins.sends.length"), 0, "plugin Send cannot overtake the existing waiting row");
    await evaluate("retentionPlugins.allowRuntime()");
    assert.equal(await wait("retentionPlugins.sends.length===1"), true);
    assert.equal(await evaluate("retentionPlugins.sends[0].request.selection?.agentPromptId"), "plan",
      "enqueue of an unattached saved session remains a deferred Send with its requested agent prompt");
    assert.equal(await evaluate("window.queuedCompletion"), null, "local queue insertion is not downstream admission");
    await evaluate("retentionPlugins.accept(0, true)");
    assert.equal(await wait("window.queuedCompletion===true"), true);
    assert.equal(await wait("retentionPlugins.sends.length===2"), true);
    await evaluate("retentionPlugins.emit({kind:'close',requestId:'closed'}); retentionPlugins.emit({kind:'prompt',requestId:'closed',sessionId:'one',mode:'send',text:'must not run'})");
    await evaluate("new Promise(resolve=>setTimeout(resolve,100))");
    assert.equal(await evaluate("retentionPlugins.answers.some(answer=>answer.requestId==='closed')"), false,
      "an unknown close could belong to an earlier page's captured worker; do not falsely release it");
    assert.equal(await evaluate("retentionPlugins.sends.length"), 2, "closed-before-delivery cannot launch work");
    assert.equal(await evaluate("retentionPlugins.answers.some(answer=>answer.requestId==='accepted')"), false,
      "the mounted PluginUiHost cannot acknowledge React dispatch before the held downstream command");
    await evaluate("retentionPlugins.emit({kind:'prompt',requestId:'uncertain',sessionId:'two',mode:'send',text:'cancelled waiter'})");
    assert.equal(await wait("retentionPlugins.sends.length===3"), true);
    assert.equal(await evaluate("retentionPlugins.answers.length"), 0, "a held first admission does not block the second delivery");
    await evaluate("retentionPlugins.emit({kind:'close',requestId:'uncertain'})");
    assert.equal(await wait("retentionPlugins.sends[2].signal.aborted"), true, "close is consumed while the RPC is held");
    await evaluate("retentionPlugins.accept(1, true)");
    assert.equal(await wait("retentionPlugins.answers.some(answer=>answer.requestId==='accepted' && !answer.cancelled)"), true);
    await evaluate("retentionPlugins.accept(2); new Promise(resolve=>setTimeout(resolve,100))");
    assert.equal(await evaluate("retentionPlugins.answers.some(answer=>answer.requestId==='uncertain')"), false,
      "a canceled waiter cannot conclusively release the host's original handoff claim");
    await evaluate("retentionPlugins.emit({kind:'prompt',requestId:'busy-race',sessionId:'one',mode:'send',text:'became busy'})");
    assert.equal(await wait("retentionPlugins.sends.length===4"), true);
    await evaluate("retentionPlugins.busy(3)");
    assert.equal(await wait("retentionPlugins.sends.length===5"), true, "an idle-to-busy refusal becomes a deferred Send");
    assert.equal(await evaluate("retentionPlugins.answers.some(answer=>answer.requestId==='busy-race')"), false);
    assert.notEqual(await evaluate("retentionPlugins.sends[3].request.clientRequestId"), await evaluate("retentionPlugins.sends[4].request.clientRequestId"));
    await evaluate("retentionPlugins.accept(4, true)");
    assert.equal(await wait("retentionPlugins.answers.some(answer=>answer.requestId==='busy-race' && !answer.cancelled)"), true);
    await click(tab("one"));
    assert.equal(await wait(selected("one")), true);
    await evaluate("window.retainedEditor = document.querySelector('#session-prompt'); window.dispatchEvent(new PageTransitionEvent('pagehide'))");
    assert.equal(await wait("!!document.querySelector('#session-prompt')?.closest('[inert]')"), true, "page invalidation makes the admitted editor inert");
    assert.equal(await evaluate("window.retainedEditor === document.querySelector('#session-prompt')"), true,
      "transport invalidation must not destroy an editor whose draft could not be persisted");
    assert.equal(await evaluate(`(() => { const request = { kind: 'draft', sessionId: 'one', text: 'refused', handled: false, result: false };
      window.dispatchEvent(new CustomEvent('codealta:plugin', { detail: request })); return request.result; })()`), false);
    assert.equal(await evaluate(`(() => { const request = { kind: 'state', sessionId: 'one', handled: false, result: false };
      window.dispatchEvent(new CustomEvent('codealta:plugin', { detail: request })); return request.state?.draftText; })()`), "memory-only draft");
    await command("Page.reload");
    assert.equal(await wait("window.retentionFixture?.lifetimeStarted"), true);
    await evaluate("retentionFixture.acknowledgeLifetime()");
    assert.equal(await wait("window.retentionFixture?.requests.length > 0"), true);
    assert.notEqual(await evaluate("retentionFixture.requests[0].pageId"), first.pageId, "reload never reuses a persisted page identity");
    assert.equal(await wait("!!document.querySelector('button[data-scope=project]')"), true);
    await evaluate("document.querySelector('button[data-scope=project]').click()");
    assert.equal(await wait(`!!${row("two")}`), true);
    await evaluate(`${row("two")}.click(); retentionFixture.settle(true)`);
    assert.equal(await wait(selected("two")), true, "late initial restoration must not overwrite a newer row selection");
    await command("Page.reload");
    assert.equal(await wait("window.retentionFixture?.lifetimeStarted"), true);
    await evaluate("retentionFixture.acknowledgeLifetime()");
    assert.equal(await wait("retentionFixture.requests.length > 0"), true);
    await evaluate("retentionFixture.closeLifetime(); new Promise(resolve=>setTimeout(resolve,50))");
    await evaluate("retentionFixture.settle(true); new Promise(resolve=>setTimeout(resolve,100))");
    assert.equal(await evaluate("!!document.querySelector('#session-prompt')"), false,
      "lifetime closure revokes held publication before its late acknowledgement can expose an editor");
  } finally {
    socket?.close(); browser?.kill();
    await rm(root, { recursive: true, force: true, maxRetries: 20, retryDelay: 100 });
  }
});
