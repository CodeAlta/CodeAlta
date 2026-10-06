import assert from "node:assert/strict";
import test from "node:test";
import { createElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import { DiffPreview } from "./DiffPreview";
import { diffLineCounts, parseUnifiedDiff } from "./unifiedDiff";

const edit = `diff --git a/src/app.ts b/src/app.ts
index 1111111..2222222 100644
--- a/src/app.ts
+++ b/src/app.ts
@@ -1,4 +1,5 @@ export function run() {
 alpha
-beta
+BETA
 gamma
+delta
\\ No newline at end of file
`;

test("a unified diff becomes rows with the line numbers of both sides", () => {
  const rows = parseUnifiedDiff(edit);
  assert.deepEqual(rows.map(row => [row.kind, row.oldLine, row.newLine, row.text]), [
    ["file", null, null, "src/app.ts"], ["hunk", null, null, "export function run() {"], ["context", 1, 1, "alpha"], ["removed", 2, null, "beta"],
    ["added", null, 2, "BETA"], ["context", 3, 3, "gamma"], ["added", null, 4, "delta"], ["note", null, null, "No newline at end of file"]]);
  assert.deepEqual(diffLineCounts(rows), { added: 2, removed: 1 });
});

test("several files, a new file, and hunks without any file header are read as they are", () => {
  const rows = parseUnifiedDiff("diff --git a/a.txt b/a.txt\nnew file mode 100644\n--- /dev/null\n+++ b/a.txt\n@@ -0,0 +1,2 @@\n+one\n+two\n"
    + "diff --git a/old name.md b/new name.md\nsimilarity index 90%\nrename from old name.md\nrename to new name.md\n--- a/old name.md\n+++ b/new name.md\n@@ -3 +3 @@\n-x\n+y\r\n");
  assert.deepEqual(rows.filter(row => row.kind === "file").map(row => row.text), ["a.txt", "new name.md"]);
  assert.deepEqual(rows.filter(row => row.kind === "added").map(row => [row.newLine, row.text]), [[1, "one"], [2, "two"], [3, "y"]]);
  // The rows of one file as the host cuts them: no header at all, or only the two path lines.
  assert.deepEqual(parseUnifiedDiff("@@ -7,2 +7,2 @@\n-a\n+b\n c").map(row => [row.kind, row.oldLine, row.newLine]),
    [["hunk", null, null], ["removed", 7, null], ["added", null, 7], ["context", 8, 8]]);
  assert.deepEqual(parseUnifiedDiff("--- a/x.cs\n+++ b/x.cs\n@@ -1 +1 @@\n-a\n+b").map(row => row.kind), ["file", "hunk", "removed", "added"]);
  // A line of a hunk that looks like a header is still a line of the hunk.
  assert.deepEqual(parseUnifiedDiff("@@ -1,2 +1,2 @@\n--- not a header\n+++ nor this").map(row => [row.kind, row.text]),
    [["hunk", ""], ["removed", "-- not a header"], ["added", "++ nor this"]]);
});

test("a text that is no diff comes back as its lines; a very long one is cut", () => {
  assert.deepEqual(parseUnifiedDiff("Binary files a/x.png and b/x.png differ").map(row => [row.kind, row.oldLine, row.text]),
    [["context", null, "Binary files a/x.png and b/x.png differ"]]);
  assert.deepEqual(parseUnifiedDiff(""), []);
  const long = parseUnifiedDiff("@@ -1,5000 +1,5000 @@\n" + " line\n".repeat(5000));
  assert.equal(long.length, 4001);
  assert.equal(long.at(-1)!.kind, "note");
});

test("the preview renders the lines as text, never as markup", () => {
  const html = renderToStaticMarkup(createElement(DiffPreview, { text: "diff --git a/<b>.ts b/<b>.ts\n@@ -1 +1 @@\n-<script>alert(1)</script>\n+<img src=x>" }));
  assert.ok(html.includes("&lt;b&gt;.ts"), html);
  assert.ok(html.includes("&lt;script&gt;alert(1)&lt;/script&gt;") && html.includes("&lt;img src=x&gt;"), html);
  assert.ok(!html.includes("<script>") && !html.includes("<img"), html);
  assert.equal(html.match(/data-kind="added"/g)?.length, 1);
  assert.equal(html.match(/data-kind="removed"/g)?.length, 1);
});
