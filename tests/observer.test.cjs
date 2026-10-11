const assert = require("node:assert/strict");
const { readFileSync } = require("node:fs");
const { test } = require("node:test");
const { JSDOM } = require("jsdom");

const root = "backend/GEarth.Api/wwwroot/";
const html = readFileSync(root + "index.html", "utf8");
const script = readFileSync(root + "observer.js", "utf8");
const sample = JSON.parse(
  readFileSync("docs/gearth/fixtures/stage2-api-sample.json", "utf8"),
);

function fixture(id, tick = 40) {
  const data = structuredClone(sample);
  data.snapshot.run.id = id;
  data.snapshot.tick = tick;
  data.events = [
    {
      run_id: id,
      event_type: "policy_enacted",
      tick,
      seq: data.snapshot.seq - 1,
      source: "rule",
      entity_ids: ["aster"],
      payload: { action: "set_tax" },
    },
    {
      run_id: id,
      event_type: "public_report",
      tick,
      seq: data.snapshot.seq,
      source: "model",
      entity_ids: ["aster", "aster_journalist"],
      payload: { text: "A policy update" },
    },
  ];
  data.notes = {
    status: "completed",
    tick,
    through_sequence: data.snapshot.seq,
    history_complete: true,
    observations: [{ text: `Observations for ${id}`, evidence_ids: [] }],
  };
  data.agent_detail.model_decision_age_seconds = 12;
  data.agent_detail.agent.last_model_decision_at = "2026-10-10T12:00:00Z";
  return data;
}

function response(body, status = 200) {
  return {
    ok: status >= 200 && status < 300,
    status,
    json: async () => structuredClone(body),
  };
}

function deferred() {
  let resolve;
  const promise = new Promise((complete) => {
    resolve = complete;
  });
  return { promise, resolve };
}

async function until(predicate) {
  for (let attempt = 0; attempt < 200; attempt++) {
    if (predicate()) return;
    await new Promise((resolve) => setTimeout(resolve, 5));
  }
  throw new Error("Observer did not reach the expected state");
}

function observer(data, intercept, query = "") {
  const dom = new JSDOM(html, {
    url: `http://localhost/${query}`,
    runScripts: "outside-only",
  });
  const requests = [];
  const window = dom.window;
  const current = Object.keys(data)[0];
  window.setInterval = () => 0;
  window.fetch = async (path, options) => {
    requests.push({ path, signal: options.signal });
    const overridden = intercept?.(path, options);
    if (overridden) return overridden;
    if (path === "/api/health")
      return response({ status: "ok", storage: "local", run_id: current });
    if (path === "/api/runs")
      return response({ current_run_id: current, run_ids: Object.keys(data) });
    const url = new URL(path, "http://localhost");
    const [, , , id, resource, agentId] = url.pathname.split("/");
    const run = data[id];
    if (!run) return response({}, 404);
    if (resource === "snapshot") return response(run.snapshot);
    if (resource === "notes") return response(run.notes);
    if (resource === "events") {
      const after = Number(url.searchParams.get("after_seq"));
      const through = Number(url.searchParams.get("through_seq"));
      return response(
        run.events.filter((event) => event.seq > after && event.seq <= through).slice(0, Number(url.searchParams.get("limit"))),
      );
    }
    if (resource === "agents" && agentId) return response(run.agent_detail);
    if (resource === "agents") {
      const page = structuredClone(run.agent_page);
      const offset = Number(url.searchParams.get("offset"));
      page.offset = offset;
      page.total = run.agent_page.total;
      if (url.searchParams.get("q")) {
        page.agents = page.agents.filter((agent) =>
          agent.id.includes(url.searchParams.get("q")),
        );
        page.total = page.agents.length;
      }
      return response(page);
    }
    return response({}, 404);
  };
  window.eval(script);
  const element = (id) => window.document.getElementById(id);
  const changeRun = (id) => {
    element("runs").value = id;
    element("runs").dispatchEvent(new window.Event("change"));
  };
  return { dom, window, element, requests, changeRun };
}

test("policy strings and action objects render together; optional parameters are safe", async (t) => {
  const run = fixture("first");
  run.events.push({
    event_type: "action_rejected",
    tick: 40,
    seq: run.snapshot.seq,
    source: "model",
    payload: { action: { action_type: "hold" } },
  });
  const view = observer({ first: run });
  t.after(() => view.dom.window.close());
  await until(() =>
    view.element("connection").textContent.startsWith("Connected"),
  );
  assert.match(view.element("events").textContent, /set_tax/);
  assert.match(view.element("events").textContent, /A policy update/);
  assert.equal(view.element("message").textContent, "");
  assert.match(view.element("run-notes").textContent, /Observations for first/);
});

test("feed drops noise, writes sentences and groups same-day repeats", async (t) => {
  const run = fixture("feed");
  const seq = run.snapshot.seq;
  const accepted = (n, id, params) => ({
    event_type: "action_accepted", tick: 40, seq: seq - 10 + n, source: "model", entity_ids: [id],
    payload: { action: { action_type: "set_price", params, reason: "More profit" } },
  });
  run.events = [
    { event_type: "decision_requested", tick: 40, seq: seq - 12, source: "rule", entity_ids: ["aster_food"], payload: {} },
    { event_type: "labor_market", tick: 40, seq: seq - 11, source: "rule", entity_ids: ["aster"], payload: { layoffs: 7, hires: 0 } },
    accepted(0, "aster_food_2", { change_pct: 10, business_id: "aster_food_2" }),
    accepted(1, "aster_energy", { change_pct: 10, business_id: "aster_energy" }),
    accepted(2, "aster_clinic", { change_pct: -5, business_id: "aster_clinic" }),
    {
      event_type: "economy_cycle", tick: 40, seq: seq - 5, source: "rule", entity_ids: ["aster"],
      payload: { unemployed: 13, hungry_residents: 4, priced_out_residents: 4, government_income_minor: 24233, government_spending_minor: 14200 },
    },
    {
      event_type: "policy_enacted", tick: 40, seq, source: "rule", entity_ids: ["aster", "policy_1"],
      payload: { action: "adjust_tax", params: { change_basis_points: -100 }, reason: "INSTITUTIONAL_APPROVAL_AND_FUNDING" },
    },
  ];
  const view = observer({ feed: run });
  t.after(() => view.dom.window.close());
  await until(() => view.element("connection").textContent.startsWith("Connected"));
  const titles = [...view.element("events").querySelectorAll(".event-title")].map((node) => node.textContent);
  assert.deepEqual(titles, [
    "Government cuts income tax by 1 point",
    "Week 6 summary: 4 hungry (4 priced out of food), 13 unemployed, 7 laid off, budget +100.33 WLD (in 242.33 WLD, out 142.00 WLD)",
    "Clinic cuts prices 5%",
    "2 firms in 1 country raise prices 10% (energy ×1, food ×1)",
  ]);
  const shown = [...view.element("events").querySelectorAll(".event-title, .event-body")].map((node) => node.textContent).join("\n");
  assert.doesNotMatch(shown, /decision requested|INSTITUTIONAL/);
});

test("bank ledger noise stays available without crowding out highlights", async (t) => {
  const run = fixture("noise");
  run.snapshot.seq = 1500;
  run.events = [{ event_type: "policy_enacted", tick: 7, seq: 1, entity_ids: ["aster"], payload: { action: "set_tax" } }];
  for (let seq = 2; seq <= 1500; seq++) run.events.push({
    event_type: "bank_ledger", tick: 7, seq, entity_ids: ["aster_bank"],
    payload: { bank_id: "aster_bank", kind: "wages", amount_minor: 100 },
  });
  const view = observer({ noise: run });
  t.after(() => view.dom.window.close());
  await until(() => view.element("connection").textContent.startsWith("Connected"));
  assert.match(view.element("events").textContent, /set_tax/);
  assert.doesNotMatch(view.element("events").textContent, /Bank records|bank records/);
  assert.match(view.element("event-count").textContent, /^1 matching/);
  const requestCount = view.requests.length;
  view.element("event-routine").checked = true;
  view.element("event-routine").dispatchEvent(new view.window.Event("change"));
  assert.match(view.element("events").textContent, /1499 wages entries totaling 1,499.00 WLD/);
  assert.match(view.element("event-count").textContent, /^1500 matching/);
  assert.equal(view.requests.length, requestCount);
  const ledger = view.element("event-types").querySelector('input[value="bank_ledger"]');
  ledger.checked = false;
  ledger.dispatchEvent(new view.window.Event("change"));
  await view.window.eval("refresh()");
  assert.doesNotMatch(view.element("events").textContent, /wages entries/);
  assert.equal(view.element("event-types").querySelector('input[value="bank_ledger"]').checked, false);
  assert.deepEqual(JSON.parse(view.window.localStorage.getItem("gearth.eventFilters")).hidden, ["bank_ledger"]);
});

test("event country and search filters combine and reset without changing agent filters", async (t) => {
  const run = fixture("filters");
  const view = observer({ filters: run });
  t.after(() => view.dom.window.close());
  await until(() => view.element("connection").textContent.startsWith("Connected"));
  view.element("event-country").value = "aster";
  view.element("event-country").dispatchEvent(new view.window.Event("change"));
  view.element("event-search").value = "ASTER_JOURNALIST";
  view.element("event-search").dispatchEvent(new view.window.Event("input"));
  assert.match(view.element("events").textContent, /A policy update/);
  assert.doesNotMatch(view.element("events").textContent, /set_tax/);
  assert.equal(view.element("country-filter").value, "");
  view.element("event-search").value = "nothing matches";
  view.element("event-search").dispatchEvent(new view.window.Event("input"));
  assert.match(view.element("events").textContent, /No events match/);
  view.element("event-reset").click();
  assert.match(view.element("events").textContent, /set_tax/);
  assert.equal(view.element("event-search").value, "");
  assert.equal(view.element("event-country").value, "");
});

test("late run responses cannot overwrite the selected run", async (t) => {
  const slow = deferred();
  let delay = false;
  const first = fixture("first");
  const second = fixture("second", 80);
  const view = observer({ first, second }, (path) =>
    delay && path === "/api/runs/first/notes" ? slow.promise : null,
  );
  t.after(() => view.dom.window.close());
  await until(() => view.element("run-notes").textContent.includes("first"));
  await view.window.eval("refresh()");
  assert.match(view.element("run-notes").textContent, /first/);
  view.changeRun("second");
  await until(() => view.element("run-notes").textContent.includes("second"));
  delay = true;
  view.changeRun("first");
  await until(
    () =>
      view.requests.filter(
        (request) => request.path === "/api/runs/first/notes",
      ).length === 2,
  );
  const pending = view.requests.at(-1);
  view.changeRun("second");
  assert.equal(view.element("tick").textContent, "—");
  assert.equal(view.element("run-notes").textContent, "");
  await until(() => view.element("run-notes").textContent.includes("second"));
  slow.resolve(response(first.notes));
  await new Promise((resolve) => setTimeout(resolve, 20));
  assert.equal(pending.signal.aborted, true);
  assert.equal(view.element("tick").textContent, "80");
  assert.match(view.element("notes-download").href, /second\/notes.md$/);
  assert.match(view.element("run-notes").textContent, /second/);
  assert.doesNotMatch(view.element("run-notes").textContent, /first/);
});

test("failed run loading clears previous panels and recovers on refresh", async (t) => {
  let fail = true;
  const view = observer(
    { first: fixture("first"), second: fixture("second", 80) },
    (path) =>
      fail && path === "/api/runs/second/notes" ? response({}, 503) : null,
  );
  t.after(() => view.dom.window.close());
  await until(() => view.element("run-notes").textContent.includes("first"));
  view.changeRun("second");
  await until(
    () => view.element("connection").textContent === "Connection interrupted",
  );
  for (const id of [
    "events",
    "nations",
    "shipments",
    "relations",
    "agents",
    "run-notes",
  ])
    assert.equal(view.element(id).textContent, "");
  assert.equal(view.element("notes-download").hidden, true);
  assert.equal(view.element("tick").textContent, "—");
  fail = false;
  await view.window.eval("refresh()");
  assert.equal(view.element("tick").textContent, "80");
  assert.match(view.element("run-notes").textContent, /second/);
});

test("stale agent profiles are discarded and decision age uses committed time", async (t) => {
  const slow = deferred();
  const first = fixture("first");
  const second = fixture("second", 80);
  const agentId = first.agent_page.agents[0].id;
  const view = observer({ first, second }, (path) =>
    path === `/api/runs/first/agents/${agentId}` ? slow.promise : null,
  );
  t.after(() => view.dom.window.close());
  await until(() => view.element("agents").children.length);
  view.element("agents").firstChild.click();
  await until(() =>
    view.requests.some((request) =>
      request.path.endsWith(`/agents/${agentId}`),
    ),
  );
  await view.window.eval("refresh()");
  const profiles = view.requests.filter((request) =>
    request.path.endsWith(`/agents/${agentId}`),
  );
  assert.equal(profiles.length, 1);
  assert.equal(profiles[0].signal.aborted, false);
  view.changeRun("second");
  await until(() => view.element("tick").textContent === "80");
  slow.resolve(response(first.agent_detail));
  await new Promise((resolve) => setTimeout(resolve, 20));
  assert.match(view.element("profile").textContent, /Select an agent/);
  view.element("agents").firstChild.click();
  await until(() =>
    view.element("profile").textContent.includes("decision age"),
  );
  assert.match(
    view.element("profile").textContent,
    /decision age 12s at checkpoint/,
  );
});

test("new search and page requests replace in-flight requests", async (t) => {
  const slow = deferred();
  const run = fixture("first");
  run.agent_page.total = 100;
  const view = observer({ first: run }, (path) =>
    path.includes("q=slow") ? slow.promise : null,
  );
  t.after(() => view.dom.window.close());
  await until(() => view.element("agents").children.length);
  view.element("agent-search").value = "slow";
  view.element("agent-search").dispatchEvent(new view.window.Event("input"));
  await until(() =>
    view.requests.some((request) => request.path.includes("q=slow")),
  );
  view.element("agent-search").value = "";
  view.element("agent-search").dispatchEvent(new view.window.Event("input"));
  await until(() => view.element("next-page").disabled === false);
  slow.resolve(response({ ...run.agent_page, total: 0, agents: [] }));
  await new Promise((resolve) => setTimeout(resolve, 20));
  assert.notEqual(view.element("agents").children.length, 0);
  view.element("next-page").click();
  await until(() => view.element("agent-page").textContent.startsWith("51"));
  view.element("previous-page").click();
  await until(() => view.element("agent-page").textContent.startsWith("1–"));
});

test(
  "recorded live and stopped runs render and switch through the real read API",
  {
    skip: !process.env.GEARTH_OBSERVER_URL,
  },
  async (t) => {
    const base = process.env.GEARTH_OBSERVER_URL.replace(/\/$/, "");
    const ids = [
      "world_20261010_125750_6d3812",
      "world_20261010_133526_7c181f",
    ];
    const data = {};
    const get = async (path) => {
      const result = await fetch(base + path);
      assert.equal(result.status, 200, path);
      return result.json();
    };
    for (const id of ids) {
      const path = `/api/runs/${id}`;
      const snapshot = await get(path + "/snapshot");
      const [events, notes, agent_page] = await Promise.all([
        get(
          `${path}/events?after_seq=${Math.max(0, snapshot.seq - 200)}&through_seq=${snapshot.seq}&limit=500`,
        ),
        get(path + "/notes"),
        get(path + "/agents?limit=50"),
      ]);
      const agent_detail = await get(
        `${path}/agents/${agent_page.agents[0].id}`,
      );
      data[id] = { snapshot, events, notes, agent_page, agent_detail };
    }
    const view = observer(data);
    t.after(() => view.dom.window.close());
    for (const id of [ids[0], ids[1], ids[0]]) {
      if (id !== ids[0] || view.element("tick").textContent !== "—")
        view.changeRun(id);
      await until(
        () =>
          view.element("tick").textContent === String(data[id].snapshot.tick),
      );
      assert.equal(view.element("message").textContent, "");
      assert.match(view.element("connection").textContent, /Connected/);
      assert.equal(
        view.element("run-notes").children.length,
        data[id].notes.observations.length,
      );
      assert.match(
        view.element("notes-download").href,
        new RegExp(id + "/notes.md$"),
      );
      assert.equal(view.element("nations").children.length, 10);
      view.element("agents").firstChild.click();
      await until(() =>
        view
          .element("profile")
          .textContent.includes(data[id].agent_detail.agent.id),
      );
    }
  },
);


test("saved-data backlink opens its selected older run even outside the latest catalog", async (t) => {
  const current = fixture("current", 45), old = fixture("older", 12);
  const view = observer({ current, older: old }, path => path === "/api/runs" ? response({ current_run_id: "current", run_ids: ["current"] }) : null, "?run_id=older");
  t.after(() => view.dom.window.close());
  await until(() => view.element("tick").textContent === "12");
  assert.equal(view.element("runs").value, "older");
  assert.equal(view.element("data-explorer-link").getAttribute("href"), "/data.html?run_id=older");
  assert.ok(view.requests.some(request => request.path === "/api/runs/older/snapshot"));
  assert.ok(!view.requests.some(request => request.path === "/api/runs/current/snapshot"));
});

test("malformed saved-data run IDs do not override the current observer run", async (t) => {
  const view = observer({ current: fixture("current", 45) }, null, "?run_id=../../unsafe");
  t.after(() => view.dom.window.close());
  await until(() => view.element("tick").textContent === "45");
  assert.equal(view.element("runs").value, "current");
  assert.ok(!view.requests.some(request => request.path.includes("unsafe")));
});
