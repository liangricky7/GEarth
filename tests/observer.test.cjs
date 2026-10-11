const assert = require("node:assert/strict");
const { readFileSync } = require("node:fs");
const { test } = require("node:test");
const { JSDOM } = require("jsdom");

const root = "backend/Faultline.Api/wwwroot/";
const html = readFileSync(root + "index.html", "utf8");
const script = readFileSync(root + "observer.js", "utf8");
const sample = JSON.parse(
  readFileSync("docs/faultline/fixtures/stage2-api-sample.json", "utf8"),
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
      event_type: "action_accepted",
      tick,
      seq: data.snapshot.seq,
      source: "model",
      entity_ids: ["aster_government"],
      payload: {
        action: { action_type: "publish", params: { text: "A policy update" } },
      },
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

function observer(data, intercept, { manualTimeouts = false } = {}) {
  const dom = new JSDOM(html, {
    url: "http://localhost",
    runScripts: "outside-only",
  });
  const requests = [];
  const window = dom.window;
  const current = Object.keys(data)[0];
  window.setInterval = () => 0;
  const requestTimers = new Map();
  if (manualTimeouts) {
    let timerId = 0;
    window.setTimeout = (callback) => {
      requestTimers.set(++timerId, callback);
      return timerId;
    };
    window.clearTimeout = (id) => requestTimers.delete(id);
  }
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
        run.events.filter((event) => event.seq > after && event.seq <= through),
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
  return {
    dom, window, element, requests, changeRun,
    expireRequests: () => {
      const callbacks = [...requestTimers.values()];
      requestTimers.clear();
      for (const callback of callbacks) callback();
    },
  };
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
  const pending = view.requests.filter(
    (request) => request.path === "/api/runs/first/notes",
  ).at(-1);
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

test("a failed secondary panel does not hide a newly selected world", async (t) => {
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
    () => view.element("connection").textContent.includes("panels retrying"),
  );
  assert.equal(view.element("tick").textContent, "80");
  assert.notEqual(view.element("events").textContent, "");
  assert.notEqual(view.element("nations").textContent, "");
  assert.notEqual(view.element("agents").textContent, "");
  assert.equal(view.element("run-notes").textContent, "");
  assert.doesNotMatch(view.element("run-notes").textContent, /first/);
  assert.equal(view.element("notes-download").hidden, true);
  fail = false;
  await view.window.eval("refresh()");
  assert.equal(view.element("tick").textContent, "80");
  assert.match(view.element("run-notes").textContent, /second/);
});

test("a connection outage retains the world and recovers without resetting the event cursor", async (t) => {
  let fail = false;
  const run = fixture("first");
  const view = observer({ first: run }, (path) =>
    fail && path === "/api/health" ? response({}, 503) : null,
  );
  t.after(() => view.dom.window.close());
  await until(() => view.element("run-notes").textContent.includes("first"));
  const panels = ["events", "nations", "agents", "run-notes"].map(
    (id) => view.element(id).textContent,
  );
  fail = true;
  await view.window.eval("refresh()");
  assert.equal(view.element("tick").textContent, "40");
  assert.deepEqual(
    ["events", "nations", "agents", "run-notes"].map((id) => view.element(id).textContent),
    panels,
  );
  assert.match(view.element("connection").textContent, /Reconnecting/);
  assert.match(view.element("message").textContent, /last loaded state/);
  fail = false;
  run.snapshot.tick = 41;
  await view.window.eval("refresh()");
  assert.equal(view.element("tick").textContent, "41");
  assert.match(view.element("connection").textContent, /Connected/);
  assert.equal(view.element("message").textContent, "");
  const eventRequest = view.requests.filter((request) => request.path.includes("/events?")).at(-1);
  assert.match(eventRequest.path, new RegExp(`after_seq=${run.snapshot.seq}&`));
});

test("metrics render while a panel hangs, and its timeout allows the next refresh", async (t) => {
  const slow = deferred();
  let hang = false;
  const run = fixture("first");
  const view = observer({ first: run }, (path) =>
    hang && path.includes("/agents?") ? slow.promise : null,
    { manualTimeouts: true },
  );
  t.after(() => view.dom.window.close());
  await until(() => view.element("run-notes").textContent.includes("first"));
  hang = true;
  run.snapshot.tick = 41;
  const pendingRefresh = view.window.eval("refresh()");
  await until(() => view.element("tick").textContent === "41");
  await until(() => view.requests.filter((request) => request.path.includes("/agents?")).length === 2);
  const pending = view.requests.filter((request) => request.path.includes("/agents?")).at(-1);
  view.expireRequests();
  await pendingRefresh;
  assert.equal(pending.signal.aborted, true);
  assert.notEqual(view.element("agents").textContent, "");
  assert.match(view.element("message").textContent, /Retrying agents/);
  hang = false;
  run.snapshot.tick = 42;
  await view.window.eval("refresh()");
  assert.equal(view.element("tick").textContent, "42");
  assert.equal(view.element("message").textContent, "");
});

test("a hung health request times out without locking future refreshes", async (t) => {
  const slow = deferred();
  let hang = false;
  const view = observer({ first: fixture("first") }, (path) =>
    hang && path === "/api/health" ? slow.promise : null,
    { manualTimeouts: true },
  );
  t.after(() => view.dom.window.close());
  await until(() => view.element("run-notes").textContent.includes("first"));
  hang = true;
  const pendingRefresh = view.window.eval("refresh()");
  await until(() => view.requests.filter((request) => request.path === "/api/health").length === 2);
  view.expireRequests();
  await pendingRefresh;
  assert.equal(view.element("tick").textContent, "40");
  assert.match(view.element("message").textContent, /timed out/);
  hang = false;
  await view.window.eval("refresh()");
  assert.match(view.element("connection").textContent, /Connected/);
});

test("long disconnections catch up only the recent event window", async (t) => {
  const run = fixture("first");
  const view = observer({ first: run });
  t.after(() => view.dom.window.close());
  await until(() => view.element("run-notes").textContent.includes("first"));
  run.snapshot.seq += 10000;
  run.events = [{ ...run.events[0], seq: run.snapshot.seq }];
  await view.window.eval("refresh()");
  const eventRequest = view.requests.filter((request) => request.path.includes("/events?")).at(-1);
  assert.match(eventRequest.path, new RegExp(`after_seq=${run.snapshot.seq - 500}&`));
  assert.equal(view.requests.filter((request) => request.path.includes("/events?")).length, 2);
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
    skip: !process.env.FAULTLINE_OBSERVER_URL,
  },
  async (t) => {
    const base = process.env.FAULTLINE_OBSERVER_URL.replace(/\/$/, "");
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
