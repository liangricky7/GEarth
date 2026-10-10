const element = (id) => document.getElementById(id);
let selectedRun = null;
let selectedAgent = null;
let lastSequence = 0;
let displayedEvents = [];
let busy = false;

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
  const point = (location) => [
    100 + (location.lon + 80) * 6,
    65 + (50 - location.lat) * 4.2,
  ];
  const locations = new Map(
    snapshot.locations.map((location) => [location.id, location]),
  );
  const planned = new Set(snapshot.shipments[0].plan?.edge_ids ?? []);

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
  for (const event of displayedEvents.slice(-35).reverse()) {
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

function showAgents(snapshot) {
  const list = element("agents");
  list.replaceChildren();
  for (const agent of snapshot.agents) {
    const button = textNode("button", agent.id, "agent-button");
    button.type = "button";
    if (agent.id === selectedAgent) button.classList.add("selected");
    button.append(
      textNode(
        "span",
        `${agent.role} · ${(agent.cash_minor / 100).toFixed(2)} WLD`,
      ),
    );
    button.addEventListener("click", async () => {
      selectedAgent = agent.id;
      showAgents(snapshot);
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
  } catch (error) {
    element("message").textContent = error.message;
  }
}

async function refresh() {
  if (busy) return;
  busy = true;
  try {
    const health = await getJson("/api/health");
    if (!selectedRun) {
      const runs = await getJson("/api/runs");
      selectedRun = runs.current_run_id ?? runs.run_ids[0];
      element("runs").replaceChildren();
      for (const id of runs.run_ids) {
        const option = textNode("option", id);
        option.value = id;
        element("runs").append(option);
      }
      element("runs").value = selectedRun ?? "";
    }
    if (!selectedRun) {
      element("connection").textContent = "Initializing world";
      return;
    }

    const snapshot = await getJson(
      `/api/runs/${encodeURIComponent(selectedRun)}/snapshot`,
    );
    let events;
    do {
      events = await getJson(
        `/api/runs/${encodeURIComponent(selectedRun)}/events?after_seq=${lastSequence}&limit=500`,
      );
      displayedEvents.push(...events);
      if (events.length) lastSequence = events.at(-1).seq;
    } while (events.length === 500);

    displayedEvents = displayedEvents.slice(-100);
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
    const plan = snapshot.shipments[0].plan;
    element("route-plan").textContent = plan
      ? `${plan.objective} plan · ${plan.travel_ticks} travel ticks · ${(plan.cost_minor / 100).toFixed(2)} WLD`
      : "No active medicine route plan.";
    element("message").textContent = health.error ?? "";
    drawMap(snapshot);
    showEvents(snapshot);
    showAgents(snapshot);
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
  selectedAgent = null;
  lastSequence = 0;
  displayedEvents = [];
  element("profile").replaceChildren(
    textNode("p", "Select an agent to inspect its profile.", "muted"),
  );
  refresh();
});

refresh();
setInterval(refresh, 1500);
