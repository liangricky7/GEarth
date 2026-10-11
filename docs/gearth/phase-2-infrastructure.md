# Phase 2: infrastructure and builders

Based on `origin/main` at `f730e18`; implementation branch: `gearth/phase-2-infrastructure`.

## Checkpoint 1: shared prerequisites

Copied the frozen PhaseContracts catalog and shapes, made Nation partial, and added miner/builder to unskilled occupations. New-world population limits, launchers, executable examples and tests now use 200. Historical evidence and handoff documents retain their original population figures.

Validation: 189 .NET tests passed, including the full-year calibration; 96 Windows-compatible Python tests passed. The Spark helper tests require POSIX `pwd`, unavailable on Windows. The design validator initially needs timezone data on this machine.

Shared-file detail: SocietyFactory's computed minimum is also bounded at 200, so direct engine callers and the API enforce the same limit. This is the population prerequisite rather than an infrastructure setup hook.

Construction behavior is the next checkpoint. No Spark runs or external services were used.
