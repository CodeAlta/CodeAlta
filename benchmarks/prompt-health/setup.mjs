// Creates the project of the benchmark and the prompts it compares.
//
//   node setup.mjs <folder> <name>=<git revision | WORKTREE> ...
//
// <folder>/shopkit becomes a git repository holding a copy of fixture/, and gets one agent prompt `bench-<name>` (with its
// system prompt) per name: the built-in default prompts of this repository at that revision, or as they are in the working tree.
import { execFileSync } from "node:child_process";
import { cpSync, existsSync, mkdirSync, readFileSync, writeFileSync, appendFileSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const kit = dirname(fileURLToPath(import.meta.url));
const repository = resolve(kit, "..", "..");
const prompts = "src/CodeAlta.Orchestration/content/prompts";
const [folder, ...variants] = process.argv.slice(2);
if (!folder || variants.length === 0 || variants.some((variant) => !/^[a-z0-9-]+=.+$/.test(variant))) {
  console.error("usage: node setup.mjs <folder> <name>=<git revision | WORKTREE>[,<revision of the system prompt>] ...");
  process.exit(2);
}

const project = join(resolve(folder), "shopkit");
const git = (cwd, ...args) => execFileSync("git", args, { cwd, encoding: "utf8" });

if (!existsSync(join(project, ".git"))) {
  mkdirSync(project, { recursive: true });
  cpSync(join(kit, "fixture"), project, { recursive: true });
  git(project, "init", "-q", "-b", "main");
  git(project, "config", "user.name", "prompt-health");
  git(project, "config", "user.email", "prompt-health@example.invalid");
  git(project, "config", "core.autocrlf", "false");
  // The prompts of the benchmark stay out of the worktrees of the sessions.
  appendFileSync(join(project, ".git", "info", "exclude"), ".alta/\n");
}

// A fixture that changed since the project was created becomes a new commit of main: the sessions that follow start from it.
cpSync(join(kit, "fixture"), project, { recursive: true });
if (git(project, "status", "--porcelain").trim()) {
  git(project, "add", "-A");
  git(project, "commit", "-q", "-m", "shopkit fixture");
}

const read = (revision, path) => revision === "WORKTREE"
  ? readFileSync(join(repository, path), "utf8")
  : git(repository, "show", `${revision}:${path}`);
const body = (text) => text.replace(/\r\n/g, "\n").replace(/^---\n[\s\S]*?\n---\n/, "");

const sizes = {};
for (const variant of variants) {
  // <name>=<revision> takes both prompts there; <name>=<agent revision>,<system revision> mixes two versions.
  const [name, revisions] = variant.split(/=(.*)/s);
  const [revision, systemRevision = revision] = revisions.split(",");
  const system = read(systemRevision, `${prompts}/system/default.system-prompt.md`).replace(/\r\n/g, "\n");
  const agent = body(read(revision, `${prompts}/agents/default.prompt.md`));
  const short = (value) => (value === "WORKTREE" ? "the working tree" : git(repository, "rev-parse", "--short", value).trim());
  const at = systemRevision === revision ? short(revision) : `${short(revision)} (system prompt at ${short(systemRevision)})`;
  const root = join(project, ".alta", "prompts");
  mkdirSync(join(root, "system"), { recursive: true });
  mkdirSync(join(root, "agents"), { recursive: true });
  writeFileSync(join(root, "system", `bench-${name}.system-prompt.md`), system);
  writeFileSync(join(root, "agents", `bench-${name}.prompt.md`),
    `---\nname: Bench ${name}\ndescription: Default agent prompt of CodeAlta at ${at}, for the prompt-health benchmark.\nsystem: bench-${name}\n---\n${agent}`);
  sizes[name] = { at, systemCharacters: body(system).length, agentCharacters: agent.length };
}

console.log(JSON.stringify({ project, base: git(project, "rev-parse", "--short", "main").trim(), prompts: sizes }, null, 2));
