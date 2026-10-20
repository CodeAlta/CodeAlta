import assert from "node:assert/strict";
import test from "node:test";
import { createMarkdownParser, frontMatterLineCount, splitFrontMatter } from "./markdown";
import { parseSourceLines } from "./markdownSourceLines";

// A plan, with a line of the file for each kind of block.
const plan = [
  "---", "title: Plan", "tags:", "  - a", "---", "",                          // 1-6
  "# Heading", "",                                                             // 7-8
  "Paragraph one", "continues.", "",                                           // 9-11
  "- item a", "  - nested b", "  - nested c", "", "- item d", "",              // 12-17
  "1. first", "2. second", "",                                                 // 18-20
  "| A | B |", "| - | - |", "| 1 | 2 |", "| 3 | 4 |", "",                      // 21-25
  "```ts", "const x = 1;", "```", "",                                          // 26-29
  "```mermaid", "graph TD; A-->B", "```", "",                                  // 30-33
  "    indented", "    code", "",                                            // 34-36
  "> quoted", "> text", "",                                                    // 37-39
  "***", "",                                                                   // 40-41
  '<div data-source-lines="1-2">', '<p data-source-lines="other:1-2">raw</p>', "</div>", "", // 42-45
  "Setext", "======",                                                          // 46-47
].join("\n");
const planBlocks = ["h1 7-7", "p 9-10", "li 12-14", "p 12-12", "li 13-13", "li 14-14", "li 16-16", "p 16-16", "li 18-18", "li 19-19",
  "tr 21-21", "tr 23-23", "tr 24-24", "pre 26-28", "pre 30-32", "pre 34-35", "blockquote 37-38", "p 37-38", "hr 40-40", "h1 46-47"];

// The blocks that the parser marked with the lines of the whole text, as the boundary asks for them.
function render(source: string, document = true) {
  const { body } = splitFrontMatter(source);
  return createMarkdownParser({ breaks: !document })(body, { mark: "m", firstLine: frontMatterLineCount(source, body) + 1 });
}
const blocks = (html: string) => Array.from(html.matchAll(/<(\w+)[^>]*? data-source-lines="m:(\d+)-(\d+)"/g), match => `${match[1]} ${match[2]}-${match[3]}`);

test("each block of a document says which lines of the whole file it comes from, front matter included", () => {
  assert.deepEqual(blocks(render(plan)), planBlocks);
  // A message breaks its lines, and its blocks are the same.
  assert.deepEqual(blocks(render(plan, false)), planBlocks);
  // A block of HTML says nothing: what it writes itself reaches the boundary, which removes it.
  const html = render(plan);
  assert.ok(html.includes('<div data-source-lines="1-2">') && html.includes('<p data-source-lines="other:1-2">raw</p>'));
});

test("CRLF and a byte order mark leave the lines of the blocks as they are", () => {
  assert.deepEqual(blocks(render(plan.replaceAll("\n", "\r\n"))), planBlocks);
  assert.deepEqual(blocks(render(`\ufeff${plan}`)), planBlocks);
  assert.deepEqual(blocks(render(`\ufeff${plan}`.replaceAll("\n", "\r\n"))), planBlocks);
  // Without a front matter, the text starts on line 1.
  for (const text of ["One\n\n> two\n\n- three", "\ufeffOne\r\n\r\n> two\r\n\r\n- three"]) {
    assert.deepEqual(blocks(render(text)), ["p 1-1", "blockquote 3-3", "p 3-3", "li 5-5"], JSON.stringify(text));
  }
});

test("a render that asks for no lines is the render of before", () => {
  const parse = createMarkdownParser({ breaks: false });
  const { body } = splitFrontMatter(plan);
  const plain = parse(body);
  assert.doesNotMatch(plain, /data-source-lines="m:/);
  assert.equal(plain, parse(body, undefined));
  // The lines are the only difference.
  assert.equal(render(plan).replace(/ data-source-lines="m:\d+-\d+"/g, ""), plain);
});

test("the lines of a front matter are its two delimiters and what is between them", () => {
  assert.equal(frontMatterLineCount("# Title\n", "# Title\n"), 0);
  for (const text of ["---\na: b\n---\nBody", "---\na: b\n---", "---\na: b\n...\n\nBody", "\ufeff---\r\na: b\r\n---\r\nBody"]) {
    assert.equal(frontMatterLineCount(text, splitFrontMatter(text).body), 3, JSON.stringify(text));
  }
  assert.equal(frontMatterLineCount(plan, splitFrontMatter(plan).body), 5);
});

test("the lines of a block are read back from its attribute", () => {
  assert.deepEqual(parseSourceLines("3-5"), { start: 3, end: 5 });
  assert.deepEqual(parseSourceLines("1-1"), { start: 1, end: 1 });
  for (const value of [null, "", "5-3", "0-1", "1-0", "3", "a-b", " 3-5", "3-5 ", "m:3-5", "-1-2", "1-2-3", "01-2"]) assert.equal(parseSourceLines(value), null, String(value));
});
