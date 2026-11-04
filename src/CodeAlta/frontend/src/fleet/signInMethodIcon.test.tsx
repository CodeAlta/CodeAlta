import assert from "node:assert/strict";
import test from "node:test";
import { renderToStaticMarkup } from "react-dom/server";
import { hasBrandMark, SignInMethodIcon } from "./SignInMethodIcon";

const draw = (method: string, size?: number) => renderToStaticMarkup(<SignInMethodIcon method={method} size={size} />);

test("Microsoft is drawn as its four squares in the colors of the brand", () => {
  const markup = draw("microsoft");
  for (const color of ["#F25022", "#7FBA00", "#00A4EF", "#FFB900"]) assert.ok(markup.includes(`fill="${color}"`), color);
  assert.match(markup, /width="16"/u);
  assert.match(markup, /height="16"/u);
});

test("Google is drawn as its multicolor G, untouched", () => {
  const markup = draw("google");
  for (const color of ["#4285F4", "#34A853", "#FBBC05", "#EB4335"]) assert.ok(markup.includes(`fill="${color}"`), color);
});

test("GitHub is the mark in the color of the text, so it reads in both themes", () => {
  const markup = draw("github");
  assert.match(markup, /fill="currentColor"/u);
  assert.doesNotMatch(markup, /fill="#/u);
});

test("a passkey is a key and the company's sign-in a shield, both in the color of the text", () => {
  for (const method of ["passkey", "company"]) {
    const markup = draw(method, 14);
    assert.match(markup, /<svg/u, method);
    assert.match(markup, /stroke="currentColor"/u, method);
    assert.match(markup, /width="14"/u, method);
  }
  assert.notEqual(draw("passkey"), draw("company"));
});

test("a method it does not know has no icon, and only the brands have a mark", () => {
  assert.equal(draw("something-else"), "");
  assert.deepEqual(["company", "passkey", "microsoft", "google", "github", "other"].filter(hasBrandMark), ["microsoft", "google", "github"]);
});
