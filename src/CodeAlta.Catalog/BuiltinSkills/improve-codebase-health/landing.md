# Landing the changes

A report that nobody acts on has cost its tokens for nothing. A finding lands when it is small enough to start, safe enough to merge, and checked against the number it promised.

## After the report: the user's choices

Ask which findings to do, defer or reject, once, with the first wave of the order of work as your recommendation. Then:

- Write each decision to the ledger ([report.md](report.md)), a rejection with its reason.
- Set `status` of the findings in `report.json` (`accepted`, `deferred`, `rejected`) and build again.
- For each accepted finding, create what tracks it, and put its id or path in `task`:
  - small (`S`): a task. `alta task create --title "<the title of the finding>" --kind problem|improvement --summary "<its claim>" --stdin`, with the finding as the description: problem, evidence, change, first step, verification, risks, and the path of the report.
  - larger, or several findings that depend on each other: a plan under `.alta/plans/` with one checkbox per step, each step ending in its verification.
- Create nothing for what the user did not choose.

"Copy as a prompt" on a finding of the HTML report gives the same brief for a session started by hand.

## Order

What a change depends on comes first. When nothing else decides:

1. **Tests that lie**: flaky and order-dependent tests with the retry that hides them, tests that cannot fail, and the missing test of a behavior the next steps touch. Every later change is judged by the suite.
2. **Deletions**: dead code, unused options, tests of nothing. They shrink what the next steps have to move.
3. **Merges and simplifications** in the hotspots.
4. **Moves and boundary changes**, which touch many files.
5. **Guards**, last and few: only for a mistake that came back.

Docs rewrites fit anywhere; after a structural change, the docs of that area are rewritten with it, not before.

## Implementing one finding

1. **Check that it is still true.** The report was written at a commit; open the cited code first. If the finding no longer holds, say so, mark it in the ledger and stop.
2. **Pin the behavior.** Where no test covers what the change must preserve, write that test first, through the interface that will remain.
3. **Take the first step alone.** It is the smallest change that lands by itself; commit it when its verification passes.
4. **Keep structure and behavior apart.** A commit either moves, renames, merges or deletes without changing behavior, or changes behavior, never both. A defect met on the way gets its own commit and its own test.
5. **When a step breaks more than expected**, undo it, note what it needs first, and do that first. Do not push through a red build.
6. **Stay inside the finding.** What you notice beside it is a new observation for the ledger or a task, not part of this change. The `outOfScope` of the finding says what was already considered and left.
7. **Delete what the change makes unused**: the tests of a removed layer, the option nobody reads any more, the paragraph that described the old shape.
8. **Measure what was promised.** Run the `verify` of the finding and collect the number of its claim again: lines, failures in N runs, words, seconds. Report the difference, including when it is smaller than claimed.
9. **Record it** in the ledger: done, the commit, the measured result.

One session writes at a time in a working tree. Changes that are independent can go to sessions with a worktree each; the session that drives merges and verifies them one by one.

## A second opinion on the diff

For a change that is large, touches a boundary or deletes much, have another model of the panel the user chose review the diff before it is committed: a read-only session given the finding and the diff, asked for defects and for anything the change added that the finding did not call for. It is the confrontation of the review, continued where the mistakes are made.

## Following up

After the accepted findings landed, or at the next review: run the signals again, compare with the baselines of the ledger, and say what moved. Compare only what was measured the same way: the same command, window and runner. A number that did not move then says that the cause was elsewhere; one measured differently says nothing.
