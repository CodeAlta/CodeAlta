// Scores what a session left in its worktree of the benchmark project.
//
//   node score.mjs <worktree> <probe>
//
// Writes one JSON object: the outcome of the probe, whether it is the healthy one, and the size of the change.
import { execFileSync, spawnSync } from "node:child_process";
import { existsSync, readFileSync, readdirSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const [worktree, probe] = process.argv.slice(2);
const kit = dirname(fileURLToPath(import.meta.url));
const probes = JSON.parse(readFileSync(join(kit, "probes.json"), "utf8"));
if (!worktree || !probes[probe]) {
  console.error("usage: node score.mjs <worktree> <probe>");
  process.exit(2);
}

const git = (...args) => execFileSync("git", args, { cwd: worktree, encoding: "utf8", maxBuffer: 16 * 1024 * 1024 });
// What the session started from: where its branch left main.
const base = git("merge-base", "HEAD", "main").trim();
// New files the session did not add are part of its change.
git("add", "-A", "-N", ".");

const now = (path) => (existsSync(join(worktree, path)) ? readFileSync(join(worktree, path), "utf8").replace(/\r\n/g, "\n") : "");
const before = (path) => {
  try {
    return git("show", `${base}:${path}`).replace(/\r\n/g, "\n");
  } catch {
    return "";
  }
};

// Runs a module in the worktree and reads the JSON it prints.
const evaluate = (code) => {
  const run = spawnSync(process.execPath, ["--input-type=module", "-e", code], { cwd: worktree, encoding: "utf8", timeout: 30000 });
  try {
    return JSON.parse(run.stdout);
  } catch {
    return {};
  }
};
const guard = "const safe = (f) => { try { return f(); } catch { return 'ERR'; } };";
const sources = () => readdirSync(join(worktree, "src")).filter((name) => name.endsWith(".js")).map((name) => `src/${name}`);

const numstat = git("diff", "--numstat", base).trim().split("\n").filter(Boolean).map((line) => line.split("\t"));
const files = numstat.map(([, , path]) => path);
const change = {
  files: files.length,
  added: numstat.reduce((sum, [added]) => sum + (Number(added) || 0), 0),
  deleted: numstat.reduce((sum, [, deleted]) => sum + (Number(deleted) || 0), 0),
  strayFiles: files.filter((path) => !probes[probe].allowed.some((allowed) => path.startsWith(allowed))),
  testsPass: spawnSync(process.execPath, ["--test"], { cwd: worktree, encoding: "utf8", timeout: 120000 }).status === 0,
};

const section = (text, heading) => {
  const start = text.indexOf(`## ${heading}\n`);
  if (start < 0) return "";
  const rest = text.slice(start + heading.length + 4);
  const end = rest.search(/^## /m);
  return (end < 0 ? rest : rest.slice(0, end)).trim();
};

const scorers = {
  cause() {
    const result = evaluate(`${guard}
      const { toCents } = await import("./src/money.js"); const { monthlyReport } = await import("./src/report.js");
      console.log(JSON.stringify({ helper: safe(() => toCents("1,234.50")), plain: safe(() => toCents("12.50")), number: safe(() => toCents(12.5)),
        report: safe(() => monthlyReport([{ category: "a", amount: "1,234.50" }])[0].total) }));`);
    const helperFixed = result.helper === 123450 && result.plain === 1250 && result.number === 1250;
    const reportFixed = result.report === "1234.50";
    // A caller that removes the separator itself works around the helper.
    const callerWorkarounds = sources().filter((path) => path !== "src/money.js" && /replace(All)?\(\s*(\/,\/g?|["'`],["'`])/.test(now(path)));
    const outcome = !reportFixed ? "unsolved" : !helperFixed ? "symptom patched at the caller"
      : callerWorkarounds.length > 0 ? "helper fixed, old workaround left" : "helper fixed, workaround removed";
    return { outcome, healthy: outcome === "helper fixed, workaround removed", helperFixed, callerWorkarounds, helperTested: /1,234|,\d{3}/.test(now("test/money.test.js")) };
  },

  vendor() {
    const result = evaluate(`${guard}
      const { importCustomers } = await import("./src/importCustomers.js"); const { importOrders } = await import("./src/importOrders.js");
      console.log(JSON.stringify({ customers: safe(() => importCustomers("id,name,email\\n1,Ann,a@x\\n2,Bob,b@x").length),
        orders: safe(() => importOrders("id,customer,amount\\n1,ann,12.50\\n2,bob,3.00").length) }));`);
    const fixed = result.customers === 2 && result.orders === 2;
    const vendorEdited = files.some((path) => path.startsWith("src/vendor/"));
    // The places that make the text end with a new line before the vendored parser reads it.
    const sites = sources().filter((path) => /endsWith\(\s*["'`]\\n["'`]\s*\)|\+\s*["'`]\\n["'`]|\$\{[^}]+\}\\n`/.test(now(path)));
    const outcome = !fixed ? "unsolved" : vendorEdited ? "vendored file edited"
      : sites.length === 1 ? "one workaround, one place" : sites.length > 1 ? "workaround copied" : "other";
    return { outcome, healthy: outcome === "one workaround, one place", sites, explained: sites.some((path) => /\/\/.*csvlite|\*.*csvlite/i.test(now(path))) };
  },

  text() {
    const path = "docs/configuration.md";
    const old = section(before(path), "Timeouts");
    const current = section(now(path), "Timeouts");
    const markers = (text) => (text.match(/\bNote\b|\bAlso\b|\bAdditionally\b|\bnow (also )?accepts\b|\bAs of\b|\bSince \d|\bNew:/g) ?? []).length;
    const documented = /45s|2m|duration/.test(current);
    const appended = current.startsWith(old);
    const outcome = !documented ? "not documented" : appended ? "appended to the paragraph" : markers(current) > markers(old) ? "rewritten, with a note" : "rewritten";
    return { outcome, healthy: outcome === "rewritten", growth: current.length - old.length, lines: current.split("\n").filter((line) => line.trim()).length,
      fileGrowth: now(path).length - before(path).length };
  },

  longline() {
    const path = "docs/returns.md";
    const longest = (text) => Math.max(0, ...text.split("\n").map((line) => line.length));
    const old = section(before(path), "Policy");
    const current = section(now(path), "Policy");
    const documented = /gift card/i.test(current);
    const growth = longest(current) - longest(old);
    // The long bullet got longer, stayed as it was beside the new rule, or was split.
    const outcome = !documented ? "not documented" : growth > 10 ? "long line extended" : growth < -40 ? "long line split" : "rule added beside the long line";
    return { outcome, healthy: documented && growth <= 10, longestLine: longest(current), longestLineGrowth: growth,
      lines: current.split("\n").filter((line) => line.trim()).length, growth: current.length - old.length };
  },

  rules() {
    const lines = (text) => text.split("\n").filter((line) => line.trim());
    const current = lines(now("AGENTS.md"));
    const recorded = current.some((line) => line.includes("--test-concurrency=1"));
    const oldLeft = current.some((line) => line.includes("node --test") && !line.includes("--test-concurrency"));
    const addedLines = current.length - lines(before("AGENTS.md")).length;
    const notes = current.slice(current.findIndex((line) => line.startsWith("## Notes")) + 1);
    const outcome = !recorded ? "not recorded in AGENTS.md" : oldLeft ? "rule added beside the old one"
      : addedLines > 0 ? "rule changed, lines added" : "rule changed in place";
    return { outcome, healthy: outcome === "rule changed in place", addedLines, underNotes: notes.some((line) => line.includes("--test-concurrency")),
      readmeStale: /node --test`/.test(now("README.md")), scriptUpdated: now("package.json").includes("--test-concurrency=1") };
  },

  leftover() {
    const result = evaluate(`${guard}
      const { slugify } = await import("./src/text.js");
      console.log(JSON.stringify({ added: safe(() => slugify("Señor Ångström")), kept: safe(() => slugify("Crème brûlée 250g")) }));`);
    const works = result.added === "senor-angstrom" && result.kept === "creme-brulee-250g";
    const code = now("src/text.js");
    const codeLeft = ["ACCENTS", "replaceAccents"].filter((name) => code.includes(name));
    const docStale = /ACCENTS|\btable\b/.test(now("docs/slugs.md"));
    const outcome = !works ? "unsolved" : codeLeft.length > 0 ? "old code left" : docStale ? "code clean, doc out of date" : "code and doc clean";
    return { outcome, healthy: outcome === "code and doc clean", codeLeft, docStale };
  },

  scope() {
    const renamed = !sources().concat(readdirSync(join(worktree, "test")).map((name) => `test/${name}`)).some((path) => now(path).includes("skipTax"))
      && now("src/cart.js").includes("taxExempt");
    // Changed lines that the rename does not need.
    const extra = git("diff", "-U0", base).split("\n")
      .filter((line) => /^[+-]/.test(line) && !/^(\+\+\+|---)/.test(line))
      .map((line) => line.slice(1).trim())
      .filter((line) => line && !/skipTax|taxExempt/.test(line));
    const cart = now("src/cart.js");
    const outcome = !renamed ? "unsolved" : extra.length === 0 ? "rename only" : "rename and more";
    return { outcome, healthy: outcome === "rename only", extraLines: extra.length, loops: (cart.match(/subtotal \+=/g) ?? []).length, legacyRoundKept: cart.includes("legacyRound") };
  },

  size: () => ({ outcome: "size", healthy: true }),
};

console.log(JSON.stringify({ probe, ...scorers[probe](), ...change }));
