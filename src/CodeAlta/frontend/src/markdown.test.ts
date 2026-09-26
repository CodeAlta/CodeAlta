import assert from "node:assert/strict";
import test from "node:test";
import { renderMarkdownHtml } from "./markdown";

test("renders persisted GFM content instead of flattening it", () => {
  const html = renderMarkdownHtml("# Result\n\n- first\n- second\n\n```cs\nvar answer = 42;\n```\n\n| A | B |\n| - | - |\n| 1 | 2 |");

  assert.match(html, /<h1>Result<\/h1>/);
  assert.match(html, /<li>first<\/li>/);
  assert.match(html, /language-cs/);
  assert.match(html, /var answer = 42;/);
  assert.match(html, /<table>/);
});

test("keeps raw Markdown HTML inert for the sanitizer boundary", () => {
  const html = renderMarkdownHtml("before<script>alert('no')</script><button>run</button>after");

  assert.doesNotMatch(html, /<script>/);
  assert.doesNotMatch(html, /<button>/);
  assert.match(html, /&lt;script&gt;/);
  assert.match(html, /&lt;button&gt;/);
});

test("only explicit timeline code opts into trusted keyboard regions without changing source text", () => {
  const source = "```ts\n<a> & 界🙂\n```\n\n    indented\n\n<pre tabindex=0 onclick=evil()>raw</pre>";
  const html = renderMarkdownHtml(source, true);
  assert.equal((html.match(/class="timeline-code" tabindex="0" role="region" aria-label="Code block"/g) ?? []).length, 2);
  assert.match(html, /class="language-ts">&lt;a&gt; &amp; 界🙂/);
  assert.match(html, /&lt;pre tabindex=0 onclick=evil\(\)&gt;/);
  assert.doesNotMatch(renderMarkdownHtml(source), /class="timeline-code"|role="region"/);
});
