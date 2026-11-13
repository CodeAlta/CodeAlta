// Summarizes result files of run.js as Markdown tables.
//
//   node report.mjs <results.jsonl> ...
import { readFileSync } from "node:fs";

const rows = process.argv.slice(2)
  .flatMap((path) => readFileSync(path, "utf8").split("\n").filter(Boolean).map((line) => JSON.parse(line)))
  .filter((row) => row.status === "completed" && row.probe !== "size");
const variants = [...new Set(rows.map((row) => row.variant))];
const models = [...new Set(rows.map((row) => row.model))];
const probes = [...new Set(rows.map((row) => row.probe))];
const short = (model) => model.split(":")[1].split("@")[0];
const median = (values) => {
  const sorted = values.filter((value) => typeof value === "number").sort((a, b) => a - b);
  return sorted.length === 0 ? 0 : sorted[Math.floor((sorted.length - 1) / 2)];
};
const of = (probe, model, variant) => rows.filter((row) => row.probe === probe && row.variant === variant && (!model || row.model === model));
const healthy = (set) => `${set.filter((row) => row.healthy).length}/${set.length}`;

console.log(`## Healthy outcomes\n\n| Probe | ${models.flatMap((model) => variants.map((variant) => `${short(model)} ${variant}`)).join(" | ")} | ${variants.map((variant) => `all ${variant}`).join(" | ")} |`);
console.log(`| --- | ${models.flatMap(() => variants.map(() => "---:")).join(" | ")} | ${variants.map(() => "---:").join(" | ")} |`);
for (const probe of probes) {
  console.log(`| \`${probe}\` | ${models.flatMap((model) => variants.map((variant) => healthy(of(probe, model, variant)))).join(" | ")} | ${variants.map((variant) => `**${healthy(of(probe, null, variant))}**`).join(" | ")} |`);
}

console.log(`| all | ${models.flatMap((model) => variants.map((variant) => healthy(rows.filter((row) => row.model === model && row.variant === variant)))).join(" | ")} | ${variants.map((variant) => `**${healthy(rows.filter((row) => row.variant === variant))}**`).join(" | ")} |`);

console.log(`\n## Outcomes\n\n| Probe | Outcome | ${variants.join(" | ")} |\n| --- | --- | ${variants.map(() => "---:").join(" | ")} |`);
for (const probe of probes) {
  const outcomes = [...new Set(rows.filter((row) => row.probe === probe).map((row) => row.outcome))].sort();
  for (const outcome of outcomes) {
    console.log(`| \`${probe}\` | ${outcome} | ${variants.map((variant) => of(probe, null, variant).filter((row) => row.outcome === outcome).length).join(" | ")} |`);
  }
}

console.log(`\n## Size of the work, medians\n\n| Probe | ${variants.flatMap((variant) => [`lines +/- ${variant}`, `tool calls ${variant}`, `output tokens ${variant}`]).join(" | ")} |`);
console.log(`| --- | ${variants.flatMap(() => ["---:", "---:", "---:"]).join(" | ")} |`);
for (const probe of probes) {
  console.log(`| \`${probe}\` | ${variants.flatMap((variant) => {
    const set = of(probe, null, variant);
    return [`+${median(set.map((row) => row.added))} −${median(set.map((row) => row.deleted))}`, median(set.map((row) => row.toolCalls)), median(set.map((row) => row.outputTokens))];
  }).join(" | ")} |`);
}

const totals = (variant) => rows.filter((row) => row.variant === variant);
const sum = (set, key) => set.reduce((total, row) => total + (row[key] ?? 0), 0);
console.log(`\n## Totals\n\n| | ${variants.join(" | ")} |\n| --- | ${variants.map(() => "---:").join(" | ")} |`);
for (const [label, key] of [["Sessions", null], ["Requests", "requests"], ["Tool calls", "toolCalls"], ["Input tokens", "inputTokens"], ["Output tokens", "outputTokens"], ["Lines added", "added"], ["Lines deleted", "deleted"], ["Reported cost", "cost"]]) {
  console.log(`| ${label} | ${variants.map((variant) => (key ? Math.round(sum(totals(variant), key) * 100) / 100 : totals(variant).length).toLocaleString("en-US")).join(" | ")} |`);
}

console.log(`| Files outside the task | ${variants.map((variant) => totals(variant).filter((row) => (row.strayFiles ?? []).length > 0).length).join(" | ")} |`);
console.log(`| Sessions with failing tests | ${variants.map((variant) => totals(variant).filter((row) => row.testsPass === false).length).join(" | ")} |`);
