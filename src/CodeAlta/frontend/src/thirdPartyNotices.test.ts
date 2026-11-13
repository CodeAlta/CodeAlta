import assert from "node:assert/strict";
import { join } from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";
import { packageRootOf, thirdPartyNotices } from "./thirdPartyNotices";

const modules = fileURLToPath(new URL("../node_modules/", import.meta.url));

test("the package of a bundled module is the folder under its last node_modules, with its scope", () => {
  assert.equal(packageRootOf(join(modules, "vega-lite", "build", "index.js")), join(modules, "vega-lite"));
  assert.equal(packageRootOf(join(modules, "@observablehq", "plot", "src", "plot.js")), join(modules, "@observablehq", "plot"));
  assert.equal(packageRootOf(`${join(modules, "react", "index.js")}?commonjs-exports`), join(modules, "react"));
  assert.equal(packageRootOf("\0vite/preload-helper.js"), null, "a virtual module of the build is no package");
  assert.equal(packageRootOf(fileURLToPath(new URL("./main.tsx", import.meta.url))), null, "the code of the application is its own");
});

test("the notices name each bundled package once, with its version, its license and the text of its license file", () => {
  const notices = thirdPartyNotices([
    join(modules, "vega-lite", "build", "index.js"),
    join(modules, "vega-lite", "build", "compile.js"),
    join(modules, "@observablehq", "plot", "src", "plot.js"),
    join(modules, "react", "index.js"),
    fileURLToPath(new URL("./main.tsx", import.meta.url)),
  ]);
  const names = [...notices.matchAll(/^={10,}\r?\n(\S+) (\S+) \(([^)]*)\)$/gmu)].map(match => match[1]);
  assert.deepEqual(names, ["@observablehq/plot", "react", "vega-lite"], "sorted, once each, nothing of the application");
  assert.match(notices, /^vega-lite \d+\.\d+\.\S+ \(BSD-3-Clause\)$/mu);
  assert.match(notices, /^@observablehq\/plot \d+\.\d+\.\S+ \(ISC\)$/mu);
  // The license text itself goes with the package: BSD and ISC ask for the notice and the permission.
  assert.match(notices, /Redistribution and use in source and binary forms/u);
  assert.match(notices, /Permission to use, copy, modify, and\/or distribute this software/u);
  assert.match(notices, /Copyright \(c\) Meta Platforms, Inc\. and affiliates\./u);
});
