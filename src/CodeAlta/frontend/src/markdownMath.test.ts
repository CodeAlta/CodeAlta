import assert from "node:assert/strict";
import test from "node:test";
import { createMarkdownParser } from "./markdown";
import { mathSpanAt, mathSpans } from "./markdownMath";

const r = String.raw;
const parse = createMarkdownParser({ placeholderMark: "M" });
// The formulas of a rendered text: inline ones as `i:`, display ones as `d:`.
const formulas = (html: string) => Array.from(html.matchAll(/<(?:span|div) (data-math(?:-display)?)="M"[^>]*>([^<]*)<\/(?:span|div)>/g),
  match => `${match[1] === "data-math" ? "i" : "d"}:${match[2]}`);

test("inline math is read with dollars, brackets and the backtick form of GitHub", () => {
  assert.deepEqual(formulas(parse(r`Energy $E=mc^2$, roots \(\sqrt{2}\) and ` + "$`\\alpha`$.")), ["i:E=mc^2", r`i:\sqrt{2}`, r`i:\alpha`]);
  assert.deepEqual(formulas(parse(r`($x$) and $n$th, then $a$-$b$`)), ["i:x", "i:n", "i:a", "i:b"]);
  // HTML of the formula is escaped: it is text until the renderer draws it.
  assert.match(parse("$a<b$"), /<span data-math="M">a&lt;b<\/span>/);
});

test("dollars that are prices, variables or escaped stay text", () => {
  for (const text of ["It costs $5 and $10.", "Set $HOME and $PATH", r`An escaped \$x\$ stays`, "a$x$ in a word", "$ x$ and $x $", "between $1 and $2 each"])
    assert.deepEqual(formulas(parse(text)), [], text);
  assert.match(parse("It costs $5 and $10."), /It costs \$5 and \$10\./);
  assert.match(parse(r`An escaped \$x\$ stays`), /An escaped \$x\$ stays/);
});

test("code keeps its dollars", () => {
  assert.deepEqual(formulas(parse("Run `echo $x$` then\n\n```sh\n$$\nx\n$$\n```")), []);
  assert.deepEqual(formulas(parse("    $$x$$")), [], "an indented code block");
});

test("display math is read on lines of its own, closed on the same line or a later one", () => {
  assert.deepEqual(formulas(parse("$$x^2$$")), ["d:x^2"]);
  assert.deepEqual(formulas(parse("$$\n\\frac{a}{b}\n= c\n$$")), [r`d:\frac{a}{b}
= c`]);
  assert.deepEqual(formulas(parse(r`\[ a + b \]`)), [r`d: a + b `]);
  assert.deepEqual(formulas(parse("\\[\nx\n\\]")), ["d:x"]);
  assert.deepEqual(formulas(parse("\\begin{align}\na &= b \\\\\nc &= d\n\\end{align}")), [r`d:\begin{align}
a &amp;= b \\
c &amp;= d
\end{align}`]);
  // A display block interrupts a paragraph, and a paragraph follows it.
  assert.deepEqual(formulas(parse("Before\n$$\nx\n$$\nafter")), ["d:x"]);
  assert.match(parse("Before\n$$\nx\n$$\nafter"), /<p>Before<\/p>\n<div data-math-display="M">x<\/div>\n<p>after<\/p>/);
  // In a list item and a quote.
  assert.deepEqual(formulas(parse("- item\n\n  $$\n  y\n  $$\n\n> $$z$$")), ["d:y", "d:z"]);
});

test("a display formula inside a paragraph is read where it is", () => {
  assert.deepEqual(formulas(parse("The value $$x^2$$ is positive.")), ["d:x^2"]);
  assert.match(parse("The value $$x^2$$ is positive."), /<p>The value <span data-math-display="M">x\^2<\/span> is positive\.<\/p>/);
});

test("a display block that is not closed stays text while it is being written", () => {
  assert.deepEqual(formulas(parse("$$\n\\frac{a}{")), []);
  assert.deepEqual(formulas(parse("\\[\nx")), []);
  assert.deepEqual(formulas(parse("\\begin{equation}\nx")), []);
  assert.match(parse("$$\n\\frac{a}{"), /<p>\$\$/);
});

test("only the display environments of LaTeX start a block", () => {
  assert.deepEqual(formulas(parse("\\begin{itemize}\n\\item x\n\\end{itemize}")), []);
  assert.deepEqual(formulas(parse("\\begin{equation*} y \\end{equation*}")), [r`d:\begin{equation*} y \end{equation*}`]);
});

test("a math fence stays a fence, and display blocks say which lines they come from", () => {
  assert.match(parse("```math\nx^2\n```"), /<code class="language-math">x\^2\n<\/code>/);
  const html = createMarkdownParser({ placeholderMark: "M" })("intro\n\n$$\nx\n$$", { mark: "S", firstLine: 10 });
  assert.match(html, /<div data-source-lines="S:12-14" data-math-display="M">x<\/div>/);
});

test("the formulas of a prompt are found as the parser reads them, code skipped", () => {
  const text = "Let $x^2$ and `$y$` and\n\n$$\n\\frac{a}{b}\n$$\n\nprice $5 and \\(z\\)";
  assert.deepEqual(mathSpans(text).map(span => [span.tex, span.display, span.open]),
    [["x^2", false, "$"], ["\n\\frac{a}{b}\n", true, "$$"], ["z", false, "\\("]]);
  const at = text.indexOf("x^2") + 1;
  assert.deepEqual(mathSpanAt(text, at), { start: at - 2, end: at + 3, tex: "x^2", display: false, open: "$", close: "$", closed: true });
  assert.equal(mathSpanAt(text, text.indexOf("price")), null);
  assert.equal(mathSpanAt(text, text.indexOf("$y$") + 1), null, "code is not math");
  assert.equal(mathSpans("```\n$x$\n```\n$y$").map(span => span.tex).join(), "y");
});

test("a formula being written in the prompt is found when it looks like TeX", () => {
  const typing = r`Try $\frac{1}{2`;
  assert.deepEqual(mathSpanAt(typing, typing.length), { start: 4, end: typing.length, tex: r`\frac{1}{2`, display: false, open: "$", close: "$", closed: false });
  assert.equal(mathSpanAt("It costs $5 now", 11), null);
  const block = "$$\n\\sum_i";
  assert.equal(mathSpanAt(block, block.length)?.closed, false);
  assert.equal(mathSpans(block).length, 0, "only the prompt looks at unclosed formulas");
});
