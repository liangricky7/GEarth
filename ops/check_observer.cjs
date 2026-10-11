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
  const snapshots = {}, rendererFrames = {};
  for (const [id, tick, capacity] of [["A", 420, 64], ["B", 80, 8], ["legacy", 16, 4], ["live", 5, 8]]) {
    const snapshot = structuredClone(fixture.snapshot);
    snapshot.run.id = id;
    snapshot.tick = tick;
    snapshot.compute[0].worker_capacity = capacity;
    snapshot.nations[0].name = "Aster " + id;
    snapshot.agents_summary.spawned = 1100;
    snapshot.constellation = snapshot.countries.map((country, index) => {
      const met = 90 - index * 7, partial = 5 + index * 2, unmet = 100 - met - partial;
      return { country_id:country.id, citizen_count:100, met, partial, unmet, unknown:0,
        citizens:Array.from({length:48}, (_,i) => ({id:`${country.id}_citizen_${i}`,country_id:country.id,occupation:i%2 ? "farmer" : "factory_worker",needs_met:i*100/48<met ? 2 : i*100/48<met+partial ? 1 : 0,health:80,wellbeing:75,hungry:false,employer_id:`${country.id}_food`})) };
    });
    snapshot.relationships.find(item=>item.id==="aster:beryl").trade_agreement=true;
    Object.assign(snapshot.relationships.find(item=>item.id==="aster:cinder"),{proposed_by:"aster",proposal_kind:"trade",proposal_expires_tick:tick+7});
    snapshot.relationships.find(item=>item.id==="aster:ember").at_war=true;
    snapshot.shipments.push({id:"trace_shipment",seller_id:"aster_food",buyer_id:"beryl_food",good:"food",quantity:12,status:"in_transit",created_tick:tick,location_id:"aster_city",plan:null});
    if (id === "legacy") {
      snapshot.run.stage = 1;
      snapshot.countries = snapshot.countries.slice(0, 5);
      snapshot.shipments = [{ status: "planning_only", plan: null }];
    }
    if (id === "live") snapshot.run.status = "running";
    rendererFrames[id]={run_id:id,tick:snapshot.tick,snapshot:{agents:snapshot.constellation.flatMap(country=>Array.from({length:100},(_,index)=>({...country.citizens[index%country.citizens.length],id:`${country.country_id}_renderer_${index}`,role:"citizen",needs_met:index<country.met ? 2 : index<country.met+country.partial ? 1 : 0})))}};
    if(id==="legacy")delete snapshot.constellation;
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
      if (kind === "renderer" && agentId === "current") body = rendererFrames[id];
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
    await wait(() => get("constellation-countries").children.length === 10);
    check(Number(get("constellation-panel").dataset.networkNodes) > 490, "Network did not render citizens across all countries");
    check(Number(get("constellation-panel").dataset.networkEdges) > 480, "Network did not connect agent clusters");
    check(Number(get("constellation-panel").dataset.ambientNodes) === 2400,"Ambient density layer did not populate the world");
    check(Number(get("constellation-panel").dataset.ambientEdges) > 2000,"Ambient layer did not create its decorative mesh");
    check(get("constellation-caption").textContent.includes("visual points"),"Visual density was presented as a simulated agent count");
    check(Number(get("constellation-panel").dataset.signalEvents) === 1, "Recorded policy did not produce a country ripple");
    check(!get("network-event").hidden && get("network-event").textContent.includes("Recorded") && get("network-event").textContent.includes("Policy enacted"), "Ripple did not identify its recorded source");
    const duplicateEvent={seq:fixture.snapshot.seq-1,tick:snapshots.A.tick,event_type:"policy_enacted",entity_ids:["aster"]};
    document.dispatchEvent(new CustomEvent("gearth-snapshot",{detail:{snapshot:snapshots.A,events:[duplicateEvent]}}));
    check(Number(get("constellation-panel").dataset.signalEvents) === 1,"Repeated snapshot replayed the same policy event");
    document.dispatchEvent(new CustomEvent("gearth-snapshot",{detail:{snapshot:snapshots.A,events:[duplicateEvent,{seq:fixture.snapshot.seq,tick:snapshots.A.tick,event_type:"shipment_delivered",entity_ids:["aster_food","beryl_food"]}]}}));
    check(Number(get("constellation-panel").dataset.signalEvents) === 2,"Trade event did not produce a ripple");
    get("constellation-colors").click();
    check(get("constellation-panel").dataset.colorMode === "needs", "Needs color mode did not activate");
    get("constellation-colors").click();
    check(get("constellation-panel").dataset.colorMode === "countries", "Country color mode did not restore");
    check(get("constellation-detail").textContent.includes("Missing essentials"), "Constellation did not render citizen needs");
    get("constellation-countries").firstChild.click();
    check(get("constellation-detail").textContent.includes("93%"), "Country needs score did not use exact citizen totals");
    check(get("constellation-node-list").children.length > 48, "Country focus did not expose citizens and institutions");
    check(!get("constellation-node-list").textContent.includes("ambient:"),"Ambient dots leaked into agent inspection");
    const connections=[...get("constellation-node-list").querySelectorAll(".network-connection")];
    check(connections.some(button=>button.textContent.includes("Trade offer pending")),"Network did not show pending diplomatic offers");
    check(connections.some(button=>button.textContent.includes("Active conflict")),"Network did not distinguish conflict traces");
    const trade=connections.find(button=>button.textContent.includes("Goods trade"));
    check(trade,"Network did not show a recorded cross-country shipment");trade.click();
    check(get("constellation-detail").textContent.includes("Aster → Beryl"),"Trade trace reversed seller and buyer countries");
    check(get("constellation-detail").textContent.includes("12"),"Trade trace lost shipment quantity");
    get("constellation-countries").firstChild.click();
    get("constellation-reset").click();
    get("constellation-present").click();
    check(get("constellation-panel").classList.contains("constellation-presentation"), "Presentation mode did not open");
    await new Promise(resolve=>setTimeout(resolve,4350));
    check(get("constellation-panel").classList.contains("network-idle"),"Presentation chrome did not fade while idle");
    get("constellation-panel").dispatchEvent(new PointerEvent("pointermove",{bubbles:true}));
    check(!get("constellation-panel").classList.contains("network-idle"),"Pointer movement did not restore presentation controls");
    get("constellation-panel").classList.add("network-idle");
    document.dispatchEvent(new KeyboardEvent("keydown",{key:"Tab"}));
    check(!get("constellation-panel").classList.contains("network-idle"),"Keyboard input did not restore presentation controls");
    document.dispatchEvent(new KeyboardEvent("keydown",{key:"Escape"}));
    get("constellation-countries").firstChild.click();
    check(!get("constellation-panel").classList.contains("constellation-presentation"), "Escape did not exit presentation");
    get("constellation-motion").click();
    check(get("constellation-motion").getAttribute("aria-pressed") === "true", "Motion could not be paused");
    get("constellation-node-list").lastChild.click();
    await wait(() => get("constellation-detail").textContent.includes("Citizen wellbeing"));
    get("constellation-reset").click();
    check(get("constellation-detail").textContent.includes("World overview"), "World overview did not reset country focus");
    check(get("events").textContent.includes("set_tax") && get("events").textContent.includes("Policy enacted A"), "String-valued policy action failed to render");
    check(get("mode").textContent.includes("SAVED"), "Completed run is not labeled saved");
    for (const endpoint of ["snapshot", "events", "notes", "agents", "notes-failure"]) {
      blocked = { path: `/api/runs/B/${endpoint.replace("-failure", "")}`, fail: endpoint.endsWith("-failure") };
      select("B");
      check(!get("nations").children.length && !get("run-notes").children.length, "Old panels survived run selection");
      check(!get("constellation-countries").children.length, "Old constellation survived run selection");
      check(get("constellation-panel").dataset.ambientNodes==="0","Old ambient particles survived run selection");
      check(get("network-event").hidden && Number(get("constellation-panel").dataset.signalEvents)===0,"Old event ripples survived run selection");
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
    check(get("constellation-countries").children.length === 5, "Constellation failed to switch to a smaller legacy world");
    await wait(()=>get("constellation-panel").dataset.citizenCheckpoint==="ready");
    get("constellation-countries").firstChild.click();
    check(get("constellation-detail").textContent.includes("93%") && get("constellation-detail").textContent.includes("checkpoint day 16"),"Legacy renderer checkpoint did not provide exact citizen needs with its checkpoint day");
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
  const web = process.env.GEARTH_OBSERVER_WEB_ROOT || path.join(root, "backend/GEarth.Api/wwwroot");
  const fixture = fs.readFileSync(path.join(root, "docs/gearth/fixtures/stage2-api-sample.json"), "utf8");
  const html = fs.readFileSync(path.join(web, "index.html"), "utf8").replace(/\/observer\.[a-f0-9]+\.js/g,"/observer.js").replace(
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
    const files = { "/observer.js": "text/javascript", "/style.css": "text/css", "/constellation.js":"text/javascript", "/constellation.css":"text/css" };
    const asset=new URL(request.url,"http://localhost").pathname.replace(/\/(constellation|observer)\.[a-f0-9]+\.(js|css)/g,"/$1.$2");
    response.setHeader("Content-Type", files[asset] ?? "text/html");
    response.end(files[asset] ? fs.readFileSync(path.join(web, asset)) : html);
  });
  await new Promise((resolve) => server.listen(0, "127.0.0.1", resolve));
  const profile = fs.mkdtempSync(path.join(os.tmpdir(), "gearth-browser-"));
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
      console.log(`Passed ${name}: abstract network/ambient density, needs/country colors, trade direction/quantity, diplomacy/conflict traces, country focus, needs totals, presentation fade/wake, recorded event ripples/deduplication, motion, run switching, observer filters and pagination`);
      console.log("Screenshot: " + screenshot);
    }
  } finally {
    server.close();
    assert.equal(path.dirname(path.resolve(profile)), path.resolve(os.tmpdir()));
    fs.rmSync(profile, { recursive: true, force: true, maxRetries: 3 });
  }
}

main().catch((error) => { console.error(error.message); process.exitCode = 1; });
