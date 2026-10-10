import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { mkdir, mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { fileURLToPath, pathToFileURL } from "node:url";
import { build } from "esbuild";
import { blueprintPaletteVariables } from "../blueprintPalette";
import { browserBaseArgs, browserExecutable } from "../browserTarget";

const edge = browserExecutable;
// Where the pictures of this test go, when a person wants to look at them.
const shots = process.env.CODEALTA_SHOTS ?? "";
const pause = (milliseconds: number) => new Promise(resolve => setTimeout(resolve, milliseconds));

test("the landing page welcomes, lists what was used last, draws the cards of plugins and keeps what the user chose", { skip: !edge, timeout: 240_000 }, async context => {
  const root = await mkdtemp(join(tmpdir(), "codealta-landing-"));
  let browser: ReturnType<typeof spawn> | undefined;
  let socket: WebSocket | undefined;
  try {
    await build({ entryPoints: [fileURLToPath(new URL("./landing.mount.tsx", import.meta.url))], outfile: join(root, "fixture.js"), bundle: true, platform: "browser", format: "iife",
      // The fixture plays the shell itself: the client of the host, which the window's own shell reads the providers and the cards with, is left out.
      plugins: [{ name: "no-host", setup(bundle) {
        bundle.onResolve({ filter: /^#neoastra$/ }, () => ({ path: "neoastra", namespace: "no-host" }));
        bundle.onLoad({ filter: /.*/, namespace: "no-host" }, () => ({ contents: "export const modelCatalog = {}, pluginUi = {};", loader: "js" }));
      } }] });
    await writeFile(join(root, "style.css"), blueprintPaletteVariables(await readFile(new URL("../../node_modules/@blueprintjs/core/lib/css/blueprint.css", import.meta.url), "utf8"))
      + await readFile(new URL("../style.css", import.meta.url), "utf8") + await readFile(new URL("./landing.css", import.meta.url), "utf8"));
    const page = join(root, "fixture.html");
    await writeFile(page, '<!doctype html><html><head><meta charset="utf-8"><link rel="stylesheet" href="style.css"></head><body><div id="root"></div><script src="fixture.js"></script></body></html>');
    browser = spawn(edge!, [...browserBaseArgs, `--user-data-dir=${join(root, "profile")}`, "--remote-debugging-port=0", "about:blank"], { stdio: "ignore", windowsHide: true });
    let port = "";
    for (let attempt = 0; attempt < 100 && !port; attempt++) {
      try { port = (await readFile(join(root, "profile", "DevToolsActivePort"), "utf8")).split(/\r?\n/)[0]; }
      catch { await pause(50); }
    }
    assert.match(port, /^\d+$/);
    const pages: { type?: string; webSocketDebuggerUrl?: string }[] = await (await fetch(`http://127.0.0.1:${port}/json/list`, { signal: AbortSignal.timeout(10_000) })).json();
    socket = new WebSocket(pages.find(value => value.type === "page")!.webSocketDebuggerUrl!);
    await new Promise<void>((resolve, reject) => { socket!.addEventListener("open", () => resolve(), { once: true }); socket!.addEventListener("error", () => reject(Error("Browser unavailable")), { once: true }); });
    let sequence = 0;
    const problems: string[] = [];
    socket.addEventListener("message", event => {
      const message = JSON.parse(String(event.data));
      if (message.method === "Runtime.exceptionThrown") problems.push(JSON.stringify(message.params.exceptionDetails).slice(0, 600));
      if (message.method === "Runtime.consoleAPICalled" && message.params.type === "error") problems.push(message.params.args.map((value: any) => value.value ?? value.description).join(" ").slice(0, 400));
    });
    const command = (method: string, params: object = {}) => new Promise<any>((resolve, reject) => {
      const id = ++sequence;
      const timer = setTimeout(() => reject(Error(`${method} timed out`)), 30_000);
      const reply = (event: MessageEvent) => {
        const message = JSON.parse(String(event.data));
        if (message.id !== id) return;
        socket!.removeEventListener("message", reply); clearTimeout(timer);
        if (message.error) reject(Error(JSON.stringify(message.error))); else resolve(message.result);
      };
      socket!.addEventListener("message", reply); socket!.send(JSON.stringify({ id, method, params }));
    });
    const evaluate = async <T,>(expression: string): Promise<T> => {
      const result = await command("Runtime.evaluate", { expression, returnByValue: true, awaitPromise: true });
      assert.equal(result.exceptionDetails, undefined, JSON.stringify(result.exceptionDetails));
      return result.result?.value as T;
    };
    const wait = (expression: string, milliseconds = 8_000) => evaluate<boolean>(
      `new Promise(resolve=>{const end=Date.now()+${milliseconds};const tick=()=>{let ok=false;try{ok=!!(${expression})}catch{}ok?resolve(true):Date.now()>end?resolve(false):setTimeout(tick,25)};tick()})`);
    const shot = async (name: string) => {
      if (!shots) return;
      await mkdir(shots, { recursive: true });
      await writeFile(join(shots, name), Buffer.from((await command("Page.captureScreenshot", { format: "png" })).data, "base64"));
    };
    type Box = { left: number; right: number; top: number; bottom: number; width: number; height: number };
    const rect = (selector: string) => evaluate<Box | null>(
      `(()=>{const e=document.querySelector(${JSON.stringify(selector)});if(!e)return null;const r=e.getBoundingClientRect();return {left:r.left,right:r.right,top:r.top,bottom:r.bottom,width:r.width,height:r.height}})()`);
    const text = (selector: string) => evaluate<string[]>(`Array.from(document.querySelectorAll(${JSON.stringify(selector)})).map(e=>e.textContent)`);
    const count = (selector: string) => evaluate<number>(`document.querySelectorAll(${JSON.stringify(selector)}).length`);
    // A click on the first element of the selector that has exactly this text.
    const press = async (selector: string, label: string) => assert.equal(await evaluate<boolean>(
      `(()=>{const e=Array.from(document.querySelectorAll(${JSON.stringify(selector)})).find(e=>e.textContent.trim()===${JSON.stringify(label)});if(!e)return false;e.click();return true})()`), true, `${selector} "${label}"`);
    const state = <T,>(name: string) => evaluate<T>(`landingFixture.state.${name}`);

    await command("Runtime.enable");
    await command("Emulation.setDeviceMetricsOverride", { width: 1280, height: 900, deviceScaleFactor: 1, mobile: false });
    await command("Page.navigate", { url: pathToFileURL(page).href });
    assert.equal(await wait("window.landingFixture"), true);

    await context.test("a new profile is shown what is left to do, and nothing of it once there is a provider and a project", async () => {
      await evaluate("landingFixture.reset(); landingFixture.state.providers = { ready: 0, detecting: false }; landingFixture.show({ projects: [], sessions: [] })");
      assert.equal(await wait("document.querySelector('.landing-onboarding [data-step=provider]') && document.querySelector('.landing-onboarding [data-step=project]')"), true);
      assert.deepEqual(await text(".landing-hero h1"), ["Welcome"]);
      assert.deepEqual(await text(".landing-hero-actions button"), ["New session", "Open a project", "Documentation"]);
      assert.deepEqual(await text(".landing-onboarding h2"), ["Get started"]);
      assert.deepEqual(await text(".landing-list .landing-empty"), ["No session yet. Send a first prompt to start one.", "No project yet. Open a folder to add one."]);
      await shot("landing-new-profile.png");
      await evaluate("document.querySelector('[data-step=provider] button').click(); document.querySelector('[data-step=project] button').click()");
      assert.deepEqual(await state("runs"), ["model_providers", "open"]);
      await press(".landing-hero-actions button", "New session");
      await press(".landing-hero-actions button", "Open a project");
      assert.deepEqual(await state("runs"), ["model_providers", "open", "new_session", "open"]);

      // Providers that are still looked for are not missing: only the project is asked for.
      await evaluate("landingFixture.reset(); landingFixture.state.providers = { ready: 0, detecting: true }; landingFixture.show({ projects: [], sessions: [] })");
      assert.equal(await wait("document.querySelector('[data-step=project]') && landingFixture.state.providerReads > 0"), true);
      await pause(120);
      assert.equal(await count("[data-step=provider]"), 0);
      assert.equal(await count("[data-step=project]"), 1);

      // A profile that has both is not told to get started.
      await evaluate("landingFixture.reset(); landingFixture.show(landingFixture.sample())");
      assert.equal(await wait("document.querySelectorAll('[data-list=projects] .landing-row').length === 3 && landingFixture.state.providerReads > 0"), true);
      await pause(120);
      assert.equal(await count(".landing-onboarding"), 0);

      // What the window has not read yet is neither empty nor missing.
      await evaluate("landingFixture.reset(); landingFixture.show({ projects: null, sessions: null })");
      assert.equal(await wait("document.querySelectorAll('.landing-list .landing-empty[aria-busy=true]').length === 2 && landingFixture.state.providerReads > 0"), true);
      assert.deepEqual(await text(".landing-list .landing-empty"), ["Loading…", "Loading…"]);
      await pause(120);
      assert.equal(await count(".landing-onboarding"), 0);
      assert.equal(await count(".landing-row"), 0);
    });

    await context.test("the lists show what was used last and open it, and the Documentation is the one of the application", async () => {
      await evaluate("landingFixture.reset(); landingFixture.show(landingFixture.sample())");
      assert.equal(await wait("document.querySelectorAll('[data-list=sessions] .landing-row').length === 3"), true);
      // The session a sub-agent ran is not listed, and a title loses its Markdown marker.
      assert.deepEqual(await text("[data-list=sessions] .landing-row-title"), ["Newest", "A chat", "Old one"]);
      assert.deepEqual(await text("[data-list=sessions] .landing-row-detail"), ["Beta", "Chat", "Alpha"]);
      assert.equal(await count("[data-list=sessions] .landing-row-time[datetime]"), 3);
      // A project follows its last session, a sub-agent's included; the one without any session comes last, without a time.
      assert.deepEqual(await text("[data-list=projects] .landing-row-title"), ["Alpha", "Beta", "Gamma"]);
      assert.deepEqual(await text("[data-list=projects] .landing-row-detail"), ["/code/alpha", "/code/beta", "/code/gamma"]);
      assert.equal(await count("[data-list=projects] .landing-row-time"), 2);
      await evaluate("document.querySelectorAll('[data-list=sessions] button.landing-row')[0].click(); document.querySelectorAll('[data-list=sessions] button.landing-row')[2].click(); document.querySelectorAll('[data-list=projects] button.landing-row')[1].click()");
      assert.deepEqual(await state("sessions"), ["s-new", "s-old"]);
      assert.deepEqual(await state("projects"), ["p2"]);
      // The space that is not the one of every project is named on the list.
      assert.equal(await count("[data-list=projects] .landing-card-project"), 0);
      await evaluate("landingFixture.show({ space: { id: 'work', name: 'Work', isDefault: false } })");
      assert.deepEqual(await text("[data-list=projects] .landing-card-project"), ["Work"]);

      // The documentation is the one of the application, opened by the command of the window. A window that cannot run the command now
      // says so: the page never sends to the web.
      await press(".landing-hero-actions button", "Documentation");
      assert.deepEqual(await state("runs"), ["documentation"]);
      assert.deepEqual(await state("unavailable"), []);
      await evaluate("landingFixture.state.refused.push('documentation')");
      await press(".landing-hero-actions button", "Documentation");
      assert.deepEqual(await state("unavailable"), ["Documentation"]);
      await press(".landing-explore button", "Documentation");
      assert.deepEqual(await state("unavailable"), ["Documentation", "Documentation"]);
      assert.equal(await count(".landing a[href]"), 0, "nothing of the page is a link to the web");
      await press(".landing-explore button", "Settings");
      assert.equal((await state<string[]>("runs")).at(-1), "settings");
      assert.equal((await state<string[]>("unavailable")).length, 2, "a command that ran says nothing");
    });

    await context.test("the cards of plugins are drawn in order, sanitized, with their actions, and a card that failed stays a card", async () => {
      await evaluate(`landingFixture.reset(); landingFixture.state.cards = [
        landingFixture.card({ cardId: "stats", pluginKey: "builtin:statistics", plugin: "Statistics", title: "Statistics", statusText: "Up to date", tone: "Success", projectId: "p1", projectName: "Alpha",
          html: '<p class="note">12 sessions</p><script>window.pwned = 1</' + 'script><img src="x" onerror="window.pwned = 3"><button onclick="window.pwned = 2" data-alta-command="STATS_REFRESH">Refresh now</button>'
            + '<button data-alta-command="notes_add">Of another plugin</button>',
          commands: [{ name: "stats_refresh", id: "cmd-refresh" }],
          actions: [landingFixture.action({ label: "Recount", commandId: "cmd-recount" }), landingFixture.action({ label: "Open", canvas: "statistics", canvasScope: "Project", key: "week", primary: true }),
            landingFixture.action({ label: "Later", commandId: "cmd-later", disabled: true })] }),
        landingFixture.card({ cardId: "broken", title: "Broken", state: "failed", html: "<p>never shown</p>", statusText: "hidden", actions: [landingFixture.action({ label: "Never", commandId: "cmd-never" })] }),
        landingFixture.card({ cardId: "notes", title: "Notes", html: "<p>Third card</p>", projectId: "p2", projectName: "Beta", commands: [{ name: "notes_add", id: "cmd-notes-add" }],
          actions: [landingFixture.action({ label: "Board", canvas: "board", canvasScope: "Application" })] }),
      ]; landingFixture.show(landingFixture.sample())`);
      assert.equal(await wait("document.querySelectorAll('.landing-plugin-card').length === 3"), true);
      assert.deepEqual(await evaluate("Array.from(document.querySelectorAll('.landing-plugin-card')).map(e => [e.dataset.plugin, e.dataset.card, e.dataset.state])"),
        [["builtin:statistics", "stats", "ok"], ["builtin:fixture", "broken", "failed"], ["builtin:fixture", "notes", "ok"]]);
      assert.deepEqual(await text(".landing-plugin-card h2"), ["Statistics", "Broken", "Notes"]);
      assert.deepEqual(await text("[data-card=stats] .landing-card-status"), ["Up to date"]);
      assert.equal(await evaluate("document.querySelector('[data-card=stats] .landing-card-status').dataset.tone"), "Success");
      assert.deepEqual(await text("[data-card=stats] .landing-card-project"), ["Alpha"]);
      // The cards sit in the grid of the page, after the two lists and before Explore.
      assert.deepEqual(await evaluate("Array.from(document.querySelectorAll('.landing-grid > section')).map(e => e.dataset.list || e.dataset.card || 'explore')"), ["sessions", "projects", "stats", "broken", "notes", "explore"]);

      // The card that failed says so and has nothing else; the others are drawn.
      assert.deepEqual(await text("[data-card=broken] .landing-card-failed"), ["This card could not be loaded."]);
      assert.equal(await count("[data-card=broken] .landing-card-html, [data-card=broken] .landing-card-actions, [data-card=broken] .landing-card-status"), 0);
      assert.equal(await evaluate("document.querySelector('[data-card=notes] .landing-card-html').textContent"), "Third card");
      assert.equal(await count("[data-card=stats] .landing-card-failed, [data-card=notes] .landing-card-failed"), 0);

      // Nothing of a fragment runs: the script and the handlers are gone, and the text stays.
      assert.equal(await evaluate("document.querySelector('[data-card=stats] .landing-card-html p').textContent"), "12 sessions");
      assert.equal(await count(".landing-plugin-card script"), 0);
      assert.equal(await count(".landing-plugin-card [onclick], .landing-plugin-card [onerror]"), 0);
      await press("[data-card=stats] .landing-card-html [data-alta-command]", "Refresh now");
      await pause(60);
      assert.equal(await evaluate("typeof window.pwned"), "undefined");
      // The window acts for the fragment: the command of the plugin of the card, by its name whatever the case, for the project of the card
      // and whatever project the window has selected. It does not go through the commands of the selected project.
      assert.deepEqual(await state("commands"), [{ commandId: "cmd-refresh", projectId: "p1", label: "stats_refresh" }]);
      assert.deepEqual(await state("named"), []);
      // A name that is a command of another plugin, which has a card on the same page, runs nothing.
      await press("[data-card=stats] .landing-card-html [data-alta-command]", "Of another plugin");
      await pause(60);
      assert.equal((await state<unknown[]>("commands")).length, 1);
      assert.deepEqual(await state("named"), []);

      // The actions of a card: a command, a canvas, and one the plugin disabled.
      assert.deepEqual(await text("[data-card=stats] .landing-card-actions button"), ["Recount", "Open", "Later"]);
      assert.deepEqual(await evaluate("Array.from(document.querySelectorAll('[data-card=stats] .landing-card-actions button')).map(e => e.disabled)"), [false, false, true]);
      await press("[data-card=stats] .landing-card-actions button", "Recount");
      const ran = [{ commandId: "cmd-refresh", projectId: "p1", label: "stats_refresh" }, { commandId: "cmd-recount", projectId: "p1", label: "Recount" }];
      assert.deepEqual(await state("commands"), ran);
      await press("[data-card=stats] .landing-card-actions button", "Open");
      await press("[data-card=notes] .landing-card-actions button", "Board");
      // A canvas of a project opens for the project of the card. A canvas of the application opens without one, also from the card of a
      // plugin of a project: it is the tab the Canvases page opens.
      assert.deepEqual(await state("canvases"), [{ pluginKey: "builtin:statistics", canvasId: "statistics", projectId: "p1", key: "week" }, { pluginKey: "builtin:fixture", canvasId: "board", projectId: null, key: null }]);
      await press("[data-card=stats] .landing-card-actions button", "Later");
      await evaluate("document.querySelectorAll('[data-card=stats] .landing-card-actions button')[2].dispatchEvent(new MouseEvent('click', { bubbles: true }))");
      assert.deepEqual(await state("commands"), ran);
      assert.equal((await state<unknown[]>("canvases")).length, 2);
      await shot("landing-cards.png");
    });

    await context.test("the cards are read while the page is shown, once for a burst of changes, and a read that fails keeps them", async () => {
      const cards = "[landingFixture.card({ cardId: 'one', title: 'One' }), landingFixture.card({ cardId: 'two', title: 'Two' })]";
      // A page behind another tab reads nothing, and neither does a window without a host.
      await evaluate(`landingFixture.reset(); landingFixture.state.cards = ${cards}; landingFixture.show({ ...landingFixture.sample(), visible: false })`);
      await pause(200);
      assert.deepEqual([await state("cardReads"), await state("providerReads"), await count(".landing-plugin-card")], [0, 0, 0]);
      await evaluate("landingFixture.invalidate(); landingFixture.invalidate('codealta:plugins-changed')");
      await pause(200);
      assert.equal(await state("cardReads"), 0);
      await evaluate(`landingFixture.reset(); landingFixture.state.cards = ${cards}; landingFixture.show({ ...landingFixture.sample(), epoch: null })`);
      await pause(200);
      assert.deepEqual([await state("cardReads"), await state("providerReads"), await count(".landing-plugin-card")], [0, 0, 0]);

      // Shown: one read. StrictMode runs the effect of a new component twice, and the first of the two is given up.
      await evaluate(`landingFixture.reset(); landingFixture.state.cards = ${cards}; landingFixture.show(landingFixture.sample())`);
      assert.equal(await wait("document.querySelectorAll('.landing-plugin-card').length === 2"), true);
      await pause(200);
      const first = await state<number>("cardReads");
      assert.ok(first >= 1 && first <= 2, `${first} reads for a page that was just shown`);
      assert.deepEqual(await text(".landing-plugin-card h2"), ["One", "Two"]);

      // Drawing the page again with the same host reads nothing.
      await evaluate("landingFixture.show({ dark: false }); landingFixture.show({ dark: true })");
      await pause(150);
      assert.equal(await state("cardReads"), first);

      // A burst of changes is one read.
      await evaluate("landingFixture.state.cards = [landingFixture.card({ cardId: 'one', title: 'One again' }), landingFixture.card({ cardId: 'two', title: 'Two' })]; for (let i = 0; i < 6; i++) landingFixture.invalidate()");
      assert.equal(await wait("document.querySelector('[data-card=one] h2').textContent === 'One again'"), true);
      await pause(250);
      assert.equal(await state("cardReads"), first + 1);

      // A read that fails keeps what is drawn.
      await evaluate("landingFixture.state.cardsFail = true; landingFixture.invalidate()");
      assert.equal(await wait(`landingFixture.state.cardReads === ${first + 2}`), true);
      await pause(120);
      assert.deepEqual(await text(".landing-plugin-card h2"), ["One again", "Two"]);
      // So does an answer that is not one of the host.
      await evaluate("landingFixture.state.cardsFail = false; landingFixture.state.cards = 'gone'; landingFixture.invalidate()");
      assert.equal(await wait(`landingFixture.state.cardReads === ${first + 3}`), true);
      await pause(120);
      assert.deepEqual(await text(".landing-plugin-card h2"), ["One again", "Two"]);

      // A plugin that stopped has no card any more: the host says that plugins changed, and its card goes.
      await evaluate("landingFixture.state.cards = [landingFixture.card({ cardId: 'two', title: 'Two' })]; landingFixture.invalidate('codealta:plugins-changed')");
      assert.equal(await wait("document.querySelectorAll('.landing-plugin-card').length === 1"), true);
      assert.deepEqual(await text(".landing-plugin-card h2"), ["Two"]);
      assert.equal(await state("cardReads"), first + 4);

      // Hidden, the page keeps its cards and reads nothing; shown again, it reads once.
      await evaluate("landingFixture.show({ visible: false }); landingFixture.state.cards = []; landingFixture.invalidate()");
      await pause(200);
      assert.equal(await state("cardReads"), first + 4);
      assert.equal(await count(".landing-plugin-card"), 1);
      await evaluate("landingFixture.show({ visible: true })");
      assert.equal(await wait("document.querySelectorAll('.landing-plugin-card').length === 0"), true);
      assert.equal(await state("cardReads"), first + 5);
      // The lists of the application are still there.
      assert.equal(await count("[data-list=sessions] .landing-row"), 3);
    });

    await context.test("the accent is a part of the hero that moves only when it may, and the switches keep what the user chose", async () => {
      await evaluate("landingFixture.reset(); landingFixture.show(landingFixture.sample())");
      assert.equal(await wait("document.querySelector('.landing-hero canvas.landing-pixels')?.dataset.moving === 'true'"), true);
      assert.equal(await evaluate("document.querySelector('.landing').dataset.animate"), "true");

      // A part of the hero, at its right: never the background of the page.
      const hero = (await rect(".landing-hero"))!, pixels = (await rect(".landing-pixels"))!, landing = (await rect(".landing"))!;
      assert.ok(pixels.left >= hero.left - 0.5 && pixels.right <= hero.right + 0.5 && pixels.top >= hero.top - 0.5 && pixels.bottom <= hero.bottom + 0.5, JSON.stringify({ hero, pixels }));
      assert.ok(pixels.width > 100 && pixels.width < hero.width * 0.7, `the accent is ${pixels.width} wide in a hero of ${hero.width}`);
      assert.ok(pixels.left > hero.left + hero.width * 0.3, "it leaves the left of the hero, where the text is");
      assert.ok(pixels.height < landing.height * 0.5 && pixels.width < landing.width * 0.7, JSON.stringify({ landing, pixels }));
      assert.equal(await evaluate("document.querySelector('.landing-pixels').getAttribute('aria-hidden')"), "true");
      // The buttons of the hero are above it: a click where they are reaches them.
      assert.equal(await evaluate("(() => { const r = document.querySelector('.landing-hero-actions button').getBoundingClientRect(); return !!document.elementFromPoint(r.left + r.width / 2, r.top + r.height / 2)?.closest('.landing-hero-actions button'); })()"), true);

      // It is painted, one pixel for each cell, and it drifts.
      const frame = "(() => { const c = document.querySelector('.landing-pixels'); const d = c.getContext('2d').getImageData(0, 0, c.width, c.height).data; let lit = 0, sum = 0; for (let i = 3; i < d.length; i += 4) { if (d[i]) lit++; sum = (sum * 31 + d[i]) % 1000003; } return { width: c.width, height: c.height, lit, sum }; })()";
      type Frame = { width: number; height: number; lit: number; sum: number };
      const moving = await evaluate<Frame>(frame);
      assert.ok(moving.width > 10 && moving.width <= 96 && moving.height > 4 && moving.height <= 40 && moving.lit > 0, JSON.stringify(moving));
      assert.equal(await wait(`${frame}.sum !== ${moving.sum}`, 5_000), true, "the field moves");

      // Animation off: one still frame, and nothing changes it.
      assert.deepEqual(await text(".landing-footer .landing-switch"), ["Show at startup", "Animation"]);
      assert.deepEqual(await evaluate("Array.from(document.querySelectorAll('.landing-footer input[type=checkbox]')).map(e => e.checked)"), [true, true]);
      await evaluate("document.querySelectorAll('.landing-footer input[type=checkbox]')[1].click()");
      assert.equal(await wait("document.querySelector('.landing-pixels').dataset.moving === 'false' && document.querySelector('.landing').dataset.animate === 'false'"), true);
      assert.deepEqual(await evaluate("landingFixture.preferences()"), { openAtStartup: true, animate: false });
      assert.deepEqual(await evaluate("landingFixture.stored()"), { "codealta.desktop.landing.animation.v1": "off" });
      await pause(150);
      const still = await evaluate<Frame>(frame);
      assert.ok(still.lit > 0, "a still frame is drawn");
      await pause(450);
      assert.deepEqual(await evaluate<Frame>(frame), still, "nothing paints the field while it does not move");

      await evaluate("document.querySelectorAll('.landing-footer input[type=checkbox]')[0].click()");
      assert.equal(await wait("document.querySelectorAll('.landing-footer input[type=checkbox]')[0].checked === false"), true);
      assert.deepEqual(await evaluate("landingFixture.preferences()"), { openAtStartup: false, animate: false });
      assert.deepEqual(await evaluate("landingFixture.stored()"), { "codealta.desktop.landing.animation.v1": "off", "codealta.desktop.landing.startup.v1": "off" });
      await evaluate("document.querySelectorAll('.landing-footer input[type=checkbox]')[0].click(); document.querySelectorAll('.landing-footer input[type=checkbox]')[1].click()");
      assert.equal(await wait("document.querySelector('.landing-pixels').dataset.moving === 'true'"), true);
      assert.deepEqual(await evaluate("landingFixture.stored()"), { "codealta.desktop.landing.animation.v1": "on", "codealta.desktop.landing.startup.v1": "on" });
      // The switch of Settings writes the same store: the page follows it.
      await evaluate("landingFixture.setPreference('animate', false)");
      assert.equal(await wait("document.querySelector('.landing-pixels').dataset.moving === 'false' && document.querySelectorAll('.landing-footer input[type=checkbox]')[1].checked === false"), true);
      await evaluate("landingFixture.setPreference('animate', true)");
      assert.equal(await wait("document.querySelector('.landing-pixels').dataset.moving === 'true'"), true);

      // A page behind another tab does not move, and moves again in front.
      await evaluate("landingFixture.show({ visible: false })");
      assert.equal(await wait("document.querySelector('.landing-pixels').dataset.moving === 'false'"), true);
      assert.equal(await evaluate("document.querySelector('.landing').dataset.animate"), "true", "the choice of the user did not change");
      await evaluate("landingFixture.show({ visible: true })");
      assert.equal(await wait("document.querySelector('.landing-pixels').dataset.moving === 'true'"), true);

      // Someone who asked the system for less motion gets none, whatever the switch says; the page follows the setting while it is open.
      await command("Emulation.setEmulatedMedia", { features: [{ name: "prefers-reduced-motion", value: "reduce" }] });
      assert.equal(await wait("document.querySelector('.landing-pixels').dataset.moving === 'false'"), true);
      assert.equal(await evaluate("document.querySelectorAll('.landing-footer input[type=checkbox]')[1].checked"), true);
      await evaluate("landingFixture.reset(); landingFixture.show(landingFixture.sample())");
      await pause(250);
      assert.equal(await evaluate("document.querySelector('.landing-pixels').dataset.moving"), "false", "a page opened under that setting never starts to move");
      assert.ok((await evaluate<Frame>(frame)).lit > 0, "it still has its still frame");
      await command("Emulation.setEmulatedMedia", { features: [] });
      assert.equal(await wait("document.querySelector('.landing-pixels').dataset.moving === 'true'"), true);

      // A light theme keeps the page whole.
      await evaluate("landingFixture.show({ dark: false })");
      assert.equal(await evaluate("document.querySelector('.landing').dataset.theme"), "light");
      assert.equal(await count(".landing-pixels"), 1);
    });

    await context.test("a narrow tab still shows the hero, its buttons and both lists, without anything to scroll sideways", async () => {
      await evaluate(`landingFixture.reset(); landingFixture.state.providers = { ready: 0, detecting: false };
        landingFixture.state.cards = [landingFixture.card({ cardId: 'wide', title: 'A card with a title that is rather long for a narrow tab', statusText: 'A status that is long as well',
          actions: [landingFixture.action({ label: 'First action', commandId: 'a' }), landingFixture.action({ label: 'Second action', commandId: 'b' }), landingFixture.action({ label: 'Third action', commandId: 'c' })] })];
        landingFixture.show({ ...landingFixture.sample(), projects: [{ id: 'p1', name: 'A project with a very long name that cannot fit on a line of a narrow tab', path: '/code/a/very/long/path/that/goes/on/and/on/for/a/while/alpha' }], width: 420 })`);
      assert.equal(await wait("document.querySelector('.landing-plugin-card') && document.querySelector('[data-step=provider]') && getComputedStyle(document.querySelector('.landing-hero h1')).fontSize === '24px'"), true, "the page answers to its own width");
      const frame = (await rect("#frame"))!, landing = (await rect(".landing"))!;
      assert.equal(frame.width, 420);
      assert.ok(landing.width <= 420, JSON.stringify(landing));
      assert.deepEqual(await evaluate("(() => { const e = document.querySelector('.landing'); return [e.scrollWidth <= e.clientWidth, document.documentElement.scrollWidth <= document.documentElement.clientWidth]; })()"), [true, true]);
      // Everything the page draws is inside it.
      const outside = await evaluate<string[]>(`(() => { const box = document.querySelector('.landing').getBoundingClientRect();
        return Array.from(document.querySelectorAll('.landing-hero, .landing-hero-actions button, .landing-step, .landing-step button, .landing-card, .landing-row, .landing-card-actions button, .landing-links button, .landing-footer label'))
          .filter(e => { const r = e.getBoundingClientRect(); return r.width === 0 || r.left < box.left - 0.5 || r.right > box.right + 0.5; }).map(e => e.className + ':' + e.textContent.slice(0, 30)); })()`);
      assert.deepEqual(outside, []);
      assert.deepEqual(await text(".landing-hero-actions button"), ["New session", "Open a project", "Documentation"]);
      assert.equal(await count("[data-list=sessions] .landing-row"), 3);
      assert.equal(await count("[data-list=projects] .landing-row"), 1);
      // One column: the lists are one above the other, each as wide as the page.
      const sessions = (await rect("[data-list=sessions]"))!, projects = (await rect("[data-list=projects]"))!;
      assert.ok(Math.abs(sessions.left - projects.left) < 1 && projects.top >= sessions.bottom, JSON.stringify({ sessions, projects }));
      assert.ok(Math.abs(sessions.width - landing.width) < 1, JSON.stringify({ sessions, landing }));
      // The accent is still inside the hero.
      const hero = (await rect(".landing-hero"))!, pixels = (await rect(".landing-pixels"))!;
      assert.ok(pixels.left >= hero.left - 0.5 && pixels.right <= hero.right + 0.5 && pixels.top >= hero.top - 0.5 && pixels.bottom <= hero.bottom + 0.5, JSON.stringify({ hero, pixels }));
      await shot("landing-narrow.png");

      // Wide, the two lists sit side by side.
      await evaluate("landingFixture.show({ width: 1100 })");
      assert.equal(await wait("(() => { const a = document.querySelector('[data-list=sessions]').getBoundingClientRect(), b = document.querySelector('[data-list=projects]').getBoundingClientRect(); return Math.abs(a.top - b.top) < 1 && b.left >= a.right; })()"), true);
      assert.deepEqual(await evaluate("(() => { const e = document.querySelector('.landing'); return e.scrollWidth <= e.clientWidth; })()"), true);
      await shot("landing-wide.png");
    });

    await context.test("the page opens once for the start of the window, when its canvas is declared and the user wants it", async () => {
      // Not before the window is ready, and not before the plugins declare the canvas.
      await evaluate("landingFixture.reset(); landingFixture.startup({ ready: false, declared: true })");
      assert.deepEqual(await state("started"), []);
      await evaluate("landingFixture.startup({ ready: true, declared: false })");
      assert.deepEqual(await state("started"), []);
      await evaluate("landingFixture.startup({ ready: true, declared: true })");
      assert.deepEqual(await state("started"), ["builtin:landing/landing"]);
      // Once: a new listing of the canvases opens nothing more.
      await evaluate("landingFixture.startup({ ready: true, declared: false }); landingFixture.startup({ ready: true, declared: true }); landingFixture.startup({ ready: true, declared: true })");
      assert.deepEqual(await state("started"), ["builtin:landing/landing"]);

      // A new window that is ready with its canvas opens it once, although StrictMode runs the effect twice.
      await evaluate("landingFixture.reset(); landingFixture.startup({ ready: true, declared: true })");
      assert.deepEqual(await state("started"), ["builtin:landing/landing"]);

      // The user does not want it: nothing opens, and turning the preference on later opens nothing in this run of the window.
      await evaluate("landingFixture.reset({ 'codealta.desktop.landing.startup.v1': 'off' }); landingFixture.startup({ ready: true, declared: true })");
      assert.deepEqual(await state("started"), []);
      await evaluate("landingFixture.setPreference('openAtStartup', true); landingFixture.startup({ ready: true, declared: true })");
      assert.deepEqual(await evaluate("landingFixture.preferences().openAtStartup"), true);
      assert.deepEqual(await state("started"), []);
    });

    assert.deepEqual(problems, [], "no exception and no error in the console of the page");
  } finally {
    socket?.close();
    browser?.kill();
    await pause(200);
    await rm(root, { recursive: true, force: true, maxRetries: 5, retryDelay: 200 }).catch(() => { });
  }
});
