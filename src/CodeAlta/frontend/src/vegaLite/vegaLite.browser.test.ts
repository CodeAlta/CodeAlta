import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { mkdtemp, readFile, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";
import { build } from "esbuild";
import { browserBaseArgs, browserExecutable, endBrowser, waitForBrowserPort } from "../browserTarget";
import { testLimit, testLimitRanOut } from "../testTime";

const edge = browserExecutable;

const fence = (spec: unknown) => `\`\`\`vega-lite\n${typeof spec === "string" ? spec : JSON.stringify(spec, null, 2)}\n\`\`\``;
const inline = { $schema: "https://vega.github.io/schema/vega-lite/v6.json", title: "Inline",
  data: { values: [{ step: "build", seconds: 42 }, { step: "test", seconds: 97 }, { step: "pack", seconds: 12 }] },
  mark: { type: "bar", clip: true }, encoding: { x: { field: "step", type: "nominal" }, y: { field: "seconds", type: "quantitative" }, color: { field: "step" },
    href: { field: "step" } } };
const fromFile = { data: { url: "data/runs.csv" }, encoding: { x: { field: "run", type: "quantitative" }, y: { field: "ms", type: "quantitative" } },
  layer: [{ mark: "line" }, { mark: "text", encoding: { text: { field: "label" } } }] };

test("vega-lite blocks are drawn as charts under the production policy, from the spec or from files of the project, and reach nothing", { skip: !edge, timeout: testLimit(180_000) }, async () => {
  const directory = await mkdtemp(join(tmpdir(), "codealta-vega-lite-"));
  let browser: ReturnType<typeof spawn> | undefined, socket: WebSocket | undefined;
  try {
    const config = JSON.parse(await readFile(new URL("../../../neoastra.json", import.meta.url), "utf8")) as { assets: { csp: string } };
    const origin = "https://charts.invalid", page = `${origin}/index.html`;
    // The worker is a file of its own, as the build makes it, served with the policy of the page.
    const [fixture, worker] = await Promise.all([
      build({ entryPoints: [fileURLToPath(new URL("./vegaLite.mount.tsx", import.meta.url))], bundle: true, platform: "browser", format: "iife", write: false,
        plugins: [{ name: "chart-worker", setup(bundle) {
          bundle.onResolve({ filter: /vegaLiteWorkerStart$/ }, args => ({ path: args.path, namespace: "chart-worker" }));
          bundle.onLoad({ filter: /.*/, namespace: "chart-worker" }, () => ({ contents: 'export function startChartWorker() { return new Worker("/chart-worker.js", { name: "Charts" }); }', loader: "js" }));
        } }] }),
      build({ entryPoints: [fileURLToPath(new URL("./vegaLite.worker.ts", import.meta.url))], bundle: true, platform: "browser", format: "iife", write: false }),
    ]);
    const styles = await Promise.all(["../../node_modules/@blueprintjs/core/lib/css/blueprint.css", "../style.css", "../charts/charts.css", "../editor/editor.css"]
      .map(path => readFile(new URL(path, import.meta.url), "utf8")));
    const assets = new Map<string, { type: string; body: string }>([
      [page, { type: "text/html", body: '<!doctype html><html data-theme="dark" class="bp6-dark"><head><meta charset="utf-8"><link rel="icon" href="data:,"><link rel="stylesheet" href="/style.css"></head><body class="bp6-dark"><div id="root" style="width:900px"></div><script src="/fixture.js"></script></body></html>' }],
      [`${origin}/fixture.js`, { type: "text/javascript", body: fixture.outputFiles[0].text }],
      [`${origin}/chart-worker.js`, { type: "text/javascript", body: worker.outputFiles[0].text }],
      [`${origin}/style.css`, { type: "text/css", body: styles.join("\n") }],
    ]);
    browser = spawn(edge!, [...browserBaseArgs, "--host-resolver-rules=MAP * ~NOTFOUND", `--user-data-dir=${join(directory, "profile")}`, "--remote-debugging-port=0", "about:blank"],
      { stdio: ["ignore", "ignore", "pipe"], windowsHide: true });
    const port = await waitForBrowserPort(join(directory, "profile"), browser);
    const pages = await (await fetch(`http://127.0.0.1:${port}/json/list`, { signal: AbortSignal.timeout(testLimit(5000)) })).json() as { type: string; url: string; webSocketDebuggerUrl: string }[];
    socket = new WebSocket(pages.find(p => p.type === "page" && p.url === "about:blank")!.webSocketDebuggerUrl);
    await new Promise<void>((resolve, reject) => { socket!.addEventListener("open", () => resolve(), { once: true }); socket!.addEventListener("error", reject, { once: true }); });
    let sequence = 0;
    type Reply = { result?: { value?: unknown }; exceptionDetails?: unknown };
    const pending = new Map<number, (reply: { result?: Reply; error?: unknown }) => void>();
    const command = (method: string, params: object = {}, sessionId?: string) => new Promise<Reply>((resolve, reject) => {
      const id = ++sequence;
      const timer = setTimeout(() => { pending.delete(id); reject(new Error(testLimitRanOut(testLimit(60_000), `the answer to ${method}`) + `\n${failures.join("\n")}`)); }, testLimit(60_000));
      pending.set(id, reply => { clearTimeout(timer); reply.error ? reject(new Error(JSON.stringify(reply.error))) : resolve(reply.result ?? {}); });
      socket!.send(JSON.stringify({ id, method, params, ...(sessionId ? { sessionId } : {}) }));
    });
    const requests: string[] = [], failures: string[] = [], workers: string[] = [];
    socket.addEventListener("message", event => {
      const m = JSON.parse(String(event.data));
      if (pending.has(m.id)) { pending.get(m.id)!(m); pending.delete(m.id); }
      if (m.method === "Fetch.requestPaused") {
        const { requestId, request } = m.params;
        const asset = assets.get(request.url);
        const action = asset
          ? command("Fetch.fulfillRequest", { requestId, responseCode: 200, responseHeaders: [{ name: "Content-Type", value: asset.type },
            { name: "Content-Security-Policy", value: config.assets.csp }], body: Buffer.from(asset.body).toString("base64") }, m.sessionId)
          : (failures.push(`request: ${request.url}`), command("Fetch.failRequest", { requestId, errorReason: "BlockedByClient" }, m.sessionId));
        void action.catch(error => failures.push(String(error)));
      }
      // Every request of the page and of its worker: the assets, and pictures of the page itself (`data:`), nothing else.
      if (m.method === "Network.requestWillBeSent") {
        const url = String(m.params.request.url);
        requests.push(url);
        if (!assets.has(url) && !url.startsWith("data:")) failures.push(`request attempt: ${url}`);
      }
      if (m.method === "Runtime.exceptionThrown") failures.push(JSON.stringify(m.params.exceptionDetails).slice(0, 500));
      if (m.method === "Log.entryAdded" && m.params.entry.source === "security") failures.push(String(m.params.entry.text));
      // The worker is a target of its own: its requests and its console are watched as the page's.
      if (m.method === "Target.attachedToTarget") {
        const child = m.params.sessionId as string;
        workers.push(child);
        // A worker has no Fetch of its own: the page's intercepts its requests.
        void Promise.all([command("Network.enable", {}, child), command("Log.enable", {}, child), command("Runtime.enable", {}, child)])
          .catch(error => failures.push(String(error))).finally(() => command("Runtime.runIfWaitingForDebugger", {}, child).catch(error => failures.push(String(error))));
      }
    });
    await command("Page.enable"); await command("Runtime.enable"); await command("Network.enable"); await command("Log.enable");
    await command("Fetch.enable", { patterns: [{ urlPattern: "*", requestStage: "Request" }] });
    await command("Target.setAutoAttach", { autoAttach: true, waitForDebuggerOnStart: true, flatten: true });
    await command("Page.navigate", { url: page });
    const evaluate = async <T,>(expression: string): Promise<T> => {
      const reply = await command("Runtime.evaluate", { expression, returnByValue: true, awaitPromise: true });
      assert.equal(reply.exceptionDetails, undefined, JSON.stringify(reply.exceptionDetails)); return reply.result?.value as T;
    };
    const wait = (expression: string, milliseconds = testLimit(30_000)) => evaluate<boolean>(
      `new Promise(resolve=>{const end=Date.now()+${milliseconds};const tick=()=>{let ok=false;try{ok=!!(${expression})}catch{}ok?resolve(true):Date.now()>end?resolve(false):setTimeout(tick,25)};tick()})`);
    assert.equal(await wait("window.chartFixture"), true);
    const content = "document.querySelector('.markdown-content')";
    const shown = () => evaluate<string[]>(`[...${content}.children].map(e=>e.tagName+(e.className?'.'+e.className.replaceAll(' ','.'):''))`);
    const chart = `${content}.querySelector('.markdown-rendered > .markdown-diagram.markdown-chart > svg')`;

    // A chart of a message, from its spec: drawn in the worker, in the font and the colors of the window, with nothing live in it.
    await evaluate(`chartFixture.render(${JSON.stringify(`Before.\n\n${fence(inline)}\n\nAfter.`)}, "message")`);
    assert.equal(await wait(chart), true, JSON.stringify(await shown()) + failures.join("\n"));
    assert.deepEqual(await shown(), ["P", "DIV.markdown-rendered", "P"]);
    const drawn = await evaluate<{ texts: string[]; live: number; ids: string[]; references: string[]; background: string; font: string; source: string; language: string }>(`(() => {
      const svg = ${chart};
      return { texts: [...svg.querySelectorAll('text')].map(t => t.textContent),
        live: svg.querySelectorAll('a, image, use, script, foreignObject, style, [href], [*|href], [onload], [onclick]').length,
        ids: [...svg.querySelectorAll('[id]')].map(e => e.id), references: [...svg.querySelectorAll('[clip-path]')].map(e => e.getAttribute('clip-path')),
        background: svg.querySelector(':scope > rect')?.getAttribute('fill'), font: svg.querySelector('text')?.getAttribute('font-family'),
        source: ${content}.querySelector('.markdown-rendered > pre > code').textContent, language: ${content}.querySelector('.markdown-rendered > pre').dataset.language }; })()`);
    assert.ok(drawn.texts.includes("Inline") && drawn.texts.includes("build") && drawn.texts.includes("seconds"), JSON.stringify(drawn.texts));
    assert.equal(drawn.live, 0, "no link, image, script, style or handler");
    assert.ok(drawn.ids.length > 0 && drawn.ids.every(id => id.startsWith("codealta-chart-")), JSON.stringify(drawn.ids));
    assert.ok(drawn.references.length > 0 && drawn.references.every(reference => /^url\(#codealta-chart-\d+-/u.test(reference!)), JSON.stringify(drawn.references));
    const panel = await evaluate<string>("getComputedStyle(document.querySelector('.markdown-diagram')).backgroundColor");
    assert.equal(await evaluate<string>(`(() => { const probe = document.createElement('span'); probe.style.color = ${JSON.stringify(drawn.background)}; document.body.append(probe); const color = getComputedStyle(probe).color; probe.remove(); return color; })()`),
      panel, "the chart is drawn on the color behind it");
    assert.equal(drawn.font, await evaluate<string>("getComputedStyle(document.documentElement).fontFamily"));
    assert.equal(drawn.language, "vega-lite");
    assert.deepEqual(JSON.parse(drawn.source), inline, "the source is kept beside the drawing");

    // The theme changes: the chart is drawn again in the colors of the other one, and the old drawing stays meanwhile.
    await evaluate("document.documentElement.dataset.theme = 'light'; document.documentElement.classList.remove('bp6-dark'); document.body.classList.remove('bp6-dark')");
    assert.equal(await wait(`${chart} && ${chart}.querySelector(':scope > rect').getAttribute('fill') !== ${JSON.stringify(drawn.background)}`), true, "drawn again for the light theme");
    assert.equal(await evaluate<string>("getComputedStyle(document.querySelector('.markdown-diagram')).backgroundColor") !== panel, true);

    // Expand: the chart in a window of its own, which copies it as PNG, as SVG and as its spec.
    await evaluate(`${content}.querySelector('.markdown-rendered button.markdown-diagram-expand').click()`);
    const dialog = "document.querySelector('dialog.diagram-viewer-dialog[open]')";
    assert.equal(await wait(`${dialog}?.querySelector('.editor-image-view img')?.naturalWidth > 0`), true, "the chart is drawn as a picture");
    assert.equal(await evaluate<string>(`${dialog}.querySelector('.app-window-titlebar').textContent`).then(text => text.includes("Chart")), true);
    const header = (name: string) => `${dialog}.querySelector('.app-window-titlebar button.${name}')`;
    await evaluate(`${header("diagram-viewer-copy-image")}.click()`);
    assert.equal(await wait("chartClipboard.images.length === 1"), true, "the PNG is copied");
    assert.equal(await evaluate<string>("chartClipboard.images[0].type"), "image/png");
    await evaluate(`${header("diagram-viewer-copy-svg")}.click()`);
    assert.equal(await wait("chartClipboard.text.length === 1"), true, "the SVG is copied");
    assert.match(await evaluate<string>("chartClipboard.text[0]"), /^<svg[\s\S]*Inline[\s\S]*<\/svg>$/u);
    await evaluate(`${header("diagram-viewer-copy-source")}.click()`);
    assert.equal(await wait("chartClipboard.text.length === 2"), true, "the spec is copied");
    assert.deepEqual(JSON.parse(await evaluate<string>("chartClipboard.text[1]")), inline);
    await evaluate(`${dialog}.querySelector('.app-window-titlebar button[aria-label="Close"]')?.click() ?? ${dialog}.close()`);

    // A chart of a document reads its files from the folder of the document, in its project; the files of a message come from its session.
    await evaluate(`chartFixture.setFile("docs/data/runs.csv", "run,ms,label\\n1,40,\\n2,35,\\n3,52,from-the-file\\n")`);
    await evaluate(`chartFixture.render(${JSON.stringify(`# Report\n\n${fence(fromFile)}\n`)}, "document")`);
    assert.equal(await wait(`${chart}?.textContent.includes('from-the-file')`), true, JSON.stringify(await shown()) + failures.join("\n"));
    assert.deepEqual(await evaluate("chartReads.at(-1)"), { path: "data/runs.csv", scope: { projectId: "p1", directory: "docs" } });
    // The file changes: a render more than a few seconds later reads it again and draws the new data.
    await evaluate(`chartFixture.setFile("docs/data/runs.csv", "run,ms,label\\n1,40,changed-data\\n")`);
    await new Promise(resolve => setTimeout(resolve, 5_200));
    await evaluate("chartFixture.clear()");
    await evaluate(`chartFixture.render(${JSON.stringify(`# Report\n\n${fence(fromFile)}\n`)}, "document")`);
    assert.equal(await wait(`${chart}?.textContent.includes('changed-data')`), true, "the changed file is drawn");

    // What is not drawn stays code, labelled, with one line that says why.
    const refused = async (source: string, surface: "message" | "document", why: RegExp) => {
      await evaluate("chartFixture.clear()");
      await evaluate(`chartFixture.render(${JSON.stringify(source)}, ${JSON.stringify(surface)})`);
      assert.equal(await wait(`${content}?.querySelector('.markdown-diagram-refused')`), true, `${source}: ${JSON.stringify(await shown())}`);
      assert.deepEqual(await shown(), [surface === "message" ? "PRE.timeline-code" : "PRE", "P.markdown-diagram-refused"]);
      assert.match(await evaluate<string>(`${content}.querySelector('.markdown-diagram-refused').textContent`), why);
      assert.ok(["vega-lite", "vegalite"].includes(await evaluate<string>(`${content}.querySelector('pre').dataset.language`)));
    };
    await refused(fence("{ \"mark\": "), "message", /^Not drawn as a chart: Not JSON: /u);
    await refused(fence({ data: { url: "https://example.com/data.csv" }, mark: "bar" }), "message", /^Not drawn as a chart: Data from the web is not read: https:\/\/example\.com\/data\.csv$/u);
    await refused(fence({ data: { url: "../secrets.csv" }, mark: "bar" }), "document", /^Not drawn as a chart: Not a path inside the project: \.\.\/secrets\.csv$/u);
    await refused(fence({ data: { url: "out/missing.csv" }, mark: "bar" }), "message", /^Not drawn as a chart: out\/missing\.csv was not found in the project$/u);
    await refused("```vegalite\n" + JSON.stringify({ mark: "bar", encoding: { x: { field: "a", axis: { tickCount: 1e9 } } } }) + "\n```", "message", /^Not drawn as a chart: tickCount is larger than 1000$/u);
    await refused(fence({ data: { values: [{ a: 1 }] }, mark: "point", transform: [{ calculate: "length(sequence(0, 1e9))", as: "n" }] }), "message", /^Not drawn as a chart: A sequence is too long to draw$/u);
    // An image of the web is never loaded.
    await evaluate("chartFixture.clear()");
    await evaluate(`chartFixture.render(${JSON.stringify(fence({ data: { values: [{ u: "https://example.com/a.png", x: 1 }] }, mark: "image", encoding: { url: { field: "u" }, x: { field: "x", type: "quantitative" } } }))}, "message")`);
    assert.equal(await wait(chart), true);
    assert.equal(await evaluate(`${chart}.querySelectorAll('image, [href], [*|href]').length`), 0);

    // A color scale of quantities takes the ramp of the window, whose steps the stylesheet writes with `color-mix`: each
    // one reaches the chart as a color it can read, so the cells differ and none is black.
    await evaluate("chartFixture.clear()");
    await evaluate(`chartFixture.render(${JSON.stringify(fence({ data: { values: [{ a: "x", n: 1 }, { a: "y", n: 5 }, { a: "z", n: 9 }] }, mark: "rect",
      encoding: { x: { field: "a", type: "nominal" }, color: { field: "n", type: "quantitative", legend: null } } }))}, "message")`);
    assert.equal(await wait(chart), true);
    const cells = await evaluate<string[]>(`[...${chart}.querySelectorAll('.mark-rect path')].map(e => e.getAttribute('fill'))`);
    assert.equal(new Set(cells).size, 3, JSON.stringify(cells));
    assert.ok(cells.every(fill => !/^rgb\(0, 0, 0\)$|^#000(000)?$/u.test(fill)), JSON.stringify(cells));
    // A chart of categories is as wide as a chart of quantities, however few its categories are.
    assert.equal(await evaluate(`Number(${chart}.getAttribute('width')) >= 440`), true, "three categories fill the width of a chart");

    // A narrow window shows a chart whole: it is no wider than its block.
    await evaluate("chartFixture.clear(); document.getElementById('root').style.width = '320px'");
    await evaluate(`chartFixture.render(${JSON.stringify(fence({ ...inline, width: 600 }))}, "message")`);
    assert.equal(await wait(chart), true);
    assert.equal(await evaluate(`${chart}.getBoundingClientRect().width <= ${content}.querySelector('.markdown-diagram').clientWidth`), true);

    assert.ok(requests.includes(`${origin}/chart-worker.js`), "the worker started from its own file");
    // The worker runs under the policy of the page: it compiles no code from text, so the charts above were drawn without it.
    assert.equal(workers.length, 1, "one worker drew every chart");
    const compiled = await command("Runtime.evaluate", { expression: "(() => { try { new Function('return 1')(); return 'allowed'; } catch { return 'refused'; } })()", returnByValue: true,
      allowUnsafeEvalBlockedByCSP: false }, workers[0]);
    assert.equal(compiled.result?.value, "refused");
    failures.splice(0, failures.length, ...failures.filter(text => !/unsafe-eval|EvalError/u.test(text)));
    assert.deepEqual(failures, [], "no request but the assets, no error, no violation of the policy");
  } finally {
    if (socket?.readyState === WebSocket.OPEN) socket.send(JSON.stringify({ id: 9999, method: "Browser.close" }));
    socket?.close();
    await endBrowser(browser);
    await rm(directory, { recursive: true, force: true, maxRetries: 10, retryDelay: 100 });
  }
});
