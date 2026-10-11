// Run with node ops/check_observer.cjs; uses an installed Chrome or Edge, no npm packages.
const assert = require("node:assert/strict");
const fs = require("node:fs");
const http = require("node:http");
const os = require("node:os");
const path = require("node:path");
const { spawn } = require("node:child_process");

async function checkInBrowser(fixture) {
  await new Promise((resolve) => document.addEventListener("DOMContentLoaded", resolve));
  const get = (id) => document.getElementById(id);
  const check = (condition, message) => { if (!condition) throw new Error(message); };
  const wait = async (condition) => {
    for (let attempt = 0; attempt < 100; attempt++) {
      if (condition()) return;
      await new Promise((resolve) => setTimeout(resolve, 10));
    }
    throw new Error("Observer did not finish loading: " + get("message").textContent);
  };
  const snapshots = {};
  for (const [id, tick, capacity] of [["A", 420, 64], ["B", 80, 8], ["legacy", 16, 4], ["live", 5, 8]]) {
    const snapshot = structuredClone(fixture.snapshot);
    snapshot.run.id = id;
    snapshot.tick = tick;
    snapshot.compute[0].worker_capacity = capacity;
    snapshot.nations[0].name = "Aster " + id;
    if (id === "legacy") {
      snapshot.run.stage = 1;
      snapshot.countries = snapshot.countries.slice(0, 5);
      snapshot.shipments = [{ status: "planning_only", plan: null }];
    }
    if (id === "live") snapshot.run.status = "running";
    snapshots[id] = snapshot;
  }
  const agents = (id) => Array.from({ length: 60 }, (_, index) => ({
    ...fixture.agent_page.agents[0],
    id: `${index % 2 ? "beryl" : "aster"}_${id}_${index}`,
    country_id: index % 2 ? "beryl" : "aster",
    occupation: index % 2 ? "doctor" : "farmer",
  }));
  let current = "A";
  let blocked;
  let release;
  window.setInterval = () => 0;
  window.fetch = async (url) => {
    const { pathname, searchParams } = new URL(url, location.href);
    if (blocked?.path === pathname && !blocked.used) {
      const delayed = blocked;
      blocked.used = true;
      await new Promise((resolve) => { release = resolve; });
      if (delayed.fail) throw new Error("Old run request failed");
    }
    let body;
    if (pathname === "/api/health") body = { status: "ok", storage: "local", run_id: current };
    else if (pathname === "/api/runs") body = { current_run_id: current, run_ids: Object.keys(snapshots) };
    else {
      const [, , , id, kind, agentId] = pathname.split("/");
      if (kind === "snapshot") body = snapshots[id];
      if (kind === "events") body = Number(searchParams.get("after_seq")) < fixture.snapshot.seq ? [
        { seq: fixture.snapshot.seq - 1, tick: snapshots[id].tick, source: "rule", event_type: "policy_enacted", entity_ids: ["aster"], payload: { action: "set_tax", reason: "Policy enacted " + id } },
        { seq: fixture.snapshot.seq, tick: snapshots[id].tick, source: "model", event_type: "action_accepted", entity_ids: ["aster_government"], payload: { action: { action_type: "hold", reason: "Wait " + id } } },
      ] : [];
      if (kind === "notes") body = { status: "completed", tick: snapshots[id].tick, through_sequence: fixture.snapshot.seq, history_complete: true, observations: [{ text: "Notes " + id, evidence_ids: [] }] };
      if (kind === "agents" && !agentId) {
        const matches = agents(id).filter((agent) =>
          (!searchParams.get("country") || agent.country_id === searchParams.get("country")) &&
          (!searchParams.get("role") || agent.role === searchParams.get("role")) &&
          (!searchParams.get("q") || agent.occupation.includes(searchParams.get("q")) || agent.id.includes(searchParams.get("q"))));
        const offset = Number(searchParams.get("offset"));
        body = { total: matches.length, offset, agents: matches.slice(offset, offset + 50) };
      }
      if (kind === "agents" && agentId) {
        body = structuredClone(fixture.agent_detail);
        body.agent.id = agentId;
        body.agent.last_model_decision_at = fixture.snapshot.updated_at;
        body.agent.model_decisions = 2;
        body.model_decision_age_seconds = 120;
      }
    }
    check(body !== undefined, "Unexpected request: " + url);
    return new Response(JSON.stringify(body), { headers: { "Content-Type": "application/json" } });
  };
  const select = (id) => {
    get("runs").value = id;
    get("runs").dispatchEvent(new Event("change"));
  };
  const ready = (id) => get("run-notes").textContent === "Notes " + id && get("agents").children.length === 50;
  try {
    const script = document.createElement("script");
    script.src = "/observer.js";
    document.head.append(script);
    await wait(() => ready("A"));
    check(get("events").textContent.includes("set_tax") && get("events").textContent.includes("Policy enacted A"), "String-valued policy action failed to render");
    check(get("mode").textContent.includes("SAVED"), "Completed run is not labeled saved");
    for (const endpoint of ["snapshot", "events", "notes", "agents", "notes-failure"]) {
      blocked = { path: `/api/runs/B/${endpoint.replace("-failure", "")}`, fail: endpoint.endsWith("-failure") };
      select("B");
      check(!get("nations").children.length && !get("run-notes").children.length, "Old panels survived run selection");
      await wait(() => blocked.used);
      select("A");
      release();
      await wait(() => ready("A"));
      check(get("tick").textContent === "420" && get("nations").textContent.includes("Aster A"), "Stale snapshot overwrote selected run");
      check(get("compute").textContent.includes("/64 pending"), "Compute panel belongs to another run");
      check(!get("events").textContent.includes("Policy enacted B"), "Stale events survived run selection");
      check(get("notes-download").getAttribute("href").includes("/A/"), "Download points to another run");
    }
    blocked = { path: "/api/runs/A/agents/aster_A_0" };
    get("agents").firstChild.click();
    await wait(() => blocked.used);
    select("B");
    release();
    await wait(() => ready("B"));
    check(!get("profile").textContent.includes("aster_A_0"), "Stale agent profile overwrote selected run");
    get("next-page").click();
    await wait(() => get("agent-page").textContent === "51–60 of 60");
    get("agent-search").value = "doctor";
    get("agent-search").dispatchEvent(new Event("input"));
    await wait(() => get("agent-page").textContent === "1–30 of 30");
    check([...get("agents").children].every((button) => button.textContent.startsWith("beryl_B_")), "Search returned wrong agents");
    get("agent-search").value = "";
    get("agent-search").dispatchEvent(new Event("input"));
    await wait(() => ready("B"));
    get("country-filter").value = "aster";
    get("country-filter").dispatchEvent(new Event("change"));
    await wait(() => get("agent-page").textContent === "1–30 of 30");
    check([...get("agents").children].every((button) => button.textContent.startsWith("aster_B_")), "Country filter returned wrong agents");
    get("role-filter").value = "government";
    get("role-filter").dispatchEvent(new Event("change"));
    await wait(() => get("agent-page").textContent === "0–0 of 0");
    get("country-filter").value = "";
    get("role-filter").value = "";
    select("legacy");
    await wait(() => get("compute").textContent.includes("Stage 1 recorded run") && get("agents").children.length === 50);
    check(get("world-details").hidden, "Expanded panels remain visible for Stage 1");
    current = "live";
    select("live");
    await wait(() => get("notes-status").textContent.includes("Simulation in progress") && get("agents").children.length === 50);
    check(!get("mode").textContent.includes("SAVED"), "Running current world is labeled saved");
    select("A");
    await wait(() => ready("A"));
    get("agents").firstChild.click();
    await wait(() => get("profile").textContent.includes("decision age 120s at checkpoint"));
    check(get("message").textContent === "", "Observer reported an error");
    check(document.documentElement.scrollWidth <= window.innerWidth, "Observer overflows the viewport horizontally");
    document.body.dataset.observerCheck = "passed";
  } catch (error) {
    document.body.dataset.observerCheck = "failed";
    get("message").textContent = error.stack;
  }
}

async function main() {
  const root = path.resolve(__dirname, "..");
  const web = path.join(root, "backend/Faultline.Api/wwwroot");
  const fixture = fs.readFileSync(path.join(root, "docs/faultline/fixtures/stage2-api-sample.json"), "utf8");
  const html = fs.readFileSync(path.join(web, "index.html"), "utf8").replace(
    '<script src="/observer.js" defer></script>',
    `<script>(${checkInBrowser.toString()})(${fixture.replaceAll("<", "\\u003c")});</script>`,
  );
  const browser = [
    "C:/Program Files/Google/Chrome/Application/chrome.exe",
    "C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe",
    "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome",
    "/usr/bin/chromium", "/usr/bin/google-chrome",
  ].find((file) => fs.existsSync(file));
  assert(browser, "Install Chrome or Edge to run this browser check");
  const server = http.createServer((request, response) => {
    const files = { "/observer.js": "text/javascript", "/style.css": "text/css" };
    response.setHeader("Content-Type", files[request.url] ?? "text/html");
    response.end(files[request.url] ? fs.readFileSync(path.join(web, request.url)) : html);
  });
  await new Promise((resolve) => server.listen(0, "127.0.0.1", resolve));
  const profile = fs.mkdtempSync(path.join(os.tmpdir(), "faultline-browser-"));
  const artifacts = path.join(root, "artifacts/local");
  fs.mkdirSync(artifacts, { recursive: true });
  try {
    for (const [name, size] of [["desktop", "1440,2800"], ["mobile", "430,5000"]]) {
      const screenshot = path.join(artifacts, `observer-${name}.png`);
      const child = spawn(browser, ["--headless=new", "--disable-gpu", "--no-first-run", `--user-data-dir=${profile}`, `--window-size=${size}`, "--virtual-time-budget=10000", "--dump-dom", `--screenshot=${screenshot}`, `http://127.0.0.1:${server.address().port}`], { windowsHide: true });
      let output = "";
      child.stdout.on("data", (chunk) => { output += chunk; });
      child.stderr.on("data", () => {});
      const code = await new Promise((resolve, reject) => { child.on("error", reject); child.on("close", resolve); });
      assert.equal(code, 0, "Browser process failed");
      assert(output.includes('data-observer-check="passed"'), output.match(/<p id="message"[^>]*>([\s\S]*?)<\/p>/)?.[1] ?? "Browser check did not finish");
      console.log(`Passed ${name}: policy events, delayed run responses/errors, agent profiles, search, filters, pagination, Stage 1, saved/running labels, checkpoint decision age`);
      console.log("Screenshot: " + screenshot);
    }
  } finally {
    server.close();
    assert.equal(path.dirname(path.resolve(profile)), path.resolve(os.tmpdir()));
    fs.rmSync(profile, { recursive: true, force: true, maxRetries: 3 });
  }
}

main().catch((error) => { console.error(error.message); process.exitCode = 1; });
