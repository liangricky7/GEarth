# GEarth — Trello and GitHub workflow

[Trello task board](https://trello.com/b/b0cLTB64/diarockets-kanban) · [GitHub repository](https://github.com/liangricky7/GEarth)

Trello holds task owners, stages, progress, blockers and completion criteria. GitHub holds the source, branches, commits, reviews and pull requests. Owners remain on Trello; GitHub issue assignment is not needed.

## One task, from start to finish

1. Read the Trello card's finished criteria and prerequisites. Inspect the existing branch before writing new code.
2. When work begins, change the card to **DOING**. Create a branch for that task and record its name and base on the card.
3. Keep changes focused on that task. Commit runnable pieces and run checks appropriate to the change.
4. Open a pull request, put the Trello card URL in its description, and put the PR URL and check results on the card. Set the card to **REVIEW**.
5. Resolve review findings. Merge only after the relevant checks and team review. Branch creation and PR creation do not by themselves finish a task.
6. When the card's finished criteria are met, record the merged revision and evidence, mark the card done, and move it to **Completed**. A report, benchmark, rehearsal or submission task can finish with its saved evidence without a code PR.

Codex updates the matching cards and GitHub records while working on tasks in this chat. Work completed elsewhere can be reconciled from its branch, PR or saved results in a later task. These are MCP-driven updates; continuous background synchronization is not configured.

## Branches and merge order

`main` is the shared integrated version. Use one short descriptive branch per new task, created when work starts: `feat/<description>` for features, `fix/<description>` for bug fixes, `test/<description>` for test coverage, `docs/<description>` for documentation, or `chore/<description>` for maintenance. Use lowercase words separated by hyphens, for example `test/worker-failure-checks` or `docs/evaluation-design`. Branch names describe the work and must not use a `codex/` prefix.

Use PR titles in the form `<type>: <short description>`, with the same types above. Describe the resulting change, for example `test: verify worker failure recovery and fallback counts` or `fix: prevent stale panels when switching saved runs`. Keep the owner and Trello link in the description. PRs retain their dependency base, relevant check results, and review requirements.

Read the latest branch head before updating it. Preserve unrelated changes. A breaking shared contract change needs the engine, worker, storage and website owners to coordinate their adapters and fixtures in the same reviewed change.

The expanded-world implementation and the subsequent scheduling, compute, observer, metrics, banking and analytics branches are integrated into `main`. Start new tasks from the latest main revision. Original pull requests and commits retain the development history.
For new work that requires a future unmerged branch, record it as the dependency/base and target the PR at that branch. After the dependency reaches main, update the task branch and retarget the PR to main. Avoid merging copies of the same existing work twice.

After integration, delete completed remote task branches once their tips are ancestors of `main`, their exact heads belong to merged pull requests, or their remaining patches are verified equivalents of commits in `main`. Check for open pull requests and newly pushed commits first. Preserve branches with unique work. Historical commits and merged pull requests remain the source of record; old branch names are not a list of pending features.

## Status meanings

| Trello status | Meaning |
| --- | --- |
| START NOW | Required inputs are available; work can begin. |
| DOING | Someone has actually started. |
| REVIEW EXISTING WORK / REVIEW | Code or evidence exists; compare it with the task's finished criteria. |
| LATER | An earlier named result, feature or access is needed. The card states which. |
| DONE | The finished criteria are met and the card is in Completed. |

Keep active and review cards in their numbered stage. **Optional** is a scope choice; it is not a separate implementation stage.

## Task order and earlier evidence

1. **Existing simulation:** keep the expanded core complete as reported; share the repository revision and [run instructions](08-world-runbook.md).
2. **AI, scheduling and storage:** review existing worker, scheduler, measurements, API, persistence and integrated-run work.
3. **Speed and population:** existing live evidence reached 977/1,100 coverage. Host sleep interrupted the run, so sustained cadence and full coverage remain unqualified. This measurement gap does not prevent independent observer or evaluation work.
4. **Website and demo:** panels exist on the branch; inspect their layout, causal navigation and demo story in a working browser.
5. **Comparison and optional integrations:** declare a fair AI-versus-rules procedure, then collect matched runs. Government comparisons, Databricks, Solana and AMD remain optional or conditional.
6. **Final demo and submission:** preserve a working revision, settings, saved run and evidence; rehearse and verify submitted links.

The branch README reports 30 C# and 14 Python tests. Workflow setup inspected source and recorded evidence; it did not rerun those tests. A subsequent browser review on October 10 confirmed the new hosted observer loads, but found a saved-run rendering error and mixed run data. See the Stage 4 cards and the local task board for reproduction steps.

## Earlier Ahmad card handoff

- [Connect the Python worker to the expanded simulation](https://trello.com/c/Vkps1ook) — stage 2, implemented; awaiting review and integration.
- [Check bad model responses, timeouts, and fallback behavior](https://trello.com/c/qn0TEGCa) — stage 2, checks passed; awaiting review and integration.
- [Record model speed, errors, and accepted decisions](https://trello.com/c/lswO2Gu9) — stage 2, review existing work.
- [Measure Spark speed using real world prompts](https://trello.com/c/lLhey8gB) — stage 3, later.
- [Test a longer run and check every agent gets turns](https://trello.com/c/KCCLSC9M) — stage 3, later.
- [Define a fair comparison between AI and simple rules](https://trello.com/c/XZTv81dZ) — stage 5, start now.
- [Run the AI-versus-rules comparison and report results](https://trello.com/c/jceHvt82) — stage 5, later.
- [Compare government configurations](https://trello.com/c/LT2UIS0u) — stage 5, later, optional.
- [Analyze saved simulation runs in Databricks](https://trello.com/c/POlAT0GH) — stage 5, later, optional.

For a completed task, record: Trello URL, branch, PR (if applicable), exact revision, check command and result, saved run/report or screenshot, and any remaining limitation. Keep credentials in private configuration.

## Historical GitHub publishing status

GitHub MCP access was verified on October 10, 2026. The connector lists `liangricky7`'s installation with `dia-rockets`; branch and draft PR creation succeeded. The workflow setup is on `docs/team-workflow`. Existing expanded-world work is in [draft PR #1](https://github.com/liangricky7/GEarth/pull/1). No application code was changed or merged by this setup.

Ahmad's authorized worker failure checks are implemented on `test/worker-failure-checks`: 21 Python tests and 44 C# tests passed, with no runtime changes needed. Review and integration remain pending.

## Branch consolidation evidence

The branch cleanup integrates all remote branch heads while preserving their commit ancestry. See [integration evidence](evidence/branch-consolidation.json) for tests, a combined banking/analytics rule run and the independently reproduced upstream calibration failure. Source integration does not itself finish every Trello acceptance criterion or update the running cloud release.
