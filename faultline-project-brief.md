# Faultline — observable AI society

Integration note (October 10): [current plan and design](faultline-current-plan-and-design.md) is the maintained combined specification. This document preserves the earlier brief or economy decisions and analysis. Main now includes weekly labor-market scheduling, scoped Qwen Spark/AMD compute and resident endowments; deferred banking and calibration decisions remain deferred.

Faultline is an observable simulation in which individual AI agents work, govern, trade, finance shipments, and react to resource shortages and conflict. Viewers inspect countries, agents, decisions, and consequences rather than controlling the world.

**Current implementation:** an integrated ten-country world with a C# engine, bounded Python worker, one Spark model server, Neon/local persistence, and a read-only observer. A 1,100-agent rule-mode run passed accounting and event-history checks. The first expanded live run reached 977 model-deciding agents; laptop sleep interrupted that qualification, and further coverage testing has been deferred.

**Current direction:** qualify the largest useful population on one Spark, a 1,100-agent test target with separate institutional and resident decision cadences. Countries have explicit government rules, resource differences, budgets, and individual leaders. Economic consequences are governed by cash and goods conservation.

**Primary track:** Best AI. Neon and model inference are working in the expanded world. Full Sail, BNY, Databricks, Solana, and AMD claims require their corresponding implemented workflows and evidence; design intent alone is insufficient.

**Team:** Jayden Hong, Kennedy Nguyen, Ricky Liang, and Ahmad Hijazi. Delivery deadline: Sunday, October 11, 2026, 10:00 a.m. EDT.

Read the [current plan and design](faultline-current-plan-and-design.md) for the complete scope, measured Spark throughput, government profiles, and next implementation steps. Use the [README](README.md) to start the expanded world.
