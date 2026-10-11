# Training options and decision criteria

October 9, 2026. Planning/research only. The user reopened the training decision to compare approaches. No method is newly selected. Document 13 is the earlier hybrid proposal; document 17 preserves an unqualified from-scratch PPO candidate. Neither establishes feasibility or takes precedence until selection. Do not implement all these alternatives during the event.

## What is being chosen

There is no guaranteed method for the combined target: our engineered MaleCNS reservoir, an F1 car, one Windows simulator instance, remote Spark, and a weekend. Published results support methods in their tested settings, not this exact system.

Separate three decisions: (1) where experience comes from, (2) which learning algorithm uses it, and (3) whether the fly controller supplies all driving commands or assists another controller. A curriculum changes task difficulty and can accompany several methods; it is not an alternative algorithm to PPO/SAC.

| Approach | How it works | Advantages | Costs and failure modes | Project fit (engineering judgment) |
| --- | --- | --- | --- | --- |
| Behavior cloning | Fit the fly readout to recorded state/action examples | Offline training; fastest route to a first policy if compatible data exists; simple diagnostics | Data alignment/coverage; errors can compound outside recorded states; pure imitation does not optimize beyond teacher performance | Strong bootstrap candidate if F1 data passes qualification |
| Demonstrations then RL | First imitate, then improve using reward-based practice | Avoids discovering basic controls randomly; allows autonomous improvement | Both pipelines must work; RL can regress; exceeding teacher performance is possible, not guaranteed | Best balance for a functioning weekend lap if data is usable |
| RL from scratch | Random actor explores and learns from rewards | No expert data; clear learning-from-zero experiment | High interaction demand; reward exploits; exploration may never discover useful driving in time | Worth studying, uncertain weekend completion |
| Curriculum RL from scratch | Same learner starts with short easier tasks, then longer/faster ones | More opportunities to discover success without driving demonstrations | Requires feasible resets/tasks; staged success may fail to transfer; throttle caps do not guarantee low speed | Preferred no-demonstration option |
| Interactive imitation | Run the learner, obtain expert actions at encountered states, refit | Targets the actual failure distribution rather than collecting endless clean laps | Expert availability, aligned labels, repeated collection; simple takeover is not identical to full DAgger labeling | Useful when an accessible human or programmatic teacher exists |
| Programmatic teacher then imitation | Use built-in AI or a conventional path/speed controller to generate examples | Avoids requiring the team to drive well; repeatable collection | Must verify useful action access, same controllable physics, mapping and teacher competence; a custom teacher is extra engineering | Promising middle ground, not from scratch without demonstrations |
| Residual RL | Conventional controller drives; fly learns a bounded correction | A qualified base can give immediate reasonable behavior and reduce exploration burden | Still requires a good base; corrections can destabilize it; cannot claim the fly independently learned all driving | Consider only if assisted-controller scope is acceptable |
| Evolution strategies | Perturb controller parameters, evaluate candidates, retain better ones | Can optimize a non-differentiable graph/controller without critic training | Many whole-rollout evaluations; simulator parallelism matters; little benefit from a large GPU with one game | Low priority for this weekend |
| Dopamine-inspired local plasticity | Use a simulated reward signal to change recently active, eligible fly-derived connections | Stronger experiment about learning within a biological circuit; permits a clear signal/no-signal comparison | Requires a specified plasticity rule, credit assignment across delayed turns, extra state/compute, and a carefully bounded subcircuit | Promising research-focused alternative on a short section; higher risk for a fast full lap |

Behavior cloning is not guaranteed to reproduce a teacher successfully. Interactive imitation addresses the distribution shift caused by the learner's own mistakes; do not equate a few interventions with the full algorithm. [DAgger paper](https://proceedings.mlr.press/v15/ross11a.html)

AssettoCorsaGym evaluates RL and demonstration-assisted approaches, providing a directly relevant simulator precedent. Its results do not establish fly-model or F1-car performance. [Research and datasets](https://assetto-corsa-gym.github.io/)

The closest published training procedure is more informative than the labels alone: the AssettoCorsaGym study ran online SAC from new weights over repeated simulator episodes, used three-step returns, ended certain off-track/stuck episodes early, and supplied a reference path for its successful from-scratch racing experiments. Its no-demonstration SAC condition failed to complete a lap when the reference path was removed in the reported test. The paper reports final results after four days of training on RTX 4090 workstations. These are reported conditions, not resource estimates for our fly controller. [Primary research paper](https://proceedings.neurips.cc/paper_files/paper/2024/file/b91ea2ba1b6521940e204b7b81d5ef47-Paper-Datasets_and_Benchmarks_Track.pdf)

A separate creator-built rhythm-game experiment, Fly Hero, contrasts reward-modified simulated synapses with a learned readout on a MaleCNS-derived subgraph. It repeats short songs and compares real, shuffled, and random wiring. Its author explicitly notes that each note is one decision; the result does not establish long-horizon driving or that any viral Beat Saber clip used the same training method. [Fly Hero source and experiment](https://github.com/bsgelman/fly-hero)

In flies, dopamine can modulate plasticity in specific mushroom-body circuits; treating reward as a global positive/negative pulse across every connection is an engineering choice, not a faithful biological simulation. To study this approach, specify which synapses may change, how recent activity receives credit, and what control comparisons will test the mechanism. [Primary mushroom-body plasticity model](https://www.nature.com/articles/s41467-021-22592-4), [reward/punishment study](https://www.nature.com/articles/s41586-024-07819-w)

Residual control combines conventional and learned actions; the cited evidence concerns robot control, not proof of performance in this car. [Original project](https://residualrl.github.io/)

Evolution strategies evaluate parameter perturbations through environmental returns; their demonstrated scaling relies on substantial parallel evaluation. [Original research](https://arxiv.org/abs/1703.03864)

## PPO versus SAC if choosing RL

- PPO uses fresh behavior-policy rollouts for each update, with limited reuse inside that update. It offers a comparatively straightforward collect/update loop, but repeated new collection can be expensive in a real-time game. Clipped updates do not guarantee monotonically improving performance. [Algorithm reference](https://spinningup.openai.com/en/latest/algorithms/ppo.html)
- SAC uses off-policy replay, allowing repeated learning from earlier compatible experience. That makes it a strong candidate when simulator interactions are the bottleneck. It adds twin critics, target networks, replay management, and action/entropy handling. AssettoCorsaGym already includes SAC code, but adapting it to fixed recurrent fly features and remote training remains work. [SAC reference](https://spinningup.openai.com/en/latest/algorithms/sac.html), [racing implementation](https://github.com/dasGringuen/assetto_corsa_gym)
- SAC replay is not permission to mix incompatible car physics, feature versions, task meanings, or action transforms. Preserve ordered history to compute fixed-graph features and correct successors; changing the feature generator invalidates affected cached representations.
- The previous PPO proposal was an implementation choice for fine-tuning an initialized policy, not proof it is best for scratch training. Inspect integration feasibility before selecting either algorithm. Do not transfer PPO likelihood rules, critic configuration, or update schedules into SAC unchanged.

## What the fly learns

For the weekend proposal, keep the connectome-derived graph fixed and train the small action head plus whatever critic/exploration parameters the algorithm requires. This isolates one uncertain component and permits cached graph features. Training graph edges or the input mapping is a separate expansion requiring sequence-aware optimization and new runtime/learning tests. No training method can rescue an input representation that omits essential driving state or produces uninformative neural features.

Teacher data, when used, must pass car/track/version, control-unit, timing, validity, and observation-coverage checks. The new AMR24/Monza repository is a promising source, not yet accepted training data. F3 records are not interchangeable with F1 actions. Learned model/world-model planning is another family, but building an accurate F1 dynamics model adds substantial scope and is not a recommended first weekend implementation.

## Decision gate and small pilot

1. Qualify one car/track's observation/control/stop/reset loop and graph responsiveness, regardless of method.
2. Measure simulator collection throughput, reset time, and update/export time. At 25 Hz and real time, 100,000 decisions alone require 1.11 hours; 1 million require 11.11 hours. These are arithmetic lower bounds, not required sample counts or convergence estimates.
3. Choose one objective: fastest route to autonomous laps, learning visibly from zero, or an assisted-controller demonstration. This is a project preference, not a fact that research can settle.
4. Test one candidate on a short fixed task within a declared elapsed-time/transition budget. Save the untrained checkpoint, curriculum/settings, seeds, all attempts, and observed progress. An easy-section result is not evidence of full-lap competence.
5. Compare candidate and incumbent on identical tasks with exploration disabled; keep learner and evaluated champion separately. Reward curves across changed curricula cannot be compared directly. Two failed early updates are insufficient evidence that scratch RL cannot learn.
6. Diagnose reward/observations/exploration before one targeted adjustment. If still unsuccessful within the budget, narrow the demonstration or revisit method selection explicitly; do not silently introduce expert data into a from-scratch claim.

Current recommendation: demonstration initialization plus RL if the overriding priority is reliable laps and the recordings qualify; curriculum RL, with SAC seriously considered, if no starter driving data is the priority. Neither path guarantees a full lap or beating a published professional time within the event. Keep the final scoring/video window protected after any schedule revision.
