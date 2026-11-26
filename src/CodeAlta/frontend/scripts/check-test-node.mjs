const version = process.versions.node;
const [major, minor] = version.split(".").map(Number);

// Node 24.0–24.2 made t.test() return undefined, letting a parent close its browser before its subtests finish.
// Node 24.3.0 restored the promise (nodejs/node#58282).
if (major === 24 && minor < 3) {
  console.error(`Frontend tests cannot await subtests on Node.js ${version}. Use Node.js 24.3.0 or newer.`);
  process.exitCode = 1;
}
