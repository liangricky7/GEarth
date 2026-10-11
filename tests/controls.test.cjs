const { test } = require("node:test");
const assert = require("node:assert/strict");
const { readFileSync } = require("node:fs");
const { JSDOM } = require("jsdom");
const root = "backend/GEarth.Api/wwwroot/";
async function until(predicate) {
  for (let i=0;i<100;i++) { if(predicate()) return; await new Promise(resolve=>setTimeout(resolve,5)); }
  throw new Error("Controls did not settle");
}
function controls() {
  const dom = new JSDOM(readFileSync(root+"index.html","utf8"), {url:"https://team.example.test",runScripts:"outside-only"});
  const window = dom.window;
  const state = {authenticated:false,state:"running",operation:null,error:null};
  const calls=[];
  let refresh;
  window.setInterval = callback => {refresh=callback;return 0;};
  window.fetch = async (url, options={}) => {
    const name=url.split("/").at(-1);calls.push(name);
    if(name==="login") { assert.equal(JSON.parse(options.body).password,"team-test-password");state.authenticated=true; }
    if(name==="logout") state.authenticated=false;
    if(name==="stop") state.state="pausing";
    if(name==="start") state.state="running";
    return {ok:true,status:200,json:async()=>({...state})};
  };
  const dialog=window.document.getElementById("reset-confirmation");
  dialog.showModal=()=>dialog.setAttribute("open","");
  window.eval(readFileSync(root+"controls.js","utf8"));
  return {window,state,calls,refresh:()=>refresh(),get:id=>window.document.getElementById(id)};
}
async function unlock(ui) {
  ui.get("team-password").value="team-test-password";
  ui.get("team-login").dispatchEvent(new ui.window.Event("submit",{cancelable:true}));
  await until(()=>!ui.get("team-actions").hidden);
}
test("team sign-in unlocks controls, clears the password, and logout relocks them", async()=>{
  const ui=controls();await until(()=>ui.get("simulation-state").textContent==="Running continuously");
  assert.equal(ui.get("team-actions").hidden,true);
  await unlock(ui);assert.equal(ui.get("team-password").value,"");
  assert.equal(ui.get("simulation-start").disabled,true);
  ui.get("team-logout").click();await until(()=>ui.get("team-actions").hidden);
  ui.window.close();
});
test("Stop shows saving until acknowledged and Start resumes the same shared world", async()=>{
  const ui=controls();await until(()=>ui.get("simulation-state").textContent==="Running continuously");await unlock(ui);
  ui.get("simulation-stop").click();await until(()=>ui.get("simulation-state").textContent.startsWith("Stopping"));
  assert.equal(ui.get("simulation-stop").disabled,true);
  ui.state.state="paused";await ui.refresh();
  assert.equal(ui.get("simulation-start").disabled,false);
  ui.get("simulation-start").click();await until(()=>ui.get("simulation-state").textContent==="Running continuously");
  assert.deepEqual(ui.calls.filter(name=>["start","stop"].includes(name)),["stop","start"]);
  ui.window.close();
});
test("Reset only runs after the user confirms saving and starting a fresh world", async()=>{
  const ui=controls();await until(()=>ui.get("simulation-state").textContent==="Running continuously");await unlock(ui);
  ui.get("simulation-reset").click();assert.equal(ui.calls.includes("reset"),false);
  const dialog=ui.get("reset-confirmation");dialog.returnValue="cancel";dialog.dispatchEvent(new ui.window.Event("close"));
  assert.equal(ui.calls.includes("reset"),false);
  dialog.returnValue="reset";dialog.dispatchEvent(new ui.window.Event("close"));
  await until(()=>ui.calls.includes("reset") && ui.get("simulation-message").textContent.startsWith("Saving the current run") && !ui.get("simulation-reset").disabled);ui.window.close();
});
