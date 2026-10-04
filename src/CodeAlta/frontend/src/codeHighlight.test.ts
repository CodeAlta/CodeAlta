import assert from "node:assert/strict";
import test from "node:test";
import { highlightCode, maximumHighlightedLength } from "./codeHighlight";

test("known languages and their usual short names are highlighted", () => {
  const csharp = highlightCode("public class Peak { int Height => 42; } // top\n", "cs");
  assert.match(csharp!, /<span class="hljs-keyword">public<\/span>/);
  assert.match(csharp!, /<span class="hljs-number">42<\/span>/);
  assert.match(csharp!, /<span class="hljs-comment">\/\/ top<\/span>/);
  for (const [language, text] of [["javascript", "const a = 1;"], ["JS", "const a = 1;"], ["ts", "let a: number = 1;"], ["tsx", "const a = 1;"],
    ["python", "def f(): return 1"], ["rust", "fn main() {}"], ["go", "func main() {}"], ["json", "{\"a\": 1}"], ["jsonc", "{\"a\": 1}"],
    ["yaml", "a: 1"], ["toml", "a = 1"], ["html", "<p>x</p>"], ["csproj", "<Project />"], ["sql", "select 1"], ["bash", "echo 1"],
    ["sh", "echo 1"], ["powershell", "Get-Item ."], ["ps1", "Get-Item ."], ["dockerfile", "FROM scratch"], ["diff", "+a\n-b"],
    ["fsharp", "let a = 1"], ["cpp", "int main() {}"], ["java", "class A {}"]] as const)
    assert.match(highlightCode(text, language) ?? "", /<span class="hljs-/, language);
});

test("markup in code stays text", () => {
  const html = highlightCode("var s = \"<script>alert(1)</script>\"; // <b>&", "csharp")!;
  assert.doesNotMatch(html, /<script|<b>/);
  assert.match(html, /&lt;script&gt;/);
  assert.match(html, /&lt;b&gt;&amp;/);
  // Only highlight.js token spans are produced.
  assert.deepEqual([...html.matchAll(/<([a-z]+)[ >]/g)].map(match => match[1]).filter(tag => tag !== "span"), []);
  assert.deepEqual([...html.matchAll(/class="([^"]*)"/g)].flatMap(match => match[1].split(" ")).filter(name => !/^(hljs-[a-z_-]+|[a-z]+_)$/.test(name)), []);
});

test("unknown, plain and oversized blocks are left as they are", () => {
  assert.equal(highlightCode("whatever", "not-a-language"), null);
  assert.equal(highlightCode("whatever", "text"), null);
  assert.equal(highlightCode("whatever", "plaintext"), null);
  assert.equal(highlightCode("graph TD; A-->B", "mermaid"), null);
  assert.equal(highlightCode("x".repeat(maximumHighlightedLength + 1), "js"), null);
  assert.notEqual(highlightCode("const a = 1;\n".repeat(100), "js"), null);
});
