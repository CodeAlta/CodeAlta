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
