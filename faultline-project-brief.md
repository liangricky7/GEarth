# Faultline — observable AI society

Faultline is an observable simulation in which individual AI agents work, govern, trade, finance shipments, and react to resource shortages and conflict. Viewers inspect countries, agents, decisions, and consequences rather than controlling the world.

**Current implementation:** a tested 50-agent Stage 1 demo with a C# engine, Python worker, one Spark model server, Neon persistence, and a read-only observer. Ten-country society rules have been drafted and compile, but the expanded coordinator and viewer are unfinished.

**Current direction:** qualify the largest useful population on one Spark, provisionally 1,000 agents with separate institutional and resident decision cadences. Countries have explicit government rules, resource differences, budgets, and individual leaders. Economic consequences are governed by cash and goods conservation.

**Primary track:** Best AI. Neon and model inference are working in Stage 1. Full Sail, BNY, Databricks, Solana, and AMD claims require their corresponding implemented workflows and evidence; design intent alone is insufficient.

**Team:** Jayden Hong, Kennedy Nguyen, Ricky Liang, and Ahmad Hijazi. Delivery deadline: Sunday, October 11, 2026, 10:00 a.m. EDT.

Read the [current plan and design](faultline-current-plan-and-design.md) for the complete scope, measured Spark throughput, government profiles, and next implementation steps. Use the [README](README.md) to start the working Stage 1 demo.
