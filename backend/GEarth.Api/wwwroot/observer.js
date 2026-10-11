const element = (id) => document.getElementById(id);
const requestedRunId = new URLSearchParams(location.search).get("run_id");
const initialRunId = requestedRunId && /^[A-Za-z0-9_-]{1,100}$/.test(requestedRunId) ? requestedRunId : null;
let selectedRun = initialRunId;
let selectedAgent = null;
let lastSequence = 0;
let displayedEvents = [];
let routineEvents = [];
let eventSnapshot = null;
let hiddenEventTypes = new Set();
let busy = false;
let agentOffset = 0;
let agentTotal = 0;
let followCurrent = !initialRunId;
let notesCache = null;
let analyticsReport = null;
let refreshController = null;
let profileController = null;
let viewGeneration = 0;

async function getJson(path, signal) {
  const response = await fetch(path, { signal });
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
      (route) => route.id === cargo.plan?.edge_ids?.[cargo.edge_index],
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

// ---- World events feed: plain sentences instead of raw event types ----

let countryNames = new Map();
const SECTORS = ["food", "energy", "tools", "medicine", "entertainment", "clinic", "school", "freight", "bank"];
// Owner/business moves worth reading; everything else an agent does shows up through its own engine event
// (shipment_dispatched, loan_requested, business_started, policy_enacted, plan_set, enrolled, ...).
const SHOWN_ACTIONS = new Set(["set_price", "set_wage", "set_headcount", "set_deposit_rate", "set_loan_rate", "set_lending_standard"]);
const HIDDEN_EVENTS = new Set([
  "decision_requested", "decision_canceled", "decision_accounting_started", "goods_purchased",
  "payment_sent", "policy_proposed", "shipment_delivered", "labor_market",
  "bank_ledger",
]);

function newsworthy(event) {
  if (HIDDEN_EVENTS.has(event.event_type)) return event.event_type === "labor_market"; // kept for the weekly line
  if (event.event_type === "bank_week") return Number(event.payload?.unmet_withdrawals_minor) > 0;
  if (event.event_type === "action_accepted")
    return SHOWN_ACTIONS.has(event.payload?.action?.action_type);
  if (event.event_type === "action_rejected")
    return !event.entity_ids?.[0]?.includes("_citizen_");
  return true;
}

const wld = (minor) =>
  `${(Number(minor ?? 0) / 100).toLocaleString("en-US", { minimumFractionDigits: 2, maximumFractionDigits: 2 })} WLD`;
const signedWld = (minor) => `${Number(minor) < 0 ? "−" : "+"}${wld(Math.abs(Number(minor)))}`;
const cap = (text) => (text ? text[0].toUpperCase() + text.slice(1) : "");
const bare = (verbPhrase) => verbPhrase.replace(/^(\w+)s /, "$1 "); // "raises prices" → "raise prices"
const plural = (n, word) => `${n} ${word}${n === 1 ? "" : "s"}`;
const countryOf = (id) => String(id ?? "").split(/[_:]/)[0];
const countryName = (id) => countryNames.get(countryOf(id)) ?? cap(countryOf(id));

// aster_food_2 → "Food firm #2", aster_entertainment_firm151 → "Entertainment startup #151", aster_bank → "Bank".
function who(id) {
  if (!id) return "Someone";
  const rest = String(id).split("_").slice(1);
  const [kind, suffix] = rest;
  if (kind === "government") return "Government";
  if (kind === "journalist") return "Journalist";
  if (kind === "citizen") return "A resident";
  if (kind === "bank") return "Bank";
  if (SECTORS.includes(kind)) {
    const base = kind === "clinic" || kind === "school" ? cap(kind) : `${cap(kind)} firm`;
    if (!suffix) return base;
    return suffix.startsWith("firm") ? `${cap(kind)} startup #${suffix.slice(4)}` : `${base} #${suffix}`;
  }
  return String(id);
}
const sectorOf = (id) => String(id ?? "").split("_")[1];

function policyText(action, params = {}) {
  switch (action) {
    case "adjust_tax":
      if (params.income_tax_percent !== undefined) return `sets income tax to ${params.income_tax_percent}%`;
      if (params.change_basis_points !== undefined) {
        const points = params.change_basis_points / 100;
        return `${points < 0 ? "cuts" : "raises"} income tax by ${Math.abs(points)} point${Math.abs(points) === 1 ? "" : "s"}`;
      }
      return "changes income tax";
    case "set_sales_tax": return `sets sales tax to ${params.sales_tax_basis_points / 100}%`;
    case "fund_services": return `sets the services budget to ${wld(params.amount_minor)} per resident a week`;
    case "set_welfare": return `sets welfare to ${wld(params.amount_minor)} a week`;
    case "set_unemployment_benefit": return `sets unemployment benefit to ${wld(params.amount_minor)} a week`;
    case "subsidize_food": return `subsidizes food by ${params.percent}%`;
    case "subsidize_tuition": return `subsidizes school tuition by ${params.percent}%`;
    case "student_stipend": return params.on ? "starts paying students a stipend" : "stops the student stipend";
    case "hiring_bonus": return `offers firms ${wld(params.amount_minor)} for each graduate hired`;
    case "subsidize_startup": return `pays to start a new ${params.sector} firm`;
    case "set_border": return `${params.open ? "opens" : "closes"} the ${params.edge_id} border`;
    case "propose_agreement": return `proposes a trade agreement to ${countryName(params.country_id)}`;
    case "offer_peace": return `offers peace to ${countryName(params.country_id)}`;
    case "respond_agreement": return `${params.accept ? "accepts" : "rejects"} ${countryName(params.country_id)}'s offer`;
    case "declare_conflict": return `DECLARES CONFLICT on ${countryName(params.country_id)}`;
    case "pay": case "invest": return `${action === "pay" ? "pays" : "invests"} ${wld(params.amount_minor)}`;
    default: return `enacts ${action}`;
  }
}

function actionText(action) {
  const p = action.params ?? {};
  switch (action.action_type) {
    case "set_price": return `${p.change_pct < 0 ? "cuts" : "raises"} prices ${Math.abs(p.change_pct)}%`;
    case "set_wage": return `${p.change_pct < 0 ? "cuts" : "raises"} wages ${Math.abs(p.change_pct)}%`;
    case "set_headcount": return `${p.change < 0 ? "cuts" : "adds"} ${plural(Math.abs(p.change), "job")}`;
    case "set_deposit_rate": return `sets the savings rate to ${p.rate_pct}% a year`;
    case "set_loan_rate": return `sets the loan rate to ${p.rate_pct}% a year`;
    case "set_lending_standard": return `makes lending ${p.standard}`;
    case "import_goods": return `orders ${p.quantity} ${p.good} from ${countryName(p.seller_id)}`;
    default: return action.action_type;
  }
}

// Weekly line per country: economy_cycle plus that week's labor_market for the same country and day.
function weekText(event, labor) {
  const p = event.payload;
  const parts = [];
  if (p.hungry_residents) parts.push(`${p.hungry_residents} hungry` + (p.priced_out_residents ? ` (${p.priced_out_residents} priced out of food)` : ""));
  parts.push(`${p.unemployed ?? 0} unemployed`);
  if (labor?.layoffs) parts.push(`${labor.layoffs} laid off`);
  if (labor?.hires) parts.push(`${labor.hires} hired`);
  if (p.sick_residents) parts.push(`${p.sick_residents} too sick to work`);
  if (p.students) parts.push(`${p.students} studying`);
  const net = Number(p.government_income_minor ?? 0) - Number(p.government_spending_minor ?? 0);
  parts.push(`budget ${signedWld(net)} (in ${wld(p.government_income_minor)}, out ${wld(p.government_spending_minor)})`);
  if (labor?.dividends_minor) parts.push(`owners took ${wld(labor.dividends_minor)} in dividends`);
  return parts.join(", ");
}

// Returns { country, text, body?, key?, many?(events), alert? }. key groups same-day repeats into one line.
function describe(event, labor) {
  const p = event.payload ?? {};
  const ids = event.entity_ids ?? [];
  const place = countryName(ids[0]);
  switch (event.event_type) {
    case "economy_cycle":
      return { country: place, text: `Week ${Math.floor(event.tick / 7) + 1} summary: ${weekText(event, labor)}` };
    case "action_accepted": {
      const action = p.action;
      const firm = (item) => item.payload.action.params?.business_id ?? item.entity_ids?.[0]; // an owner acts for their firm
      const text = `${who(firm(event))} ${actionText(action)}`;
      return {
        country: place, text, body: action.reason ? `“${action.reason}”` : "",
        key: `${action.action_type}:${JSON.stringify({ ...action.params, business_id: undefined })}`,
        many: (events) => {
          const bySector = {};
          for (const item of events) bySector[sectorOf(firm(item))] = (bySector[sectorOf(firm(item))] ?? 0) + 1;
          const countries = new Set(events.map((item) => countryName(item.entity_ids?.[0])));
          return `${events.length} firms in ${countries.size === 1 ? "1 country" : `${countries.size} countries`} ${bare(actionText(action))} (${Object.entries(bySector).map(([s, n]) => `${s} ×${n}`).join(", ")})`;
        },
      };
    }
    case "action_rejected": {
      const action = typeof p.action === "string" ? { action_type: p.action } : (p.action ?? {});
      return {
        country: place, alert: true,
        text: `${who(ids[0])} tried to ${actionText(action)}, refused (${(p.code ?? "rejected").toLowerCase().replaceAll("_", " ")})`,
        key: `rejected:${action.action_type}:${p.code}`,
        many: (events) => `${events.length} attempts to ${action.action_type?.replaceAll("_", " ")} were refused (${(p.code ?? "").toLowerCase().replaceAll("_", " ")})`,
      };
    }
    case "policy_enacted":
    case "policy_blocked": {
      const action = typeof p.action === "string" ? p.action : p.action?.action_type;
      const params = p.params ?? p.action?.params;
      const blocked = event.event_type === "policy_blocked";
      const reason = p.reason && !/^[A-Z_]+$/.test(p.reason) ? p.reason : "";
      return {
        country: place, alert: blocked,
        text: `Government ${blocked ? "was blocked: it tried to " + bare(policyText(action, params)) : policyText(action, params)}`,
        body: reason,
      };
    }
    case "plan_set":
      return { country: place, text: `Planner sets weekly quotas: ${Object.entries(p.plan ?? {}).map(([good, n]) => `${good} ${n}`).join(", ")}` };
    case "public_report":
      return { country: place, text: "News report", body: p.text };
    case "shipment_dispatched": {
      const via = (p.plan?.edge_ids ?? []).map((edge) => countryName(edge.split("_to_")[1])).slice(0, -1);
      return {
        country: countryName(ids[1]),
        text: `${who(ids[1])} imports ${p.quantity} ${p.good} from ${countryName(ids[2])}: arrives in ${plural(p.plan?.travel_ticks ?? 0, "day")}` +
          (via.length ? ` via ${via.join(", ")}` : ""),
        body: `Goods ${wld(p.escrow_minor)} + tariff ${wld(p.tariff_minor)} + shipping ${wld(p.shipping_minor)}${p.ordered_by === "agent decision" ? "" : " · automatic order"}`,
        key: `import:${countryOf(ids[1])}`,
        many: (events) => {
          const goods = {};
          let cost = 0;
          for (const item of events) {
            const q = item.payload;
            const label = `${q.good} from ${countryName(item.entity_ids?.[2])}`;
            goods[label] = (goods[label] ?? 0) + Number(q.quantity ?? 0);
            cost += Number(q.escrow_minor ?? 0) + Number(q.tariff_minor ?? 0) + Number(q.shipping_minor ?? 0);
          }
          return `${countryName(ids[1])} firms place ${events.length} import orders: ${Object.entries(goods).map(([label, n]) => `${n} ${label}`).join(", ")} (total ${wld(cost)})`;
        },
      };
    }
    case "shipment_failed":
      return { country: "", alert: true, text: `A shipment of ${p.quantity} ${p.good} never arrived and was written off` };
    case "shipment_rerouted":
      return { country: "", text: `A shipment was rerouted around a closed border` };
    case "business_started":
      return {
        country: place, text: `A resident opens a new ${p.sector} business, investing ${wld(p.invested_minor)}`,
        body: `Opens at ${wld(p.launch_price_minor)} a unit${p.chosen_by === "agent decision" ? "" : ` · ${p.chosen_by}`}`,
      };
    case "business_closed":
      return {
        country: countryName(ids[1]), alert: true,
        text: `${who(ids[0])} closes: ${p.reason}`,
        body: `${wld(p.invested_minor)} invested, ${wld(p.recovered_minor)} returned to the owner, ${plural(p.workers_laid_off ?? 0, "worker")} laid off`,
      };
    case "startup_grant":
      return { country: countryName(ids[1]), text: `Government pays ${wld(p.grant_minor)} to open a new ${p.sector} firm` };
    case "enrolled":
      return {
        country: place,
        text: `A resident enrolls in school${p.part_time ? " part-time" : ""}${p.quit_employer ? `, quitting ${who(p.quit_employer)}` : ""}`,
        body: `Tuition ${wld(p.tuition_minor)} (paid ${wld(p.paid_by_student_minor)} themselves${p.student_loan ? ", rest on a student loan" : ""}) · graduates day ${p.graduates_tick}`,
      };
    case "graduated":
      return { country: place, text: `A ${p.occupation?.replaceAll("_", " ") ?? "resident"} graduates and now produces ${Math.round(((p.output_boost ?? 1) - 1) * 100)}% more` };
    case "agent_migrated":
      return { country: countryName(p.from), text: `A ${p.occupation?.replaceAll("_", " ") ?? "resident"} emigrates to ${countryName(p.to)}`, key: `migrate:${p.from}:${p.to}`,
        many: (events) => `${events.length} residents emigrate to ${countryName(p.to)}` };
    case "election_result":
      return {
        country: place, alert: p.replaced,
        text: p.replaced ? `Leadership is voted out at ${Math.round(p.approval * 100)}% approval` : `Leadership holds on at ${Math.round(p.approval * 100)}% approval`,
        body: `Next election day ${p.next_election_tick}`,
      };
    case "resource_crisis":
      return { country: place, alert: true, text: `${(p.reason ?? "crisis").toUpperCase()}: ${p.good} output falls to ${Math.round(p.production_multiplier * 100)}% until day ${p.until_tick}` };
    case "diplomatic_tension": {
      const [a, b] = String(ids[0]).split(":");
      return { country: countryName(a), text: p.score != null ? `Relations between ${countryName(a)} and ${countryName(b)} fall to ${p.score}` : `Tension between ${countryName(a)} and ${countryName(b)} rises to ${p.tension}`, body: p.reason };
    }
    case "diplomatic_offer":
      return { country: place, text: `${countryName(p.proposed_by ?? ids[0])} offers ${p.proposal_kind === "peace" ? "peace" : "a trade agreement"}` };
    case "diplomatic_response":
      return { country: "", text: `An offer is ${p.accept ? "accepted" : "rejected"}${p.trade_agreement ? ": trade agreement in force" : ""}${p.at_war ? " (still at war)" : ""}` };
    case "conflict_started":
      return { country: countryName(p.first), alert: true, text: `CONFLICT: ${countryName(p.first)} and ${countryName(p.second)} are at war` };
    case "conflict_costs":
      return { country: place, alert: true, text: p.funded_upkeep_minor != null ? `Soldiers receive ${wld(p.funded_upkeep_minor)} in war upkeep (${plural(p.conflicts ?? 0, "conflict")})` : `War costs this week (${plural(p.conflicts ?? 0, "conflict")}), ${p.lost_units ?? 0} units of goods lost so far` };
    case "war_week":
      return { country: countryName(p.winner), alert: true, text: `${countryName(p.winner)} wins the weekly contest against ${countryName(p.loser)}`,
        body: `Tribute ${wld(p.tribute_minor)} and ${Object.entries(p.tribute_goods ?? {}).map(([good, units]) => `${units} ${good}`).join(", ")}; ${plural(p.wounded ?? 0, "soldier")} wounded` };
    case "war_ended":
      return { country: countryName(p.winner), text: `War ends with ${countryName(p.loser)}: ${String(p.terms).replaceAll("_", " ")}`, body: p.reason };
    case "gift_sent":
      return { country: place, text: `${countryName(ids[0])} sends ${p.kind === "cash" ? wld(p.amount) : `${p.amount} ${p.kind}`} to ${countryName(ids[1])}`,
        body: `Relations +${p.relation_gain}, now ${p.score}` };
    case "missile_ready":
      return { country: place, text: `Missile completed; ${p.missiles} in stock` };
    case "missile_strike":
      return { country: countryName(p.target), alert: true, text: `${countryName(p.attacker)} launches a missile: ${p.hit ? p.damaged ? `${p.damaged} condition falls to ${p.condition}` : "hits, but no completed project can be damaged" : "intercepted"}` };
    case "soldiers_recruited":
    case "soldiers_discharged":
      return { country: place, text: `${p.recruited ?? p.discharged} soldiers ${event.event_type === "soldiers_recruited" ? "recruited" : "discharged"}; ${p.soldiers} total` };
    case "unrest_started":
      return { country: place, alert: true, text: `Unrest begins: average quality of life ${p.average_quality_of_life}, below ${p.threshold}` };
    case "early_election_called":
      return { country: place, alert: true, text: `Unrest calls an early election on day ${p.next_election_tick}` };
    case "revolution":
      return { country: place, alert: true, text: `Revolution changes government from ${p.from} to ${p.to}`, body: `${p.paid_soldiers} paid soldiers for ${p.residents} residents` };
    case "revolution_deferred":
      return { country: place, alert: true, text: "Revolution deferred: ownership transition is unsupported", body: p.reason };
    case "unrest_suppressed":
      return { country: place, alert: true, text: `Unrest suppressed by ${p.paid_soldiers} paid soldiers` };
    case "border_changed":
      return { country: "", text: `Border ${p.edge_id} ${p.open ? "opens" : "closes"}` };
    case "loan_requested":
      return { country: place, text: `${who(ids[1])} asks the bank for ${wld(p.amount_minor)} to ${p.purpose}` };
    case "loan_funded":
      return {
        country: place, text: `Bank lends ${wld(p.principal_minor)} (${p.kind} loan) at ${p.rate_pct}%`,
        body: `${wld(p.weekly_payment_minor)} a week for ${p.weeks} weeks`,
      };
    case "loan_rejected":
      return { country: place, alert: true, text: `Bank turns down a ${wld(p.amount_minor)} loan`, body: p.reason };
    case "loan_settled":
      return {
        country: place, alert: p.status === "defaulted",
        text: p.status === "defaulted" ? `A loan defaults: the bank loses ${wld(p.loss_minor)}` : "A loan is repaid in full",
        key: `settled:${p.status}`,
        many: (events) => p.status === "defaulted"
          ? `${events.length} loans default, the banks lose ${wld(events.reduce((sum, item) => sum + Number(item.payload.loss_minor ?? 0), 0))}`
          : `${events.length} loans are repaid in full`,
      };
    case "loan_guaranteed":
      return { country: place, text: `Government covers a defaulted student loan: ${wld(p.paid_minor)}` };
    case "owner_debt":
      return { country: place, alert: true, text: `A failed firm's owner now personally owes the bank ${wld(p.amount_minor)}`, body: `${wld(p.weekly_payment_minor)} a week` };
    case "treasury_borrowed":
      return { country: place, alert: true, text: `Treasury borrows ${wld(p.amount_minor)} from the state bank`, body: `Now owes ${wld(p.owed_minor)}, repaying ${wld(p.weekly_payment_minor)} a week` };
    case "savings_withdrawn":
      return {
        country: place, text: `A resident takes ${wld(p.received_minor)} of savings out of the bank`, key: "withdraw",
        many: (events) => `${events.length} residents take ${wld(events.reduce((sum, item) => sum + Number(item.payload.received_minor ?? 0), 0))} out of the bank` +
          (events.some((item) => item.payload.still_owed_minor > 0) ? ", some could not get all of it" : ""),
      };
    case "bank_week":
      return { country: place, alert: true, text: `Bank cannot pay ${wld(p.unmet_withdrawals_minor)} of withdrawals this week`, body: `Reserves ${wld(p.reserves_minor)}, deposits ${wld(p.deposits_minor)}, loans ${wld(p.loans_minor)}` };
    case "bank_failed":
      return { country: place, alert: true, text: `BANK FAILURE: the state takes over the bank`, body: `Deposits ${wld(p.deposits_minor)}, equity ${wld(p.equity_minor)}` };
    case "bank_ledger":
      return {
        country: place, text: `Bank records ${wld(p.amount_minor)}: ${(p.kind ?? "transfer").replaceAll("_", " ")}`,
        body: `${p.from ?? "Bank"} → ${p.to ?? "Bank"}`,
        key: `${countryOf(p.bank_id)}:${p.kind}`,
        many: (events) => `${countryName(p.bank_id)} bank records ${events.length} ${(p.kind ?? "transfer").replaceAll("_", " ")} entries totaling ${wld(events.reduce((sum, item) => sum + Number(item.payload?.amount_minor ?? 0), 0))}`,
      };
    default:
      return { country: "", text: cap(event.event_type.replaceAll("_", " ")), body: p.reason ?? "" };
  }
}

function showEvents(snapshot) {
  eventSnapshot = snapshot;
  const list = element("events");
  list.replaceChildren();
  countryNames = new Map((snapshot.countries ?? []).map((country) => [country.id, country.name]));
  const labor = new Map(
    displayedEvents.filter((event) => event.event_type === "labor_market")
      .map((event) => [`${event.tick}:${event.entity_ids?.[0]}`, event.payload]),
  );
  // Newest first; same-day events with the same key collapse into one line.
  const lines = [];
  const groups = new Map();
  const country = element("event-country").value;
  const search = element("event-search").value.trim().toLowerCase();
  const showRoutine = element("event-routine").checked;
  const history = [...displayedEvents, ...routineEvents].sort((a, b) => b.seq - a.seq);
  let matching = 0;
  for (const event of history) {
    if ((!showRoutine && !newsworthy(event)) || hiddenEventTypes.has(event.event_type)
        || (!showRoutine && event.event_type === "labor_market")) continue;
    if (country && !(event.entity_ids ?? []).some((id) => countryOf(id) === country)
        && ![event.payload?.from, event.payload?.to, event.payload?.country_id].some((id) => countryOf(id) === country)) continue;
    const line = describe(event, labor.get(`${event.tick}:${event.entity_ids?.[0]}`));
    if (search && !`${line.text} ${line.body ?? ""} ${JSON.stringify(event)}`.toLowerCase().includes(search)) continue;
    matching += 1;
    const groupKey = line.key && `${event.tick}:${event.event_type}:${line.key}`;
    if (groupKey && groups.has(groupKey)) {
      groups.get(groupKey).events.push(event);
      continue;
    }
    const entry = { line, events: [event] };
    if (groupKey) groups.set(groupKey, entry);
    lines.push(entry);
  }
  element("event-count").textContent = `${matching} matching · ${snapshot.seq.toLocaleString()} recorded`;
  element("event-scope").textContent = `Recent history: ${displayedEvents.filter((event) => event.event_type !== "labor_market").length} highlights and ${routineEvents.length} routine events retained. Showing up to 60 grouped entries. Routine activity includes bank ledger entries and decision bookkeeping.`;
  if (!lines.length) list.append(textNode("li", "No events match these filters in the retained recent history.", "muted"));
  for (const { line, events } of lines.slice(0, 60)) {
    const event = events[0];
    const grouped = events.length > 1;
    const item = document.createElement("li");
    item.append(
      textNode("div", `Day ${event.tick}${!grouped && line.country ? ` · ${line.country}` : ""}`, "event-meta"),
      textNode("div", grouped ? line.many(events) : line.text, line.alert ? "event-title event-rejected" : "event-title"),
    );
    if (!grouped && line.body) item.append(textNode("p", line.body, "event-body"));
    const raw = document.createElement("details");
    raw.className = "event-raw";
    raw.append(textNode("summary", "raw"), textNode("pre", JSON.stringify(grouped ? events : event, null, 2)));
    item.append(raw);
    list.append(item);
  }
}

function showEventFilters(snapshot) {
  const select = element("event-country");
  const country = select.value;
  select.replaceChildren(textNode("option", "All countries"));
  select.firstChild.value = "";
  for (const nation of snapshot.countries ?? []) {
    const option = textNode("option", nation.name);
    option.value = nation.id;
    select.append(option);
  }
  select.value = country;
  const counts = new Map();
  for (const event of [...displayedEvents, ...routineEvents]) counts.set(event.event_type, (counts.get(event.event_type) ?? 0) + 1);
  const types = element("event-types");
  types.replaceChildren();
  for (const [type, count] of [...counts].sort(([a], [b]) => a.localeCompare(b))) {
    const label = document.createElement("label");
    const checkbox = document.createElement("input");
    checkbox.type = "checkbox";
    checkbox.value = type;
    checkbox.checked = !hiddenEventTypes.has(type);
    checkbox.addEventListener("change", () => {
      if (checkbox.checked) hiddenEventTypes.delete(type); else hiddenEventTypes.add(type);
      saveEventFilters();
      showEvents(eventSnapshot);
    });
    label.append(checkbox, textNode("span", `${cap(type.replaceAll("_", " "))} (${count})`));
    types.append(label);
  }
}

function saveEventFilters() {
  try {
    localStorage.setItem("gearth.eventFilters", JSON.stringify({
      hidden: [...hiddenEventTypes], routine: element("event-routine").checked,
      search: element("event-search").value,
    }));
  } catch { /* Filtering still works when browser storage is unavailable. */ }
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
      clearProfile();
      await showProfile();
    });
    list.append(button);
  }
}

function clearProfile() {
  profileController?.abort();
  element("profile").replaceChildren(
    textNode(
      "p",
      selectedAgent
        ? "Loading agent profile…"
        : "Select an agent to inspect its profile.",
      "muted",
    ),
  );
}

async function showProfile() {
  if (!selectedAgent || !selectedRun) return;
  if (profileController && !profileController.signal.aborted) return;
  const controller = new AbortController();
  profileController = controller;
  const runId = selectedRun;
  const agentId = selectedAgent;
  const generation = viewGeneration;
  const isCurrent = () =>
    !controller.signal.aborted &&
    generation === viewGeneration &&
    selectedRun === runId &&
    selectedAgent === agentId;
  try {
    const data = await getJson(
      `/api/runs/${encodeURIComponent(runId)}/agents/${encodeURIComponent(agentId)}`,
      controller.signal,
    );
    if (!isCurrent()) return;
    const profile = element("profile");
    profile.replaceChildren(
      textNode("h3", data.agent.id),
      textNode("p", data.agent.goal),
    );
    profile.append(
      textNode(
        "p",
        `Food: ${data.agent.inventory?.food ?? 0} · Energy: ${data.agent.inventory?.energy ?? 0} · ${(data.agent.cash_minor / 100).toFixed(2)} WLD`,
      ),
    );
    if (data.agent.last_model_decision_at) {
      const age =
        data.model_decision_age_seconds ??
        Math.max(
          0,
          (new Date(data.updated_at) -
            new Date(data.agent.last_model_decision_at)) /
            1000,
        );
      profile.append(
        textNode(
          "p",
          `Accepted model turns: ${data.agent.model_decisions} · decision age ${Math.round(age)}s at checkpoint`,
        ),
      );
    }
    const traits = document.createElement("div");
    traits.className = "traits";
    for (const [name, value] of Object.entries(data.agent.traits ?? {})) {
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
    for (const memory of data.agent.memories ?? [])
      memories.append(textNode("li", memory));
    profile.append(memories);
    const analysis = analyticsReport?.agents?.find((item) =>
      (item.agent_id ?? item.id) === agentId);
    if (analysis) {
      const details = document.createElement("details");
      details.append(textNode("summary", "Investigate imported agent analysis"),
        textNode("pre", JSON.stringify(analysis, null, 2)));
      profile.append(details);
    }
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
    if (isCurrent()) {
      element("profile").replaceChildren(
        textNode("p", error.message, "event-rejected"),
      );
    }
  } finally {
    if (profileController === controller) profileController = null;
  }
}

function clearRunView() {
  document.dispatchEvent(new CustomEvent("gearth-view-cleared"));
  for (const id of [
    "map",
    "events",
    "nations",
    "shipments",
    "relations",
    "agents",
    "run-notes",
    "analytics-summary",
    "analytics-checks",
    "analytics-country-list",
  ])
    element(id).replaceChildren();
  for (const id of ["tick", "population", "coverage", "decisions", "failures"])
    element(id).textContent = "—";
  element("world-details").hidden = true;
  element("compute").textContent = "Loading compute telemetry…";
  element("route-plan").textContent = "Loading trade corridors…";
  element("mode").textContent = "Waiting";
  element("event-count").textContent = "Loading events…";
  element("shipping-count").textContent = "";
  element("agent-page").textContent = "Loading agents…";
  element("updated").textContent = "Waiting for committed state";
  element("message").textContent = "";
  element("notes-status").textContent = "Loading run observations…";
  element("notes-download").hidden = true;
  element("notes-download").removeAttribute("href");
  element("analytics-status").textContent = "Loading imported analytics…";
  element("analytics-download").hidden = true;
  element("analytics-download").removeAttribute("href");
  element("analytics-countries").hidden = true;
  element("previous-page").disabled = true;
  element("next-page").disabled = true;
  clearProfile();
}

function selectRun(runId, cancelRefresh = true) {
  if (cancelRefresh) refreshController?.abort();
  viewGeneration += 1;
  selectedRun = runId;
  element("data-explorer-link").href = `/data.html?run_id=${encodeURIComponent(runId)}`;
  document.dispatchEvent(new CustomEvent("gearth-run-selected", {detail:{runId}}));
  selectedAgent = null;
  agentOffset = 0;
  agentTotal = 0;
  lastSequence = 0;
  displayedEvents = [];
  routineEvents = [];
  eventSnapshot = null;
  element("event-country").value = "";
  element("event-types").replaceChildren();
  element("event-scope").textContent = "";
  notesCache = null;
  analyticsReport = null;
  element("country-filter").value = "";
  element("role-filter").value = "";
  element("agent-search").value = "";
  element("connection").textContent = "Loading selected run";
  clearRunView();
}

function requestRefresh() {
  refreshController?.abort();
  profileController?.abort();
  viewGeneration += 1;
  busy = false;
  return refresh();
}

async function refresh() {
  if (busy) return;
  busy = true;
  const controller = new AbortController();
  refreshController = controller;
  let generation = viewGeneration;
  const isCurrent = () =>
    !controller.signal.aborted && generation === viewGeneration;
  try {
    const [health, runs] = await Promise.all([
      getJson("/api/health", controller.signal),
      getJson("/api/runs", controller.signal),
    ]);
    if (!isCurrent()) return;
    const target =
      followCurrent && runs.current_run_id
        ? runs.current_run_id
        : (selectedRun ?? runs.current_run_id ?? runs.run_ids[0] ?? null);
    if (target !== selectedRun) {
      selectRun(target, false);
      generation = viewGeneration;
    }
    element("data-explorer-link").href = selectedRun ? `/data.html?run_id=${encodeURIComponent(selectedRun)}` : "/data.html";
    element("runs").replaceChildren();
    const selectableIds = selectedRun && !runs.run_ids.includes(selectedRun) ? [selectedRun, ...runs.run_ids] : runs.run_ids;
    for (const id of selectableIds) {
      const option = textNode("option", id);
      option.value = id;
      element("runs").append(option);
    }
    element("runs").value = selectedRun ?? "";
    if (!selectedRun) {
      element("connection").textContent = "Initializing world";
      return;
    }
    const runId = selectedRun;
    const path = `/api/runs/${encodeURIComponent(runId)}`;
    const snapshot = await getJson(`${path}/snapshot`, controller.signal);
    if (!isCurrent()) return;
    const query = new URLSearchParams({
      country: element("country-filter").value,
      role: element("role-filter").value,
      q: element("agent-search").value,
      offset: agentOffset,
      limit: 50,
    });
    const [history, notes, page, analysis] = await Promise.all([
      loadEvents(path, snapshot, controller.signal),
      loadRunNotes(path, snapshot, health, controller.signal),
      getJson(`${path}/agents?${query}`, controller.signal),
      loadAnalyticsReport(path, snapshot, controller.signal),
    ]);
    if (!isCurrent()) return;
    displayedEvents = history.events;
    routineEvents = history.routine;
    lastSequence = history.sequence;
    notesCache = notes;
    analyticsReport = analysis.report;
    showAnalyticsReport(runId, analysis);
    element("tick").textContent = snapshot.tick;
    element("population").textContent = snapshot.agents_summary.spawned;
    element("coverage").textContent =
      `${snapshot.agents_summary.unique_model_deciding}/${snapshot.agents_summary.spawned}`;
    element("decisions").textContent = snapshot.metrics.model_decision_count;
    element("failures").textContent =
      `${snapshot.metrics.fallback_decision_count} / ${snapshot.metrics.rejected_decision_count}`;
    element("mode").textContent =
      `${snapshot.run.status !== "running" || runId !== runs.current_run_id ? "SAVED · " : ""}${snapshot.run.mode} · ${snapshot.run.status.toUpperCase()}`;
    const paused = health.run_id === runId && health.status === "paused";
    element("connection").textContent =
      paused ? "Paused" : `Connected · ${health.storage}`;
    element("updated").textContent =
      `Last commit ${new Date(snapshot.updated_at).toLocaleTimeString()}`;
    const plan = snapshot.shipments[0]?.plan;
    element("route-plan").textContent = plan
      ? `${plan.objective} plan · ${plan.travel_ticks} travel ticks · ${(plan.cost_minor / 100).toFixed(2)} WLD`
      : "No active medicine route plan.";
    element("message").textContent = paused ? health.error ?? "" : "";
    drawMap(snapshot);
    document.dispatchEvent(new CustomEvent("gearth-snapshot", { detail: { snapshot, events: displayedEvents } }));
    showEventFilters(snapshot);
    showEvents(snapshot);
    showWorld(snapshot);
    showRunNotes(runId, notes);
    showAgentPage(snapshot, page);
    showProfile();
  } catch (error) {
    if (isCurrent()) {
      clearRunView();
      lastSequence = 0;
      displayedEvents = [];
      routineEvents = [];
      eventSnapshot = null;
      element("event-types").replaceChildren();
      element("event-scope").textContent = "";
      notesCache = null;
      element("connection").textContent = "Connection interrupted";
      element("message").textContent = error.message;
    }
  } finally {
    if (refreshController === controller) {
      refreshController = null;
      busy = false;
    }
  }
}

async function loadEvents(path, snapshot, signal) {
  let sequence = lastSequence;
  if (sequence === 0 && snapshot.run.stage === 2)
    sequence = Math.max(0, snapshot.seq - 2000); // a few weeks back once the noise is filtered, so the first view has weekly summaries
  let history = displayedEvents.slice();
  let routine = routineEvents.slice();
  let page;
  do {
    page = await getJson(
      `${path}/events?after_seq=${sequence}&through_seq=${snapshot.seq}&limit=500`,
      signal,
    );
    page = page.filter(
      (event) => event.seq > sequence && event.seq <= snapshot.seq,
    );
    history.push(...page.filter(newsworthy));
    routine.push(...page.filter((event) => !newsworthy(event)));
    if (page.length) sequence = page.at(-1).seq;
  } while (page.length === 500 && sequence < snapshot.seq);
  return { events: history.slice(-500), routine: routine.slice(-2000), sequence };
}

try {
  const filters = JSON.parse(localStorage.getItem("gearth.eventFilters") ?? "null");
  if (filters) {
    hiddenEventTypes = new Set(Array.isArray(filters.hidden) ? filters.hidden.filter((type) => typeof type === "string") : []);
    element("event-routine").checked = filters.routine === true;
    element("event-search").value = typeof filters.search === "string" ? filters.search : "";
  }
} catch { /* Ignore missing or invalid saved preferences. */ }
for (const id of ["event-country", "event-search", "event-routine"]) {
  element(id).addEventListener(id === "event-search" ? "input" : "change", () => {
    saveEventFilters();
    if (eventSnapshot) showEvents(eventSnapshot);
  });
}
element("event-reset").addEventListener("click", () => {
  hiddenEventTypes.clear();
  element("event-country").value = "";
  element("event-search").value = "";
  element("event-routine").checked = false;
  saveEventFilters();
  if (eventSnapshot) { showEventFilters(eventSnapshot); showEvents(eventSnapshot); }
});

element("runs").addEventListener("change", () => {
  followCurrent = false;
  selectRun(element("runs").value);
  requestRefresh();
});

for (const id of ["country-filter", "role-filter", "agent-search"]) {
  element(id).addEventListener(
    id === "agent-search" ? "input" : "change",
    () => {
      agentOffset = 0;
      selectedAgent = null;
      clearProfile();
      requestRefresh();
    },
  );
}
element("previous-page").addEventListener("click", () => {
  agentOffset = Math.max(0, agentOffset - 50);
  requestRefresh();
});
element("next-page").addEventListener("click", () => {
  if (agentOffset + 50 < agentTotal) agentOffset += 50;
  requestRefresh();
});

refresh();
setInterval(refresh, 1500);

function showAgentPage(snapshot, page) {
  const countrySelect = element("country-filter");
  const country = countrySelect.value;
  countrySelect.replaceChildren(textNode("option", "All countries"));
  countrySelect.firstChild.value = "";
  for (const nation of snapshot.countries) {
    const option = textNode("option", nation.name);
    option.value = nation.id;
    countrySelect.append(option);
  }
  countrySelect.value = country;
  agentTotal = page.total;
  element("agent-page").textContent =
    `${page.total ? Math.min(page.offset + 1, page.total) : 0}–${Math.min(page.offset + page.agents.length, page.total)} of ${page.total}`;
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
  const residents = compute.by_role?.find((group) => group.role === "citizen");
  const age = residents?.model_decision_age_p95_seconds;
  const scheduling = compute.citizen_rotation_weeks
    ? `institutions decide weekly, citizens every ${compute.citizen_rotation_weeks} weeks`
    : "historical scheduling configuration";
  const providerNames = compute.providers?.length
    ? compute.providers.map((provider) => {
      const roles = provider.decision_group === "citizen" ? "citizens"
        : provider.decision_group === "institution" ? "institutions" : "all roles";
      return `${provider.name}: ${provider.model} for ${roles} (${provider.capacity} slots)`;
    }).join(" + ")
    : "Recorded model compute";
  element("compute").textContent =
    `${snapshot.run.mode === "LIVE" ? providerNames : "Rule simulation"} · ${compute.pending_decisions}/${compute.worker_capacity} pending requests · ${compute.due_agents} due · ${compute.average_model_decisions_per_second.toFixed(2)} accepted decisions/s average · p95 ${(snapshot.metrics.latency_p95_ms / 1000).toFixed(1)}s · ${scheduling} · resident AI age p95 ${age == null ? "unavailable" : `${Math.round(age)}s`}`;
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
      textNode(
        "p",
        government?.name ?? nation.government_id,
        "government-name",
      ),
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
        `Support ${Math.round(nation.approval * 100)}% · cash + deposits Gini ${nation.gini_cash.toFixed(2)}`,
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
        `${relation.first} ↔ ${relation.second} · ${relation.at_war ? "conflict" : relation.trade_agreement ? "trade agreement" : "offer pending"} · relations ${relation.score ?? (40 - Number(relation.tension ?? 40))}${relation.proposal_kind ? ` · ${relation.proposal_kind} offer by ${relation.proposed_by}` : ""}`,
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
        `${loan.bank_id} → ${loan.borrower_id} · ${(loan.principal_minor / 100).toFixed(2)} WLD · ${loan.status}${loan.status === "active" ? ` · ${(loan.principal_left_minor / 100).toFixed(2)} left` : ""}`,
      ),
    );
}

async function loadRunNotes(path, snapshot, health, signal) {
  const status =
    health.run_id === snapshot.run.id && health.status === "paused"
      ? "paused"
      : snapshot.run.status;
  const key = `${snapshot.run.id}:${snapshot.seq}:${status}`;
  if (status === "running") return { key, report: null };
  if (notesCache?.key === key) return notesCache;
  const report = await getJson(`${path}/notes`, signal);
  return { key, report };
}

function showRunNotes(runId, notes) {
  element("notes-download").href =
    `/api/runs/${encodeURIComponent(runId)}/notes.md`;
  element("notes-download").hidden = false;
  const list = element("run-notes");
  list.replaceChildren();
  const report = notes.report;
  if (!report) {
    element("notes-status").textContent =
      "Simulation in progress. Notes are saved at completion or stop; download a checkpoint report at any time.";
    return;
  }
  element("notes-status").textContent =
    `${report.status.toUpperCase()} · notes through T${report.tick} / event ${report.through_sequence} · ${report.history_complete ? "complete recorded history" : "partial history"}`;
  for (const observation of report.observations ?? []) {
    const item = textNode("li", observation.text);
    if (observation.evidence_ids?.length)
      item.append(
        textNode(
          "span",
          `Evidence: ${observation.evidence_ids.join(", ")}`,
          "muted",
        ),
      );
    list.append(item);
  }
}

async function loadAnalyticsReport(path, snapshot, signal) {
  if (!["completed", "stopped", "failed"].includes(snapshot.run.status))
    return { report: null, message: "Simulation in progress. Analysis becomes available after a finished run is exported and its report is imported." };
  try {
    const report = await getJson(`${path}/analytics/report`, signal);
    if (report.schema_version !== "gearth.analytics-report.v1" || report.run_id !== snapshot.run.id
        || report.through_revision !== snapshot.run.revision)
      return { report: null, message: "The imported report does not match this checkpoint." };
    return { report };
  } catch (error) {
    if (signal.aborted) throw error;
    return { report: null, message: error.message.includes("404")
      ? "No imported analytics report is available for this run."
      : "Imported analytics report is unavailable. The recorded world remains available." };
  }
}

function showAnalyticsReport(runId, analysis) {
  const summary = element("analytics-summary");
  const checks = element("analytics-checks");
  const countries = element("analytics-country-list");
  summary.replaceChildren(); checks.replaceChildren(); countries.replaceChildren();
  element("analytics-download").hidden = !analysis.report;
  element("analytics-countries").hidden = true;
  if (!analysis.report) {
    element("analytics-status").textContent = analysis.message;
    element("analytics-download").removeAttribute("href");
    return;
  }
  const report = analysis.report;
  const complete = report.completeness ?? {};
  element("analytics-download").href = `/api/runs/${encodeURIComponent(runId)}/analytics/report`;
  element("analytics-status").textContent =
    `${String(complete.run_status ?? "unknown").toUpperCase()} · ${complete.run_complete === true ? "finished run" : "partial run"} · audit through #${report.through_sequence} · revision ${report.through_revision} · analysis ${report.analysis_version}`;
  const totals = report.summary ?? {};
  summary.append(textNode("p", `${totals.record_count ?? "Unknown"} audit records · ${totals.transaction_count ?? "Unknown"} transfers · ${totals.agent_count ?? "Unknown"} agents · ${totals.decision_result_count ?? "Unknown"} decision results · ${totals.fallback_count ?? "Unknown"} fallbacks`));
  if (totals.transfer_minor !== undefined)
    summary.append(textNode("p", `${(Number(totals.transfer_minor) / 100).toLocaleString()} WLD recorded transfers`));
  const missing = complete.missing_data ?? [];
  const unknown = complete.metadata_unknown ?? [];
  summary.append(textNode("p", `Audit snapshot: ${complete.snapshot_complete === true ? "complete" : "partial"} · Missing data: ${missing.length ? missing.join(", ") : "none declared"} · Unknown metadata: ${unknown.length ? unknown.join(", ") : "none declared"}`, "scope"));
  for (const check of (report.checks ?? []).slice(0, 30)) {
    const item = textNode("li", `${check.name}: ${check.status}`);
    if (check.details && Object.keys(check.details).length) {
      const details = document.createElement("details");
      details.append(textNode("summary", "Inspect check and evidence"),
        textNode("pre", JSON.stringify({ details: check.details, evidence_refs: check.evidence_refs ?? [] }, null, 2)));
      item.append(details);
    }
    checks.append(item);
  }
  for (const country of (report.countries ?? []).slice(-30))
    countries.append(textNode("pre", JSON.stringify(country, null, 2)));
  element("analytics-countries").hidden = !countries.childElementCount;
  const comparison = report.government_comparison;
  if (comparison) summary.append(textNode("p", `Government comparison: ${comparison.status} · ${comparison.reason}`, "scope"));
  const provenance = document.createElement("details");
  provenance.append(textNode("summary", "Source manifest and evidence"),
    textNode("pre", JSON.stringify({ source_manifest_sha256: report.source_manifest_sha256,
      generated_at: report.generated_at, synthetic: complete.synthetic ?? "unknown",
      evidence_refs: (report.evidence_refs ?? []).slice(0, 50) }, null, 2)));
  summary.append(provenance);
}
