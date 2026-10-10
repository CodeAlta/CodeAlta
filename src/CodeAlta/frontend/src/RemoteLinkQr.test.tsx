import assert from "node:assert/strict";
import test from "node:test";
import { renderToStaticMarkup } from "react-dom/server";
import { encode } from "uqr";
import { qrCode, RemoteLinkQr } from "./RemoteLinkQr";

const link = "https://claude.ai/code/session_01AbCdEfGhIjKlMnOpQrStUv";

test("the QR code of a link has a dark square for each dark module, inside a quiet zone of four", () => {
  const code = qrCode(link);
  const modules = encode(link, { ecc: "M", border: 4 }).data;
  assert.equal(code.size, modules.length);
  assert.equal(code.path.match(/M/g)?.length, modules.flat().filter(Boolean).length);
  // The quiet zone is light, and the finder pattern of the top-left corner starts right after it.
  assert.ok(!code.path.includes("M3 3h") && code.path.startsWith("M4 4h1v1h-1z"));
  assert.deepEqual(qrCode(link), code, "The same link gives the same code");
});

test("the QR code is drawn dark on white, whatever the theme, and is named for readers", () => {
  const markup = renderToStaticMarkup(<RemoteLinkQr url={link} label="QR code of the link" />);
  assert.match(markup, /^<svg[^>]* role="img" aria-label="QR code of the link"/);
  assert.match(markup, /<rect width="41" height="41" fill="#fff"><\/rect><path d="M4 4h1v1h-1z[^"]*" fill="#000"><\/path><\/svg>$/);
});
