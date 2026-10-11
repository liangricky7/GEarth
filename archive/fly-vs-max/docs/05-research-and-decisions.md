# Research and design decisions

Historical/supporting document. Use [10 — execution roadmap](10-implementation-roadmap.md) and [11–13 — current contracts](11-implementation-contract.md) for build decisions. Earlier schedules, inputs, data quotas, and learning recipes below do not add requirements to that plan.

Sources checked October 4, 2026. Documentation is evidence for interfaces and provenance; compatibility and performance still need measurement.

## Primary sources

| Source | What it establishes | What it does not establish |
| --- | --- | --- |
| [Google's male fly CNS article](https://research.google/blog/a-connectomics-milestone-mapping-the-complete-male-fruit-fly-brain/) | Structural connectome release and collaboration | A ready-to-train racing network |
| [Janelia MaleCNS](https://www.janelia.org/project-team/flyem/male-cns-connectome) | Official dataset context, access routes, CC-BY attribution, v1.0 release | Unique choices of functional dynamics or racing inputs |
| [Official downloads](https://male-cns.janelia.org/download/) | Dataset files and provenance entry point | An imported and validated graph in this project |
| [EA UDP documentation](https://forums.ea.com/blog/f1-games-game-info-hub-en/ea-sports%E2%84%A2-f1%C2%AE25-2026-season-pack-udp-specification/12187347) | Versioned telemetry output documentation | Control injection, reset, or accelerated training APIs |
| [F1 25 telemetry structures](https://forums.ea.com/t5/s/tghpe58374/attachments/tghpe58374/f1-games-game-info-hub-en/61/5/F1%2025%20Telemetry%20Output%20Structures.txt) | Candidate fields for motion, controls, timing, and session identity | Compatibility with an unspecified game edition |
| [vgamepad repository](https://github.com/yannbouteiller/vgamepad) | Python analog controller interface candidate | Acceptance by the team's EA F1 installation |
| [ViGEmBus repository](https://github.com/nefarius/ViGEmBus) | Driver project's retired status | A maintained integration path guaranteed for this host |
| [Connectomic control paper](https://arxiv.org/abs/2602.17997) | Research precedent for graph-based embodied control | Expected lap performance or a weekend training budget |
| [FIA Austrian 2023 qualifying classification](https://www.fia.com/events/fia-formula-one-world-championship/season-2023/austrian-grand-prix/qualifying-classification) | Verstappen's 1:04.391 qualifying result | Comparable game conditions or full-lap telemetry |
| [NVIDIA Spark system overview](https://docs.nvidia.com/dgx/dgx-spark/system-overview.html) | ARM64/Blackwell architecture and software environment | Tested compatibility or graph throughput for this project |
| [NVIDIA Spark specifications](https://www.nvidia.com/en-us/products/workstations/dgx-spark/) | Published hardware configurations and precision-qualified peak performance | Real-time sparse recurrent performance |

Google's September 2026 article describes the release the user supplied. Janelia records MaleCNS v1.0 availability from June 2026 and official publication in September. Keep dataset release version distinct from article/publication date. [Janelia timeline](https://www.janelia.org/project-team/flyem/male-cns-connectome)

## Decision record

| ID | Proposed decision | Reason and tradeoff |
| --- | --- | --- |
| D1 | Use MaleCNS, not interchangeably FlyWire | Matches the supplied release; model/data provenance stays clear |
| D2 | Telemetry plus recorded path first | Avoids building perception during the weekend; limits claims to known-track driving |
| D3 | Fixed sparse rate network, learned readout | Limits training cost; does not reproduce biological learning or tune every connection |
| D4 | Benchmark full graph before reduction | Preserves the ambitious premise where feasible; compute remains unproven |
| D5 | Imitation before online improvement | Uses scarce game interaction efficiently; requires demonstrations and recovery data |
| D6 | One track and fixed assists | Makes comparison reproducible; no generalization claim |
| D7 | Manual resets acceptable | Avoids depending on an unverified automation interface; limits training throughput |
| D8 | Numeric real-world lap target | Matches the user's chosen benchmark; must be labeled a cross-simulator comparison |
| D9 | Laptop game/controller/inference + remote Spark training | Internet access is confirmed; optional remote inference requires measured deadlines; full local graph remains unverified |

These are draft decisions to revise as the team confirms constraints. The scientific premise does not require a claim that fly wiring outperforms ordinary neural networks. A convincing result can be a working fly-derived controller plus an honest comparison showing where it helps, fails, or remains inconclusive.
