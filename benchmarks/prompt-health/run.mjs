// Runs the probes of the prompt-health benchmark with altacli, each in a git worktree of the benchmark project, and scores
// what each run left.
//
//   node run.mjs --project <folder>/shopkit --altacli <path of altacli> --out <results.jsonl>
//                --variants before,after --probes cause,vendor --models codex:gpt-6.1-sol@low=3[,<model>=<runs>...]
//                [--limit 4] [--cost-limit <amount>]
//
// The sessions are kept in the isolated storage of altacli (~/.alta/cli): CodeAlta Desktop and TUI never list them.
import { execFileSync, spawn } from "node:child_process";
import { appendFileSync, cpSync, mkdirSync, readFileSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const kit = dirname(fileURLToPath(import.meta.url));
const options = {};
for (let index = 2; index < process.argv.length; index += 2) options[process.argv[index].replace(/^--/, "")] = process.argv[index + 1];
for (const name of ["project", "altacli", "out", "variants", "probes", "models"]) {
  if (!options[name]) {
    console.error(`missing --${name}; see the head of run.mjs`);
    process.exit(2);
  }
}

const project = resolve(options.project);
const probes = JSON.parse(readFileSync(join(kit, "probes.json"), "utf8"));
const limit = Number(options.limit ?? 4);
const costLimit = options["cost-limit"] ? Number(options["cost-limit"]) : null;
const stamp = new Date().toISOString().replace(/[-:T]/g, "").slice(0, 14);
const runs = [];
for (const entry of options.models.split(",")) {
  const [model, count = "1"] = entry.split("=");
  for (let n = 1; n <= Number(count); n++) {
    for (const probe of options.probes.split(",")) {
      if (!probes[probe]) throw new Error(`unknown probe ${probe}`);
      for (const variant of options.variants.split(",")) runs.push({ probe, variant, model, n });
    }
  }
}

mkdirSync(dirname(resolve(options.out)), { recursive: true });
let cost = 0;

// Runs altacli on one prompt and reads its JSON lines: the session, the tool calls, the result.
function agent(worktree, run) {
  return new Promise((done) => {
    const child = spawn(options.altacli, ["run", "--isolated", "-C", worktree, "--prompt-id", `bench-${run.variant}`, "-m", run.model,
      "--no-sub-sessions", "--enable", "all", "--format", "jsonl", "--timeout", "1200", "-q", "-"], { stdio: ["pipe", "pipe", "pipe"] });
    let output = "";
    let errors = "";
    child.stdout.on("data", (chunk) => (output += chunk));
    child.stderr.on("data", (chunk) => (errors += chunk));
    child.on("error", (error) => done({ exitCode: -1, error: error.message }));
    child.on("close", (exitCode) => {
      let session;
      let result;
      let toolCalls = 0;
      for (const line of output.split("\n")) {
        if (!line.trim()) continue;
        let message;
        try {
          message = JSON.parse(line);
        } catch {
          continue;
        }

        if (message.method === "_codealta/session") session = message.params;
        else if (message.method === "_codealta/result") result = message.params;
        else if (message.method === "session/update" && message.params?.update?.sessionUpdate === "tool_call") toolCalls++;
      }

      done({ exitCode, session, result, toolCalls, error: result ? undefined : errors.slice(-300) });
    });
    child.stdin.end(probes[run.probe].task);
  });
}

async function one(run, index) {
  if (costLimit !== null && cost >= costLimit) return { ...run, status: "skipped: cost limit" };
  const name = `${stamp}-${index}-${run.probe}-${run.variant}`;
  const worktree = join(dirname(project), "runs", name);
  execFileSync("git", ["-C", project, "worktree", "add", "-q", worktree, "-b", `bench/${name}`, "main"]);
  // The prompts are excluded from git, so a worktree does not have them; the exclusion keeps the copy out of the scored diff.
  cpSync(join(project, ".alta", "prompts"), join(worktree, ".alta", "prompts"), { recursive: true });

  const { exitCode, session, result, toolCalls, error } = await agent(worktree, run);
  const usage = result?.usage ?? {};
  cost += result?.cost?.amount ?? 0;
  let score;
  try {
    score = JSON.parse(execFileSync(process.execPath, [join(kit, "score.mjs"), worktree, run.probe], { encoding: "utf8" }));
  } catch (failure) {
    score = { outcome: "score failed", healthy: false, error: String(failure.message).slice(0, 300) };
  }

  // A run that did not use the prompt it was asked to use says nothing about that prompt.
  const prompt = session?.agentPrompt;
  const status = prompt && prompt !== `bench-${run.variant}` ? `wrong prompt: ${prompt}` : result?.status ?? `no result (exit ${exitCode})`;
  return {
    ...run, sessionId: session?.sessionId, worktree, status, ...score, toolCalls, durationMs: result?.durationMs,
    inputTokens: usage.inputTokens, cachedInputTokens: usage.cachedReadTokens, cacheWriteTokens: usage.cachedWriteTokens,
    outputTokens: usage.outputTokens, reasoningTokens: usage.thoughtTokens, cost: result?.cost?.amount, costUnit: result?.cost?.currency,
    disabledFeatures: session?.disabledFeatures, answer: (result?.finalMessage ?? "").slice(0, 1500), error,
  };
}

let next = 0;
let finished = 0;
async function worker() {
  while (next < runs.length) {
    const index = next++;
    const result = await one(runs[index], index).catch((failure) => ({ ...runs[index], status: `failed: ${failure.message}` }));
    appendFileSync(options.out, JSON.stringify(result) + "\n");
    console.log(`${++finished}/${runs.length} ${result.probe} ${result.variant} ${result.model}: ${result.status}, ${result.outcome ?? ""}`);
  }
}

await Promise.all(Array.from({ length: Math.min(limit, runs.length) }, worker));
console.log(`done: ${finished} runs, reported cost ${cost.toFixed(2)}, results in ${options.out}`);
