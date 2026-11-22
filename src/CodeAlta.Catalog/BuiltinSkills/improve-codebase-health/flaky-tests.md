# Flaky tests

A test is flaky when it passes and fails on the same code. Find which tests are, name the mechanism of each, reproduce, then fix: a flaky test has a cause, and "timing" is not one.

## 1. Establish the facts

Which tests, how often, and since when, before reading any test:

```sh
# GitHub: runs that were run again, and those that passed the second time
gh run list --limit 200 --json databaseId,attempt,conclusion,headSha,workflowName \
  --jq '{runs: length, failed: (map(select(.conclusion=="failure"))|length), rerunPassed: (map(select(.attempt>1 and .conclusion=="success"))|map(.databaseId))}'
gh run view <id> --attempt 1 --log-failed      # what failed before the run passed
```

- A run that failed and then passed on the same commit is a lead, not yet a flaky test: its first attempt may have been cancelled, or have failed in the set-up or the build. Open the first attempt and keep what names a test. A test case that failed and passed on the same commit and the same configuration is the proof; it shows that the outcome varies, not yet why.
- Count per test, with the denominator: failures over the runs in which it ran.
- `signals.md` lists the commits about unstable tests and the tests that sleep, skip or retry: a test patched for flakiness before is likely to flake again.
- Retries configured in the runner or the CI hide the rate. Find them (`retries`, `rerun`, `flaky`, `--retry`) and count what they absorb if the runner reports it.
- Other CI systems: use their API or the test reports they keep (JUnit XML, TRX). No history at all: say so, and rely on reproduction.

Put the counts in `signals/`. Order the work by failures times people blocked: a few tests usually cause most of the red runs.

## 2. Find the cause

Read the failure message and the test, then name the cause. A flaky test has a mechanism; "timing" is not one.

| Cause | Signs | Cure |
| --- | --- | --- |
| **Waits a duration instead of a condition** (the most common) | `sleep`, `delay`, `waitForTimeout`, a fixed timer before an assertion; a check that reads state once, right after an action | Wait for the condition: an event, a callback, a promise, or a poll with a deadline. Expose a readiness signal in the product when none exists |
| **Deadline too tight** | Fails on slow CI machines, under parallel load, at browser or process start | One generous default deadline, defined in one place. A deadline costs time only when the test fails |
| **Depends on other tests** | Fails or passes with the order, alone but not in the suite, or the reverse | State created per test: fresh database, temp folder, port 0, no static or global state, no shared account. Clean up in a `finally` that reports its own failure |
| **A race in the product** | The failure also describes a possible user-visible bug | Fix the product. Do not weaken the test: it found something |
| **Time** | Fails at midnight, at the end of a month, in another time zone, or compares to "now" | A clock the test controls |
| **Randomness, unordered data** | Random data without a printed seed; assertions on the order of a set, a map, a directory listing | Seed and print the seed; sort or compare as sets |
| **Leaks** | Later tests fail; processes, ports, handles or browsers stay behind | The test owns what it starts and stops it; fail the test that leaks |
| **Network or external service** | DNS, rate limits, a real remote API | A local fake, with a contract test run apart from the main suite |
| **UI not settled** | Animation, focus, layout, a debounce; clicks that land on the wrong element | Assertions that retry until true; wait for the app's own "ready" state; turn animations off in tests; never a one-shot `isVisible()` check |
| **Environment** | Locale, fonts, display scale, path length, case sensitivity, antivirus locks on Windows, fewer cores | Pin what the test needs; start-up of a browser or server gets its own deadline, apart from the test's |

A pause between two checks of a condition, under a deadline, is polling, not a sleep: read the loop before you call it one.

A flaky test can be right: a team at Google traced one newly flaky test in six to a defect in the product. Decide which side the bug is on before touching the test.

## 3. Timeouts

A timeout detects a failure; it does not synchronize.

- **A sleep** ("wait 500 ms, then check") is always wrong as a wait: too short on a slow machine, wasted time on a fast one.
- **A deadline on a condition** ("until ready, 10 s at most") is right. Make it generous, define it once, raise it on CI through one multiplier. When it expires, the message says what was awaited and what the state was.
- **A performance assertion** ("answers within 200 ms") belongs in a benchmark, not in a functional test.

A finding that only raises a number is not a cure. Ask what the test waits for and make it wait for that.

## 4. Reproduce, then prove

A fix without a reproduction is a guess: say so when that is all there is.

- Run the one test many times (`--repeat-each`, `--count`, `--runs`, a loop), alone and with its neighbors, in random order when the runner can shuffle, with fewer workers or cores to imitate CI.
- Keep it gentle on a machine someone works on: one or two processes, a few dozen runs of one test, never a load generator. Heavy reproduction belongs on CI or needs the user's agreement.
- The same command after the fix is the verification: "0 failures in 40 runs, 7 before". No failure in N runs only bounds the rate at about 3 in N: after a failure seen once in 50 runs, 20 clean runs prove nothing.

## 5. Policy, the least that works

- Retries: none by default. Where one stays, every retry that passes is logged and counted, or it hides the next flaky test.
- Quarantine only as a bridge: with an owner, a date, and a cap on how many. A quarantined test protects nothing.
- A flaky test that checks nothing another test does not, and costs more than it catches: delete it. Say what coverage goes and why that is fine.
- A failing test must say why: the awaited condition, the state, a screenshot or log for UI tests. Recommend this where failures are unreadable; it is what makes the next flake cheap.
