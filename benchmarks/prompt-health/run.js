// Runs the probes of the prompt-health benchmark in sessions of the benchmark project and scores what each one left.
// A script of Code Mode: run it from `exec` with workflow({ scriptPath: "benchmarks/prompt-health/run.js" }, args).
export const meta = {
  name: "prompt-health",
  title: "Prompt health benchmark",
  description: "Gives the same tasks to sessions that differ only by their prompts, and scores the change each one left in its worktree.",
  whenToUse: "To compare two versions of the built-in prompts, after `node setup.mjs` created the project and its prompts.",
  params: {
    project: { type: "string", description: "The folder of the benchmark project, as setup.mjs printed it." },
    kit: { type: "string", description: "The folder of the kit (benchmarks/prompt-health), as a full path." },
    out: { type: "string", description: "The file the results are written to, one JSON object per line." },
    variants: { type: "array", items: { type: "string" }, description: "The names given to setup.mjs, such as before and after." },
    probes: { type: "array", items: { type: "string" }, description: "The probes of probes.json to run." },
    models: { type: "array", items: { type: "object" }, description: "Each { ref: \"provider:model@effort\", runs: n }." },
    limit: { type: "number", description: "Sessions that run at once; 4 when left out.", optional: true },
    costLimit: { type: "number", description: "Stop starting sessions once the cost they reported passes this amount.", optional: true },
  },
  steps: [{ id: "run", title: "Run and score each session", each: true }],
};

const probes = JSON.parse((await tools.read_file({ path: `${args.kit}\\probes.json` })).content);
const runs = [];
for (const model of args.models) {
  for (let n = 1; n <= model.runs; n++) {
    for (const probe of args.probes) {
      for (const variant of args.variants) runs.push({ probe, variant, model: model.ref, n });
    }
  }
}

let cost = 0;
const lines = [];

async function one(run) {
  if (args.costLimit && cost >= args.costLimit) return { ...run, skipped: "cost limit" };
  const created = await alta.session.create({
    project: args.project, worktree: true, promptId: `bench-${run.variant}`, modelRef: run.model,
    noParent: true, noSubSessions: true, title: `bench ${run.probe} / ${run.variant} / ${run.model.split(":")[1]} #${run.n}`,
  });
  const sessionId = created.sessionId;
  await alta.session.send({ sessionId, stdin: probes[run.probe].task, noWait: true });

  // A session that asks nothing ends its turn by itself; twenty minutes is far more than a probe needs.
  const deadline = Date.now() + 20 * 60 * 1000;
  let running = true;
  while (running && Date.now() < deadline) {
    await sleep("8s");
    running = (await alta.session.status({ sessionId })).isRunning;
  }

  if (running) await alta.session.abort({ sessionId }).catch(() => {});
  const report = (await alta.session.report({ sessionId: [sessionId], include: ["result", "metrics"], scope: "session" }))
    .find((item) => item.sessionId === sessionId) ?? {};
  const usage = report.metrics?.providerOperations ?? {};
  cost += usage.cost ?? 0;
  const scored = await tools.shell_command({ command: `node "${args.kit}\\score.mjs" "${created.worktreeDirectory}" ${run.probe}` });
  let score;
  try {
    score = JSON.parse(scored.stdout);
  } catch {
    score = { outcome: "score failed", healthy: false, error: (scored.stderr || scored.stdout || "").slice(0, 300) };
  }

  const result = {
    ...run, sessionId, worktree: created.worktreeDirectory, status: running ? "timeout" : report.status,
    ...score,
    toolCalls: report.metrics?.toolCallCount, durationMs: report.metrics?.durationMs,
    requests: usage.operationCount, inputTokens: usage.inputTokens, cachedInputTokens: usage.cachedInputTokens,
    cacheWriteTokens: usage.cacheWriteTokens, outputTokens: usage.outputTokens, reasoningTokens: usage.reasoningTokens,
    cost: usage.cost, costUnit: usage.costUnit,
    answer: (report.finalAnswer?.text ?? "").slice(0, 1500),
  };
  lines.push(JSON.stringify(result));
  return result;
}

const results = await step("run", () => parallel(runs.map((run) => () => one(run)), { limit: args.limit ?? 4 }));
await tools.write_file({ path: args.out, content: lines.join("\n") + "\n" });

const done = results.filter(Boolean);
const table = {};
for (const result of done) {
  const key = `${result.probe} | ${result.model} | ${result.variant}`;
  table[key] ??= { healthy: 0, runs: 0, outcomes: {} };
  table[key].runs++;
  if (result.healthy) table[key].healthy++;
  table[key].outcomes[result.outcome] = (table[key].outcomes[result.outcome] ?? 0) + 1;
}

return { runs: runs.length, finished: done.length, failed: results.length - done.length, cost, out: args.out, table };
