import assert from "node:assert/strict";
import test from "node:test";
import { createMarkdownParser, frontMatterEntries, maximumFrontMatterLength, splitFrontMatter } from "./markdown";

test("browser-independent parser supports CommonMark baseline, explicit tables/strike/linkify and breaks", () => {
  const parse = createMarkdownParser();
  const html = parse("# Result\n\n- first\n- second\n\n```cs\nvar answer = 42;\n```\n\n| A | B |\n| :- | -: |\n| 1 | 2 |\n\n~~strike~~\nnext https://example.invalid/");
  for (const pattern of [/<h1>Result<\/h1>/, /<li>first<\/li>/, /language-cs/, /var answer = 42;/, /<table>/, /align="left"/, /<s>strike<\/s><br \/>/, /href="https:\/\/example.invalid\//]) assert.match(html, pattern);
  assert.doesNotMatch(html, /style=/);
});

test("parser output explicitly remains untrusted: useful and hostile raw HTML reach the mandatory boundary", () => {
  const html = createMarkdownParser()("before<b>useful</b><script>alert('no')</script><button>run</button>after");
  assert.match(html, /<b>useful<\/b>/); assert.match(html, /<script>/); assert.match(html, /<button>/);
  // Safety is tested through actual MarkdownContent, not claimed by the pure parser.
});

test("plain code preserves text/newlines without authorizing timeline attributes or language loaders", () => {
  const parse = createMarkdownParser();
  const source = "```ts\r\n<a> & \u754c\u{1f642}\r\n```\r\n\r\n    indented\n\n```bad\"class\nplain\n```";
  const html = parse(source);
  assert.match(html, /class="language-ts">&lt;a&gt; &amp; \u754c\u{1f642}\n<\/code>/u);
  assert.match(html, /<code>indented\n<\/code>/);
  assert.match(html, /<code>plain\n<\/code>/);
  assert.doesNotMatch(html, /timeline-code|tabindex|role=|language-bad/);
  assert.equal(source.includes("\r\n"), true);
});

test("a message breaks its lines where its text does, and the lines of a document follow each other", () => {
  const source = "first line\nsecond line\n\nnext paragraph";
  assert.match(createMarkdownParser()(source), /first line<br \/>\nsecond line/);
  assert.match(createMarkdownParser({ breaks: true })(source), /<br \/>/);
  const document = createMarkdownParser({ breaks: false })(source);
  assert.match(document, /<p>first line\nsecond line<\/p>\n<p>next paragraph<\/p>/);
  assert.doesNotMatch(document, /<br/);
  // Two spaces or a backslash at the end of a line still break it.
  assert.match(createMarkdownParser({ breaks: false })("one  \ntwo\\\nthree"), /one<br \/>\ntwo<br \/>\nthree/);
});

test("the front matter of a document is taken off its text, and a text that starts with a rule keeps it", () => {
  assert.deepEqual(splitFrontMatter("---\nname: a\ndescription: b\n---\n\n# Title\n"), { frontMatter: "name: a\ndescription: b", body: "\n# Title\n" });
  assert.deepEqual(splitFrontMatter("\ufeff---\r\nname: a\r\n---\r\nBody"), { frontMatter: "name: a", body: "Body" });
  assert.deepEqual(splitFrontMatter("--- \ntitle: \"A: b\"\n...\nBody"), { frontMatter: "title: \"A: b\"", body: "Body" });
  assert.deepEqual(splitFrontMatter("---\n# what follows is read by the site\n\nlayout: page\n---"), { frontMatter: "# what follows is read by the site\n\nlayout: page", body: "" });
  // The whole text is the body when it has no front matter: a rule, a heading under a rule, a block that is not closed.
  for (const text of ["", "# Title\n\n---\n\ntext", "---", "---\n", "---\n\n---\n", "---\nSome text\n---\n", "---\n- item\n---\n", "--- not a rule\nname: a\n---\n",
    "----\nname: a\n---\n", " ---\nname: a\n---\n", "---\nname: a\n", "---\nname: a\n--- \nmore", "text\n---\nname: a\n---\n"]) {
    const kept = text === "---\nname: a\n--- \nmore" ? { frontMatter: "name: a", body: "more" } : { frontMatter: null, body: text };
    assert.deepEqual(splitFrontMatter(text), kept, JSON.stringify(text));
  }
  // A front matter longer than what is taken off stays in the text.
  const long = `---\nname: ${"x".repeat(maximumFrontMatterLength)}\n---\nBody`;
  assert.deepEqual(splitFrontMatter(long), { frontMatter: null, body: long });
});

test("the entries of a front matter are its keys with their text, their items, or the YAML they are written in", () => {
  assert.deepEqual(frontMatterEntries([
    "name: release-notes", "description: \"Writes: the \\\"notes\\\"\"   ", "license: 'It''s MIT' ", "count: 3 # a comment", "empty:", "# a comment of its own",
    "\"quoted key\": yes", "url: https://example.invalid/a#b"].join("\n")), [
    { key: "name", text: "release-notes" }, { key: "description", text: "Writes: the \"notes\"" }, { key: "license", text: "It's MIT" }, { key: "count", text: "3" },
    { key: "empty", text: "" }, { key: "quoted key", text: "yes" }, { key: "url", text: "https://example.invalid/a#b" }]);
  // A value that goes on: a block of text, a folded one, and a plain value on several lines.
  assert.deepEqual(frontMatterEntries("literal: |\n  first\n  second\n\nfolded: >-\n  one\n  two\n\n  three\nplain: starts here\n  and goes on\n"), [
    { key: "literal", text: "first\nsecond" }, { key: "folded", text: "one two\n\nthree" }, { key: "plain", text: "starts here and goes on" }]);
  // A list of plain items is its items, indented or not; anything nested is kept as it is written.
  assert.deepEqual(frontMatterEntries("tags:\n  - docs\n  - \"release notes\"\nkeywords:\n- a\n- b\nallowed-tools: Bash(dnx:*)\nmetadata:\n  owner: me\n  level: 2\nsteps:\n  - name: build\n    run: make\nflow: [a, b]"), [
    { key: "tags", items: ["docs", "release notes"] }, { key: "keywords", items: ["a", "b"] }, { key: "allowed-tools", text: "Bash(dnx:*)" },
    { key: "metadata", yaml: "owner: me\nlevel: 2" }, { key: "steps", yaml: "- name: build\n  run: make" }, { key: "flow", text: "[a, b]" }]);
  // A front matter that is no list of keys has no entries: it is shown as the YAML it is.
  for (const text of ["just a sentence", "- item\n- other", "name: a\nnot a key", "key:value"]) assert.equal(frontMatterEntries(text), null, text);
  assert.deepEqual(frontMatterEntries(""), []);
});

test("deliberate policy differences: escaped image alt, tasks left to the boundary, no fuzzy links or grid/footnote plugins", () => {
  const html = createMarkdownParser()('![alt <b>](https://example.invalid/a)\n\n- [x] task\n\nwww.example.invalid x@y.invalid\n\n[^note]\n\n[^note]: text');
  assert.match(html, /alt &lt;b&gt;/); assert.match(html, /\[x\] task/);
  assert.doesNotMatch(html, /<img|<input|href="(?:https?:|mailto:)|class="footnote/);
  assert.match(html, /<a href="text">\^note<\/a>/); // Ordinary reference link, not a footnote extension: a link to a file named "text".
});

test("a link to a file is a link, and the parser marks the ones it wrote", () => {
  const source = "[Program.cs](src/Program.cs#L42) [win](<C:\\my code\\a.cs>) [page](file:///C:/out/report.html) <file:///tmp/a.txt> see file:///tmp/b.txt. "
    + '[web](https://example.invalid/) [mail](mailto:a@b.invalid) <a href="src/raw.cs">raw</a>';
  const plain = createMarkdownParser()(source);
  for (const href of ["src/Program.cs#L42", "C:%5Cmy%20code%5Ca.cs", "file:///C:/out/report.html", "file:///tmp/a.txt", "file:///tmp/b.txt", "https://example.invalid/"]) assert.ok(plain.includes(`<a href="${href}">`), href);
  assert.doesNotMatch(plain, /data-file-link/);
  // With a mark, each link of the Markdown that names a file carries it; a page of the web, another scheme and an authored <a> do not.
  const marked = createMarkdownParser({ fileLinkMark: "mark-1" })(source);
  for (const href of ["src/Program.cs#L42", "C:%5Cmy%20code%5Ca.cs", "file:///C:/out/report.html", "file:///tmp/a.txt", "file:///tmp/b.txt"]) assert.ok(marked.includes(`<a href="${href}" data-file-link="mark-1">`), href);
  assert.equal(marked.match(/data-file-link/g)?.length, 5);
  assert.ok(marked.includes('<a href="https://example.invalid/">web</a>') && marked.includes('<a href="mailto:a@b.invalid">mail</a>') && marked.includes('<a href="src/raw.cs">raw</a>'));
  // Scripts are still no link.
  assert.doesNotMatch(createMarkdownParser()("[x](javascript:alert(1)) [y](data:text/html,x) [z](vbscript:x)"), /<a /);
});
