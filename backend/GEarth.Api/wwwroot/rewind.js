/* Rewind scenario designer. Saved sources are read-only; drafts never invoke a simulation or Neon writes. */
(() => {
  "use strict";
  const $ = id => document.getElementById(id);
  const governments = [
    ["market_democracy", "Market democracy"], ["social_democracy", "Social democracy"],
    ["planned_republic", "Planned economy"], ["dictatorship", "Dictatorship"], ["monarchy", "Monarchy"]
  ];
  // Rates and units reflect SocietyBank, SocietyLabor, Nation and the government's offered policy ranges.
  // These controls describe future interventions; only the existing experiment editor executes interventions.
  const definitions = [
    {id:"wages", label:"Weekly wages", group:"labor", unit:"%", min:50, max:200, step:5, default:100, help:"Scale existing salaries and wage offers relative to this checkpoint."},
    {id:"ore_price", label:"Ore price", group:"labor", unit:"WLD", min:.01, max:10, step:.01, default:.15, help:"Starting price per unit at this country's ore sellers."},
    {id:"fuel_price", label:"Fuel price", group:"labor", unit:"WLD", min:.01, max:10, step:.01, default:.15, help:"Starting fuel price. Explore extraction, energy costs and imports."},
    {id:"food_price", label:"Food price", group:"labor", unit:"WLD", min:.01, max:10, step:.01, default:.4, help:"Starting shelf price. Compare affordability and hunger."},
    {id:"energy_price", label:"Energy price", group:"labor", unit:"WLD", min:.01, max:10, step:.01, default:.25, help:"Starting energy price. Compare production costs and output."},
    {id:"productivity", label:"Country productivity", group:"labor", unit:"×", min:.5, max:2, step:.05, default:1, help:"Proposed override of the country's production multiplier."},
    {id:"loan_rate", label:"Loan interest", group:"credit", unit:"% / year", choices:[6,10,14], default:10, help:"Commercial bank rate on new loans. Existing loan terms stay intact."},
    {id:"deposit_rate", label:"Savings interest", group:"credit", unit:"% / year", choices:[1,3,5], default:3, help:"Commercial bank rate on deposits, accrued weekly."},
    {id:"lending_standard", label:"Lending standard", group:"credit", unit:"", choices:["strict","normal","loose"], default:"normal", help:"Borrower screening and payment coverage; not a central-bank policy."},
    {id:"income_tax", label:"Income tax", group:"policy", unit:"%", min:0, max:60, step:1, default:15, help:"Share of wages collected by the government."},
    {id:"sales_tax", label:"Sales tax", group:"policy", unit:"%", min:0, max:10, step:.5, default:5, help:"Tax included in household purchase prices."},
    {id:"welfare", label:"Weekly welfare", group:"policy", unit:"WLD / person", min:0, max:2, step:.1, default:0, help:"Weekly payment to eligible residents; funded by the treasury."},
    {id:"unemployment", label:"Unemployment benefit", group:"policy", unit:"WLD / week", min:0, max:9, step:.25, default:0, help:"Support for eligible unemployed residents."},
    {id:"clinic_budget", label:"Clinic funding", group:"policy", unit:"WLD / person", min:0, max:1.5, step:.05, default:.5, help:"Weekly capitation funding per resident; compare care and labor health."},
    {id:"food_subsidy", label:"Food subsidy", group:"policy", unit:"%", choices:[0,25,50,75], default:0, help:"Treasury share of eligible households' food bills."}
  ];
  const colors = ["#596d3c","#446777","#866047","#7c744b","#625982","#507d70","#83646f","#596f85","#597c44","#87614d"];
  const state = {sample:false, run:null, runs:new Map(), cursor:null, catalogGeneration:0, generation:0,
    frameGeneration:0, controller:null, checkpoints:[], index:0, frame:null, country:null, baseline:{}, draft:{}, touched:new Set(), preview:null, cursorInitialized:false};
  const node = (tag, text, className) => { const item=document.createElement(tag); if(text!==undefined)item.textContent=text; if(className)item.className=className; return item; };
  const validId = id => typeof id === "string" && /^[A-Za-z0-9_-]{1,100}$/.test(id);
  const message = (id, text, error=false) => { $(id).textContent=text; $(id).classList.toggle("error",error); };
  const govName = id => governments.find(item=>item[0]===id)?.[1] || String(id||"Not recorded").replaceAll("_"," ");
  const display = (value, definition) => value == null ? "Not recorded" : `${typeof value === "number" ? Number(value.toFixed(2)).toLocaleString() : value}${definition?.unit ? " " + definition.unit : ""}`;
  async function get(path, signal) {
    const response=await fetch(path,{method:"GET",cache:"no-store",signal,headers:{Accept:"application/json"}});
    if(!response.ok)throw new Error(response.status===404?"This saved source is no longer available.":`The saved source could not be read (HTTP ${response.status}).`);
    return response.json();
  }
  function clearPreview() { state.preview=null; $("scenario-preview").hidden=true; }
  function clearEditor() {
    state.frame=null; state.country=null; state.baseline={}; state.draft={};
    $("scenario-fields").disabled=true; $("preview-scenario").disabled=true;
    $("editor-heading").textContent="Choose your country";
    $("editor-description").textContent="Its saved conditions will be your starting point.";
    $("draft-changes").replaceChildren(); clearPreview();
    $("country-grid").replaceChildren(node("p","Loading the saved checkpoint…","empty-state"));
    $("country-count").textContent="";
  }
  function catalogOptions() {
    const select=$("rewind-run");select.replaceChildren();
    if(state.sample) {const option=node("option","Sample world · 365 days · illustrative");option.value="sample_world";select.append(option);return;}
    if(state.run && !state.runs.has(state.run)) {const option=node("option",`${state.run} · selected`);option.value=state.run;select.append(option);}
    for(const run of state.runs.values()) {const option=node("option",`${run.run_id} · ${run.status||"saved"}`);option.value=run.run_id;select.append(option);}
    if(!select.options.length){const option=node("option","No saved simulations found");option.value="";select.append(option);}
    if(state.run)select.value=state.run;
  }
  async function discover(older=false) {
    if(state.sample)return;
    const token=++state.catalogGeneration;$("refresh-runs").disabled=true;$("older-runs").disabled=true;
    try {
      const query=new URLSearchParams({limit:"50"});if(older&&state.cursor)query.set("cursor",state.cursor);
      const result=await get(`/api/data/runs?${query}`);
      if(token!==state.catalogGeneration||state.sample)return;
      if(!Array.isArray(result.runs))throw new Error("The saved-run catalog is unavailable.");
      for(const run of result.runs)if(validId(run.run_id))state.runs.set(run.run_id,run);
      if(older||!state.cursorInitialized){state.cursor=result.next_cursor??null;state.cursorInitialized=true;}
      $("older-runs").hidden=!state.cursor;catalogOptions();
      const neon=result.storage==="neon_https";
      message("source-badge",neon?"Neon · saved checkpoints":result.storage==="local"?"Local saved checkpoints":"Saved source");
      message("source-status",`${state.runs.size} saved simulations found. ${neon?"Reading this server's configured Neon branch.":"These are this server's saved runs; they are not a count of Databricks imports."}`);
      if(!state.run&&state.runs.size)selectRun(state.runs.keys().next().value);
      else if(!state.run)message("checkpoint-status","No saved runs found. Use the sample world to try the UI.");
    } catch(error) {
      if(token!==state.catalogGeneration||state.sample)return;
      message("source-badge","Saved source unavailable",true);
      message("source-status",`${error.message} You can still choose “Use sample world” to preview the flow.`,true);
    } finally {if(token===state.catalogGeneration){$("refresh-runs").disabled=false;$("older-runs").disabled=false;}}
  }
  function updateLinks() {
    const suffix=!state.sample&&validId(state.run)?`?run_id=${encodeURIComponent(state.run)}`:"";
    $("saved-data-link").href="/data.html"+suffix;
    $("real-experiments-link").href="/"+suffix+"#experiments-panel";
    $("real-experiments-link").hidden=state.sample;
  }
  function checkpointLabel(point) {return point.tick===0?"Opening":point.kind==="terminal"&&point.tick%7!==0?`Day ${point.tick} · partial week`:`Week ${Math.ceil(point.tick/7)}`;}
  function renderWeeks() {
    const slider=$("week-slider"),points=state.checkpoints;
    slider.max=String(Math.max(0,points.length-1));slider.value=String(state.index);slider.disabled=!points.length;
    $("week-prev").disabled=!points.length||state.index===0;$("week-next").disabled=!points.length||state.index===points.length-1;
    $("week-list").replaceChildren();
    points.forEach((point,index)=>{const button=node("button",point.tick===0?"Start":point.tick%7!==0?`D${point.tick}`:`W${point.tick/7}`);
      button.type="button";button.title=`${checkpointLabel(point)} · day ${point.tick}${point.replay_available?"":" · state unavailable"}`;
      button.setAttribute("aria-label",button.title);button.setAttribute("aria-pressed",String(index===state.index));
      button.addEventListener("click",()=>chooseWeek(index));$("week-list").append(button);
    });
    const point=points[state.index];message("week-title",point?checkpointLabel(point):"No saved weekly checkpoints");
    message("week-description",point?`Day ${point.tick}${state.sample?" · sample state":" · saved revision "+point.revision}`:"");
  }
  async function selectRun(run) {
    if(!validId(run))return;
    state.run=run;state.controller?.abort();state.controller=new AbortController();
    const signal=state.controller.signal,token=++state.generation;state.frameGeneration++;state.checkpoints=[];state.index=0;clearEditor();renderWeeks();updateLinks();
    const url=new URL(location.href);if(state.sample)url.searchParams.delete("run_id");else url.searchParams.set("run_id",run);url.searchParams.set("mode",state.sample?"sample":"saved");history.replaceState(null,"",url);
    if(state.sample){state.checkpoints=[{tick:0,revision:1,kind:"opening",replay_available:true},...Array.from({length:52},(_,index)=>({tick:(index+1)*7,revision:index+2,kind:"week",replay_available:true})),{tick:365,revision:54,kind:"terminal",replay_available:true}];state.index=12;renderWeeks();chooseWeek(state.index);return;}
    message("checkpoint-status","Reading all saved checkpoint pages…");
    try {
      let after=0,more=true;const points=new Map();
      while(more) {
        const data=await get(`/api/runs/${encodeURIComponent(run)}/data/checkpoints?after_revision=${after}&limit=100`,signal);
        if(token!==state.generation)return;
        if(!Array.isArray(data.checkpoints))throw new Error("No valid saved checkpoint list was returned.");
        for(const point of data.checkpoints)if(Number.isSafeInteger(point.tick)&&point.tick>=0&&Number.isSafeInteger(point.revision)) {
          // Pre-save markers are not full weeks. Terminal partial weeks are kept, gaps stay visible.
          if(point.tick===0||point.tick%7===0||point.kind==="terminal") {
            const existing=points.get(point.tick);if(!existing||point.replay_available||!existing.replay_available)points.set(point.tick,point);
          }
        }
        more=data.has_more===true;
        if(more&&(!Number.isSafeInteger(data.next_after_revision)||data.next_after_revision<=after))throw new Error("The checkpoint cursor did not advance. Refresh the saved source.");
        after=data.next_after_revision??after;
      }
      state.checkpoints=[...points.values()].sort((a,b)=>a.tick-b.tick);state.index=Math.max(0,state.checkpoints.length-1);renderWeeks();
      if(state.checkpoints.length)await chooseWeek(state.index);
      else {$("country-grid").replaceChildren(node("p","No weekly replay state was captured for this run. Try another run or the sample world.","empty-state"));message("checkpoint-status","No weekly replay state captured. Daily analytics rows alone cannot restore the simulation.");}
    }catch(error){if(token===state.generation&&error.name!=="AbortError"){message("checkpoint-status",error.message,true);$("country-grid").replaceChildren(node("p","This run's saved checkpoints could not be read. Try another run or the sample world.","empty-state"));}}
  }
  async function chooseWeek(index) {
    state.index=Math.max(0,Math.min(state.checkpoints.length-1,index));const point=state.checkpoints[state.index];if(!point)return;
    const token=++state.frameGeneration,run=state.run;clearEditor();renderWeeks();
    if(!point.replay_available){message("checkpoint-status","This save marker has no replay state. Choose a checkpoint with captured state.",true);$("country-grid").replaceChildren(node("p","Country state was not captured at this checkpoint.","empty-state"));return;}
    message("checkpoint-status",state.sample?"Sample checkpoints and country values are illustrative. No database was read.":"Reading the original country state at this checkpoint…");
    try {
      const frame=state.sample?sampleFrame(point):await get(`/api/runs/${encodeURIComponent(run)}/renderer/frames/${point.revision}`,state.controller?.signal);
      if(token!==state.frameGeneration||run!==state.run)return;
      if(!state.sample&&(frame.run_id!==run||frame.revision!==point.revision||frame.tick!==point.tick))throw new Error("Saved frame identity does not match this checkpoint.");
      if(!Array.isArray(frame.snapshot?.nations)||!frame.snapshot.nations.length)throw new Error("No country state is available in this saved checkpoint.");
      state.frame=frame;renderCountries();selectCountry(frame.snapshot.nations[0].id);
      const count=state.checkpoints.filter(item=>item.tick>0&&item.tick%7===0).length;
      message("checkpoint-status",state.sample?"Sample world · 52 full weeks + the final day (365). All values below are illustrative.":`${count} saved full-week checkpoints in this run. Showing the actual day ${point.tick} state. Checkpoint gaps are not filled with invented data.`);
    }catch(error){if(token===state.frameGeneration&&error.name!=="AbortError"){message("checkpoint-status",error.message,true);$("country-grid").replaceChildren(node("p","This checkpoint's country state is unavailable. Try a different saved week.","empty-state"));}}
  }
  function landscape(index) {
    // Original vector terrain; no external images or unsanitized saved content.
    const ns="http://www.w3.org/2000/svg",svg=document.createElementNS(ns,"svg");svg.setAttribute("viewBox","0 0 200 140");svg.setAttribute("aria-hidden","true");
    const shape=(tag,attributes)=>{const item=document.createElementNS(ns,tag);for(const[key,value]of Object.entries(attributes))item.setAttribute(key,value);svg.append(item);};
    shape("circle",{cx:140-index%3*25,cy:40,r:20,fill:"#d9ebbc",opacity:".45"});
    shape("path",{d:"M0 110 L45 45 L81 100 L120 62 L177 114 L200 90 L200 140 L0 140Z",fill:"#172a23",opacity:".75"});
    shape("path",{d:"M0 120 Q55 94 105 115 T200 105 L200 140 L0 140Z",fill:"#597958",opacity:".75"});
    shape("path",{d:"M0 133 Q70 113 125 130 T200 119 L200 140 L0 140Z",fill:"#263f32"});
    for(let i=0;i<6;i++)shape("rect",{x:63+i*12,y:107-(i+index)%3*9,width:8,height:15+(i+index)%3*9,fill:"#dae2c0",opacity:".65"});
    return svg;
  }
  function renderCountries() {
    const nations=state.frame.snapshot.nations;$("country-grid").replaceChildren();message("country-count",`${nations.length} countries`);
    nations.forEach((nation,index)=>{
      const card=node("button",undefined,"country-card");card.type="button";card.dataset.country=nation.id;card.setAttribute("aria-pressed","false");card.setAttribute("aria-label",`Select ${nation.name||nation.id}`);card.style.setProperty("--country-color",colors[index%colors.length]);
      const art=node("div",undefined,"card-art");art.append(landscape(index),node("span",String(index+1).padStart(2,"0"),"card-index"));
      const check=node("span","✓","card-selected");check.hidden=true;check.setAttribute("aria-hidden","true");art.append(check);
      const info=node("div",undefined,"card-info");info.append(node("h3",nation.name||nation.id),node("span",govName(nation.government_id),"card-government"));
      const stats=node("div",undefined,"card-stats");stats.append(node("span",`${nation.population??"—"} agents`),node("strong",nation.tax_basis_points==null?"Tax unknown":`${nation.tax_basis_points/100}% tax`));info.append(stats);card.append(art,info);
      card.addEventListener("click",()=>selectCountry(nation.id));$("country-grid").append(card);
    });
  }
  function finite(value) {return typeof value==="number"&&Number.isFinite(value)?value:null;}
  function average(agents,field,filter) {const values=agents.filter(filter).map(agent=>finite(agent[field])).filter(value=>value!==null);return values.length?Math.round(values.reduce((a,b)=>a+b,0)/values.length)/100:null;}
  function baseline(nation) {
    const agents=(state.frame.snapshot.agents||[]).filter(agent=>(agent.country_id??agent.country)===nation.id);
    const bank=agents.find(agent=>agent.role==="business"&&agent.specialty==="bank");
    const goods={};for(const good of ["ore","fuel","food","energy"])goods[good+"_price"]=average(agents,"price_minor",agent=>agent.role==="business"&&agent.specialty===({ore:"mine",fuel:"well"}[good]||good));
    return {...goods,wages:100,productivity:finite(nation.productivity),loan_rate:finite(bank?.loan_rate_pct),deposit_rate:finite(bank?.deposit_rate_pct),
      lending_standard:bank?.lending_standard??null,income_tax:finite(nation.tax_basis_points)===null?null:nation.tax_basis_points/100,
      sales_tax:finite(nation.sales_tax_basis_points)===null?null:nation.sales_tax_basis_points/100,welfare:finite(nation.welfare_minor)===null?null:nation.welfare_minor/100,
      unemployment:finite(nation.unemployment_benefit_minor)===null?null:nation.unemployment_benefit_minor/100,clinic_budget:finite(nation.service_budget_per_resident_minor)===null?null:nation.service_budget_per_resident_minor/100,
      food_subsidy:finite(nation.food_subsidy_pct),government:nation.government_id??null};
  }
  function selectCountry(id) {
    const nation=state.frame?.snapshot.nations.find(item=>item.id===id);if(!nation)return;
    clearPreview();state.country=nation;state.baseline=baseline(nation);state.draft={...state.baseline};state.touched.clear();
    for(const definition of definitions)if(state.draft[definition.id]===null)state.draft[definition.id]=definition.default;
    state.draft.government??="market_democracy";
    $("scenario-fields").disabled=false;$("editor-heading").textContent=nation.name||nation.id;
    message("editor-description",`${state.sample?"Illustrative sample":"Saved"} day ${state.frame.tick} · ${govName(nation.government_id)}`);
    $("scenario-name").value="";
    document.querySelectorAll(".country-card").forEach(card=>{const selected=card.dataset.country===id;card.setAttribute("aria-pressed",String(selected));card.querySelector(".card-selected").hidden=!selected;});
    renderControls();renderChanges();
  }
  function addOptions(select, choices, current) {
    select.replaceChildren();const values=[...choices];if(current!=null&&!values.includes(current))values.unshift(current);
    for(const choice of values){const option=node("option",String(choice).replaceAll("_"," "));option.value=String(choice);select.append(option);}select.value=String(current);
  }
  function renderControls() {
    for(const group of ["labor","credit","policy"])$("controls-"+group).replaceChildren();
    $("government").replaceChildren();const profiles=[...governments];if(!profiles.some(([id])=>id===state.draft.government))profiles.unshift([state.draft.government,govName(state.draft.government)]);
    for(const[id,name]of profiles){const option=node("option",name);option.value=id;$("government").append(option);}$("government").value=state.draft.government;
    for(const definition of definitions) {
      const block=node("div",undefined,"economic-control"),row=node("div",undefined,"control-label"),label=node("label",definition.label);
      label.htmlFor="value-"+definition.id;row.append(label);
      const value=state.draft[definition.id];
      if(definition.choices) {
        const select=node("select");select.id="value-"+definition.id;addOptions(select,definition.choices,value);select.addEventListener("change",()=>change(definition.id,typeof definition.default==="number"?Number(select.value):select.value));
        block.append(row,select);
      }else {
        const holder=node("span",undefined,"control-value"),input=node("input");input.type="number";input.id="value-"+definition.id;input.min=Math.min(definition.min,value);input.max=Math.max(definition.max,value);input.step=definition.step;input.value=value;input.required=true;
        holder.append(input,node("span",definition.unit));row.append(holder);block.append(row);
        const range=node("input");range.type="range";range.id="range-"+definition.id;range.min=input.min;range.max=input.max;range.step=input.step;range.value=value;range.setAttribute("aria-label",definition.label);
        range.addEventListener("input",()=>{input.value=range.value;change(definition.id,Number(range.value));});
        input.addEventListener("input",()=>{if(input.value!==""&&input.checkValidity()){range.value=input.value;change(definition.id,Number(input.value));}else{$("preview-scenario").disabled=true;clearPreview();}});
        block.append(range);
      }
      block.append(node("p",definition.help+(definition.choices&&definition.unit?` (${definition.unit})`:""),"control-help"));
      block.append(node("p",state.baseline[definition.id]===null?`Not recorded at this checkpoint · ${display(value,definition)} is a draft starting value.`:`${state.sample?"Sample":"Saved"}: ${display(state.baseline[definition.id],definition)}`,"original-value"));
      $("controls-"+definition.group).append(block);
    }
  }
  function change(id,value) {state.draft[id]=value;state.touched.add(id);clearPreview();renderChanges();}
  function changes() {
    if(!state.country)return[];
    const result=[];
    if(state.draft.government!==state.baseline.government&&(state.baseline.government!==null||state.touched.has("government")))result.push({field:"government",label:"Government",before:state.baseline.government,value:state.draft.government,unit:""});
    for(const definition of definitions)if(state.draft[definition.id]!==state.baseline[definition.id]&&(state.baseline[definition.id]!==null||state.touched.has(definition.id)))result.push({field:definition.id,label:definition.label,before:state.baseline[definition.id],value:state.draft[definition.id],unit:definition.unit});
    return result;
  }
  function changeText(edit) {return edit.field==="government"?`${edit.label}: ${govName(edit.before)} → ${govName(edit.value)}`:`${edit.label}: ${display(edit.before,edit)} → ${display(edit.value,edit)}`;}
  function renderChanges() {
    const edits=changes();$("draft-changes").replaceChildren(...edits.map(edit=>node("li",changeText(edit))));
    if(!edits.length)$("draft-changes").append(node("li","No changes yet. Adjust a control or choose a quick experiment."));
    $("preview-scenario").disabled=!state.country||!edits.length||!$("scenario-form").checkValidity();
  }
  function preset(kind) {
    if(!state.country)return;
    if(kind==="wages"){state.draft.wages=120;state.touched.add("wages");}
    if(kind==="costs")for(const id of["ore_price","fuel_price","energy_price"]){state.draft[id]=Math.min(10,Math.round((state.baseline[id]??definitions.find(d=>d.id===id).default)*1.5*100)/100);state.touched.add(id);}
    if(kind==="credit"){state.draft.loan_rate=6;state.draft.lending_standard="loose";state.touched.add("loan_rate");state.touched.add("lending_standard");}
    clearPreview();renderControls();renderChanges();
  }
  function preview(event) {
    event.preventDefault();if(!state.country||!changes().length||!$("scenario-form").reportValidity())return;
    const days=Number($("scenario-days").value),point=state.checkpoints[state.index],edits=changes();
    state.preview={schema_version:"gearth.scenario-draft.v1",preview_only:true,source:state.sample?"illustrative_sample":"saved_checkpoint",run_id:state.run,
      checkpoint:{revision:point.revision,tick:point.tick},country_id:state.country.id,name:$("scenario-name").value.trim()||`${state.country.name||state.country.id} · alternate timeline`,days,edits:edits.map(({field,before,value,unit})=>({field,before,value,unit})),
      execution_status:"not_started",integration_required:true};
    $("scenario-preview").hidden=false;$("preview-heading").textContent=state.preview.name;
    message("preview-summary",`${state.country.name||state.country.id} · ${checkpointLabel(point)} · day ${point.tick} → ${point.tick+days} · ${edits.length} proposed ${edits.length===1?"change":"changes"}${state.sample?" · illustrative sample":" · saved checkpoint"}`);
    message("branch-parent-label",`${checkpointLabel(point)} · day ${point.tick}`);message("branch-draft-label",state.preview.name);message("branch-draft-duration",`${days} days forward · configuration preview`);
    $("preview-changes").replaceChildren(...edits.map(edit=>node("li",changeText(edit))));
    const metrics=new Set(["Employment, disposable income and household needs","Treasury income, spending and balance"]);
    for(const edit of edits) {
      if(["wages","income_tax","welfare","unemployment"].includes(edit.field))metrics.add("Real income, wage arrears and inequality");
      if(edit.field.endsWith("_price")||["productivity","food_subsidy"].includes(edit.field))metrics.add("Food affordability, production, imports and firm closures");
      if(["loan_rate","deposit_rate","lending_standard"].includes(edit.field))metrics.add("New lending, defaults, deposits and bank equity");
      if(edit.field==="clinic_budget")metrics.add("Treatment, health and productive workdays");
    }
    $("research-metrics").replaceChildren(...[...metrics].map(metric=>node("li",metric)));
    $("scenario-preview").focus();$("scenario-preview").scrollIntoView?.({behavior:matchMedia("(prefers-reduced-motion: reduce)").matches?"auto":"smooth",block:"start"});
  }
  function sampleFrame(point) {
    const geography=[["aster","Jaypan"],["beryl","Beryl"],["cinder","Papa New Kenny"],["dune","Capybarlos"],["ember","Lebanonia"],["fjord","Fjord"],["grove","Dyland"],["haven","Haven"],["ionia","Ionia"],["jade","Jade"]];
    const nations=geography.map(([id,name],index)=>({id,name,government_id:governments[index%5][0],population:100,
      tax_basis_points:[1500,3000,2500,1000,2000][index%5],sales_tax_basis_points:500,welfare_minor:[0,100,150,0,50][index%5],unemployment_benefit_minor:0,service_budget_per_resident_minor:50,food_subsidy_pct:0,productivity:1}));
    const agents=nations.flatMap(nation=>[...["mine","well","food","energy"].map((specialty,index)=>({id:nation.id+"_"+specialty,country_id:nation.id,role:"business",specialty,price_minor:[15,15,40,25][index]})),
      {id:nation.id+"_bank",country_id:nation.id,role:"business",specialty:"bank",loan_rate_pct:10,deposit_rate_pct:3,lending_standard:"normal"}]);
    return {run_id:"sample_world",revision:point.revision,tick:point.tick,snapshot:{nations,agents}};
  }
  function toggleSample() {
    state.sample=!state.sample;state.catalogGeneration++;state.controller?.abort();state.generation++;state.frameGeneration++;state.run=null;state.checkpoints=[];clearEditor();renderWeeks();
    $("demo-toggle").textContent=state.sample?"Use saved simulations":"Use sample world";$("refresh-runs").hidden=state.sample;$("refresh-runs").disabled=false;$("older-runs").hidden=true;
    if(state.sample){message("source-badge","Sample world · illustrative");message("source-status","Try the full 365-day journey using sample data. No live simulation or database is changed.");catalogOptions();selectRun("sample_world");}
    else{catalogOptions();discover();}
  }
  $("rewind-run").addEventListener("change",()=>selectRun($("rewind-run").value));$("refresh-runs").addEventListener("click",()=>discover());$("older-runs").addEventListener("click",()=>discover(true));$("demo-toggle").addEventListener("click",toggleSample);
  $("week-slider").addEventListener("input",()=>chooseWeek(Number($("week-slider").value)));$("week-prev").addEventListener("click",()=>chooseWeek(state.index-1));$("week-next").addEventListener("click",()=>chooseWeek(state.index+1));
  $("government").addEventListener("change",()=>change("government",$("government").value));$("scenario-days").addEventListener("change",clearPreview);$("scenario-name").addEventListener("input",clearPreview);
  document.querySelectorAll("[data-preset]").forEach(button=>button.addEventListener("click",()=>preset(button.dataset.preset)));
  function selectTab(button) {document.querySelectorAll("[data-group]").forEach(tab=>{const selected=tab===button;tab.setAttribute("aria-selected",String(selected));tab.tabIndex=selected?0:-1;$("controls-"+tab.dataset.group).hidden=!selected;});}
  const tabs=[...document.querySelectorAll("[data-group]")];tabs.forEach((button,index)=>{button.addEventListener("click",()=>selectTab(button));button.addEventListener("keydown",event=>{let next;if(event.key==="ArrowRight")next=(index+1)%tabs.length;if(event.key==="ArrowLeft")next=(index+tabs.length-1)%tabs.length;if(event.key==="Home")next=0;if(event.key==="End")next=tabs.length-1;if(next!==undefined){event.preventDefault();selectTab(tabs[next]);tabs[next].focus();}});});
  $("reset-draft").addEventListener("click",()=>selectCountry(state.country.id));$("scenario-form").addEventListener("submit",preview);
  $("edit-scenario").addEventListener("click",()=>{$("editor-heading").scrollIntoView?.({block:"start"});$("government").focus();});
  $("download-scenario").addEventListener("click",()=>{if(!state.preview)return;const url=URL.createObjectURL(new Blob([JSON.stringify(state.preview,null,2)],{type:"application/json"})),a=node("a");a.href=url;a.download="gearth-scenario-draft.json";document.body.append(a);a.click();a.remove();setTimeout(()=>URL.revokeObjectURL(url),1000);});
  window.addEventListener("pagehide",()=>{state.catalogGeneration++;state.generation++;state.frameGeneration++;state.controller?.abort();});
  const parameters=new URLSearchParams(location.search),requested=parameters.get("run_id");
  if(parameters.get("mode")==="sample")toggleSample();
  else {if(validId(requested)){state.run=requested;catalogOptions();selectRun(requested);}discover();}
})();
