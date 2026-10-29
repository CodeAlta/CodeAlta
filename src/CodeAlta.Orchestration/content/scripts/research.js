// Splits a question into sub-questions, researches each in a session of its own, then writes one answer from their notes.
export const meta = {
  name: "research",
  title: "Research a question",
  description: "Splits a question into sub-questions, researches them in parallel, one session each, then writes one answer from their findings.",
  whenToUse: "For a question that needs reading in several places: the code, the documentation, the web.",
  params: {
    question: { type: "string", description: "The question, with what the answer is for." },
    angles: { type: "integer", description: "How many sub-questions to research.", default: 4 },
    tier: { type: "string", description: "The tier of the researching sessions: complex, standard or quick.", default: "standard" },
  },
  steps: [
    { id: "plan", title: "Split the question" },
    { id: "explore", title: "Research each sub-question", after: ["plan"], each: true },
    { id: "synthesize", title: "Write the answer", after: ["explore"] },
  ],
};

const tier = args.tier ?? meta.params.tier.default;
const angles = args.angles ?? meta.params.angles.default;

const questions = await step("plan", () => agent(
  `Split this question into at most ${angles} sub-questions that can be researched separately and that together answer it.\n\n${args.question}`,
  { tier: "quick", title: "Split the question", schema: { type: "array", items: { type: "string" }, maxItems: angles } }));

present(`## ${args.question}\nResearching ${questions.length} sub-questions, one session each:\n${questions.map((question) => `- ${question}`).join("\n")}`);

const notes = await step("explore", () => parallel(questions.map((question) => () =>
  agent(`Research this question, part of a larger one, with the tools you have (search and read the code, the documentation, the web if you can). ` +
    `Answer with what you found and where, and say what you could not find.\n\nQuestion: ${question}\nLarger question: ${args.question}`,
    { tier, title: `Research: ${question.slice(0, 60)}` }))));

// The answer is shown to the user; what returns says what was researched, and what could not be.
const answer = await step("synthesize", () => agent(
  `Answer this question from the notes below, citing where each fact comes from, and say what remains unknown.\n\n# ${args.question}\n\n` +
  questions.map((question, i) => `## ${question}\n${notes[i] ?? "(this research failed)"}`).join("\n\n"),
  { tier, title: "Write the answer" }));
present(answer);
return { question: args.question, researched: questions.filter((_, i) => notes[i] !== null), failed: questions.filter((_, i) => notes[i] === null) };
