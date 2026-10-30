import assert from "node:assert/strict";
import test from "node:test";
import { createDiagramPages, diagramPageSvg, linkTarget } from "./markdownView";

const look = { dark: true, variables: { "--pele-bg": "#2f343c", "--pele-fg": "#f6f7f9" } };

test("a diagram page gets the width of the drawing as a value it reads, and the colors it had in the page", () => {
  const svg = '<svg class="pele" viewBox="0 0 312.5 80" width="312.5" height="80" style="max-width:100%;--_bg:var(--pele-bg,#fff)"><g width="9"/></svg>';
  assert.equal(diagramPageSvg(svg, look.variables), '<svg data-w="312.5" class="pele" viewBox="0 0 312.5 80" width="312.5" height="80" '
    + 'style="--w:312.5px;--pele-bg:#2f343c;--pele-fg:#f6f7f9;max-width:100%;--_bg:var(--pele-bg,#fff)"><g width="9"/></svg>');
  assert.equal(diagramPageSvg("<svg><g/></svg>"), "<svg><g/></svg>");
  // A value never leaves its attribute.
  assert.equal(diagramPageSvg("<svg><g/></svg>", { "--pele-bg": 'red"><script>' }), '<svg style="--pele-bg:redscript;"><g/></svg>');
});

test("a diagram page is made on the server the first time it is opened, once per drawing and colors", async () => {
  const uploads: unknown[] = [];
  const pages = createDiagramPages(async body => { uploads.push(body); return { url: `/d/${uploads.length}` }; });
  assert.equal(pages.known("<svg/>", look), undefined, "nothing is made before it is opened");
  assert.equal(uploads.length, 0);
  const [first, second] = await Promise.all([pages.open("<svg/>", look, "https://pc.ts.net/"), pages.open("<svg/>", look, "https://pc.ts.net/")]);
  assert.equal(first, "https://pc.ts.net/d/1");
  assert.equal(second, first);
  assert.deepEqual(uploads, [{ svg: '<svg style="--pele-bg:#2f343c;--pele-fg:#f6f7f9;"/>', background: "#2f343c", text: "#f6f7f9", dark: true }]);
  assert.equal(pages.known("<svg/>", look), first);
  await pages.open("<svg/>", { dark: false, variables: { "--pele-bg": "#edeff2" } }, "https://pc.ts.net/");
  assert.equal(uploads.length, 2, "other colors are another page");
});

test("a page that could not be made is asked for again at the next open", async () => {
  let attempts = 0;
  const pages = createDiagramPages(async () => { attempts++; if (attempts === 1) throw new Error("offline"); return { url: "/d/x" }; });
  assert.equal(await pages.open("<svg/>", look, "https://pc.ts.net/"), null);
  assert.equal(await pages.open("<svg/>", look, "https://pc.ts.net/"), "https://pc.ts.net/d/x");
  assert.equal(attempts, 2);
});

test("a link of a message opens a page of the web; a link to a file of the computer does nothing on the phone", () => {
  assert.equal(linkTarget("https://example.com/a"), "web");
  assert.equal(linkTarget("mailto:me@example.com"), "web");
  assert.equal(linkTarget("src/Program.cs#L3"), "none");
  assert.equal(linkTarget("C:/code/a.md"), "none");
  assert.equal(linkTarget(null), "none");
});
