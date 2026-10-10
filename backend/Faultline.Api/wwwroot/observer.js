const element = (id) => document.getElementById(id);
let selectedRun = null;
let selectedAgent = null;
let lastSequence = 0;
let displayedEvents = [];
let busy = false;
let agentOffset = 0;
let agentTotal = 0;
let followCurrent = true;
let notesKey = "";

async function getJson(path) {
  const response = await fetch(path);
  if (!response.ok) throw new Error(`Request failed (${response.status}).`);
  return response.json();
}

function textNode(tag, text, className = "") {
  const node = document.createElement(tag);
  node.textContent = text;
  node.className = className;
  return node;
}

function svgNode(tag, attributes, text) {
  const node = document.createElementNS("http://www.w3.org/2000/svg", tag);
  for (const [name, value] of Object.entries(attributes))
    node.setAttribute(name, value);
  if (text !== undefined) node.textContent = text;
  return node;
}

function drawMap(snapshot) {
  const map = element("map");
  map.replaceChildren();
  const point = (location) =>
    snapshot.run.stage === 2
      ? [
          40 + ((location.lon + 180) * 820) / 360,
          30 + ((90 - location.lat) * 410) / 180,
        ]
      : [100 + (location.lon + 80) * 6, 65 + (50 - location.lat) * 4.2];
  const locations = new Map(
    snapshot.locations.map((location) => [location.id, location]),
  );
  const planned = new Set(
    snapshot.shipments
      .filter((cargo) => !cargo.status || cargo.status === "in_transit")
      .flatMap((cargo) => cargo.plan?.edge_ids ?? []),
  );

  for (let x = 40; x < 900; x += 60)
    map.append(
      svgNode("line", {
        x1: x,
        x2: x,
        y1: 0,
        y2: 470,
        stroke: "#203042",
        "stroke-width": 0.5,
      }),
    );
  for (let y = 20; y < 470; y += 50)
    map.append(
      svgNode("line", {
        x1: 0,
        x2: 900,
        y1: y,
        y2: y,
        stroke: "#203042",
        "stroke-width": 0.5,
      }),
    );

  for (const route of snapshot.routes) {
    const [x1, y1] = point(locations.get(route.from));
    const [x2, y2] = point(locations.get(route.to));
    const color = !route.open
      ? "#ff867b"
      : planned.has(route.id)
        ? "#bba0ff"
        : "#65d5be";
    const line = svgNode("line", {
      x1,
      y1,
      x2,
      y2,
      stroke: color,
      "stroke-width": planned.has(route.id) ? 3 : 1.5,
      opacity: route.open ? 0.7 : 1,
    });
    if (!route.open) line.setAttribute("stroke-dasharray", "6 5");
    line.append(
      svgNode(
        "title",
        {},
        `${route.id}: ${route.travel_ticks} ticks / ${route.cost_minor / 100} WLD / ${route.open ? "open" : "closed"}`,
      ),
    );
    map.append(line);
  }

  for (const cargo of snapshot.shipments.filter(
    (item) => item.status === "in_transit",
  )) {
    const location = locations.get(cargo.location_id);
    if (!location) continue;
    let [x, y] = point(location);
    const edge = snapshot.routes.find(
      (route) => route.id === cargo.plan?.edge_ids[cargo.edge_index],
    );
    if (edge && cargo.edge_remaining_ticks > 0) {
      const [toX, toY] = point(locations.get(edge.to));
      const fraction = 1 - cargo.edge_remaining_ticks / edge.travel_ticks;
      x += (toX - x) * fraction;
      y += (toY - y) * fraction;
    }
    const marker = svgNode("circle", {
      cx: x,
      cy: y,
      r: 5,
      fill: "#bba0ff",
      stroke: "#131c27",
      "stroke-width": 2,
    });
    marker.append(
      svgNode("title", {}, `${cargo.id}: ${cargo.quantity} ${cargo.good}`),
    );
    map.append(marker);
  }
  for (const location of snapshot.locations) {
    const [x, y] = point(location);
    map.append(svgNode("circle", { cx: x, cy: y, r: 3, fill: "#98a9bd" }));
  }
  for (const country of snapshot.countries) {
    const [x, y] = point(country);
    map.append(
      svgNode("circle", {
        cx: x,
        cy: y,
        r: 7,
        fill: "#131c27",
        stroke: "#e9eef5",
        "stroke-width": 2,
      }),
    );
    map.append(
      svgNode("text", { x: x + 12, y: y - 12 }, country.name.toUpperCase()),
    );
  }
}

function showEvents(snapshot) {
  const list = element("events");
  list.replaceChildren();
  element("event-count").textContent = `${snapshot.seq} events`;
  const important = displayedEvents.filter(
    (event) =>
      event.event_type !== "economy_cycle" &&
      !(
        event.event_type === "action_accepted" &&
        event.entity_ids[0]?.includes("_citizen_")
      ),
  );
  for (const event of important.slice(-35).reverse()) {
    const item = document.createElement("li");
    item.append(
      textNode(
        "div",
        `T${event.tick} · #${event.seq} · ${event.source}`,
        "event-meta",
      ),
    );
    const action = event.payload.action;
    const title = action
      ? `${event.entity_ids[0]} → ${action.action_type}`
      : event.event_type.replaceAll("_", " ");
    item.append(
      textNode(
        "div",
        title,
        event.event_type === "action_rejected"
          ? "event-title event-rejected"
          : "event-title",
      ),
    );
    const detail =
      action?.params.text ??
      action?.reason ??
      (event.event_type === "border_changed"
        ? `${event.payload.edge_id}: ${event.payload.open ? "open" : "closed"}`
        : (event.payload.reason ?? ""));
    item.append(
      textNode(
        "p",
        `${event.payload.code && event.payload.code !== "OK" ? event.payload.code + ": " : ""}${detail}`,
        "event-body",
      ),
    );
    list.append(item);
  }
}

function showAgents(agents) {
  const list = element("agents");
  list.replaceChildren();
  for (const agent of agents) {
    const button = textNode("button", agent.id, "agent-button");
    button.type = "button";
    if (agent.id === selectedAgent) button.classList.add("selected");
    button.append(
      textNode(
        "span",
        `${agent.role} · ${(agent.cash_minor / 100).toFixed(2)} WLD · ${agent.model_decisions} model turns`,
      ),
    );
    button.addEventListener("click", async () => {
      selectedAgent = agent.id;
      showAgents(agents);
      await showProfile();
    });
    list.append(button);
  }
}

async function showProfile() {
  if (!selectedAgent) return;
  try {
    const data = await getJson(
      `/api/runs/${encodeURIComponent(selectedRun)}/agents/${encodeURIComponent(selectedAgent)}`,
    );
    const profile = element("profile");
    profile.replaceChildren(
      textNode("h3", data.agent.id),
      textNode("p", data.agent.goal),
    );
    profile.append(
      textNode(
        "p",
        `Food: ${data.agent.inventory.food} · Energy: ${data.agent.inventory.energy} · ${(data.agent.cash_minor / 100).toFixed(2)} WLD`,
      ),
    );
    if (data.agent.last_model_decision_at) {
      const age = Math.max(
        0,
        (Date.now() - new Date(data.agent.last_model_decision_at)) / 1000,
      );
      profile.append(
        textNode(
          "p",
          `Accepted model turns: ${data.agent.model_decisions} · last turn ${Math.round(age)}s ago`,
        ),
      );
    }
    const traits = document.createElement("div");
    traits.className = "traits";
    for (const [name, value] of Object.entries(data.agent.traits)) {
      const trait = textNode("div", `${name.replaceAll("_", " ")} · ${value}`);
      const progress = document.createElement("progress");
      progress.max = 1;
      progress.value = value;
      progress.setAttribute("aria-label", name);
      trait.append(progress);
      traits.append(trait);
    }
    profile.append(
      traits,
      textNode("p", "Recent memories · observer access", "muted"),
    );
    const memories = document.createElement("ul");
    for (const memory of data.agent.memories)
      memories.append(textNode("li", memory));
    profile.append(memories);
    // The recent-decisions window keeps full observations; the agent's own copy is slim.
    const decision = data.decisions.at(-1) ?? data.last_decision;
    const observed = decision && Object.keys(decision.job.observation ?? {}).length > 0;
    if (decision) {
      profile.append(
        textNode("h3", "Last decision"),
        textNode(
          "p",
          `${decision.result.source} · ${decision.code} · observation T${decision.job.observation_tick}`,
        ),
        textNode(
          "p",
          `${decision.result.action.action_type}: ${decision.result.action.reason}`,
        ),
      );
      const details = document.createElement("details");
      details.append(
        textNode("summary", "Inspect observation and proposed action"),
        textNode(
          "pre",
          JSON.stringify(
            {
              observation: observed
                ? decision.job.observation
                : "Not kept: observations are stored for the 256 most recent decisions.",
              action: decision.result.action,
            },
            null,
            2,
          ),
        ),
      );
      profile.append(details);
    }
  } catch (error) {
    element("message").textContent = error.message;
  }
}

async function refresh() {
  if (busy) return;
  busy = true;
  try {
    const health = await getJson("/api/health");
    const runs = await getJson("/api/runs");
    if (
      followCurrent &&
      runs.current_run_id &&
      selectedRun !== runs.current_run_id
    ) {
      selectedRun = runs.current_run_id;
      lastSequence = 0;
      displayedEvents = [];
      selectedAgent = null;
      agentOffset = 0;
    }
    selectedRun ??= runs.current_run_id ?? runs.run_ids[0];
    element("runs").replaceChildren();
    for (const id of runs.run_ids) {
      const option = textNode("option", id);
      option.value = id;
      element("runs").append(option);
    }
    element("runs").value = selectedRun ?? "";
    if (!selectedRun) {
      element("connection").textContent = "Initializing world";
      return;
    }

    const snapshot = await getJson(
      `/api/runs/${encodeURIComponent(selectedRun)}/snapshot`,
    );
    if (lastSequence === 0 && snapshot.run.stage === 2)
      lastSequence = Math.max(0, snapshot.seq - 200);
    let events;
    do {
      events = await getJson(
        `/api/runs/${encodeURIComponent(selectedRun)}/events?after_seq=${lastSequence}&limit=500`,
      );
      displayedEvents.push(...events);
      if (events.length) lastSequence = events.at(-1).seq;
    } while (events.length === 500);

    displayedEvents = displayedEvents.slice(-500);
    element("tick").textContent = snapshot.tick;
    element("population").textContent = snapshot.agents_summary.spawned;
    element("coverage").textContent =
      `${snapshot.agents_summary.unique_model_deciding}/${snapshot.agents_summary.spawned}`;
    element("decisions").textContent = snapshot.metrics.model_decision_count;
    element("failures").textContent =
      `${snapshot.metrics.fallback_decision_count} / ${snapshot.metrics.rejected_decision_count}`;
    element("mode").textContent =
      `${snapshot.run.mode} · ${snapshot.run.status.toUpperCase()}`;
    element("connection").textContent =
      health.status === "paused" ? "Paused" : `Connected · ${health.storage}`;
    element("updated").textContent =
      `Last commit ${new Date(snapshot.updated_at).toLocaleTimeString()}`;
    const plan = snapshot.shipments[0]?.plan;
    element("route-plan").textContent = plan
      ? `${plan.objective} plan · ${plan.travel_ticks} travel ticks · ${(plan.cost_minor / 100).toFixed(2)} WLD`
      : "No active medicine route plan.";
    element("message").textContent = health.error ?? "";
    drawMap(snapshot);
    showEvents(snapshot);
    showWorld(snapshot);
    await showRunNotes(snapshot, health);
    await refreshAgents(snapshot);
    await showProfile();
  } catch (error) {
    element("connection").textContent = "Connection interrupted";
    element("message").textContent = error.message;
  } finally {
    busy = false;
  }
}

element("runs").addEventListener("change", () => {
  selectedRun = element("runs").value;
  followCurrent = false;
  agentOffset = 0;
  selectedAgent = null;
  lastSequence = 0;
  displayedEvents = [];
  element("profile").replaceChildren(
    textNode("p", "Select an agent to inspect its profile.", "muted"),
  );
  refresh();
});

for (const id of ["country-filter", "role-filter", "agent-search"]) {
  element(id).addEventListener(
    id === "agent-search" ? "input" : "change",
    () => {
      agentOffset = 0;
      refresh();
    },
  );
}
element("previous-page").addEventListener("click", () => {
  agentOffset = Math.max(0, agentOffset - 50);
  refresh();
});
element("next-page").addEventListener("click", () => {
  if (agentOffset + 50 < agentTotal) agentOffset += 50;
  refresh();
});

refresh();
setInterval(refresh, 1500);

async function refreshAgents(snapshot) {
  const countrySelect = element("country-filter");
  if (countrySelect.options.length !== snapshot.countries.length + 1) {
    countrySelect.replaceChildren(textNode("option", "All countries"));
    countrySelect.firstChild.value = "";
    for (const country of snapshot.countries) {
      const option = textNode("option", country.name);
      option.value = country.id;
      countrySelect.append(option);
    }
  }
  const query = new URLSearchParams({
    country: countrySelect.value,
    role: element("role-filter").value,
    q: element("agent-search").value,
    offset: agentOffset,
    limit: 50,
  });
  const page = await getJson(
    `/api/runs/${encodeURIComponent(selectedRun)}/agents?${query}`,
  );
  agentTotal = page.total;
  element("agent-page").textContent =
    `${page.total ? page.offset + 1 : 0}–${Math.min(page.offset + page.agents.length, page.total)} of ${page.total}`;
  element("previous-page").disabled = agentOffset === 0;
  element("next-page").disabled = agentOffset + 50 >= agentTotal;
  showAgents(page.agents);
}

function showWorld(snapshot) {
  const enabled = snapshot.run.stage === 2;
  element("world-details").hidden = !enabled;
  if (!enabled) {
    element("compute").textContent =
      "Stage 1 recorded run · route planning prototype";
    return;
  }
  const compute = snapshot.compute[0];
  element("compute").textContent =
    `One Spark · ${compute.pending_decisions}/${compute.worker_capacity} pending requests · ${compute.due_agents} due · ${compute.average_model_decisions_per_second.toFixed(2)} accepted decisions/s average · p95 ${(snapshot.metrics.latency_p95_ms / 1000).toFixed(1)}s · institutions decide weekly, citizens every ${compute.citizen_rotation_weeks} weeks`;
  element("route-plan").textContent =
    `${snapshot.shipments.filter((cargo) => cargo.status === "in_transit").length} shipments in transit · ${snapshot.metrics.delivered_shipments} delivered · ${snapshot.metrics.failed_shipments} failed`;
  const nations = element("nations");
  nations.replaceChildren();
  for (const nation of snapshot.nations) {
    const card = document.createElement("article");
    card.className = "nation-card";
    const government = snapshot.government_profiles.find(
      (profile) => profile.id === nation.government_id,
    );
    const metric = nation.history.at(-1);
    card.append(
      textNode("h3", nation.name),
      textNode("p", government.name, "government-name"),
      textNode(
        "p",
        `${nation.population} agents · leadership term ${nation.leadership_term}`,
      ),
      textNode(
        "strong",
        `${Math.round((metric?.needs_met ?? 1) * 100)}% needs met`,
      ),
      textNode(
        "p",
        `Support ${Math.round(nation.approval * 100)}% · cash Gini ${nation.gini_cash.toFixed(2)}`,
      ),
      textNode(
        "p",
        `Treasury ${((metric?.treasury_minor ?? 0) / 100).toLocaleString()} WLD · tax ${nation.tax_basis_points / 100}%`,
      ),
      textNode(
        "p",
        `Welfare ${(nation.welfare_minor / 100).toFixed(2)} WLD/resident/cycle`,
      ),
      textNode(
        "p",
        `Food ${nation.stocks.food ?? 0} · Energy ${nation.stocks.energy ?? 0}`,
      ),
      textNode(
        "p",
        `Trade ${(nation.trade_value_minor / 100).toLocaleString()} WLD · ${nation.unmet_needs_last_cycle} unmet needs this cycle`,
      ),
    );
    const chart = svgNode("svg", {
      viewBox: "0 0 200 40",
      role: "img",
      "aria-label": `${nation.name} needs met history`,
    });
    chart.append(
      svgNode("polyline", {
        fill: "none",
        stroke: "#65d5be",
        "stroke-width": 2,
        points: nation.history
          .map(
            (value, index) =>
              `${(index * 200) / Math.max(1, nation.history.length - 1)},${38 - value.needs_met * 36}`,
          )
          .join(" "),
      }),
    );
    card.append(chart, textNode("span", "Needs met over time", "muted"));
    const policy = snapshot.policies.find(
      (item) => item.country_id === nation.id,
    );
    if (policy)
      card.append(
        textNode(
          "p",
          `${policy.action_type} pending until T${policy.effective_tick}`,
          "policy",
        ),
      );
    nations.append(card);
  }
  element("shipping-count").textContent =
    `${snapshot.metrics.delivered_shipments} settled`;
  const shipments = element("shipments");
  shipments.replaceChildren();
  const cargoList = snapshot.shipments
    .slice()
    .sort(
      (a, b) =>
        (b.status === "in_transit") - (a.status === "in_transit") ||
        b.created_tick - a.created_tick,
    )
    .slice(0, 12);
  for (const cargo of cargoList) {
    shipments.append(
      textNode(
        "p",
        `${cargo.seller_id} → ${cargo.buyer_id} · ${cargo.quantity} ${cargo.good} · ${cargo.status.replaceAll("_", " ")} · ${(cargo.escrow_minor / 100).toFixed(2)} WLD escrow`,
      ),
    );
  }
  if (!cargoList.length)
    shipments.append(textNode("p", "No cross-border shipments yet.", "muted"));
  const relations = element("relations");
  relations.replaceChildren();
  const visible = snapshot.relationships
    .filter((item) => item.at_war || item.trade_agreement || item.proposed_by)
    .sort((a, b) => Number(b.at_war) - Number(a.at_war));
  for (const relation of visible)
    relations.append(
      textNode(
        "p",
        `${relation.first} ↔ ${relation.second} · ${relation.at_war ? "conflict" : relation.trade_agreement ? "trade agreement" : "offer pending"} · tension ${relation.tension}${relation.proposal_kind ? ` · ${relation.proposal_kind} offer by ${relation.proposed_by}` : ""}`,
        relation.at_war ? "event-rejected" : "",
      ),
    );
  if (!visible.length)
    relations.append(
      textNode("p", "No active agreements or conflicts.", "muted"),
    );
  for (const loan of snapshot.loans.slice(-8).reverse())
    relations.append(
      textNode(
        "p",
        `${loan.bank_id} → ${loan.borrower_id} · ${(loan.principal_minor / 100).toFixed(2)} WLD · ${loan.status} · due T${loan.due_tick}`,
      ),
    );
}

async function showRunNotes(snapshot, health) {
  element("notes-download").href =
    `/api/runs/${encodeURIComponent(selectedRun)}/notes.md`;
  const status =
    health.run_id === selectedRun && health.status === "paused"
      ? "paused"
      : snapshot.run.status;
  const key = `${selectedRun}:${snapshot.seq}:${status}`;
  if (status === "running") {
    element("notes-status").textContent =
      "Simulation in progress. Notes are saved at completion or stop; download a checkpoint report at any time.";
    element("run-notes").replaceChildren();
    notesKey = "";
    return;
  }
  if (notesKey === key) return;
  const report = await getJson(
    `/api/runs/${encodeURIComponent(selectedRun)}/notes`,
  );
  element("notes-status").textContent =
    `${report.status.toUpperCase()} · notes through T${report.tick} / event ${report.through_sequence} · ${report.history_complete ? "complete recorded history" : "partial history"}`;
  const list = element("run-notes");
  list.replaceChildren();
  for (const observation of report.observations) {
    const item = textNode("li", observation.text);
    if (observation.evidence_ids.length)
      item.append(
        textNode(
          "span",
          `Evidence: ${observation.evidence_ids.join(", ")}`,
          "muted",
        ),
      );
    list.append(item);
  }
  notesKey = key;
}
