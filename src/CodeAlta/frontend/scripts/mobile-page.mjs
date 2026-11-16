// The files of the phone page alone, taken out of the build of the application (dist): mobile.html, public/mobile, and the
// chunks, styles and fonts they reach. CodeAlta Node packs them (src/CodeAlta.Plugin.Mobile/MobilePage.targets) and serves
// only what the list written here names (files.json: path, bytes, sha256), as the desktop serves what its manifest names.
//
//   node scripts/mobile-page.mjs <dist> <output>
import { createHash } from "node:crypto";
import { cpSync, existsSync, mkdirSync, readdirSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";

const [dist, output] = process.argv.slice(2);
if (!dist || !output) throw new Error("usage: node scripts/mobile-page.mjs <dist> <output>");
if (!existsSync(join(dist, "mobile.html"))) throw new Error(`${dist} holds no mobile.html: build the frontend first.`);

// A file of assets/ is reached when a file already reached names it: its name carries a hash, so a name is a reference.
const assets = new Set(readdirSync(join(dist, "assets")));
const text = /\.(html|js|css|webmanifest|json)$/;
const names = /[\w.@~-]+\.[a-z0-9]+/gi;
const reached = new Set(["mobile.html", ...readdirSync(join(dist, "mobile")).map(name => `mobile/${name}`)]);
// The worker that draws the charts is started by its fixed name (src/vegaLite/chartWorkerFile.ts).
if (assets.has("chart-worker.js")) reached.add("assets/chart-worker.js");
const queue = [...reached];
while (queue.length) {
  const file = queue.pop();
  if (!text.test(file)) continue;
  for (const name of readFileSync(join(dist, file), "utf8").match(names) ?? []) {
    const path = `assets/${name}`;
    if (!assets.has(name) || reached.has(path)) continue;
    reached.add(path);
    queue.push(path);
  }
}

rmSync(output, { recursive: true, force: true });
const files = [];
for (const path of [...reached].sort()) {
  const target = join(output, path);
  mkdirSync(dirname(target), { recursive: true });
  cpSync(join(dist, path), target);
  const content = readFileSync(target);
  files.push({ path, bytes: content.length, sha256: createHash("sha256").update(content).digest("hex") });
}
writeFileSync(join(output, "files.json"), JSON.stringify({ files }, null, 1) + "\n");
