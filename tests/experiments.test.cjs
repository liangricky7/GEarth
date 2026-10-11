const {test} = require("node:test");
const assert = require("node:assert/strict");
const {readFileSync} = require("node:fs");
const {JSDOM} = require("jsdom");
const root = "backend/GEarth.Api/wwwroot/";
async function until(predicate) {
  for(let count=0;count<150;count++) { if(predicate()) return; await new Promise(resolve=>setTimeout(resolve,5)); }
  throw new Error("Experiment UI did not settle");
}
function deferred() { let resolve;const promise=new Promise(done=>resolve=done);return {promise,resolve}; }
function ui() {
  const dom = new JSDOM(readFileSync(root+"index.html","utf8"), {url:"https://team.example.test",runScripts:"outside-only"});
  const window = dom.window, get=id=>window.document.getElementById(id), calls=[];
  const job = {id:"experiment_test",state:"queued",fork_tick:6,end_tick:13,tick:6,mode:"RULE",edit:{scope:"agent",target_id:"resident",field:"health",expected_value:90,value:20}};
  const state = {jobs:[],delays:{},errors:{},callbacks:[]};
  const data = {complete:false,experiment:job,aggregation:"selected agent",note:"Rule decisions.",metrics:{health:{label:"Health",unit:"points",scale:1},cash_minor:{label:"Cash",unit:"WLD",scale:.01},net_wealth_minor:{label:"Net wealth",unit:"WLD",scale:.01},wellbeing:{label:"Quality of life",unit:"points",scale:1}},timelines:{control:[],changed:[]}};
  get("runs").append(new window.Option("world_old","world_old"));get("runs").value="world_old";
  get("experiment-dialog").showModal=()=>{get("experiment-dialog").open=true;};
  get("experiment-dialog").close=()=>{get("experiment-dialog").open=false;get("experiment-dialog").dispatchEvent(new window.Event("close"));};
  window.setInterval = callback => {state.callbacks.push(callback);return 0;};
  window.fetch = async (url,options={}) => {
    const body=options.body ? JSON.parse(options.body) : null;
    calls.push({url,body});
    let value;
    if(url.endsWith("/capabilities")) value={enabled:true};
    else if(url.includes("/checkpoints")) value={checkpoints:[{revision:2,tick:6}]};
    else if(url.endsWith("/previews")) value={id:"preview_test",run_id:"world_old",tick:6,parent_mode:"LIVE"};
    else if(url.includes("/targets?")) value={targets:[{id:"resident",label:"resident · farmer",values:{health:90,productivity:1}}],total:1,fields:{health:{label:"Health",min:0,max:100,step:1},productivity:{label:"Productivity",min:.6,max:1.4,step:.01}}};
    else if(url.endsWith("/comparison")) value=data;
    else if(url.endsWith("/cancel")) {job.state="cancelled";value=job;}
    else if(options.method==="POST") {state.jobs=[job];value=job;}
    else value={experiments:state.jobs};
    if(state.delays[url]) value=await state.delays[url].promise;
    if(state.errors[url]) return {ok:false,status:409,json:async()=>({detail:state.errors[url]})};
    return {ok:true,status:200,json:async()=>value};
  };
  window.eval(readFileSync(root+"experiments.js","utf8"));
  const auth=authenticated=>window.document.dispatchEvent(new window.CustomEvent("gearth-controls",{detail:{authenticated,enabled:true}}));
  return {window,get,state,calls,job,data,auth,refresh:()=>state.callbacks[0]()};
}
async function editor(app) {
  await until(()=>!app.get("experiments-panel").hidden);app.auth(true);
  app.get("experiment-create").click();
  await until(()=>app.get("experiment-checkpoint").options.length===1);
  app.get("experiment-preview").click();
  await until(()=>!app.get("experiment-editor").disabled);
}
test("authentication gates creation and editor values come from the historical checkpoint",async()=>{
  const app=ui();await until(()=>!app.get("experiments-panel").hidden);
  assert.equal(app.get("experiment-create").disabled,true);
  await editor(app);
  assert.match(app.get("experiment-source").textContent,/day 6.*parent LIVE.*experiment RULE/);
  assert.match(app.get("experiment-original").textContent,/90/);
  assert.equal(app.get("experiment-value").value,"90");
  assert.equal(app.get("experiment-submit").disabled,true);
  app.auth(false);assert.equal(app.get("experiment-dialog").open,false);
  app.window.close();
});
test("submission pins the revision, sends expected value and horizon, and displays paired results",async()=>{
  const app=ui();await editor(app);
  app.get("experiment-value").value="20";app.get("experiment-value").dispatchEvent(new app.window.Event("input"));
  app.get("experiment-days").value="7";
  app.get("experiment-form").dispatchEvent(new app.window.Event("submit",{cancelable:true}));
  await until(()=>app.calls.some(call=>call.body?.request_key));
  const request=app.calls.find(call=>call.body?.request_key).body;
  assert.equal(request.preview_id,"preview_test");assert.equal(request.days,7);
  assert.deepEqual(request.edit,{scope:"agent",target_id:"resident",field:"health",value:20,expected_value:90});
  await until(()=>!app.get("experiment-dialog").open);
  app.job.state="completed";app.data.complete=true;
  app.data.timelines={control:[{tick:6,health:90},{tick:13,health:80}],changed:[{tick:6,health:20},{tick:13,health:40}]};
  app.refresh();await until(()=>app.get("experiment-progress").textContent.includes("Comparison complete"));
  assert.match(app.get("experiment-results").textContent,/Unchanged control/);
  assert.match(app.get("experiment-results").textContent,/-40/);
  assert.equal(app.get("experiment-cancel").hidden,true);
  app.window.close();
});
test("an expired Neon checkpoint shows an actionable failure and leaves the editor disabled",async()=>{
  const app=ui();app.state.errors["/api/control/experiments/previews"]="Checkpoint is outside the history window.";
  await until(()=>!app.get("experiments-panel").hidden);app.auth(true);app.get("experiment-create").click();
  await until(()=>app.get("experiment-checkpoint").options.length===1);app.get("experiment-preview").click();
  await until(()=>app.get("experiment-editor-message").textContent.includes("history window"));
  assert.equal(app.get("experiment-editor").disabled,true);
  app.window.close();
});
test("switching saved runs discards a delayed checkpoint preview",async()=>{
  const app=ui(),pending=deferred();app.state.delays["/api/control/experiments/previews"]=pending;
  await until(()=>!app.get("experiments-panel").hidden);app.auth(true);app.get("experiment-create").click();
  await until(()=>app.get("experiment-checkpoint").options.length===1);app.get("experiment-preview").click();
  await until(()=>app.calls.some(call=>call.url.endsWith("/previews")));
  app.window.document.dispatchEvent(new app.window.CustomEvent("gearth-run-selected",{detail:{runId:"other"}}));
  assert.equal(app.get("experiment-create").disabled,false);
  pending.resolve({id:"stale_preview",tick:6,parent_mode:"LIVE"});
  await new Promise(resolve=>setTimeout(resolve,20));
  assert.equal(app.get("experiment-dialog").open,false);assert.equal(app.get("experiment-editor").disabled,true);
  assert.equal(app.calls.some(call=>call.url.includes("stale_preview/targets")),false);
  app.window.close();
});
test("partial results show no final delta and cancellation targets the selected experiment",async()=>{
  const app=ui();app.state.jobs=[app.job];
  await until(()=>app.get("experiment-list").options.length===2);app.auth(true);
  app.data.timelines={control:[{tick:13,health:80}],changed:[{tick:8,health:30}]};
  app.get("experiment-list").value=app.job.id;app.get("experiment-list").dispatchEvent(new app.window.Event("change"));
  await until(()=>app.get("experiment-results").textContent.includes("Partial results"));
  assert.equal(app.get("experiment-results").querySelector("tbody tr").lastChild.textContent,"—");
  app.get("experiment-cancel").click();
  await until(()=>app.calls.some(call=>call.url.endsWith("/experiment_test/cancel")));
  await until(()=>app.get("experiment-progress").textContent.startsWith("Cancelled"));
  app.window.close();
});
