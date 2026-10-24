import assert from "node:assert/strict";
import test from "node:test";
import { createDiagramPages, diagramPageSvg, linkTarget } from "./markdownView";

const look = { key: "dark|", variables: { background: "#2f343c", textColor: "#f6f7f9", darkMode: true } };

test("a diagram page gets the width Mermaid gave the drawing as a value it reads", () => {
  const svg = '<svg id="d" style="max-width: 312.5px;" viewBox="0 0 312.5 80"><g/></svg>';
  assert.equal(diagramPageSvg(svg), '<svg data-w="312.5" style="--w:312.5px" id="d" viewBox="0 0 312.5 80"><g/></svg>');
  assert.equal(diagramPageSvg("<svg><g/></svg>"), "<svg><g/></svg>");
});

test("a diagram page is made on the server the first time it is opened, once per drawing and theme", async () => {
  const uploads: unknown[] = [];
  const pages = createDiagramPages(async body => { uploads.push(body); return { url: `/d/${uploads.length}` }; });
  assert.equal(pages.known("<svg/>", look), undefined, "nothing is made before it is opened");
  assert.equal(uploads.length, 0);
  const [first, second] = await Promise.all([pages.open("<svg/>", look, "https://pc.ts.net/"), pages.open("<svg/>", look, "https://pc.ts.net/")]);
  assert.equal(first, "https://pc.ts.net/d/1");
  assert.equal(second, first);
  assert.deepEqual(uploads, [{ svg: "<svg/>", background: "#2f343c", text: "#f6f7f9", dark: true }]);
  assert.equal(pages.known("<svg/>", look), first);
  await pages.open("<svg/>", { ...look, key: "light|" }, "https://pc.ts.net/");
  assert.equal(uploads.length, 2, "another theme is another page");
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
