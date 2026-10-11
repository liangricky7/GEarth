# Boss Fight AI: Spark compute and evidence plan

The player fights on the Windows laptop. Two DGX Sparks continually produce, review, and improve executable tactics. The demonstration must show both useful GPU work and improvements attributable to the brain and coach.

## 1. Define the utilization goal honestly

Target near-continuous GPU activity during sustained workloads, with a backlog of useful jobs. Do not promise 100% of theoretical FLOPs, equate memory reservation with compute, or enlarge models solely to fill RAM. High GPU activity does not establish high Tensor Core efficiency.

Record GPU activity, memory, power, inference throughput, request latency, queue depth, completed analyses, and policies tested. Choose concurrency from measurements. Start at one request, then benchmark 2, 4, 8, and 16; stop increasing it when throughput stops improving or latency becomes unacceptable.

The same GPU cannot be assumed to provide both maximum background throughput and guaranteed urgent-response latency. The recommended design removes remote inference from the immediate controller path. Both Sparks can remain busy while the laptop executes the latest validated playbook. Remote live commands remain an optional mode for comparison.

## 2. Assign substantial work to both Sparks

| Machine | Main workload | Work while the game is running | Work between attempts |
| --- | --- | --- | --- |
| Laptop | Cuphead, NitroGen, state capture, arbiter, local skills | Execute a frozen playbook; collect observations and action provenance | Retry, load the next approved configuration, run evaluation attempts |
| Spark A: brain factory | Batched tactical planning and visual analysis | Analyze sampled observations and replay clips; generate competing phase plans and conditional tactics | Prepare the next playbook and score previous predictions against observed attacks |
| Spark B: coaching lab | Batched failure diagnosis, candidate evaluation, optional training | Review hits and missed opportunities as clips arrive; critique candidate changes; label evidence | Select a candidate for an actual game test; train and validate a small skill selector when data supports it |

Starting model candidates are Qwen3-8B for short tactical requests, Qwen2.5-VL-7B-Instruct for replay interpretation, and Qwen3-32B for detailed coaching. Benchmark serving support and throughput on the actual devices before selecting quantization and concurrency. If internal state already identifies attacks reliably, use visual analysis for failure investigation rather than duplicating that information every frame.

Use a small number of resident checkpoints. Defense, offense, head coach, and critic can be separate structured requests to the same reasoning model. Keep evidence and objectives distinct; agreement among role prompts is not independent proof that a tactic works.

On each Spark, schedule memory-intensive inference and training in blocks rather than assuming they will coexist without contention. Keep the laptop hub architecture. Independent models do not need cross-Spark tensor parallelism. If a future model requires sharding, use the supported high-speed Spark interconnect; laptop-to-Spark Tailscale is for application messages.

## 3. Create enough useful work

Build a replay corpus before relying on live attempts: record human play and baseline attempts, including failures, successful dodges, phases, and offensive opportunities. Sampling adjacent frames does not make independent training examples. Split data by complete attempt or session.

Every hit produces a clip with observations before and after it, timestamps, player state, attack history, NitroGen's proposed inputs, the arbiter's executed inputs, and the active policy version. Also capture successful responses to the same attack so the coach can compare success and failure.

Give the job queue a bounded set of worthwhile tasks: identify an attack, explain a hit using recorded evidence, find a counterexample to a proposed tactic, generate a candidate change, and evaluate prediction accuracy. Avoid repeatedly rephrasing an already-reviewed clip just to keep the GPU busy.

Once the replay backlog is exhausted, more useful compute requires more data or experiments. If additional Windows machines are available, run independent game workers testing different policies. Benchmark resources and isolation before trying multiple instances on one laptop. The Sparks run DGX OS/Linux; do not assume they can host the existing Windows Cuphead integration.

A single game instance limits the rate of real experimental evidence. Batched LLM analysis can keep devices active, but cannot replace additional game trials.

## 4. Make the brain control a real mechanism

The brain emits a conditional playbook using a fixed skill vocabulary: reposition, jump, dash, maintain firing, and retreat. Each rule contains an observable trigger, applicable phase, bounded parameters, priority, expiration, and supporting evidence. The laptop matches these rules against current state and executes the corresponding skill through the arbiter.

Example hypothesis: during a particular attack, move toward the center before the danger arrives. The deployed rule must identify that attack from available observations and actually change controller behavior. It must not depend on inaccessible future state.

Keep NitroGen weights and the primitive skill implementations fixed for the first experiment. Brain-off uses the same controller primitives and emergency rules with a fixed default selection policy. Brain-on uses the generated playbook. This isolates the value of tactical selection from the value of adding scripted skills.

If testing remote commands during play, give every result an observation timestamp, attempt ID, phase ID, and expiration. Ignore stale results. Never wait for a Spark to decide an immediate dodge.

## 5. Turn coaching into controlled updates

The defense coach proposes timing and positioning changes. The offense coach proposes improvements to firing opportunities and damage. The critic identifies situations where those changes could fail. The head coach chooses one bounded change for the next experiment and records its predicted effect.

Example, with illustrative values rather than Cuphead constants:

> Evidence: the player was hit during three encounters with attack X. Hypothesis: jump initiation was late. Candidate: increase warning lead time from 120 ms to 180 ms. Predicted effect: fewer hits per encounter with X, without reducing overall boss damage.

Apply changes between attempts. Save the initial configuration, every candidate, every test result, and the best validated version. Reject malformed output and out-of-range settings. Do not silently edit the scripts by hand during the measurement phase.

Test candidates in the real game. A clip shows what happened under the original inputs; it does not reveal the outcome under a different action. Approximate simulations may filter candidates, but actual game trials determine promotion.

## 6. Add learning when enough evidence exists

As an extension, Spark B trains a small model to select among the fixed skills using recent state and attack history. Labels come from observed, validated successful interventions, not merely the coach's preferences. Train on earlier attempts; validate on held-out attempts or sessions.

Deploy only if the selector improves held-out performance against a simple fixed or heuristic selector. Keep inference local on the laptop. This creates a durable benefit from Spark analysis: lessons become fast decisions that survive network interruptions.

Treat apprentice training as a separate experimental factor. Do not add it halfway through the brain/coach comparison and attribute all improvement to the LLMs. NitroGen fine-tuning is a later project requiring a verified training pipeline and sufficient demonstrations.

## 7. Prove the brain and coach separately

First collect a limited adaptation budget, for example ten attempts, from which the coach may learn. Select the final learned artifact using development results. Then freeze both the initial and learned configurations for evaluation on fresh attempts.

Run this two-by-two comparison:

| Condition | Brain | Coach artifact | What it establishes |
| --- | --- | --- | --- |
| A | Off | Initial configuration | Baseline with the same primitives and emergency rules |
| B | On | Initial configuration | B versus A tests the brain |
| C | Off | Learned configuration | C versus A tests coach changes usable without the brain |
| D | On | Learned configuration | D versus B tests coaching with the brain; D versus C tests the brain after coaching |

For brain-off, ignore any learned language instructions while retaining applicable coach-adjusted reflex parameters. If the coach only changes brain instructions, omit C and explain that coaching is evaluated conditional on the brain rather than as an independent factor.

Hold boss, difficulty, loadout, starting state, observation access, NitroGen checkpoint, scripted primitives, and emergency rules constant. Use matched seeds only if complete reset reproducibility has been verified; a restored save does not guarantee the same random behavior. Otherwise randomize or interleave evaluation conditions to reduce ordering effects.

Start with ten evaluation attempts per condition for an exploratory demo. Report every result, sample count, and spread; ten runs do not guarantee reliable significance. Repeat independent adaptation sessions from empty coach memory if time permits. Reserve evaluation attempts from policy selection and training.

Primary outcome: boss damage or progress per attempt, plus win rate. Secondary outcomes: survival time, phases reached, hits per encounter with the targeted attack, and time spent firing. A longer survival time alone can reward an agent that avoids fighting.

Prediction claims require separate evidence: timestamp each forecast before the attack, evaluate accuracy and lead time, and show whether the associated repositioning executed. Forecast accuracy alone does not prove useful control.

## 8. Make the demo show cause and effect

Show a failure clip, the exact proposed configuration change, and a later encounter where the player executes that change. Present this example beside aggregate results; do not treat the selected clip as statistical proof.

The overlay displays the active condition, playbook/configuration version, actual controller owner, triggered rule, coach hypothesis, and test outcome. The utilization panel separately displays each Spark's GPU activity, memory, useful jobs completed, and queue depth.

Use a learning plot of raw attempt progress with policy-change annotations, and a separate evaluation plot comparing A, B, C, and D. Do not substitute a best-so-far curve for average improvement.

## 9. Build order

1. Verify NitroGen gameplay timing and capture executed actions. NitroGen's published benchmark pauses the game during inference; continuous real-time performance must be measured separately.
2. Implement fixed skills, brain playbooks, and the arbiter. Record a baseline corpus.
3. Add one defense-coach parameter update and real-game candidate tests.
4. Run the frozen brain/coach comparison before adding more complexity.
5. Add replay queues and batching to fill idle periods, then offense review and visual analysis where useful.
6. Add apprentice training and additional Windows game workers if data, hardware, and time allow.

## Sources

- [DGX Spark hardware and peak-performance definitions](https://docs.nvidia.com/dgx/dgx-spark/hardware.html)
- [NVIDIA vLLM serving and batching playbook](https://build.nvidia.com/spark/vllm)
- [NitroGen paper, including synchronous evaluation](https://arxiv.org/html/2601.02427v1)
- [Qwen3-8B](https://huggingface.co/Qwen/Qwen3-8B)
- [Qwen3-32B](https://huggingface.co/Qwen/Qwen3-32B)
- [Qwen2.5-VL-7B-Instruct](https://huggingface.co/Qwen/Qwen2.5-VL-7B-Instruct)
