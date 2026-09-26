import assert from "node:assert/strict";
import test from "node:test";
import { createMarkdownParser } from "./markdown";

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

test("deliberate policy differences: escaped image alt, literal tasks, no fuzzy links or grid/footnote plugins", () => {
  const html = createMarkdownParser()('![alt <b>](https://example.invalid/a)\n\n- [x] task\n\nwww.example.invalid x@y.invalid\n\n[^note]\n\n[^note]: text');
  assert.match(html, /alt &lt;b&gt;/); assert.match(html, /\[x\] task/);
  assert.doesNotMatch(html, /<img|<input|href="(?:https?:|mailto:)|class="footnote/);
  assert.match(html, /<a href="text">\^note<\/a>/); // Ordinary reference link, not a footnote extension; boundary removes relative href.
});
