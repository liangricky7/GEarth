# Faultline — product and demonstration

## Experience and audience

Build an observable simulation for curious visitors and students learning how decisions propagate through societies, logistics, and finance. The application opens directly on a running or recorded world. No sign-in, wallet connection, player faction, or intervention controls are required to observe it.

The audience can inspect an agent, country, shipment, event, or financial institution. These selections change the view, never the simulation. Replay selection and camera navigation are acceptable viewing controls. Scenario creation, restart, and policy changes belong to operator scripts, not the public UI.

Five fictional countries avoid implying that model behavior represents real populations. Each has three locations, one government, two businesses, one bank, one media agent, and 95 individual citizens: 100 agents per country, 500 total. Visible names, occupations, risk tolerance, trust, and local social connections differ. Country personalities are initial incentives rather than permanent scripted outcomes.

## What agents can decide

| Role | Goals and available decisions |
| --- | --- |
| Government | Balance legitimacy and trade; open/close a border, propose/accept/reject an agreement, publish a statement |
| Business | Produce and sell goods; choose a market order, request financing, select a route objective, dispatch reserved inventory |
| Bank | Preserve liquidity while earning returns; approve/reject a loan offer, respond to repayment pressure |
| Media | Build audience and credibility; publish a claim received through its information network |
| Citizen | Meet needs and preserve wealth; buy goods, move deposits, express support/protest, migrate, or message a neighbor |

All actions are constrained by the engine. No agent can create currency, change loan terms after acceptance, invent routes, or read another agent's private state. A role's goal can conflict with its country's aggregate welfare.

## Screen composition

The globe occupies roughly two thirds of a desktop view. It shows labeled locations, route arcs, convoy motion, and country status. The remaining area contains the live event feed and a selected entity inspector. A compact header shows simulation tick, LIVE/REPLAY/FIXTURE status, population, unique agents with model decisions, unresolved shortages, and working inference nodes.

Use three views over the same world: Society, Logistics, and Finance. Keep persistent selection across views. Society shows citizen need and public messages; Logistics shows route alternatives and delayed shipments; Finance shows bank cash, deposits, loans, and linked trades. Aggregate citizens at distant zoom; render individuals only for the selected region. Agent count must not depend on how many dots are drawn.

The inspector displays an identity, current situation, recent accepted and rejected actions, short stated rationales, and event references. “Information available then” is separate from “what the engine later recorded.” A rumor is labeled unverified; a recorded engine effect is labeled observed. Do not expose an omniscient diagnosis as an agent's own knowledge.

An event detail follows explicit cause IDs: border closure → route invalidated → financing request → bank decision → shipment result. Branches can end in failure. Do not invent causal arrows from a fluent model summary.

Accessibility: readable text, keyboard-accessible tabs and entity lists, icon/label differences in addition to color, and reduced-motion rendering. Route closure and shortage indicators must remain understandable in a static screenshot. Avoid flashing crisis effects and a constantly rotating globe.

## Primary scenario

Use [scenario.demo.json](scenario.demo.json) as the authoritative initial configuration. A prepared medicine trade creates a useful demonstration even before autonomous agents generate new trades. It is labeled a seeded contract. All later responses to its disruption come from agents or declared engine rules.

The world runs for 120 logical ticks, with a starting target of three wall-clock seconds per tick. A tick represents one abstract simulation period, not a minute of real-world economic time. The reference disruption closes a direct transport connection at tick 8. Prices, shortages, lending pressure, and information propagation continue whether an alternative shipment succeeds or fails.

The public view does not offer a “fix crisis” button. Preconfigured runs explore different responses automatically. The operator may start the chosen run before judging; repeatability comes from saved configurations and recorded event logs.

## Three-minute demonstration

1. **0:00–0:30:** identify the 500-agent population and the two confirmed Spark workers; include AMD only if running. Show actual unique decision coverage and accepted actions rather than a decorative counter.
2. **0:30–1:15:** follow the border disruption and a convoy's route change or inability to find an eligible route. Inspect a business or government decision and its available information.
3. **1:15–2:00:** inspect the financing decision, bank exposure, and a Solana settlement record with a real devnet transaction link if confirmed.
4. **2:00–2:40:** show the Databricks scenario comparison, including run counts and failed runs. Explain the logistics lesson and modeled finance tradeoff.
5. **2:40–3:00:** follow one citizen's observed response. State what is live, what is recorded, and which outcomes remain unvalidated outside the simulator.

Prepare a captured run because live agent decisions can differ. Recorded runs retain model provenance and transaction evidence and are labeled REPLAY. Synthetic fixtures are labeled FIXTURE and never described as captured results.

## Scope boundaries

Required: individual agents, memory, local messaging, resources, legitimacy/trust, autonomous trade and financing choices, meaningful route calculation, live viewer, persistent events, devnet settlement, and cross-scenario analytics. Add the ROCm worker when AMD credits and supported hardware are confirmed. Nuclear combat, elections with full constitutions, face detection, token speculation, audio, custom shaders, and reinforcement learning are later features. Represent conflict first through border closures, trade restrictions, and legitimacy changes; these already generate observable political consequences.

## Product acceptance

A visitor can identify a disruption, inspect at least one real model-generated response, trace its recorded effects, distinguish pending from confirmed payment, and understand why one route is selected over another. A full run contains 500 separate identities with recorded decision coverage; a low-resolution globe must not conceal missing agents or a frozen simulation.
