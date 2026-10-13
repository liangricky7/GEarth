const {test}=require("node:test");
const assert=require("node:assert/strict");
const {readFileSync}=require("node:fs");
const {JSDOM}=require("jsdom");
const root="backend/GEarth.Api/wwwroot/";
const until=async predicate=>{for(let i=0;i<150;i++){if(predicate())return;await new Promise(resolve=>setTimeout(resolve,5));}throw Error("Rewind UI did not settle");};
function frame(run="world_saved",revision=2,tick=7) {
  return {run_id:run,revision,tick,snapshot:{nations:[{id:"aster",name:"Jaypan",government_id:"market_democracy",population:100,tax_basis_points:1700,sales_tax_basis_points:500,welfare_minor:100,unemployment_benefit_minor:200,service_budget_per_resident_minor:75,food_subsidy_pct:25,productivity:1.1}],agents:[{id:"mine",country_id:"aster",role:"business",specialty:"mine",price_minor:22},{id:"bank",country_id:"aster",role:"business",specialty:"bank",loan_rate_pct:14,deposit_rate_pct:5,lending_standard:"strict"}]}};
}
function ui(options={}) {
  const dom=new JSDOM(readFileSync(root+"rewind.html","utf8"),{url:"https://gearth.test/rewind.html"+(options.sample?"?mode=sample":""),runScripts:"outside-only"});
  const w=dom.window,get=id=>w.document.getElementById(id),calls=[],state={custom:options.custom,downloads:[]};
  w.matchMedia=()=>({matches:true});w.HTMLElement.prototype.scrollIntoView=()=>{};
  w.URL.createObjectURL=blob=>{state.downloads.push(blob);return"blob:test";};w.URL.revokeObjectURL=()=>{};w.HTMLAnchorElement.prototype.click=function(){};
  w.fetch=async(url,settings)=>{
    calls.push({url,settings});let value;
    if(state.custom)value=await state.custom(url,settings);
    if(value===undefined) {
      if(url.startsWith("/api/data/runs"))value={storage:"neon_https",runs:[{run_id:"world_saved",status:"completed"}],next_cursor:null};
      else if(url.includes("/data/checkpoints"))value={checkpoints:[{tick:0,revision:1,kind:"opening",replay_available:true},{tick:6,revision:9,kind:"save_marker",replay_available:false},{tick:7,revision:2,kind:"week",replay_available:true}],has_more:false};
      else if(url.endsWith("/frames/1"))value=frame("world_saved",1,0);
      else value=frame();
    }
    if(value instanceof Error)throw value;
    return{ok:true,json:async()=>value};
  };
  w.eval(readFileSync(root+"rewind.js","utf8"));return {w,get,calls,state,close:()=>w.close()};
}
test("saved country values come from the exact weekly frame; pre-week save markers are excluded",async()=>{
  const app=ui();try{await until(()=>!app.get("scenario-fields").disabled);
    assert.equal(app.get("value-income_tax").value,"17");assert.equal(app.get("value-ore_price").value,"0.22");assert.equal(app.get("value-loan_rate").value,"14");
    assert.equal(app.get("value-clinic_budget").value,"0.75");assert.equal(app.get("week-list").children.length,2);
    assert.match(app.get("source-badge").textContent,/Neon/);assert.match(app.get("week-title").textContent,/Week 1/);
    assert(app.calls.every(call=>call.settings.method==="GET"));
  }finally{app.close();}
});
test("sample flow includes all 52 full weeks and final partial day; preview never writes or fabricates outcomes",async()=>{
  const app=ui({sample:true});try{await until(()=>!app.get("scenario-fields").disabled);
    assert.equal(app.get("week-list").children.length,54);assert.equal(app.calls.length,0);
    app.get("week-slider").value="53";app.get("week-slider").dispatchEvent(new app.w.Event("input"));
    assert.match(app.get("week-title").textContent,/365.*partial week/);
    app.w.document.querySelector('[data-preset="wages"]').click();
    app.get("scenario-form").dispatchEvent(new app.w.Event("submit",{cancelable:true}));
    assert.equal(app.get("scenario-preview").hidden,false);assert.match(app.get("preview-changes").textContent,/100.*120/);
    assert.match(app.get("preview-limit").textContent,/No Neon branch/);assert.equal(app.get("real-experiments-link").hidden,true);
    app.get("download-scenario").click();assert.equal(app.state.downloads.length,1);assert.equal(app.calls.length,0);
  }finally{app.close();}
});
test("checkpoint paging keeps gaps; does not invent missing weeks",async()=>{
  const app=ui({custom:async url=>{
    if(url.includes("checkpoints?after_revision=0"))return{checkpoints:[{tick:7,revision:2,kind:"week",replay_available:true}],has_more:true,next_after_revision:2};
    if(url.includes("checkpoints?after_revision=2"))return{checkpoints:[{tick:21,revision:4,kind:"week",replay_available:true}],has_more:false};
    if(url.endsWith("/frames/4"))return frame("world_saved",4,21);
  }});try{await until(()=>!app.get("scenario-fields").disabled);assert.deepEqual([...app.get("week-list").children].map(button=>button.textContent),["W1","W3"]);assert.match(app.get("week-title").textContent,/Week 3/);}finally{app.close();}
});
test("changing weeks clears stale edits and late historical frames cannot replace current selection",async()=>{
  let resolve;const pending=new Promise(done=>resolve=done);
  const app=ui({custom:async url=>url.endsWith("/frames/1")?pending:undefined});try{
    await until(()=>!app.get("scenario-fields").disabled);app.w.document.querySelector('[data-preset="wages"]').click();
    app.get("week-prev").click();assert.equal(app.get("scenario-fields").disabled,true);
    app.get("week-next").click();await until(()=>!app.get("scenario-fields").disabled);
    resolve(frame("world_saved",1,0));await new Promise(done=>setTimeout(done,15));
    assert.match(app.get("editor-description").textContent,/day 7/);assert.equal(app.get("value-wages").value,"100");
  }finally{app.close();}
});
test("missing historical fields are explicit and invalid inputs block preview",async()=>{
  const missing=frame();delete missing.snapshot.nations[0].service_budget_per_resident_minor;
  const app=ui({custom:async url=>url.includes("/frames/")?missing:undefined});try{await until(()=>!app.get("scenario-fields").disabled);
    assert.match(app.get("value-clinic_budget").closest(".economic-control").textContent,/Not recorded/);
    app.w.document.querySelector('[data-preset="wages"]').click();
    assert.equal(app.get("draft-changes").children.length,1,"Untouched missing fields must not become inferred overrides");
    const input=app.get("value-wages");input.value="999";input.dispatchEvent(new app.w.Event("input"));assert.equal(app.get("preview-scenario").disabled,true);
    app.get("scenario-form").dispatchEvent(new app.w.Event("submit",{cancelable:true}));assert.equal(app.get("scenario-preview").hidden,true);
  }finally{app.close();}
});
test("mismatched saved frame is rejected",async()=>{
  const app=ui({custom:async url=>url.includes("/frames/")?frame("wrong_run"):undefined});try{
    await until(()=>app.get("checkpoint-status").textContent.includes("identity"));assert.equal(app.get("scenario-fields").disabled,true);
  }finally{app.close();}
});
