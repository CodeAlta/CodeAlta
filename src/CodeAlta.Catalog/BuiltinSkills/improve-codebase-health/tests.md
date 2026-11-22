# Tests: what they are worth

A test suite is worth what it tells you. Four questions decide it, and a review asks them of the tests that guard what matters most, not of every test:

1. **Does a pass mean the behavior works?** Tests that cannot fail, or that check something else than they claim.
2. **Is what matters covered?** The behaviors whose failure costs most, not a percentage.
3. **Does a failure mean a defect?** Flaky tests.
4. **Do the tests cost what they are worth?** Slow, brittle and redundant tests.

In the order of work, the tests come first: every other change of a review is judged by the suite.

## Does a pass mean the behavior works?

A test earns its place when it would fail if the behavior it names broke. Look for the ones that would not:

| Sign | What the test really does |
| --- | --- |
| No assertion, or only "not null", "did not throw", "was called" | Runs the code without checking its result |
| Asserts what the test itself set up: the value a mock was told to return, a constant copied from the code | Checks the test, not the product |
| The expected value is computed by the same logic as the code | Agrees with a bug |
| A snapshot or golden file accepted again at each change | Records what the code does, right or wrong |
| A `try`/`catch`, a condition or a loop around the assertions | Passes on the path that asserts nothing |
| A name that says one thing and a body that checks another | Misleads whoever relies on it |

To settle a doubt, break the behavior and watch: invert the condition or return a constant in the code under test, then run its tests. Those that stay green do not test it. Do this only in a checkout of your own (a session started with a worktree), never in the folder the user or the other analysts work in; without one, make it the first step of the finding instead. Where the project has a mutation tool (Stryker, PIT, mutmut), its surviving mutants in the hotspots are leads.

A test checks the result a caller or a user gets, through the interface they use. One that pins what nobody relies on (the wording of a log, the order of unrelated calls, a private field, whitespace) fails for changes that break nothing and stays silent for those that do.

## Is what matters covered?

A coverage percentage counts lines, and lines are not equally worth testing. Rank the behaviors by what their failure costs and start at the top:

- what loses or corrupts data: saving, migrations, concurrent writers, recovery after a crash;
- what users do most, and what money, access or safety depends on;
- the error paths: a full disk, a dropped connection, a wrong input. They are the least tested and the most surprising;
- the code that changes and breaks: the hotspots and the files fixed most often.

Evidence that something important is not covered:

- **A fix without a test.** `signals.md` counts, per area, the fix commits that touched no test. Read a few: a defect fixed twice without a test is the strongest case for one.
- A hotspot with no test, or whose tests never change when it does.
- A public entry point that no test calls: search its name in the tests.
- A coverage report, when the project has one, read for the files at the top of the list only: an uncovered branch there is a lead; the total is not.

A finding names the behavior, what its failure costs, and the one test that would catch it, at the lowest level that can. It never asks for "more tests" or a percentage.

The reverse is a finding too: twenty tests on a formatting helper and none on the save path. Tests of details in code that never breaks are paid on every run and every refactor: propose deleting those that check nothing a caller relies on.

## Does a failure mean a defect?

A test is flaky when it passes and fails on the same code. Tests that fail without a cause teach everyone to run again and stop reading failures, and then a real defect passes as "flaky again". Retries and raised timeouts hide the rate without removing the cause.

When the CI history, the commits about unstable tests or the sleeps and retries of `signals.md` point at flaky tests, follow [flaky-tests.md](flaky-tests.md): the facts first, then the cause of each, a reproduction, and the least policy that works.

## Do the tests cost what they are worth?

**Slow.** Measure first: the slowest tests and fixtures from the runner's timing report, and the total wait of fixed sleeps. Real costs: sleeps (they add up on every run), a browser or a server started per test where one per file would do, end-to-end tests for logic a small test could check, the same expensive set-up repeated. The cure is the smallest test that can catch the bug; larger tests are more often flaky as well.

**Brittle.** Tests that break when nothing a user can see changed. Signs: tests edited by commits that change no behavior (a rename, a move, a refactor); mocks that assert which internal method was called, and how often; snapshots of internals; assertions on source text or private fields. A test that changes with the behavior it checks is healthy, however often. Cure: test through the module's interface, with real collaborators or simple fakes, and assert results. When a layer is merged or deleted, its tests go with it, replaced by tests at the interface that remains.

**Redundant.** The same behavior checked at three levels, or by cases that differ in nothing the code distinguishes. Keep the cheapest test that fails for the defect and delete the others; say which behavior each deleted test duplicated.

## What a test finding contains

The test or the pattern with file and line; what it costs today, with its number (failures in N runs, fixes without a test, seconds, tests edited per refactor); the cause in one sentence; the cure; how it was shown (the reproduction, the behavior broken on purpose and the tests that stayed green) or the statement that it was not; the verification command and the number to reach.