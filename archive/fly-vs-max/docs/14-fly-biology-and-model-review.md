# Understanding the fly brain and our model

Research review: October 5, 2026. This is a beginner's explanation and a review of our design assumptions. Biology claims below link to primary research or the researchers' own descriptions. Suggested engineering changes are our proposals, not experimental results. No implementation or training was performed.

## Four things that are easy to conflate

| Term | Meaning | What it gives this project |
| --- | --- | --- |
| Connectome | A reconstruction of neurons and their connections | A candidate architecture and relative connection-count information |
| Neural simulation | Equations for how activity changes over time | A way to turn that architecture into a running system |
| Controller | A mapping from observations to actions | A way for that system to operate a car |
| Learning algorithm | A procedure for changing model parameters using experience | Demonstration fitting and RL improvement |

MaleCNS supplies wiring and annotations for a male fly's brain and ventral nerve cord. It does not provide a pretrained racing policy, the animal's live activity state, or a fully specified learning algorithm. The official download separates connectivity, neuron annotations, and predicted transmitter information. [MaleCNS project](https://www.janelia.org/project-team/flyem/male-cns-connectome), [dataset documentation](https://male-cns.janelia.org/download/)

## How signals become behavior

Neurons receive influences from other cells and affect downstream cells through connections called synapses. Excitatory and inhibitory effects influence whether activity increases or decreases; receptor properties and physiological context matter. Synapse counts are useful structural measurements, but using them directly as functional weights is a modeling assumption. The FlyWire network analysis also notes transmitter-prediction errors, co-transmission, and missing electrical/peptide pathways. These are general cautions; FlyWire's counts and labels must not be substituted for MaleCNS. [Network analysis and limitations](https://www.nature.com/articles/s41586-024-07968-y)

Some neurons communicate using spikes; others, including fly photoreceptors, use graded signals. There is no requirement that every useful computational model use the same neuron equation. Our single signed tanh state deliberately compresses substantial cellular detail. [Photoreceptor physiology](https://www.nature.com/articles/nature01384)

The nervous system contains specialized, interconnected circuits:

| Circuit/system | Plain-language role | Relationship to racing |
| --- | --- | --- |
| Optic lobes | Process visual input | Potential inspiration for future image/motion processing; our telemetry version does not perform this task |
| Central complex | Represents navigation-related quantities and contributes to steering | A research lead for heading/goal interfaces, not an automatic car controller |
| Mushroom bodies | Support associative learning and memory-based choices | A biological learning reference; not currently the site of learning in our software |
| Descending neurons | Carry signals from brain circuits toward motor systems | Possible future output populations, after matching annotations and validating a mapping |
| Ventral nerve cord | Contains circuits for coordinating body movement | Included in MaleCNS; the game supplies a car instead of the fly's body |

These descriptions summarize the researchers' work on [visual processing](https://www.janelia.org/node/69305), [navigation](https://www.janelia.org/lab/jayaraman-lab/research), [mushroom bodies](https://www.janelia.org/lab/rubin-lab/our-research/anatomical-and-behavioral-analyses-brain-areas/learning-and-memory), and [descending pathways](https://www.janelia.org/project-team/fly-descending-interneuron). They are not independent modules with one neuron per behavior; signals interact across circuits.

## Why the navigation research is relevant

In a studied fly navigation circuit, EPG neurons represent heading, FC2 activity relates to a goal direction, and PFL3 neurons combine those signals into steering-related output. Researchers tested parts of this account with neural recording and experimental activation. [Mussells Pires et al., 2024](https://www.nature.com/articles/s41586-023-07006-3)

A complementary study investigated how PFL populations contribute to directional corrections and steering strength. This is evidence that a fly can control its movement relative to a goal, including correcting deviations. It is not evidence of racing competence or transfer to car physics. [Westeinde et al., 2024](https://www.nature.com/articles/s41586-024-07039-2)

Our inference: fixed-route driving is a sensible control experiment, but the stronger analogy is repeated feedback correction, not executing a predetermined timeline. Our telemetry-derived heading errors and lookahead route already perform part of the state estimation for the controller. A future anatomy-informed variant would need to map features into verified MaleCNS populations and compare its behavior with the initial artificial mapping.

## How biological learning differs from our learning

Work on mushroom bodies links sensory representations, output neurons, and compartment-specific dopamine signals to changes in connections that influence later choices. Aso's group describes plasticity at active Kenyon-cell-to-output-neuron synapses, with learning rules depending on the circuit compartment. This is more specific than assigning a global numerical reward to a whole wiring diagram. [Aso lab research](https://www.janelia.org/lab/aso-lab/our-research)

In our software, demonstrations and PPO change the action head; the graph initially stays fixed. The value head estimates future reward for training. Neither is a reconstruction of the fly's dopamine system. Recurrent activity changing during a lap is temporary state; lasting adaptation lives in saved learned parameters.

This division is an engineering choice. It keeps the first training problem small. A later experiment could learn selected input gains, per-population dynamics, or connection multipliers while retaining anatomy, but would require retraining/replay changes and a clear declaration of what is allowed to change.

## What existing models actually establish

- **Shiu et al., Nature 2024:** a simplified spiking model used FlyWire connectivity and transmitter predictions to study taste/feeding and grooming circuits, with experimentally tested predictions. The paper explicitly discusses missing modulation, non-spiking cells, baseline activity, and other limitations. This supports the value of constrained models for particular tasks; it does not establish universal behavior from wiring alone. [Open paper](https://pmc.ncbi.nlm.nih.gov/articles/PMC11446845/)
- **Lappalainen et al., Nature 2024:** a visual-circuit model combined anatomical constraints with learned neuron/synapse parameters for motion estimation. The modeled responses agreed with a range of biological measurements. This is a useful precedent for combining wiring with task optimization, although its dataset and objective differ from ours. [Paper](https://www.nature.com/articles/s41586-024-07939-3)
- **Morra and Daley, 2022 preprint:** a fly-derived fixed reservoir with a learned readout was tested on time-series prediction against conventional reservoirs. It is a closer architectural precedent for our first version than a full spiking emulation. Its reported results concern another connectome and task. [Preprint](https://arxiv.org/abs/2201.09359)
- **FlyGM, 2026 preprint:** authors report RL control of a simulated fly using a connectome-based architecture and comparisons with other controllers. This motivates investigation; it is not a racing checkpoint or a result for our exact frozen-graph model. [Preprint](https://arxiv.org/abs/2602.17997)

The initial model is best described as a **connectome-derived recurrent reservoir with a learned control head**. Here “reservoir” means that the fixed recurrent network transforms current/recent input into activity patterns from which we learn outputs. The term describes the architecture, not evidence that its features will be useful.

## How to interpret impressive demonstration videos

Ask what the sensory encoder supplies, what actually learns, how neural output reaches the controls, and whether a separate controller handles most movement.

For example, Eon's own March 2026 account says its virtual fly combines existing brain/body models, hand-chosen interfaces, and learned low-level body controllers; plasticity and learning are largely absent from that demonstration. This is useful engineering context, not an audit of the user's unverified Beat Saber clip. [Creator's technical account](https://eon.systems/updates/embodied-brain-emulation)

Our game also supplies substantial mechanics and any declared assists. That is acceptable for this project; the demonstration should identify what the learned policy controls and what the simulator handles.

## Design changes from this review

1. **Name the model precisely.** Keep MaleCNS provenance separate from our dynamics, input mapping, and training algorithm. Do not call the engineered reservoir a validated biological emulation or a pretrained fly brain.
2. **Measure useful memory.** The contraction setting helps numerical stability but limits retention of old state. Add a short lag-recovery diagnostic; route-position features, not hidden state alone, identify where the car is in the lap.
3. **Keep mapping claims honest.** The initial hashed input/output selection is a reproducible artificial interface. It does not reproduce the native visual-to-steering pathway. Treat anatomy-informed mapping as a separate experiment rather than relabeling the existing one.
4. **Separate structural reachability from effective contribution.** A graph path may carry negligible signal or respond too late. Record perturbation magnitude and response latency at sampled outputs as well as graph coverage.
5. **Require the right control for the right claim.** Disabling graph propagation is a dependency check. Comparing a retrained matched randomized graph is required before claiming an advantage of this particular wiring. If that comparison is omitted, report driving performance without a topology-superiority claim.
6. **Audit sign assumptions.** Keep uncertain transmitter effects explicitly marked. Add a bounded sensitivity check for the fallback sign rule instead of treating it as known biology.
7. **Keep human/RL learning outside the biological claims.** Our hybrid training strategy remains intact, but PPO reward and a biological dopamine signal are not interchangeable explanations.

## Reading order for the team

Start with the brain-region explanation here and the MaleCNS project page. Then read the abstracts and figures of the heading-to-steering paper and the Aso lab overview. Read the assumptions/limitations of the Shiu model before copying its ideas. For implementation inspiration, compare the reservoir preprint and the task-optimized visual model; neither is a drop-in implementation for this project.

The practical question to test remains: under a transparent input/output mapping and fair comparisons, can this particular fly-derived architecture learn useful racing control within our hardware and time budget?
