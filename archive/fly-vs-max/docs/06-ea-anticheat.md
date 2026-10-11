# EA anti-cheat assessment

Historical/supporting document. Use [10 — execution roadmap](10-implementation-roadmap.md) and [11–13 — current contracts](11-implementation-contract.md) for build decisions. Earlier schedules, inputs, data quotas, and learning recipes below do not add requirements to that plan.

Planning assessment, October 4, 2026. Windows and Steam are confirmed; the exact EA F1 edition remains unknown. No driver or automated game-control test has been performed.

## Finding

Virtual-controller compatibility and permission to automate driving are separate unresolved questions. Do not promise that the project will work with EA anti-cheat or that it cannot lead to account action. There is no verified official approval for this project's proposed vgamepad/ViGEm agent in the sources reviewed.

EA's anti-cheat team describes blocking reWASD/Disc-Soft virtual hardware and its driver. It also identifies Steam Input and Microsoft's Keyboard Manager as supported remapping examples. This is not evidence that every virtual controller is blocked, or that an autonomous agent routed through a supported remapper is approved. [EA anti-cheat team statement](https://forums.ea.com/discussions/ea-forums-general-discussion-en/ea-javelin-anticheat--recent-software-blocks/12218073)

EA's F1 25 help page acknowledges Javelin-related controller connection issues. Its supported-device list establishes physical-device support, not acceptance of our model-controlled input path. [F1 25 controller guidance](https://help.ea.com/en/articles/f1/f1-25/controllers-wheels-and-vr-support/)

EA documents software/driver denylisting and notes that its published list is incomplete. It also says protected games cannot be played with their required anti-cheat uninstalled. A missing denylist entry is not an approval. [Javelin troubleshooting](https://help.ea.com/en/articles/platforms/pc-ea-anticheat/)

The current agreement addresses cheating, unauthorized third-party programs, and possible account sanctions. It says anti-cheat may activate when launching an online-capable game and that offline data may later be transmitted. Consequently, solo play or disconnecting the internet does not establish an exemption for this experiment. [EA User Agreement, sections 6–9](https://www.ea.com/legal/user-agreement)

## Recommended design decision

Treat EA F1 as conditional until the team has edition-specific guidance on autonomous inputs and a compatible controller path. Ask EA for written clarification before investing heavily in this integration. This is our risk-management recommendation, not a published EA requirement to seek research approval.

Also establish whether the proposed mode publishes lap times or ghosts. The intended experiment should avoid multiplayer and public competitive submissions; do not assume Time Trial is local merely because there are no other cars. If EA cannot support the experiment within the event timeline, evaluate a simulator designed to accept programmatic agents and revise the benchmark accordingly.

Preserve the same abstract observation/action contracts regardless of simulator. Continue graph preparation, offline model design, and evaluation planning independently. Do not make protection bypasses, hidden drivers, or disguising automated controls part of the project architecture. Hardware-generated inputs would not, by themselves, resolve the permitted-use question either.

## Draft inquiry for EA — not sent

> We are a four-person hackathon team planning a noncompetitive research demo using EA F1 [edition and patch] on Windows through Steam. A neural model would read the game's documented UDP telemetry and autonomously produce steering, throttle, and brake through a virtual controller, potentially Python vgamepad/ViGEmBus. We would not modify game memory, inject code, or alter anti-cheat. We want to run solo laps, with no multiplayer or public leaderboard/ghost submissions, and compare local results with a published real-world lap time. Is this use permitted? Is there a supported input interface for it, and which mode/settings would keep results out of public competition? If this needs review by another EA team, could you direct us to them?

Replace the edition placeholder before using this draft. A generic response about physical controllers or Steam Input would not answer the autonomous-use question. Retain any response and its scope with the project decisions.
