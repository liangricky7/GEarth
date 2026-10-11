(() => {
  const el = id => document.getElementById(id);
  const panel = el("constellation-panel"), canvas = el("constellation-canvas");
  if (!panel || !canvas) return;
  const ctx = canvas.getContext("2d");
  if (!ctx) return;
  const colors = { met: "#7befc2", partial: "#f7c77d", unmet: "#ff857d", institution: "#bba0ff", unknown: "#97a8bd" };
  const W = 900, H = 760;
  const reduced = window.matchMedia?.("(prefers-reduced-motion: reduce)");
  let snapshot = null, countries = [], nodes = [], links = [], localLinks = [], ambientNodes = [], ambientLinks = [], countryId = null, agentId = null;
  let hover = null, detailController = null, detail = null, frame = 0, lastFrame = 0, time = 0;
  let paused = reduced?.matches ?? false, visible = true, presentation = false;
  let inertNodes = [], previousFocus = null;
  let yaw = .12, pitch = .12, zoom = 1.1, drag = null, dragged = false;
  let recentEvents = [], selectedTrace = null, traceHits = [], markerHits = [], hoverTrace = null;
  let colorMode = "countries";
  let checkpointController = null, checkpointCache = null, checkpointRetryAt = 0;
  let signalQueue = [], signalHistory = [], seenSignals = new Set(), idleTimer = null, keyboardMode = false;
  const signalTypes = {
    shipment_dispatched:["Trade dispatched","trade"],shipment_delivered:["Trade arrived","trade"],
    shipment_failed:["Shipment failed","conflict"],policy_enacted:["Policy enacted","policy"],
    diplomatic_offer:["Diplomatic offer","diplomacy"],diplomatic_response:["Diplomatic response","diplomacy"],
    conflict_started:["Conflict started","conflict"],election_result:["Election result","policy"]
  };
  const depthLight = z => .27+.73*Math.pow(Math.max(0,Math.min(1,(z+1)/2)),.85);
  const palette = ["#59dd76","#38c9d9","#b48aeb","#e0d552","#dd9766","#66cdb4","#8dcc55","#5684de","#c875d0","#69dadd"];
  // Evenly spaced country directions keep the network round from every viewing angle.
  const goldenAngle = Math.PI * (3 - Math.sqrt(5));
  function countryDirection(index,count) {
    const y=1-2*(index+.5)/count,radius=Math.sqrt(1-y*y),theta=index*goldenAngle;
    return [Math.cos(theta)*radius,y,Math.sin(theta)*radius];
  }
  function clusterPoint(country,item,index,count) {
    const n=country.direction,side=Math.hypot(n[0],n[2]);
    const u=[n[2]/side,0,-n[0]/side];
    const v=[n[1]*u[2],n[2]*u[0]-n[0]*u[2],-n[1]*u[0]];
    const citizen=item.kind==="citizen",ambient=item.kind==="ambient";
    const theta=index*goldenAngle+(hash(country.id)%628)/100+(citizen ? 0 : 1.2);
    const angle=(ambient ? .73 : citizen ? .66 : .58)*Math.sqrt((index+.5)/count);
    const radius=(ambient ? .83 : citizen ? .92 : .76)+(hash(item.id)%1000)/1000*(ambient ? .17 : citizen ? .08 : .22);
    return n.map((value,axis)=>radius*(value*Math.cos(angle)+Math.sin(angle)*(u[axis]*Math.cos(theta)+v[axis]*Math.sin(theta))));
  }
  function rotate([x,y,z]) {
    const side=Math.cos(yaw)*x-Math.sin(yaw)*z,depth=Math.sin(yaw)*x+Math.cos(yaw)*z;
    return [side,Math.cos(pitch)*y-Math.sin(pitch)*depth,Math.sin(pitch)*y+Math.cos(pitch)*depth];
  }
  const traceColors = {trade:"#89d9ff",diplomacy:"#f7c77d",conflict:"#ff746f",corridor:"#68afc1"};
  const traceLabel = link => `${link.from.name} ${link.directional ? "→" : "↔"} ${link.to.name} · ${link.title}`;
  const networkHint = "Drag to rotate · Select a node or signal to explore";

  function node(tag, text, className) {
    const result = document.createElement(tag);
    if (text !== undefined) result.textContent = text;
    if (className) result.className = className;
    return result;
  }
  const format = value => Number(value).toLocaleString();
  const citizenColor = value => value === 2 ? colors.met : value === 1 ? colors.partial : value === 0 ? colors.unmet : colors.unknown;
  const hash = value => [...String(value)].reduce((total, ch) => ((total * 31) + ch.charCodeAt(0)) >>> 0, 7);
  const ratio = country => {
    const total = country.data?.citizen_count - (country.data?.unknown ?? 0);
    if (total > 0) return (country.data.met * 2 + country.data.partial) / (2 * total);
    const latest = country.nation?.history?.at(-1)?.needs_met;
    return Number.isFinite(latest) ? latest : null;
  };
  const countryColor = country => ratio(country) === null ? colors.unknown : ratio(country) >= .9 ? colors.met : ratio(country) >= .5 ? colors.partial : colors.unmet;
  const pct = value => value === null ? "—" : `${Math.round(value * 100)}%`;
  const label = agent => agent.role === "citizen" ? agent.occupation?.replaceAll("_", " ") || "Citizen" : agent.role === "business" ? `${agent.occupation?.replaceAll("_", " ") || "Business"}` : agent.role?.replaceAll("_", " ") || "Agent";
  function countryButton(country) {
    const button = node("button", country.name);
    button.type = "button";
    button.style.setProperty("--country-color",country.color);
    const dot=node("span",undefined,"network-country-dot");dot.setAttribute("aria-hidden","true");button.prepend(dot);
    button.setAttribute("aria-pressed", String(country.id === countryId));
    button.setAttribute("aria-label", `Explore ${country.name}, ${pct(ratio(country))} essentials met`);
    button.addEventListener("click", () => focusCountry(country.id));
    return button;
  }
  function makeScene() {
    nodes=[];links=[];localLinks=[];ambientNodes=[];ambientLinks=[];hover=null;hoverTrace=null;
    const chosen=countries.find(country=>country.id===countryId);
    const byId=new Map(countries.map(country=>[country.id,country]));
    const agents=new Map((snapshot.agents??[]).map(agent=>[agent.id,agent.country_id]));
    const locations=new Map((snapshot.locations??[]).map(location=>[location.id,location.country_id]));
    const countryFor=id=>byId.has(id) ? id : agents.get(id)??locations.get(id);
    const windowStart=Math.max(0,snapshot.tick-7);
    for(const [index,country] of countries.entries()) {
      country.direction=countryDirection(index,countries.length);
      country.vector=country.direction.map(value=>value*.88);
      country.color=palette[index%palette.length];
      const pending=(snapshot.policies??[]).some(policy=>policy.country_id===country.id);
      const enacted=recentEvents.some(event=>event.event_type==="policy_enacted" && event.tick>=windowStart && event.entity_ids?.includes(country.id));
      country.activity=pending ? "Pending policy" : enacted ? "Policy enacted" : "";
      nodes.push({id:country.id,country,kind:"country",vector:country.vector});
      for(const citizen of country.data?.citizens??[])nodes.push({id:citizen.id,country,agent:{...citizen,role:"citizen"},kind:"citizen"});
      for(const agent of (snapshot.agents??[]).filter(agent=>agent.country_id===country.id)
        .sort((a,b)=>((a.role==="business")-(b.role==="business"))||a.id.localeCompare(b.id)).slice(0,64))
        nodes.push({id:agent.id,country,agent,kind:"institution"});
    }
    // Citizen dots are bounded samples of real citizens. Larger dots are real institutions.
    for(const country of countries) {
      const members=nodes.filter(item=>item.country.id===country.id && item.kind!=="country");
      const citizens=members.filter(item=>item.kind==="citizen"),institutions=members.filter(item=>item.kind==="institution");
      // Each country occupies a spherical cap; institutions sit slightly deeper in the cloud.
      for(const [i,item] of citizens.entries())item.vector=clusterPoint(country,item,i,citizens.length);
      for(const [i,item] of institutions.entries())item.vector=clusterPoint(country,item,i,institutions.length);
      const membersById=new Map(members.map(item=>[item.id,item]));
      const hub=nodes.find(item=>item.id===country.id);
      for(const item of members) {
        localLinks.push({from:hub,to:item,kind:"membership"});
        for(const [kind,id] of [["employment",item.agent.employer_id],["education",item.agent.school_id]]) {
          const institution=membersById.get(id);
          if(institution)localLinks.push({from:item,to:institution,kind});
        }
      }
    }
    panel.dataset.citizenCheckpoint=snapshot.constellation ? "ready" : "loading";
    makeAmbientScene();
    panel.dataset.networkNodes=String(nodes.length);
    panel.dataset.ambientNodes=String(ambientNodes.length);
    panel.dataset.ambientEdges=String(ambientLinks.length);
    if(agentId && !nodes.some(item=>item.id===agentId)){agentId=null;cancelDetail();}
    function add(fromId,toId,kind,title,description,extra={}) {
      if(fromId===toId || !byId.has(fromId) || !byId.has(toId))return null;
      const id=`${kind}:${fromId}:${toId}`;
      const link={id,from:byId.get(fromId),to:byId.get(toId),kind,title,description,...extra};
      links.push(link);return link;
    }
    const corridors=new Map();
    for(const route of snapshot.routes??[]) {
      const ids=[countryFor(route.from),countryFor(route.to)].sort(),key=ids.join(":");
      if(!ids[0]||!ids[1]||ids[0]===ids[1])continue;
      if(!corridors.has(key))corridors.set(key,{ids,open:route.open});
      else corridors.get(key).open ||= route.open;
    }
    for(const {ids,open} of corridors.values())add(...ids,open ? "corridor" : "conflict",open ? "Open corridor" : "Border closed",open ? "An available trade corridor connects these countries." : "All recorded corridors between these countries are closed.",{border:!open});
    const trade=new Map();
    for(const cargo of snapshot.shipments??[]) {
      if(cargo.status!=="in_transit" && !(cargo.created_tick>=windowStart))continue;
      const from=countryFor(cargo.seller_id),to=countryFor(cargo.buyer_id);
      if(!from||!to||from===to)continue;
      const key=`${from}:${to}:${cargo.status==="failed"?"failed":"trade"}`;
      if(!trade.has(key))trade.set(key,{from,to,count:0,active:0,quantity:0,goods:new Set(),failed:cargo.status==="failed"});
      const item=trade.get(key);item.count++;item.quantity+=Number(cargo.quantity??0);item.goods.add(cargo.good??"goods");item.active+=cargo.status==="in_transit" ? 1 : 0;
    }
    for(const item of trade.values())add(item.from,item.to,item.failed ? "conflict" : "trade",item.failed ? "Shipment failed" : "Goods trade",
      `${item.count} recorded shipment${item.count===1?"":"s"} of ${[...item.goods].join(", ")}. ${item.active} currently in transit. Completed shipments are included only when dispatched within the last seven simulated days.`,
      {directional:true,count:item.count,active:item.active,quantity:item.quantity,lift:.22});
    for(const relationship of snapshot.relationships??[]) {
      if(relationship.at_war)add(relationship.first,relationship.second,"conflict","Active conflict","These countries are in a recorded economic conflict.",{tension:relationship.tension,lift:.32});
      else if(relationship.proposed_by) {
        const from=relationship.proposed_by,to=from===relationship.first ? relationship.second : relationship.first;
        add(from,to,"diplomacy",relationship.proposal_kind==="peace" ? "Peace offer pending" : "Trade offer pending","A diplomatic proposal is waiting for the other country's response.",{directional:true,expires:relationship.proposal_expires_tick,lift:.3});
      } else if(relationship.trade_agreement)add(relationship.first,relationship.second,"diplomacy","Trade agreement","An accepted trade agreement connects these countries.",{lift:.18});
    }
    panel.dataset.networkEdges=String(localLinks.length+links.length);
    if(selectedTrace && !links.some(link=>link.id===selectedTrace))selectedTrace=null;
    el("constellation-countries").replaceChildren(...countries.map(countryButton));
    el("constellation-reset").disabled=!countryId && !selectedTrace;
    el("constellation-caption").textContent=chosen ? `${chosen.name} / cluster focus` : `${countries.length} clusters / ${format(nodes.length+ambientNodes.length)} visual points`;
    canvas.setAttribute("aria-label",`${chosen?.name??"World"} agent network with country clusters, citizen samples, institutions, ambient particles and recorded interaction signals. Drag to rotate, or use the country and agent buttons to explore.`);
    renderInspector();draw();
  }
  function clearCheckpoint() {
    checkpointController?.abort();checkpointController=null;checkpointCache=null;checkpointRetryAt=0;
  }
  function applyCheckpoint() {
    if(!snapshot || checkpointCache?.runId!==snapshot.run.id || checkpointCache.tick>snapshot.tick)return;
    snapshot={...snapshot,constellation:checkpointCache.data};
    for(const country of countries)country.data=checkpointCache.data.find(item=>item.country_id===country.id);
  }
  async function loadCitizenCheckpoint() {
    if(!snapshot || checkpointController || performance.now()<checkpointRetryAt)return;
    if(checkpointCache?.runId===snapshot.run.id && (snapshot.run.status!=="running" || snapshot.tick===checkpointCache.tick || performance.now()-checkpointCache.loadedAt<60000))return;
    const controller=new AbortController(),runId=snapshot.run.id;checkpointController=controller;
    checkpointRetryAt=performance.now()+60000;
    try {
      const response=await fetch(`/api/runs/${encodeURIComponent(runId)}/renderer/current`,{signal:controller.signal});
      if(!response.ok)throw new Error("Citizen checkpoint unavailable");
      const frame=await response.json();
      if(controller.signal.aborted || snapshot?.run.id!==runId || frame.run_id!==runId || !Array.isArray(frame.snapshot?.agents))return;
      const data=countries.map(country=>{
        const citizens=frame.snapshot.agents.filter(agent=>agent.role==="citizen" && agent.country_id===country.id).sort((a,b)=>a.id.localeCompare(b.id));
        const count=Math.min(48,citizens.length),sample=Array.from({length:count},(_,index)=>citizens[Math.floor(index*citizens.length/count)]);
        const value={country_id:country.id,citizen_count:citizens.length,met:0,partial:0,unmet:0,unknown:0,citizens:sample,checkpoint_tick:frame.tick};
        for(const citizen of citizens)value[citizen.needs_met===2 ? "met" : citizen.needs_met===1 ? "partial" : citizen.needs_met===0 ? "unmet" : "unknown"]++;
        return value;
      });
      checkpointCache={runId,tick:frame.tick,data,loadedAt:performance.now()};applyCheckpoint();makeScene();
    } catch(error) {
      if(!controller.signal.aborted && snapshot?.run.id===runId)panel.dataset.citizenCheckpoint="unavailable";
    } finally {if(checkpointController===controller)checkpointController=null;}
  }
  function makeAmbientScene() {
    // This bounded layer is purely decorative: it has no agent profiles, needs, or interaction records.
    const perCountry=Math.floor(2400/Math.max(10,countries.length));
    for(const country of countries) {
      const members=[];
      for(let i=0;i<perCountry;i++) {
        const item={id:`ambient:${country.id}:${i}`,kind:"ambient",country};
        item.vector=clusterPoint(country,item,i,perCountry);members.push(item);ambientNodes.push(item);
      }
      // A local spatial grid adds short mesh filaments, with bounded work as density grows.
      const grid=new Map(),cell=.14,key=([x,y,z])=>`${x}:${y}:${z}`;
      for(const item of members) {
        const bucket=item.vector.map(value=>Math.floor(value/cell)),near=[];
        for(let x=-1;x<=1;x++)for(let y=-1;y<=1;y++)for(let z=-1;z<=1;z++) {
          for(const other of grid.get(key([bucket[0]+x,bucket[1]+y,bucket[2]+z]))??[]) {
            const distance=Math.hypot(...item.vector.map((value,axis)=>value-other.vector[axis]));
            if(distance<.16)near.push({other,distance});
          }
        }
        for(const {other} of near.sort((a,b)=>a.distance-b.distance).slice(0,2))ambientLinks.push({from:item,to:other});
        const id=key(bucket);if(!grid.has(id))grid.set(id,[]);grid.get(id).push(item);
      }
    }
  }
  function drawAmbient(signals) {
    // Batch the cosmetic mesh and particles by color and depth to keep the animation light.
    const projected=new Map(ambientNodes.map(item=>[item.id,{...project(item.vector),item,ripple:signals.length ? rippleStrength(item,signals) : 0}]));
    const edgeBatches=new Map(),dotBatches=new Map();
    for(const edge of ambientLinks) {
      const a=projected.get(edge.from.id),b=projected.get(edge.to.id);
      const depth=Math.max(0,Math.min(4,Math.floor(((a.z+b.z)/2+1)*2.5))),country=edge.from.country;
      const key=`${country.id}:${depth}`;
      if(!edgeBatches.has(key))edgeBatches.set(key,{country,depth,edges:[]});
      edgeBatches.get(key).edges.push({a,b});
    }
    for(const p of projected.values()) {
      const depth=Math.max(0,Math.min(4,Math.floor((p.z+1)*2.5))),glow=p.ripple>.35;
      const key=`${p.item.country.id}:${depth}:${glow}`;
      if(!dotBatches.has(key))dotBatches.set(key,{country:p.item.country,depth,glow,points:[]});
      dotBatches.get(key).points.push(p);
    }
    ctx.save();ctx.globalCompositeOperation="screen";
    for(const {country,depth,edges} of edgeBatches.values()) {
      const focused=!countryId || country.id===countryId;
      ctx.globalAlpha=(focused ? .085 : .018)*(.35+depth*.16);ctx.lineWidth=.4;
      ctx.strokeStyle=colorMode==="needs" ? "#607b82" : country.color;
      ctx.beginPath();for(const {a,b} of edges){ctx.moveTo(a.x,a.y);ctx.lineTo(b.x,b.y);}ctx.stroke();
    }
    for(const {country,depth,glow,points} of [...dotBatches.values()].sort((a,b)=>a.depth-b.depth)) {
      const focused=!countryId || country.id===countryId;
      ctx.globalAlpha=(focused ? (glow ? .85 : .74) : .09)*(.35+depth*.16);
      ctx.fillStyle=colorMode==="needs" ? "#637a80" : country.color;
      ctx.beginPath();
      for(const p of points){const r=(glow ? 1.45 : 1.2)*p.scale;ctx.moveTo(p.x+r,p.y);ctx.arc(p.x,p.y,r,0,Math.PI*2);}
      ctx.fill();
    }
    ctx.restore();
  }
  function resetSignals() {
    signalQueue=[];signalHistory=[];seenSignals.clear();panel.dataset.signalEvents="0";
    el("network-event").hidden=true;
  }
  function ingestSignals(initial) {
    const owners=new Map((snapshot.agents??[]).map(agent=>[agent.id,agent.country_id]));
    const ids=new Set(countries.map(country=>country.id));
    const shipments=new Map((snapshot.shipments??[]).map(cargo=>[cargo.id,cargo]));
    const relationships=new Map((snapshot.relationships??[]).map(item=>[item.id,[item.first,item.second]]));
    const fresh=[];
    const committed=recentEvents.filter(event=>event.tick<=snapshot.tick && (!Number.isFinite(event.seq) || !Number.isFinite(snapshot.seq) || event.seq<=snapshot.seq));
    for(const event of committed) {
      const key=event.event_id??`${event.seq??event.tick}:${event.event_type}:${event.entity_ids?.join(":")}`;
      if(seenSignals.has(key))continue;
      seenSignals.add(key);
      if(!signalTypes[event.event_type] || event.tick<Math.max(0,snapshot.tick-7))continue;
      const affected=new Set();
      for(const id of event.entity_ids??[]) {
        if(ids.has(id))affected.add(id);
        if(ids.has(owners.get(id)))affected.add(owners.get(id));
        const cargo=shipments.get(id);
        if(cargo)for(const agent of [cargo.seller_id,cargo.buyer_id])if(ids.has(owners.get(agent)))affected.add(owners.get(agent));
        for(const country of relationships.get(id)??[])if(ids.has(country))affected.add(country);
      }
      if(!affected.size)continue;
      const [title,kind]=signalTypes[event.event_type];
      fresh.push({key,title,kind,tick:event.tick,countries:[...affected],seq:event.seq??0});
    }
    // Remember only the current bounded feed, so long-running sessions retain no unbounded event cache.
    seenSignals=new Set(committed.map(event=>event.event_id??`${event.seq??event.tick}:${event.event_type}:${event.entity_ids?.join(":")}`));
    const selected=fresh.sort((a,b)=>a.seq-b.seq).slice(initial ? -6 : -8);
    signalHistory=[...signalHistory,...selected].slice(-6);
    if(!reduced?.matches) {
      signalQueue=signalQueue.filter(signal=>time-signal.start<3.6);
      const start=Math.max(time,signalQueue.at(-1)?.start+.9||time);
      selected.forEach((signal,index)=>signalQueue.push({...signal,start:start+index*(initial ? 2.2 : .9)}));
      signalQueue=signalQueue.slice(-18);
    }
    panel.dataset.signalEvents=String(signalHistory.length);
  }
  function replaySignals() {
    if(reduced?.matches || signalQueue.some(signal=>time-signal.start<3.6))return;
    signalQueue=signalHistory.map((signal,index)=>({...signal,start:time+index*2.2}));
  }
  function activeSignals() {
    signalQueue=signalQueue.filter(signal=>time-signal.start<3.6);
    const active=signalQueue.filter(signal=>time>=signal.start);
    const latest=active.at(-1),badge=el("network-event");
    if(latest) {
      const names=latest.countries.map(id=>countries.find(country=>country.id===id)?.name).filter(Boolean).join(" ↔ ");
      const text=`Recorded · Day ${latest.tick} · ${latest.title} · ${names}`;
      if(badge.textContent!==text)badge.textContent=text;
      badge.hidden=false;
    } else badge.hidden=true;
    return active;
  }
  function rippleStrength(item,signals) {
    const n=item.country.direction,length=Math.hypot(...item.vector);
    const angle=Math.acos(Math.max(-1,Math.min(1,item.vector.reduce((sum,value,axis)=>sum+value*n[axis],0)/length)));
    let strength=0;
    for(const signal of signals) {
      if(!signal.countries.includes(item.country.id))continue;
      const progress=(time-signal.start)/3.6,wave=.04+progress*.8;
      strength=Math.max(strength,Math.exp(-(((angle-wave)/.085)**2))*Math.sin(Math.PI*Math.min(1,progress+.1)));
    }
    return strength;
  }
  function drawRipples(signals) {
    ctx.save();ctx.globalCompositeOperation="screen";
    for(const signal of signals)for(const id of signal.countries) {
      const country=countries.find(item=>item.id===id);if(!country)continue;
      const progress=(time-signal.start)/3.6,angle=.04+progress*.8;
      const n=country.direction,side=Math.hypot(n[0],n[2]),u=[n[2]/side,0,-n[0]/side];
      const v=[n[1]*u[2],n[2]*u[0]-n[0]*u[2],-n[1]*u[0]];
      const color=signal.kind==="policy" ? country.color : traceColors[signal.kind];
      const fade=Math.sin(Math.PI*Math.min(1,progress+.1))*.6;
      const points=Array.from({length:49},(_,i)=>{
        const theta=i/48*Math.PI*2;
        return project(n.map((value,axis)=>.985*(value*Math.cos(angle)+Math.sin(angle)*(u[axis]*Math.cos(theta)+v[axis]*Math.sin(theta)))));
      });
      ctx.strokeStyle=color;
      for(let i=1;i<points.length;i++) {
        const a=points[i-1],b=points[i];ctx.globalAlpha=fade*depthLight((a.z+b.z)/2);
        ctx.lineWidth=1.15;ctx.beginPath();ctx.moveTo(a.x,a.y);ctx.lineTo(b.x,b.y);ctx.stroke();
      }
      const center=project(country.vector);ctx.globalAlpha=fade*.4*depthLight(center.z);
      const glow=ctx.createRadialGradient(center.x,center.y,0,center.x,center.y,65);
      glow.addColorStop(0,`${color}50`);glow.addColorStop(1,`${color}00`);
      ctx.fillStyle=glow;ctx.beginPath();ctx.arc(center.x,center.y,65,0,Math.PI*2);ctx.fill();
    }
    ctx.restore();
  }
  function stat(title, value) {
    const row = node("div",undefined,"constellation-stat");
    row.append(node("span",title),node("strong",value));
    return row;
  }
  function distribution(data) {
    const bar = node("div",undefined,"constellation-distribution");
    bar.setAttribute("aria-hidden","true");
    for (const key of ["met","partial","unmet","unknown"]) {
      const segment = node("span"); segment.style.background = colors[key]; segment.style.flex = String(data[key] ?? 0); bar.append(segment);
    }
    return bar;
  }
  function renderInspector() {
    panel.classList.toggle("network-inspecting",Boolean(countryId || agentId || selectedTrace));
    const target = el("constellation-detail"), list = el("constellation-node-list");
    target.replaceChildren(); list.replaceChildren();
    if (!snapshot) {
      target.append(node("p","World overview","eyebrow"),node("h3","A society, connected."),node("p","The network appears when a simulation checkpoint is available.","constellation-description"));
      return;
    }
    const interaction=links.find(link=>link.id===selectedTrace);
    if(interaction) {
      target.append(node("p","Country interaction","eyebrow"),node("h3",interaction.title),node("p",`${interaction.from.name} ${interaction.directional ? "→" : "↔"} ${interaction.to.name}`,"constellation-description"),node("p",interaction.description,"constellation-reason"));
      if(interaction.count)target.append(stat("Shipments",format(interaction.count)),stat("In transit",format(interaction.active)),stat("Goods units",format(interaction.quantity)));
      if(interaction.tension!==undefined)target.append(stat("Diplomatic tension",`${interaction.tension}/100`));
      if(interaction.expires!==undefined)target.append(stat("Offer expires",`Day ${interaction.expires}`));
      for(const country of [interaction.from,interaction.to]) {
        const button=node("button",`Explore ${country.name}`);button.type="button";button.addEventListener("click",()=>focusCountry(country.id));list.append(button);
      }
      return;
    }
    const country = countries.find(item => item.id === countryId);
    const selected = nodes.find(item => item.id === agentId);
    if (selected?.agent) {
      const agent = detail?.agent ?? selected.agent;
      target.append(node("p",`${selected.country.name} / ${label(agent)}`,"eyebrow"),node("h3",agent.role === "citizen" ? "Citizen wellbeing" : label(agent)));
      target.append(node("p",agent.id,"constellation-description"));
      if (agent.role === "citizen") {
        const score = node("strong",Number.isInteger(agent.needs_met) ? `${agent.needs_met}/2` : "—","constellation-score");
        score.style.color = citizenColor(agent.needs_met);
        target.append(score,node("span","Daily essentials met · food & energy","constellation-score-label"),stat("Health",agent.health === undefined ? "—" : `${agent.health}/100`),stat("Wellbeing",agent.wellbeing === undefined ? "—" : `${agent.wellbeing}/100`),stat("Employment",agent.employer_id ? "Employed" : "No employer"));
        if (agent.hungry !== undefined) target.append(stat("Food today",agent.hungry ? "Meal missed" : "Meal met"));
      } else {
        target.append(stat("Role",label(agent)),stat("Accepted model turns",format(agent.model_decisions ?? 0)));
        const decision = detail?.decisions?.at(-1) ?? detail?.last_decision;
        if (decision?.result?.action) {
          target.append(node("p",decision.result.action.action_type.replaceAll("_"," "),"constellation-note"),node("p",decision.result.action.reason,"constellation-reason"),node("p",`${decision.result.source} · ${decision.accepted ? "Accepted" : "Not accepted"}`,"constellation-note"));
        } else target.append(node("p",detail ? "No decision explanation recorded for this agent." : "Loading the latest recorded decision…","constellation-note"));
      }
      const button = node("button","Back to country"); button.type="button"; button.addEventListener("click",()=>focusCountry(countryId)); target.append(button);
      if (detail?.updated_at) target.append(node("p",`Agent checkpoint · ${new Date(detail.updated_at).toLocaleTimeString()}`,"constellation-note"));
      return;
    }
    const data = country?.data ?? countries.reduce((total,item) => {
      for (const key of ["citizen_count","met","partial","unmet","unknown"]) total[key] += item.data?.[key] ?? 0;
      return total;
    },{citizen_count:0,met:0,partial:0,unmet:0,unknown:0});
    const known = data.citizen_count - data.unknown;
    const value = country ? ratio(country) : known > 0 ? (data.met*2+data.partial)/(known*2) : null;
    target.append(node("p",country ? "Country focus" : "World overview","eyebrow"),node("h3",country?.name ?? "A society, connected."));
    const description = country ? snapshot.government_profiles?.find(item=>item.id===country.nation?.government_id)?.name ?? country.nation?.government_id?.replaceAll("_"," ") ?? "" : "Each cluster is a country. Bright nodes represent agents and institutions; faint ambient dots fill out the visual field.";
    target.append(node("p",description,"constellation-description"));
    const score = node("strong",pct(value),"constellation-score"); score.style.color = value === null ? colors.unknown : value>=.9 ? colors.met : value>=.5 ? colors.partial : colors.unmet;
        const checkpointTick=country?.data?.checkpoint_tick ?? countries.find(item=>item.data?.checkpoint_tick!==undefined)?.data.checkpoint_tick;
    target.append(score,node("span",checkpointTick!==undefined ? `Daily essentials met · checkpoint day ${checkpointTick}` : "Daily essentials met","constellation-score-label"));
    if (data.citizen_count) target.append(distribution(data),stat("Both needs met",format(data.met)),stat("Missing essentials",format(data.partial+data.unmet)),stat("Citizens",format(data.citizen_count)));
    else target.append(node("p","Individual needs totals are unavailable in this checkpoint.","constellation-note"));
    if (data.unknown) target.append(stat("Needs status unavailable",format(data.unknown)));
    if (!country) target.append(stat("Countries",format(countries.length)),stat("Total agents",format(snapshot.agents_summary?.spawned ?? 0)),stat("Interaction traces",format(links.filter(link=>link.kind!=="corridor").length)),node("p","Rotate the network. Follow a country, an agent, or an interaction signal.","constellation-note"));
    else {
      const sampled = nodes.filter(item=>item.agent && item.country.id===country.id);
      const connections=links.filter(link=>link.kind!=="corridor" && (link.from.id===countryId || link.to.id===countryId));
      target.append(stat("Interaction traces",format(connections.length)),node("p","Connections","eyebrow"));
      for(const link of connections) {
        const button=node("button",traceLabel(link),"network-connection");button.type="button";button.style.borderColor=`${traceColors[link.kind]}66`;button.addEventListener("click",()=>selectInteraction(link.id));list.append(button);
      }
      target.append(node("p",`${country.data?.citizens?.length??0} citizens sampled for inspection. Choose an agent below to see needs or its latest decision.`,"constellation-note"));
      for (const item of sampled) {
        const button = node("button",label(item.agent)); button.type="button";
        button.setAttribute("aria-label",`${label(item.agent)}, ${item.id}${item.kind==="citizen" ? `, ${item.agent.needs_met} of 2 needs met` : ""}`);
        button.style.borderColor = item.kind==="citizen" ? `${citizenColor(item.agent.needs_met)}66` : "#bba0ff66";
        button.addEventListener("click",()=>selectAgent(item.id)); list.append(button);
      }
    }
  }
  function cancelDetail() { detailController?.abort(); detailController=null; detail=null; }
  function focusCountry(id) {
    cancelDetail();countryId=id;agentId=null;selectedTrace=null;
    const country=countries.find(item=>item.id===id);
    if(!country){yaw=.12;pitch=.12;zoom=1.1;}
    makeScene();
  }
  async function loadDetail() {
    if (!snapshot || !agentId || detailController) return;
    const controller = new AbortController(); detailController=controller;
    const run = snapshot.run.id, selected = agentId;
    try {
      const response = await fetch(`/api/runs/${encodeURIComponent(run)}/agents/${encodeURIComponent(selected)}`,{signal:controller.signal});
      if (!response.ok) throw new Error("Agent details unavailable");
      const data = await response.json();
      if (controller.signal.aborted || snapshot?.run.id!==run || agentId!==selected) return;
      detail=data; renderInspector();
    } catch (error) {
      if (!controller.signal.aborted && agentId===selected && snapshot?.run.id===run)
        el("constellation-detail").append(node("p","Agent details could not be loaded. Showing the recorded visual sample.","constellation-note"));
    } finally { if (detailController===controller) detailController=null; }
  }
  function selectAgent(id) {
    cancelDetail(); selectedTrace=null; agentId=id;countryId=nodes.find(item=>item.id===id)?.country.id??countryId;makeScene();loadDetail();
  }
  function selectInteraction(id) {
    cancelDetail();agentId=null;selectedTrace=id;el("constellation-reset").disabled=false;renderInspector();draw();
  }
  function project(world) {
    const [x,y,z]=rotate(world),perspective=1/(1-z*.16),r=(presentation ? 300 : 260)*zoom;
    return {x:W/2+x*r*perspective,y:H/2-y*r*perspective,z,scale:perspective};
  }
  function drawTrace(link) {
    const relevant=!countryId || link.from.id===countryId || link.to.id===countryId;
    const highlighted=link.id===selectedTrace || link.id===hoverTrace?.id;
    const a=project(link.from.vector),b=project(link.to.vector);
    const bend=(hash(link.id)%2 ? 1 : -1)*(link.kind==="corridor" ? 12 : 28);
    const mid={x:(a.x+b.x)/2+(b.y-a.y)*.12,y:(a.y+b.y)/2+bend};
    const point=t=>({x:(1-t)**2*a.x+2*(1-t)*t*mid.x+t*t*b.x,y:(1-t)**2*a.y+2*(1-t)*t*mid.y+t*t*b.y});
    const depth=depthLight((a.z+b.z)/2);
    const opacity=(!relevant ? .055 : link.kind==="corridor" ? .26 : highlighted ? .98 : .76)*(highlighted ? 1 : depth);
    const color=traceColors[link.kind];
    const path=()=>{ctx.beginPath();ctx.moveTo(a.x,a.y);ctx.quadraticCurveTo(mid.x,mid.y,b.x,b.y);};
    ctx.save();ctx.globalCompositeOperation="screen";
    ctx.strokeStyle=color;
    // A broad, faint underlay gives the important connections a soft beam effect.
    ctx.globalAlpha=opacity*.12;ctx.lineWidth=highlighted ? 7 : link.kind==="corridor" ? 2.5 : 4;
    path();ctx.stroke();
    ctx.globalAlpha=opacity;ctx.lineWidth=highlighted ? 1.9 : link.kind==="corridor" ? .75 : 1.05;
    ctx.setLineDash(link.kind==="diplomacy" || link.border ? [4,5] : []);
    path();ctx.stroke();ctx.setLineDash([]);
    if(relevant && link.kind!=="corridor") {
      for(let i=1;i<=24;i++)traceHits.push({link,a:point((i-1)/24),b:point(i/24)});
      const t=(time*.14+(hash(link.id)%100)/100)%1,p=point(t),next=point(Math.min(1,t+.025));
      const start=Math.max(0,t-.13),tail=point(start);
      if(t>start) {
        const glow=ctx.createLinearGradient(tail.x,tail.y,p.x,p.y);
        glow.addColorStop(0,`${color}00`);glow.addColorStop(.75,color);glow.addColorStop(1,"#e6fff6");
        ctx.globalAlpha=highlighted ? .95 : .8*depth;ctx.strokeStyle=glow;ctx.lineWidth=2;
        ctx.beginPath();ctx.moveTo(tail.x,tail.y);
        for(let i=1;i<=8;i++){const trail=point(start+(t-start)*i/8);ctx.lineTo(trail.x,trail.y);}
        ctx.stroke();
      }
      ctx.globalAlpha=.95*depth;ctx.fillStyle=color;ctx.shadowColor=color;ctx.shadowBlur=9;
      ctx.beginPath();ctx.arc(p.x,p.y,2.4,0,Math.PI*2);ctx.fill();ctx.shadowBlur=0;
      ctx.save();ctx.translate(p.x,p.y);ctx.rotate(Math.atan2(next.y-p.y,next.x-p.x));
      ctx.fillStyle="#dcf8ef";ctx.beginPath();ctx.moveTo(4.5,0);ctx.lineTo(-3,-2.2);ctx.lineTo(-3,2.2);ctx.closePath();ctx.fill();ctx.restore();
    }
    ctx.restore();
    ctx.globalAlpha=1;
  }
  function draw() {
    const rect=canvas.getBoundingClientRect();if(!rect.width||!rect.height)return;
    const dpr=Math.min(window.devicePixelRatio||1,2),width=Math.round(rect.width*dpr),height=Math.round(rect.height*dpr);
    if(canvas.width!==width||canvas.height!==height){canvas.width=width;canvas.height=height;}
    const scale=Math.min(rect.width/W,rect.height/H);
    ctx.setTransform(1,0,0,1,0,0);ctx.clearRect(0,0,width,height);
    ctx.setTransform(scale*dpr,0,0,scale*dpr,(rect.width-W*scale)*dpr/2,(rect.height-H*scale)*dpr/2);
    traceHits=[];markerHits=[];
    if(!snapshot){ctx.fillStyle="#9eaea6";ctx.textAlign="center";ctx.font="14px system-ui";ctx.fillText("Connecting to the network",W/2,H/2);return;}
    const signals=activeSignals();
    drawAmbient(signals);
    const projected=new Map(nodes.map(item=>[item.id,{...project(item.vector),item,ripple:rippleStrength(item,signals)}]));
    // Brighter filaments show real membership, employment and schooling; depth keeps the mesh readable.
    ctx.save();ctx.globalCompositeOperation="screen";
    for(const edge of localLinks) {
      const a=projected.get(edge.from.id),b=projected.get(edge.to.id);
      const relevant=!countryId || edge.from.country.id===countryId;
      const selected=edge.from.id===agentId || edge.to.id===agentId || edge.from.id===hover?.id || edge.to.id===hover?.id;
      const depth=depthLight((a.z+b.z)/2),ripple=Math.max(a.ripple,b.ripple);
      ctx.globalAlpha=Math.min(1,(selected ? .85 : relevant ? (edge.kind==="membership" ? .22 : .4)+ripple*.38 : .04)*depth);
      ctx.lineWidth=selected ? 1.2 : edge.kind==="membership" ? .7 : .85;
      ctx.strokeStyle=selected || ripple>.2 ? edge.from.country.color : edge.kind==="membership" ? "#accbc4" : "#92ccdb";
      ctx.beginPath();ctx.moveTo(a.x,a.y);ctx.lineTo(b.x,b.y);ctx.stroke();
    }
    ctx.restore();
    ctx.globalAlpha=1;
    for(const link of links)drawTrace(link);
    drawRipples(signals);
    for(const p of [...projected.values()].sort((a,b)=>a.z-b.z)) {
      const {item,x,y,z,ripple}=p,country=item.country;
      const focused=!countryId || country.id===countryId;
      const selected=item.id===agentId || item.id===hover?.id || item.kind==="country" && country.id===countryId;
      const needs=item.kind==="citizen" ? citizenColor(item.agent.needs_met) : item.kind==="country" ? countryColor(country) : colors.institution;
      const color=colorMode==="needs" ? needs : country.color;
      const radius=(item.kind==="country" ? 5.5 : item.kind==="institution" ? (item.agent.role==="government" ? 4.5 : 2.6) : 1.65)*p.scale;
      ctx.globalAlpha=focused ? Math.min(1,depthLight(z)+ripple*.55) : .12;
      ctx.fillStyle=color;
      if(selected || ripple>.35) {ctx.shadowColor=color;ctx.shadowBlur=selected ? 12 : 7*ripple;}
      ctx.beginPath();ctx.arc(x,y,radius*(selected ? 1.35 : 1+ripple*.55),0,Math.PI*2);ctx.fill();ctx.shadowBlur=0;
      if(item.kind==="citizen" && colorMode==="countries" && item.agent.needs_met!==2 && Number.isInteger(item.agent.needs_met)) {
        ctx.strokeStyle=needs;ctx.lineWidth=.65;ctx.beginPath();ctx.arc(x,y,radius+1,0,Math.PI*2);ctx.stroke();
      }
      if(selected) {
        ctx.strokeStyle=color;ctx.lineWidth=.8;ctx.beginPath();ctx.arc(x,y,radius+7+Math.sin(time*1.4)*1.3,0,Math.PI*2);ctx.stroke();
      }
      markerHits.push({...item,x,y,r:Math.max(radius+2,item.kind==="country" ? 10 : 4)});
    }
    ctx.globalAlpha=1;
    const labelled=hover?.country ?? countries.find(item=>item.id===countryId);
    if(labelled) {
      const p=project(labelled.vector),title=hover?.agent ? label(hover.agent) : labelled.name;
      ctx.font="500 14px system-ui";ctx.textAlign="left";
      const width=Math.max(ctx.measureText(title).width,ctx.measureText(`${labelled.name} · ${pct(ratio(labelled))} essentials`).width)+22;
      const x=Math.max(22,Math.min(W-width-22,p.x+17)),y=Math.max(55,Math.min(H-110,p.y-28));
      ctx.fillStyle="#131916ec";ctx.fillRect(x-9,y-19,width,48);
      ctx.fillStyle="#edf4ef";ctx.fillText(title,x,y);
      ctx.font="10px ui-monospace,monospace";ctx.fillStyle=labelled.color;ctx.fillText(`${labelled.name} · ${pct(ratio(labelled))} essentials`,x,y+18);
    }
    ctx.textAlign="left";ctx.font="9px ui-monospace,monospace";ctx.fillStyle="#7e9186";
    ctx.fillText(`${format(snapshot.agents_summary?.spawned??0)} AGENTS  /  ${format(nodes.length+ambientNodes.length)} VISUAL POINTS`,32,H-48);
    ctx.textAlign="right";ctx.fillText("GEARTH / SIGNAL FIELD",W-32,H-48);
  }
  function animate(stamp) {
    frame=0;
    if(paused || !visible || document.hidden) return;
    if(stamp-lastFrame>=32) {const step=Math.min((stamp-lastFrame)/1000,.08);time+=step;if(!countryId&&!drag)yaw+=step*.035;lastFrame=stamp;draw();}
    frame=requestAnimationFrame(animate);
  }
  function motion() {
    if(frame) cancelAnimationFrame(frame);frame=0;lastFrame=performance.now();
    el("constellation-motion").textContent=paused ? "Resume motion" : "Pause motion";
    el("constellation-motion").setAttribute("aria-pressed",String(paused));
    if(!paused && visible && !document.hidden) frame=requestAnimationFrame(animate);
    draw();
  }
  function canvasPoint(event) {
    const rect=canvas.getBoundingClientRect(),scale=Math.min(rect.width/W,rect.height/H);
    return {x:(event.clientX-rect.left-(rect.width-W*scale)/2)/scale,y:(event.clientY-rect.top-(rect.height-H*scale)/2)/scale};
  }
  function hit(event) {
    const {x,y}=canvasPoint(event);
    const marker=[...markerHits].reverse().find(item=>Math.hypot(item.x-x,item.y-y)<item.r);
    if(marker)return marker;
    for(const {link,a,b} of [...traceHits].reverse()) {
      const dx=b.x-a.x,dy=b.y-a.y,t=Math.max(0,Math.min(1,((x-a.x)*dx+(y-a.y)*dy)/(dx*dx+dy*dy||1)));
      if(Math.hypot(a.x+t*dx-x,a.y+t*dy-y)<6)return {kind:"trace",link};
    }
    return null;
  }
  canvas.addEventListener("pointerdown",event=>{
    if(event.button!==0)return;drag={x:event.clientX,y:event.clientY,startX:event.clientX,startY:event.clientY};dragged=false;canvas.setPointerCapture?.(event.pointerId);canvas.style.cursor="grabbing";
  });
  canvas.addEventListener("pointermove",event=>{
    if(drag) {
      const dx=event.clientX-drag.x,dy=event.clientY-drag.y;
      if(Math.hypot(event.clientX-drag.startX,event.clientY-drag.startY)>4)dragged=true;
      yaw-=dx*.006;pitch=Math.max(-1.2,Math.min(1.2,pitch+dy*.006));drag.x=event.clientX;drag.y=event.clientY;draw();return;
    }
    const item=hit(event);hover=item && item.kind!=="trace"?item:null;hoverTrace=item?.kind==="trace"?item.link:null;
    canvas.style.cursor=item ? "pointer" : "grab";
    el("constellation-hover").textContent=hover ? hover.agent ? `${hover.country.name} · ${label(hover.agent)}${hover.kind==="citizen" ? ` · ${hover.agent.needs_met??"?"}/2 needs met` : ""} · Click to inspect` : `${hover.country.name} · ${pct(ratio(hover.country))} needs met · Click to explore` : hoverTrace ? traceLabel(hoverTrace) : networkHint;
    if(paused)draw();
  });
  const endDrag=()=>{drag=null;canvas.style.cursor="grab";};
  canvas.addEventListener("pointerup",endDrag);canvas.addEventListener("pointercancel",endDrag);
  canvas.addEventListener("pointerleave",()=>{hover=null;hoverTrace=null;el("constellation-hover").textContent=networkHint;if(paused)draw();});
  canvas.addEventListener("click",event=>{
    if(dragged){dragged=false;return;}
    const item=hit(event);if(!item)return;
    if(item.kind==="country")focusCountry(item.id);else if(item.kind==="trace")selectInteraction(item.link.id);else selectAgent(item.id);
  });
  function changeZoom(amount){zoom=Math.max(.75,Math.min(1.5,zoom+amount));draw();}
  el("network-zoom-in").addEventListener("click",()=>changeZoom(.1));
  el("network-zoom-out").addEventListener("click",()=>changeZoom(-.1));
  el("constellation-colors").addEventListener("click",()=>{
    colorMode=colorMode==="countries" ? "needs" : "countries";
    el("constellation-colors").textContent=colorMode==="countries" ? "Color by needs" : "Color by country";
    el("constellation-colors").setAttribute("aria-pressed",String(colorMode==="needs"));
    el("network-color-key").textContent=colorMode==="needs" ? "Needs: mint = met · amber = partial · coral = unmet · violet = institutions · muted dots = ambient" : "Colors = countries · Bright nodes = agents · Large nodes = institutions · Faint dots = ambient";
    panel.dataset.colorMode=colorMode;draw();
  });
  el("constellation-reset").addEventListener("click",()=>focusCountry(null));
  el("constellation-motion").addEventListener("click",()=>{paused=!paused;motion();});
  function wakePresentation(keyboard=false) {
    clearTimeout(idleTimer);panel.classList.remove("network-idle");
    if(!presentation)return;
    if(keyboard)keyboardMode=true;
    idleTimer=setTimeout(()=>{
      if(countryId || agentId || selectedTrace || drag || keyboardMode && panel.contains(document.activeElement))return;
      panel.classList.add("network-idle");
    },4200);
  }
  panel.addEventListener("pointermove",()=>{keyboardMode=false;wakePresentation();},{passive:true});
  panel.addEventListener("pointerdown",()=>{keyboardMode=false;wakePresentation();},{passive:true});
  panel.addEventListener("wheel",()=>wakePresentation(),{passive:true});
  panel.addEventListener("focusin",()=>wakePresentation());
  function present(value) {
    if (presentation === value) return;
    presentation=value;wakePresentation();panel.classList.toggle("constellation-presentation",value);document.body.classList.toggle("constellation-presenting",value);
    el("constellation-present").textContent=value ? "Exit presentation ↙" : "Present ↗";
    el("constellation-present").setAttribute("aria-pressed",String(value));
    if (value) {
      replaySignals();
      previousFocus=document.activeElement;
      panel.setAttribute("role","dialog");panel.setAttribute("aria-modal","true");
      for (let branch=panel;branch.parentElement && branch!==document.body;branch=branch.parentElement) {
        for (const sibling of branch.parentElement.children) {
          if (sibling===branch || !(sibling instanceof HTMLElement)) continue;
          inertNodes.push([sibling,sibling.inert]);sibling.inert=true;
        }
      }
      el("constellation-present").focus();
    } else {
      panel.removeAttribute("role");panel.removeAttribute("aria-modal");
      for (const [element,inert] of inertNodes) element.inert=inert;
      inertNodes=[];previousFocus?.focus();
    }
    requestAnimationFrame(draw);
  }
  el("constellation-present").addEventListener("click",()=>present(!presentation));
  document.addEventListener("keydown",event=>{
    if (!presentation) return;
    wakePresentation(true);
    if (event.key==="Escape") {present(false);return;}
    if (event.key!=="Tab") return;
    const buttons=[...panel.querySelectorAll("button:not(:disabled)")].filter(button=>button.offsetParent!==null);
    const first=buttons[0],last=buttons.at(-1);
    if (event.shiftKey && (document.activeElement===first || !panel.contains(document.activeElement))) {event.preventDefault();last?.focus();}
    else if (!event.shiftKey && (document.activeElement===last || !panel.contains(document.activeElement))) {event.preventDefault();first?.focus();}
  });
  document.addEventListener("visibilitychange",motion);
  window.addEventListener("resize",draw);
  if(window.ResizeObserver)new ResizeObserver(draw).observe(canvas);
  if(window.IntersectionObserver)new IntersectionObserver(entries=>{visible=entries[0].isIntersecting;motion();},{rootMargin:"100px"}).observe(panel);
  reduced?.addEventListener?.("change",event=>{paused=event.matches;motion();});
  document.addEventListener("gearth-view-cleared",()=>{
    clearCheckpoint();resetSignals();wakePresentation();
    cancelDetail();snapshot=null;countries=[];nodes=[];links=[];localLinks=[];ambientNodes=[];ambientLinks=[];countryId=null;agentId=null;hover=null;selectedTrace=null;recentEvents=[];traceHits=[];markerHits=[];
    panel.dataset.ambientNodes="0";panel.dataset.ambientEdges="0";
    el("constellation-countries").replaceChildren();el("constellation-reset").disabled=true;
    el("constellation-state").textContent="Waiting for world";el("constellation-caption").textContent="Connecting to the simulation";
    el("constellation-hover").textContent=networkHint;renderInspector();draw();
  });
  document.addEventListener("gearth-snapshot",event=>{
    const next=event.detail.snapshot,initial=snapshot?.run.id!==next.run.id;
    if(initial){clearCheckpoint();resetSignals();cancelDetail();countryId=null;agentId=null;selectedTrace=null;yaw=.12;pitch=.12;zoom=1.1;}
    const needsCompatibility=!next.constellation;
    snapshot=next;recentEvents=event.detail.events??[];
    countries=(snapshot.countries??[]).map(country=>({id:country.id,name:country.name,lat:Number(country.lat??0),lon:Number(country.lon??0),nation:snapshot.nations?.find(n=>n.id===country.id),data:snapshot.constellation?.find(item=>item.country_id===country.id)}));
    if(!countries.some(country=>country.id===countryId)){countryId=null;agentId=null;cancelDetail();}
    el("constellation-state").textContent=`${snapshot.run.status==="running"?snapshot.run.mode:"SAVED"} / DAY ${snapshot.tick}`;
    if(needsCompatibility)applyCheckpoint();
    makeScene();ingestSignals(initial);draw();
    if(needsCompatibility)loadCitizenCheckpoint();
    if(agentId){detail=null;renderInspector();loadDetail();}
  });
  renderInspector();motion();
})();
