import assert from "node:assert/strict";
import { existsSync, readFileSync } from "node:fs";
import test from "node:test";

// The built phone page, when there is a build (`npm run build`, or the build of the desktop project).
const dist = new URL("../../dist/", import.meta.url);
const built = existsSync(new URL("mobile.html", dist));

test("the built phone page has no inline script and loads none of the desktop's RPC client", { skip: !built }, () => {
  const html = readFileSync(new URL("mobile.html", dist), "utf8");
  const scripts = [...html.matchAll(/<script\b([^>]*)>([\s\S]*?)<\/script>/g)];
  assert.ok(scripts.length > 0);
  for (const [, attributes, body] of scripts) {
    assert.match(attributes, /\ssrc="/, "the policy of the plugin's server allows no inline script");
    assert.equal(body.trim(), "");
  }
  assert.doesNotMatch(html, /type="importmap"|splash|\son[a-z]+=/i, "the desktop's import map and start-up screen are not written in it, nor any handler");
  // The chunks the page loads, followed through their static and dynamic imports.
  const entry = /<script type="module"[^>]*src="\.\/(assets\/[^"]+\.js)"/.exec(html)?.[1];
  assert.ok(entry, "the module of the page");
  const seen = new Set<string>(), queue = [entry!];
  while (queue.length) {
    const file = queue.pop()!;
    if (seen.has(file)) continue;
    seen.add(file);
    const code = readFileSync(new URL(file, dist), "utf8");
    for (const found of code.matchAll(/["'`]\.\/([\w.-]+\.js)["'`]/g)) queue.push(`assets/${found[1]}`);
    // Words of NeoAstra's client and of the desktop page's link to its host, which the phone page has no use for.
    assert.doesNotMatch(code, /channel_ack|too_many_requests|data-neoastra/, `${file} carries the desktop's RPC client`);
  }
});
