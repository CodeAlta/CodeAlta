# Docs and wording: shorter, clearer, in layers

Text grows by addition: a caveat appended to a sentence, a paragraph for each change, a note that something used to be different. Models do this more than people, and both overlook the edit that removes. Each addition is true; the page as a whole stops being read. For instructions that agents load in every session, each sentence is also paid for on every turn.

This lens covers user docs, contributor docs, architecture notes, agent instructions (`AGENTS.md`, skills, prompts), READMEs and comments. The same rules apply to what a review writes: the analyses, the report, the plans.

## 1. Find where it costs

- `signals.md` lists the documents by size, with how often each changed. Long and often changed is where accretion happens; long and never changed may be stale.
- **Check it against the code.** Extract every path, command, flag, setting and name a document mentions, and test that each exists. This is objective and cheap, and a dead command is the strongest doc finding.
- **Find the fact told twice.** Search a distinctive term of a rule across the docs. Two places that state it will disagree after the next change.
- **Run the reader test** on the documents that matter (below).

## 2. The reader test

Write three to five questions the document must answer for its reader ("How do I run one test?", "Which module owns retries?"). Give a fresh session **only the document** and the questions, on a model of the panel at a low effort: `alta session create --same-model-as <your session id> --reasoning low --no-sub-sessions`. Ask for each answer and the heading where it was found.

A wrong answer, "not found", or an answer found far down is the evidence: it shows what a reader meets, where reading the page yourself shows what you already know. The same questions verify the rewrite.

## 3. Give it layers

A reader takes the first layer and goes deeper only when needed:

1. **The title and one sentence**: what this is and who needs it.
2. **What most readers need to act**, on about one screen: the command, the rule, the default.
3. **Details on demand**: sections whose headings say what they answer, a linked page, a table of reference.

Rules of the structure:

- The conclusion first, in the document and in each section. Background after, or not at all.
- At most two levels down. A reader sent to a third place is lost.
- A link or a heading says what is behind it. "More details" does not.
- One purpose per page: learning step by step, doing a task, looking up a fact, understanding why. A how-to interrupted by history and reference tables is the usual culprit: move them, do not trim them in place.
- One fact, one place. Everything else links to it.

## 4. Cut

Read each sentence with one question: **would the reader do something wrong, or miss something they need, without it?** If not, it goes.

What goes almost always:

- History: "previously", "we now", "new", "has been changed to". The reader needs what is, not what was.
- Announcements and throat-clearing: "it is important to note that", "this section describes".
- A restatement of the heading, of the sentence before, or of the code beside a comment.
- Hedges and reassurance: "generally", "in most cases", "simply", without the case that differs.
- Caveats for situations the reader will not meet. Keep the one that prevents data loss or an hour of confusion.
- Provenance: who decided, in which discussion, for which ticket.

What stays: the reason behind a rule that looks arbitrary (one clause), the exact command, the constraint, the warning before an irreversible step, an example where the rule alone is abstract.

Then, for what remains:

- One idea per sentence. The condition before the instruction: "To run one test, pass `--filter`."
- The same word for the same thing every time. Two names make the reader look for two things.
- A number instead of an adjective; an example instead of an abstraction; a table for facts that share a shape.

Shorter is not the goal; clearer is. Do not compress into fragments, drop articles or replace a sentence by jargon and arrows: a reader who has to decode saves nothing.

## 5. Edit by rewriting

The rule that prevents the next accretion: **never append to make a point**. To add a fact, rewrite the sentence or section that covers the subject so that it reads as written once. A change to a document that adds words should remove at least as many elsewhere in it, unless it documents something users see and nothing covered.

## Comments

A comment earns its place by saying why: the constraint, the trap, the reason for the odd choice. Delete the comment that repeats the code, the one that describes a change ("fixed the race"), the commented-out code, the `TODO` nobody owns. A long explanation above code that changes often is a hint that the code wants to be simpler.

## What a doc finding contains

The document and what its reader comes for; the evidence (dead references, the fact told twice with both places, the reader test's result, the position of the first useful answer); the change as a list of what is deleted, moved and rewritten; words before and after; a short passage rewritten as the before and after of the finding; the questions that verify it.

A doc finding removes or rewrites. It never proposes a new page, except to move existing material out of one that mixes purposes.
