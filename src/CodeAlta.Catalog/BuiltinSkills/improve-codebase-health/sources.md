# Sources

Where the rules of this skill come from. Read one when a rule is disputed or a case falls outside it.

## Simplicity and module design

- John Ousterhout, *A Philosophy of Software Design*: complexity comes from dependencies and obscurity and grows in small increments; deep modules hide much behind a small interface. https://web.stanford.edu/~ouster/cgi-bin/aposd.php
- David Parnas, "On the Criteria To Be Used in Decomposing Systems into Modules" (1972): a module hides a decision likely to change. https://doi.org/10.1145/361598.361623
- Matt Pocock, `improve-codebase-architecture` and `codebase-design` skills: the deletion test, "one adapter is a hypothetical seam, two are a real one", replace tests instead of layering them. https://github.com/mattpocock/skills/tree/main/skills/engineering
- Sandi Metz, "The Wrong Abstraction": duplication is cheaper than the wrong abstraction; inline it back when it has grown flags. https://sandimetz.com/blog/2016/1/20/the-wrong-abstraction
- Dan Abramov, "Goodbye, Clean Code": removing duplication can cost more than it saves. https://overreacted.io/goodbye-clean-code/
- Kapser and Godfrey, "'Cloning considered harmful' considered harmful" (2008), and Juergens et al., "Do code clones matter?" (ICSE 2009): clones are often deliberate and harmless; the ones changed inconsistently cause defects. https://doi.org/10.1007/s10664-008-9076-9 · https://doi.org/10.1109/ICSE.2009.5070547
- Martin Fowler, "Yagni" and "Is High Quality Software Worth the Cost?". https://martinfowler.com/bliki/Yagni.html · https://martinfowler.com/articles/is-quality-worth-cost.html
- Adams, Converse, Hales and Klotz, "People systematically overlook subtractive changes", Nature 592 (2021): people search for additions and miss subtractions unless prompted. https://www.nature.com/articles/s41586-021-03380-y

## History as evidence

- Adam Tornhill, *Your Code as a Crime Scene* (2nd edition 2024): hotspots are code that is both complex and often changed, a few percent of the files carrying most of the work; files that change together reveal dependencies the structure hides.
- Nagappan and Ball, "Use of relative code churn measures to predict system defect density" (ICSE 2005). https://doi.org/10.1109/ICSE.2005.1553571
- GitClear, AI code quality reports (2024 to 2026): more copied blocks, less refactoring since coding assistants; vendor research, read with that in mind. https://www.gitclear.com/

## Tests

- Google Testing Blog, "Flaky Tests at Google and How We Mitigate Them" (2016): about 1.5% of test runs are flaky; flaky tests appear as fast as they are fixed. https://testing.googleblog.com/2016/05/flaky-tests-at-google-and-how-we.html
- Google Testing Blog, "Where do our flaky tests come from?" (2017): larger tests are flakier; start with the smallest test that does the job. https://testing.googleblog.com/2017/04/where-do-our-flaky-tests-come-from.html
- Luo, Hariri, Eloussi and Marinov, "An Empirical Analysis of Flaky Tests" (FSE 2014): asynchronous waits, concurrency and test order are the main causes. https://doi.org/10.1145/2635868.2635920
- Martin Fowler, "Eradicating Non-Determinism in Tests": never a bare sleep; callbacks or polling with a deadline; isolation, clocks, quarantine with a limit. https://martinfowler.com/articles/nonDeterminism.html
- GitHub Engineering, "Reducing flaky builds by 18x": detect by re-running on the same code, record every flaky failure, give people the few tests that cause most of the damage. https://github.blog/engineering/engineering-principles/reducing-flaky-builds-by-18x/
- Playwright, "Best Practices": assertions that wait and retry, no fixed timeouts, test what the user sees. https://playwright.dev/docs/best-practices
- Winters, Manshreck and Wright, *Software Engineering at Google*, chapters 11 to 14: test behavior through public interfaces, prefer fakes to mocks, small tests first. https://abseil.io/resources/swe-book

## Docs

- Nielsen Norman Group, "Progressive Disclosure": show the most important first, make the way to the rest obvious, no more than two levels. https://www.nngroup.com/articles/progressive-disclosure/
- Diátaxis: tutorials, how-to guides, reference and explanation are four kinds of documentation that mix badly. https://diataxis.fr/
- Google, "Technical Writing" courses: short sentences, one idea each, the audience first. https://developers.google.com/tech-writing
- *Software Engineering at Google*, chapter 10: documentation is owned, reviewed and deleted like code. https://abseil.io/resources/swe-book/html/ch10.html

## Landing changes

- Kent Beck, *Tidy First?*: structural and behavioral changes apart; tidy in small reversible steps, and not always.
- Michael Feathers, *Working Effectively with Legacy Code*: seams and characterization tests.
- Ellnestam and Brolund, *The Mikado Method*: reach a large change through small ones that each leave the code working. https://mikadomethod.info/
- Martin Fowler, "Strangler Fig" and "Branch by Abstraction": replace in place, step by step. https://martinfowler.com/bliki/StranglerFigApplication.html · https://martinfowler.com/bliki/BranchByAbstraction.html
- qntm, "Ratchets in software development": count the uses of a pattern to remove, fail when the count rises, lower it as they go. https://qntm.org/ratchet

## Several models

- Du et al., "Improving Factuality and Reasoning in Language Models through Multiagent Debate" (2023). https://arxiv.org/abs/2305.14325
- Zheng et al., "Judging LLM-as-a-Judge with MT-Bench and Chatbot Arena" (2023): position, verbosity and self-preference in model judges. https://arxiv.org/abs/2306.05685
- Sharma et al., "Towards Understanding Sycophancy in Language Models" (2023). https://arxiv.org/abs/2310.13548
- Andrej Karpathy, `llm-council`: first opinions alone, review with the authors hidden, one chairman who writes the answer. https://github.com/karpathy/llm-council
