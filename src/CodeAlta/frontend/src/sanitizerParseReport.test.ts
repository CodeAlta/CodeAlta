import assert from "node:assert/strict";
import test from "node:test";
import { isSanitizerParseReport } from "./sanitizerParseReport";

const handler = "Executing inline event handler violates the following Content Security Policy directive 'script-src 'self''.";
const frames = (...names: string[]) => names.map(functionName => ({ functionName }));

test("the report of the inert parse of DOMPurify is told apart from a handler of the page", () => {
  // As Chromium 156 reports them: from DOMPurify's own parse, with or without a wrapper of DOMParser around it.
  assert.equal(isSanitizerParseReport(handler, frames("_initDocument2", "DOMPurify.sanitize", "", "useMemo")), true);
  assert.equal(isSanitizerParseReport(handler, frames("DOMParser.parseFromString", "_initDocument2", "DOMPurify.sanitize", "render")), true);
  // A handler of the live page, another report, or a report raised elsewhere in the sanitizer, is a failure.
  assert.equal(isSanitizerParseReport(handler, frames("onerror", "render")), false);
  assert.equal(isSanitizerParseReport(handler, []), false);
  assert.equal(isSanitizerParseReport(handler, undefined), false);
  assert.equal(isSanitizerParseReport(handler, frames("_sanitizeElements", "_initDocument2", "DOMPurify.sanitize")), false);
  assert.equal(isSanitizerParseReport(handler, frames("_initDocument2", "render")), false);
  assert.equal(isSanitizerParseReport("Refused to load the image 'https://remote.invalid/a.png'", frames("_initDocument2", "DOMPurify.sanitize")), false);
});
