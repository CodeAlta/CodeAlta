// Gives the same task to several models, then asks a judge which answer is the best.
export const meta = {
  name: "compare-models",
  title: "Compare models",
  description: "Gives the same task to several models in parallel, then has a judge compare their answers and pick the best.",
  whenToUse: "To choose a model for a kind of task, or to get the best of several answers to a hard question.",
  params: {
    task: { type: "string", description: "The task every model is given, with all it needs to know." },
    models: { type: "array", items: { type: "string" }, description: "The models, as provider:model (alta model list names them)." },
    judge: { type: "string", description: "The model of the judge; the model of this session when left out.", optional: true },
  },
  steps: [
    { id: "answer", title: "Answer with each model", each: true },
    { id: "judge", title: "Judge the answers", after: ["answer"] },
  ],
};

const answers = await step("answer", () => parallel(args.models.map((model) => () =>
  agent(args.task, { model, title: `Answer: ${model}` }))));

const verdict = await step("judge", () => agent(
  `Several models were given the same task. Compare their answers on correctness, completeness and clarity, ` +
  `rank them, and say which is the best and why, in a few lines.\n\n## Task\n${args.task}\n\n` +
  args.models.map((model, i) => `## Answer ${i + 1} (${model})\n${answers[i] ?? "(no answer: the session failed)"}`).join("\n\n"),
  { title: "Judge", ...(args.judge ? { model: args.judge } : {}) }));

// The verdict is shown to the user; the answers return as data, for what goes on with the best one.
present(`## Verdict\n${verdict}`);
return { answers: args.models.map((model, i) => ({ model, answer: answers[i] })) };
