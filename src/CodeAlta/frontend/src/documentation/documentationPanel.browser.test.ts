import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";
import { build } from "esbuild";
import { browserBaseArgs, browserExecutable } from "../browserTarget";

const edge = browserExecutable;

test("the Documentation tab shows the guide under the policy of the application: navigation, links, headings, pictures, search, a question, and a narrow pane",
  { skip: !edge, timeout: 240_000 }, async () => {
  const directory = await mkdtemp(join(tmpdir(), "codealta-documentation-"));
  let browser: ReturnType<typeof spawn> | undefined, socket: WebSocket | undefined;
  try {
    const config = JSON.parse(await readFile(new URL("../../../neoastra.json", import.meta.url), "utf8")) as { assets: { csp: string } };
    const bundle = await build({ entryPoints: [fileURLToPath(new URL("./documentationPanel.mount.tsx", import.meta.url))], bundle: true, platform: "browser", format: "iife", write: false });
    const style = await readFile(new URL("../../node_modules/@blueprintjs/core/lib/css/blueprint.css", import.meta.url), "utf8")
      + await readFile(new URL("../../node_modules/flexlayout-react/style/dark.css", import.meta.url), "utf8")
      + await readFile(new URL("../style.css", import.meta.url), "utf8") + await readFile(new URL("./documentation.css", import.meta.url), "utf8");
    const origin = "https://documentation-production.invalid", page = origin + "/index.html";
    const assets = new Map([
      [page, '<!doctype html><html data-theme="dark"><head><meta charset="utf-8"><link rel="icon" href="data:,"><link rel="stylesheet" href="/style.css"></head>'
        + '<body class="bp6-dark" style="margin:0"><div class="ide-shell"><div id="root" style="height:760px"></div></div><script src="/fixture.js"></script></body></html>'],
      [origin + "/fixture.js", bundle.outputFiles[0].text], [origin + "/style.css", style]]);
    const types = new Map([[page, "text/html"], [origin + "/fixture.js", "text/javascript"], [origin + "/style.css", "text/css"]]);
    browser = spawn(edge!, [...browserBaseArgs, "--host-resolver-rules=MAP * ~NOTFOUND", `--user-data-dir=${join(directory, "profile")}`, "--remote-debugging-port=0", "--window-size=1500,950", "about:blank"],
      { stdio: "ignore", windowsHide: true });
    let port = "";
    for (let attempt = 0; attempt < 100 && !port; attempt++) {
      try { port = (await readFile(join(directory, "profile", "DevToolsActivePort"), "utf8")).split(/\r?\n/)[0]; }
      catch { await new Promise(resolve => setTimeout(resolve, 50)); }
    }
    assert.match(port, /^\d+$/);
    // Loopback is the control channel of the test alone, never an asset of the page.
    const targets = await (await fetch(`http://127.0.0.1:${port}/json/list`, { signal: AbortSignal.timeout(10_000) })).json() as { type: string; webSocketDebuggerUrl: string }[];
    socket = new WebSocket(targets.find(target => target.type === "page")!.webSocketDebuggerUrl);
    await new Promise<void>((resolve, reject) => { socket!.addEventListener("open", () => resolve(), { once: true }); socket!.addEventListener("error", () => reject(Error("Browser unavailable")), { once: true }); });
    let sequence = 0;
    const pending = new Map<number, (reply: { result?: any; error?: unknown }) => void>();
    const command = (method: string, params: object = {}) => new Promise<any>((resolve, reject) => {
      const id = ++sequence;
      const timer = setTimeout(() => { pending.delete(id); reject(Error(`${method} timed out`)); }, 30_000);
      pending.set(id, reply => { clearTimeout(timer); if (reply.error) reject(Error(JSON.stringify(reply.error))); else resolve(reply.result ?? {}); });
      socket!.send(JSON.stringify({ id, method, params }));
    });
    // Everything the page asks the network for is seen here: only its own three files are answered.
    const refused: string[] = [], failures: string[] = [];
    socket.addEventListener("message", event => {
      const message = JSON.parse(String(event.data));
      if (pending.has(message.id)) { pending.get(message.id)!(message); pending.delete(message.id); }
      if (message.method === "Fetch.requestPaused") {
        const { requestId, request } = message.params;
        const body = assets.get(request.url);
        const action = body === undefined
          ? (refused.push(request.url), command("Fetch.failRequest", { requestId, errorReason: "BlockedByClient" }))
          : command("Fetch.fulfillRequest", { requestId, responseCode: 200, responseHeaders: [{ name: "Content-Type", value: types.get(request.url)! },
            { name: "Content-Security-Policy", value: config.assets.csp }], body: Buffer.from(body).toString("base64") });
        void action.catch(error => failures.push(String(error)));
      }
      if (["Page.windowOpen", "Page.frameAttached", "Runtime.exceptionThrown"].includes(message.method)) failures.push(JSON.stringify(message).slice(0, 600));
      if (message.method === "Page.frameNavigated" && message.params.frame.url !== page) failures.push(`navigated: ${message.params.frame.url}`);
      if (message.method === "Log.entryAdded" && message.params.entry.source === "security") failures.push(String(message.params.entry.text));
    });
    await command("Page.enable"); await command("Runtime.enable"); await command("Network.enable"); await command("Log.enable");
    await command("Fetch.enable", { patterns: [{ urlPattern: "*", requestStage: "Request" }] });
    await command("Page.navigate", { url: page });
    const evaluate = async <T,>(expression: string): Promise<T> => {
      const reply = await command("Runtime.evaluate", { expression, returnByValue: true, awaitPromise: true });
      assert.equal(reply.exceptionDetails, undefined, JSON.stringify(reply.exceptionDetails));
      return reply.result?.value as T;
    };
    const wait = (expression: string, milliseconds = 8_000) => evaluate<boolean>(
      `new Promise(resolve=>{const end=Date.now()+${milliseconds};const tick=()=>{let ok=false;try{ok=!!(${expression})}catch{}ok?resolve(true):Date.now()>end?resolve(false):setTimeout(tick,25)};tick()})`);
    const expect = async (expression: string, message?: string) => assert.equal(await wait(expression), true, message ?? expression);
    // A click of the user, which is what a link of rendered Markdown follows.
    const click = async (selector: string) => {
      const box = await evaluate<{ x: number; y: number } | null>(`(() => { const node = ${selector}; if (!node) return null; node.scrollIntoView({ block: 'center' });
        const box = node.getBoundingClientRect(); return { x: box.x + Math.min(box.width / 2, 40), y: box.y + box.height / 2 }; })()`);
      assert.ok(box, `nothing to click: ${selector}`);
      for (const type of ["mousePressed", "mouseReleased"]) await command("Input.dispatchMouseEvent", { type, button: "left", x: box.x, y: box.y, clickCount: 1 });
    };
    const press = async (key: string, code: string, virtualKey: number, modifiers = 0) => {
      await command("Input.dispatchKeyEvent", { type: "rawKeyDown", key, code, windowsVirtualKeyCode: virtualKey, modifiers });
      await command("Input.dispatchKeyEvent", { type: "keyUp", key, code, windowsVirtualKeyCode: virtualKey, modifiers });
    };
    const capture = async (name: string) => {
      if (!process.env.DOCUMENTATION_SHOTS) return;
      const shot = await command("Page.captureScreenshot", { format: "png" });
      await writeFile(join(process.env.DOCUMENTATION_SHOTS, `${name}.png`), Buffer.from(shot.data, "base64"));
    };
    const link = (text: string) => `[...document.querySelectorAll('.documentation-article a')].find(node => node.textContent === ${JSON.stringify(text)})`;
    const nav = (text: string) => `[...document.querySelectorAll('.documentation-menu [data-doc-nav]')].find(node => node.textContent === ${JSON.stringify(text)})`;
    const current = "document.querySelector('.documentation-menu [aria-current=\"page\"]')?.textContent";
    const title = "document.querySelector('.documentation-article h1')?.textContent";
    const scrollTop = "document.querySelector('.documentation-scroll').scrollTop";
    await expect("window.documentationFixture");

    // The tab opens on the first page of the guide, with the navigation of the menu and the page marked in it.
    await evaluate("documentationFixture.mount()");
    await expect(`${title} === 'User Guide'`);
    assert.deepEqual(await evaluate("[...document.querySelectorAll('.documentation-menu > ul > li > .documentation-menu-row [data-doc-nav]')].map(node => node.textContent)"), ["User Guide", "Sessions", "Plugins"]);
    assert.equal(await evaluate(current), "User Guide");
    assert.equal(await evaluate("document.querySelectorAll('.documentation-menu [data-doc-nav][tabindex=\"0\"]').length"), 1, "The navigation is one stop of the Tab key.");
    assert.equal(await evaluate("document.querySelector('.documentation-menu ul ul')"), null, "The folder of the plugins is closed until it is opened.");
    assert.deepEqual(await evaluate("[...document.querySelectorAll('.documentation-trail li')].map(node => node.textContent)"), ["User Guide"]);
    assert.equal(await evaluate("documentationFixture.state.calls.menu"), 1, "The effects of StrictMode read the navigation once.");
    assert.deepEqual(await evaluate("documentationFixture.state.calls.page"), ["readme.md"]);

    // What a page writes goes through the boundary of the window: nothing of it runs, loads or frames anything.
    assert.equal(await evaluate("document.querySelectorAll('.documentation-text img, .documentation-article script, .documentation-article iframe').length"), 0);
    assert.equal(await evaluate(`${link("script link")}.hasAttribute('href')`), false);
    assert.equal(await evaluate("document.querySelectorAll('.documentation-text table tbody tr').length"), 1);

    // The headings take the addresses the site gives them, and the outline lists the ones of the page.
    assert.deepEqual(await evaluate("[...document.querySelectorAll('.documentation-article [data-doc-anchor]')].map(node => node.getAttribute('data-doc-anchor'))"),
      ["user-guide", "start-here", "start-here-1", "details-and-more"]);
    assert.deepEqual(await evaluate("[...document.querySelectorAll('.documentation-outline li')].map(node => [node.dataset.level, node.textContent])"),
      [["2", "Start here"], ["2", "Start here"], ["3", "Details, and more"]]);
    assert.equal(await evaluate("document.querySelector('.documentation-article [id]')"), null, "A heading takes no id of the document.");

    // A picture keeps its place before it is read, then shows the file the host gave; a drawing is an image, not markup.
    assert.equal(await evaluate("getComputedStyle(document.querySelector('.documentation-figure-frame')).aspectRatio"), "1280 / 800");
    await expect("document.querySelector('.documentation-figure img[alt=\"The workspace\"]')?.src.startsWith('data:image/png;base64,')");
    await expect("document.querySelector('.documentation-figure img[alt=\"The workspace\"]').naturalWidth === 1");
    assert.equal(await evaluate("document.querySelector('.documentation-figure figcaption').textContent.trim()"), "The main workspace, see Git.");
    await expect("document.querySelector('.documentation-figure img[alt=\"Prompt flow\"]')?.src.startsWith('data:image/svg+xml')");
    await expect("document.querySelector('.documentation-figure img[alt=\"Prompt flow\"]').naturalWidth > 0", "The drawing is drawn.");
    assert.equal(await evaluate("getComputedStyle(document.querySelectorAll('.documentation-figure-frame')[1]).aspectRatio"), "960 / 430");
    assert.equal(await evaluate("document.querySelector('.documentation-article svg rect')"), null);
    // A picture the guide does not have says so.
    await expect("document.querySelectorAll('.documentation-figure')[2].dataset.state === 'failed'");
    assert.equal(await evaluate("document.querySelector('.documentation-figure-missing').getAttribute('aria-label')"), "Not there");
    assert.deepEqual(await evaluate("documentationFixture.state.calls.image.slice().sort()"), ["alta-desktop-home.webp", "missing.webp"]);
    await capture("guide-wide-dark");

    // A picture is enlarged over the tab, and Escape closes it.
    await click("document.querySelector('.documentation-figure-zoom')");
    await expect("document.querySelector('.documentation-zoom img')?.alt === 'The workspace'");
    await expect("document.activeElement === document.querySelector('.documentation-zoom')");
    await press("Escape", "Escape", 27);
    await expect("!document.querySelector('.documentation-zoom') && document.activeElement.matches('.documentation-figure-zoom')", "The picture takes the keyboard back.");

    // A page of the web goes to the opener of the window; the tab itself never leaves its page.
    await click(link("the web"));
    await expect("documentationFixture.state.web.length === 1");
    assert.deepEqual(await evaluate("documentationFixture.state.web"), ["https://example.com/guide"]);
    // What is no page of the guide is not followed, and no file is asked of the host: a path that leaves the guide, an address of a file, a page that is not there.
    for (const text of ["outside", "a file", "nowhere"]) await click(link(text));
    await expect("documentationFixture.state.notices.length === 3");
    assert.deepEqual(await evaluate("[...new Set(documentationFixture.state.notices)]"), ["This link leads outside the guide."]);
    assert.deepEqual(await evaluate("documentationFixture.state.calls.page"), ["readme.md"]);
    assert.equal(await evaluate("documentationFixture.state.web.length"), 1);

    // A link to a heading of another page opens that page at the heading, and the navigation follows.
    await click(link("Sessions"));
    await expect(`${title} === 'Sessions'`);
    assert.equal(await evaluate(current), "Sessions");
    const queueTop = "document.querySelector('[data-doc-anchor=\"queue\"]').getBoundingClientRect().top - document.querySelector('.documentation-scroll').getBoundingClientRect().top";
    await expect(`${scrollTop} > 400 && Math.abs(${queueTop} - 12) < 3`, "The heading that was asked for is at the top of the page.");
    await expect("document.querySelector('.documentation-outline li[data-active]')?.textContent === 'Queue'");
    await expect("document.activeElement === document.querySelector('.documentation-scroll')", "The keys scroll the page that was opened.");
    // The outline goes to a heading; Back and Forward go through the pages that were read.
    await click("document.querySelector('.documentation-bar button[aria-label=\"Go back\"]')");
    await expect(`${title} === 'User Guide'`);
    assert.equal(await evaluate("document.querySelector('.documentation-bar button[aria-label=\"Go forward\"]').disabled"), false);
    await evaluate("document.querySelector('.documentation-scroll').focus()");
    await press("ArrowRight", "ArrowRight", 39, 1);
    await expect(`${title} === 'Sessions'`);
    await press("ArrowLeft", "ArrowLeft", 37, 1);
    await expect(`${title} === 'User Guide'`);
    await click("[...document.querySelectorAll('.documentation-outline button')].at(-1)");
    await expect("document.querySelector('.documentation-outline li[data-active]')?.textContent === 'Details, and more'");

    // The navigation: a folder opens with its pages, the arrows move among the entries, and the foot of a page leads on.
    await evaluate(`${nav("Plugins")}.click()`);
    await expect(`${title} === 'Plugins'`);
    assert.deepEqual(await evaluate("[...document.querySelectorAll('.documentation-menu ul ul [data-doc-nav]')].map(node => node.textContent)"), ["Git"]);
    assert.equal(await evaluate(`${nav("Plugins")}.getAttribute('aria-expanded')`), "true");
    await evaluate(`${nav("Plugins")}.focus()`);
    await press("ArrowDown", "ArrowDown", 40);
    await expect("document.activeElement.textContent === 'Git' && document.activeElement.matches('[data-doc-nav]')");
    await press("Enter", "Enter", 13);
    await evaluate("document.activeElement.click()");
    await expect(`${title} === 'Git'`);
    assert.deepEqual(await evaluate("[...document.querySelectorAll('.documentation-trail li')].map(node => node.textContent)"), ["Plugins", "Git"]);
    assert.equal(await evaluate("document.querySelector('.documentation-menu-row[data-within] [data-doc-nav]').textContent"), "Plugins");
    assert.deepEqual(await evaluate("[...document.querySelectorAll('.documentation-pager-link')].map(node => [node.dataset.direction, node.querySelector('strong').textContent])"), [["previous", "Plugins"]]);
    await capture("folder-page");
    await press("ArrowUp", "ArrowUp", 38);
    await press("ArrowLeft", "ArrowLeft", 37);
    await expect("!document.querySelector('.documentation-menu ul ul')", "The left arrow closes the folder.");
    await press("Home", "Home", 36);
    await expect("document.activeElement.textContent === 'User Guide'");
    await evaluate("document.querySelector('.documentation-pager-link[data-direction=\"previous\"]').click()");
    await expect(`${title} === 'Plugins'`);

    // The search finds a text in the guide, marks it, and opens the page at the heading it is under.
    await evaluate("documentationFixture.type(document.querySelector('.documentation-search input'), 'prompt QUEUE')");
    await expect("document.querySelector('.documentation-result mark')?.textContent === 'prompt queue'");
    assert.equal(await evaluate("document.querySelector('.documentation-menu')"), null, "The results take the place of the navigation.");
    assert.equal(await evaluate("document.querySelector('.documentation-result strong').textContent"), "Sessions › Queue");
    await capture("search");
    assert.deepEqual(await evaluate("documentationFixture.state.calls.search"), ["prompt QUEUE"], "The search waits for the typing to pause.");
    await evaluate("document.querySelector('.documentation-result').click()");
    await expect(`${title} === 'Sessions' && Math.abs(${queueTop} - 12) < 3`);
    // A title is found without asking the host; a text that is nowhere says so; clearing brings the navigation back.
    await evaluate("documentationFixture.type(document.querySelector('.documentation-search input'), 'plug')");
    await expect("document.querySelector('.documentation-result mark')?.textContent === 'Plug'");
    await evaluate("documentationFixture.type(document.querySelector('.documentation-search input'), 'zzzz')");
    await expect("document.querySelector('.documentation-results-state')?.textContent === 'No result for “zzzz”.'");
    await evaluate("document.querySelector('.documentation-search button[aria-label=\"Clear the search\"]').click()");
    await expect("document.querySelector('.documentation-menu') && !document.querySelector('.documentation-results')");
    // "/" goes to the search from the page.
    await evaluate("document.querySelector('.documentation-scroll').focus()");
    await command("Input.dispatchKeyEvent", { type: "keyDown", key: "/", code: "Slash", windowsVirtualKeyCode: 191, text: "/" });
    await command("Input.dispatchKeyEvent", { type: "keyUp", key: "/", code: "Slash", windowsVirtualKeyCode: 191 });
    await expect("document.activeElement === document.querySelector('.documentation-search input') && document.activeElement.value === ''");

    // A question for an agent: nothing is asked until it is sent; it is about the page, or about the whole guide; the chat is then shown.
    assert.deepEqual(await evaluate("documentationFixture.state.calls.ask"), []);
    await evaluate("document.querySelector('.documentation-ask-button').click()");
    await expect("document.querySelector('.documentation-ask textarea') && document.activeElement === document.querySelector('.documentation-ask textarea')");
    assert.equal(await evaluate("document.querySelector('.documentation-ask textarea').placeholder"), "Ask about this page…");
    assert.equal(await evaluate("document.querySelector('.documentation-ask button[type=submit]').disabled"), true);
    await evaluate("documentationFixture.type(document.querySelector('.documentation-ask textarea'), '  How do I queue a prompt?  ')");
    if (process.env.DOCUMENTATION_SHOTS) await new Promise(resolve => setTimeout(resolve, 500));
    await capture("ask");
    await press("Enter", "Enter", 13);
    await expect("documentationFixture.state.sessions.length === 1");
    assert.deepEqual(await evaluate("documentationFixture.state.calls.ask"), [{ question: "How do I queue a prompt?", page: "sessions.md" }]);
    assert.deepEqual(await evaluate("documentationFixture.state.sessions"), ["chat-1"]);
    await expect("!document.querySelector('.documentation-ask')", "The form closes once the chat is shown.");
    // No provider: the form says so and leads to the providers; nothing is shown as a chat.
    await evaluate("documentationFixture.state.askReply = { status: 'no_provider', sessionId: null, problem: null }");
    await evaluate("document.querySelector('.documentation-ask-button').click()");
    await expect("document.querySelector('.documentation-ask textarea')?.value === ''");
    await evaluate("[...document.querySelectorAll('.documentation-ask [role=radio], .documentation-ask .bp6-segmented-control button')].find(node => node.textContent === 'Whole guide').click()");
    await expect("document.querySelector('.documentation-ask textarea').placeholder === 'Ask about the guide…'");
    await evaluate("documentationFixture.type(document.querySelector('.documentation-ask textarea'), 'What is CodeAlta?')");
    await evaluate("document.querySelector('.documentation-ask button[type=submit]').click()");
    await expect("document.querySelector('.documentation-ask-problem')?.textContent.startsWith('No model provider is enabled.')");
    assert.deepEqual(await evaluate("documentationFixture.state.calls.ask.at(-1)"), { question: "What is CodeAlta?", page: null });
    assert.equal(await evaluate("document.querySelector('.documentation-ask textarea').value"), "What is CodeAlta?", "The question is kept.");
    await evaluate("document.querySelector('.documentation-ask-problem button').click()");
    await expect("documentationFixture.state.providers === 1 && !document.querySelector('.documentation-ask')");
    assert.equal(await evaluate("documentationFixture.state.sessions.length"), 1);

    // Closing the tab and opening it again shows the page where it was read, without asking the host again.
    await evaluate("document.querySelector('.documentation-scroll').scrollTop = 640");
    await expect(`${scrollTop} === 640`);
    await new Promise(resolve => setTimeout(resolve, 80));
    const read = await evaluate<number>("documentationFixture.state.calls.page.length");
    await evaluate("documentationFixture.unmount()");
    assert.equal(await evaluate("document.querySelector('.documentation')"), null);
    await evaluate("documentationFixture.mount()");
    await expect(`${title} === 'Sessions' && ${scrollTop} === 640`);
    assert.equal(await evaluate("documentationFixture.state.calls.menu"), 1);
    assert.equal(await evaluate("documentationFixture.state.calls.page.length"), read);
    // The window draws the content of a tab before the tab is in its pane: the content has no size when it is first drawn.
    // The page is placed once it has one, and the place that was kept is not replaced by the top of a page nobody read.
    await evaluate("documentationFixture.unmount(); documentationFixture.mount({ pane: true })");
    assert.equal(await evaluate("!!document.querySelector('.fixture-pane .documentation-scroll')"), true, "The tab is in its pane.");
    await expect(`${title} === 'Sessions' && ${scrollTop} === 640`, "A tab that is opened again in a pane shows where the page was read.");
    await new Promise(resolve => setTimeout(resolve, 120));
    await evaluate("documentationFixture.unmount(); documentationFixture.mount({ pane: true })");
    await expect(`${scrollTop} === 640`, "Closing and opening twice keeps the place.");
    // The reader goes on, closes, and comes back to where they went on to.
    await evaluate("document.querySelector('.documentation-scroll').scrollTop = 900");
    await expect("documentationFixture.hub.scroll.get('sessions.md') === 900", "How far the page is read is kept as the reader scrolls.");
    await evaluate("documentationFixture.unmount(); documentationFixture.mount({ pane: true })");
    await expect(`${scrollTop} === 900`);
    assert.equal(await evaluate("documentationFixture.hub.scroll.get('sessions.md')"), 900);
    // A heading that is asked for while the tab is closed is where the tab opens, in a pane too.
    await evaluate("documentationFixture.unmount(); documentationFixture.hub.show('sessions.md', 'queue'); documentationFixture.mount({ pane: true })");
    await expect(`Math.abs(${queueTop} - 12) < 3`, "The tab opens at the heading.");
    assert.equal(await evaluate("documentationFixture.state.calls.page.length"), read);
    // A tab that is drawn while its pane is hidden has no size either: it is placed when the pane is shown, and
    // what was kept is not replaced meanwhile.
    await expect(`documentationFixture.hub.scroll.get('sessions.md') === ${scrollTop} && ${scrollTop} > 1000`);
    const kept = await evaluate<number>(scrollTop);
    await evaluate("documentationFixture.unmount(); documentationFixture.mount({ hidden: true })");
    assert.equal(await evaluate("document.querySelector('.documentation-scroll').clientHeight"), 0);
    await new Promise(resolve => setTimeout(resolve, 120));
    assert.equal(await evaluate("documentationFixture.hub.scroll.get('sessions.md')"), kept);
    await evaluate("documentationFixture.reveal()");
    await expect(`${scrollTop} === ${kept}`, "The page is where it was read once its pane is shown.");
    assert.equal(await evaluate("documentationFixture.hub.scroll.get('sessions.md')"), kept);
    await evaluate("documentationFixture.unmount()");
    // A hidden tab reads nothing; a request of the host (alta documentation open) moves the tab, shown or not.
    await evaluate("documentationFixture.unmount(); documentationFixture.hub.show('plugins/git.md', 'sign-in'); documentationFixture.mount({ visible: false })");
    await expect(`${title} === 'Git'`);
    assert.equal(await evaluate(current), "Git");
    await evaluate("documentationFixture.hub.show('nowhere.md')");
    await expect("document.querySelector('.documentation-problem')?.textContent.includes('This page is not in the guide.')");
    await evaluate("document.querySelector('.documentation-problem button').click()");
    await expect(`${title} === 'User Guide'`);

    // The light colors of the window.
    const dark = await evaluate<string>("getComputedStyle(document.querySelector('.documentation-nav')).backgroundColor");
    await evaluate("documentationFixture.theme('light')");
    await expect(`getComputedStyle(document.querySelector('.documentation-nav')).backgroundColor !== ${JSON.stringify(dark)}`);
    await capture("guide-wide-light");
    await evaluate("documentationFixture.theme('dark')");

    // A pane of medium width has no outline; a narrow one opens its navigation over the page.
    await evaluate("documentationFixture.mount({ width: 1000 })");
    await expect("getComputedStyle(document.querySelector('.documentation-outline')).display === 'none'");
    assert.equal(await evaluate("getComputedStyle(document.querySelector('.documentation-nav')).visibility"), "visible");
    assert.equal(await evaluate("getComputedStyle(document.querySelector('.documentation-nav-toggle')).display"), "none");
    await evaluate("documentationFixture.mount({ width: 520 })");
    await expect("getComputedStyle(document.querySelector('.documentation-nav')).visibility === 'hidden'");
    assert.equal(await evaluate("document.querySelector('.documentation-article').getBoundingClientRect().width > 440"), true, "The page takes the pane.");
    assert.equal(await evaluate("document.querySelector('.documentation').scrollWidth <= 520"), true, "Nothing is wider than the pane.");
    assert.equal(await evaluate("getComputedStyle(document.querySelector('.documentation-ask-button .bp6-button-text')).display"), "none");
    await capture("guide-narrow");
    await evaluate("document.querySelector('.documentation-nav-toggle').click()");
    await expect("getComputedStyle(document.querySelector('.documentation-nav')).visibility === 'visible' && document.querySelector('.documentation-nav-toggle').getAttribute('aria-expanded') === 'true'");
    await expect("Math.abs(document.querySelector('.documentation-nav').getBoundingClientRect().left) < 1", "The navigation has slid in.");
    await capture("guide-narrow-pages");
    await evaluate(`${nav("Sessions")}.click()`);
    await expect(`${title} === 'Sessions' && !document.querySelector('.documentation').hasAttribute('data-nav-open')`, "Choosing a page closes the navigation.");
    await evaluate("document.querySelector('.documentation-nav-toggle').click()");
    await expect("document.querySelector('.documentation').hasAttribute('data-nav-open')");
    await evaluate("document.querySelector('.documentation-scroll').focus()");
    await press("Escape", "Escape", 27);
    await expect("!document.querySelector('.documentation').hasAttribute('data-nav-open')");

    // The native regression: a cached, multi-block prompts page in the actual production dock, not the simplified Pane above.
    const prompts = await readFile(new URL("../../../../../site/docs/prompts.md", import.meta.url), "utf8");
    await evaluate(`documentationFixture.unmount(); documentationFixture.prompts(${JSON.stringify(prompts)}); documentationFixture.hub.show('prompts.md'); documentationFixture.dock()`);
    await expect(`${title} === 'Agent Prompts' && document.querySelector('.documentation-scroll').scrollHeight > 6000`);
    assert.deepEqual(await evaluate("documentationFixture.hub.getSnapshot().page.blocks.map(block => block.kind)"), ["markdown", "figure", "markdown", "markdown"], "The unshipped screenshot is omitted, but still splits the surrounding Markdown.");
    await expect("document.querySelector('.documentation-figure img').naturalWidth > 0");
    const promptsRead = await evaluate<number>("documentationFixture.state.calls.page.length");
    const settle = () => evaluate("new Promise(resolve => { let frames = 6; const tick = () => --frames ? requestAnimationFrame(tick) : resolve(); requestAnimationFrame(tick); })");
    await evaluate("document.querySelector('.documentation-scroll').scrollTop = 800; document.querySelector('.documentation-scroll').focus()");
    await expect("documentationFixture.hub.scroll.get('prompts.md') === 800");
    await settle();
    assert.equal(await evaluate(scrollTop), 800, "The manual scroll is stable before closing.");
    await press("w", "KeyW", 87, 2);
    await expect("!document.querySelector('.documentation')");
    assert.equal(await evaluate("documentationFixture.hub.scroll.get('prompts.md')"), 800, "Closing the real dock tab keeps the saved place.");
    await click("document.querySelector('.fixture-book')");
    await expect(`${title} === 'Agent Prompts' && ${scrollTop} === 800`, "Reopening the real dock restores the cached prompts page to 800.");
    await settle();
    assert.equal(await evaluate(scrollTop), 800, "The restored scroll survives the dock's subsequent layout frames.");
    await expect("document.querySelector('.documentation-outline li[data-active]')?.textContent === 'Prompt composition at a glance'");
    // Once restored, the reader is in charge. Neither a render for new colors nor a later reopen replays 800.
    await evaluate("document.querySelector('.documentation-scroll').scrollTop = 1100; documentationFixture.theme('light')");
    await expect("documentationFixture.hub.scroll.get('prompts.md') === 1100");
    await settle();
    assert.equal(await evaluate(scrollTop), 1100);
    await evaluate("document.querySelector('.documentation-scroll').focus()");
    await press("w", "KeyW", 87, 2);
    await expect("!document.querySelector('.documentation')");
    await click("document.querySelector('.fixture-book')");
    await expect(`${scrollTop} === 1100`, "The next close/reopen keeps the reader's new place.");
    // An explicit heading while closed is a new navigation, not a request to restore the last manual position.
    await evaluate("document.querySelector('.documentation-scroll').focus()");
    await press("w", "KeyW", 87, 2);
    await expect("!document.querySelector('.documentation')");
    await evaluate("documentationFixture.hub.show('prompts.md', 'built-in-modes')");
    await click("document.querySelector('.fixture-book')");
    await expect("Math.abs(document.querySelector('[data-doc-anchor=\"built-in-modes\"]').getBoundingClientRect().top - document.querySelector('.documentation-scroll').getBoundingClientRect().top - 12) < 3", "A heading opens at its final, laid-out position in the real dock.");
    assert.equal(await evaluate("documentationFixture.state.calls.page.length"), promptsRead, "Reopening uses the cached prompts page throughout.");

    // Nothing a page wrote ran, and the page asked the network for nothing but its own files.
    assert.equal(await evaluate("documentationFixture.state.executed"), 0);
    assert.deepEqual(refused, []);
    assert.deepEqual(failures, []);
    assert.ok(await evaluate<number>("documentationFixture.state.activated") > 0);
  } finally {
    // Edge's launcher can exit while the browser it started goes on: the browser itself is asked to close.
    if (socket?.readyState === WebSocket.OPEN) socket.send(JSON.stringify({ id: 999_999, method: "Browser.close" }));
    socket?.close();
    if (browser && browser.exitCode === null) { const closed = new Promise<void>(resolve => browser!.once("exit", () => resolve())); browser.kill(); await Promise.race([closed, new Promise(resolve => setTimeout(resolve, 3000))]); }
    await rm(directory, { recursive: true, force: true, maxRetries: 10, retryDelay: 100 });
  }
});
